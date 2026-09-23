// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.ViewModels;

/// <summary>One row of the Dashboard security-scan list (weak and reused passwords merged into a single
/// keyboard-navigable list). For a weak-password row, DisplayText is that secret's title and JumpSecretId
/// is its own id. For a reused-password group, DisplayText joins every member's title (so each duplicate
/// group renders as its own row instead of all groups flattening into one line) and JumpSecretId targets
/// the group's first member - the user can review the rest of the group from within the Secrets page.</summary>
public record SecurityScanRowItem(string DisplayText, int JumpSecretId, bool IsReused);
