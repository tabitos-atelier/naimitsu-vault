// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// "TOTP boundary values" - all 9 cases for TotpCalculator.
/// </summary>
public sealed class TotpCalculatorTests
{
    private const string KnownSecret = "JBSWY3DPEHPK3PXP";

    // ── TC-TPC-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_ValidBase32Uppercase_Returns6DigitStringCode()
    {
        // Act
        var (code, rem) = TotpCalculator.Generate(KnownSecret);

        // Assert
        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsDigit), $"Non-digit in code: '{code}'");
        Assert.InRange(rem, 1, 30);
    }

    // ── TC-TPC-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_LowercaseSecret_NormalizesToUpperAndSucceeds()
    {
        // Act - use GenerateAtOffset(0) to explicitly pin the same step and avoid the 30-second boundary issue
        var codeUpper = TotpCalculator.GenerateAtOffset(KnownSecret, 0);
        var codeLower = TotpCalculator.GenerateAtOffset(KnownSecret.ToLowerInvariant(), 0);

        // Assert - both uppercase and lowercase produce the same code
        Assert.Equal(codeUpper, codeLower);
    }

    // ── TC-TPC-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_SecretWithSpaces_StripsSpacesAndSucceeds()
    {
        // Arrange - contains spaces (Base32Decode skips spaces)
        const string secretWithSpaces = "JBSW Y3DP EHP K3P XP";

        // Act & Assert - no exception, a 6-digit code is returned
        var (code, _) = TotpCalculator.Generate(secretWithSpaces);
        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsDigit));
    }

    // ── TC-TPC-04 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// FormatException is thrown when the input contains characters outside the Base32 alphabet (e.g. '0','1','8','9').
    /// </summary>
    [Fact]
    public void Base32Decode_InvalidBase32DigitChars_ThrowsFormatException()
    {
        // '1' is not in Base32 (alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")
        Assert.Throws<FormatException>(() => TotpCalculator.Base32Decode("JBSWY3DP1234"));
    }

    // ── TC-TPC-05 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// ArgumentException is thrown when an empty secret is passed.
    /// </summary>
    [Fact]
    public void Generate_EmptySecret_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => TotpCalculator.Generate(string.Empty));
        Assert.Throws<ArgumentException>(() => TotpCalculator.Generate("   ")); // whitespace-only is also rejected
    }

    // ── TC-TPC-06 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_ConsecutiveCalls_SameStep_ReturnIdenticalCode()
    {
        // Act - calling with the same offset 0 guarantees the same step
        var code1 = TotpCalculator.GenerateAtOffset(KnownSecret, 0);
        var code2 = TotpCalculator.GenerateAtOffset(KnownSecret, 0);

        // Assert
        Assert.Equal(code1, code2);
    }

    // ── TC-TPC-07 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_CurrentAndNextStep_ReturnDifferentCodes()
    {
        // Act
        var code0 = TotpCalculator.GenerateAtOffset(KnownSecret, 0);
        var code1 = TotpCalculator.GenerateAtOffset(KnownSecret, +1);

        // Assert - adjacent steps produce different codes (per RFC 6238)
        Assert.NotEqual(code0, code1);
    }

    // ── TC-TPC-08 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseOtpAuth_ValidUri_ExtractsSecretAndIssuer()
    {
        // Arrange
        const string uri =
            "otpauth://totp/Example:alice@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example";

        // Act
        var p = TotpCalculator.ParseOtpAuth(uri);

        // Assert
        Assert.NotNull(p);
        Assert.Equal("JBSWY3DPEHPK3PXP",    p!.Secret);
        Assert.Equal("Example",              p.Issuer);
        Assert.Equal("alice@example.com",    p.AccountName);
        Assert.Equal(6,  p.Digits);
        Assert.Equal(30, p.Period);
    }

    // ── TC-TPC-09 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseOtpAuth_NullOrInvalidUri_ReturnsNull()
    {
        // Assert - a URI that isn't otpauth:// is null
        Assert.Null(TotpCalculator.ParseOtpAuth("https://not-an-otp-uri"));
        // Assert - null when the scheme is correct but it isn't totp
        Assert.Null(TotpCalculator.ParseOtpAuth("otpauth://hotp/Example?secret=ABC"));
        // Assert - null when there is no secret key
        Assert.Null(TotpCalculator.ParseOtpAuth("otpauth://totp/Example?issuer=Ex"));
    }
}
