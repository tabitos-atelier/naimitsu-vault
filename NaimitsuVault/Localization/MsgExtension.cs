// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Xaml.Markup;

namespace NaimitsuVault.Localization;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public class MsgExtension : MarkupExtension
{
    public string Key  { get; set; } = string.Empty;
    public bool   Warn { get; set; } = false;

    protected override object ProvideValue()
    {
        var text = LocalizationManager.Get(Key);
        return Warn ? "⚠️ " + text : text;
    }
}
