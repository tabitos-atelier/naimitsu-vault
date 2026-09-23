// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Export/import boundary values - all 6 cases of SettingsViewModel's pure logic
/// plus 3 Phase 2 stream-based test cases.
///
/// UI flows involving the file picker, ContentDialog, or InfoBar are out of scope (Phase 2).
/// ParseCsvLine / IsCsvRecordComplete / MapCategory are promoted to internal and tested directly.
/// Import row counts use ParseAndCountCsvAsync (internal static).
///
/// ResolveDuplicateTitle calls LocalizationManager.Get internally, so this class participates
/// in the SequentialLocale collection to serialize static state.
/// </summary>
[Collection("SequentialLocale")]
public sealed class ExportImportTests
{
    // ── TC-EXP-01 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Confirms the CSV header-line constant matches the export spec.
    /// Since WriteCsvAsync calls WriteLineAsync with this constant first, a zero-item
    /// export is equivalent to "header line only".
    /// </summary>
    [Fact]
    public void ExportCsv_HeaderLineConstant_ContainsAllExpectedColumns()
    {
        // Assert - the column order matches the export spec
        var columns = VaultImportExportHelper.CsvHeaderLine.Split(',');
        Assert.Equal(12, columns.Length);
        Assert.Equal("Title",        columns[0]);
        Assert.Equal("Category",     columns[1]);
        Assert.Equal("UserId",       columns[2]);
        Assert.Equal("Password",     columns[3]);
        Assert.Equal("Website",      columns[4]);
        Assert.Equal("Email",        columns[5]);
        Assert.Equal("Notes",        columns[6]);
        Assert.Equal("CustomFields", columns[7]);
        Assert.Equal("CreatedAt",    columns[8]);
        Assert.Equal("UpdatedAt",    columns[9]);
        Assert.Equal("IsFavorite",   columns[10]);
        Assert.Equal("ExpiresAt",    columns[11]);
    }

    // ── TC-EXP-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportCsv_ZeroSecrets_ParseAndCountReturns0()
    {
        // Arrange - CSV with only a header line (no data rows)
        var csv    = VaultImportExportHelper.CsvHeaderLine + "\n";
        var reader = new StringReader(csv);

        // Act - ParseAndCountCsvAsync skips the header and processes data rows
        var (total, skipped) = await VaultImportExportHelper.ParseAndCountCsvAsync(reader, validCodes: []);

        // Assert
        Assert.Equal(0, total);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public async Task Import_EmptyFile_Returns0ImportCount()
    {
        // Arrange - an empty file (not even a header)
        var reader = new StringReader(string.Empty);

        // Act - for an empty file, ReadLineAsync() returns null -> it ends right at the header-skip step
        var (total, skipped) = await VaultImportExportHelper.ParseAndCountCsvAsync(reader, validCodes: []);

        // Assert
        Assert.Equal(0, total);
        Assert.Equal(0, skipped);
    }

    // ── TC-EXP-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_HeaderRowOnly_Returns0ImportCount()
    {
        // Arrange - header line only
        var reader = new StringReader(VaultImportExportHelper.CsvHeaderLine);

        // Act
        var (total, skipped) = await VaultImportExportHelper.ParseAndCountCsvAsync(reader, validCodes: []);

        // Assert
        Assert.Equal(0, total);
        Assert.Equal(0, skipped);
    }

    // ── TC-EXP-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_TitleEmptyRow_IsImported()
    {
        // Arrange - a row with an empty Title but a value in another column (comma-delimited)
        var csv = VaultImportExportHelper.CsvHeaderLine + "\n" +
                  ",1100,user@example.com,,,,,,,,false,\n"; // Title = ""

        var reader = new StringReader(csv);

        // Act
        var (total, skipped) = await VaultImportExportHelper.ParseAndCountCsvAsync(reader, validCodes: []);

        // Assert - the record is imported (not lost) even though its Title is empty
        Assert.Equal(1, total);
        Assert.Equal(0, skipped);
    }

    // ── TC-EXP-21 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_BlankAndDelimiterOnlyRows_AreIgnored()
    {
        // Arrange - one real record followed by the kinds of trailing junk editors and spreadsheet apps add:
        // a truly blank line, a delimiter-only line, and a whitespace-only line
        var csv = VaultImportExportHelper.CsvHeaderLine + "\n" +
                  "GitHub,1,tabito,pw,,,,,,,false,\n" +
                  "\n" +
                  ",,,,,,,,,,,\n" +
                  "  ,  ,,,,,,,,,,\n";

        var reader = new StringReader(csv);

        // Act
        var (total, skipped) = await VaultImportExportHelper.ParseAndCountCsvAsync(reader, validCodes: []);

        // Assert - only the real record counts; the three blank rows are ignored, not imported as empty secrets
        Assert.Equal(1, total);
        Assert.Equal(3, skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",,,,,,,,,,,")]
    [InlineData(" , ,\t,")]
    public void IsBlankCsvRecord_AllColumnsEmptyOrWhitespace_ReturnsTrue(string line)
    {
        Assert.True(VaultImportExportHelper.IsBlankCsvRecord(VaultImportExportHelper.ParseCsvLine(line)));
    }

    [Theory]
    [InlineData(",1100,,,,,,,,,,")]      // empty Title, value in Category
    [InlineData(",,,,,,x,,,,,")]         // empty Title, value only in Notes
    [InlineData("Only Title")]           // Title only
    public void IsBlankCsvRecord_AnyColumnHasValue_ReturnsFalse(string line)
    {
        Assert.False(VaultImportExportHelper.IsBlankCsvRecord(VaultImportExportHelper.ParseCsvLine(line)));
    }

    // ── TC-EXP-05 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Import_UnknownCategoryNum_RoundsToUncategorized()
    {
        // Arrange - a category code that does not exist
        var validCodes = new HashSet<int> { 1, 10, 99 };

        // Act
        var (code, wasMapped) = VaultImportExportHelper.MapCategory(99999, validCodes);

        // Assert - an undefined code is rounded to 0 (uncategorized)
        Assert.Equal(0, code);
        Assert.True(wasMapped);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Phase 2: stream-based export tests
    // Calls WriteCsvToStreamAsync / WriteJsonToStreamAsync directly to verify
    // the export path without any filesystem dependency.
    // ════════════════════════════════════════════════════════════════════════

    private static CryptoService Crypto() => new(NullLogger<CryptoService>.Instance);

    private static DekScope MakeDek()
    {
        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        return new DekScope(key, 32); // internal ctor - allowed via InternalsVisibleTo
    }

    // ── TC-EXP-06 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteCsvToStream_ZeroSecrets_OutputsHeaderLineOnly()
    {
        // Arrange
        await using var ms = new MemoryStream();

        // Act
        await VaultImportExportHelper.WriteCsvToStreamAsync([], Crypto(), MakeDek(), ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        var line1 = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        var line2 = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        // Assert - only 1 header line
        Assert.Equal(VaultImportExportHelper.CsvHeaderLine, line1);
        Assert.Null(line2);
    }

    // ── TC-EXP-07 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteJsonToStream_ZeroSecrets_OutputsEmptyJsonArray()
    {
        // Arrange
        await using var ms = new MemoryStream();

        // Act
        await VaultImportExportHelper.WriteJsonToStreamAsync([], Crypto(), MakeDek(), ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ms);

        // Assert - the JSON is an empty array
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // ── TC-EXP-08 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteCsvToStream_SecretWithCommaInNotes_IsDoubleQuoteEscaped()
    {
        // Arrange - build an encrypted Secret whose Notes is "line1, line2"
        var crypto = Crypto();
        var dek    = MakeDek();
        var notesPlain = Encoding.UTF8.GetBytes("line1, line2");
        var notesEnc   = crypto.Encrypt(notesPlain, dek.Span);
        var titleEnc   = crypto.Encrypt(Encoding.UTF8.GetBytes("TestEntry"), dek.Span);

        var secret = new Secret
        {
            Title    = titleEnc,
            Notes    = notesEnc,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();

        // Act
        await VaultImportExportHelper.WriteCsvToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        await reader.ReadLineAsync(TestContext.Current.CancellationToken); // skip the header
        var dataLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        // Assert - the Notes field is wrapped in double quotes
        Assert.NotNull(dataLine);
        Assert.Contains("\"line1, line2\"", dataLine);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Phase 3: direct tests for ParseCsvLine / IsCsvRecordComplete / ResolveDuplicateTitle
    // ════════════════════════════════════════════════════════════════════════

    // ── TC-EXP-09: ParseCsvLine ──────────────────────────────────────────────────

    [Fact]
    public void ParseCsvLine_PlainFields_ReturnsSplitValues()
    {
        var fields = VaultImportExportHelper.ParseCsvLine("title,1100,user,pass");

        Assert.Equal(4, fields.Count);
        Assert.Equal("title", fields[0]);
        Assert.Equal("1100",  fields[1]);
        Assert.Equal("user",  fields[2]);
        Assert.Equal("pass",  fields[3]);
    }

    [Fact]
    public void ParseCsvLine_QuotedFieldWithComma_ParsesAsOneField()
    {
        var fields = VaultImportExportHelper.ParseCsvLine("\"hello, world\",second");

        Assert.Equal(2, fields.Count);
        Assert.Equal("hello, world", fields[0]);
        Assert.Equal("second",       fields[1]);
    }

    [Fact]
    public void ParseCsvLine_DoubleQuoteEscapedInField_UnescapesQuote()
    {
        // CSV: "say ""hi""",end -> field: say "hi"
        var fields = VaultImportExportHelper.ParseCsvLine("\"say \"\"hi\"\"\",end");

        Assert.Equal(2, fields.Count);
        Assert.Equal("say \"hi\"", fields[0]);
        Assert.Equal("end",        fields[1]);
    }

    [Fact]
    public void ParseCsvLine_EmptyMiddleField_ReturnsEmptyString()
    {
        var fields = VaultImportExportHelper.ParseCsvLine("a,,c");

        Assert.Equal(3, fields.Count);
        Assert.Equal("a", fields[0]);
        Assert.Equal("",  fields[1]);
        Assert.Equal("c", fields[2]);
    }

    // ── TC-EXP-10: IsCsvRecordComplete ───────────────────────────────────────────

    [Fact]
    public void IsCsvRecordComplete_NoQuotes_ReturnsTrue()
    {
        Assert.True(VaultImportExportHelper.IsCsvRecordComplete("title,1100,user,pass"));
    }

    [Fact]
    public void IsCsvRecordComplete_OpenQuotedField_ReturnsFalse()
    {
        // One quote (odd count) -> the field is still open
        Assert.False(VaultImportExportHelper.IsCsvRecordComplete("\"title with newline"));
    }

    [Fact]
    public void IsCsvRecordComplete_ClosedQuotedField_ReturnsTrue()
    {
        Assert.True(VaultImportExportHelper.IsCsvRecordComplete("\"title\",1100"));
    }

    [Fact]
    public void IsCsvRecordComplete_AccumulatedLines_CompletesOnClose()
    {
        // Joining multiple lines with "\n" produces the same judgment as ParseAndCountCsvAsync
        var partial = "\"line1";
        var full    = partial + "\nline2\"";

        Assert.False(VaultImportExportHelper.IsCsvRecordComplete(partial));
        Assert.True(VaultImportExportHelper.IsCsvRecordComplete(full));
    }

    // ── TC-EXP-11: ResolveDuplicateTitle ────────────────────────────────────────

    [Fact]
    public void ResolveDuplicateTitle_UniqueTitle_ReturnsSameTitle()
    {
        var existing = new HashSet<string> { "Other" };
        var result   = VaultImportExportHelper.ResolveDuplicateTitle("MyTitle", existing);

        Assert.Equal("MyTitle", result);
        Assert.Contains("MyTitle", existing); // side effect: already added
    }

    [Fact]
    public void ResolveDuplicateTitle_ExistingTitle_AppendsImportSuffix()
    {
        var importTag = LocalizationManager.Get("Common.Import");
        var existing  = new HashSet<string> { "MyTitle" };
        var result    = VaultImportExportHelper.ResolveDuplicateTitle("MyTitle", existing);

        Assert.Equal($"MyTitle ({importTag})", result);
        Assert.Contains($"MyTitle ({importTag})", existing);
    }

    [Fact]
    public void ResolveDuplicateTitle_TwoConflicts_IncrementsCounter()
    {
        var importTag = LocalizationManager.Get("Common.Import");
        var existing  = new HashSet<string> { "MyTitle", $"MyTitle ({importTag})" };
        var result    = VaultImportExportHelper.ResolveDuplicateTitle("MyTitle", existing);

        Assert.Equal($"MyTitle ({importTag}2)", result);
        Assert.Contains($"MyTitle ({importTag}2)", existing);
    }

    // ── TC-EXP-12: WriteJsonToStreamAsync with fields ────────────────────────────

    [Fact]
    public async Task WriteJsonToStream_SecretWithAllFields_ContainsExpectedValues()
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = VaultImportExportHelper.BuildSecret(
            crypto, "TestTitle", 1100,
            "user@example.com", "secret123", "https://example.com", null,
            "My notes", null, null, null, dek,
            isFavorite: true);

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteJsonToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc   = JsonDocument.Parse(ms);
        var arr         = doc.RootElement;
        Assert.Equal(1, arr.GetArrayLength());
        var entry = arr[0];
        Assert.Equal("TestTitle",          entry.GetProperty("title").GetString());
        Assert.Equal(1100,                 entry.GetProperty("category").GetInt32());
        Assert.Equal("user@example.com",   entry.GetProperty("userId").GetString());
        Assert.Equal("secret123",          entry.GetProperty("password").GetString());
        Assert.Equal("https://example.com",entry.GetProperty("website").GetString());
        Assert.Equal("My notes",           entry.GetProperty("notes").GetString());
        Assert.True(entry.GetProperty("isFavorite").GetBoolean());
    }

    [Fact]
    public async Task WriteJsonToStream_CjkTitle_WrittenAsReadableUtf8NotEscaped()
    {
        // The default Utf8JsonWriter encoder escapes non-ASCII as \uXXXX (HTML-safe), which makes
        // a plaintext export the user opens directly unreadable. Verify the relaxed encoder keeps
        // CJK characters as literal UTF-8 bytes in the exported file.
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = VaultImportExportHelper.BuildSecret(
            crypto, "サンプル１", null,
            null, null, null, null, null, null, null, null, dek);

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteJsonToStreamAsync([secret], crypto, dek, ms);

        var rawJson = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("サンプル１", rawJson);
        Assert.DoesNotContain("\\u30B5", rawJson);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ms);
        Assert.Equal("サンプル１", doc.RootElement[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task WriteJsonToStream_IncludeTotpFalse_TotpSecretKeyAbsent()
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = VaultImportExportHelper.BuildSecret(
            crypto, "Entry", null,
            null, null, null, null, null, null, null, null, dek,
            totpSecret: "JBSWY3DPEHPK3PXP");

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteJsonToStreamAsync([secret], crypto, dek, ms, includeTotp: false);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ms);
        Assert.False(doc.RootElement[0].TryGetProperty("totpSecret", out _));
    }

    [Fact]
    public async Task WriteJsonToStream_IncludeTotpTrue_TotpSecretKeyPresent()
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = VaultImportExportHelper.BuildSecret(
            crypto, "Entry", null,
            null, null, null, null, null, null, null, null, dek,
            totpSecret: "JBSWY3DPEHPK3PXP");

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteJsonToStreamAsync([secret], crypto, dek, ms, includeTotp: true);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ms);
        Assert.True(doc.RootElement[0].TryGetProperty("totpSecret", out var totpProp));
        Assert.Equal("JBSWY3DPEHPK3PXP", totpProp.GetString());
    }

    // ── TC-EXP-19: BuildSecret Notes line-ending normalization ───────────────────

    [Fact]
    public async Task BuildSecret_NotesWithCrlf_NormalizedToLfBeforeEncryption()
    {
        // Regression: an imported Notes value containing CRLF (e.g. from a Windows-authored or
        // hand-edited JSON file) must be normalized to LF before encryption, matching what
        // SecretEditModel.Notes's setter always produces for UI-entered text. Otherwise the Gen0
        // baseline stores it untouched, and WinUI 3's multiline TextBox - which normalizes to its
        // own bare-CR internal representation the moment the value is displayed - reads back a
        // different value on the very next focus-out, spuriously flagging the item dirty and
        // autosaving a no-op draft (visible to the user as the item entering "draft mode" on view).
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = VaultImportExportHelper.BuildSecret(
            crypto, "Entry", null,
            null, null, null, null, "line1\r\nline2\r\nline3", null, null, null, dek);

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteJsonToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ms);
        Assert.Equal("line1\nline2\nline3", doc.RootElement[0].GetProperty("notes").GetString());
    }

    [Fact]
    public async Task BuildSecret_NotesWithoutCarriageReturn_UnchangedFastPath()
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = VaultImportExportHelper.BuildSecret(
            crypto, "Entry", null,
            null, null, null, null, "line1\nline2", null, null, null, dek);

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteJsonToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ms);
        Assert.Equal("line1\nline2", doc.RootElement[0].GetProperty("notes").GetString());
    }

    // ── TC-EXP-20: ParseCsvRecordsAsync multi-line field reassembly ──────────────

    [Fact]
    public async Task ParseCsvRecords_QuotedFieldWithCrlfLineBreak_ReassemblesAsLf()
    {
        // Unlike JSON (where a literal \r\n inside a string value survives JSON parsing untouched,
        // which is what TC-EXP-19 above guards against), a CSV file's own row terminator and an
        // embedded newline inside a quoted field are indistinguishable to TextReader.ReadLineAsync -
        // it strips CR/LF/CRLF alike at every physical line break, and ParseCsvRecordsAsync rejoins
        // continuation lines with a bare "\n" (see line 367). So a quoted multi-line CSV field is
        // already always LF-only by the time it reaches BuildSecret, regardless of whether the
        // source file used CRLF or LF for its own line endings - this pins that CSV import was never
        // actually exposed to the draft-mode bug TC-EXP-19 fixes for JSON.
        var csv = VaultImportExportHelper.CsvHeaderLine + "\r\n" +
                  "Entry,,,,,,\"line1\r\nline2\",,,,,\r\n";
        var reader = new StringReader(csv);

        var records = new List<List<string>>();
        await foreach (var fields in VaultImportExportHelper.ParseCsvRecordsAsync(reader))
            records.Add(fields);

        var row = Assert.Single(records);
        Assert.Equal("line1\nline2", row[6]);
    }

    // ── TC-EXP-13: additional WriteCsvToStreamAsync escaping ──────────────────────────

    [Fact]
    public async Task WriteCsvToStream_SecretWithDoubleQuoteInNotes_EscapesDoubleQuotes()
    {
        var crypto   = Crypto();
        var dek      = MakeDek();
        var notes    = "He said \"hello\"";
        var notesEnc = crypto.Encrypt(Encoding.UTF8.GetBytes(notes), dek.Span);
        var titleEnc = crypto.Encrypt(Encoding.UTF8.GetBytes("Entry"), dek.Span);

        var secret = new Secret
        {
            Title    = titleEnc,
            Notes    = notesEnc,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteCsvToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        await reader.ReadLineAsync(TestContext.Current.CancellationToken); // skip the header
        var dataLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        // He said "hello" -> "He said ""hello"""
        Assert.NotNull(dataLine);
        Assert.Contains("\"He said \"\"hello\"\"\"", dataLine);
    }

    [Fact]
    public async Task WriteCsvToStream_SecretWithNewlineInNotes_WrapsInDoubleQuotes()
    {
        var crypto   = Crypto();
        var dek      = MakeDek();
        var notes    = "line1\nline2";
        var notesEnc = crypto.Encrypt(Encoding.UTF8.GetBytes(notes), dek.Span);
        var titleEnc = crypto.Encrypt(Encoding.UTF8.GetBytes("Entry"), dek.Span);

        var secret = new Secret
        {
            Title    = titleEnc,
            Notes    = notesEnc,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteCsvToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        var fullContent = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Notes containing a newline must be wrapped in double quotes
        Assert.Contains("\"line1\nline2\"", fullContent);
    }

    // ── TC-EXP-14: skip-and-continue when one record fails to decrypt ─────────────
    // Regression coverage for the bug where a single undecryptable secret (e.g. data
    // corruption) aborted the entire export via an uncaught CryptographicException,
    // leaving zero records written even though the other secrets were fine.

    [Fact]
    public async Task WriteJsonToStream_OneCorruptedSecret_SkipsItAndWritesTheOthers()
    {
        var crypto      = Crypto();
        var dek         = MakeDek();
        var wrongKeyDek = MakeDek(); // a different key: ciphertext fails GCM tag verification under `dek`

        var good1 = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes("Good1"), dek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var corrupted = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes("Corrupted"), wrongKeyDek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var good2 = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes("Good2"), dek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();

        var (written, skipped, failedIds) = await VaultImportExportHelper.WriteJsonToStreamAsync([good1, corrupted, good2], crypto, dek, ms);

        Assert.Equal(2, written);
        Assert.Equal(1, skipped);
        Assert.Equal([corrupted.Id], failedIds);

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        var titles = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToList();
        Assert.Contains("Good1", titles);
        Assert.Contains("Good2", titles);
        Assert.DoesNotContain("Corrupted", titles);
    }

    [Fact]
    public async Task WriteCsvToStream_OneCorruptedSecret_SkipsItAndWritesTheOthers()
    {
        var crypto      = Crypto();
        var dek         = MakeDek();
        var wrongKeyDek = MakeDek();

        var good1 = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes("Good1"), dek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var corrupted = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes("Corrupted"), wrongKeyDek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var good2 = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes("Good2"), dek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();

        var (written, skipped, failedIds, guardedRecords) = await VaultImportExportHelper.WriteCsvToStreamAsync([good1, corrupted, good2], crypto, dek, ms);

        Assert.Equal(2, written);
        Assert.Equal(1, skipped);
        Assert.Equal([corrupted.Id], failedIds);
        Assert.Empty(guardedRecords);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        await reader.ReadLineAsync(TestContext.Current.CancellationToken); // skip the header
        var fullContent = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var dataLines = fullContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, dataLines.Length);
        Assert.Contains(dataLines, l => l.StartsWith("Good1,"));
        Assert.Contains(dataLines, l => l.StartsWith("Good2,"));
        Assert.DoesNotContain(dataLines, l => l.StartsWith("Corrupted,"));
    }

    // ── TC-EXP-16: CSV formula-injection guard ─────────────────────────────
    // A Title/field value beginning with =, +, -, or @ is interpreted as a formula by
    // Excel/Sheets/LibreOffice when the exported CSV is later opened, not as literal text - e.g. a
    // secret imported with Title "=HYPERLINK(...)" could exfiltrate data or run a command on
    // re-export. WriteSecureCsvFieldAsync prepends a leading apostrophe (the standard "force text"
    // marker) to neutralize this, independent of the AES-256-GCM encryption in the DB layer (which
    // already rules out SQL injection - this guards the plaintext CSV file itself).

    [Theory]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("+HYPERLINK(\"http://evil/\")")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1,2)")]
    public async Task WriteCsvToStream_FormulaTriggerCharPrefix_PrependsApostrophe(string malicious)
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = new Secret
        {
            Id       = 7,
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes(malicious), dek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();
        var (_, _, _, guardedRecords) = await VaultImportExportHelper.WriteCsvToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        await reader.ReadLineAsync(TestContext.Current.CancellationToken); // skip the header
        var dataLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(dataLine);
        // A formula-triggering value always gets CSV-quoted (even with no comma/quote/newline of its
        // own) so the guard apostrophe can be written INSIDE the quotes, right after the opening `"`
        // - writing it ahead of the quotes instead would make the field start at `'` rather than `"`,
        // which stops it from being parsed as a quoted CSV field at all.
        Assert.StartsWith("\"'", dataLine);
        // The record is reported for CsvFormulaGuardApplied audit logging, carrying the (already
        // decrypted, un-prefixed) title so the log row identifies which item was affected.
        var guarded = Assert.Single(guardedRecords);
        Assert.Equal(7, guarded.Id);
        Assert.Equal(malicious, guarded.Title);
    }

    [Fact]
    public async Task WriteCsvToStream_FormulaTriggerCharPlusComma_StaysOneValidCsvField()
    {
        // Regression guard: an earlier draft of the fix wrote the apostrophe as a separate literal
        // BEFORE the CSV quoting wrapper (`'"..."`) instead of inside it (`"'..."`). That produces a
        // field that no longer starts with `"`, so a CSV parser reads it as unquoted from the `'`
        // onward - the embedded comma below would then be misread as a second column instead of
        // staying part of the (still-malicious) Title value.
        var crypto  = Crypto();
        var dek     = MakeDek();
        const string malicious = "=A1,B1";
        var secret = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes(malicious), dek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteCsvToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        // ParseCsvRecordsAsync skips the header line itself - unlike the other tests in this file,
        // which read the raw stream directly and so must skip it manually.

        var records = new List<List<string>>();
        await foreach (var fields in VaultImportExportHelper.ParseCsvRecordsAsync(reader))
            records.Add(fields);

        var row = Assert.Single(records);
        // The whole malicious value (comma included) round-trips as a single Title field, with the
        // guard apostrophe prepended - it must NOT have split into two CSV columns.
        Assert.Equal("'" + malicious, row[0]);
    }

    [Fact]
    public async Task WriteCsvToStream_OrdinaryValue_NoApostropheAdded()
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = new Secret
        {
            Title    = crypto.Encrypt(Encoding.UTF8.GetBytes("Ordinary Title"), dek.Span),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();
        var (_, _, _, guardedRecords) = await VaultImportExportHelper.WriteCsvToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        var dataLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("Ordinary Title,", dataLine);
        Assert.Empty(guardedRecords);
    }

    // ── TC-EXP-17: full-Unicode JSON encoder ────────────────────────────────
    // UnsafeRelaxedJsonEscaping (the previous encoder) \uXXXX-escaped all non-ASCII, including CJK
    // letters. JavaScriptEncoder.Create(UnicodeRanges.All) keeps CJK letters as literal UTF-8, but
    // a full-width space (U+3000, Unicode category Space Separator) is still \uXXXX-escaped - this
    // is a hardcoded JavaScriptEncoder safety behavior that UnicodeRanges.All cannot override (the
    // only workaround is bypassing the encoder entirely via Utf8JsonWriter.WriteRawValue with a
    // hand-rolled JSON string escaper, not worth the added complexity/risk here). This test pins
    // both halves of that behavior so a future encoder change doesn't silently regress the CJK fix.

    [Fact]
    public async Task WriteJsonToStream_CjkAndFullWidthSpaceInNotes_CjkReadableSpaceStillEscaped()
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        var secret = VaultImportExportHelper.BuildSecret(
            crypto, "Entry", null,
            null, null, null, null, "全角　空白", null, null, null, dek);

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteJsonToStreamAsync([secret], crypto, dek, ms);

        var rawJson = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("全角", rawJson);
        Assert.Contains("空白", rawJson);
        Assert.Contains("\\u3000", rawJson); // known JavaScriptEncoder limitation, not a bug

        ms.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ms);
        Assert.Equal("全角　空白", doc.RootElement[0].GetProperty("notes").GetString());
    }

    // ── TC-EXP-18: CSV CustomFields column isn't \uXXXX-escaped ────────────
    // CustomFields is stored as JSON. Records written before FieldCrypto.SealJson switched to
    // FullUnicodeEncoder have the default encoder's \uXXXX escapes baked into the encrypted blob.
    // WriteCsvToStreamAsync must re-serialize with the relaxed encoder before writing the CSV
    // column, so even those older records read as literal text in the exported file.

    [Fact]
    public async Task WriteCsvToStream_CustomFieldsWithDefaultEncoderEscaping_ReExportsReadable()
    {
        var crypto = Crypto();
        var dek    = MakeDek();
        // Simulate a pre-fix record: CustomFields JSON serialized with the default (escaping) encoder.
        var cfJson = System.Text.Json.JsonSerializer.Serialize(
            new[] { new { Label = "シークレットキー", Value = "値", FieldType = 0 } });
        Assert.Contains("\\u30B7", cfJson); // sanity check: the default encoder did escape it

        var secret = new Secret
        {
            Title        = crypto.Encrypt(Encoding.UTF8.GetBytes("Entry"), dek.Span),
            CustomFields = crypto.Encrypt(Encoding.UTF8.GetBytes(cfJson), dek.Span),
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        };

        await using var ms = new MemoryStream();
        await VaultImportExportHelper.WriteCsvToStreamAsync([secret], crypto, dek, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        await reader.ReadLineAsync(TestContext.Current.CancellationToken); // skip the header
        var dataLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(dataLine);
        Assert.Contains("シークレットキー", dataLine);
        Assert.DoesNotContain("\\u30B7", dataLine);
    }
}

