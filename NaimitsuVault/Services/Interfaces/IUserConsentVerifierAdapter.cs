// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// Abstracts the WinRT static calls of Windows Hello.
/// Replace with FakeUserConsentVerifierAdapter for testing.
/// </summary>
public interface IUserConsentVerifierAdapter
{
    Task<UserConsentVerifierAvailability> CheckAvailabilityAsync();
    Task<UserConsentVerificationResult> RequestVerificationAsync(string message);
}
