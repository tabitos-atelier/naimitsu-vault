// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.ViewModels;

/// <summary>One row of the Dashboard expiry-alert list. FileId opens that attachment directly in the
/// Viewer (certificate files); otherwise Tag is the NavigationView tag to jump to ("secrets", "profile"),
/// with SecretId set only for rows backed by a specific secret. IsExpired distinguishes an already-past
/// expiry (critical/red) from a still-upcoming one within the warning window (caution/amber) - both can
/// appear in the same list, so this can't be a single fixed color for the whole ItemsControl.</summary>
public record ExpiryAlertItem(string Text, string Tag, bool IsExpired, int? SecretId = null, int? FileId = null);
