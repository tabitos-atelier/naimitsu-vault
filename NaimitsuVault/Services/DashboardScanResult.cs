// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>One row of the warning list. Holds only SecretId and title (decrypted string). Never includes the plaintext password.
/// DuplicateGroupId is null for weak-password items; for reused-password items, it's shared by every member of the same duplicate-password group.</summary>
public record SecurityAlertItem(int SecretId, string Title, int? StrengthScore, int? DuplicateGroupId = null);

/// <summary>Aggregated security scan result for the dashboard. Contains no plaintext passwords. Expiry is managed separately in section 2, so it is out of scope for this scan.</summary>
/// <param name="WeakPasswordCount">Count of items with a zxcvbn score of 0-2.</param>
/// <param name="ReusedPasswordCount">Number of "duplicate groups" sharing the same password. E.g.: [pw_A, pw_A, pw_B, pw_B, pw_C] -> pw_A and pw_B are duplicated -> 2.</param>
public record DashboardScanResult(
    int WeakPasswordCount,
    int ReusedPasswordCount,
    IReadOnlyList<SecurityAlertItem> WeakItems,
    IReadOnlyList<SecurityAlertItem> ReusedItems
);
