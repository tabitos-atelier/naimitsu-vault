// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// Identity-transform fake implementation of IProtectedDataService.
/// Protect(x) = x, Unprotect(x) = x (returns the input unchanged).
/// Eliminates DPAPI calls in the test environment and reproduces the Windows Hello flow
/// in pure C#.
///
/// Security note: never register this in production DI (no impact on Rule 4).
/// </summary>
internal sealed class FakeProtectedDataService : IProtectedDataService
{
    /// <summary>
    /// When non-null, Unprotect throws this exception.
    /// </summary>
    public Exception? UnprotectException { get; set; }

    /// <summary>
    /// A strong reference to the byte[] most recently returned by Unprotect.
    /// Used on the test side to verify the ZeroMemory guarantee (the caller should
    /// zero it after checking).
    ///
    /// [Usage note] This is a reference to the exact array Unprotect returned, not a copy.
    /// If the code under test correctly runs CryptographicOperations.ZeroMemory(), this
    /// reference target is zeroed out at the same time. Consequently it cannot be used to
    /// verify "matches the original data before zeroing" (e.g. Assert.Equal(expectedKey,
    /// LastUnprotectedBytes)) — the more correctly the code under test behaves, the more
    /// such an assertion would fail. Restrict its use to confirming that zeroing itself
    /// happened (the pattern used in WindowsHelloDekMemoryTests' TC-WH-13, checking
    /// returnedBuf.All(b => b == 0)).
    /// </summary>
    public byte[]? LastUnprotectedBytes { get; private set; }

    public byte[] Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope)
        // The real DpapiProtectedDataService.Protect (ProtectedData.Protect) always returns a
        // new array, so calling code can assume its ZeroMemory of the output never also
        // zeroes the input. This identity-transform fake preserves the same contract
        // (a distinct array) by returning a copy via .ToArray().
        => userData.ToArray();

    public byte[] Unprotect(byte[] encryptedData, byte[]? optionalEntropy, DataProtectionScope scope)
    {
        if (UnprotectException != null) throw UnprotectException;

        // Return a copy so the calling code's ZeroMemory doesn't touch our internal state
        var result = encryptedData.ToArray();
        LastUnprotectedBytes = result;
        return result;
    }
}
