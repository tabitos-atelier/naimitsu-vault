// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;

namespace NaimitsuVault.Services.Interfaces;

/// <summary>
/// Abstracts DPAPI (ProtectedData).
/// Replace with FakeProtectedDataService (identity transform) for testing.
/// </summary>
public interface IProtectedDataService
{
    /// <summary>
    /// Ownership: the caller retains ownership of <paramref name="userData"/> (plaintext key material).
    /// This method only reads it and does not clear it. The caller is responsible
    /// for clearing it with <c>CryptographicOperations.ZeroMemory</c> after completion.
    /// </summary>
    byte[] Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope);

    /// <summary>
    /// The return value is newly allocated plaintext key material, and the caller takes over its ownership.
    /// This method itself does not clear the return value. The caller is responsible
    /// for clearing it with <c>CryptographicOperations.ZeroMemory</c> after use.
    /// </summary>
    byte[] Unprotect(byte[] encryptedData, byte[]? optionalEntropy, DataProtectionScope scope);
}
