// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.ViewModels;

/// <summary>
/// Snapshot JSON serializer for SecretHistory slots.
/// Operates directly on Utf8JsonWriter/Reader and never goes through the SecretSnapshot record.
/// Sensitive fields (Password/Notes/TotpSecret) are read/written directly via
/// SecurePlaintext.Utf8 / SecureCharBuffer.Span / CopyString(Span&lt;char&gt;) without ever creating a string.
///
/// JSON keys are PascalCase (originally matching the property names of the now-removed
/// SecretSnapshot record, and realigned with Secret's CreatedAt/Website rename in the pre-v1.0
/// schema pass since no released version depends on the old spelling). Must not be changed
/// casually after this, to preserve backward compatibility with existing encrypted slots.
/// </summary>
internal static class SnapshotSerializer
{
    // ── PascalCase keys (compatible with the existing SecretSnapshot) ─────────────────
    private static ReadOnlySpan<byte> FTimestamp      => "Timestamp"u8;
    private static ReadOnlySpan<byte> FTitle          => "Title"u8;
    private static ReadOnlySpan<byte> FUserId         => "UserId"u8;
    private static ReadOnlySpan<byte> FPassword       => "Password"u8;
    private static ReadOnlySpan<byte> FWebsite        => "Website"u8;
    private static ReadOnlySpan<byte> FEmail          => "Email"u8;
    private static ReadOnlySpan<byte> FNotes          => "Notes"u8;
    private static ReadOnlySpan<byte> FCustomFields   => "CustomFields"u8;
    private static ReadOnlySpan<byte> FExpiresAt      => "ExpiresAt"u8;
    private static ReadOnlySpan<byte> FTotpSecret     => "TotpSecret"u8;
    private static ReadOnlySpan<byte> FLabelOverrides => "LabelOverrides"u8;
    private static ReadOnlySpan<byte> FFileIds        => "FileIds"u8;
    private static ReadOnlySpan<byte> FSecretId       => "SecretId"u8;
    private static ReadOnlySpan<byte> FCategoryNum    => "CategoryNum"u8;
    private static ReadOnlySpan<byte> FIsFavorite     => "IsFavorite"u8;
    private static ReadOnlySpan<byte> FCreatedAt      => "CreatedAt"u8;
    private static ReadOnlySpan<byte> FGenSymbols     => "GenSymbols"u8;

    /// <summary>
    /// Writes a Secret entity to a Utf8JsonWriter.
    /// Encrypted fields are written via FieldCrypto.Open → SecurePlaintext.Utf8 span (no string is created).
    /// Callers must attach the writer to a MemoryStream and call <see cref="ICryptoService.Encrypt"/> after Flush/GetBuffer.
    /// </summary>
    internal static void WriteEntity(Utf8JsonWriter w, Secret entity, ICryptoService crypto, DekScope dek, int[] fileIds)
    {
        w.WriteStartObject();

        w.WriteString(FTimestamp, entity.UpdatedAt.ToString("o"));
        WriteDecrypted(w, FTitle,          entity.Title,          crypto, dek);
        WriteDecrypted(w, FUserId,         entity.UserId,         crypto, dek);
        WriteDecrypted(w, FPassword,       entity.Password,       crypto, dek);
        WriteDecrypted(w, FWebsite,        entity.Website,        crypto, dek);
        WriteDecrypted(w, FEmail,       entity.Email,          crypto, dek);
        WriteDecrypted(w, FNotes,          entity.Notes,          crypto, dek);
        WriteDecrypted(w, FCustomFields,   entity.CustomFields,   crypto, dek);
        // ExpiresAt is a date-only concept in the UI (CalendarDatePicker has no time-of-day input), so
        // normalize to local midnight here exactly like BuildEditModelFromEntityAsync/WriteEditModel
        // do for the edit-model side. Writing the raw stored instant instead would only coincidentally
        // match the edit-model's reconstructed value when the stored value already happens to be
        // local-midnight-in-UTC; any entity whose ExpiresAt carries a different time-of-day component
        // (e.g. legacy data written before this normalization was applied consistently) would then
        // permanently fail the no-op self-heal comparison and stay stuck showing a phantom draft the
        // next time ANY save is triggered on that item, even one where nothing meaningful changed.
        if (entity.ExpiresAt.HasValue)
        {
            var localMidnightUtc = new DateTimeOffset(entity.ExpiresAt.Value.ToLocalTime().Date).UtcDateTime;
            w.WriteString(FExpiresAt, localMidnightUtc.ToString("o"));
        }
        else w.WriteNull(FExpiresAt);
        WriteDecrypted(w, FTotpSecret,     entity.TotpSecret,     crypto, dek);
        WriteDecrypted(w, FLabelOverrides, entity.LabelOverrides, crypto, dek);

        w.WriteStartArray(FFileIds);
        foreach (var id in fileIds) w.WriteNumberValue(id);
        w.WriteEndArray();

        w.WriteNumber(FSecretId, entity.Id);
        if (entity.CategoryNum.HasValue) w.WriteNumber(FCategoryNum, entity.CategoryNum.Value);
        else                             w.WriteNull(FCategoryNum);
        w.WriteBoolean(FIsFavorite, entity.IsFavorite);
        w.WriteString(FCreatedAt, entity.CreatedAt.ToString("o"));
        if (entity.GeneratorSymbols != null) w.WriteString(FGenSymbols, entity.GeneratorSymbols);
        else                           w.WriteNull(FGenSymbols);

        w.WriteEndObject();
    }

    /// <summary>
    /// Writes the current state of a SecretEditModel to a Utf8JsonWriter.
    /// Password/Notes/TotpSecret are written directly from SecureCharBuffer.Span (no string is created).
    /// cfJson/loJson are plaintext JSON strings assembled by the caller (may be null).
    /// </summary>
    internal static void WriteEditModel(
        Utf8JsonWriter w, SecretEditModel em, ReadOnlySpan<char> cfJson, ReadOnlySpan<char> loJson)
    {
        w.WriteStartObject();

        w.WriteString(FTimestamp, DateTime.UtcNow.ToString("o"));
        WriteSpanOrNull(w, FTitle,          em.TitleBuf.Span);
        WriteSpanOrNull(w, FUserId,         em.UserIdBuf.Span);
        WriteSpanOrNull(w, FPassword,       em.PasswordBuf.Span);
        WriteSpanOrNull(w, FWebsite,        em.WebsiteBuf.Span);
        WriteSpanOrNull(w, FEmail,       em.EmailBuf.Span);
        WriteSpanOrNull(w, FNotes,          em.NotesBuf.Span);
        WriteSpanOrNull(w, FCustomFields,   cfJson);
        if (em.ExpiresAt.HasValue) w.WriteString(FExpiresAt, em.ExpiresAt.Value.UtcDateTime.ToString("o"));
        else                       w.WriteNull(FExpiresAt);
        WriteSpanOrNull(w, FTotpSecret,     em.TotpSecretBuf.Span);
        WriteSpanOrNull(w, FLabelOverrides, loJson);

        w.WriteStartArray(FFileIds);
        foreach (var img in em.AttachedFiles) w.WriteNumberValue(img.Id);
        w.WriteEndArray();

        w.WriteNumber(FSecretId, em.Id);
        if (em.CategoryNum.HasValue) w.WriteNumber(FCategoryNum, em.CategoryNum.Value);
        else                         w.WriteNull(FCategoryNum);
        w.WriteBoolean(FIsFavorite, em.IsFavorite);
        if (em.CreatedAt != default) w.WriteString(FCreatedAt, em.CreatedAt.ToUniversalTime().ToString("o"));
        else                         w.WriteNull(FCreatedAt);
        WriteSpanOrNull(w, FGenSymbols, em.GenSymbols.AsSpan());

        w.WriteEndObject();
    }

    /// <summary>
    /// Writes the current state of a HistorySlotContent to a Utf8JsonWriter (the SlotContent
    /// counterpart of WriteEditModel). Used when only FileIds changes and the whole list
    /// needs to be re-encrypted and overwritten into the draft.
    /// SecureCharBuffer.Span is a plaintext char span, so it can be written as-is via WriteSpanOrNull.
    /// </summary>
    internal static void WriteFromSlot(Utf8JsonWriter w, HistorySlotContent slot)
    {
        w.WriteStartObject();

        WriteSpanOrNull(w, FTimestamp,      slot.TimestampBuf.Span);
        WriteSpanOrNull(w, FTitle,          slot.TitleBuf.Span);
        WriteSpanOrNull(w, FUserId,         slot.UserIdBuf.Span);
        WriteSpanOrNull(w, FPassword,       slot.PasswordBuf.Span);
        WriteSpanOrNull(w, FWebsite,        slot.WebsiteBuf.Span);
        WriteSpanOrNull(w, FEmail,       slot.EmailBuf.Span);
        WriteSpanOrNull(w, FNotes,          slot.NotesBuf.Span);
        WriteSpanOrNull(w, FCustomFields,   slot.CustomFieldsBuf.Span);
        WriteSpanOrNull(w, FExpiresAt,      slot.ExpiresAtBuf.Span);
        WriteSpanOrNull(w, FTotpSecret,     slot.TotpSecretBuf.Span);
        WriteSpanOrNull(w, FLabelOverrides, slot.LabelOverridesBuf.Span);

        w.WriteStartArray(FFileIds);
        if (slot.FileIds != null)
            foreach (var id in slot.FileIds) w.WriteNumberValue(id);
        w.WriteEndArray();

        w.WriteNumber(FSecretId, slot.SecretId);
        if (slot.CategoryNum.HasValue) w.WriteNumber(FCategoryNum, slot.CategoryNum.Value);
        else                           w.WriteNull(FCategoryNum);
        w.WriteBoolean(FIsFavorite, slot.IsFavorite);
        WriteSpanOrNull(w, FCreatedAt, slot.CreatedAtBuf.Span);
        WriteSpanOrNull(w, FGenSymbols, slot.GenSymbolsBuf.Span);

        w.WriteEndObject();
    }

    /// <summary>
    /// Reads snapshot JSON into a HistorySlotContent.
    /// Sensitive fields (Password/Notes/TotpSecret) use CopyString(Span&lt;char&gt;) (.NET 8+)
    /// to stream directly into SecureCharBuffer without creating a string.
    /// </summary>
    internal static HistorySlotContent ReadToSlot(ReadOnlySpan<byte> json)
    {
        var slot   = new HistorySlotContent();
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return slot;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if      (reader.ValueTextEquals(FTimestamp))
                { reader.Read(); ReadToBuffer(ref reader, slot.TimestampBuf); }
            else if (reader.ValueTextEquals(FTitle))
                { reader.Read(); ReadToBuffer(ref reader, slot.TitleBuf); }
            else if (reader.ValueTextEquals(FUserId))
                { reader.Read(); ReadToBuffer(ref reader, slot.UserIdBuf); }
            else if (reader.ValueTextEquals(FPassword))
                { reader.Read(); ReadToBuffer(ref reader, slot.PasswordBuf); }
            else if (reader.ValueTextEquals(FWebsite))
                { reader.Read(); ReadToBuffer(ref reader, slot.WebsiteBuf); }
            else if (reader.ValueTextEquals(FEmail))
                { reader.Read(); ReadToBuffer(ref reader, slot.EmailBuf); }
            else if (reader.ValueTextEquals(FNotes))
                { reader.Read(); ReadToBuffer(ref reader, slot.NotesBuf); }
            else if (reader.ValueTextEquals(FCustomFields))
                { reader.Read(); ReadToBuffer(ref reader, slot.CustomFieldsBuf); }
            else if (reader.ValueTextEquals(FExpiresAt))
                { reader.Read(); ReadToBuffer(ref reader, slot.ExpiresAtBuf); }
            else if (reader.ValueTextEquals(FTotpSecret))
                { reader.Read(); ReadToBuffer(ref reader, slot.TotpSecretBuf); }
            else if (reader.ValueTextEquals(FLabelOverrides))
                { reader.Read(); ReadToBuffer(ref reader, slot.LabelOverridesBuf); }
            else if (reader.ValueTextEquals(FFileIds))
            {
                reader.Read(); // StartArray
                var ids = new List<int>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    if (reader.TokenType == JsonTokenType.Number) ids.Add(reader.GetInt32());
                slot.FileIds = ids.Count > 0 ? ids.ToArray() : null;
            }
            else if (reader.ValueTextEquals(FSecretId))
                { reader.Read(); slot.SecretId    = reader.TokenType == JsonTokenType.Null ? 0 : reader.GetInt32(); }
            else if (reader.ValueTextEquals(FCategoryNum))
                { reader.Read(); slot.CategoryNum = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32(); }
            else if (reader.ValueTextEquals(FIsFavorite))
                { reader.Read(); slot.IsFavorite  = reader.GetBoolean(); }
            else if (reader.ValueTextEquals(FCreatedAt))
                { reader.Read(); ReadToBuffer(ref reader, slot.CreatedAtBuf); }
            else if (reader.ValueTextEquals(FGenSymbols))
                { reader.Read(); ReadToBuffer(ref reader, slot.GenSymbolsBuf); }
            else
                { reader.Read(); reader.Skip(); }
        }

        return slot;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private static void WriteDecrypted(
        Utf8JsonWriter w, ReadOnlySpan<byte> name, byte[]? cipher, ICryptoService crypto, DekScope dek)
    {
        using var sp = FieldCrypto.Open(cipher, crypto, dek);
        if (sp != null) w.WriteString(name, sp.Utf8);
        else            w.WriteNull(name);
    }

    private static void WriteSpanOrNull(Utf8JsonWriter w, ReadOnlySpan<byte> name, ReadOnlySpan<char> value)
    {
        if (!value.IsEmpty) w.WriteString(name, value);
        else                w.WriteNull(name);
    }

    /// <summary>
    /// Streams the current string token from a Utf8JsonReader into SecureCharBuffer without going through a string.
    /// Writes directly to an ArrayPool buffer via CopyString(Span&lt;char&gt;) (.NET 8+).
    /// </summary>
    private static void ReadToBuffer(ref Utf8JsonReader reader, SecureCharBuffer buf)
    {
        if (reader.TokenType == JsonTokenType.Null) return;
        int maxLen = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (maxLen <= 512)
        {
            Span<char> stack = stackalloc char[512];
            int n = reader.CopyString(stack);
            buf.SetFromSpan(stack[..n]);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(stack[..n]));
        }
        else
        {
            var pinned = GC.AllocateArray<char>(maxLen, pinned: true);
            try
            {
                int n = reader.CopyString(pinned.AsSpan());
                buf.SetFromSpan(pinned.AsSpan(0, n));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pinned.AsSpan()));
            }
        }
    }
}
