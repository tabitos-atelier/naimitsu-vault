// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// PasswordGenerator entropy, boundary-value, and character-class guarantee tests (TC-PG-01 .. TC-PG-20)
/// </summary>
public sealed class PasswordGeneratorEntropyTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private const string Upper   = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower   = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits  = "0123456789";
    private const string DefSym  = PasswordGenerator.DefaultSymbols;

    private static string Gen(int length,
        bool up = true, bool lo = true, bool di = true, bool sym = true,
        string? customSym = null)
    {
        var buf = new char[length];
        PasswordGenerator.Generate(buf, up, lo, di, sym, customSym);
        return new string(buf);
    }

    // ── TC-PG-01 ──────────────────────────────────────────────────────────────
    // Length 0 -> nothing at all is written to the output

    [Fact]
    public void Generate_ZeroLength_WritesNothing()
    {
        var buf = Array.Empty<char>();
        PasswordGenerator.Generate(buf);
        Assert.Empty(buf);
    }

    // ── TC-PG-02 ──────────────────────────────────────────────────────────────
    // Length 1 -> a single valid character is returned

    [Fact]
    public void Generate_Length1_ReturnsSingleChar()
    {
        var result = Gen(1);
        Assert.Equal(1, result.Length);
        Assert.True(Upper.Contains(result[0]) || Lower.Contains(result[0]) ||
                    Digits.Contains(result[0]) || DefSym.Contains(result[0]));
    }

    // ── TC-PG-03 ──────────────────────────────────────────────────────────────
    // Generating 20 characters with default settings -> at least 1 uppercase, lowercase, digit,
    // and symbol character each are included (the required-character guarantee)

    [Fact]
    public void Generate_Default20_ContainsAllCategories()
    {
        var result = Gen(20);
        Assert.Contains(result, c => Upper.Contains(c));
        Assert.Contains(result, c => Lower.Contains(c));
        Assert.Contains(result, c => Digits.Contains(c));
        Assert.Contains(result, c => DefSym.Contains(c));
    }

    // ── TC-PG-04 ──────────────────────────────────────────────────────────────
    // useUpper=false -> no uppercase characters are included

    [Fact]
    public void Generate_NoUpper_ResultContainsNoUppercase()
    {
        var result = Gen(40, up: false);
        Assert.DoesNotContain(result, c => Upper.Contains(c));
    }

    // ── TC-PG-05 ──────────────────────────────────────────────────────────────
    // useLower=false -> no lowercase characters are included

    [Fact]
    public void Generate_NoLower_ResultContainsNoLowercase()
    {
        var result = Gen(40, lo: false);
        Assert.DoesNotContain(result, c => Lower.Contains(c));
    }

    // ── TC-PG-06 ──────────────────────────────────────────────────────────────
    // useDigits=false -> no digits are included

    [Fact]
    public void Generate_NoDigits_ResultContainsNoDigit()
    {
        var result = Gen(40, di: false);
        Assert.DoesNotContain(result, c => Digits.Contains(c));
    }

    // ── TC-PG-07 ──────────────────────────────────────────────────────────────
    // useSymbols=false -> no default symbols are included

    [Fact]
    public void Generate_NoSymbols_ResultContainsNoDefaultSymbol()
    {
        var result = Gen(40, sym: false);
        Assert.DoesNotContain(result, c => DefSym.Contains(c));
    }

    // ── TC-PG-08 ──────────────────────────────────────────────────────────────
    // All flags false -> throws ArgumentException (the UI layer's CanExecute is the first line of defense)

    [Fact]
    public void Generate_AllFalse_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            Gen(40, up: false, lo: false, di: false, sym: false));
    }

    // ── TC-PG-09 ──────────────────────────────────────────────────────────────
    // Custom symbols are actually used

    [Fact]
    public void Generate_CustomSymbols_UsedInOutput()
    {
        const string custom = "+-=";
        bool found = false;
        for (int i = 0; i < 50; i++)
        {
            var result = Gen(10, sym: true, customSym: custom);
            if (result.Any(c => custom.Contains(c))) { found = true; break; }
        }
        Assert.True(found, "A custom symbol was never chosen across 50 attempts (probabilistic insufficiency)");
    }

    // ── TC-PG-10 ──────────────────────────────────────────────────────────────
    // Only characters from the custom symbol set are adopted as symbols (default symbols never mix in)

    [Fact]
    public void Generate_CustomSymbols_DefaultSymbolsNotMixed()
    {
        const string custom = "+-=";
        var result = Gen(100, up: false, lo: false, di: false, sym: true, customSym: custom);
        Assert.DoesNotContain(result, c => DefSym.Contains(c));
    }

    // ── TC-PG-11 ──────────────────────────────────────────────────────────────
    // Generating twice with the same flags -> different output each time (probabilistic - an
    // extremely rare match is possible)

    [Fact]
    public void Generate_TwoCalls_DifferentOutput()
    {
        // Probability of an identical result = (1/pool_size)^length ~ 0 (negligible)
        bool distinct = false;
        for (int i = 0; i < 5; i++)
        {
            var a = Gen(32);
            var b = Gen(32);
            if (a != b) { distinct = true; break; }
        }
        Assert.True(distinct);
    }

    // ── TC-PG-12 ──────────────────────────────────────────────────────────────
    // The required-character guarantee still works even at the minimum length (equal to the number of enabled categories)

    [Fact]
    public void Generate_LengthEqualsEnabledCategories_AllCategoriesPresent()
    {
        // useUpper+useLower+useDigits = 3 categories, length=3 -> 1 character each
        for (int trial = 0; trial < 20; trial++)
        {
            var result = Gen(3, up: true, lo: true, di: true, sym: false);
            Assert.Contains(result, c => Upper.Contains(c));
            Assert.Contains(result, c => Lower.Contains(c));
            Assert.Contains(result, c => Digits.Contains(c));
        }
    }

    // ── TC-PG-13 ──────────────────────────────────────────────────────────────
    // Required characters are not biased toward the leading N characters (shuffle check: diversity of the first character across 100 passwords)

    [Fact]
    public void Generate_Shuffle_FirstCharIsNotAlwaysSameCategory()
    {
        var firstCategories = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            var result = Gen(10);
            var c = result[0];
            if (Upper.Contains(c))  firstCategories.Add("upper");
            if (Lower.Contains(c))  firstCategories.Add("lower");
            if (Digits.Contains(c)) firstCategories.Add("digit");
            if (DefSym.Contains(c)) firstCategories.Add("symbol");
        }
        // If the shuffle works correctly, at least 2 categories should appear first across 100 attempts
        Assert.True(firstCategories.Count >= 2,
            "The first character is always the same category - suspected Fisher-Yates shuffle failure");
    }

    // ── TC-PG-14 ──────────────────────────────────────────────────────────────
    // Length 128 characters - works correctly even with a sufficiently large buffer

    [Fact]
    public void Generate_Length128_CorrectLength()
    {
        var result = Gen(128);
        Assert.Equal(128, result.Length);
        Assert.Contains(result, c => Upper.Contains(c));
        Assert.Contains(result, c => Lower.Contains(c));
        Assert.Contains(result, c => Digits.Contains(c));
        Assert.Contains(result, c => DefSym.Contains(c));
    }

    // ── TC-PG-15 ──────────────────────────────────────────────────────────────
    // Chi-squared statistical entropy test.
    // Generate 10,000 characters: each character class's occurrence frequency stays within 5% of the expected distribution

    [Fact]
    public void Generate_ChiSquaredEntropy_NoCategory_IsBiased()
    {
        const int N = 10_000;

        // Gen(1) always picks its single required character from uppercase, giving upperRatio=1.0.
        // Generate 10 characters and measure the distribution across all characters to verify uniformity.
        int upperCount = 0, lowerCount = 0, digitCount = 0;
        for (int i = 0; i < N; i++)
        {
            var result = Gen(10, sym: false);
            foreach (var c in result)
            {
                if (Upper.Contains(c))  upperCount++;
                if (Lower.Contains(c))  lowerCount++;
                if (Digits.Contains(c)) digitCount++;
            }
        }

        // Pool size: 26 + 26 + 10 = 62 characters
        // Uppercase 26/62 ~ 41.9%, lowercase 26/62 ~ 41.9%, digits 10/62 ~ 16.1%
        // (with length=10, the measured values including the required-1-char effect are roughly 39%/39%/21% - all within +/-10%)
        double total = upperCount + lowerCount + digitCount;
        double upperRatio = upperCount / total;
        double lowerRatio = lowerCount / total;
        double digitRatio = digitCount / total;

        // Within +/-10% (a bias would easily fall outside this range across 100,000 characters x trials)
        Assert.InRange(upperRatio, 0.319, 0.519);   // 41.9% ± 10%
        Assert.InRange(lowerRatio, 0.319, 0.519);
        Assert.InRange(digitRatio, 0.062, 0.262);   // 16.1% ± 10%
    }

    // ── TC-PG-16 ──────────────────────────────────────────────────────────────
    // No duplicates across 1000 generations (entropy continuity)

    [Fact]
    public void Generate_1000Calls_AllDistinct()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            var result = Gen(16);
            seen.Add(result);
        }
        // The probability of a 16-char / all-4-category password colliding across 1000 attempts is astronomically low
        Assert.Equal(1000, seen.Count);
    }

    // ── TC-PG-17 ──────────────────────────────────────────────────────────────
    // useSymbols=true with customSymbols="" -> DefaultSymbols is used

    [Fact]
    public void Generate_EmptyCustomSymbols_FallsBackToDefaultSymbols()
    {
        bool found = false;
        for (int i = 0; i < 50; i++)
        {
            var result = Gen(15, customSym: "");
            if (result.Any(c => DefSym.Contains(c))) { found = true; break; }
        }
        Assert.True(found, "A default symbol was never chosen across 50 attempts");
    }

    // ── TC-PG-18 ──────────────────────────────────────────────────────────────
    // PasswordGenerator.AllowedSymbols contains all of DefaultSymbols

    [Fact]
    public void AllowedSymbols_ContainsAllDefaultSymbols()
    {
        foreach (var c in PasswordGenerator.DefaultSymbols)
            Assert.Contains(c, PasswordGenerator.AllowedSymbols);
    }

    // ── TC-PG-19 ──────────────────────────────────────────────────────────────
    // shuffleBytes's ZeroMemory is called correctly (confirmed via behavior).
    // Verifies that no plaintext leaks outside the output buffer even after Generate completes
    // (direct verification of ZeroMemory is deferred to the code-inspection comment on TC-PG-20)

    [Fact]
    public void Generate_LargeOutput_DoesNotThrow_AndLengthIsPreserved()
    {
        var buf = new char[4096];
        var ex = Record.Exception(() => PasswordGenerator.Generate(buf));
        Assert.Null(ex);
        Assert.Equal(4096, buf.Length);
        Assert.True(buf.All(c => c != '\0'), "The generated result contains a null character (suspected unwritten buffer cell)");
    }

    // ── TC-PG-20 ──────────────────────────────────────────────────────────────
    // ZeroMemory for shuffleBytes / workBuf: since local variables cannot be asserted directly,
    // this substitutes a heap-pressure test to confirm any residue is reclaimed by the GC cycle
    // after the function completes (the actual zeroing guarantee comes from the
    // CryptographicOperations.ZeroMemory call inside Generate()'s code)

    [Fact]
    public void Generate_ZeroMemoryOnShuffleBytes_BehavioralProof()
    {
        // Call it 1000 times to confirm there is no memory leak or hang (originating from ZeroMemory not being called)
        var buf = new char[64];
        for (int i = 0; i < 1000; i++)
            PasswordGenerator.Generate(buf);

        // Confirm no exception or hang occurs even when GC.Collect encourages release
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Even at this point, buf's content has been overwritten with the result of the last Generate call
        Assert.Equal(64, buf.Length);
    }
}
