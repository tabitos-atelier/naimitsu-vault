// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public sealed class CryptoService(ILogger<CryptoService> logger) : ICryptoService
{
    public void DeriveKey(ReadOnlySpan<char> masterPassword, ReadOnlySpan<byte> salt, Span<byte> output)
    {
        logger.LogDebug("Starting Argon2id key derivation (POH-pinned). (Parallelism={Parallelism}, Memory={Memory}KB, Iterations={Iterations})",
            Argon2Parameters.Parallelism, Argon2Parameters.MemorySize, Argon2Parameters.Iterations);

        // Allocate on the POH (pinned object heap): even if a GC compaction runs during the heavy
        // Argon2id computation, the address never moves, so the finally ZeroMemory always wipes the correct address
        int byteCount = Encoding.UTF8.GetByteCount(masterPassword);
        var passwordBytes = GC.AllocateArray<byte>(byteCount, pinned: true);
        Encoding.UTF8.GetBytes(masterPassword, passwordBytes.AsSpan());
        try
        {
            var saltArr = GC.AllocateArray<byte>(salt.Length, pinned: true);
            salt.CopyTo(saltArr);
            try
            {
                using var argon2 = new Argon2id(passwordBytes)
                {
                    Salt = saltArr,
                    DegreeOfParallelism = Argon2Parameters.Parallelism,
                    MemorySize = Argon2Parameters.MemorySize,
                    Iterations = Argon2Parameters.Iterations,
                };
                var key = argon2.GetBytes(output.Length);
                try { key.AsSpan().CopyTo(output); }
                finally { CryptographicOperations.ZeroMemory(key.AsSpan()); }
            }
            finally { CryptographicOperations.ZeroMemory(saltArr.AsSpan()); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes.AsSpan());
        }
        logger.LogDebug("Key derivation completed (POH-pinned).");
    }

    public byte[] GenerateSalt(int size = 32)
    {
        logger.LogDebug("Generating a new salt ({Size} bytes).", size);
        var salt = new byte[size];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key,
                          ReadOnlySpan<byte> associatedData = default)
    {
        // stackalloc: isolate the tiny fixed buffer entirely on the stack, excluding it from GC tracking
        Span<byte> nonce = stackalloc byte[ICryptoService.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        Span<byte> tag = stackalloc byte[ICryptoService.TagSize];
        try
        {
            // Storage format: nonce(12) + tag(16) + ciphertext. Encrypt directly into the tail of
            // result instead of a separate ciphertext array, halving the allocation/copy for large
            // (multi-MB attachment) payloads.
            var result = new byte[ICryptoService.AeadOverhead + plaintext.Length];
            using var aes = new AesGcm(key, ICryptoService.TagSize);
            aes.Encrypt(nonce, plaintext, result.AsSpan(ICryptoService.AeadOverhead), tag, associatedData);
            nonce.CopyTo(result.AsSpan(0, ICryptoService.NonceSize));
            tag.CopyTo(result.AsSpan(ICryptoService.NonceSize, ICryptoService.TagSize));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public void Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, Span<byte> output,
                        ReadOnlySpan<byte> associatedData = default)
    {
        if (data.Length < ICryptoService.AeadOverhead)
        {
            logger.LogError("Encrypted data length is insufficient (span-output). (Length={Length})", data.Length);
            throw new CryptographicException("Encrypted data is invalid.");
        }
        var nonce      = data[..ICryptoService.NonceSize];
        var tag        = data[ICryptoService.NonceSize..ICryptoService.AeadOverhead];
        var ciphertext = data[ICryptoService.AeadOverhead..];
        using var aes = new AesGcm(key, ICryptoService.TagSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, output, associatedData);
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning("Decryption failed (span-output). The key is incorrect or the data is corrupted. [{ExType}]", ex.GetType().Name);
            throw;
        }
    }
}
