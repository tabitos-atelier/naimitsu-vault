// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public record AvatarChangedMessage(byte[]? AvatarBytes);

/// <summary>
/// Encrypts and saves/loads the avatar image to the VaultMetadata table (ConfigKey="AvatarImage_TwinA").
/// The image bytes are cached in AppSession to prevent references from outliving session disposal.
/// </summary>
public class AvatarService(
    IDbContextFactory<AppDbContext> factory,
    ICryptoService crypto,
    ISecurityContext session)
{
    public async Task<byte[]?> LoadAsync(DekScope dek)
    {
        // Read path: dek.Span is evaluated after the await (same design as IAuditLogService.GetRecentAsync).
        // If Lock() completes during the await, dek.Span returns a zeroed span and DecryptToPin returns null (fail-safe).
        byte[]? bytes = null;
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var setting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinA);
            if (setting?.ConfigValue is not { Length: > 0 } encrypted) return null;

            bytes = crypto.DecryptToPin(encrypted, dek.Span);
            if (bytes == null) return null;

            session.AvatarBytes = bytes; // The setter ZeroMemory's bytes before copying into the pinned buffer
            bytes = null;                // Already ZeroMemory'd. Prevents a double clear in finally
            return session.AvatarBytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            // bytes non-null means DecryptToPin succeeded but the transfer to session did not complete
            // (e.g. an exception was thrown during session.AvatarBytes = bytes).
            // If DecryptToPin returns null (decryption failed), bytes is null so ZeroMemory is unnecessary.
            if (bytes != null) CryptographicOperations.ZeroMemory(bytes.AsSpan());
        }
    }

    /// <summary>
    /// Ownership: after encryption, <paramref name="imageBytes"/> ownership transfers to <c>session.AvatarBytes</c>.
    /// Its setter internally calls <c>ZeroMemory</c>, so once the call completes, the contents of
    /// <paramref name="imageBytes"/> are wiped (the caller must not reuse this array afterward).
    /// </summary>
    public async Task SaveAsync(byte[] imageBytes, DekScope dek)
    {
        byte[]? encrypted = null;
        try
        {
            encrypted = crypto.Encrypt(imageBytes, dek.Span);

            // Transfer ownership to the session before entering the await
            session.AvatarBytes = imageBytes;

            await using var db = await factory.CreateDbContextAsync();
            var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinA);
            if (existing == null)
                db.Metadata.Add(new VaultMetadata { ConfigKey = VaultMetadataKey.AvatarImage_TwinA, ConfigValue = encrypted });
            else
                existing.ConfigValue = encrypted;

            await db.SaveChangesAsync();
        }
        finally
        {
            if (encrypted != null) CryptographicOperations.ZeroMemory(encrypted.AsSpan());
        }
    }

    public async Task DeleteAvatarAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinA);
        if (existing != null)
        {
            db.Metadata.Remove(existing);
            await db.SaveChangesAsync();
        }
        session.AvatarBytes = null;
    }

    /// <summary>Decrypts and returns the committed (TwinA) avatar without touching AppSession. Caller owns wiping.</summary>
    public async Task<byte[]?> LoadCommittedRawAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var setting = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinA);
        return setting?.ConfigValue is { Length: > 0 } encrypted ? crypto.DecryptToPin(encrypted, dek.Span) : null;
    }

    /// <summary>
    /// Decrypts and returns the draft (TwinB) avatar. Null = no draft row (draft avatar equals the
    /// committed one). Empty array = the draft explicitly clears the avatar. Non-empty = the draft's
    /// replacement avatar. Caller owns wiping a non-empty result.
    /// </summary>
    public async Task<byte[]?> LoadDraftRawAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var setting = await db.Metadata.AsNoTracking().FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinB);
        if (setting == null) return null;
        if (setting.ConfigValue.Length == 0) return [];
        return crypto.DecryptToPin(setting.ConfigValue, dek.Span) ?? [];
    }

    /// <summary>Restores the avatar for editing, preferring the draft (TwinB) — mirrors ProfileService.LoadProfileAsync.</summary>
    public async Task<byte[]?> LoadForEditAsync(DekScope dek)
    {
        var draft = await LoadDraftRawAsync(dek);
        if (draft != null) return draft.Length > 0 ? draft : null; // empty draft = explicit "no avatar"
        return await LoadCommittedRawAsync(dek);
    }

    /// <summary>
    /// Immediately encrypts and saves the pending avatar to AvatarImage_TwinB. Pass an empty array to
    /// record "the draft clears the avatar" (stored unencrypted, since an empty value carries nothing to protect).
    /// </summary>
    /// <remarks>
    /// Ownership: unlike <see cref="SaveAsync"/>, this only reads <paramref name="imageBytes"/> and never
    /// clears it - the caller (ProfileViewModel's <c>_pendingAvatarBytes</c>) keeps reusing the same buffer
    /// across repeated auto-saves during active editing and is responsible for zeroing it once editing ends.
    /// </remarks>
    public async Task SaveDraftAsync(byte[] imageBytes, DekScope dek)
    {
        byte[] encrypted = imageBytes.Length > 0 ? crypto.Encrypt(imageBytes, dek.Span) : [];
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinB);
            if (existing == null)
                db.Metadata.Add(new VaultMetadata { ConfigKey = VaultMetadataKey.AvatarImage_TwinB, ConfigValue = encrypted });
            else
                existing.ConfigValue = encrypted;

            await db.SaveChangesAsync();
        }
        finally
        {
            if (encrypted.Length > 0) CryptographicOperations.ZeroMemory(encrypted.AsSpan());
        }
    }

    public async Task<bool> HasAvatarDraftAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Metadata.AsNoTracking().AnyAsync(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinB);
    }

    /// <summary>Physically deletes the draft (AvatarImage_TwinB) row.</summary>
    public async Task DiscardAvatarDraftAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Metadata.Where(s => s.ConfigKey == VaultMetadataKey.AvatarImage_TwinB).ExecuteDeleteAsync();
    }
}
