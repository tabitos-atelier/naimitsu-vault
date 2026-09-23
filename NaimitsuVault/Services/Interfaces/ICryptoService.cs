// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public interface ICryptoService
{
    /// <summary>AES-256-GCM nonce size in bytes (96-bit, GCM standard recommendation).</summary>
    public const int NonceSize = 12;

    /// <summary>AES-256-GCM authentication tag size in bytes (128-bit).</summary>
    public const int TagSize = 16;

    /// <summary>
    /// Fixed per-blob overhead of the storage format (nonce + tag) that every AES-256-GCM output
    /// produced by <see cref="Encrypt"/> carries ahead of the ciphertext. Single source of truth for
    /// the "- 28" / "&lt;= 28" arithmetic scattered across callers that need to size a plaintext buffer.
    /// </summary>
    public const int AeadOverhead = NonceSize + TagSize;

    /// <summary>
    /// Derives a key from the master password with Argon2id and writes it into the caller-provided buffer.
    /// Passing a stackalloc or GC.AllocateArray(pinned:true) buffer completely eliminates
    /// key-material residue (ghosts) on the movable heap.
    /// </summary>
    void DeriveKey(ReadOnlySpan<char> masterPassword, ReadOnlySpan<byte> salt, Span<byte> output);

    /// <summary>Generates a salt using a cryptographic RNG. Returned as a movable array since the salt is public data.</summary>
    byte[] GenerateSalt(int size = 32);

    /// <summary>Encrypts with AES-256-GCM. Output format: nonce(<see cref="NonceSize"/>) + tag(<see cref="TagSize"/>) + ciphertext. Returning byte[] is fine since ciphertext isn't secret.</summary>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData = default);

    /// <summary>
    /// Decrypts with AES-256-GCM and writes into the caller-provided buffer.
    /// Requires output.Length == cipherData.Length - <see cref="AeadOverhead"/>.
    /// Decrypting directly into a GC.AllocateArray(pinned:true) buffer eliminates plaintext ghosts.
    /// </summary>
    void Decrypt(ReadOnlySpan<byte> cipherData, ReadOnlySpan<byte> key, Span<byte> output, ReadOnlySpan<byte> associatedData = default);
}
