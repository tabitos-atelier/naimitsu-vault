// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public interface IPasswordEvaluationService
{
    /// <summary>
    /// Returns a strength score (0-4) for a single plaintext password.
    /// Takes the plaintext as a span so callers never need to materialize their own heap string;
    /// the ephemeral string zxcvbn requires is created and zeroed entirely within the implementation.
    /// </summary>
    int EvaluateStrength(ReadOnlySpan<char> plainText);

    /// <summary>
    /// Scans the entire DB and aggregates a security scan result.
    /// Each record's plaintext is <c>ZeroMemory</c>'d immediately within the loop (relay-style teardown design).
    /// The DEK is received as a DekScope (a reference wrapper around the active session's pinned key).
    /// The caller does not ZeroMemory the DekScope (the security context clears it on Lock()).
    /// </summary>
    Task<DashboardScanResult> ScanAllSecretsAsync(DekScope dek, CancellationToken ct = default);
}
