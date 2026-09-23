// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage (TC-PS-01..03) for the 2026-09-17 ProfileService fixes:
/// - ParseIso(char[]?) was rewritten from an allocating `new string(c)` + ZeroStringInternals to a
///   zero-allocation DateTime.TryParse(ReadOnlySpan&lt;char&gt;) call. These tests round-trip identity
///   expiry dates through the full encrypt/decrypt pipeline to confirm the parsed value is unchanged.
/// - ScanDisplayName's stackalloc buffer now gets an explicit ZeroMemory in a finally block before
///   the stack frame unwinds. These tests confirm the Nickname/Name extraction logic is unaffected.
/// </summary>
public sealed class ProfileServiceTests
{
    private static DekScope MakeDek(out byte[] key)
    {
        key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return new DekScope(key, 32);
    }

    private static ProfileService MakeService(TestDb db, AppSession session)
        => new(db.Factory, new CryptoService(NullLogger<CryptoService>.Instance), session,
               NullLogger<ProfileService>.Instance);

    // ── TC-PS-01 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetExpiryInfoAsync_RoundTripsIdentityExpiryDates()
    {
        using var db = TestDb.Create();
        var session = new AppSession();
        var dek = MakeDek(out var key);
        session.SetKey(key);
        var service = MakeService(db, session);

        var expiry1 = new DateTime(2030, 5, 17, 0, 0, 0, DateTimeKind.Utc);
        var expiry2 = new DateTime(2031, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var model = new ProfileEditModel { Id1Expiry = expiry1, Id2Expiry = expiry2 };

        await service.CommitProfileAsync(model, dek);
        var info = await service.GetExpiryInfoAsync(dek);

        Assert.Equal(expiry1, info.Id1Expiry);
        Assert.Equal(expiry2, info.Id2Expiry);
        Assert.Null(info.Id3Expiry);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PS-02 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadDisplayNameAsync_PrefersNicknameOverName()
    {
        using var db = TestDb.Create();
        var session = new AppSession();
        var dek = MakeDek(out var key);
        session.SetKey(key);
        var service = MakeService(db, session);

        var model = new ProfileEditModel { Name = "Taro Yamada", Nickname = "Taro" };
        await service.CommitProfileAsync(model, dek);

        Assert.Equal("Taro", await service.LoadDisplayNameAsync());

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PS-03 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadDisplayNameAsync_FallsBackToName_WhenNicknameEmpty()
    {
        using var db = TestDb.Create();
        var session = new AppSession();
        var dek = MakeDek(out var key);
        session.SetKey(key);
        var service = MakeService(db, session);

        var model = new ProfileEditModel { Name = "Taro Yamada" };
        await service.CommitProfileAsync(model, dek);

        Assert.Equal("Taro Yamada", await service.LoadDisplayNameAsync());

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }
}
