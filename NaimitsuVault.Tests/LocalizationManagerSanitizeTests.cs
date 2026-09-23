// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Localization;

namespace NaimitsuVault.Tests;

/// <summary>
/// Tests for the control-character sanitization performed by LocalizationManager.Initialize().
/// LineBreaks:    \n and \r (including \r\n pairs) → preserved as \n (intentional line breaks)
/// WhitespaceRun: a run of \t \f \v → collapses to a single space
/// ControlChars:  NUL, non-whitespace C0, DEL, C1 (U+0080-U+009F) → stripped
///
/// LocalizationManager holds static state, so tests are serialized via the SequentialLocale
/// collection.
/// </summary>
[Collection("SequentialLocale")]
public sealed class LocalizationManagerSanitizeTests
{
    // Test-only key (a 6-character A-Z/0-9 key that doesn't collide with real locale keys)
    private const string K = "TSTTST";

    private static string Round(string value)
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string> { [K] = value }, []);
        return LocalizationManager.Get(K);
    }

    // ── Line breaks are preserved (not collapsed) ───────────────────────────────

    [Theory]
    [InlineData("\r\n",     "\n")]
    [InlineData("\n",       "\n")]
    [InlineData("\r",       "\n")]
    [InlineData("\n\n\n",   "\n\n\n")]
    [InlineData("\r\n\r\n", "\n\n")]
    [InlineData("a\r\nb",   "a\nb")]
    [InlineData("a\n\n\nb", "a\n\n\nb")]
    public void Initialize_LineBreak_IsPreserved(string input, string expected)
        => Assert.Equal(expected, Round(input));

    // ── Collapsing tabs / form feed / vertical tab ──────────────────────────────

    [Theory]
    [InlineData("\t",     " ")]
    [InlineData("\t\t\t", " ")]
    [InlineData("a\tb",   "a b")]
    [InlineData("\v",     " ")]
    [InlineData("\f",     " ")]
    public void Initialize_WhitespaceRun_CollapsesToSingleSpace(string input, string expected)
        => Assert.Equal(expected, Round(input));

    [Fact]
    public void Initialize_TabsThenLineBreak_LineBreakSupersedesPendingSpace()
        // \v \f collapse toward a pending space, but the \r\n that follows supersedes it,
        // then the trailing \t adds its own pending space at the very end.
        => Assert.Equal("\n ", Round("\v\f\r\n\t"));

    // ── Stripping control characters ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("\x00")]  // NUL
    [InlineData("\x01")]  // SOH
    [InlineData("\x07")]  // BEL
    [InlineData("\x08")]  // BS
    [InlineData("\x0E")]  // SO  (\x09=\t, \x0A=\n, \x0B=\v, \x0C=\f, \x0D=\r are treated as whitespace)
    [InlineData("\x1F")]  // US
    [InlineData("\x7F")]  // DEL
    [InlineData("\x80")]  // C1 start
    [InlineData("\x9F")]  // C1 end
    public void Initialize_ControlChar_IsStripped(string input)
        => Assert.Equal("", Round(input));

    // C#'s \x escape greedily consumes following hex digits (\x00b→U+000B, \x7Fb→U+07FB,
    // \x1Fb→U+01FB). When a control character is adjacent to a hex digit, use \0 or
    // \u (fixed 4 digits) instead.

    [Fact]
    public void Initialize_NulInMiddle_IsStripped()
        => Assert.Equal("ab", Round("a\0b"));

    [Fact]
    public void Initialize_MultipleControlCharsInSequence_AllStripped()
        => Assert.Equal("ab", Round("a\x01\x02\x7F" + "b"));

    [Fact]
    public void Initialize_MixOfControlCharsAndNewlines_StrippedAndCollapsed()
        => Assert.Equal("a\nb", Round("a\0\r\n\x1F" + "b"));

    // ── Placeholder preservation ────────────────────────────────────────────────

    [Fact]
    public void Initialize_SinglePlaceholder_IsPreserved()
        => Assert.Equal("count: {0}", Round("count: {0}"));

    [Fact]
    public void Initialize_MultiplePlaceholders_AllPreserved()
        => Assert.Equal("{0} of {1}", Round("{0} of {1}"));

    // ── Normal strings are unchanged ──────────────────────────────────────────────

    [Fact]
    public void Initialize_NormalAscii_Unchanged()
        => Assert.Equal("Hello World", Round("Hello World"));

    [Fact]
    public void Initialize_Japanese_Unchanged()
        => Assert.Equal("了解しました", Round("了解しました"));

    // ── The fallback dictionary is also sanitized ──────────────────────────────────────

    [Fact]
    public void Initialize_FallbackWithControlChar_IsStripped()
    {
        LocalizationManager.Initialize(
            [], new Dictionary<string, string> { [K] = "hello\x00world" });
        Assert.Equal("helloworld", LocalizationManager.Get(K));
    }

    [Fact]
    public void Initialize_FallbackWithNewline_IsPreserved()
    {
        LocalizationManager.Initialize(
            [], new Dictionary<string, string> { [K] = "line1\r\nline2" });
        Assert.Equal("line1\nline2", LocalizationManager.Get(K));
    }

    // ── GetById also returns the sanitized value ────────────────────────────────────────

    [Fact]
    public void Initialize_GetById_ReturnsSanitizedValue()
    {
        LocalizationManager.Initialize(
            new Dictionary<string, string> { [K] = "ok\r\n" }, []);
        // Serial numbers start at 1, so a real key's code is never 0
        // (0 is reserved exclusively as GetCode's "not found" sentinel).
        int code = LocalizationManager.GetCode(K);
        Assert.NotEqual(0, code);
        Assert.Equal("ok\n", LocalizationManager.GetById(code));
    }
}
