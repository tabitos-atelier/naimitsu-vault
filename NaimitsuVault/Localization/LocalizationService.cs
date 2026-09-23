// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NaimitsuVault.Localization;

public class LocalizationService(
    IDbContextFactory<UnifiedDbContext> factory,
    ILogger<LocalizationService> logger) : ILocalizationService
{
    public const int CurrentLocaleVersion = 1;

    public string CurrentLocale { get; private set; } = "ja";
    public bool HasCustomLocale { get; private set; }
    public string? CustomLocaleDisplayName { get; private set; }

    public async Task LoadForStartupAsync()
    {
        // The unified DB may be unreadable at this point (e.g. App.xaml.cs already detected startup
        // corruption with no usable shadow and is about to show the frozen UnlockWindow). Locale
        // loading must still succeed with a built-in fallback in that case - otherwise the crash here
        // would propagate past the frozen-window design entirely and take down the whole app before
        // the corruption message ever gets a chance to render.
        string? locale = null;
        UnifiedMetadata? customSetting = null;
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var localeSetting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.Locale);
            locale = localeSetting?.ConfigValue is { Length: > 0 } cv ? Encoding.UTF8.GetString(cv) : null;
            // Always check whether a custom locale exists (regardless of the current locale)
            customSetting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.CustomLocale);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read locale settings from the unified DB; falling back to the OS locale. [{ExType}]", ex.GetType().Name);
        }
        locale ??= DetermineOsLocale();

        CurrentLocale = locale;

        var jaMessages = LoadBuiltinLocale("ja");
        var enMessages = LoadBuiltinLocale("en");
        var fallback = DetermineOsLocale() == "ja" ? jaMessages : enMessages;

        if (customSetting != null)
        {
            HasCustomLocale = true;
            try
            {
                var customData = JsonSerializer.Deserialize(customSetting.ConfigValue.AsSpan(), LocaleJsonContext.Default.LocaleData);
                CustomLocaleDisplayName = customData?.DisplayName;
            }
            catch { /* Keep HasCustomLocale = true even if corrupted */ }
        }

        Dictionary<string, string> messages;
        if (locale == "custom")
        {
            if (customSetting != null)
            {
                try
                {
                    var data = JsonSerializer.Deserialize(customSetting.ConfigValue.AsSpan(), LocaleJsonContext.Default.LocaleData);
                    // Purge immutable keys (System.*) when loading from the DB (hardens against DB tampering/injection)
                    messages = (data?.Messages ?? fallback)
                        .Where(kvp => !IsSystemKey(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Custom locale JSON is invalid; falling back to the OS locale. [{ExType}]", ex.GetType().Name);
                    messages = fallback;
                }
            }
            else
            {
                messages = fallback;
            }
        }
        else
        {
            messages = locale == "ja" ? jaMessages : enMessages;
        }

        LocalizationManager.Initialize(messages, fallback);
        logger.LogInformation("Locale loaded: {Locale}", locale);
    }

    public async Task SetLocaleAsync(string locale)
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.Locale);
        if (existing == null)
            db.Metadata.Add(new UnifiedMetadata { ConfigKey = UnifiedMetadataKey.Locale, ConfigValue = Encoding.UTF8.GetBytes(locale) });
        else
            existing.ConfigValue = Encoding.UTF8.GetBytes(locale);
        await db.SaveChangesAsync();
    }

    public async Task<LocaleImportResult> ImportCustomAsync(ReadOnlyMemory<byte> utf8Json)
    {
        // First line of defense: strip BOM + validate UTF-8 (blocks ANSI/UTF-16 upfront)
        var span = StripUtf8Bom(utf8Json.Span);
        if (!System.Text.Unicode.Utf8.IsValid(span))
            return new LocaleImportResult(false, 0, [], ErrorMessage: LocalizationManager.Get("Common.ErrorInvalidEncoding"));

        LocaleTreeData data;
        try
        {
            // Second line of defense: only structural errors in otherwise-valid UTF-8 reach here
            data = JsonSerializer.Deserialize(span, LocaleTreeJsonContext.Default.LocaleTreeData)
                   ?? throw new InvalidOperationException("JSON is null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new LocaleImportResult(false, 0, [], ErrorMessage: ex.Message);
        }

        string? versionWarning = null;
        if (data.LocaleVersion != CurrentLocaleVersion)
            versionWarning = string.Format(LocalizationManager.Get("AppSettings.Dialog.VersionMismatchPatchNotice"), data.LocaleVersion);

        // Validate against the fallback matching the imported file's OWN declared language, not the
        // OS UI language - the two built-in locales aren't guaranteed to use the same {N} placeholder
        // count for a given key (e.g. Totp.TimeDriftCodes: ja uses {0}{1}{2}, en used to use only
        // {0}{1}), so validating a same-language-as-itself file against a different-language fallback
        // produced false "broken placeholder" positives whenever the OS language didn't match the
        // imported file's language (e.g. a JA-OS user importing the English cyber-desert pack).
        var fallbackLocale = data.Locale is "ja" or "en" ? data.Locale : DetermineOsLocale();
        var fallback = LoadBuiltinLocale(fallbackLocale);

        // Flatten the uploaded tree, then exclude immutable keys (System.*) from the imported data
        var flatImported = new Dictionary<string, string>();
        if (data.Messages.ValueKind == JsonValueKind.Object)
            FlattenLocaleTree(string.Empty, data.Messages, flatImported);
        var messages = flatImported
            .Where(kvp => !IsSystemKey(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var patched               = new List<string>();
        var missingKeys           = new List<string>();
        var tooLongKeys           = new List<string>();
        var placeholderBrokenKeys = new List<string>();

        // Third line of defense: fill in missing keys + check for broken placeholders + reject
        // abnormally long values (soft patching - overwrite with the fallback, not a full rejection)
        foreach (var (key, value) in fallback)
        {
            if (IsSystemKey(key)) continue; // Skip immutable keys
            if (!messages.ContainsKey(key))
            {
                messages[key] = value;
                patched.Add(key);
                missingKeys.Add(key);
            }
            else if (HasBrokenPlaceholders(value, messages[key]))
            {
                messages[key] = value;
                patched.Add(key);
                placeholderBrokenKeys.Add(key);
            }
            else if (IsAbnormallyLong(value, messages[key]))
            {
                messages[key] = value;
                patched.Add(key);
                tooLongKeys.Add(key);
            }
        }

        // DB storage stays flat (LocaleData/LocaleJsonContext) regardless of the uploaded file's
        // shape - nesting is purely a file-I/O-boundary concern (see ExportAsync/LoadBuiltinLocale).
        var storedData = new LocaleData
        {
            Locale = data.Locale,
            DisplayName = data.DisplayName,
            LocaleVersion = CurrentLocaleVersion,
            Messages = messages,
        };
        // No intermediate string — UTF-8 byte sequence goes directly to the DB
        var savedBytes = JsonSerializer.SerializeToUtf8Bytes(storedData, LocaleJsonContext.Default.LocaleData);

        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.CustomLocale);
        if (existing == null)
            db.Metadata.Add(new UnifiedMetadata { ConfigKey = UnifiedMetadataKey.CustomLocale, ConfigValue = savedBytes });
        else
            existing.ConfigValue = savedBytes;
        await db.SaveChangesAsync();

        HasCustomLocale = true;
        CustomLocaleDisplayName = storedData.DisplayName;

        return new LocaleImportResult(true, patched.Count, patched,
            WarningMessage: versionWarning, MissingKeys: missingKeys, TooLongKeys: tooLongKeys, PlaceholderBrokenKeys: placeholderBrokenKeys);
    }

    // Rejects an imported value that's abnormally long relative to the built-in fallback - a
    // malformed or malicious locale file stuffing oversized strings into UI labels could otherwise
    // overflow fixed-width controls or push other elements off-screen. Both thresholds must hold
    // (an absolute floor so short fallback strings like "OK" don't trip on any modest translation,
    // and a relative multiple so long fallback strings get proportionate headroom). The floor was
    // raised from 30 to 120 bytes on 2026-08-31: themed locale files (e.g. cyber-desert) intentionally
    // use flavorful phrasing 2-3x longer than a terse functional fallback, which false-positived at 30.
    private const int AbnormalLengthMinBytes = 120;
    private const int AbnormalLengthMultiplier = 2;

    private static bool IsAbnormallyLong(string fallbackValue, string importedValue)
    {
        int importedBytes = Encoding.UTF8.GetByteCount(importedValue);
        if (importedBytes < AbnormalLengthMinBytes) return false;
        int fallbackBytes = Encoding.UTF8.GetByteCount(fallbackValue);
        return importedBytes >= fallbackBytes * AbnormalLengthMultiplier;
    }

    public async Task ExportAsync(string locale, Stream outputStream)
    {
        string localeCode = locale;
        string displayName;
        JsonObject messagesNode;

        if (locale == "custom")
        {
            await using var db = await factory.CreateDbContextAsync();
            var setting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.CustomLocale);
            if (setting != null)
            {
                // Custom values differ from the built-in template, so they must be substituted in
                // (NestLocaleTree) rather than just cloning the template like the branches below.
                var saved = JsonSerializer.Deserialize(setting.ConfigValue.AsSpan(), LocaleJsonContext.Default.LocaleData);
                var filtered = (saved?.Messages ?? [])
                    .Where(kvp => !IsSystemKey(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                // Keep the imported pack's own identity: "locale" drives which built-in fallback
                // ImportCustomAsync validates against on re-import, so it must not degrade to the
                // "custom" pseudo-locale (which would silently fall back to the OS language).
                localeCode = !string.IsNullOrEmpty(saved?.Locale) ? saved.Locale : DetermineOsLocale();
                displayName = !string.IsNullOrEmpty(saved?.DisplayName) ? saved.DisplayName : "Custom";
                var template = LoadBuiltinLocaleTemplate(localeCode);
                messagesNode = (template.ValueKind == JsonValueKind.Object
                    ? NestLocaleTree(string.Empty, template, filtered) as JsonObject
                    : null) ?? new JsonObject();
            }
            else
            {
                localeCode = DetermineOsLocale();
                displayName = LoadBuiltinLocaleDisplayName(localeCode);
                messagesNode = CloneLocaleTemplateWithoutSystem(localeCode);
            }
        }
        else
        {
            // Exporting a built-in locale is exactly its embedded resource with System.* removed -
            // no flatten/renest round-trip needed, the values are already in the right shape.
            displayName = LoadBuiltinLocaleDisplayName(locale);
            messagesNode = CloneLocaleTemplateWithoutSystem(locale);
        }

        var envelope = new JsonObject
        {
            ["locale"] = localeCode,
            ["displayName"] = displayName,
            ["localeVersion"] = CurrentLocaleVersion,
            ["messages"] = messagesNode,
        };

        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        // Writes directly to outputStream - no intermediate string or byte[] copy of the whole envelope.
        await JsonSerializer.SerializeAsync(outputStream, envelope, options);
    }

    // Clones the embedded-resource "messages" tree for a built-in locale into a mutable JsonObject
    // and strips the immutable System domain - used for both the built-in export branch and the
    // custom-with-no-saved-settings fallback branch (both just want "the template, minus System").
    private JsonObject CloneLocaleTemplateWithoutSystem(string locale)
    {
        var template = LoadBuiltinLocaleTemplate(locale);
        if (template.ValueKind != JsonValueKind.Object) return [];
        var node = JsonObject.Create(template) ?? [];
        node.Remove("System");
        return node;
    }

    private static string DetermineOsLocale() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

    // Immutable-key check: the System domain is never customizable via Import/Export.
    // (Section-heading comments no longer need a separate check here - they live only inside the
    // nested embedded-resource tree as transparent grouping objects and never survive flattening
    // into this flat Dictionary<string,string>, see LocaleTreeData/Flatten below.)
    private static bool IsSystemKey(string key) =>
        key.StartsWith("System.", StringComparison.Ordinal);

    // First-line-of-defense helper: strips the UTF-8 BOM (0xEF 0xBB 0xBF) with zero copying
    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> span)
        => span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF
            ? span[3..] : span;

    // Third-line-of-defense helper: checks whether a placeholder required by the fallback is missing
    // from the imported value, or whether the imported value contains a format (out-of-range index,
    // unclosed { , etc.) that would throw a FormatException at runtime.
    private static bool HasBrokenPlaceholders(string fallbackValue, string importedValue)
    {
        // (1) A fallback {N} is missing from the import → broken
        int argCount = 0;
        for (int i = 0; i <= 9; i++)
        {
            if (!fallbackValue.Contains($"{{{i}}}", StringComparison.Ordinal)) continue;
            argCount = i + 1; // max index + 1 = number of arguments the caller passes
            if (!importedValue.Contains($"{{{i}}}", StringComparison.Ordinal))
                return true;
        }
        // (2) Only when the fallback has placeholders: run a trial Format with the same number of dummy
        //    arguments to detect an out-of-range index ({10}, etc.) or malformed syntax (unclosed { , etc.).
        //    Keys with argCount == 0 go through Get(key)'s args.Length == 0 path and are never Formatted, so they're excluded.
        if (argCount > 0)
        {
            var dummies = new object[argCount];
            for (int i = 0; i < argCount; i++) dummies[i] = "_";
            try { string.Format(System.Globalization.CultureInfo.InvariantCulture, importedValue, dummies); }
            catch (FormatException) { return true; }
        }
        return false;
    }

    public Dictionary<string, string> LoadBuiltinLocale(string locale)
    {
        var template = LoadBuiltinLocaleTemplate(locale);
        var result = new Dictionary<string, string>();
        if (template.ValueKind == JsonValueKind.Object)
            FlattenLocaleTree(string.Empty, template, result);
        return result;
    }

    // Returns the raw nested tree ("messages") for a built-in locale, before flattening. Used both
    // as the source for LoadBuiltinLocale (flattened for runtime use) and as the structural
    // template for ExportAsync (walked to rebuild a nested export with the same domain/heading
    // grouping, see NestLocaleTree). The returned JsonElement is safe to keep after this method
    // returns: JsonSerializer.Deserialize into a JsonElement-typed property clones the element, unlike
    // JsonDocument.RootElement which is only valid while its JsonDocument is alive.
    private JsonElement LoadBuiltinLocaleTemplate(string locale) => LoadBuiltinLocaleData(locale)?.Messages ?? default;

    // The locale's own display name (e.g. "日本語"/"English"), read from the JSON itself rather than
    // hardcoded per-locale in C# - so a fork adding/renaming a built-in locale only touches the JSON.
    private string LoadBuiltinLocaleDisplayName(string locale) => LoadBuiltinLocaleData(locale)?.DisplayName ?? locale;

    private LocaleTreeData? LoadBuiltinLocaleData(string locale)
    {
        try
        {
            var resourceName = $"NaimitsuVault.Localization.Resources.locale-{locale}.json";
            var assembly = Assembly.GetExecutingAssembly();
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                logger.LogError("Embedded resource '{ResourceName}' not found.", resourceName);
                return null;
            }
            // No intermediate string — read the stream into bytes and deserialize via ReadOnlySpan<byte>
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return JsonSerializer.Deserialize(StripUtf8Bom(bytes), LocaleTreeJsonContext.Default.LocaleTreeData);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to load the embedded resource for built-in locale '{Locale}'. [{ExType}]", locale, ex.GetType().Name);
            return null;
        }
    }

    // Rebuilds a nested JSON tree from a flat Dictionary<string,string>, using an embedded-resource
    // tree as the structural template (domain/subdomain/comment-wrapper grouping) - the inverse of
    // FlattenLocaleTree, used by ExportAsync. A leaf whose reconstructed key isn't present in
    // flatValues is omitted, and any comment-wrapper or domain object left empty as a result is
    // omitted too, so the export naturally reflects only the keys actually being exported (e.g.
    // after the System.* filter).
    private static JsonNode? NestLocaleTree(string prefix, JsonElement templateNode, Dictionary<string, string> flatValues)
    {
        if (templateNode.ValueKind == JsonValueKind.String)
            return flatValues.TryGetValue(prefix, out var v) ? JsonValue.Create(v) : null;

        var result = new JsonObject();
        foreach (var prop in templateNode.EnumerateObject())
        {
            var nextPrefix = prop.Name.Length > 0 && prop.Name[0] == '#'
                ? prefix
                : prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            var child = NestLocaleTree(nextPrefix, prop.Value, flatValues);
            if (child != null)
                result[prop.Name] = child;
        }
        return result.Count > 0 ? result : null;
    }

    // Embedded resources (locale-ja.json/locale-en.json) are physically nested for editor
    // readability (VS Code folding): Domain -> [SubDomain ->] Identifier, with section-heading
    // comments ("#nn-nn: label") as transparent grouping objects that wrap identifiers purely for
    // human scanning. This walks that tree and reconstructs the exact same dotted-string keys
    // ("Common.Ok", "TimeMachine.Dialog.PurgeConfirmText", etc.) that the rest of the app has always
    // used - LocalizationManager, LK.cs, and XAML {loc:Msg Key=...} bindings are unaware this
    // conversion step exists. A "#"-prefixed property name is always a comment/grouping wrapper: its
    // own name is discarded (never joined into the path) and its children are walked as if they were
    // direct children of the parent. Since the 2026-08-10 nested-format revision this also applies to
    // the Export/Import file boundary: ImportCustomAsync flattens the uploaded file through this same
    // method, and ExportAsync rebuilds a nested file through the inverse (NestLocaleTree). Only the
    // custom-locale DB row itself stays flat (LocaleData/LocaleJsonContext) - nesting is purely a
    // file-I/O-boundary concern.
    private static void FlattenLocaleTree(string prefix, JsonElement node, Dictionary<string, string> outDict)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            outDict[prefix] = node.GetString() ?? string.Empty;
            return;
        }
        foreach (var prop in node.EnumerateObject())
        {
            var nextPrefix = prop.Name.Length > 0 && prop.Name[0] == '#'
                ? prefix
                : prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            FlattenLocaleTree(nextPrefix, prop.Value, outDict);
        }
    }
}

internal record LocaleData
{
    [JsonPropertyName("locale")]        public string Locale         { get; init; } = string.Empty;
    [JsonPropertyName("displayName")]   public string DisplayName    { get; init; } = string.Empty;
    [JsonPropertyName("localeVersion")] public int    LocaleVersion  { get; init; } = 1;
    [JsonPropertyName("messages")]      public Dictionary<string, string>? Messages { get; init; }
}

[JsonSerializable(typeof(LocaleData))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class LocaleJsonContext : JsonSerializerContext { }

// Nested-tree counterpart of LocaleData (see FlattenLocaleTree remarks), used both for embedded
// built-in locale resources and for the Export/Import file boundary. "Messages" is
// left as a raw JsonElement (not a Dictionary<string,TValue> POCO) because a value under it can be
// either a string (a translated leaf) or a nested object (a domain/subdomain/comment-wrapper) -
// JsonElement has built-in source-generator support without needing reflection over a custom type,
// so this stays compatible with the project's AOT/trimming-safe System.Text.Json policy.
internal sealed record LocaleTreeData
{
    [JsonPropertyName("locale")]        public string Locale        { get; init; } = string.Empty;
    [JsonPropertyName("displayName")]   public string DisplayName   { get; init; } = string.Empty;
    [JsonPropertyName("localeVersion")] public int    LocaleVersion { get; init; } = 1;
    [JsonPropertyName("messages")]      public JsonElement Messages { get; init; }
}

[JsonSerializable(typeof(LocaleTreeData))]
internal partial class LocaleTreeJsonContext : JsonSerializerContext { }
