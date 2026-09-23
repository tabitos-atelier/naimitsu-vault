// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Localization;

public interface ILocalizationService
{
    string CurrentLocale { get; }
    bool HasCustomLocale { get; }
    string? CustomLocaleDisplayName { get; }

    Task LoadForStartupAsync();
    Task SetLocaleAsync(string locale);
    Task<LocaleImportResult> ImportCustomAsync(ReadOnlyMemory<byte> utf8Json);
    Task ExportAsync(string locale, Stream outputStream);

    /// <summary>Loads a built-in locale's flattened messages (e.g. for reading immutable System.* values in a specific language).</summary>
    Dictionary<string, string> LoadBuiltinLocale(string locale);
}

public record LocaleImportResult(
    bool Success,
    int PatchedCount,
    IReadOnlyList<string> PatchedKeys,
    string? ErrorMessage = null,
    string? WarningMessage = null,
    IReadOnlyList<string>? MissingKeys = null,
    IReadOnlyList<string>? TooLongKeys = null,
    IReadOnlyList<string>? PlaceholderBrokenKeys = null);
