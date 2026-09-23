// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NaimitsuVault.Helpers;

/// <summary>RFC 6238 TOTP calculation / otpauth:// URI parser. No external library needed.</summary>
public static class TotpCalculator
{
    /// <summary>Default HMAC algorithm per RFC 6238.</summary>
    public const string DefaultAlgorithm = "SHA1";

    // Parameters extracted from an otpauth:// URI
    public record OtpAuthParams(
        string Secret,
        string Issuer,
        string AccountName,
        int Digits,
        int Period,
        string Algorithm);

    /// <summary>Full TOTP configuration persisted alongside the secret (Secret.TotpSecret payload).</summary>
    public sealed record TotpConfig(string Secret, int Digits = 6, int Period = 30, string Algorithm = DefaultAlgorithm);

    /// <summary>
    /// Separator used to pack a TotpConfig into Secret.TotpSecret's plaintext payload as
    /// "{Secret}{sep}{Digits}{sep}{Period}{sep}{Algorithm}". A non-printable control character
    /// so it can never collide with a Base32 secret or an algorithm name.
    /// </summary>
    public const char PackedFieldSeparator = '\u0001';

    /// <summary>Packs a TotpConfig into Secret.TotpSecret's plaintext payload.</summary>
    public static string Pack(TotpConfig config)
        => $"{config.Secret}{PackedFieldSeparator}{config.Digits}{PackedFieldSeparator}{config.Period}{PackedFieldSeparator}{config.Algorithm}";

    /// <summary>
    /// Splits a packed payload into (SecretSpan, Digits, Period, Algorithm) without allocating a
    /// string for the secret - callers that only need to compute a code can pass the span straight
    /// into Generate/GenerateAtOffset. Returns false for an empty or malformed payload.
    /// </summary>
    public static bool TryUnpack(ReadOnlySpan<char> packed, out ReadOnlySpan<char> secret, out int digits, out int period, out string algorithm)
    {
        secret = default;
        digits = 6;
        period = 30;
        algorithm = DefaultAlgorithm;
        if (packed.IsEmpty) return false;

        // Destination capacity is intentionally 5, not 4: MemoryExtensions.Split stops filling once the
        // destination is full and packs everything remaining (including further separators) into the
        // last slot, so a 4-slot destination can't distinguish "exactly 4 fields" from "4+ fields" -
        // a 5th or later field would silently get absorbed into parts[3] (Algorithm) undetected.
        Span<Range> parts = stackalloc Range[5];
        int count = packed.Split(parts, PackedFieldSeparator);
        if (count != 4) return false;

        secret = packed[parts[0]];
        if (secret.IsEmpty) return false;
        if (!int.TryParse(packed[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out digits)) return false;
        if (!int.TryParse(packed[parts[2]], NumberStyles.None, CultureInfo.InvariantCulture, out period)) return false;
        algorithm = new string(packed[parts[3]]);
        return true;
    }

    /// <summary>
    /// Same as <see cref="TryUnpack(ReadOnlySpan{char}, out ReadOnlySpan{char}, out int, out int, out string)"/>
    /// but materializes the secret into a TotpConfig (for UI display where a string is already required).
    /// </summary>
    public static TotpConfig? TryUnpackToConfig(ReadOnlySpan<char> packed)
        => TryUnpack(packed, out var secret, out var digits, out var period, out var algorithm)
            ? new TotpConfig(new string(secret), digits, period, algorithm)
            : null;

    /// <summary>
    /// UTF-8 byte-span counterpart of <see cref="TryUnpack"/>, for callers (import/export) that already
    /// hold the decrypted payload as UTF-8 bytes and want to avoid a char-array round trip.
    /// </summary>
    public static bool TryUnpackUtf8(ReadOnlySpan<byte> packed, out ReadOnlySpan<byte> secret, out int digits, out int period, out string algorithm)
    {
        secret = default;
        digits = 6;
        period = 30;
        algorithm = DefaultAlgorithm;
        if (packed.IsEmpty) return false;

        // ReadOnlySpan<byte> has no Split extension (unlike ReadOnlySpan<char>), so this walks the
        // three separators by hand instead.
        byte sep = (byte)PackedFieldSeparator;
        int i1 = packed.IndexOf(sep);
        if (i1 < 0) return false;
        var rest1 = packed[(i1 + 1)..];
        int i2 = rest1.IndexOf(sep);
        if (i2 < 0) return false;
        var rest2 = rest1[(i2 + 1)..];
        int i3 = rest2.IndexOf(sep);
        if (i3 < 0) return false;
        // A 5th field (a further separator trailing the algorithm name) means the payload has more
        // fields than expected - reject it rather than silently absorbing the extra data into algorithm.
        var rest3 = rest2[(i3 + 1)..];
        if (rest3.IndexOf(sep) >= 0) return false;

        secret = packed[..i1];
        if (secret.IsEmpty) return false;
        if (!Utf8Parser.TryParse(rest1[..i2], out digits, out _)) return false;
        if (!Utf8Parser.TryParse(rest2[..i3], out period, out _)) return false;
        algorithm = Encoding.UTF8.GetString(rest3);
        return true;
    }

    /// <summary>
    /// Returns the current time's TOTP code and the number of seconds remaining until this step ends.
    /// </summary>
    /// <param name="base32Secret">A Base32-encoded secret (either case is accepted).</param>
    public static (string Code, int SecondsRemaining) Generate(string base32Secret, int digits = 6, int period = 30, string algorithm = DefaultAlgorithm)
        => Generate(base32Secret.AsSpan(), digits, period, algorithm);

    /// <summary>Span overload that never generates a string. Can be called directly from SecureCharBuffer.Span.</summary>
    public static (string Code, int SecondsRemaining) Generate(ReadOnlySpan<char> base32Secret, int digits = 6, int period = 30, string algorithm = DefaultAlgorithm)
    {
        if (base32Secret.Trim().IsEmpty)
            throw new ArgumentException("The TOTP secret is empty.", nameof(base32Secret));
        ValidatePeriodAndDigits(period, digits);
        var key   = Base32Decode(base32Secret);
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var step  = epoch / period;
        var rem   = (int)(period - epoch % period);
        try
        {
            var code = ComputeHotp(key, step, digits, algorithm);
            return (code, rem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.AsSpan());
        }
    }

    /// <summary>
    /// Returns the TOTP code shifted by stepOffset from the current step (for checking PC clock drift).
    /// stepOffset = -1: previous step, +1: next step.
    /// </summary>
    public static string GenerateAtOffset(string base32Secret, int stepOffset, int digits = 6, int period = 30, string algorithm = DefaultAlgorithm)
        => GenerateAtOffset(base32Secret.AsSpan(), stepOffset, digits, period, algorithm);

    /// <summary>Span overload that never generates a string.</summary>
    public static string GenerateAtOffset(ReadOnlySpan<char> base32Secret, int stepOffset, int digits = 6, int period = 30, string algorithm = DefaultAlgorithm)
    {
        if (base32Secret.Trim().IsEmpty)
            throw new ArgumentException("The TOTP secret is empty.", nameof(base32Secret));
        ValidatePeriodAndDigits(period, digits);
        var key  = Base32Decode(base32Secret);
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / period + stepOffset;
        try
        {
            return ComputeHotp(key, step, digits, algorithm);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.AsSpan());
        }
    }

    /// <summary>
    /// Groups a TOTP code for display only - never for clipboard copy or comparison. Some 2FA input
    /// fields don't tolerate the inserted space, so callers must keep using the raw code (from
    /// Generate/GenerateAtOffset) for anything but the on-screen label.
    /// 6 digits -> "123 456", 8 digits -> "1234 5678". Any other length (a 7-digit code, or an
    /// empty/loading placeholder) is returned unchanged - there's no established display convention
    /// for a 7-digit split.
    /// </summary>
    public static string FormatCodeForDisplay(string code) => code.Length switch
    {
        6 => $"{code[..3]} {code[3..]}",
        8 => $"{code[..4]} {code[4..]}",
        _ => code,
    };

    /// <summary>
    /// Parses an otpauth://totp/... URI and returns OtpAuthParams. null on failure: not an otpauth://totp/ URI,
    /// no secret, or a digits / period / algorithm parameter that is present but unsupported
    /// (digits outside 6-8, period not a positive integer, algorithm other than SHA1 / SHA256 / SHA512).
    /// </summary>
    public static OtpAuthParams? ParseOtpAuth(string uri)
    {
        // otpauth://totp/<label>?<query>
        // label may be URL-encoded. Format is issuer:accountname
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase))
            return null;

        var withoutScheme = uri["otpauth://totp/".Length..];
        var qIdx = withoutScheme.IndexOf('?');
        var rawLabel = qIdx >= 0 ? withoutScheme[..qIdx] : withoutScheme;
        var rawQuery = qIdx >= 0 ? withoutScheme[(qIdx + 1)..] : string.Empty;

        var label   = Uri.UnescapeDataString(rawLabel);
        var query   = ParseQuery(rawQuery);

        if (!query.TryGetValue("secret", out var secret) || string.IsNullOrEmpty(secret))
            return null;

        query.TryGetValue("issuer", out var issuer);

        // An absent digits / period / algorithm parameter takes the RFC 6238 default, but a parameter that is
        // present and unsupported rejects the whole URI. Falling back to a default here would let the user
        // enroll successfully while every code the app generates differs from the service's.
        int digits = 6;
        int period = 30;
        // digits is restricted to the 6-8 range defined by RFC 4226, which also prevents an int overflow in
        // Math.Pow(10, digits). NumberStyles.None rejects a sign or whitespace inside the value.
        if (query.TryGetValue("digits", out var dStr))
        {
            if (!int.TryParse(dStr, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 6 or > 8)
                return null;
            digits = d;
        }
        if (query.TryGetValue("period", out var pStr))
        {
            if (!int.TryParse(pStr, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p <= 0)
                return null;
            period = p;
        }
        string algorithm = DefaultAlgorithm;
        if (query.TryGetValue("algorithm", out var aStr))
        {
            var normalized = aStr.Trim().ToUpperInvariant();
            if (normalized is not ("SHA1" or "SHA256" or "SHA512"))
                return null;
            algorithm = normalized;
        }

        // label: "issuer:account" or "account"
        string accountName = label;
        string resolvedIssuer = issuer ?? string.Empty;
        var colonIdx = label.IndexOf(':');
        if (colonIdx >= 0)
        {
            if (string.IsNullOrEmpty(resolvedIssuer))
                resolvedIssuer = label[..colonIdx].Trim();
            accountName = label[(colonIdx + 1)..].Trim();
        }

        return new OtpAuthParams(secret.ToUpperInvariant(), resolvedIssuer, accountName, digits, period, algorithm);
    }

    // ─── private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Guards Generate/GenerateAtOffset against a period/digits pair that didn't go through
    /// ParseOtpAuth's validation - e.g. TotpConfig unpacked from a corrupted or maliciously crafted
    /// Secret.TotpSecret payload (TryUnpack/TryUnpackUtf8 don't range-check these fields). period &lt;= 0
    /// would otherwise divide by zero below, and digits outside 6-8 would overflow the int cast in
    /// Math.Pow(10, digits) used by ComputeHotp.
    /// </summary>
    private static void ValidatePeriodAndDigits(int period, int digits)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be greater than zero.");
        if (digits is < 6 or > 8)
            throw new ArgumentOutOfRangeException(nameof(digits), "Digits must be between 6 and 8.");
    }

    private static string ComputeHotp(byte[] key, long counter, int digits, string algorithm = DefaultAlgorithm)
    {
        // RFC 4226: HMAC(key, counter as 8-byte big-endian). RFC 6238 extends this to SHA256/SHA512.
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        byte[] hmac = algorithm switch
        {
            "SHA256" => HMACSHA256.HashData(key, counterBytes),
            "SHA512" => HMACSHA512.HashData(key, counterBytes),
            _        => HMACSHA1.HashData(key, counterBytes),
        };

        // Dynamic Truncation
        int offset = hmac[^1] & 0x0F;
        int code   = ((hmac[offset]     & 0x7F) << 24)
                   | ((hmac[offset + 1] & 0xFF) << 16)
                   | ((hmac[offset + 2] & 0xFF) << 8)
                   |  (hmac[offset + 3] & 0xFF);

        var mod = (int)Math.Pow(10, digits);
        return (code % mod).ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// RFC 4648 Base32 decode.
    /// The return value must be erased with <c>CryptographicOperations.ZeroMemory(result.AsSpan())</c> after use.
    /// </summary>
    public static byte[] Base32Decode(string input) => Base32Decode(input.AsSpan());

    /// <summary>
    /// Decodes a Base32 string.
    /// </summary>
    /// <remarks>
    /// Since this returns the raw secret byte sequence, zeroing the return value's memory is the caller's responsibility.
    /// </remarks>
    public static byte[] Base32Decode(ReadOnlySpan<char> input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        // Compute the effective length excluding padding/whitespace and allocate the work buffer
        int effectiveLen = 0;
        foreach (char rawC in input) if (rawC != '=' && !char.IsWhiteSpace(rawC)) effectiveLen++;
        var workBuf = new byte[effectiveLen * 5 / 8 + 1];
        int buffer = 0, bitsLeft = 0, idx = 0;
        try
        {
            foreach (char rawC in input)
            {
                if (rawC == '=' || char.IsWhiteSpace(rawC)) continue;
                char c = char.ToUpperInvariant(rawC);
                int val = alphabet.IndexOf(c);
                if (val < 0)
                    throw new FormatException(
                        $"The Base32 string contains an invalid character: '{rawC}' (U+{(int)rawC:X4})");
                buffer   = (buffer << 5) | val;
                bitsLeft += 5;
                if (bitsLeft >= 8)
                {
                    bitsLeft -= 8;
                    workBuf[idx++] = (byte)(buffer >> bitsLeft);
                    buffer &= (1 << bitsLeft) - 1;
                }
            }

            // Allocate a just-sized array once, transcribe into it, and return it
            var result = new byte[idx];
            Buffer.BlockCopy(workBuf, 0, result, 0, idx);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(workBuf.AsSpan());
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[key] = val;
        }
        return result;
    }
}
