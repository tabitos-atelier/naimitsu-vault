// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public interface IFontResourceService
{
    /// <summary>
    /// Overwrites the app-wide ContentControlThemeFontFamily theme resource (null = system default).
    /// Does not, by itself, force already-rendered controls to refresh; callers must also trigger a
    /// theme-resource re-evaluation (see ShellWindow/ViewerWindow/etc.'s FontFamilyChangedMessage handler).
    /// </summary>
    void Apply(string? fontFamily);
}
