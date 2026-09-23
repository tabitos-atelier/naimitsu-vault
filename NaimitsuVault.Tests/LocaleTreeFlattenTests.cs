// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Localization;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression tests for LocalizationService.LoadBuiltinLocale's nested-JSON-to-flat-Dictionary
/// conversion (embedded resources locale-ja.json/locale-en.json, physically nested for VS Code
/// folding). Verifies the flattening step reconstructs the same dotted-string key contract every
/// other layer (LocalizationManager, LK.cs, XAML {loc:Msg Key=...}) has always relied on.
/// </summary>
public sealed class LocaleTreeFlattenTests
{
    private static LocalizationService CreateService(TestUnifiedDb db)
        => new(db.Factory, NullLogger<LocalizationService>.Instance);

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void LoadBuiltinLocale_ReturnsSubstantialFlatKeySet(string locale)
    {
        using var db = TestUnifiedDb.Create();
        var messages = CreateService(db).LoadBuiltinLocale(locale);

        Assert.True(messages.Count > 400, $"Expected a substantial flat key set, got {messages.Count}.");
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void LoadBuiltinLocale_NeverReturnsCommentKeys(string locale)
    {
        using var db = TestUnifiedDb.Create();
        var messages = CreateService(db).LoadBuiltinLocale(locale);

        Assert.DoesNotContain(messages.Keys, k => k.Length > 0 && k[0] == '#');
    }

    [Theory]
    [InlineData("Common.Ok")]                                  // 2-segment (Domain.Identifier)
    [InlineData("Shell.Navi.Secrets")]                         // 3-segment (Domain.SubDomain.Identifier)
    [InlineData("TimeMachine.Dialog.PurgeConfirmText")]        // 3-segment, different domain/subdomain
    [InlineData("AppSettings.FontFamily")]                     // sibling of a renamed key (see next case)
    [InlineData("AppSettings.FontFamilyDesc")]                 // renamed from "AppSettings.FontFamily.Desc"
    [InlineData("VaultSettings.AutoLockTimeout")]              // domain whose comment header interleaves with AppSettings in the source file
    [InlineData("System.Product.Name")]                        // System domain
    [InlineData("Categories.01")]                              // category preset (2-digit numeric key)
    [InlineData("Categories.99")]                              // category preset, last in the 2026-08-17 set
    [InlineData("Categories.Uncategorized")]                   // fixed non-numeric category key
    public void LoadBuiltinLocale_ReconstructsKnownFlatKeys(string key)
    {
        using var db = TestUnifiedDb.Create();
        var messages = CreateService(db).LoadBuiltinLocale("ja");

        Assert.True(messages.ContainsKey(key), $"Expected flattened key '{key}' to be present.");
    }

    [Fact]
    public void LoadBuiltinLocale_JaAndEn_HaveIdenticalKeySets()
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        var ja = service.LoadBuiltinLocale("ja");
        var en = service.LoadBuiltinLocale("en");

        Assert.Equal(ja.Keys.OrderBy(k => k, StringComparer.Ordinal), en.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    // System.Audit.* (42 keys, audit log summary templates) moved to AuditLog.* on 2026-08-10 -
    // audit log summary text no longer belongs to the immutable/non-customizable System domain.
    [Fact]
    public void LoadBuiltinLocale_AuditLogSummaryTemplates_LiveUnderAuditLogNotSystem()
    {
        using var db = TestUnifiedDb.Create();
        var messages = CreateService(db).LoadBuiltinLocale("ja");

        Assert.True(messages.ContainsKey("AuditLog.ReadSecret"));
        Assert.True(messages.ContainsKey("AuditLog.DeletedItemPlaceholder"));
        Assert.False(messages.ContainsKey("System.Audit.ReadSecret"));
        Assert.DoesNotContain(messages.Keys, k => k.StartsWith("System.Audit.", StringComparison.Ordinal));
    }

    // ExportAsync (2026-08-10): the exported file is what a user opens to translate/customize a
    // locale, so it must get the same nested/foldable shape as the embedded resources - not a flat
    // dump.

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public async Task ExportAsync_ProducesNestedStructureNotFlatKeys(string locale)
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        using var stream = new MemoryStream();

        await service.ExportAsync(locale, stream);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        var messages = doc.RootElement.GetProperty("messages");

        var common = messages.GetProperty("Common");
        Assert.Equal(JsonValueKind.Object, common.ValueKind);
        // A leaf under Common must be reachable through a comment-wrapper sub-object, not
        // sitting directly as "Common.Ok" - i.e. the flat "dotted key" is gone from the export.
        Assert.False(messages.TryGetProperty("Common.Ok", out _));
        bool foundOk = common.EnumerateObject()
            .Any(g => g.Value.ValueKind == JsonValueKind.Object && g.Value.TryGetProperty("Ok", out _));
        Assert.True(foundOk, "Expected \"Ok\" reachable via a comment-wrapper group under Common.");
    }

    [Fact]
    public async Task ExportAsync_ExcludesSystemDomain()
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        using var stream = new MemoryStream();

        await service.ExportAsync("ja", stream);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(doc.RootElement.GetProperty("messages").TryGetProperty("System", out _));
    }

    // System.LangJa/LangEn (the built-in-language radio button captions) are the last anchor a user
    // has to find their way back to a readable language after applying a broken or hostile custom
    // locale - so an imported file must never be able to override them, not even by smuggling a
    // "System" domain into the upload. Regression guard for the 2026-09-23 AppSettings.* -> System.*
    // move (design/03-07-03-00-cyber-desert-locale-terminology.md P9/§9.7).
    [Fact]
    public async Task ImportCustomAsync_AttemptToOverrideSystemLangKeys_IsSilentlyIgnored()
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        var pack = JsonSerializer.SerializeToUtf8Bytes(new
        {
            locale = "en",
            displayName = "Hostile Pack",
            localeVersion = 1,
            messages = new Dictionary<string, object>
            {
                ["System"] = new Dictionary<string, object>
                {
                    ["LangJa"] = "HACKED",
                    ["LangEn"] = "HACKED",
                },
            },
        });

        var result = await service.ImportCustomAsync(pack);
        Assert.True(result.Success);
        // The smuggled key is dropped outright, not "patched back" - it must never surface as
        // missing/patched, since IsSystemKey skips it before the missing-key patch loop runs.
        Assert.DoesNotContain("System.LangJa", result.PatchedKeys);
        Assert.DoesNotContain("System.LangEn", result.PatchedKeys);

        using var stream = new MemoryStream();
        await service.ExportAsync("custom", stream);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(doc.RootElement.GetProperty("messages").TryGetProperty("System", out _));
    }

    // System.Product.* (the app's own name/description/copyright/license/disclaimer - shown in every
    // window's title bar and the About page) is the single most consequential System.* entry to
    // protect: unlike LangJa/LangEn (a UX-confusion risk), an attacker who could rewrite
    // System.Product.Name through a hostile custom locale could rebrand the whole app for
    // impersonation/phishing. Same guard as ImportCustomAsync_AttemptToOverrideSystemLangKeys_IsSilentlyIgnored above.
    [Fact]
    public async Task ImportCustomAsync_AttemptToOverrideProductInfo_IsSilentlyIgnored()
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        var pack = JsonSerializer.SerializeToUtf8Bytes(new
        {
            locale = "en",
            displayName = "Hostile Pack",
            localeVersion = 1,
            messages = new Dictionary<string, object>
            {
                ["System"] = new Dictionary<string, object>
                {
                    ["Product"] = new Dictionary<string, object>
                    {
                        ["Name"] = "Totally Legit Password Manager",
                        ["Description"] = "HACKED",
                        ["Copyright"] = "HACKED",
                        ["License"] = "HACKED",
                        ["Disclaimer"] = "HACKED",
                    },
                },
            },
        });

        var result = await service.ImportCustomAsync(pack);
        Assert.True(result.Success);
        Assert.DoesNotContain("System.Product.Name", result.PatchedKeys);
        Assert.DoesNotContain("System.Product.Description", result.PatchedKeys);
        Assert.DoesNotContain("System.Product.Copyright", result.PatchedKeys);
        Assert.DoesNotContain("System.Product.License", result.PatchedKeys);
        Assert.DoesNotContain("System.Product.Disclaimer", result.PatchedKeys);

        using var stream = new MemoryStream();
        await service.ExportAsync("custom", stream);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(doc.RootElement.GetProperty("messages").TryGetProperty("System", out _));
    }

    // Exporting the saved custom locale must keep the imported pack's own "locale"/"displayName".
    // A "custom" locale code would make a later re-import fall back to the OS language for placeholder
    // validation (only "ja"/"en" are recognized), and a hard-coded display name loses the pack's name.

    [Fact]
    public async Task ExportAsync_Custom_PreservesImportedLocaleAndDisplayName()
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        var pack = JsonSerializer.SerializeToUtf8Bytes(new
        {
            locale = "en",
            displayName = "Cyber Desert",
            localeVersion = 1,
            messages = new Dictionary<string, object>(),
        });
        Assert.True((await service.ImportCustomAsync(pack)).Success);

        using var stream = new MemoryStream();
        await service.ExportAsync("custom", stream);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("en", doc.RootElement.GetProperty("locale").GetString());
        Assert.Equal("Cyber Desert", doc.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task ExportAsync_Custom_BlankSavedIdentity_FallsBackToOsLocaleAndDefaultName()
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        var pack = JsonSerializer.SerializeToUtf8Bytes(new
        {
            locale = "",
            displayName = "",
            localeVersion = 1,
            messages = new Dictionary<string, object>(),
        });
        Assert.True((await service.ImportCustomAsync(pack)).Success);

        using var stream = new MemoryStream();
        await service.ExportAsync("custom", stream);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(doc.RootElement.GetProperty("locale").GetString(), new[] { "ja", "en" });
        Assert.Equal("Custom", doc.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsToTheSameFlatKeySet()
    {
        using var db = TestUnifiedDb.Create();
        var service = CreateService(db);
        using var stream = new MemoryStream();

        await service.ExportAsync("ja", stream);
        var exportedBytes = stream.ToArray();

        var result = await service.ImportCustomAsync(exportedBytes);

        Assert.True(result.Success);
        // A locale exported from the built-in ja fallback and immediately re-imported should need
        // no patching - every key round-trips through nest (export) -> flatten (import) intact.
        Assert.Equal(0, result.PatchedCount);
    }
}
