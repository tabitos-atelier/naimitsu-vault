// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Localization;

/// <summary>
/// Static locale dictionary initialized once at startup.
/// Referenced by XAML MarkupExtension and ViewModels.
/// </summary>
public static class LocalizationManager
{
    private static readonly ILogger Logger = AppLog.For(typeof(LocalizationManager));

    private static Dictionary<string, string> _messages    = [];
    private static Dictionary<string, string> _fallback    = [];
    private static Dictionary<int, string>    _byCode      = [];
    private static Dictionary<string, int>    _codeByKey   = [];
    private static (int Code, string Name)[]  _categoryPresets = [];

    // Max bucket size for Serial (8 bits, with 0 reserved as the "not found" sentinel, leaving 255 values 1-255).
    private const int MaxSerialPerBucket = 255;

    public static void Initialize(Dictionary<string, string> messages, Dictionary<string, string> fallback)
    {
        _messages = Sanitize(messages);
        _fallback = Sanitize(fallback);

        // Collect and sort non-comment keys across all locales (primary + fallback)
        var allKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var k in _fallback.Keys) if (k.Length > 0 && k[0] != '#') allKeys.Add(k);
        foreach (var k in _messages.Keys) if (k.Length > 0 && k[0] != '#') allKeys.Add(k);

        // Group by (Domain, SubDomain) 16-bit prefix.
        // Serial = alphabetical index within the group (collision-free, deterministic)
        var groups = new Dictionary<int, List<string>>(allKeys.Count / 4);
        foreach (var key in allKeys)  // SortedSet → already alphabetical
        {
            int prefix = PackKeyPrefix(key);
            if (!groups.TryGetValue(prefix, out var list)) { list = []; groups[prefix] = list; }
            list.Add(key);
        }

        _byCode    = new Dictionary<int, string>(allKeys.Count);
        _codeByKey = new Dictionary<string, int>(allKeys.Count, StringComparer.Ordinal);
        foreach (var (prefix, keyList) in groups)
        {
            if (keyList.Count > MaxSerialPerBucket)
            {
                // Serial is 8 bits (0 reserved for the "not found" sentinel, leaving 255 values 1-255).
                // Exceeding this makes the bits overflow into the SubDomain range and collide with a
                // completely different key space, so stop immediately.
                const string ErrorTemplate = "Locale key group (prefix=0x{0:X6}) exceeds the Serial limit of {1} entries ({2} entries).";
                var message = string.Format(ErrorTemplate, prefix, MaxSerialPerBucket, keyList.Count);
                Logger.LogError("{Message} Split the group, e.g. via SubDomain partitioning.", message);
                throw new InvalidOperationException(message);
            }

            for (int i = 0; i < keyList.Count; i++)
            {
                int code = prefix | (i + 1);  // Serial starts at 1. 0 is reserved exclusively for the "not found" sentinel.
                var key  = keyList[i];
                _codeByKey[key] = code;
                // Dictionary.TryGetValue assigns the out parameter to default(T) (null for string) on a
                // miss, even when it doesn't find the key - a prior successful lookup's value would get
                // silently wiped out by a later failed one if these ran as two separate statements
                // (e.g. "found in _fallback" followed by "not found in _messages"). Short-circuit ||
                // avoids that: the second TryGetValue only runs, and only its result is kept, when the
                // first one missed.
                if (_messages.TryGetValue(key, out var val) || _fallback.TryGetValue(key, out val))
                    _byCode[code] = val;
            }
        }

        _categoryPresets = BuildCategoryPresets();
    }

    private static Dictionary<string, string> Sanitize(Dictionary<string, string> src)
    {
        var result = new Dictionary<string, string>(src.Count, StringComparer.Ordinal);
        foreach (var (k, v) in src)
            result[k] = SanitizeValue(v);
        return result;
    }

    // \n and \r (including \r\n pairs) are preserved as intentional line breaks - many dialog message
    // keys rely on them to separate a warning from its confirmation question. \t \f \v collapse to a
    // single space instead (stray formatting, never intentional in a UI string), and remaining control
    // characters (NUL, non-whitespace C0, DEL, C1) are stripped outright.
    // Processed with a loop because Regex.Compiled misbehaves on strings containing NUL in .NET 10.
    private static string SanitizeValue(string v)
    {
        var sb = new StringBuilder(v.Length);
        bool pendingSpace = false;
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            bool isCrLfPair = c == '\r' && i + 1 < v.Length && v[i + 1] == '\n';
            if (c == '\n' || (c == '\r' && !isCrLfPair))
            {
                pendingSpace = false; // a line break supersedes any pending collapsed-whitespace space
                sb.Append('\n');
            }
            else if (isCrLfPair)
            {
                // The \r half of a \r\n pair: skip it, the \n on the next iteration appends the break
            }
            else if (c is '\t' or '\f' or '\v')
            {
                pendingSpace = true;
            }
            else if (c <= '\x08' || (c >= '\x0E' && c <= '\x1F') || c == '\x7F' || (c >= '\x80' && c <= '\x9F'))
            {
                // NUL, non-whitespace C0, DEL, C1: strip (does not consume pendingSpace)
            }
            else
            {
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }
        }
        if (pendingSpace) sb.Append(' ');
        return sb.ToString();
    }

    public static string Get(string key)
    {
        if (_messages.TryGetValue(key, out var val)) return val;
        if (_fallback.TryGetValue(key, out val)) return val;
        return $"[{key}]";
    }

    /// <summary>
    /// Returns the category presets the active locale defines - keys of the form "Categories.NN"
    /// (exactly 2 digits), sorted ascending by code. Categories have no DB table; whichever codes a
    /// locale happens to define (and however many) is exactly what this returns - the active locale's
    /// value wins over the OS-locale fallback for a given code, and either side can contribute codes
    /// the other doesn't have. Computed once by <see cref="BuildCategoryPresets"/> at the end of
    /// <see cref="Initialize"/> (the locale dictionaries are immutable afterward), so this is a plain
    /// field read rather than a rescan of both ~460-entry dictionaries on every call.
    /// </summary>
    public static IReadOnlyList<(int Code, string Name)> GetCategoryPresets() => _categoryPresets;

    private static (int Code, string Name)[] BuildCategoryPresets()
    {
        const string Prefix = "Categories.";
        var byCode = new SortedDictionary<int, string>();

        void Collect(Dictionary<string, string> dict)
        {
            foreach (var (key, value) in dict)
            {
                if (!key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                var suffix = key.AsSpan(Prefix.Length);
                if (suffix.Length == 2 && int.TryParse(suffix, out var code))
                    byCode[code] = value;
            }
        }
        Collect(_fallback);
        Collect(_messages); // primary locale overrides the fallback's value for the same code

        var result = new (int Code, string Name)[byCode.Count];
        int i = 0;
        foreach (var (code, name) in byCode) result[i++] = (code, name);
        return result;
    }

    public static string Get(string key, params object[] args)
    {
        var val = Get(key);
        if (args.Length == 0) return val;
        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, val, args);
        }
        catch (FormatException)
        {
            // Invalid format string (out-of-range index, unclosed { , etc.): return the raw template
            return val;
        }
    }

    /// <summary>
    /// Returns a locale value from an int code. Used with <see cref="LK"/> to avoid capturing a string into a closure.
    /// </summary>
    public static string GetById(int code)
    {
        if (_byCode.TryGetValue(code, out var val)) return val;
        return $"[0x{code:X}]";
    }

    /// <summary>
    /// Returns the int code for a locale key string. Used when initializing each <see cref="LK"/> field.
    /// Must be called after <see cref="Initialize"/> completes.
    /// </summary>
    public static int GetCode(string key)
    {
        if (_codeByKey.TryGetValue(key, out var code)) return code;
        // Since Serial is numbered starting at 1, a real key's code can never be 0
        // (0 is reserved exclusively as this method's "not found" sentinel). Returning 0 always means the key string is wrong.
        Logger.LogWarning("GetCode: locale key not found. [{Key}]", key);
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 24-bit bit-packing implementation
    //   bit23:16 = Domain    (8bit)
    //   bit15:8  = SubDomain (8bit)  0x00=none / 0x01=Navi / 0x02=Dialog / 0x03=Inline / 0x04=Mode
    //   bit7:0   = Serial    (8bit)  ← assigned by Initialize() as (alphabetical index within group + 1) (1-255, 0 reserved as not-found sentinel)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the key's upper 16 bits (Domain:SubDomain).
    /// The Serial bits (7:0) are assigned by Initialize() as the alphabetical rank within the group.
    /// </summary>
    internal static int PackKeyPrefix(string key)
    {
        var seg = key.Split('.');
        int n = seg.Length;
        if (n == 0) return 0;

        int domain = DomainCodes.TryGetValue(seg[0], out var domainCode) ? domainCode : 0;
        int sub = 0;

        // Reference the subdomain for any key with 3+ segments (a.b.c[.d...]) - seg[1] is always the
        // subdomain position regardless of how many segments follow it.
        if (n >= 3 && SubDomainCodes.TryGetValue(seg[1], out var subDomainCode))
            sub = subDomainCode;
        // Product / Exception under the System domain
        else if (n >= 3 && domain == 0xFF && SystemSubDomainCodes.TryGetValue(seg[1], out var systemSubDomainCode))
            sub = systemSubDomainCode;

        return (domain << 16) | (sub << 8);
    }

    private static readonly Dictionary<string, int> DomainCodes = new()
    {
        ["Common"]          = 0x00,
        ["Unlock"]          = 0x01,
        ["Shell"]           = 0x02,
        ["Dashboard"]       = 0x03,
        ["Secrets"]         = 0x04,
        ["TimeMachine"]     = 0x05,
        ["Gallery"]         = 0x06,
        ["Profile"]         = 0x07,
        ["Categories"]      = 0x08,
        ["AuditLog"]        = 0x09,
        ["Totp"]            = 0x0A,
        ["DirectInjection"] = 0x0B,
        ["Viewer"]          = 0x0C,
        ["AppSettings"]     = 0x0D,
        ["VaultSettings"]   = 0x0E,
        ["Restore"]         = 0x0F,
        ["Emergency"]       = 0x10,
        ["System"]          = 0xFF,
    };

    private static readonly Dictionary<string, int> SubDomainCodes = new()
    {
        ["Navi"]   = 0x01,
        ["Dialog"] = 0x02,
        ["Inline"] = 0x03,
        ["Mode"]   = 0x04,
    };

    // 0x04 (formerly "Audit") is a deliberate gap: System.Audit.* moved to AuditLog.* on 2026-08-10
    // (audit log summary text is no longer an immutable/protected key group), leaving 0x03/0x05 as
    // the two remaining System-only subdomains.
    private static readonly Dictionary<string, int> SystemSubDomainCodes = new()
    {
        ["Product"]   = 0x03,
        ["Exception"] = 0x05,
    };
}
