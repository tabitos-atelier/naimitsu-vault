// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Common;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Repositories;

public class StoredFileRepository(
    IDbContextFactory<AppDbContext> factory,
    ICryptoService crypto,
    ILogger<StoredFileRepository> logger)
{
    public async Task<int> CountAllAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.StoredFiles.CountAsync();
    }

    /// <summary>Alive files only (DeletedAt == null).</summary>
    public async Task<List<StoredFile>> GetAllAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var files = await db.StoredFiles
            .AsNoTracking()
            .Where(i => i.DeletedAt == null)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
        foreach (var f in files) Decrypt(f, dek);
        return files;
    }

    /// <summary>All files, alive and soft-deleted alike. For Gallery's single-screen trash view.</summary>
    public async Task<List<StoredFile>> GetAllIncludingDeletedAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var files = await db.StoredFiles
            .AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
        foreach (var f in files) Decrypt(f, dek);
        return files;
    }

    public async Task<StoredFile?> GetByIdAsync(int id, DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var f = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (f != null) Decrypt(f, dek);
        return f;
    }

    /// <param name="fileHash">Raw 32-byte HMAC-SHA256(DEK, SHA256(rawBytes)).</param>
    public async Task<StoredFile?> FindByHashAsync(byte[] fileHash, DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var f = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(i => i.FileHash == fileHash);
        if (f != null) Decrypt(f, dek);
        return f;
    }

    public async Task<bool> IsFileInUseAsync(int fileId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SecretFileLinks.AnyAsync(l => l.FileId == fileId)
            || await db.ProfileFileLinks.AnyAsync(l => l.FileId == fileId);
    }

    public async Task<List<int>> GetProfileFileLinksAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ProfileFileLinks.AsNoTracking().Select(l => l.FileId).ToListAsync();
    }

    public async Task UpdateProfileFileLinksAsync(IEnumerable<int> fileIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        await db.ProfileFileLinks.ExecuteDeleteAsync();

        db.ProfileFileLinks.AddRange(fileIds.Distinct().Select(id => new ProfileFileLink { FileId = id }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<List<StoredFile>> GetByIdsAsync(IEnumerable<int> ids, DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var files = await db.StoredFiles
            .AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .ToListAsync();
        foreach (var f in files) Decrypt(f, dek);
        return files;
    }

    public async Task<int> AddAsync(StoredFile file, DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var clone = CreateEncryptedEntity(file, dek);
        clone.CreatedAt = now;
        clone.UpdatedAt = now;
        db.StoredFiles.Add(clone);
        await db.SaveChangesAsync();
        file.CreatedAt = clone.CreatedAt;
        file.UpdatedAt = clone.UpdatedAt;
        return clone.Id;
    }

    public async Task UpdateAsync(StoredFile file, DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var clone = CreateEncryptedEntity(file, dek);
        clone.UpdatedAt = DateTime.UtcNow;
        db.StoredFiles.Update(clone);
        await db.SaveChangesAsync();
        file.UpdatedAt = clone.UpdatedAt;
    }

    /// <summary>
    /// Repairs rows whose FileModifiedAt was never set (left at DateTime.MinValue) - a data-quality
    /// backfill, not a deletion, so it's exempt from the "no automatic deletion without explicit user
    /// action" rule. Falls back to CreatedAt as the best available substitute. Historically this ran as
    /// part of DatabaseInitializer.InitializeAsync() but was dropped when the 2026-06-10 multi-vault
    /// split rewrote that method to only touch the unified DB (before any vault is even selected).
    /// </summary>
    public async Task RepairMissingFileModifiedAtAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var repaired = await db.StoredFiles
            .Where(f => f.FileModifiedAt == DateTime.MinValue)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.FileModifiedAt, f => f.CreatedAt));
        if (repaired > 0)
            logger.LogInformation("Repaired FileModifiedAt for {Count} StoredFiles row(s) that were missing it.", repaired);
    }

    public async Task<List<Secret>> GetSecretsByFileIdAsync(int fileId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SecretFileLinks
            .AsNoTracking()
            .Where(l => l.FileId == fileId)
            .Join(db.Secrets, l => l.SecretId, s => s.Id, (l, s) => s)
            .ToListAsync();
    }

    /// <summary>Permanently deletes a file row and any lingering links to it (defensive - links are
    /// normally already gone by the time this runs, either from SoftDeleteAsync or the caller's own
    /// pre-collection).</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SecretFileLinks.Where(l => l.FileId == id).ExecuteDeleteAsync();
        await db.ProfileFileLinks.Where(l => l.FileId == id).ExecuteDeleteAsync();
        await db.StoredFiles.Where(f => f.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    /// <summary>Soft-deletes a file (30-day trash) and atomically severs every link to it in the same
    /// transaction - a soft-deleted file is never left dangling as "still linked" in Secrets/Profile.</summary>
    public async Task SoftDeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.StoredFiles
            .Where(f => f.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.DeletedAt, DateTime.UtcNow));
        await db.SecretFileLinks.Where(l => l.FileId == id).ExecuteDeleteAsync();
        await db.ProfileFileLinks.Where(l => l.FileId == id).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    /// <summary>Restores a soft-deleted file. Links are deliberately not restored - the file comes
    /// back as an unlinked, unclassified item.</summary>
    public async Task UndeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.StoredFiles
            .Where(f => f.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.DeletedAt, (DateTime?)null));
    }

    public async Task AddFileLinkAsync(int secretId, int fileId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var exists = await db.SecretFileLinks.AnyAsync(l => l.SecretId == secretId && l.FileId == fileId);
        if (!exists)
        {
            db.SecretFileLinks.Add(new SecretFileLink { SecretId = secretId, FileId = fileId });
            await db.SaveChangesAsync();
        }
    }

    public async Task RemoveFileLinkAsync(int secretId, int fileId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.SecretFileLinks
            .Where(l => l.SecretId == secretId && l.FileId == fileId)
            .ExecuteDeleteAsync();
    }

    private void Decrypt(StoredFile f, DekScope dek)
    {
        f.FileName = DecryptBlobToBytes(f.FileName, dek);
        ValidateContentType(f);
    }

    /// <summary>
    /// Decrypts the encrypted FileName BLOB and returns it as a byte[].
    /// Returns an empty array on decryption failure (ValidateContentType detects the error).
    /// </summary>
    private byte[] DecryptBlobToBytes(byte[] blob, DekScope dek)
    {
        if (blob is null || blob.Length <= ICryptoService.AeadOverhead) return [];
        int plainLen = blob.Length - ICryptoService.AeadOverhead;
        var pinned = GC.AllocateArray<byte>(plainLen, pinned: true);
        try
        {
            crypto.Decrypt(blob, dek.Span, pinned);
            return pinned.ToArray();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning("Failed to decrypt StoredFile.FileName (suspected data corruption or tampering).");
            return [];
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(pinned.AsSpan());
        }
    }

    /// <summary>
    /// Checks whether the decrypted FileName's extension is consistent with the ContentType code.
    /// A mismatch (suspected tampering or corruption) sets <see cref="StoredFile.IsQuarantined"/> instead
    /// of throwing, so that one bad row never aborts a batch read (GetAllAsync/GetByIdsAsync) for the rest.
    /// </summary>
    private void ValidateContentType(StoredFile f)
    {
        ReadOnlySpan<byte> fileNameBytes = f.FileName;
        int maxChars = System.Text.Encoding.UTF8.GetMaxCharCount(fileNameBytes.Length);
        // POH-pinned fallback so a GC compaction can't leave an unzeroed plaintext file name ghost behind
        Span<char> chars = maxChars <= 512
            ? stackalloc char[512]
            : GC.AllocateArray<char>(maxChars, pinned: true);
        try
        {
            int charCount = System.Text.Encoding.UTF8.GetChars(fileNameBytes, chars);
            ReadOnlySpan<char> nameSpan = chars[..charCount];
            var expected = FileTypeCode.FromFileName(nameSpan);
            if (expected == 0)
            {
                logger.LogWarning("ContentType not validated (extension not registered in the dictionary): StoredFile(Id={Id})", f.Id);
                return;
            }
            if (f.ContentTypeCode != expected)
            {
                f.IsQuarantined = true;
                logger.LogWarning(
                    "Quarantined StoredFile(Id={Id}) (suspected tampering or corruption): expected ContentType={Expected}, actual={Actual}",
                    f.Id, expected, f.ContentTypeCode);
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars));
        }
    }

    /// <summary>
    /// Reconciles StoredFiles.ContentType against the extension of the decrypted FileName for rows
    /// where the two have drifted apart (e.g. after a new extension was registered in FileTypeCode).
    /// Only the plaintext ContentType column is corrected via ExecuteUpdateAsync; FileName ciphertext
    /// is never re-encrypted or written back. Call once, opportunistically, before a batch read.
    /// Returns one entry per corrected row (not just a count) so the caller can record which specific
    /// file was touched in the audit log, rather than an unverifiable aggregate.
    /// </summary>
    public async Task<IReadOnlyList<ContentTypeCorrection>> RepairContentTypeMismatchesAsync(DekScope dek)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.StoredFiles
            .AsNoTracking()
            .Select(f => new { f.Id, f.FileName, f.ContentTypeCode })
            .ToListAsync();

        var corrections = new List<ContentTypeCorrection>();
        foreach (var row in rows)
        {
            var plainName = DecryptBlobToBytes(row.FileName, dek);
            try
            {
                var expected = ComputeExpectedContentType(plainName);
                if (expected != 0 && row.ContentTypeCode != expected)
                    corrections.Add(new ContentTypeCorrection(
                        row.Id, System.Text.Encoding.UTF8.GetString(plainName), row.ContentTypeCode, expected));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainName);
            }
        }

        foreach (var c in corrections)
        {
            await db.StoredFiles
                .Where(f => f.Id == c.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ContentTypeCode, c.NewContentType));
        }

        if (corrections.Count > 0)
            logger.LogWarning("Repaired {Count} StoredFile ContentType mismatch(es) (extension table reclassification).", corrections.Count);

        return corrections;
    }

    /// <summary>Returns the FileTypeCode for a plaintext file name's extension, or 0 if empty/unregistered.</summary>
    private static int ComputeExpectedContentType(ReadOnlySpan<byte> plainNameUtf8)
    {
        if (plainNameUtf8.Length == 0) return 0;
        int maxChars = System.Text.Encoding.UTF8.GetMaxCharCount(plainNameUtf8.Length);
        // POH-pinned fallback so a GC compaction can't leave an unzeroed plaintext file name ghost behind
        Span<char> chars = maxChars <= 512
            ? stackalloc char[512]
            : GC.AllocateArray<char>(maxChars, pinned: true);
        try
        {
            int charCount = System.Text.Encoding.UTF8.GetChars(plainNameUtf8, chars);
            return FileTypeCode.FromFileName(chars[..charCount]);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars));
        }
    }

    // Only FileName is encrypted here. FileHash, OriginalBlob, and ThumbnailBlob are already passed
    // in as already-encrypted byte sequences by the caller (GalleryViewModel, etc.), so they are
    // copied as-is (double encryption is prohibited).
    private StoredFile CreateEncryptedEntity(StoredFile src, DekScope dek) => new()
    {
        Id              = src.Id,
        FileName        = crypto.Encrypt(src.FileName, dek.Span),
        ContentTypeCode = src.ContentTypeCode,
        FileSize        = src.FileSize,
        FileHash        = src.FileHash,
        OriginalBlob    = src.OriginalBlob,
        ThumbnailBlob   = src.ThumbnailBlob,
        FileModifiedAt  = src.FileModifiedAt,
        CreatedAt        = src.CreatedAt,
        UpdatedAt        = src.UpdatedAt,
    };
}

/// <summary>One StoredFile row corrected by StoredFileRepository.RepairContentTypeMismatchesAsync.</summary>
public sealed record ContentTypeCorrection(int Id, string FileName, int OldContentType, int NewContentType);
