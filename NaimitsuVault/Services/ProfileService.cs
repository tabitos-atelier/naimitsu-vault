// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NaimitsuVault.Helpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// DTO for storing additional profile fields (custom fields).
/// Since the entire profile is encrypted, per-field encryption is unnecessary.
/// </summary>
public record ProfileCustomFieldData(
    string Label = "",
    string Value = "",
    CustomFieldType FieldType = CustomFieldType.Text,
    int FieldId = 0
);

/// <summary>
/// Self-contained snapshot of profile information (ULTIMATE zero-memory design).
/// Holds PII fields as char[]? and guarantees CryptographicOperations.ZeroMemory in Dispose().
/// Both the UserProfile_TwinA (0x1101) and UserProfile_TwinB (0x1102) keys are stored in this format (equivalence principle).
/// </summary>
public sealed class SecureProfileSnapshot : IDisposable
{
    // ─── Non-sensitive metadata — kept as string / value types ───────────────────────────────
    public string?                       Timestamp    { get; init; }
    public string?                       CreatedAt     { get; init; }
    public List<int>?                    FileIds      { get; init; }
    public List<ProfileCustomFieldData>? CustomFields { get; init; }

    // ─── PII fields — ZeroMemory guaranteed via char[]? ────────────────────────
    public char[]? Name               { get; init; }
    public char[]? Nickname           { get; init; }
    public char[]? Email              { get; init; }
    public char[]? PostalCode         { get; init; }
    public char[]? Address1           { get; init; }
    public char[]? Address2           { get; init; }
    public char[]? MobilePhone        { get; init; }
    public char[]? HomePhone          { get; init; }
    public char[]? IdentityItem1      { get; init; }
    public char[]? Id1Expiry          { get; init; }
    public char[]? IdentityItem2      { get; init; }
    public char[]? Id2Expiry          { get; init; }
    public char[]? IdentityItem3      { get; init; }
    public char[]? Id3Expiry          { get; init; }
    public char[]? Notes              { get; init; }

    // ─── Identity item labels — not PII (custom field names, not values), no ZeroMemory needed ───
    public string? IdentityItem1Label { get; init; }
    public string? IdentityItem2Label { get; init; }
    public string? IdentityItem3Label { get; init; }

    private bool _disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            ZeroChars(Name);           ZeroChars(Nickname);       ZeroChars(Email);
            ZeroChars(PostalCode);     ZeroChars(Address1);       ZeroChars(Address2);
            ZeroChars(MobilePhone);    ZeroChars(HomePhone);      ZeroChars(IdentityItem1);
            ZeroChars(Id1Expiry);      ZeroChars(IdentityItem2);  ZeroChars(Id2Expiry);
            ZeroChars(IdentityItem3);  ZeroChars(Id3Expiry);      ZeroChars(Notes);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~SecureProfileSnapshot() => Dispose();

    internal static void ZeroChars(char[]? c)
    {
        if (c != null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(c.AsSpan()));
    }
}

/// <summary>
/// Aggregate DTO representing the ViewModel's in-memory editing state.
/// Used to pass data to and from the service layer.
/// The avatar image is managed exclusively by AvatarService / AppSession.
/// </summary>
public class ProfileEditModel
{
    public static string DefaultLabelIdentityItem1 => LocalizationManager.Get("Profile.IdentityItem1");
    public static string DefaultLabelIdentityItem2 => LocalizationManager.Get("Profile.IdentityItem2");
    public static string DefaultLabelIdentityItem3 => LocalizationManager.Get("Profile.IdentityItem3");

    public string    Name               { get; set; } = "";
    public string    Nickname           { get; set; } = "";
    public string    Email              { get; set; } = "";
    public string    PostalCode         { get; set; } = "";
    public string    Address1           { get; set; } = "";
    public string    Address2           { get; set; } = "";
    public string    MobilePhone        { get; set; } = "";
    public string    HomePhone          { get; set; } = "";
    public string    IdentityItem1      { get; set; } = "";
    public DateTime? Id1Expiry          { get; set; }
    public string    IdentityItem2      { get; set; } = "";
    public DateTime? Id2Expiry          { get; set; }
    public string    IdentityItem3      { get; set; } = "";
    public DateTime? Id3Expiry          { get; set; }
    public string    Notes              { get; set; } = "";
    // Always holds the effective display label (default or custom), mirroring SecretEditModel.LabelUserId etc.
    // Not PII: never ZeroMemory'd.
    public string    IdentityItem1Label { get; set; } = DefaultLabelIdentityItem1;
    public string    IdentityItem2Label { get; set; } = DefaultLabelIdentityItem2;
    public string    IdentityItem3Label { get; set; } = DefaultLabelIdentityItem3;
    public List<ProfileCustomFieldData> CustomFields { get; set; } = [];
    public DateTime? CreatedAt           { get; set; }
    public string?   Timestamp          { get; set; }
    public List<int> FileIds            { get; set; } = [];
}

public static class ProfileEditModelExtensions
{
    /// <summary>
    /// Zeroes the 12 fixed PII string fields of a decrypt-only ProfileEditModel (e.g. a snapshot
    /// loaded solely for prefill/comparison and then discarded). Never call this on a model built
    /// from a ViewModel's own live display properties — those strings may be cached copies a
    /// TextBox is still showing, and zeroing them would blank the visible text.
    /// </summary>
    public static void ZeroPii(this ProfileEditModel model)
    {
        foreach (var s in new[] { model.Name, model.Nickname, model.Email, model.PostalCode, model.Address1, model.Address2,
                                   model.MobilePhone, model.HomePhone, model.IdentityItem1, model.IdentityItem2,
                                   model.IdentityItem3, model.Notes })
            if (s.Length > 0) SecurePasswordHelper.ZeroStringInternals(s);
    }
}

/// <summary>
/// Lightweight DTO holding only the dates (and effective, non-PII labels) needed for expiry checks.
/// Contains no PII fields at all, so it can safely be carried on ShellWindow's async state machine.
/// </summary>
public record ProfileExpiryInfo(
    DateTime? Id1Expiry,
    DateTime? Id2Expiry,
    DateTime? Id3Expiry,
    string IdentityItem1Label,
    string IdentityItem2Label,
    string IdentityItem3Label);

public record DisplayNameChangedMessage(string? DisplayName);

/// <summary>
/// Service responsible for encrypted saving/loading of profile information.
/// </summary>
public class ProfileService(
    IDbContextFactory<AppDbContext> factory,
    ICryptoService crypto,
    ISecurityContext session,
    ILogger<ProfileService> logger)
{
    // ─── Public API ────────────────────────────────────────────────────────────

    /// <summary>Restores the profile, preferring TwinB.</summary>
    public async Task<ProfileEditModel> LoadProfileAsync(DekScope dek)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();

            var twinBRow = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB);
            if (twinBRow != null)
            {
                using var draft = DecryptSnapshot(twinBRow.ConfigValue, dek);
                if (draft != null) return MapToEditModel(draft);
            }

            var row = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA);
            if (row == null) return new ProfileEditModel();

            using var snap = DecryptSnapshot(row.ConfigValue, dek);
            return snap != null ? MapToEditModel(snap) : new ProfileEditModel();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to decrypt the profile. [{ExType}]", ex.GetType().Name);
            return new ProfileEditModel();
        }
    }

    /// <summary>
    /// Immediately encrypts and saves the current editing state to UserProfile_TwinB (0x1102).
    /// Serialization/encryption completes before the first await, preventing PII from being captured in the state machine.
    /// </summary>
    public async Task SaveDraftAsync(ProfileEditModel model, DekScope dek)
    {
        byte[] encrypted;
        {
            using var snap = PackToSecureSnapshot(model);
            encrypted = EncryptSnapshot(snap, dek);
        } // snap is disposed here, which ZeroMemory's the PII char[] fields

        await using var db = await factory.CreateDbContextAsync();
        await UpsertSettingAsync(db, VaultMetadataKey.UserProfile_TwinB, encrypted);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the encrypted blobs for the committed profile and the draft as-is, for the compare dialog.
    /// Decryption/scanning is the caller's responsibility.
    /// </summary>
    public async Task<(byte[]? TwinAEncrypted, byte[]? TwinBEncrypted)> GetCompareRawAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var twinAEnc = await db.Metadata.AsNoTracking()
            .Where(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA)
            .Select(s => s.ConfigValue).FirstOrDefaultAsync();
        var twinBEnc = await db.Metadata.AsNoTracking()
            .Where(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB)
            .Select(s => s.ConfigValue).FirstOrDefaultAsync();
        return (twinAEnc, twinBEnc);
    }

    /// <summary>
    /// Decrypts and retrieves the committed profile and the draft together, for the compare dialog.
    /// The caller must always manage the returned SecureProfileSnapshot with a using statement.
    /// </summary>
    public async Task<(SecureProfileSnapshot TwinA, SecureProfileSnapshot? TwinB)> GetCompareDataAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();

        var twinARow = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA);
        var twinA = twinARow != null ? DecryptSnapshot(twinARow.ConfigValue, dek) : null;

        var twinBRow = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB);
        var twinB = twinBRow != null ? DecryptSnapshot(twinBRow.ConfigValue, dek) : null;

        return (twinA ?? new SecureProfileSnapshot(), twinB);
    }

    /// <summary>Decrypts and returns only the committed profile (TwinA), for the autosave NoOp check.</summary>
    public async Task<SecureProfileSnapshot?> GetTwinASnapshotAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var twinARow = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA);
        return twinARow != null ? DecryptSnapshot(twinARow.ConfigValue, dek) : null;
    }

    /// <summary>
    /// Manual commit save: overwrites UserProfile_TwinA with the latest state and physically deletes UserProfile_TwinB.
    /// </summary>
    public async Task CommitProfileAsync(ProfileEditModel model, DekScope dek)
    {
        byte[] encrypted;
        {
            using var snap = PackToSecureSnapshot(model);
            encrypted = EncryptSnapshot(snap, dek);
        }

        await using var db = await factory.CreateDbContextAsync();
        await UpsertSettingAsync(db, VaultMetadataKey.UserProfile_TwinA, encrypted);
        await db.SaveChangesAsync();

        await db.Metadata
            .Where(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB)
            .ExecuteDeleteAsync();

        logger.LogInformation("Committed the profile and deleted the draft (TwinB).");
    }

    /// <summary>Physically deletes the draft (UserProfile_TwinB) row.</summary>
    public async Task DiscardDraftAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        int deleted = await db.Metadata
            .Where(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB)
            .ExecuteDeleteAsync();
        if (deleted > 0)
            logger.LogInformation("Discarded the profile draft (TwinB).");
    }

    /// <summary>Checks whether a UserProfile_TwinB row exists.</summary>
    public async Task<bool> HasDraftAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Metadata.AsNoTracking().AnyAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB);
    }

    /// <summary>
    /// Initial seeding to call after unlock. Creates an empty profile only if no UserProfile_TwinA row exists.
    /// </summary>
    public async Task SeedEmptyProfileIfAbsentAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        bool exists = await db.Metadata.AnyAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA);
        if (exists) return;

        byte[] encrypted;
        {
            using var empty = PackToSecureSnapshot(new ProfileEditModel());
            encrypted = EncryptSnapshot(empty, dek);
        }
        await UpsertSettingAsync(db, VaultMetadataKey.UserProfile_TwinA, encrypted);
        await db.SaveChangesAsync();
        logger.LogInformation("Created the initial empty profile.");
    }

    /// <summary>
    /// Query dedicated to expiry checks. Returns only 3 DateTime? values without creating a ProfileEditModel containing PII.
    /// The TwinB-first read order is the same as LoadProfileAsync.
    /// </summary>
    public async Task<ProfileExpiryInfo> GetExpiryInfoAsync(DekScope dek)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();

            var twinBRow = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB);
            if (twinBRow != null)
            {
                using var draft = DecryptSnapshot(twinBRow.ConfigValue, dek);
                if (draft != null) return ToExpiryInfo(draft);
            }

            var row = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA);
            if (row == null) return DefaultExpiryInfo();

            using var snap = DecryptSnapshot(row.ConfigValue, dek);
            return snap != null ? ToExpiryInfo(snap) : DefaultExpiryInfo();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to retrieve profile expiry information. [{ExType}]", ex.GetType().Name);
            return DefaultExpiryInfo();
        }
    }

    private static ProfileExpiryInfo DefaultExpiryInfo() => new(
        null, null, null,
        ProfileEditModel.DefaultLabelIdentityItem1, ProfileEditModel.DefaultLabelIdentityItem2, ProfileEditModel.DefaultLabelIdentityItem3);

    private static ProfileExpiryInfo ToExpiryInfo(SecureProfileSnapshot snap) => new(
        ParseIso(snap.Id1Expiry), ParseIso(snap.Id2Expiry), ParseIso(snap.Id3Expiry),
        string.IsNullOrEmpty(snap.IdentityItem1Label) ? ProfileEditModel.DefaultLabelIdentityItem1 : snap.IdentityItem1Label,
        string.IsNullOrEmpty(snap.IdentityItem2Label) ? ProfileEditModel.DefaultLabelIdentityItem2 : snap.IdentityItem2Label,
        string.IsNullOrEmpty(snap.IdentityItem3Label) ? ProfileEditModel.DefaultLabelIdentityItem3 : snap.IdentityItem3Label);

    // ─── API for the navigation panel, called by ShellWindow ──────────────────

    /// <summary>
    /// Extracts only the navigation panel display name in a targeted way.
    /// Instead of decrypting the entire profile into memory, decrypts directly into a pinned buffer
    /// and then scans only the Name / Nickname fields with Utf8JsonReader.
    /// </summary>
    public async Task<string?> LoadDisplayNameAsync()
    {
        try
        {
            var dek = session.GetKey();
            await using var db = await factory.CreateDbContextAsync();

            var twinBBlob = await db.Metadata.AsNoTracking()
                .Where(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinB)
                .Select(s => s.ConfigValue).FirstOrDefaultAsync();
            if (twinBBlob != null) return ExtractDisplayName(twinBBlob, dek);

            var twinABlob = await db.Metadata.AsNoTracking()
                .Where(s => s.ConfigKey == VaultMetadataKey.UserProfile_TwinA)
                .Select(s => s.ConfigValue).FirstOrDefaultAsync();
            return twinABlob != null ? ExtractDisplayName(twinABlob, dek) : null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Internal helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts the encrypted blob directly into a pinned buffer and hand-parses it with Utf8JsonReader
    /// to build a SecureProfileSnapshot. Never uses JsonSerializer.
    /// The caller must always manage the returned SecureProfileSnapshot with a using statement.
    /// </summary>
    internal SecureProfileSnapshot? DecryptSnapshot(byte[] encryptedBytes, DekScope dek)
    {
        int plaintextLen = encryptedBytes.Length - ICryptoService.AeadOverhead;
        if (plaintextLen <= 0) return null;
        var pinnedJson = GC.AllocateArray<byte>(plaintextLen, pinned: true);
        try
        {
            crypto.Decrypt(encryptedBytes, dek.Span, pinnedJson);
            return ParseSecureSnapshot(pinnedJson.AsSpan());
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to decrypt SecureProfileSnapshot. [{ExType}]", ex.GetType().Name);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinnedJson);
        }
    }

    /// <summary>
    /// Hand-parses the decrypted UTF-8 JSON span with Utf8JsonReader to build a SecureProfileSnapshot.
    /// Thanks to the CopyString discipline, no intermediate string objects are ever created.
    /// </summary>
    private static SecureProfileSnapshot? ParseSecureSnapshot(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

        string? timestamp = null, createdAt = null;
        string? identityItem1Label = null, identityItem2Label = null, identityItem3Label = null;
        List<int>? fileIds = null;
        List<ProfileCustomFieldData>? customFields = null;
        char[]? name = null, nickname = null, email = null, postalCode = null;
        char[]? address1 = null, address2 = null, mobilePhone = null, homePhone = null;
        char[]? identityItem1 = null, id1Expiry = null, identityItem2 = null, id2Expiry = null;
        char[]? identityItem3 = null, id3Expiry = null, notes = null;

        bool success = false;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                // "NationalIdentifier"/"IdCardExpiry"/"LicenseNumber"/"LicenseExpiry"/"PassportNumber"/"PassportExpiry"
                // are read as legacy aliases so profiles saved before the IdentityItem1-3 rename keep loading correctly.
                // Writes always use the new key names (see EncryptSnapshot); the old keys naturally stop appearing
                // after the next save.
                if      (reader.ValueTextEquals("Timestamp"u8))               { reader.Read(); timestamp      = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else if (reader.ValueTextEquals("CreatedAt"u8))                { reader.Read(); createdAt       = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else if (reader.ValueTextEquals("Name"u8))                    { reader.Read(); name           = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("Nickname"u8))                { reader.Read(); nickname       = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("Email"u8))                   { reader.Read(); email          = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("PostalCode"u8))              { reader.Read(); postalCode     = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("Address1"u8))                { reader.Read(); address1       = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("Address2"u8))                { reader.Read(); address2       = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("MobilePhone"u8))             { reader.Read(); mobilePhone    = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("HomePhone"u8))               { reader.Read(); homePhone      = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("IdentityItem1"u8) || reader.ValueTextEquals("NationalIdentifier"u8)) { reader.Read(); identityItem1 = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("Id1Expiry"u8)     || reader.ValueTextEquals("IdCardExpiry"u8))       { reader.Read(); id1Expiry     = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("IdentityItem2"u8) || reader.ValueTextEquals("LicenseNumber"u8))      { reader.Read(); identityItem2 = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("Id2Expiry"u8)     || reader.ValueTextEquals("LicenseExpiry"u8))      { reader.Read(); id2Expiry     = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("IdentityItem3"u8) || reader.ValueTextEquals("PassportNumber"u8))     { reader.Read(); identityItem3 = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("Id3Expiry"u8)     || reader.ValueTextEquals("PassportExpiry"u8))     { reader.Read(); id3Expiry     = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("IdentityItem1Label"u8))     { reader.Read(); identityItem1Label = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else if (reader.ValueTextEquals("IdentityItem2Label"u8))     { reader.Read(); identityItem2Label = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else if (reader.ValueTextEquals("IdentityItem3Label"u8))     { reader.Read(); identityItem3Label = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else if (reader.ValueTextEquals("Notes"u8))                   { reader.Read(); notes         = ReadChars(ref reader); }
                else if (reader.ValueTextEquals("FileIds"u8))                 { reader.Read(); fileIds       = ReadIntList(ref reader); }
                else if (reader.ValueTextEquals("CustomFields"u8))            { reader.Read(); customFields  = ReadCustomFields(ref reader); }
                else { reader.Read(); reader.Skip(); }
            }

            success = true;
            return new SecureProfileSnapshot
            {
                Timestamp = timestamp, CreatedAt = createdAt,
                Name = name, Nickname = nickname, Email = email,
                PostalCode = postalCode, Address1 = address1, Address2 = address2,
                MobilePhone = mobilePhone, HomePhone = homePhone, IdentityItem1 = identityItem1,
                Id1Expiry = id1Expiry, IdentityItem2 = identityItem2, Id2Expiry = id2Expiry,
                IdentityItem3 = identityItem3, Id3Expiry = id3Expiry, Notes = notes,
                IdentityItem1Label = identityItem1Label, IdentityItem2Label = identityItem2Label, IdentityItem3Label = identityItem3Label,
                FileIds = fileIds, CustomFields = customFields,
            };
        }
        finally
        {
            // Only on constructor failure: immediately zero-clear any char[] whose ownership never transferred
            if (!success)
            {
                SecureProfileSnapshot.ZeroChars(name);          SecureProfileSnapshot.ZeroChars(nickname);
                SecureProfileSnapshot.ZeroChars(email);         SecureProfileSnapshot.ZeroChars(postalCode);
                SecureProfileSnapshot.ZeroChars(address1);      SecureProfileSnapshot.ZeroChars(address2);
                SecureProfileSnapshot.ZeroChars(mobilePhone);   SecureProfileSnapshot.ZeroChars(homePhone);
                SecureProfileSnapshot.ZeroChars(identityItem1); SecureProfileSnapshot.ZeroChars(id1Expiry);
                SecureProfileSnapshot.ZeroChars(identityItem2); SecureProfileSnapshot.ZeroChars(id2Expiry);
                SecureProfileSnapshot.ZeroChars(identityItem3); SecureProfileSnapshot.ZeroChars(id3Expiry);
                SecureProfileSnapshot.ZeroChars(notes);
            }
        }
    }

    /// <summary>
    /// Reads a JSON string directly into a char[] from the Utf8JsonReader's current position.
    /// Flow: rent an ArrayPool buffer -> CopyString (no GetString()) -> copy to the result array -> ZeroMemory + Return.
    /// </summary>
    private static char[]? ReadChars(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) { reader.Skip(); return null; }

        // ValueSpan.Length is the UTF-8 byte count. The char count after escape expansion is always at most this.
        int maxLen = reader.ValueSpan.Length;
        var temp = ArrayPool<char>.Shared.Rent(maxLen > 0 ? maxLen : 1);
        try
        {
            int n = reader.CopyString(temp.AsSpan()); // Does not create a string object
            if (n == 0) return null;
            var result = new char[n];
            temp.AsSpan(0, n).CopyTo(result);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(temp.AsSpan()));
            ArrayPool<char>.Shared.Return(temp, clearArray: false); // Unnecessary since it's already ZeroMemory'd
        }
    }

    private static List<int>? ReadIntList(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        var list = new List<int>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (reader.TokenType == JsonTokenType.Number) list.Add(reader.GetInt32());
        return list.Count > 0 ? list : null;
    }

    // CustomFields' Value could contain PII, but the outer AES-256-GCM encryption protects data serialized via JsonSerializer.
    // Consider migrating ProfileCustomFieldData to a char[]?-based design in the future as well.
    private static List<ProfileCustomFieldData>? ReadCustomFields(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return JsonSerializer.Deserialize(ref reader, ProfileJsonContext.Default.ListProfileCustomFieldData);
    }

    /// <summary>
    /// Hand-serializes a SecureProfileSnapshot with Utf8JsonWriter and encrypts it.
    /// Writes char[]? fields directly via WriteString(ReadOnlySpan&lt;char&gt;), creating no intermediate string.
    /// </summary>
    private byte[] EncryptSnapshot(SecureProfileSnapshot snap, DekScope dek)
    {
        using var pinnedBuf = new PinnedBufferWriter();
        using (var writer = new Utf8JsonWriter(pinnedBuf))
        {
            writer.WriteStartObject();
            if (snap.Timestamp          != null) writer.WriteString("Timestamp",          snap.Timestamp);
            if (snap.Name               != null) writer.WriteString("Name",               snap.Name.AsSpan());
            if (snap.Nickname           != null) writer.WriteString("Nickname",           snap.Nickname.AsSpan());
            if (snap.Email              != null) writer.WriteString("Email",              snap.Email.AsSpan());
            if (snap.PostalCode         != null) writer.WriteString("PostalCode",         snap.PostalCode.AsSpan());
            if (snap.Address1           != null) writer.WriteString("Address1",           snap.Address1.AsSpan());
            if (snap.Address2           != null) writer.WriteString("Address2",           snap.Address2.AsSpan());
            if (snap.MobilePhone        != null) writer.WriteString("MobilePhone",        snap.MobilePhone.AsSpan());
            if (snap.HomePhone          != null) writer.WriteString("HomePhone",          snap.HomePhone.AsSpan());
            if (snap.IdentityItem1      != null) writer.WriteString("IdentityItem1",      snap.IdentityItem1.AsSpan());
            if (snap.Id1Expiry          != null) writer.WriteString("Id1Expiry",          snap.Id1Expiry.AsSpan());
            if (snap.IdentityItem2      != null) writer.WriteString("IdentityItem2",      snap.IdentityItem2.AsSpan());
            if (snap.Id2Expiry          != null) writer.WriteString("Id2Expiry",          snap.Id2Expiry.AsSpan());
            if (snap.IdentityItem3      != null) writer.WriteString("IdentityItem3",      snap.IdentityItem3.AsSpan());
            if (snap.Id3Expiry          != null) writer.WriteString("Id3Expiry",          snap.Id3Expiry.AsSpan());
            if (!string.IsNullOrEmpty(snap.IdentityItem1Label)) writer.WriteString("IdentityItem1Label", snap.IdentityItem1Label);
            if (!string.IsNullOrEmpty(snap.IdentityItem2Label)) writer.WriteString("IdentityItem2Label", snap.IdentityItem2Label);
            if (!string.IsNullOrEmpty(snap.IdentityItem3Label)) writer.WriteString("IdentityItem3Label", snap.IdentityItem3Label);
            if (snap.Notes              != null) writer.WriteString("Notes",              snap.Notes.AsSpan());
            if (snap.CreatedAt           != null) writer.WriteString("CreatedAt",           snap.CreatedAt);
            if (snap.FileIds is { Count: > 0 } fileIds)
            {
                writer.WritePropertyName("FileIds");
                writer.WriteStartArray();
                foreach (var id in fileIds) writer.WriteNumberValue(id);
                writer.WriteEndArray();
            }
            if (snap.CustomFields is { Count: > 0 } cfs)
            {
                writer.WritePropertyName("CustomFields");
                JsonSerializer.Serialize(writer, cfs, ProfileJsonContext.Default.ListProfileCustomFieldData);
            }
            writer.WriteEndObject();
        }
        return crypto.Encrypt(pinnedBuf.WrittenSpan, dek.Span);
    }

    /// <summary>Packs a ProfileEditModel into a SecureProfileSnapshot. The caller is responsible for disposing it.</summary>
    private static SecureProfileSnapshot PackToSecureSnapshot(ProfileEditModel model)
    {
        static char[]? ToChars(string? s) => string.IsNullOrEmpty(s) ? null : s.ToCharArray();
        static char[]? DateToChars(DateTime? dt) => dt.HasValue ? dt.Value.ToString("o").ToCharArray() : null;

        return new SecureProfileSnapshot
        {
            Timestamp          = DateTime.UtcNow.ToString("o"),
            CreatedAt           = model.CreatedAt?.ToString("o"),
            Name               = ToChars(model.Name),
            Nickname           = ToChars(model.Nickname),
            Email              = ToChars(model.Email),
            PostalCode         = ToChars(model.PostalCode),
            Address1           = ToChars(model.Address1),
            Address2           = ToChars(model.Address2),
            MobilePhone        = ToChars(model.MobilePhone),
            HomePhone          = ToChars(model.HomePhone),
            IdentityItem1      = ToChars(model.IdentityItem1),
            Id1Expiry          = DateToChars(model.Id1Expiry),
            IdentityItem2      = ToChars(model.IdentityItem2),
            Id2Expiry          = DateToChars(model.Id2Expiry),
            IdentityItem3      = ToChars(model.IdentityItem3),
            Id3Expiry          = DateToChars(model.Id3Expiry),
            Notes              = ToChars(model.Notes),
            IdentityItem1Label = model.IdentityItem1Label != ProfileEditModel.DefaultLabelIdentityItem1 ? model.IdentityItem1Label : null,
            IdentityItem2Label = model.IdentityItem2Label != ProfileEditModel.DefaultLabelIdentityItem2 ? model.IdentityItem2Label : null,
            IdentityItem3Label = model.IdentityItem3Label != ProfileEditModel.DefaultLabelIdentityItem3 ? model.IdentityItem3Label : null,
            CustomFields       = model.CustomFields.Count > 0 ? model.CustomFields : null,
            FileIds            = model.FileIds.Count > 0 ? model.FileIds : null,
        };
    }

    /// <summary>Public bridge letting ViewerViewModel build a ProfileEditModel from a SecureProfileSnapshot.</summary>
    internal ProfileEditModel BuildEditModelFromSnapshot(SecureProfileSnapshot snap) => MapToEditModel(snap);

    private static ProfileEditModel MapToEditModel(SecureProfileSnapshot snap) => new()
    {
        Name           = snap.Name           != null ? new string(snap.Name)           : "",
        Nickname       = snap.Nickname       != null ? new string(snap.Nickname)       : "",
        Email          = snap.Email          != null ? new string(snap.Email)          : "",
        PostalCode     = snap.PostalCode     != null ? new string(snap.PostalCode)     : "",
        Address1       = snap.Address1       != null ? new string(snap.Address1)       : "",
        Address2       = snap.Address2       != null ? new string(snap.Address2)       : "",
        MobilePhone    = snap.MobilePhone    != null ? new string(snap.MobilePhone)    : "",
        HomePhone      = snap.HomePhone      != null ? new string(snap.HomePhone)      : "",
        IdentityItem1  = snap.IdentityItem1  != null ? new string(snap.IdentityItem1)  : "",
        Id1Expiry      = ParseIso(snap.Id1Expiry),
        IdentityItem2  = snap.IdentityItem2  != null ? new string(snap.IdentityItem2)  : "",
        Id2Expiry      = ParseIso(snap.Id2Expiry),
        IdentityItem3  = snap.IdentityItem3  != null ? new string(snap.IdentityItem3)  : "",
        Id3Expiry      = ParseIso(snap.Id3Expiry),
        Notes          = snap.Notes          != null ? new string(snap.Notes)          : "",
        IdentityItem1Label = string.IsNullOrEmpty(snap.IdentityItem1Label) ? ProfileEditModel.DefaultLabelIdentityItem1 : snap.IdentityItem1Label,
        IdentityItem2Label = string.IsNullOrEmpty(snap.IdentityItem2Label) ? ProfileEditModel.DefaultLabelIdentityItem2 : snap.IdentityItem2Label,
        IdentityItem3Label = string.IsNullOrEmpty(snap.IdentityItem3Label) ? ProfileEditModel.DefaultLabelIdentityItem3 : snap.IdentityItem3Label,
        CustomFields   = snap.CustomFields   ?? [],
        CreatedAt       = ParseIso(snap.CreatedAt),
        Timestamp      = snap.Timestamp,
        FileIds        = snap.FileIds        ?? [],
    };

    // ─── ExtractDisplayName: targeted extraction of the display name (existing implementation retained) ────────

    private string? ExtractDisplayName(byte[] encryptedBlob, DekScope dek)
    {
        int plaintextLen = encryptedBlob.Length - ICryptoService.AeadOverhead;
        if (plaintextLen <= 0) return null;
        var pinnedJson = GC.AllocateArray<byte>(plaintextLen, pinned: true);
        try
        {
            crypto.Decrypt(encryptedBlob, dek.Span, pinnedJson);
            return ScanDisplayName(pinnedJson.AsSpan());
        }
        catch { return null; }
        finally { CryptographicOperations.ZeroMemory(pinnedJson); }
    }

    private static string? ScanDisplayName(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

        Span<char> buf = stackalloc char[512];
        try
        {
            string? name = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isNickname = reader.ValueTextEquals("Nickname"u8);
                bool isName     = !isNickname && reader.ValueTextEquals("Name"u8);
                reader.Read();

                if ((isNickname || isName) && reader.TokenType == JsonTokenType.String)
                {
                    int maxBytes = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
                    if (maxBytes <= buf.Length)
                    {
                        int n = reader.CopyString(buf);
                        if (n > 0 && !buf[..n].IsWhiteSpace())
                        {
                            if (isNickname) return new string(buf[..n]);
                            name ??= new string(buf[..n]);
                        }
                    }
                    else
                    {
                        var s = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            if (isNickname) return s;
                            name ??= s;
                        }
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            return name;
        }
        finally
        {
            // Physically wipe before the stack pointer unwinds. Scope exit alone does not zero it
            // (same convention as FieldCrypto.Seal's stackalloc path).
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buf));
        }
    }

    // ─── Utilities ──────────────────────────────────────────────────────

    private static DateTime? ParseIso(string? s)
        => s != null && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;

    private static DateTime? ParseIso(char[]? c)
    {
        if (c == null || c.Length == 0) return null;
        return DateTime.TryParse(c.AsSpan(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : null;
    }

    private static async Task UpsertSettingAsync(AppDbContext db, int key, byte[] value)
    {
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == key);
        if (existing == null)
            db.Metadata.Add(new VaultMetadata { ConfigKey = key, ConfigValue = value });
        else
            existing.ConfigValue = value;
    }
}

// ProfileSnapshot has been replaced by SecureProfileSnapshot.
// Only the source-generated code for CustomFields / FileIds remains (List<ProfileCustomFieldData> goes via JsonSerializer).
[JsonSerializable(typeof(List<ProfileCustomFieldData>))]
[JsonSerializable(typeof(List<int>))]
internal partial class ProfileJsonContext : JsonSerializerContext { }
