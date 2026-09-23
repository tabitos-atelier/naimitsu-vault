// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Safe decryption helper for ICryptoService.
/// DecryptToPin self-allocates a fixed POH array via GC.AllocateArray(pinned:true) and writes
/// directly into void Decrypt(Span output), preventing plaintext ghosts from remaining on the movable heap.
/// The caller must call CryptographicOperations.ZeroMemory on the returned byte[]? once done using it.
/// </summary>
internal static class CryptoServiceExtensions
{
    /// <summary>
    /// Decrypts directly into a GC-pinned array and returns it to the caller as byte[]?.
    /// On decryption failure, zeroes the pinned buffer and returns null (does not propagate the exception).
    /// The caller must call CryptographicOperations.ZeroMemory on the returned byte[] once done using it.
    /// </summary>
    internal static byte[]? DecryptToPin(this ICryptoService crypto, byte[]? data, ReadOnlySpan<byte> key)
    {
        if (data is not { Length: > ICryptoService.AeadOverhead }) return null;
        var pinned = GC.AllocateArray<byte>(data.Length - ICryptoService.AeadOverhead, pinned: true);
        try
        {
            crypto.Decrypt(data, key, pinned);
            return pinned;
        }
        catch (CryptographicException)
        {
            // Decryption failed (data corruption or key mismatch) — zero the pinned buffer and return null
            CryptographicOperations.ZeroMemory(pinned.AsSpan());
            return null;
        }
    }
}
