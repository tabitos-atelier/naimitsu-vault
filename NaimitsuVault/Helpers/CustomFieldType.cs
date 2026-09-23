// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Localization;

namespace NaimitsuVault.Helpers;

/// <summary>Display/edit type for a user-defined custom field (Secrets.CustomFields JSON and profile custom fields).</summary>
public enum CustomFieldType
{
    Text = 0,
    Password = 1,
    Url = 2,
    Date = 3,
}

public static class CustomFieldTypeExtensions
{
    /// <summary>Maps the AddCustomField command's string parameter ("Password"/"Url"/"Date"/anything else) to a FieldType.</summary>
    public static CustomFieldType FromCommandParameter(string? type) => type switch
    {
        "Password" => CustomFieldType.Password,
        "Url"      => CustomFieldType.Url,
        "Date"     => CustomFieldType.Date,
        _          => CustomFieldType.Text,
    };

    /// <summary>
    /// Localized placeholder label for a given FieldType. Used both when a new field is first
    /// created (AddCustomField) and when the user clears an existing field's label back to a
    /// placeholder (RecordExtrasControl's LabelEditor_LostFocus) - a custom field has no single
    /// fixed default label the way Title/UserId/Password do, so the fallback is derived from
    /// FieldType instead of a stashed constant.
    /// </summary>
    public static string GetDefaultLabel(this CustomFieldType fieldType) => fieldType switch
    {
        CustomFieldType.Password => LocalizationManager.Get("Common.NewPassword"),
        CustomFieldType.Url      => LocalizationManager.Get("Common.NewWebsite"),
        CustomFieldType.Date     => LocalizationManager.Get("Common.NewDate"),
        _                        => LocalizationManager.Get("Common.NewText"),
    };
}
