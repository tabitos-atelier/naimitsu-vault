// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// A known-answer-test (KAT) suite that verifies the mathematical correctness of cryptographic
/// primitives against the official vectors from NIST SP 800-38D (AES-256-GCM) and RFC 9106 (Argon2id).
///
/// The existing CryptoServiceTests are round-trip tests (random input).
/// This suite complements them with primitive-level verification guaranteeing fixed-input ->
/// fixed-output matching.
///
/// Test case numbering scheme:
///   TC-VEC-01..V4  : AES-256-GCM NIST vectors
///   TC-ARG-01..A3  : Argon2id RFC / production KAT / DeriveKey determinism
///   TC-ROB-01..R6  : boundary values / robustness (TC-ROB-03 uses the AesGcm primitive directly; the rest go through CryptoService)
/// </summary>
public sealed class CryptoVectorTests
{
    // ════════════════════════════════════════════════════════════════════════
    // NIST SP 800-38D Appendix B - AES-256-GCM constants (TC13..16)
    // ════════════════════════════════════════════════════════════════════════

    // TC-VEC-01 (TC13): Key=32 zeros, IV=12 zeros, PT=empty, AAD=empty
    private const string V1Key   = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string V1Nonce = "000000000000000000000000";
    private const string V1Pt    = "";
    private const string V1Aad   = "";
    private const string V1Ct    = "";
    private const string V1Tag   = "530f8afbc74536b9a963b4f1c4cb738b";

    // TC-VEC-02 (TC14): Key=32 zeros, IV=12 zeros, PT=16 zeros
    private const string V2Key   = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string V2Nonce = "000000000000000000000000";
    private const string V2Pt    = "00000000000000000000000000000000";
    private const string V2Aad   = "";
    private const string V2Ct    = "cea7403d4d606b6e074ec5d3baf39d18";
    private const string V2Tag   = "d0d1c8a799996bf0265b98b5d48ab919";

    // TC-VEC-03: feffe9 key, cafebabe IV, 60-byte PT, no AAD
    // CT is determined solely by Key/IV/PT, so it's identical to TC-VEC-04. Tag is the value unique to having no AAD.
    // NIST SP 800-38D TC-15 uses a different PT (for reference: a 64-byte version with 4 extra bytes
    // prepended to TC-16's 60-byte PT), so this vector is an independently computed AES-256-GCM value
    // over the same Key/IV/PT (its purpose is to contrast-verify AAD binding against V4).
    private const string V3Key   = "feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308";
    private const string V3Nonce = "cafebabefacedbaddecaf888";
    private const string V3Pt    = "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39";
    private const string V3Aad   = "";
    private const string V3Ct    = "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662";
    private const string V3Tag   = "eb9f796c8d356fc31a8433884b696f4f";

    // TC-VEC-04 (TC16): same Key/IV/PT as V3, 20-byte AAD. CT is identical to V3, only Tag differs
    // A property of GCM: the ciphertext is determined solely by Key/IV/PT; AAD only affects the Tag
    private const string V4Key   = "feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308";
    private const string V4Nonce = "cafebabefacedbaddecaf888";
    private const string V4Pt    = "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39";
    private const string V4Aad   = "feedfacedeadbeeffeedfacedeadbeefabaddad2";
    private const string V4Ct    = "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662";
    private const string V4Tag   = "76fc6ece0f4e1768cddf8853bb2d551b";

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CryptoService Svc() => new(NullLogger<CryptoService>.Instance);

    // Builds CryptoService's blob format (nonce(12) + tag(16) + ciphertext) from a NIST vector
    private static byte[] BuildNistBlob(string nonceHex, string tagHex, string ctHex)
    {
        var nonce = Convert.FromHexString(nonceHex);
        var tag   = Convert.FromHexString(tagHex);
        var ct    = ctHex.Length > 0 ? Convert.FromHexString(ctHex) : Array.Empty<byte>();
        var blob  = new byte[nonce.Length + tag.Length + ct.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, nonce.Length);
        ct.CopyTo(blob, nonce.Length + tag.Length);
        return blob;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Direct AesGcm primitive tests (all of TC-VEC-01..V4)
    // ════════════════════════════════════════════════════════════════════════

    // TC-VEC-01..V4: verifies AesGcm.Encrypt's output matches the official NIST values byte-for-byte.
    // TC-VEC-04 has AAD: CT is identical to TC-VEC-03, only Tag differs (a direct proof of GCM's AAD binding).
    [Theory]
    [InlineData(V1Key, V1Nonce, V1Pt, V1Aad, V1Ct, V1Tag)]
    [InlineData(V2Key, V2Nonce, V2Pt, V2Aad, V2Ct, V2Tag)]
    [InlineData(V3Key, V3Nonce, V3Pt, V3Aad, V3Ct, V3Tag)]
    [InlineData(V4Key, V4Nonce, V4Pt, V4Aad, V4Ct, V4Tag)]
    public void AesGcm_Encrypt_NistVector_MatchesExpectedOutput(
        string keyHex, string ivHex, string ptHex, string aadHex,
        string expectedCtHex, string expectedTagHex)
    {
        var key = Convert.FromHexString(keyHex);
        var iv  = Convert.FromHexString(ivHex);
        var pt  = ptHex.Length  > 0 ? Convert.FromHexString(ptHex)  : Array.Empty<byte>();
        var aad = aadHex.Length > 0 ? Convert.FromHexString(aadHex) : Array.Empty<byte>();

        using var aesGcm = new AesGcm(key, 16); // TagSizeInBytes = 16 (fixed at 128 bits)
        var ct  = new byte[pt.Length];
        var tag = new byte[16];
        aesGcm.Encrypt(iv, pt, ct, tag, aad);

        var expectedCt  = expectedCtHex.Length  > 0 ? Convert.FromHexString(expectedCtHex)  : Array.Empty<byte>();
        var expectedTag = Convert.FromHexString(expectedTagHex);
        Assert.Equal(expectedCt,  ct);
        Assert.Equal(expectedTag, tag);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CryptoService.Decrypt NIST blob tests (TC-VEC-01..V3, no-AAD only)
    // ════════════════════════════════════════════════════════════════════════

    // TC-VEC-01..V3: builds a blob (nonce+tag+ciphertext) from a NIST vector and decrypts it via CryptoService.Decrypt.
    // TC-VEC-04 (with AAD) is excluded: since the app never uses AAD when encrypting Secrets, that case
    // is verified at the AesGcm layer only.
    // Note on TC-VEC-01: blob = 28 bytes (passes the leading guard's boundary value of 28), output = new byte[0] (empty span).
    [Theory]
    [InlineData(V1Key, V1Nonce, V1Tag, V1Ct, V1Pt)]
    [InlineData(V2Key, V2Nonce, V2Tag, V2Ct, V2Pt)]
    [InlineData(V3Key, V3Nonce, V3Tag, V3Ct, V3Pt)]
    public void CryptoService_Decrypt_NistBlob_ProducesNistPlaintext(
        string keyHex, string nonceHex, string tagHex, string ctHex, string expectedPtHex)
    {
        var key      = Convert.FromHexString(keyHex);
        var blob     = BuildNistBlob(nonceHex, tagHex, ctHex);
        var expected = expectedPtHex.Length > 0 ? Convert.FromHexString(expectedPtHex) : Array.Empty<byte>();
        var output   = new byte[expected.Length]; // for TC-VEC-01 this is new byte[0] (empty span)

        Svc().Decrypt(blob, key, output);

        Assert.Equal(expected, output);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Argon2id test vectors (RFC 9106)
    // ════════════════════════════════════════════════════════════════════════

    // TC-ARG-01: a vector equivalent to RFC 9106 Appendix B (Argon2id, m=32 KiB, no Secret/AD)
    // Verifies that with Password = 32x0x01, Salt = 16x0x02, m=32, t=3, p=4, Konscious's output
    // matches argon2-cffi's (Python / C reference implementation, version=19).
    //
    // Warning: a Konscious 1.3.1 limitation - Konscious does not reproduce the full RFC 9106
    // Appendix B.4 vector that includes KnownSecret / AssociatedData (expected value 0d640df5...),
    // because Konscious's internal handling of KnownSecret diverges from the RFC. However, since the
    // app (CryptoService.DeriveKey) never uses KnownSecret / AssociatedData at all, this divergence
    // has no impact on this app's security.
    // This test, which omits Secret/AD, proves that "the Argon2id core algorithm implementation is correct".
    [Fact]
    public void Argon2id_RfcLikeVector_MatchesArgon2CffiOutput()
    {
        var password = Convert.FromHexString("0101010101010101010101010101010101010101010101010101010101010101");
        var salt     = Convert.FromHexString("02020202020202020202020202020202");

        using var argon2 = new Argon2id(password)
        {
            Salt                = salt,
            Iterations          = 3,
            MemorySize          = 32,   // 32 KiB, equivalent to RFC Appendix B
            DegreeOfParallelism = 4,
        };

        var result = argon2.GetBytes(32);
        // Independently computed value via argon2-cffi (Python, version=19):
        // hash_secret_raw(secret=bytes([1]*32), salt=bytes([2]*16),
        //                 time_cost=3, memory_cost=32, parallelism=4,
        //                 hash_len=32, type=Type.ID, version=19)
        var expected = Convert.FromHexString("03aab965c12001c9d7d0d2de33192c0494b684bb148196d73c1df1acaf6d0c2e");
        Assert.Equal(expected, result);
    }

    // TC-ARG-02: production-parameter KAT (m=65536, t=3, p=4)
    // Verifies a match against a value independently computed by argon2-cffi (Python / C reference
    // implementation, version=19). This serves as cross-library verification against Konscious and
    // can detect implementation bugs.
    [Fact]
    [Trait("Category", "SlowTest")]
    public void Argon2id_ProductionParams_MatchesHardcodedKat()
    {
        // Independently computed via argon2-cffi (version=19, no secret, no AD):
        // hash_secret_raw(secret=b'password', salt=bytes(32), time_cost=3,
        //                 memory_cost=65536, parallelism=4, hash_len=32,
        //                 type=Type.ID, version=19)
        const string ExpectedKat = "592555a93064d6ac4a58d740c0ac9c8eda33b2d4a916b88efd88ea9d6ab65cc2";

        var password = Encoding.UTF8.GetBytes("password");
        var salt     = new byte[32]; // all zeros

        using var argon2 = new Argon2id(password)
        {
            Salt                = salt,
            Iterations          = 3,
            MemorySize          = 65536, // 64 MB (production setting)
            DegreeOfParallelism = 4,
        };

        var result = argon2.GetBytes(32);
        Assert.Equal(Convert.FromHexString(ExpectedKat), result);
    }

    // TC-ARG-03: CryptoService.DeriveKey - the same 32-byte key is derived every time from the same input (determinism check)
    [Fact]
    public void CryptoService_DeriveKey_SameInputProducesSameKey()
    {
        var svc      = Svc();
        var password = "テストパスワード".AsSpan();
        var salt     = new byte[32];
        var key1     = new byte[32];
        var key2     = new byte[32];

        svc.DeriveKey(password, salt, key1);
        svc.DeriveKey(password, salt, key2);

        Assert.Equal(key1, key2);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Boundary-value / robustness tests
    // ════════════════════════════════════════════════════════════════════════

    // TC-ROB-01: flipping 1 bit of the auth tag -> AesGcm.Decrypt authentication fails (CryptographicException)
    [Fact]
    public void Decrypt_TamperedTag_ThrowsCryptographicException()
    {
        var blob = BuildNistBlob(V2Nonce, V2Tag, V2Ct);
        blob[12] ^= 0x01; // flip 1 bit of the tag's first byte (index 12). The tag region is [12..28]

        Assert.ThrowsAny<CryptographicException>(
            () => Svc().Decrypt(blob, Convert.FromHexString(V2Key), new byte[16]));
    }

    // TC-ROB-02: a 27-byte blob (1 byte short of the leading guard's boundary value of 28) -> caught by CryptoService's leading guard
    // Kept separate from TC-ROB-06 (0 bytes) rather than merged: 27 represents the specific boundary of "a complete nonce, but the tag is missing 1 byte"
    [Fact]
    public void Decrypt_27ByteBlob_ThrowsCryptographicException()
    {
        var blob = new byte[27]; // nonce(12) + tag_partial(15): reaches the data.Length < 28 guard condition

        Assert.ThrowsAny<CryptographicException>(
            () => Svc().Decrypt(blob, new byte[32], Array.Empty<byte>()));
    }

    // TC-ROB-03: passing AesGcm an invalid-length nonce (11 or 13 bytes) -> ArgumentException
    // This never happens via CryptoService (it always generates a fixed 12 bytes via RandomNumberGenerator.Fill).
    // A primitive-level safety-net test: guarantees AesGcm rejects any nonce that isn't 12 bytes.
    [Theory]
    [InlineData(11)] // under 96 bits
    [InlineData(13)] // over 96 bits
    public void AesGcm_InvalidNonceLength_ThrowsArgumentException(int nonceLength)
    {
        var nonce = new byte[nonceLength];
        var ct    = Array.Empty<byte>();
        var tag   = new byte[16];
        using var aesGcm = new AesGcm(new byte[32], 16);

        Assert.ThrowsAny<ArgumentException>(
            () => aesGcm.Encrypt(nonce, Array.Empty<byte>(), ct, tag));
    }

    // TC-ROB-04: a 0-byte ciphertext (TC-VEC-01's NIST blob, 28 bytes = passes the leading guard's boundary value of 28)
    // Authenticating "empty plaintext" must succeed normally (additional proof that TC-VEC-01's NIST tag is correct)
    [Fact]
    public void Decrypt_ZeroByteCiphertext_NistV1Blob_Succeeds()
    {
        var blob   = BuildNistBlob(V1Nonce, V1Tag, V1Ct); // V1Ct = "" -> 28 bytes total
        var output = Array.Empty<byte>();                  // empty plaintext: output is an empty span

        // No exception - confirms AesGcm succeeds at authenticating an empty ciphertext
        Svc().Decrypt(blob, Convert.FromHexString(V1Key), output);
    }

    // TC-ROB-05: flipping the ciphertext body's last byte -> AesGcm.Decrypt authentication fails (CryptographicException)
    [Fact]
    public void Decrypt_CorruptedCiphertext_ThrowsCryptographicException()
    {
        var blob = BuildNistBlob(V2Nonce, V2Tag, V2Ct);
        blob[^1] ^= 0xFF; // flip the blob's last byte = the ciphertext's last byte

        Assert.ThrowsAny<CryptographicException>(
            () => Svc().Decrypt(blob, Convert.FromHexString(V2Key), new byte[16]));
    }

    // TC-ROB-06: a 0-byte blob -> caught by CryptoService's leading guard (data.Length < 28)
    // Kept intentionally separate from TC-ROB-02 (27 bytes): this is the minimal case of "completely empty garbage data"
    [Fact]
    public void Decrypt_ZeroLengthBlob_ThrowsCryptographicException()
    {
        Assert.ThrowsAny<CryptographicException>(
            () => Svc().Decrypt(Array.Empty<byte>(), new byte[32], Array.Empty<byte>()));
    }
}
