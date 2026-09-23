// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO.Compression;
using System.Text;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// "ZIP entry listing and entry-name encoding" TC-ZIP-01–06.
/// Legacy ZIPs store entry names in the creating machine's OEM code page (e.g. CP932 on Japanese Windows)
/// without the UTF-8 flag; <see cref="ArchiveHelper.TryListZipEntries(byte[])"/> must show them as-is
/// instead of U+FFFD. The fallback code page comes from the OS, so the cases that depend on it pass it
/// explicitly through the internal overload instead of relying on the test machine's locale.
/// </summary>
public sealed class ArchiveHelperTests
{
    private static readonly Encoding Cp932 = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
    private static readonly Encoding Gbk   = CodePagesEncodingProvider.Instance.GetEncoding(936)!;

    /// <summary>Builds a ZIP in memory. <paramref name="nameEncoding"/> null = .NET default (UTF-8 flag set for non-ASCII names).</summary>
    private static byte[] BuildZip(Encoding? nameEncoding, params (string Name, int Size)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, nameEncoding))
        {
            foreach (var (name, size) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var s = entry.Open();
                if (size > 0) s.Write(new byte[size]);
            }
        }
        return ms.ToArray();
    }

    // TC-ZIP-01: a ZIP with the UTF-8 flag (the .NET / modern tool default) lists non-ASCII names unchanged.
    [Fact]
    public void TryListZipEntries_Utf8FlaggedNames_ListedUnchanged()
    {
        var data = BuildZip(null, ("資料/設計書.txt", 5), ("readme.txt", 3));

        var result = ArchiveHelper.TryListZipEntries(data);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalFileCount);
        Assert.Equal(["readme.txt", "資料/設計書.txt"], result.Entries.Select(e => e.FullName));
    }

    // TC-ZIP-02: a legacy CP932 ZIP without the UTF-8 flag lists Japanese names correctly (no U+FFFD)
    // when the system legacy code page is CP932 (Japanese Windows).
    [Fact]
    public void TryListZipEntries_Cp932NamesWithoutUtf8Flag_DecodedWithLegacyEncoding()
    {
        var data = BuildZip(Cp932, ("資料/設計書.txt", 5), ("テスト.txt", 3));

        var result = ArchiveHelper.TryListZipEntries(data, legacyEncoding: Cp932);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalFileCount);
        Assert.Equal(["テスト.txt", "資料/設計書.txt"], result.Entries.Select(e => e.FullName));
        Assert.DoesNotContain(result.Entries, e => e.FullName.Contains('�'));
    }

    // TC-ZIP-03: an ASCII-only ZIP takes the strict UTF-8 path and is unaffected by the CP932 fallback.
    [Fact]
    public void TryListZipEntries_AsciiNames_ListedWithSizes()
    {
        var data = BuildZip(null, ("a.txt", 5), ("dir/b.txt", 0));

        var result = ArchiveHelper.TryListZipEntries(data);

        Assert.NotNull(result);
        Assert.Equal(["5 B", "0 B"], result.Entries.Select(e => e.SizeDisplay));
    }

    // TC-ZIP-04: directory entries (trailing slash, empty Name) are excluded from the listing and the count.
    [Fact]
    public void TryListZipEntries_DirectoryEntries_Skipped()
    {
        var data = BuildZip(null, ("dir/", 0), ("dir/file.txt", 1));

        var result = ArchiveHelper.TryListZipEntries(data);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalFileCount);
        Assert.Equal("dir/file.txt", Assert.Single(result.Entries).FullName);
    }

    // TC-ZIP-05: bytes that are not a ZIP return null without throwing (the viewer then shows "preview unsupported").
    [Fact]
    public void TryListZipEntries_NotAZip_ReturnsNull()
    {
        var data = Encoding.ASCII.GetBytes("this is definitely not a zip archive");

        Assert.Null(ArchiveHelper.TryListZipEntries(data));
    }

    // TC-ZIP-06: the fallback follows the supplied (system) code page, not a fixed CP932: a legacy GBK ZIP
    // (Simplified Chinese Windows) is read correctly with GBK, whereas CP932 would yield different characters.
    [Fact]
    public void TryListZipEntries_LegacyNames_FollowSuppliedCodePage()
    {
        var data = BuildZip(Gbk, ("文档/设计书.txt", 5));

        var withGbk   = ArchiveHelper.TryListZipEntries(data, legacyEncoding: Gbk);
        var withCp932 = ArchiveHelper.TryListZipEntries(data, legacyEncoding: Cp932);

        Assert.NotNull(withGbk);
        Assert.Equal("文档/设计书.txt", Assert.Single(withGbk.Entries).FullName);
        Assert.NotNull(withCp932);
        Assert.NotEqual("文档/设计书.txt", Assert.Single(withCp932.Entries).FullName);
    }
}
