// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests.Stubs;

/// <summary>
/// Stub used by PasswordGeneratorStrengthTests.
/// EvaluateStrength always returns a score of 4 (so it never breaks existing assertions).
/// CallCount records the number of calls, used to verify TC-PGS-04.
/// </summary>
internal sealed class StubPasswordEvaluationService : IPasswordEvaluationService
{
    public int CallCount { get; private set; }

    public int EvaluateStrength(ReadOnlySpan<char> plainText)
    {
        CallCount++;
        return 4;
    }

    public Task<DashboardScanResult> ScanAllSecretsAsync(DekScope dek, CancellationToken ct = default)
        => Task.FromResult(new DashboardScanResult(0, 0, [], []));
}
