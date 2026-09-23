// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Encryption/decryption boundary values" - all 8 cases for CryptoService.
/// The all-zero DEK is test-only and deliberate (real usage must use GenerateSalt + DeriveKey).
/// </summary>
public sealed class CryptoServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static CryptoService Svc() => new(NullLogger<CryptoService>.Instance);

    private static byte[] RandomDek()
    {
        var k = new byte[32];
        RandomNumberGenerator.Fill(k);
        return k;
    }

    // ── TC-CRY-01 ────────────────────────────────────────────────────────────────

    [Fact]
    public void EncryptThenDecrypt_EmptyPlaintext_RoundtripsToEmpty()
    {
        // Arrange
        var svc   = Svc();
        var dek   = new byte[32]; // test-only, all zeros
        byte[] plain = [];

        // Act
        var cipher = svc.Encrypt(plain, dek);
        var output = new byte[0];
        svc.Decrypt(cipher, dek, output);

        // Assert - cipher is fixed at nonce(12) + tag(16) + ciphertext(0) = 28 bytes
        Assert.Equal(28, cipher.Length);
        Assert.Empty(output);
    }

    // ── TC-CRY-02 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Encrypt_OutputLength_Equals_Nonce12_Tag16_Plus_PlaintextLength()
    {
        // Arrange
        var svc   = Svc();
        var dek   = RandomDek();
        var plain = new byte[100];
        RandomNumberGenerator.Fill(plain);

        // Act
        var cipher = svc.Encrypt(plain, dek);

        // Assert
        Assert.Equal(28 + 100, cipher.Length);
    }

    // ── TC-CRY-03 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Encrypt_TwoCallsSamePlaintext_ProduceDifferentNonces()
    {
        // Arrange
        var svc   = Svc();
        var dek   = RandomDek();
        var plain = new byte[32];
        RandomNumberGenerator.Fill(plain);

        // Act - the leading 12 bytes are the nonce
        var c1 = svc.Encrypt(plain, dek);
        var c2 = svc.Encrypt(plain, dek);

        // Assert
        Assert.False(c1[..12].SequenceEqual(c2[..12]));
    }

    // ── TC-CRY-04 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_DataShorterThan28Bytes_ThrowsCryptographicException()
    {
        // Arrange
        var svc      = Svc();
        var dek      = RandomDek();
        var tooShort = new byte[10]; // under the nonce+tag minimum of 28 bytes
        var output   = new byte[0];

        // Act & Assert - accepts CryptographicException or a derived class (e.g. AuthenticationTagMismatchException)
        Assert.ThrowsAny<CryptographicException>(() => svc.Decrypt(tooShort, dek, output));
    }

    // ── TC-CRY-05 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_WrongDek_ThrowsCryptographicException()
    {
        // Arrange
        var svc   = Svc();
        var dekA  = RandomDek();
        var dekB  = RandomDek();
        var plain = new byte[32];
        RandomNumberGenerator.Fill(plain);
        var cipher = svc.Encrypt(plain, dekA);
        var output = new byte[plain.Length];

        // Act & Assert - GCM auth tag mismatch. On .NET 10, AuthenticationTagMismatchException (a derived class) is thrown
        Assert.ThrowsAny<CryptographicException>(() => svc.Decrypt(cipher, dekB, output));
    }

    // ── TC-CRY-06 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        // Arrange
        var svc    = Svc();
        var dek    = RandomDek();
        var plain  = new byte[32];
        RandomNumberGenerator.Fill(plain);
        var cipher = svc.Encrypt(plain, dek);
        cipher[28] ^= 0xFF; // tamper with the ciphertext's first byte (index 28 = right after nonce12+tag16)
        var output = new byte[plain.Length];

        // Act & Assert - tampering detected. On .NET 10, AuthenticationTagMismatchException (a derived class) is thrown
        Assert.ThrowsAny<CryptographicException>(() => svc.Decrypt(cipher, dek, output));
    }

    // ── TC-CRY-07 ────────────────────────────────────────────────────────────────

    [Fact]
    public void DecryptToPin_NullData_ReturnsNull()
    {
        // Arrange
        var svc = Svc();
        var dek = RandomDek();

        // Act - CryptoServiceExtensions.DecryptToPin (internal) is accessible via InternalsVisibleTo
        var result = svc.DecryptToPin(null, dek);

        // Assert
        Assert.Null(result);
    }

    // ── TC-CRY-08 ────────────────────────────────────────────────────────────────

    [Fact]
    public void EncryptDecrypt_LargePlaintext_65535Bytes_RoundtripsCorrectly()
    {
        // Arrange - equivalent to the Notes field's maximum length
        var svc   = Svc();
        var dek   = RandomDek();
        var plain = new byte[65535];
        RandomNumberGenerator.Fill(plain);

        // Act
        var cipher = svc.Encrypt(plain, dek);
        var output = new byte[plain.Length];
        svc.Decrypt(cipher, dek, output);

        // Assert
        Assert.Equal(plain, output);
    }
}
