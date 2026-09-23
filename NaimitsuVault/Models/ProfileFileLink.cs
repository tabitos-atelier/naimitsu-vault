// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

/// <summary>
/// Link table entity associating the profile (singleton) with an image resource.
/// Since only one profile exists, there is no ProfileId column.
/// </summary>
public class ProfileFileLink
{
    public int FileId { get; set; }
}
