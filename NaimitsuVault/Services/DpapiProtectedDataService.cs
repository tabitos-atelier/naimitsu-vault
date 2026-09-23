// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Production implementation of DPAPI (ProtectedData.Protect/Unprotect).
/// </summary>
public sealed class DpapiProtectedDataService : IProtectedDataService
{
    public byte[] Protect(byte[] userData, byte[]? optionalEntropy, DataProtectionScope scope)
        => ProtectedData.Protect(userData, optionalEntropy, scope);

    public byte[] Unprotect(byte[] encryptedData, byte[]? optionalEntropy, DataProtectionScope scope)
        => ProtectedData.Unprotect(encryptedData, optionalEntropy, scope);
}
