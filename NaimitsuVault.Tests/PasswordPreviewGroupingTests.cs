// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Grouping decision for the enlarged password preview" TC-PPG-01–04.
/// </summary>
public sealed class PasswordPreviewGroupingTests
{
    // TC-PPG-01: values without any structure of their own are grouped
    [Theory]
    [InlineData("ANA!Mile2024xJ")]
    [InlineData("abcdefgh12345678")]
    [InlineData("1234567890")]
    [InlineData("")]
    public void ShouldGroup_PlainPassword_ReturnsTrue(string value)
        => Assert.True(PasswordPreviewGrouping.ShouldGroup(value));

    // TC-PPG-02: any whitespace (including a full-width space) turns grouping off
    [Theory]
    [InlineData("1P@ss!Ol d20 24x")]
    [InlineData("correct horse battery staple")]
    [InlineData("abc　def")]
    [InlineData("abc\tdef")]
    public void ShouldGroup_ContainsWhitespace_ReturnsFalse(string value)
        => Assert.False(PasswordPreviewGrouping.ShouldGroup(value));

    // TC-PPG-03: letters/digits separated by one kind of symbol used two or more times is a key
    // format or a symbol-joined passphrase
    [Theory]
    [InlineData("A3-XXXXXX-XXXXX-XXXXX-XXXXX-XXXXX")]
    [InlineData("AAAA-BBBB-CCCC")]
    [InlineData("12-34-56")]
    [InlineData("correct_horse_battery_staple")]
    [InlineData("tokyo.summer.2026")]
    [InlineData("red@green@blue")]
    [InlineData("2026/10/07")]
    public void ShouldGroup_DelimiterStructured_ReturnsFalse(string value)
        => Assert.False(PasswordPreviewGrouping.ShouldGroup(value));

    // TC-PPG-04: symbols that do not form such a structure leave grouping on
    [Theory]
    [InlineData("abcd-efgh")]          // the symbol occurs once: common in random passwords
    [InlineData("tokyo.summer")]       // likewise
    [InlineData("V0y@g3$St0ck!4433")]  // several kinds of symbol, each once
    [InlineData("ab-cd!-ef-gh")]       // a second kind of symbol is present
    [InlineData("ab-cd_ef.gh")]        // mixed symbols
    [InlineData("-abcd-efgh-ijkl")]    // leading symbol
    [InlineData("abcd__efgh_ijkl")]    // doubled symbol
    [InlineData("abcd.efgh.ijkl.")]    // trailing symbol
    public void ShouldGroup_DelimitersWithoutStructure_ReturnsTrue(string value)
        => Assert.True(PasswordPreviewGrouping.ShouldGroup(value));
}
