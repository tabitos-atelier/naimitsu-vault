// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Helpers;

/// <summary>
/// Decides whether the enlarged password preview should insert a gap after every
/// <see cref="GroupSize"/> characters. The gaps help when a password has to be typed by hand, but only
/// for a value that has no structure of its own; on a value that is already divided they cut across
/// the existing divisions and make it harder to read.
/// </summary>
internal static class PasswordPreviewGrouping
{
    internal const int GroupSize = 4;

    // "A3-XXXXXX-XXXXX" style keys and "correct_horse_battery" / "tokyo.summer.2026" style
    // passphrases: the same symbol used at least this many times counts as a separator. Once is not
    // enough - a random password often contains a given symbol once.
    private const int MinSeparatorCount = 2;

    /// <summary>
    /// Returns false for a value that already carries its own separators:
    /// one that contains any whitespace (typically a passphrase), or one whose only symbol is a single
    /// kind of character that occurs two or more times between non-empty runs of letters/digits
    /// (license/secret key format, or a passphrase whose words are joined by that symbol).
    /// A value that mixes different symbols is an ordinary password and is grouped.
    /// </summary>
    internal static bool ShouldGroup(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
            if (char.IsWhiteSpace(c)) return false;

        return !IsSeparatedBySingleSymbol(value);
    }

    private static bool IsSeparatedBySingleSymbol(ReadOnlySpan<char> value)
    {
        int separatorCount = 0;
        int partLength = 0;
        char separator = default;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                partLength++;
                continue;
            }
            if (partLength == 0) return false;                         // leading or doubled symbol
            if (separatorCount > 0 && c != separator) return false;    // a second kind of symbol
            separator = c;
            separatorCount++;
            partLength = 0;
        }
        return partLength > 0 && separatorCount >= MinSeparatorCount;
    }
}
