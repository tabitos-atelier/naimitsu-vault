// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.ViewModels;

/// <summary>
/// Pure logic used for import/export. Separated from the VM to make it testable.
/// </summary>
internal static class VaultImportExportHelper
{
    internal const string CsvHeaderLine =
        "Title,Category,UserId,Password,Website,Email,Notes,CustomFields,CreatedAt,UpdatedAt,IsFavorite,ExpiresAt";

    // ── JSON export ────────────────────────────────────────────────────

    /// <summary>
    /// Writes JSON to a stream (internal static overload for testing).
    /// Accepts a MemoryStream or similar, removing any dependency on the file system.
    /// </summary>
    /// <returns>The count of secrets actually written, the count skipped because a field failed to decrypt, and the SecretIds of the skipped records.</returns>
    internal static async Task<(int written, int skipped, List<int> failedIds)> WriteJsonToStreamAsync(
        IReadOnlyList<Secret> secrets,
        ICryptoService crypto,
        DekScope key,
        Stream stream,
        bool includeTotp = false)
    {
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            // Default encoder escapes non-ASCII as \uXXXX (HTML-safe). This is a plaintext
            // export the user reads directly, not embedded in HTML, so keep CJK characters readable.
            Encoder  = FieldCrypto.FullUnicodeEncoder,
        });

        int written = 0, skipped = 0;
        var failedIds = new List<int>();
        writer.WriteStartArray();
        foreach (var s in secrets)
        {
            // FieldCrypto.Open throws (rather than returning null) on a genuine decrypt failure.
            // Decrypt every field for this record before writing anything, so one bad record is
            // skipped cleanly instead of either aborting the entire export (leaving zero records
            // written, including the ones that were fine) or leaving a half-written JSON object
            // in the output stream.
            var fields = new SecurePlaintext?[8];
            try
            {
                fields[0] = FieldCrypto.Open(s.Title,    crypto, key);
                fields[1] = FieldCrypto.Open(s.UserId,   crypto, key);
                fields[2] = FieldCrypto.Open(s.Password, crypto, key);
                fields[3] = FieldCrypto.Open(s.Website,  crypto, key);
                fields[4] = FieldCrypto.Open(s.Email, crypto, key);
                fields[5] = FieldCrypto.Open(s.Notes,    crypto, key);
                fields[6] = includeTotp ? FieldCrypto.Open(s.TotpSecret, crypto, key) : null;
                fields[7] = FieldCrypto.Open(s.CustomFields, crypto, key);
            }
            catch (CryptographicException)
            {
                foreach (var f in fields) f?.Dispose();
                skipped++;
                failedIds.Add(s.Id);
                continue;
            }
            try
            {
                var (titleSp, userSp, passSp, webSp, emailSp, noteSp, totpSp, cfSp) =
                    (fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], fields[7]);

                writer.WriteStartObject();

                if (titleSp != null) writer.WriteString("title", titleSp.Utf8);
                else                 writer.WriteString("title", "");
                if (s.CategoryNum.HasValue) writer.WriteNumber("category", s.CategoryNum.Value);
                else                        writer.WriteNull("category");
                WriteUtf8OrNull(writer, "userId",   userSp);
                WriteUtf8OrNull(writer, "password", passSp);
                WriteUtf8OrNull(writer, "website",  webSp);
                WriteUtf8OrNull(writer, "email", emailSp);
                WriteUtf8OrNull(writer, "notes",    noteSp);

                writer.WritePropertyName("customFields");
                JsonNode? cfNode = null;
                if (cfSp != null) { try { cfNode = JsonNode.Parse(cfSp.Utf8); } catch { } }
                if (cfNode != null) cfNode.WriteTo(writer);
                else writer.WriteNullValue();

                writer.WriteString("createdAt", s.CreatedAt.ToLocalTime());
                writer.WriteString("updatedAt", s.UpdatedAt.ToLocalTime());
                writer.WriteBoolean("isFavorite", s.IsFavorite);
                if (s.ExpiresAt.HasValue) writer.WriteString("expiresAt", s.ExpiresAt.Value.ToLocalTime());
                else                      writer.WriteNull("expiresAt");

                if (includeTotp) WriteTotpFieldsOrNull(writer, totpSp);

                writer.WriteEndObject();
                await writer.FlushAsync();
                written++;
            }
            finally
            {
                foreach (var f in fields) f?.Dispose();
            }
        }
        writer.WriteEndArray();
        await writer.FlushAsync();
        return (written, skipped, failedIds);
    }

    // Writes UTF-8 bytes directly as a JSON string value. Writes JSON null when null.
    private static void WriteUtf8OrNull(Utf8JsonWriter writer, string name, SecurePlaintext? sp)
    {
        if (sp != null) writer.WriteString(name, sp.Utf8);
        else            writer.WriteNull(name);
    }

    // Secret.TotpSecret's decrypted payload is packed as "{Secret}{sep}{Digits}{sep}{Period}{sep}{Algorithm}"
    // (TotpCalculator.Pack). Unpacks it back to the plain Base32 secret plus non-default parameters,
    // rather than exporting the internal packed form verbatim.
    private static void WriteTotpFieldsOrNull(Utf8JsonWriter writer, SecurePlaintext? sp)
    {
        if (sp == null || !TotpCalculator.TryUnpackUtf8(sp.Utf8, out var secretUtf8, out var digits, out var period, out var algorithm))
        {
            writer.WriteNull("totpSecret");
            return;
        }
        writer.WriteString("totpSecret", secretUtf8);
        if (digits    != 6)                              writer.WriteNumber("totpDigits", digits);
        if (period    != 30)                              writer.WriteNumber("totpPeriod", period);
        if (algorithm != TotpCalculator.DefaultAlgorithm) writer.WriteString("totpAlgorithm", algorithm);
    }

    // ── CSV export ────────────────────────────────────────────────────

    /// <summary>
    /// Writes CSV to a stream (internal static overload for testing).
    /// Accepts a MemoryStream or similar, removing any dependency on the file system.
    /// Never uses Encoding.UTF8.GetString / string.Join; writes sensitive fields directly
    /// to the StreamWriter as POH-pinned char[] (no lingering string copies).
    /// </summary>
    /// <returns>The count of secrets actually written, the count skipped because a field failed to decrypt, the SecretIds of the skipped records, and the (SecretId, Title) of records that needed the CSV formula-injection guard applied to at least one field.</returns>
    internal static async Task<(int written, int skipped, List<int> failedIds, List<(int Id, string Title)> guardedRecords)> WriteCsvToStreamAsync(
        IReadOnlyList<Secret> secrets,
        ICryptoService crypto,
        DekScope key,
        Stream stream)
    {
        await using var sw = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            leaveOpen: true);
        await sw.WriteLineAsync(CsvHeaderLine);

        int written = 0, skipped = 0;
        var failedIds = new List<int>();
        var guardedRecords = new List<(int Id, string Title)>();
        foreach (var s in secrets)
        {
            // FieldCrypto.Open throws (rather than returning null) on a genuine decrypt failure.
            // Decrypt every field for this record before writing anything, so one bad record is
            // skipped cleanly instead of either aborting the entire export (leaving zero rows
            // written, including the ones that were fine) or leaving a truncated CSV row behind.
            var fields = new SecurePlaintext?[7];
            try
            {
                fields[0] = FieldCrypto.Open(s.Title,       crypto, key);
                fields[1] = FieldCrypto.Open(s.UserId,      crypto, key);
                fields[2] = FieldCrypto.Open(s.Password,    crypto, key);
                fields[3] = FieldCrypto.Open(s.Website,     crypto, key);
                fields[4] = FieldCrypto.Open(s.Email,       crypto, key);
                fields[5] = FieldCrypto.Open(s.Notes,       crypto, key);
                fields[6] = FieldCrypto.Open(s.CustomFields, crypto, key);
            }
            catch (CryptographicException)
            {
                foreach (var f in fields) f?.Dispose();
                skipped++;
                failedIds.Add(s.Id);
                continue;
            }
            try
            {
                var (titleSp, userSp, passSp, webSp, emailSp, noteSp, cfSp) =
                    (fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6]);

                bool guarded = false;
                guarded |= await WriteSecureCsvFieldAsync(sw, titleSp);
                await sw.WriteAsync(',');
                if (s.CategoryNum.HasValue) await sw.WriteAsync(s.CategoryNum.Value.ToString());
                await sw.WriteAsync(',');
                guarded |= await WriteSecureCsvFieldAsync(sw, userSp);
                await sw.WriteAsync(',');
                guarded |= await WriteSecureCsvFieldAsync(sw, passSp);
                await sw.WriteAsync(',');
                guarded |= await WriteSecureCsvFieldAsync(sw, webSp);
                await sw.WriteAsync(',');
                guarded |= await WriteSecureCsvFieldAsync(sw, emailSp);
                await sw.WriteAsync(',');
                guarded |= await WriteSecureCsvFieldAsync(sw, noteSp);
                await sw.WriteAsync(',');
                using (var relaxedCf = RelaxCustomFieldsEscaping(cfSp))
                    guarded |= await WriteSecureCsvFieldAsync(sw, relaxedCf);
                await sw.WriteAsync(',');
                await sw.WriteAsync(s.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                await sw.WriteAsync(',');
                await sw.WriteAsync(s.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                await sw.WriteAsync(',');
                await sw.WriteAsync(s.IsFavorite ? "true" : "false");
                await sw.WriteAsync(',');
                if (s.ExpiresAt.HasValue) await sw.WriteAsync(s.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd"));
                await sw.WriteLineAsync();
                written++;
                // Title is required/non-null (unlike the other 6 guarded fields), so it's always
                // safe to snapshot here for the audit log - this is the one place in the whole
                // method where a plain (non-pinned) string copy of a decrypted field is made, kept
                // deliberately narrow (only when the guard actually fired) and consistent with how
                // every other audit payload elsewhere in the app captures a title snapshot.
                if (guarded && titleSp != null)
                {
                    var titleSnapshot = Encoding.UTF8.GetString(titleSp.Utf8);
                    guardedRecords.Add((s.Id, titleSnapshot));
                }
            }
            finally
            {
                foreach (var f in fields) f?.Dispose();
            }
        }
        await sw.FlushAsync();
        return (written, skipped, failedIds, guardedRecords);
    }

    // CustomFields is stored as JSON serialized with the default encoder (\uXXXX-escaped non-ASCII;
    // see SecretListJsonContext). The JSON export path re-serializes it via JsonNode with a relaxed
    // encoder before writing (see WriteJsonToStreamAsync above); do the same here so the CSV column
    // isn't full of \uXXXX escapes. Returns null (writes an empty CSV field) if cfSp is null/malformed.
    private static SecurePlaintext? RelaxCustomFieldsEscaping(SecurePlaintext? cfSp)
    {
        if (cfSp == null) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(cfSp.Utf8); }
        catch (JsonException) { return null; }
        if (node == null) return null;

        var json = node.ToJsonString(new JsonSerializerOptions { Encoder = FieldCrypto.FullUnicodeEncoder });
        try
        {
            var byteCount = Encoding.UTF8.GetByteCount(json);
            var sp = SecurePlaintext.Allocate(byteCount, out var span);
            Encoding.UTF8.GetBytes(json, span);
            return sp;
        }
        finally
        {
            SecurePasswordHelper.ZeroStringInternals(json);
        }
    }

    // CSV/spreadsheet formula injection guard (OWASP): a field whose content begins with one of
    // these characters is interpreted as a formula by Excel/Sheets/LibreOffice when the exported
    // file is later opened, not as literal text - e.g. a Title of "=HYPERLINK(...)" set through the
    // plaintext import feature could exfiltrate data or run a command when the re-exported CSV is
    // opened. This is unrelated to SQL injection (the DB layer never sees these values as SQL; every
    // field is AES-256-GCM-encrypted before it's part of an EF Core entity) - it only affects the
    // plaintext CSV file itself once opened in spreadsheet software.
    private static bool IsFormulaTriggerChar(char c) => c is '=' or '+' or '-' or '@';

    /// <summary>
    /// Copies the content of a SecurePlaintext into a POH-pinned char[] and writes it directly
    /// to the StreamWriter while applying CSV escaping. Never creates an intermediate string.
    /// Writes nothing if null or empty (renders as an empty CSV field).
    /// </summary>
    /// <returns>True if the formula-injection guard (leading apostrophe) was applied to this field.</returns>
    private static async Task<bool> WriteSecureCsvFieldAsync(StreamWriter sw, SecurePlaintext? sp)
    {
        if (sp == null) return false;
        var utf8 = sp.Utf8; // Consume the span before the await
        int charCount = Encoding.UTF8.GetCharCount(utf8);
        if (charCount == 0) return false;

        var chars = GC.AllocateArray<char>(charCount, pinned: true);
        try
        {
            Encoding.UTF8.GetChars(utf8, chars); // Decoding completes before the await

            // A leading apostrophe is the standard "force text, don't evaluate" prefix spreadsheet
            // apps recognize. It must land INSIDE any CSV quoting below (right after the opening
            // `"`), not written ahead of it as a separate literal - a bare `'"..."` would no longer
            // parse as a quoted CSV field at all (the field would start at `'`, not `"`), silently
            // undoing the comma/quote/newline escaping for any value that needs both guards at once.
            bool needsFormulaGuard = IsFormulaTriggerChar(chars[0]);

            bool needsEscape = needsFormulaGuard;
            for (int i = 0; !needsEscape && i < charCount; i++)
            {
                char c = chars[i];
                if (c == ',' || c == '"' || c == '\n' || c == '\r') needsEscape = true;
            }

            if (!needsEscape)
            {
                await sw.WriteAsync(chars.AsMemory(0, charCount));
                return false;
            }

            // needsEscape is true here either because the value itself needs CSV quoting
            // (comma/quote/newline present) or purely to carry the formula-guard apostrophe (or
            // both) - either way, wrapping in quotes via the shared path below handles it uniformly.
            int extraQuotes = 0;
            for (int i = 0; i < charCount; i++)
                if (chars[i] == '"') extraQuotes++;

            var escaped = GC.AllocateArray<char>(charCount + 3 + extraQuotes, pinned: true);
            try
            {
                int pos = 0;
                escaped[pos++] = '"';
                if (needsFormulaGuard) escaped[pos++] = '\'';
                for (int i = 0; i < charCount; i++)
                {
                    escaped[pos++] = chars[i];
                    if (chars[i] == '"') escaped[pos++] = '"';
                }
                escaped[pos++] = '"';
                await sw.WriteAsync(escaped.AsMemory(0, pos));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(escaped.AsSpan()));
            }
            return needsFormulaGuard;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
        }
    }

    // ── CSV import ────────────────────────────────────────────────────

    /// <summary>
    /// Parses CSV one logical line at a time (joining physical lines split by a newline
    /// inside quotes) and yields the field list. This is the single loop shared by both the
    /// production <see cref="NaimitsuVault.ViewModels.VaultOperationsViewModel"/> and the
    /// test-only <see cref="ParseAndCountCsvAsync"/>.
    /// The joined raw text (which may contain sensitive fields) is zero-cleared immediately after yielding.
    /// The caller is responsible for zero-clearing each element of the returned fields after use.
    /// </summary>
    internal static async IAsyncEnumerable<List<string>> ParseCsvRecordsAsync(TextReader reader)
    {
        await reader.ReadLineAsync(); // Skip the header line
        string? accumulated = null;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                accumulated = accumulated == null ? line : accumulated + "\n" + line;
                if (!IsCsvRecordComplete(accumulated)) continue;

                var fields = ParseCsvLine(accumulated);
                if (accumulated is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(accumulated);
                accumulated = null;
                yield return fields;
            }
        }
        finally
        {
            if (accumulated is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(accumulated);
        }
    }

    /// <summary>
    /// True when every column of a parsed CSV record is empty or whitespace-only: a blank line, or a
    /// delimiter-only line such as ",,,,,,,,,,," that editors and spreadsheet apps append at the end of a
    /// file. Such a record carries no data, so it is ignored instead of being imported as an empty secret.
    /// A row that merely has an empty Title but a value in any other column is NOT blank and is imported.
    /// </summary>
    internal static bool IsBlankCsvRecord(List<string> fields)
    {
        foreach (var f in fields)
            if (!string.IsNullOrWhiteSpace(f)) return false;
        return true;
    }

    /// <summary>
    /// Parses a CSV stream and returns the count of importable rows (every non-blank row, including one
    /// whose Title is empty), plus the ignored blank rows. Performs no DB insertion; exposed as pure logic
    /// for testing.
    /// </summary>
    internal static async Task<(int total, int skipped)> ParseAndCountCsvAsync(
        TextReader reader, HashSet<int> validCodes)
    {
        int total = 0, skipped = 0;
        await foreach (var fields in ParseCsvRecordsAsync(reader))
        {
            if (fields.Count < 1 || IsBlankCsvRecord(fields)) { skipped++; continue; }
            var title = fields[0].Trim();
            int? catRaw = fields.Count > 1 && int.TryParse(fields[1], out var c) ? c : (int?)null;
            MapCategory(catRaw, validCodes); // Resolve category (includes fallback to 0/uncategorized)
            total++;

            if (title.Length > 0) SecurePasswordHelper.ZeroStringInternals(title);
            foreach (var f in fields)
                if (f is { Length: > 0 }) SecurePasswordHelper.ZeroStringInternals(f);
        }
        return (total, skipped);
    }

    // An even number of quotes (") means the record is complete; odd means a newline inside quotes continues.
    internal static bool IsCsvRecordComplete(string line)
    {
        int quotes = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '"') continue;
            if (i + 1 < line.Length && line[i + 1] == '"') i++; // Skip "" escape
            else quotes++;
        }
        return quotes % 2 == 0;
    }

    // Parses one line of CSV text into a field list (supports quoting/escaping).
    internal static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuote)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuote = false;
                }
                else sb.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"':  inQuote = true; break;
                    case ',':  fields.Add(sb.ToString()); sb.Clear(); break;
                    case '\r': break;
                    default:   sb.Append(c); break;
                }
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    // ── Common helpers ──────────────────────────────────────────────────────

    internal static Secret BuildSecret(
        ICryptoService crypto,
        string title, int? categoryNum,
        string? userId, string? password, string? website, string? email,
        string? notes, string? customFieldsJson,
        DateTime? createdAt, DateTime? updatedAt, DekScope key,
        bool isFavorite = false, DateTime? expiresAt = null, string? totpSecret = null,
        int totpDigits = 6, int totpPeriod = 30, string totpAlgorithm = TotpCalculator.DefaultAlgorithm)
    {
        var normalizedCf = NormalizeCustomFieldIds(customFieldsJson);
        var normalizedNotes = NormalizeNotesLineEndings(notes);
        try
        {
            return new Secret
            {
                Title         = FieldCrypto.Seal(title, crypto, key),
                CategoryNum   = categoryNum,
                UserId        = userId          is { Length: > 0 } ? FieldCrypto.Seal(userId,          crypto, key) : null,
                Password      = password        is { Length: > 0 } ? FieldCrypto.Seal(password,        crypto, key) : null,
                Website       = website         is { Length: > 0 } ? FieldCrypto.Seal(website,         crypto, key) : null,
                Email         = email        is { Length: > 0 } ? FieldCrypto.Seal(email,        crypto, key) : null,
                Notes         = normalizedNotes is { Length: > 0 } ? FieldCrypto.Seal(normalizedNotes, crypto, key) : null,
                CustomFields  = normalizedCf    is { Length: > 0 } ? FieldCrypto.Seal(normalizedCf,    crypto, key) : null,
                TotpSecret    = totpSecret   is { Length: > 0 }
                    ? FieldCrypto.Seal(TotpCalculator.Pack(new TotpCalculator.TotpConfig(totpSecret, totpDigits, totpPeriod, totpAlgorithm)), crypto, key)
                    : null,
                CreatedAt     = (createdAt ?? DateTime.UtcNow).ToUniversalTime(),
                UpdatedAt     = (updatedAt ?? DateTime.UtcNow).ToUniversalTime(),
                IsFavorite    = isFavorite,
                ExpiresAt     = expiresAt?.ToUniversalTime(),
                GeneratorSymbols = PasswordGenerator.DefaultSymbols,
            };
        }
        finally
        {
            // NormalizeCustomFieldIds may have allocated a new plaintext JSON string distinct from
            // the caller's customFieldsJson (which the caller zero-clears itself) - wipe it here too.
            if (normalizedCf is { Length: > 0 } && !ReferenceEquals(normalizedCf, customFieldsJson))
                SecurePasswordHelper.ZeroStringInternals(normalizedCf);
            // Same for NormalizeNotesLineEndings - it allocates a new string only when it actually
            // had to rewrite line endings; the caller zero-clears its own `notes` separately.
            if (normalizedNotes is { Length: > 0 } && !ReferenceEquals(normalizedNotes, notes))
                SecurePasswordHelper.ZeroStringInternals(normalizedNotes);
        }
    }

    /// <summary>
    /// Collapses CR and CRLF to LF, mirroring SecretEditModel.Notes's setter so imported Notes text
    /// matches the normalized form the app always produces when a user types it via the UI. Without
    /// this, an imported value that still contains \r (e.g. CRLF from a Windows-authored JSON file)
    /// is stored as the Gen0 baseline verbatim; WinUI 3's multiline TextBox then normalizes it to its
    /// own bare-CR internal representation the moment it's displayed, so merely viewing the item and
    /// moving focus away reads back a value that no longer matches Gen0 byte-for-byte and gets
    /// autosaved as a spurious "draft" - even though the user never edited anything.
    /// </summary>
    private static string? NormalizeNotesLineEndings(string? notes)
    {
        if (string.IsNullOrEmpty(notes) || notes.IndexOf('\r') < 0) return notes;
        var normalized = GC.AllocateArray<char>(notes.Length, pinned: true);
        try
        {
            int len = SecretEditModel.NormalizeLineEndings(notes, normalized);
            return new string(normalized, 0, len);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(normalized.AsSpan()));
        }
    }

    /// <summary>
    /// Assigns a FieldId to every custom field entry that arrives with FieldId=0 (missing or explicitly
    /// zero in the import source), so an imported record's custom fields never collide with each other
    /// or diverge from the FieldId TimeMachineViewModel.CollectCustomFieldKeys later assigns in memory
    /// when re-loading the same record for editing - a mismatch there is what makes one field render as
    /// two rows in the Time Machine compare view (FieldId=0 entries are matched by label there, FieldId>0
    /// ones by id). Malformed/non-array JSON is returned unchanged and left for downstream parsing to reject.
    /// </summary>
    internal static string? NormalizeCustomFieldIds(string? customFieldsJson)
    {
        if (string.IsNullOrEmpty(customFieldsJson)) return customFieldsJson;
        try
        {
            if (JsonNode.Parse(customFieldsJson) is not JsonArray arr) return customFieldsJson;

            int nextId = 0;
            foreach (var item in arr)
                if (item is JsonObject obj
                    && obj.TryGetPropertyValue("FieldId", out var idNode)
                    && idNode != null && idNode.GetValueKind() == JsonValueKind.Number
                    && idNode.GetValue<int>() > nextId)
                    nextId = idNode.GetValue<int>();

            bool changed = false;
            foreach (var item in arr)
            {
                if (item is not JsonObject obj) continue;
                bool hasId = obj.TryGetPropertyValue("FieldId", out var idNode)
                    && idNode != null && idNode.GetValueKind() == JsonValueKind.Number && idNode.GetValue<int>() != 0;
                if (!hasId)
                {
                    obj["FieldId"] = ++nextId;
                    changed = true;
                }
            }
            return changed ? arr.ToJsonString() : customFieldsJson;
        }
        catch (JsonException)
        {
            return customFieldsJson;
        }
    }

    internal static (int? code, bool wasMapped) MapCategory(int? code, HashSet<int> validCodes)
    {
        if (code == null) return (null, false);
        return validCodes.Contains(code.Value) ? (code, false) : (0, true);
    }

    internal static string? NullIfEmpty(string s) =>
        string.IsNullOrEmpty(s) ? null : s;

    internal static string ResolveDuplicateTitle(string title, HashSet<string> existingTitles)
    {
        if (existingTitles.Add(title)) return title;
        var importTag = LocalizationManager.Get("Common.Import");
        var candidate = $"{title} ({importTag})";
        if (existingTitles.Add(candidate)) return candidate;
        for (int n = 2; ; n++)
        {
            candidate = $"{title} ({importTag}{n})";
            if (existingTitles.Add(candidate)) return candidate;
        }
    }
}
