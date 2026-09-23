// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// 6 dedicated test cases for "password generator per-secret symbol customization".
/// TC-GEN-01: Secure by Default (empty string -> DefaultSymbols auto-reinjected)
/// TC-GEN-02: characters outside AllowedSymbols are filtered out at the boundary
/// TC-GEN-03: GenSymbols is fully preserved through a WriteEditModel -> ReadToSlot round-trip
/// TC-GEN-04: a legacy snapshot JSON with no GenSymbols key -> the IsEmpty backward-compatibility path
/// TC-IMP-01: BuildSecret (CSV import scenario) -> DefaultSymbols force-injected
/// TC-IMP-02: BuildSecret (JSON import scenario) -> DefaultSymbols force-injected
/// </summary>
public sealed class GenSymbolsTests
{
    // ── Common helpers ─────────────────────────────────────────────────────────

    private static DekScope MakeDek()
    {
        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        return new DekScope(key, 32);
    }

    // ── TC-GEN-01 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The instant GenSymbols is set to an empty string, PasswordGenerator.DefaultSymbols
    /// must be immediately reinjected (Secure by Default).
    /// </summary>
    [Fact]
    public void SecretEditModel_GenSymbols_EmptyString_AutoReinjectedWithDefaultSymbols()
    {
        // Arrange
        using var em = new SecretEditModel();
        em.GenSymbols = "!@#"; // change the initial value to a custom value
        Assert.Equal("!@#", em.GenSymbols); // confirm the precondition

        // Act - assign an empty string
        em.GenSymbols = string.Empty;

        // Assert - immediately reinjected with DefaultSymbols
        Assert.Equal(PasswordGenerator.DefaultSymbols, em.GenSymbols);
    }

    // ── TC-GEN-02 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Mixing in characters outside AllowedSymbols (alphabetic characters, full-width
    /// characters, etc.) must have them completely removed by filtering.
    /// </summary>
    [Fact]
    public void SecretEditModel_GenSymbols_NonAllowedChars_AreStrippedAtBoundary()
    {
        // Arrange
        using var em = new SecretEditModel();

        // Act - "!@" is within AllowedSymbols, "abcABC全角" is outside AllowedSymbols
        em.GenSymbols = "!@abcABC全角";

        // Assert - not a single character outside AllowedSymbols remains
        Assert.True(
            em.GenSymbols.All(c => PasswordGenerator.AllowedSymbols.Contains(c)),
            $"A character outside AllowedSymbols remains: '{em.GenSymbols}'");

        // Only the valid characters "!@" should remain
        Assert.Equal("!@", em.GenSymbols);
    }

    // ── TC-GEN-03 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// After serializing with WriteEditModel and deserializing with ReadToSlot,
    /// GenSymbols must be restored without a single bit off.
    /// </summary>
    [Fact]
    public void SnapshotSerializer_WriteEditModel_ReadToSlot_GenSymbolsIsPreserved()
    {
        // Arrange - set a custom symbol set (within AllowedSymbols)
        using var em = new SecretEditModel { Id = 42 };
        em.GenSymbols = "!@#";

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            SnapshotSerializer.WriteEditModel(w, em, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);

        var jsonBytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);

        // Act
        using var slot = SnapshotSerializer.ReadToSlot(jsonBytes);

        // Assert - SecretId is correctly preserved (deserialization sanity check)
        Assert.Equal(42, slot.SecretId);
        // GenSymbols is fully preserved
        Assert.False(slot.GenSymbolsBuf.IsEmpty);
        Assert.Equal("!@#", new string(slot.GenSymbolsBuf.Span));
    }

    // ── TC-GEN-04 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Feeding a legacy-generation snapshot JSON that physically has no GenSymbols key into
    /// ReadToSlot must not crash, and GenSymbolsBuf must remain IsEmpty (the backward-compatibility path).
    /// </summary>
    [Fact]
    public void SnapshotSerializer_ReadToSlot_LegacyJsonWithoutGenSymbols_IsEmptyAndNoThrow()
    {
        // Arrange - a legacy snapshot with no GenSymbols key (minimal structure)
        const string legacyJson =
            """{"SecretId":99,"Title":"LegacyEntry","UserId":null,"Password":null,"Website":null,"Email":null,"Notes":null,"IsFavorite":false,"FileIds":[]}""";
        var bytes = Encoding.UTF8.GetBytes(legacyJson);

        // Act
        using var slot = SnapshotSerializer.ReadToSlot(bytes.AsSpan());

        // Assert - no crash, SecretId reads correctly, GenSymbolsBuf is empty
        Assert.Equal(99, slot.SecretId);
        Assert.True(slot.GenSymbolsBuf.IsEmpty,
            "Since the legacy snapshot has no GenSymbols, GenSymbolsBuf must be IsEmpty");
    }

    // ── TC-IMP-01 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// In the CSV import scenario, the Secret produced by BuildSecret must have
    /// PasswordGenerator.DefaultSymbols force-injected.
    /// </summary>
    [Fact]
    public void BuildSecret_CsvImportScenario_GenSymbolsEqualsDefaultSymbols()
    {
        // Arrange - parameters equivalent to a CSV import (no TOTP)
        var crypto = new IdentityCryptoService();
        var dek    = MakeDek();

        // Act
        var entity = VaultImportExportHelper.BuildSecret(
            crypto,
            title:           "TestEntry_CSV",
            categoryNum:     1100,
            userId:          "user@example.com",
            password:        "p@ssw0rd",
            website:         null,
            email:        null,
            notes:           null,
            customFieldsJson: null,
            createdAt:       null,
            updatedAt:       null,
            key:             dek);

        // Assert - Secure by Default is applied to a Secret originating from CSV
        Assert.Equal(PasswordGenerator.DefaultSymbols, entity.GeneratorSymbols);
    }

    // ── TC-IMP-02 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// In the JSON import scenario, the Secret produced by BuildSecret must have
    /// PasswordGenerator.DefaultSymbols force-injected.
    /// </summary>
    [Fact]
    public void BuildSecret_JsonImportScenario_GenSymbolsEqualsDefaultSymbols()
    {
        // Arrange - parameters equivalent to a JSON import (with IsFavorite and ExpiresAt)
        var crypto    = new IdentityCryptoService();
        var dek       = MakeDek();
        var expiresAt = DateTime.UtcNow.AddDays(90);

        // Act
        var entity = VaultImportExportHelper.BuildSecret(
            crypto,
            title:           "TestEntry_JSON",
            categoryNum:     2100,
            userId:          null,
            password:        "s3cr3t!",
            website:         "https://example.com",
            email:        "u@example.com",
            notes:           "imported note",
            customFieldsJson: null,
            createdAt:       DateTime.UtcNow,
            updatedAt:       DateTime.UtcNow,
            key:             dek,
            isFavorite:      true,
            expiresAt:       expiresAt);

        // Assert - Secure by Default is applied to a Secret originating from JSON
        Assert.Equal(PasswordGenerator.DefaultSymbols, entity.GeneratorSymbols);
    }
}
