// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage for a bug found in real usage: a JSON import record with a "totpSecret" but
/// no "totpDigits"/"totpPeriod"/"totpAlgorithm" produced a TOTP entry whose code never displayed and
/// whose countdown never advanced (toggle showed on, code area stayed blank, "30s" bar never moved).
///
/// Root cause: ImportSecretDto declared TotpDigits/TotpPeriod/TotpAlgorithm as non-nullable with C#
/// field initializers (= 6 / = 30 / = "SHA1"). System.Text.Json's source-generated deserializer does
/// not apply those initializers for keys absent from the payload when read via
/// DeserializeAsyncEnumerable - it leaves them at default(int)/default(string), i.e. 0/0/null.
/// Digits=0 made TotpCalculator.Generate return a zero-length code; period=0 made every subsequent
/// timer tick throw DivideByZeroException inside RefreshTotpCode, silently swallowed by its catch.
///
/// TC-TID-01: an imported record with totpSecret only (no digits/period/algorithm) round-trips
/// through BuildSecret/FieldCrypto/TryUnpack to a valid 6-digit/30s/SHA1 TOTP config.
/// </summary>
public sealed class TotpImportDefaultsTests
{
    private static CryptoService Crypto() => new(NullLogger<CryptoService>.Instance);

    private static DekScope MakeDek(out byte[] key)
    {
        key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        return new DekScope(key, 32);
    }

    // ── TC-TID-01 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportJson_TotpSecretOnlyNoDigitsNoAlgorithm_RoundTripsAndUnpacks()
    {
        var json = """
        [
          {
            "title": "Atelier",
            "totpSecret": "GEZDGNBVGY3TQNJV"
          }
        ]
        """;
        var bytes = Encoding.UTF8.GetBytes(json);

        ImportSecretDto? dto = null;
        await foreach (var d in JsonSerializer.DeserializeAsyncEnumerable(new MemoryStream(bytes), ImportJsonContext.Default.ImportSecretDto, TestContext.Current.CancellationToken))
        {
            dto = d;
        }

        Assert.NotNull(dto);
        Assert.Equal("GEZDGNBVGY3TQNJV", dto!.TotpSecret);
        // Absent from the JSON - must NOT be trusted as 0 (STJ source-gen does not honor a
        // non-nullable init property's C# field initializer when the key is missing from the
        // payload; the caller must coalesce with ?? instead of relying on the DTO's own default).
        Assert.Null(dto.TotpDigits);
        Assert.Null(dto.TotpPeriod);
        Assert.Null(dto.TotpAlgorithm);

        var crypto = Crypto();
        var dek = MakeDek(out var key);
        var entity = VaultImportExportHelper.BuildSecret(
            crypto, dto.Title!, null,
            dto.UserId, dto.Password, dto.Website, dto.Email, dto.Notes, null,
            dto.CreatedAt, dto.UpdatedAt, dek, dto.IsFavorite, dto.ExpiresAt,
            dto.TotpSecret, dto.TotpDigits ?? 6, dto.TotpPeriod ?? 30,
            dto.TotpAlgorithm ?? TotpCalculator.DefaultAlgorithm);

        Assert.NotNull(entity.TotpSecret);

        using var opened = FieldCrypto.Open(entity.TotpSecret, crypto, dek);
        Assert.NotNull(opened);
        var packed = Encoding.UTF8.GetString(opened!.Utf8);

        var ok = TotpCalculator.TryUnpack(packed, out var secret, out var digits, out var period, out var algorithm);
        Assert.True(ok, $"TryUnpack failed. packedLen={packed.Length}");
        Assert.Equal("GEZDGNBVGY3TQNJV", secret.ToString());
        Assert.Equal(6, digits);
        Assert.Equal(30, period);
        Assert.Equal("SHA1", algorithm);

        var (code, rem) = TotpCalculator.Generate(secret, digits, period, algorithm);
        Assert.Equal(6, code.Length);
        Assert.InRange(rem, 1, 30);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }
}
