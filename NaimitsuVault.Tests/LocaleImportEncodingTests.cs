// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Localization;

namespace NaimitsuVault.Tests;

/// <summary>
/// Unit tests for LocalizationService.ImportCustomAsync's 4 lines of defense.
/// 1st line: BOM stripping + Utf8.IsValid (blocks ANSI / UTF-16)
/// 2nd line: catching JsonException (structural errors)
/// 3rd line: placeholder consistency (soft repair via HasBrokenPlaceholders)
/// 4th line: abnormal-length rejection (soft repair via IsAbnormallyLong)
/// </summary>
public sealed class LocaleImportEncodingTests
{
    // Helper that generates a minimal valid locale JSON for testing. "messages" is nested
    // (Domain -> [SubDomain ->] Identifier), matching what ExportAsync now always produces -
    // ImportCustomAsync only needs to handle the current nested shape (unreleased app, no flat
    // format ever shipped to real users).
    private static byte[] MakeLocaleJson(
        Dictionary<string, object>? messages = null,
        int version = 1)
    {
        var obj = new
        {
            locale = "custom",
            displayName = "Test",
            localeVersion = version,
            messages = messages ?? new Dictionary<string, object>(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    private static LocalizationService CreateService(TestUnifiedDb db)
        => new(db.Factory, NullLogger<LocalizationService>.Instance);

    // ── 1st line of defense: UTF-8 BOM stripping ────────────────────────────────────────

    [Fact]
    public async Task ImportCustomAsync_Utf8Bom_StripsAndImportsSuccessfully()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        var content = MakeLocaleJson(new() { ["Common"] = new Dictionary<string, string> { ["Ok"] = "OK" } });
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. content];

        var result = await svc.ImportCustomAsync(withBom);

        Assert.True(result.Success);
    }

    // ── 1st line of defense: reject all ANSI ─────────────────────────────────────────────────

    [Fact]
    public async Task ImportCustomAsync_AnsiEncoded_ReturnsFailure()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // An invalid UTF-8 byte sequence containing Windows-1252's "é" (0xE9)
        byte[] ansi = [0x7B, 0xE9, 0x7D]; // {é}

        var result = await svc.ImportCustomAsync(ansi);

        Assert.False(result.Success);
        // The locale for key Common.ErrorInvalidEncoding is not yet initialized, so it's either the
        // [Common.ErrorInvalidEncoding] fallback or the actual value
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task ImportCustomAsync_ShiftJisBytes_ReturnsFailure()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // CP932's "あ" = 0x82 0xA0 (invalid as UTF-8)
        byte[] sjis = [0x82, 0xA0];

        var result = await svc.ImportCustomAsync(sjis);

        Assert.False(result.Success);
    }

    // ── 1st line of defense: blocking UTF-16 BOM (locale import has no UTF-16 rescue path — reject all) ────────

    [Fact]
    public async Task ImportCustomAsync_Utf16LeBom_ReturnsFailure()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // A byte sequence with a UTF-16 LE BOM (invalid as UTF-8)
        byte[] utf16 = [0xFF, 0xFE, 0x7B, 0x00, 0x7D, 0x00]; // {}

        var result = await svc.ImportCustomAsync(utf16);

        Assert.False(result.Success);
    }

    // ── 2nd line of defense: JSON structural errors ──────────────────────────────────────────────

    [Fact]
    public async Task ImportCustomAsync_InvalidJsonStructure_ReturnsFailure()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // Valid UTF-8, but broken as JSON
        var invalid = Encoding.UTF8.GetBytes("{ this is not valid json }}}");

        var result = await svc.ImportCustomAsync(invalid);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ImportCustomAsync_EmptyBytes_ReturnsFailure()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // 0 bytes: Utf8.IsValid = true, but JSON parsing fails
        var result = await svc.ImportCustomAsync(ReadOnlyMemory<byte>.Empty);

        Assert.False(result.Success);
    }

    // ── 3rd line of defense: placeholder consistency (soft repair) ───────────────────────────

    [Fact]
    public async Task ImportCustomAsync_PlaceholderMissing_PatchesFromFallback()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // AppSettings.Dialog.VersionMismatchPatchNotice is a key that contains "{0}". Try removing {0} from the imported data
        // (uses a real fallback key to verify soft repair)
        var messages = new Dictionary<string, object>
        {
            ["AppSettings"] = new Dictionary<string, object>
            {
                ["Dialog"] = new Dictionary<string, string>
                {
                    ["VersionMismatchPatchNotice"] = "ロケールファイルのバージョンが異なります。", // {0} is missing
                },
            },
        };
        var json = MakeLocaleJson(messages);

        var result = await svc.ImportCustomAsync(json);

        Assert.True(result.Success);
        // AppSettings.Dialog.VersionMismatchPatchNotice is broken, so it should be included in patched
        Assert.Contains("AppSettings.Dialog.VersionMismatchPatchNotice", result.PatchedKeys);
        Assert.NotNull(result.PlaceholderBrokenKeys);
        Assert.Contains("AppSettings.Dialog.VersionMismatchPatchNotice", result.PlaceholderBrokenKeys!);
    }

    [Fact]
    public async Task ImportCustomAsync_PlaceholderIntact_DoesNotPatch()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // No patch is needed when the placeholder is correctly preserved
        var messages = new Dictionary<string, object>
        {
            ["AppSettings"] = new Dictionary<string, object>
            {
                ["Dialog"] = new Dictionary<string, string>
                {
                    ["VersionMismatchPatchNotice"] = "バージョンが異なります（{0}）。", // {0} preserved
                },
            },
        };
        var json = MakeLocaleJson(messages);

        var result = await svc.ImportCustomAsync(json);

        Assert.True(result.Success);
        Assert.DoesNotContain("AppSettings.Dialog.VersionMismatchPatchNotice", result.PatchedKeys);
    }

    // ── Successful import ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportCustomAsync_ValidJson_ReturnsSuccess()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        var json = MakeLocaleJson(new() { ["Common"] = new Dictionary<string, string> { ["Ok"] = "了解" } });

        var result = await svc.ImportCustomAsync(json);

        Assert.True(result.Success);
        Assert.True(svc.HasCustomLocale);
    }

    [Fact]
    public async Task ImportCustomAsync_MissingKeys_PatchedFromFallback()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // Empty messages (everything is filled in from the fallback)
        var json = MakeLocaleJson(new());

        var result = await svc.ImportCustomAsync(json);

        Assert.True(result.Success);
        Assert.True(result.PatchedCount > 0);
        // Every patched key here is missing (not a broken-placeholder repair), so MissingKeys must
        // carry the same set the audit log logs one row per (LocaleImportKeyMissing).
        Assert.NotNull(result.MissingKeys);
        Assert.Equal(result.PatchedCount, result.MissingKeys!.Count);
        Assert.Contains("Common.Ok", result.MissingKeys);
    }

    // ── Abnormally-long-value rejection (soft repair, same treatment as a missing key) ──────

    [Fact]
    public async Task ImportCustomAsync_AbnormallyLongValue_PatchedAndListedInTooLongKeys()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // Common.Ok's built-in fallback is "OK" (2 bytes) - 125 ASCII bytes clears both the 120-byte
        // floor and the 2x-original ratio.
        var messages = new Dictionary<string, object>
        {
            ["Common"] = new Dictionary<string, string> { ["Ok"] = new string('x', 125) },
        };
        var json = MakeLocaleJson(messages);

        var result = await svc.ImportCustomAsync(json);

        Assert.True(result.Success);
        Assert.Contains("Common.Ok", result.PatchedKeys);
        Assert.NotNull(result.TooLongKeys);
        Assert.Contains("Common.Ok", result.TooLongKeys!);
        Assert.DoesNotContain("Common.Ok", result.MissingKeys ?? []);
    }

    [Fact]
    public async Task ImportCustomAsync_LongButUnder120BytesValue_DoesNotPatch()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // 100 ASCII bytes: well over 2x "OK" (2 bytes), but under the 120-byte absolute floor -
        // must NOT be treated as abnormally long (the floor exists precisely so short fallback
        // strings don't reject a themed locale's intentionally longer flavor text, e.g. the
        // cyber-desert locale pack's stylized phrasing).
        var messages = new Dictionary<string, object>
        {
            ["Common"] = new Dictionary<string, string> { ["Ok"] = new string('x', 100) },
        };
        var json = MakeLocaleJson(messages);

        var result = await svc.ImportCustomAsync(json);

        Assert.True(result.Success);
        Assert.DoesNotContain("Common.Ok", result.PatchedKeys);
        Assert.DoesNotContain("Common.Ok", result.TooLongKeys ?? []);
    }

    [Fact]
    public async Task ImportCustomAsync_VersionMismatch_ReturnsSuccessWithWarning()
    {
        using var db = TestUnifiedDb.Create();
        var svc = CreateService(db);

        // version=99 does not match the built-in version → WarningMessage is present
        var json = MakeLocaleJson(version: 99);

        var result = await svc.ImportCustomAsync(json);

        Assert.True(result.Success);
        Assert.NotNull(result.WarningMessage);
    }
}
