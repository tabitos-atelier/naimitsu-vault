// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Helpers;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Password generation" - all 8 cases for PasswordGenerator.
/// </summary>
public sealed class PasswordGeneratorTests
{
    private const string Upper   = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower   = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits  = "0123456789";

    // ── TC-PWG-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_MinLength8_AllClassesEnabled_Returns8Chars()
    {
        // Arrange
        Span<char> buf = new char[8];

        // Act
        PasswordGenerator.Generate(buf, true, true, true, true);

        // Assert - 8 non-null characters exist
        Assert.Equal(8, buf.Length);
        Assert.True(buf.ToArray().All(c => c != '\0'));
    }

    // ── TC-PWG-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_MinLength8_AllClassesEnabled_ContainsEachClass()
    {
        // Arrange - 8 characters, all classes ON: the minimum length able to satisfy all 4 required character classes
        Span<char> buf = new char[8];

        // Act
        PasswordGenerator.Generate(buf, true, true, true, true);
        var chars = buf.ToArray();

        // Assert - each class is represented by at least 1 character
        Assert.True(chars.Any(c => Upper.Contains(c)),  "Upper class missing");
        Assert.True(chars.Any(c => Lower.Contains(c)),  "Lower class missing");
        Assert.True(chars.Any(c => Digits.Contains(c)), "Digit class missing");
        Assert.True(chars.Any(c => PasswordGenerator.DefaultSymbols.Contains(c)), "Symbol class missing");
    }

    // ── TC-PWG-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_MaxLength64_Returns64Chars()
    {
        // Arrange
        Span<char> buf = new char[64];

        // Act
        PasswordGenerator.Generate(buf, true, true, true, true);

        // Assert
        Assert.Equal(64, buf.Length);
        Assert.True(buf.ToArray().All(c => c != '\0'));
    }

    // ── TC-PWG-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_DigitsOnly_AllCharsAreNumeric()
    {
        // Arrange
        Span<char> buf = new char[8];

        // Act
        PasswordGenerator.Generate(buf, useUpper: false, useLower: false, useDigits: true, useSymbols: false);

        // Assert
        Assert.True(buf.ToArray().All(char.IsDigit));
    }

    // ── TC-PWG-05 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// ArgumentException is thrown when every character class is disabled.
    /// The UI layer's CanExecute guard is the first line of defense; this throw is the second.
    /// </summary>
    [Fact]
    public void Generate_AllClassesOff_ThrowsArgumentException()
    {
        var arr = new char[8];
        Assert.Throws<ArgumentException>(() =>
            PasswordGenerator.Generate(arr, useUpper: false, useLower: false,
                                            useDigits: false, useSymbols: false));
    }

    // ── TC-PWG-06 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_1000Iterations_NoDuplicateOutputs()
    {
        // Arrange - all classes ON, length 12 (enough class diversity to make the birthday problem negligible)
        var seen = new HashSet<string>(1000);

        // Act & Assert
        for (int i = 0; i < 1000; i++)
        {
            Span<char> buf = new char[12];
            PasswordGenerator.Generate(buf, true, true, true, true);
            var str = new string(buf);
            Assert.True(seen.Add(str), $"Duplicate password generated at iteration {i}: '{str}'");
        }
    }

    // ── TC-PWG-07 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A generated password is always one that the enlarged preview divides into groups of four.
    /// Without regeneration, a value whose only symbol is one kind of character used two or more
    /// times between letters/digits is shown undivided. A single custom symbol makes that shape
    /// frequent, so the missing regeneration would fail this test almost certainly.
    /// </summary>
    [Theory]
    [InlineData(null)]   // default symbol set
    [InlineData("-")]    // only one kind of symbol can occur
    [InlineData("-_")]
    public void Generate_ManyIterations_ResultIsAlwaysGroupedInPreview(string? customSymbols)
    {
        var buf = new char[20];
        for (int i = 0; i < 2000; i++)
        {
            PasswordGenerator.Generate(buf, true, true, true, true, customSymbols);
            Assert.True(PasswordPreviewGrouping.ShouldGroup(buf),
                $"Generated a password that the preview does not group at iteration {i}: '{new string(buf)}'");
            Assert.Contains(buf, c => !char.IsLetterOrDigit(c));
        }
    }

    // ── TC-PWG-08 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regeneration is bounded: a symbol set that can never produce a grouped value (whitespace
    /// only, which the UI filters out before it reaches the generator) still returns a full-length
    /// value instead of looping forever.
    /// </summary>
    [Fact]
    public void Generate_SymbolSetThatIsNeverGrouped_ReturnsWithoutHanging()
    {
        var buf = new char[12];

        PasswordGenerator.Generate(buf, true, true, true, true, " ");

        Assert.DoesNotContain('\0', buf);
        Assert.Contains(' ', buf);
    }
}
