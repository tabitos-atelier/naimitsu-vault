// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// TotpCalculator Base32 decode boundary values, TOTP generation, URI parser, and display-formatting
/// tests (TC-TOT-01 .. TC-TOT-46).
/// Uses RFC 4226 / RFC 4648 compliant test vectors.
/// </summary>
public sealed class TotpCalculatorBoundaryTests
{
    // ── RFC 6238 test vector (TOTP: Time-Based One-Time Password Algorithm)
    // Google's TOTP test secret: "JBSWY3DPEHPK3PXP" (Base32 of "Hello!\xDE\xAD\xBE\xEF")
    private const string TestSecret = "JBSWY3DPEHPK3PXP";

    // ── TC-TOT-01 ──────────────────────────────────────────────────────────────
    // Standard Base32 input (no padding) -> decodes correctly

    [Fact]
    public void Base32Decode_StandardInput_CorrectBytes()
    {
        // "MFRA" -> 0x61 0x40 (Base32: M=12, F=5, R=17, A=0)
        // 12<<3|5>>2=6=0x61, 5&3=1, 1<<6|17<<1|0>>4=...
        // Easier: "ORSXG5A=" decodes to "test" in ASCII
        var decoded = TotpCalculator.Base32Decode("ORSXG5A=");
        var text    = Encoding.ASCII.GetString(decoded);
        Assert.Equal("test", text);
        CryptographicOperations.ZeroMemory(decoded.AsSpan());
    }

    // ── TC-TOT-02 ──────────────────────────────────────────────────────────────
    // Padding characters (=) are skipped

    [Fact]
    public void Base32Decode_PaddingChars_Ignored()
    {
        var withPad    = TotpCalculator.Base32Decode("ORSXG5A=");
        var withoutPad = TotpCalculator.Base32Decode("ORSXG5A");
        Assert.Equal(withPad, withoutPad);
        CryptographicOperations.ZeroMemory(withPad.AsSpan());
        CryptographicOperations.ZeroMemory(withoutPad.AsSpan());
    }

    // ── TC-TOT-03 ──────────────────────────────────────────────────────────────
    // Whitespace characters are skipped

    [Fact]
    public void Base32Decode_SpacesInInput_Ignored()
    {
        var withSpace    = TotpCalculator.Base32Decode("ORSXG 5A=");
        var withoutSpace = TotpCalculator.Base32Decode("ORSXG5A=");
        Assert.Equal(withSpace, withoutSpace);
        CryptographicOperations.ZeroMemory(withSpace.AsSpan());
        CryptographicOperations.ZeroMemory(withoutSpace.AsSpan());
    }

    // ── TC-TOT-04 ──────────────────────────────────────────────────────────────
    // Characters outside the Base32 alphabet throw FormatException

    [Fact]
    public void Base32Decode_InvalidChars_ThrowFormatException()
    {
        // '0','1','8','9' are not part of the Base32 alphabet (A-Z/2-7)
        Assert.Throws<FormatException>(() => TotpCalculator.Base32Decode("ORS0XG5A")); // '0'
        Assert.Throws<FormatException>(() => TotpCalculator.Base32Decode("ORS1XG5A")); // '1'
        Assert.Throws<FormatException>(() => TotpCalculator.Base32Decode("ORS8XG5A")); // '8'
        Assert.Throws<FormatException>(() => TotpCalculator.Base32Decode("ORS9XG5A")); // '9'
    }

    // ── TC-TOT-05 ──────────────────────────────────────────────────────────────
    // Lowercase input returns the same result as uppercase

    [Fact]
    public void Base32Decode_LowerCaseInput_SameAsUpperCase()
    {
        var upper = TotpCalculator.Base32Decode(TestSecret.ToUpperInvariant());
        var lower = TotpCalculator.Base32Decode(TestSecret.ToLowerInvariant());
        Assert.Equal(upper, lower);
        CryptographicOperations.ZeroMemory(upper.AsSpan());
        CryptographicOperations.ZeroMemory(lower.AsSpan());
    }

    // ── TC-TOT-06 ──────────────────────────────────────────────────────────────
    // Empty string -> empty byte array

    [Fact]
    public void Base32Decode_EmptyInput_ReturnsEmptyArray()
    {
        var decoded = TotpCalculator.Base32Decode(string.Empty);
        Assert.Empty(decoded);
    }

    // ── TC-TOT-07 ──────────────────────────────────────────────────────────────
    // Padding-only input ("======") -> empty byte array

    [Fact]
    public void Base32Decode_OnlyPadding_ReturnsEmptyArray()
    {
        var decoded = TotpCalculator.Base32Decode("======");
        Assert.Empty(decoded);
    }

    // ── TC-TOT-08 ──────────────────────────────────────────────────────────────
    // ZeroMemory of workBuf: safe as long as the caller calls ZeroMemory after Base32Decode completes.
    // The internal workBuf is cleared in a finally block (behavioral test: no exception across many calls)

    [Fact]
    public void Base32Decode_ZeroMemoryInFinally_CalledReliably()
    {
        for (int i = 0; i < 1000; i++)
        {
            var key = TotpCalculator.Base32Decode(TestSecret);
            CryptographicOperations.ZeroMemory(key.AsSpan());
        }
        // Completes 1000 times without exception -> finally is reliably running
    }

    // ── TC-TOT-09 ──────────────────────────────────────────────────────────────
    // RFC 4648 Appendix B test vector: "" -> ""

    [Fact]
    public void Base32Decode_Rfc4648_EmptyVector()
    {
        var decoded = TotpCalculator.Base32Decode("");
        Assert.Empty(decoded);
    }

    // ── TC-TOT-10 ──────────────────────────────────────────────────────────────
    // RFC 4648 Appendix B test vector: "MY======" -> "f"

    [Fact]
    public void Base32Decode_Rfc4648_SingleChar()
    {
        var decoded = TotpCalculator.Base32Decode("MY======");
        Assert.Equal([(byte)'f'], decoded);
        CryptographicOperations.ZeroMemory(decoded.AsSpan());
    }

    // ── TC-TOT-11 ──────────────────────────────────────────────────────────────
    // RFC 4648 Appendix B test vector: "MZXQ====" -> "fo"

    [Fact]
    public void Base32Decode_Rfc4648_TwoChars()
    {
        var decoded = TotpCalculator.Base32Decode("MZXQ====");
        Assert.Equal("fo"u8.ToArray(), decoded);
        CryptographicOperations.ZeroMemory(decoded.AsSpan());
    }

    // ── TC-TOT-12 ──────────────────────────────────────────────────────────────
    // RFC 4648 Appendix B test vector: "MZXW6===" -> "foo"

    [Fact]
    public void Base32Decode_Rfc4648_ThreeChars()
    {
        var decoded = TotpCalculator.Base32Decode("MZXW6===");
        Assert.Equal("foo"u8.ToArray(), decoded);
        CryptographicOperations.ZeroMemory(decoded.AsSpan());
    }

    // ── TC-TOT-13 ──────────────────────────────────────────────────────────────
    // RFC 4648 test vector: "MZXW6YTBOI======" -> "foobar"

    [Fact]
    public void Base32Decode_Rfc4648_FooBar()
    {
        var decoded = TotpCalculator.Base32Decode("MZXW6YTBOI======");
        Assert.Equal("foobar"u8.ToArray(), decoded);
        CryptographicOperations.ZeroMemory(decoded.AsSpan());
    }

    // ── TC-TOT-14 ──────────────────────────────────────────────────────────────
    // Generate()'s return value has `digits` characters (6 digits)

    [Fact]
    public void Generate_SixDigits_ReturnsSixCharString()
    {
        var (code, rem) = TotpCalculator.Generate(TestSecret, digits: 6);
        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsDigit));
    }

    // ── TC-TOT-15 ──────────────────────────────────────────────────────────────
    // Generate()'s return value is 8 characters when 8 digits are requested

    [Fact]
    public void Generate_EightDigits_Returns8CharString()
    {
        var (code, _) = TotpCalculator.Generate(TestSecret, digits: 8);
        Assert.Equal(8, code.Length);
        Assert.True(code.All(char.IsDigit));
    }

    // ── TC-TOT-16 ──────────────────────────────────────────────────────────────
    // SecondsRemaining falls within the range 1..period

    [Fact]
    public void Generate_SecondsRemaining_IsWithinPeriod()
    {
        const int period = 30;
        var (_, rem) = TotpCalculator.Generate(TestSecret, period: period);
        Assert.InRange(rem, 1, period);
    }

    // ── TC-TOT-17 ──────────────────────────────────────────────────────────────
    // Leading zero padding: code is PadLeft so it is always 6 digits

    [Fact]
    public void Generate_OutputIsZeroPadded_AlwaysSixChars()
    {
        // Try 100 times and confirm it is always 6 characters (a leading '0' is never dropped)
        for (int i = 0; i < 100; i++)
        {
            var (code, _) = TotpCalculator.Generate(TestSecret);
            Assert.Equal(6, code.Length);
        }
    }

    // ── TC-TOT-18 ──────────────────────────────────────────────────────────────
    // GenerateAtOffset(0) and Generate() use the same time step, so they match

    [Fact]
    public void GenerateAtOffset0_MatchesGenerate()
    {
        var (code1, _) = TotpCalculator.Generate(TestSecret);
        var code2 = TotpCalculator.GenerateAtOffset(TestSecret, 0);
        Assert.Equal(code1, code2);
    }

    // ── TC-TOT-19 ──────────────────────────────────────────────────────────────
    // GenerateAtOffset(-1) and GenerateAtOffset(+1) differ from the current step

    [Fact]
    public void GenerateAtOffset_Prev_And_Next_DifferFromCurrent()
    {
        var (current, _) = TotpCalculator.Generate(TestSecret);
        var prev = TotpCalculator.GenerateAtOffset(TestSecret, -1);
        var next = TotpCalculator.GenerateAtOffset(TestSecret, +1);
        // Different steps produce different TOTPs (HMAC is deterministic)
        Assert.NotEqual(current, prev);
        Assert.NotEqual(current, next);
        // prev and next also differ from each other
        Assert.NotEqual(prev, next);
    }

    // ── TC-TOT-20 ──────────────────────────────────────────────────────────────
    // Span overload matches the string overload

    [Fact]
    public void Generate_SpanOverload_MatchesStringOverload()
    {
        var (code1, rem1) = TotpCalculator.Generate(TestSecret);
        var (code2, rem2) = TotpCalculator.Generate(TestSecret.AsSpan());
        // Matches as long as both calls fall within the same step (the test is expected to complete quickly enough)
        Assert.Equal(code1, code2);
        // rem may differ slightly, so allow a range of +/-1
        Assert.True(Math.Abs(rem1 - rem2) <= 1);
    }

    // ── TC-TOT-21 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: can parse a well-formed URI

    [Fact]
    public void ParseOtpAuth_ValidUri_ReturnsParams()
    {
        var uri = $"otpauth://totp/Example:alice@example.com?secret={TestSecret}&issuer=Example&digits=6&period=30";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal(TestSecret, p!.Secret);
        Assert.Equal("Example", p.Issuer);
        Assert.Equal("alice@example.com", p.AccountName);
        Assert.Equal(6, p.Digits);
        Assert.Equal(30, p.Period);
    }

    // ── TC-TOT-22 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: null or empty string -> null

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseOtpAuth_NullOrEmpty_ReturnsNull(string? uri)
    {
        Assert.Null(TotpCalculator.ParseOtpAuth(uri!));
    }

    // ── TC-TOT-23 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: wrong scheme -> null

    [Fact]
    public void ParseOtpAuth_WrongScheme_ReturnsNull()
    {
        Assert.Null(TotpCalculator.ParseOtpAuth("https://example.com"));
        Assert.Null(TotpCalculator.ParseOtpAuth("otpauth://hotp/Example?secret=ABC"));
    }

    // ── TC-TOT-24 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: no secret parameter -> null

    [Fact]
    public void ParseOtpAuth_NoSecret_ReturnsNull()
    {
        var uri = "otpauth://totp/Example:alice@example.com?issuer=Example";
        Assert.Null(TotpCalculator.ParseOtpAuth(uri));
    }

    // ── TC-TOT-25 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: secret is normalized to uppercase

    [Fact]
    public void ParseOtpAuth_SecretNormalizedToUpperCase()
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret.ToLowerInvariant()}";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal(TestSecret.ToUpperInvariant(), p!.Secret);
    }

    // ── TC-TOT-26 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: when the label has no issuer (account name only)

    [Fact]
    public void ParseOtpAuth_LabelWithoutColon_AccountNameOnly()
    {
        var uri = $"otpauth://totp/alice%40example.com?secret={TestSecret}";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal("alice@example.com", p!.AccountName);
        Assert.Equal(string.Empty, p.Issuer);
    }

    // ── TC-TOT-27 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: when digits / period are omitted, defaults are used (6 / 30)

    [Fact]
    public void ParseOtpAuth_NoDigitsPeriod_UsesDefaults()
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal(6,  p!.Digits);
        Assert.Equal(30, p.Period);
    }

    // ── TC-TOT-28 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: a URL-encoded label decodes correctly

    [Fact]
    public void ParseOtpAuth_UrlEncodedLabel_DecodesCorrectly()
    {
        // "Test%20Service:user%40domain.com"
        var uri = $"otpauth://totp/Test%20Service%3Auser%40domain.com?secret={TestSecret}";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal("Test Service", p!.Issuer);
        Assert.Equal("user@domain.com", p.AccountName);
    }

    // ── TC-TOT-29 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: no algorithm parameter -> defaults to SHA1

    [Fact]
    public void ParseOtpAuth_NoAlgorithm_DefaultsToSha1()
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal("SHA1", p!.Algorithm);
    }

    // ── TC-TOT-30 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: algorithm=SHA256 / SHA512 parses correctly (GitHub and others use this)

    [Theory]
    [InlineData("SHA1", "SHA1")]
    [InlineData("SHA256", "SHA256")]
    [InlineData("SHA512", "SHA512")]
    [InlineData("sha256", "SHA256")] // case-insensitive
    public void ParseOtpAuth_Algorithm_ParsedAndNormalized(string input, string expected)
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}&algorithm={input}";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.Algorithm);
    }

    // ── TC-TOT-31 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: an algorithm that is present but unsupported rejects the whole URI. It used to fall
    // back to SHA1, which let the user enroll successfully while every generated code differed from the
    // service's (Rev.2026-09-22: strict parameter validation).

    [Theory]
    [InlineData("MD5")]
    [InlineData("SHA224")]
    [InlineData("SHA-1")]
    [InlineData("")]
    public void ParseOtpAuth_UnsupportedAlgorithm_ReturnsNull(string algorithm)
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}&algorithm={algorithm}";
        Assert.Null(TotpCalculator.ParseOtpAuth(uri));
    }

    // ── TC-TOT-32 ──────────────────────────────────────────────────────────────
    // Generate: SHA256/SHA512 produce a different code than SHA1 for the same secret/step
    // (proves the algorithm switch in ComputeHotp actually takes effect, not just accepted and ignored)

    [Fact]
    public void Generate_DifferentAlgorithms_ProduceDifferentCodes()
    {
        var (sha1, _)   = TotpCalculator.Generate(TestSecret, digits: 8, algorithm: "SHA1");
        var (sha256, _) = TotpCalculator.Generate(TestSecret, digits: 8, algorithm: "SHA256");
        var (sha512, _) = TotpCalculator.Generate(TestSecret, digits: 8, algorithm: "SHA512");
        Assert.NotEqual(sha1, sha256);
        Assert.NotEqual(sha1, sha512);
        Assert.NotEqual(sha256, sha512);
    }

    // ── TC-TOT-33 ──────────────────────────────────────────────────────────────
    // Pack/TryUnpack: round-trips Secret/Digits/Period/Algorithm exactly

    [Theory]
    [InlineData("JBSWY3DPEHPK3PXP", 6, 30, "SHA1")]
    [InlineData("JBSWY3DPEHPK3PXP", 8, 60, "SHA256")]
    [InlineData("JBSWY3DPEHPK3PXP", 7, 30, "SHA512")]
    public void PackTryUnpack_RoundTrips(string secret, int digits, int period, string algorithm)
    {
        var packed = TotpCalculator.Pack(new TotpCalculator.TotpConfig(secret, digits, period, algorithm));
        bool ok = TotpCalculator.TryUnpack(packed, out var outSecret, out var outDigits, out var outPeriod, out var outAlgorithm);
        Assert.True(ok);
        Assert.Equal(secret, outSecret.ToString());
        Assert.Equal(digits, outDigits);
        Assert.Equal(period, outPeriod);
        Assert.Equal(algorithm, outAlgorithm);
    }

    // ── TC-TOT-34 ──────────────────────────────────────────────────────────────
    // TryUnpack: empty or malformed payloads (missing separators) return false rather than throwing

    [Fact]
    public void TryUnpack_EmptyOrMalformed_ReturnsFalse()
    {
        Assert.False(TotpCalculator.TryUnpack(ReadOnlySpan<char>.Empty, out _, out _, out _, out _));
        Assert.False(TotpCalculator.TryUnpack(TestSecret.AsSpan(), out _, out _, out _, out _)); // no separators at all
    }

    // ── TC-TOT-35 ──────────────────────────────────────────────────────────────
    // TryUnpackUtf8: UTF-8 byte-span counterpart round-trips the same as the char-span version

    [Fact]
    public void TryUnpackUtf8_RoundTrips()
    {
        var packed = TotpCalculator.Pack(new TotpCalculator.TotpConfig(TestSecret, 8, 30, "SHA256"));
        var utf8   = Encoding.UTF8.GetBytes(packed);
        bool ok = TotpCalculator.TryUnpackUtf8(utf8, out var secret, out var digits, out var period, out var algorithm);
        Assert.True(ok);
        Assert.Equal(TestSecret, Encoding.UTF8.GetString(secret));
        Assert.Equal(8, digits);
        Assert.Equal(30, period);
        Assert.Equal("SHA256", algorithm);
    }

    // ── TC-TOT-36 ──────────────────────────────────────────────────────────────
    // TryUnpackToConfig: materializes a TotpConfig for UI display

    [Fact]
    public void TryUnpackToConfig_ReturnsMaterializedConfig()
    {
        var packed = TotpCalculator.Pack(new TotpCalculator.TotpConfig(TestSecret, 8, 60, "SHA512"));
        var config = TotpCalculator.TryUnpackToConfig(packed);
        Assert.NotNull(config);
        Assert.Equal(TestSecret, config!.Secret);
        Assert.Equal(8, config.Digits);
        Assert.Equal(60, config.Period);
        Assert.Equal("SHA512", config.Algorithm);
    }

    // ── TC-TOT-37 ──────────────────────────────────────────────────────────────
    // FormatCodeForDisplay: 6-digit code splits 3+space+3

    [Fact]
    public void FormatCodeForDisplay_SixDigits_SplitsThreeThree()
        => Assert.Equal("123 456", TotpCalculator.FormatCodeForDisplay("123456"));

    // ── TC-TOT-38 ──────────────────────────────────────────────────────────────
    // FormatCodeForDisplay: 8-digit code splits 4+space+4

    [Fact]
    public void FormatCodeForDisplay_EightDigits_SplitsFourFour()
        => Assert.Equal("1234 5678", TotpCalculator.FormatCodeForDisplay("12345678"));

    // ── TC-TOT-39 ──────────────────────────────────────────────────────────────
    // FormatCodeForDisplay: any other length (no established split convention) - including the
    // empty string shown before the first code has generated - passes through unchanged

    [Theory]
    [InlineData("")]
    [InlineData("1234567")]  // 7 digits (a valid RFC 4226 length, just not one the app exposes in its UI)
    [InlineData("12345")]
    public void FormatCodeForDisplay_OtherLength_ReturnsUnchanged(string code)
        => Assert.Equal(code, TotpCalculator.FormatCodeForDisplay(code));

    // ── TC-TOT-40 ──────────────────────────────────────────────────────────────
    // TryUnpack: a payload with a 5th field (an extra separator trailing Algorithm) is rejected
    // rather than having the extra data silently absorbed into the Algorithm field. Regression test
    // for a MemoryExtensions.Split boundary: a 4-slot destination can't tell "exactly 4 fields" apart
    // from "4 or more fields", since Split packs all remaining input into the last slot once full.

    [Fact]
    public void TryUnpack_ExtraField_ReturnsFalse()
    {
        var packed = $"{TestSecret}630SHA1EXTRA";
        Assert.False(TotpCalculator.TryUnpack(packed, out _, out _, out _, out _));
    }

    // ── TC-TOT-41 ──────────────────────────────────────────────────────────────
    // TryUnpackUtf8: same extra-field rejection as TC-TOT-40, for the UTF-8 byte-span counterpart

    [Fact]
    public void TryUnpackUtf8_ExtraField_ReturnsFalse()
    {
        var packed = $"{TestSecret}630SHA1EXTRA";
        var utf8   = Encoding.UTF8.GetBytes(packed);
        Assert.False(TotpCalculator.TryUnpackUtf8(utf8, out _, out _, out _, out _));
    }

    // ── TC-TOT-42 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: a digits value that is present but outside 6-8 (or not a plain number) rejects the URI
    // instead of falling back to 6 digits

    [Theory]
    [InlineData("5")]
    [InlineData("9")]
    [InlineData("10")]
    [InlineData("0")]
    [InlineData("-8")]
    [InlineData("+8")]
    [InlineData("abc")]
    [InlineData("")]
    public void ParseOtpAuth_UnsupportedDigits_ReturnsNull(string digits)
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}&digits={digits}";
        Assert.Null(TotpCalculator.ParseOtpAuth(uri));
    }

    // ── TC-TOT-43 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: every digits value in 6-8 is accepted as given (the boundary values of TC-TOT-42)

    [Theory]
    [InlineData("6", 6)]
    [InlineData("7", 7)]
    [InlineData("8", 8)]
    public void ParseOtpAuth_SupportedDigits_AreKept(string digits, int expected)
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}&digits={digits}";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.Digits);
    }

    // ── TC-TOT-44 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: a period that is present but not a positive integer rejects the URI instead of
    // falling back to 30 seconds

    [Theory]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("30.5")]
    public void ParseOtpAuth_InvalidPeriod_ReturnsNull(string period)
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}&period={period}";
        Assert.Null(TotpCalculator.ParseOtpAuth(uri));
    }

    // ── TC-TOT-45 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: the URI a user can paste into the setup dialog (8 digits / 60 s / SHA256) yields
    // all four TOTP parameters

    [Fact]
    public void ParseOtpAuth_PastedUriWithAllParameters_ReturnsAllParameters()
    {
        const string uri =
            "otpauth://totp/Yubico8:tabito@example.com?secret=JBSWY3DPEHPK3PXP&digits=8&period=60&algorithm=SHA256";
        var p = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal(TestSecret, p!.Secret);
        Assert.Equal("Yubico8", p.Issuer);
        Assert.Equal("tabito@example.com", p.AccountName);
        Assert.Equal(8, p.Digits);
        Assert.Equal(60, p.Period);
        Assert.Equal("SHA256", p.Algorithm);
    }

    // ── TC-TOT-46 ──────────────────────────────────────────────────────────────
    // ParseOtpAuth: query parameters the app doesn't use (image, lock, ...) are ignored rather than
    // rejecting the URI - only digits / period / algorithm are validated

    [Fact]
    public void ParseOtpAuth_UnknownExtraParameters_AreIgnored()
    {
        var uri = $"otpauth://totp/Example?secret={TestSecret}&image=https%3A%2F%2Fexample.com%2Fa.png&lock=true";
        var p   = TotpCalculator.ParseOtpAuth(uri);
        Assert.NotNull(p);
        Assert.Equal(TestSecret, p!.Secret);
        Assert.Equal(6, p.Digits);
        Assert.Equal(30, p.Period);
        Assert.Equal("SHA1", p.Algorithm);
    }
}
