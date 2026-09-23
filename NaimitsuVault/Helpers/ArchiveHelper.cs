// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace NaimitsuVault.Helpers;

public sealed record ArchiveEntryInfo(string FullName, string SizeDisplay);

public sealed record ArchiveListResult(IReadOnlyList<ArchiveEntryInfo> Entries, int TotalFileCount);

public static class ArchiveHelper
{
    // Upper bound on entries materialized for display. Guards against a pathological ZIP (a huge
    // number of tiny/zero-byte central directory records, e.g. a "zip bomb"-style file) exhausting
    // memory or freezing the ListView. Mirrors the PDF page cap in ViewerWindow.xaml.cs
    // (Math.Min(doc.PageCount, 100u)). TotalFileCount still reports the true count so the UI can
    // show "showing first N of M".
    private const int MaxListedEntries = 2000;

    // Entry names of a ZIP written without the UTF-8 flag (EFS, general purpose bit 11) are in the
    // creating tool's legacy code page; the .NET default would decode them as UTF-8 and silently
    // replace every invalid byte with U+FFFD. A throwing UTF-8 decoder turns that into an exception
    // so the listing can be retried with the legacy code page. Entries that do carry the UTF-8 flag
    // are always decoded as UTF-8 regardless of this setting.
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();

    /// <summary>
    /// The system's legacy (OEM) code page, e.g. 932 on Japanese Windows, 437 on English, 936 on Simplified
    /// Chinese. This is what Windows Explorer's "Compressed folder" and 7-Zip use for names in a ZIP that
    /// lacks the UTF-8 flag, so it is the best guess for a ZIP that reached this machine. Taken from
    /// CodePagesEncodingProvider (shared framework), so no global provider registration is needed.
    /// Falls back to UTF-8 (with replacement) when the code page is not available.
    /// </summary>
    internal static Encoding GetSystemLegacyEncoding()
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding((int)GetOEMCP()) ?? Encoding.UTF8;
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Lists the file entries (name + size only) of a ZIP archive already decrypted into memory.
    /// Directory entries are skipped. Returns null if the bytes are not a well-formed ZIP.
    /// Entry names without the UTF-8 flag that are not valid UTF-8 are decoded with the system's
    /// legacy code page (<see cref="GetSystemLegacyEncoding"/>).
    /// </summary>
    public static ArchiveListResult? TryListZipEntries(byte[] data) =>
        TryListZipEntries(data, GetSystemLegacyEncoding());

    internal static ArchiveListResult? TryListZipEntries(byte[] data, Encoding legacyEncoding)
    {
        try
        {
            try
            {
                return ListEntries(data, StrictUtf8);
            }
            catch (DecoderFallbackException)
            {
                return ListEntries(data, legacyEncoding);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ArchiveListResult ListEntries(byte[] data, Encoding entryNameEncoding)
    {
        using var stream  = new MemoryStream(data, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding);

        var entries = new List<ArchiveEntryInfo>();
        int totalFileCount = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            totalFileCount++;
            if (entries.Count < MaxListedEntries)
                entries.Add(new ArchiveEntryInfo(entry.FullName, FormatSize(entry.Length)));
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return new ArchiveListResult(entries, totalFileCount);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024        => $"{bytes / 1024.0:F1} KB",
        _              => $"{bytes} B"
    };
}
