// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only fake implementation of IUserConsentVerifierAdapter.
/// Every return value and exception of CheckAvailabilityAsync / RequestVerificationAsync
/// can be controlled from the outside.
/// </summary>
internal sealed class FakeUserConsentVerifierAdapter : IUserConsentVerifierAdapter
{
    /// <summary>The value returned by CheckAvailabilityAsync. Defaults to Available.</summary>
    public UserConsentVerifierAvailability AvailabilityResult { get; set; }
        = UserConsentVerifierAvailability.Available;

    /// <summary>The value returned by RequestVerificationAsync. Defaults to Verified.</summary>
    public UserConsentVerificationResult VerificationResult { get; set; }
        = UserConsentVerificationResult.Verified;

    /// <summary>
    /// When non-null, RequestVerificationAsync throws this exception.
    /// Takes priority over VerificationResult.
    /// </summary>
    public Exception? VerificationException { get; set; }

    public Task<UserConsentVerifierAvailability> CheckAvailabilityAsync()
        => Task.FromResult(AvailabilityResult);

    public Task<UserConsentVerificationResult> RequestVerificationAsync(string message)
    {
        if (VerificationException != null)
            throw VerificationException;
        return Task.FromResult(VerificationResult);
    }
}
