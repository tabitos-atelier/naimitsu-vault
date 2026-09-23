// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage for the 2026-06-10 multi-vault refactor that silently dropped two migration
/// steps that used to run inside the old, since-rewritten DatabaseInitializer.InitializeAsync():
/// the 30-day soft-deleted-secret purge (CleanupDeletedSecretsAsync), and the backfill that repaired
/// StoredFiles rows whose FileModifiedAt was left at DateTime.MinValue. Both silently regressed for
/// months because the only existing coverage (TC-DBC-08) called the purge method directly rather
/// than through the real call path. These tests exercise PostUnlockMaintenanceService.RunAsync - the
/// exact method App.xaml.cs now calls after every unlock - so a future refactor that drops the wiring
/// again fails a test instead of going unnoticed.
/// </summary>
public sealed class PostUnlockMaintenanceServiceTests
{
    // ── TC-PUM-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SecretDeletedOver30DaysAgo_IsPhysicallyPurged()
    {
        // Arrange
        using var db = TestDb.Create();
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            ctx.Secrets.Add(new Secret { Title = new byte[28], CreatedAt = now, UpdatedAt = now, DeletedAt = now.AddDays(-31) });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        var profileService = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var storedFiles = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var auditLog = new StubAuditLogService();
        var service = new PostUnlockMaintenanceService(initializer, profileService, storedFiles, auditLog, NullLogger<PostUnlockMaintenanceService>.Instance);

        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        var dek = new DekScope(key, 32);

        // Act - the exact method App.xaml.cs calls after every unlock
        await service.RunAsync(dek);

        // Assert
        await using var verifyCtx = db.Factory.CreateDbContext();
        var remaining = await verifyCtx.Secrets.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(remaining);

        // Regression coverage: SecretAutoPurgedByExpiry (0x6103) was defined in AuditEventCode with a
        // matching payload record but never actually wired to LogAsync from any call site.
        var purgeLog = Assert.Single(auditLog.Calls, c => c.Code == AuditEventCode.SecretAutoPurgedByExpiry);
        var payload  = Assert.IsType<SecretAutoPurgedByExpiryPayload>(purgeLog.Payload);
        Assert.Equal(1, payload.PurgedCount);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PUM-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SecretDeletedUnder30DaysAgo_IsRetained()
    {
        // Arrange
        using var db = TestDb.Create();
        int secretId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            var secret = new Secret { Title = new byte[28], CreatedAt = now, UpdatedAt = now, DeletedAt = now.AddDays(-29) };
            ctx.Secrets.Add(secret);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            secretId = secret.Id;
        }

        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        var profileService = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var storedFiles = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var service = new PostUnlockMaintenanceService(initializer, profileService, storedFiles, new StubAuditLogService(), NullLogger<PostUnlockMaintenanceService>.Instance);

        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        var dek = new DekScope(key, 32);

        // Act
        await service.RunAsync(dek);

        // Assert - still present
        await using var verifyCtx = db.Factory.CreateDbContext();
        var remaining = await verifyCtx.Secrets.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(remaining, s => s.Id == secretId);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PUM-03 ─────────────────────────────────────────────────────────────────
    // Regression coverage: SecretsViewModel/ProfileViewModel's file-attach flows never set
    // FileModifiedAt (unlike GalleryViewModel's own attach flow), leaving affected rows stuck at
    // DateTime.MinValue - which the Gallery/Viewer UI intentionally renders as a blank date rather
    // than throwing. This backfill repairs those rows using CreatedAt as the best available substitute.

    [Fact]
    public async Task RunAsync_StoredFileWithMissingFileModifiedAt_IsBackfilledFromCreatedAt()
    {
        // Arrange
        using var db = TestDb.Create();
        var createdAt = DateTime.UtcNow.AddDays(-5);
        int fileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var file = new StoredFile
            {
                FileName     = new byte[28],
                ContentTypeCode  = 1,
                FileSize     = 1,
                FileHash     = new byte[32],
                OriginalBlob = new byte[28],
                CreatedAt     = createdAt,
                UpdatedAt     = createdAt,
                // FileModifiedAt deliberately left unset (DateTime.MinValue) - reproduces the bug.
            };
            ctx.StoredFiles.Add(file);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            fileId = file.Id;
        }

        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        var profileService = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var storedFiles = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var service = new PostUnlockMaintenanceService(initializer, profileService, storedFiles, new StubAuditLogService(), NullLogger<PostUnlockMaintenanceService>.Instance);

        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        var dek = new DekScope(key, 32);

        // Act
        await service.RunAsync(dek);

        // Assert - FileModifiedAt now matches the row's own (DB round-tripped) CreatedAt instead of
        // DateTime.MinValue. Compared against the re-read CreatedAt, not the original local `createdAt`
        // variable, since the Unix-seconds column conversion truncates sub-second precision.
        await using var verifyCtx = db.Factory.CreateDbContext();
        var repaired = await verifyCtx.StoredFiles.SingleAsync(f => f.Id == fileId, TestContext.Current.CancellationToken);
        Assert.Equal(repaired.CreatedAt, repaired.FileModifiedAt);
        Assert.NotEqual(DateTime.MinValue, repaired.FileModifiedAt);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PUM-04 ─────────────────────────────────────────────────────────────────
    // Mirrors TC-PUM-01 for StoredFiles: a file soft-deleted over 30 days ago is physically purged
    // and the purge is recorded via the File-specific FileAutoPurgedByExpiry (0x6303) - deliberately
    // not the Secret-only SecretAutoPurgedByExpiry (0x6103).

    [Fact]
    public async Task RunAsync_FileDeletedOver30DaysAgo_IsPhysicallyPurged()
    {
        // Arrange
        using var db = TestDb.Create();
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            ctx.StoredFiles.Add(new StoredFile
            {
                FileName       = new byte[28],
                ContentTypeCode    = 1,
                FileSize       = 1,
                FileHash       = new byte[32],
                FileModifiedAt = now,
                CreatedAt       = now,
                UpdatedAt       = now,
                DeletedAt      = now.AddDays(-31),
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        var profileService = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var storedFiles = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var auditLog = new StubAuditLogService();
        var service = new PostUnlockMaintenanceService(initializer, profileService, storedFiles, auditLog, NullLogger<PostUnlockMaintenanceService>.Instance);

        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        var dek = new DekScope(key, 32);

        // Act
        await service.RunAsync(dek);

        // Assert
        await using var verifyCtx = db.Factory.CreateDbContext();
        var remaining = await verifyCtx.StoredFiles.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(remaining);

        var purgeLog = Assert.Single(auditLog.Calls, c => c.Code == AuditEventCode.FileAutoPurgedByExpiry);
        var payload  = Assert.IsType<FileAutoPurgedByExpiryPayload>(purgeLog.Payload);
        Assert.Equal(1, payload.PurgedCount);
        Assert.DoesNotContain(auditLog.Calls, c => c.Code == AuditEventCode.SecretAutoPurgedByExpiry);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PUM-05 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_FileDeletedUnder30DaysAgo_IsRetained()
    {
        // Arrange
        using var db = TestDb.Create();
        int fileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            var file = new StoredFile
            {
                FileName       = new byte[28],
                ContentTypeCode    = 1,
                FileSize       = 1,
                FileHash       = new byte[32],
                FileModifiedAt = now,
                CreatedAt       = now,
                UpdatedAt       = now,
                DeletedAt      = now.AddDays(-29),
            };
            ctx.StoredFiles.Add(file);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            fileId = file.Id;
        }

        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        var profileService = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var storedFiles = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var service = new PostUnlockMaintenanceService(initializer, profileService, storedFiles, new StubAuditLogService(), NullLogger<PostUnlockMaintenanceService>.Instance);

        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        var dek = new DekScope(key, 32);

        // Act
        await service.RunAsync(dek);

        // Assert - still present
        await using var verifyCtx = db.Factory.CreateDbContext();
        var remaining = await verifyCtx.StoredFiles.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(remaining, f => f.Id == fileId);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PUM-06 ─────────────────────────────────────────────────────────────────
    // Regression coverage: DatabaseInitializer.CleanupDeletedSecretsAsync used to delete only the
    // Secrets row via ExecuteDeleteAsync, leaving any SecretFileLinks row pointing at it as a
    // permanent orphan (SecretFileLinks has no FK cascade configured - see SecretRepository.HardDeleteAsync,
    // which already handled this correctly). This mirrors TC-PUM-01 but also links a file first.

    [Fact]
    public async Task RunAsync_SecretDeletedOver30DaysAgo_RemovesOrphanedFileLinkToo()
    {
        // Arrange
        using var db = TestDb.Create();
        int fileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            var secret = new Secret { Title = new byte[28], CreatedAt = now, UpdatedAt = now, DeletedAt = now.AddDays(-31) };
            var file = new StoredFile
            {
                FileName       = new byte[28],
                ContentTypeCode = 1,
                FileSize       = 1,
                FileHash       = new byte[32],
                FileModifiedAt = now,
                CreatedAt       = now,
                UpdatedAt       = now,
            };
            ctx.Secrets.Add(secret);
            ctx.StoredFiles.Add(file);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            fileId = file.Id;
            ctx.SecretFileLinks.Add(new SecretFileLink { SecretId = secret.Id, FileId = fileId });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        var profileService = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var storedFiles = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var service = new PostUnlockMaintenanceService(initializer, profileService, storedFiles, new StubAuditLogService(), NullLogger<PostUnlockMaintenanceService>.Instance);

        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        var dek = new DekScope(key, 32);

        // Act
        await service.RunAsync(dek);

        // Assert - both the Secret and its now-dangling SecretFileLinks row are gone; the linked
        // file itself is untouched (it isn't past its own retention window).
        await using var verifyCtx = db.Factory.CreateDbContext();
        Assert.Empty(await verifyCtx.Secrets.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verifyCtx.SecretFileLinks.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verifyCtx.StoredFiles.Where(f => f.Id == fileId).ToListAsync(TestContext.Current.CancellationToken));

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }

    // ── TC-PUM-07 ─────────────────────────────────────────────────────────────────
    // Regression coverage: CleanupDeletedFilesAsync used to delete only the StoredFiles row, so a
    // lingering SecretFileLinks/ProfileFileLinks row (e.g. left by an abnormal exit or outside DB
    // edit; neither table has an FK cascade) would outlive its file as a permanent orphan. The
    // purge must sweep those links too, while leaving links to a file still inside its retention
    // window untouched.

    [Fact]
    public async Task RunAsync_FileDeletedOver30DaysAgo_RemovesLingeringLinksToo()
    {
        // Arrange
        using var db = TestDb.Create();
        int expiredFileId;
        int retainedFileId;
        await using (var ctx = db.Factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            // FileHash is unique, so each file needs a distinct one
            StoredFile NewFile(DateTime deletedAt, byte hashSeed) => new()
            {
                FileName        = new byte[28],
                ContentTypeCode = 1,
                FileSize        = 1,
                FileHash        = Enumerable.Repeat(hashSeed, 32).ToArray(),
                FileModifiedAt  = now,
                CreatedAt        = now,
                UpdatedAt        = now,
                DeletedAt       = deletedAt,
            };
            var secret  = new Secret { Title = new byte[28], CreatedAt = now, UpdatedAt = now };
            var expired  = NewFile(now.AddDays(-31), 1);
            var retained = NewFile(now.AddDays(-29), 2);
            ctx.Secrets.Add(secret);
            ctx.StoredFiles.AddRange(expired, retained);
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
            expiredFileId  = expired.Id;
            retainedFileId = retained.Id;
            ctx.SecretFileLinks.AddRange(
                new SecretFileLink { SecretId = secret.Id, FileId = expiredFileId },
                new SecretFileLink { SecretId = secret.Id, FileId = retainedFileId });
            ctx.ProfileFileLinks.Add(new ProfileFileLink { FileId = expiredFileId });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var initializer = new DatabaseInitializer(
            db.Factory,
            new ExplodingUnifiedDbContextFactory(),
            new SessionGenerationGuardStub(),
            NullLogger<DatabaseInitializer>.Instance);
        var crypto  = new IdentityCryptoService();
        var session = new AppSession();
        var profileService = new ProfileService(db.Factory, crypto, session, NullLogger<ProfileService>.Instance);
        var storedFiles = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var service = new PostUnlockMaintenanceService(initializer, profileService, storedFiles, new StubAuditLogService(), NullLogger<PostUnlockMaintenanceService>.Instance);

        var key = GC.AllocateArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        var dek = new DekScope(key, 32);

        // Act
        await service.RunAsync(dek);

        // Assert - the expired file and every link to it are gone; the file still inside its
        // retention window keeps both the row and its link.
        await using var verifyCtx = db.Factory.CreateDbContext();
        Assert.DoesNotContain(await verifyCtx.StoredFiles.ToListAsync(TestContext.Current.CancellationToken), f => f.Id == expiredFileId);
        Assert.DoesNotContain(await verifyCtx.SecretFileLinks.ToListAsync(TestContext.Current.CancellationToken), l => l.FileId == expiredFileId);
        Assert.DoesNotContain(await verifyCtx.ProfileFileLinks.ToListAsync(TestContext.Current.CancellationToken), l => l.FileId == expiredFileId);
        Assert.Contains(await verifyCtx.StoredFiles.ToListAsync(TestContext.Current.CancellationToken), f => f.Id == retainedFileId);
        Assert.Contains(await verifyCtx.SecretFileLinks.ToListAsync(TestContext.Current.CancellationToken), l => l.FileId == retainedFileId);

        CryptographicOperations.ZeroMemory(key.AsSpan());
    }
}
