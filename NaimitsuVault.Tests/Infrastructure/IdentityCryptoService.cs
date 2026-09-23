// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only "identity" crypto service.
/// Encrypt: concatenates a HeaderSize-byte dummy header (standing in for nonce+tag) with
/// the plaintext, unchanged.
/// Decrypt: skips the leading HeaderSize bytes and copies the rest into destination.
/// The DEK is ignored. Used for StoredFileRepository's pass-through tests and FileName
/// round-trip verification.
///
/// Like the real CryptoService.Decrypt, it throws CryptographicException when source is
/// shorter than the header (a source of exactly HeaderSize bytes is a valid empty plaintext).
/// It has no authentication tag, so it can never detect tampering.
///
/// [Do not use] for verifying catch (CryptographicException) handling around tampered
/// auth tags or ciphertext. Inject the real CryptoService for those tests
/// (see StoredFileRepositoryBoundaryTests' TC-SFR-08, "tampered auth tag → CryptographicException
/// is caught").
/// </summary>
internal sealed class IdentityCryptoService : ICryptoService
{
    /// <summary>Dummy header size (nonce 12B + tag 16B). Also referenced by WinHelloDbSeeder.</summary>
    internal const int HeaderSize = 28;

    public void DeriveKey(ReadOnlySpan<char> _, ReadOnlySpan<byte> __, Span<byte> ___) { }
    public byte[] GenerateSalt(int size = 32) => new byte[size];

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> _, ReadOnlySpan<byte> __ = default)
    {
        var blob = new byte[HeaderSize + plaintext.Length];
        plaintext.CopyTo(blob.AsSpan(HeaderSize));
        return blob;
    }

    public void Decrypt(ReadOnlySpan<byte> source, ReadOnlySpan<byte> _, Span<byte> destination,
                        ReadOnlySpan<byte> __ = default)
    {
        if (source.Length < HeaderSize)
            throw new CryptographicException("Encrypted data is invalid.");
        source[HeaderSize..].CopyTo(destination);
    }
}
