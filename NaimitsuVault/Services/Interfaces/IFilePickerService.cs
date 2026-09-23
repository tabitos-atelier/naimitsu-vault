// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Services.Interfaces;

public interface IFilePickerService
{
    /// <summary>Lets the user choose a file save destination. Null on cancel.</summary>
    Task<SecureCharBuffer?> SaveAsync(string defaultFileName, IReadOnlyList<(string description, string extension)> filters);

    /// <summary>Lets the user choose a single file. Null on cancel.</summary>
    Task<SecureCharBuffer?> OpenAsync(IReadOnlyList<(string description, string extension)> filters);

    /// <summary>Lets the user choose multiple files. Empty list on cancel.</summary>
    Task<IReadOnlyList<SecureCharBuffer>> OpenMultipleAsync(IReadOnlyList<(string description, string extension)> filters);

    /// <summary>Lets the user choose a folder. Null on cancel.</summary>
    Task<SecureCharBuffer?> OpenFolderAsync();
}
