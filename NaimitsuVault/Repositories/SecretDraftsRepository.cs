// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Repositories;

/// <summary>
/// Draft staging buffer repository (SecretDrafts table). Physically separate from
/// SecretHistoryRepository because drafts (high-frequency, written on every focus-out) and
/// finalized history (low-frequency, written only on Save) have fundamentally different
/// lifecycles. A row's existence means a draft exists; its absence means no draft — this
/// replaces the old "is the BLOB column NULL" indirect check.
/// </summary>
public class SecretDraftsRepository(IDbContextFactory<AppDbContext> factory, ICryptoService crypto)
{
    /// <summary>
    /// Saves a draft to SecretDrafts (call on focus-out). Upsert: updates the row if it exists,
    /// otherwise inserts a new one.
    /// </summary>
    public async Task SaveDraftAsync(int secretId, byte[] snapshotEncrypted, DateTime savedAt)
    {
        await using var db = await factory.CreateDbContextAsync();

        int updated = await db.SecretDrafts
            .Where(d => d.SecretId == secretId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.SnapshotBlob, snapshotEncrypted)
                .SetProperty(d => d.SavedAt, savedAt));

        if (updated == 0)
        {
            db.SecretDrafts.Add(new SecretDraft
            {
                SecretId     = secretId,
                SnapshotBlob = snapshotEncrypted,
                SavedAt      = savedAt,
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Returns the draft's encrypted snapshot and save time (call when loading the detail screen).
    /// Both are null if no draft row exists.
    /// </summary>
    public async Task<(byte[]? DraftEnc, DateTime? DraftAt)> GetDraftAsync(int secretId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.SecretDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.SecretId == secretId);
        return (row?.SnapshotBlob, row?.SavedAt);
    }

    /// <summary>
    /// Deletes the draft row (call on explicit "discard draft" only).
    /// On successful save, PushAsync's internal transaction clears the draft atomically instead —
    /// do not call this after a save completes.
    /// Does nothing if no draft row exists (silently safe).
    /// </summary>
    public async Task DiscardDraftAsync(int secretId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.SecretDrafts
            .Where(d => d.SecretId == secretId)
            .ExecuteDeleteAsync();
    }

    /// <summary>Returns all SecretIds that currently have a draft row (bulk fetch of the HasDraft flag during LoadAsync).</summary>
    public async Task<HashSet<int>> GetAllDraftIdsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = await db.SecretDrafts
            .Select(d => d.SecretId)
            .ToListAsync();
        return [..ids];
    }

    /// <summary>Explicit cleanup of the draft row for a Secret. Normally unnecessary (ON DELETE CASCADE handles it).</summary>
    public async Task DeleteAllForSecretAsync(int secretId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.SecretDrafts
            .Where(d => d.SecretId == secretId)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Deserializes each draft's SnapshotBlob and returns the set of SecretIds whose AttachedFiles
    /// contains the given fileId. Used to set the IsDraftLink flag in the gallery/viewer.
    /// </summary>
    /// <param name="fileId">The file ID to search for.</param>
    /// <param name="dek">The session key needed for decryption (obtained via _appSession.GetKey()).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<HashSet<int>> GetSecretIdsWithDraftContainingFileAsync(
        int fileId, DekScope dek, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var rows = await db.SecretDrafts
            .AsNoTracking()
            .Select(d => new { d.SecretId, d.SnapshotBlob })
            .ToListAsync(ct);

        var result = new HashSet<int>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (row.SnapshotBlob is not { Length: > 0 }) continue;

            if (row.SnapshotBlob.Length <= ICryptoService.AeadOverhead) continue;
            int plainLen = row.SnapshotBlob.Length - ICryptoService.AeadOverhead;
            var plain = GC.AllocateArray<byte>(plainLen, pinned: true);
            try
            {
                crypto.Decrypt(row.SnapshotBlob, dek.Span, plain);
                if (SnapshotFileIdScanner.ContainsFileId(plain, fileId))
                    result.Add(row.SecretId);
            }
            catch (CryptographicException)
            {
                // Decryption failure (corrupted data): ignore and continue scanning
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain.AsSpan());
            }
        }
        return result;
    }
}
