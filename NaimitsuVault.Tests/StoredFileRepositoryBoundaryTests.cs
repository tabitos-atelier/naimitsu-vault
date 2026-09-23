// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Common;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests;

/// <summary>
/// StoredFileRepository boundary values, crypto round-trip, and ValidateContentType tests
/// (TC-SFR-01 .. TC-SFR-12 + TC-SFR-17..25)
/// </summary>
public sealed class StoredFileRepositoryBoundaryTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static StoredFileRepository MakeRepo(TestDb db, ICryptoService? crypto = null)
        => new(db.Factory, crypto ?? new IdentityCryptoService(), NullLogger<StoredFileRepository>.Instance);

    private static DekScope MakeDek() => new(new byte[32], 32);

    private static StoredFile MinimalFile(string fileName = "test.png", int contentType = FileTypeCode.ImagePng)
        => new()
        {
            FileName       = Encoding.UTF8.GetBytes(fileName),
            ContentTypeCode    = contentType,
            FileSize       = 0,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UtcNow,
        };


    // ── TC-SFR-01 ─────────────────────────────────────────────────────────────
    // FileName is 20 bytes (<=28: under the AEAD header size) -> DecryptBlobToBytes's length guard kicks in -> FileName = []
    // NULL cannot be inserted due to the DB's NOT NULL constraint, so verify the equivalent boundary with a too-short blob.

    [Fact]
    public async Task GetByIdAsync_FileNameTooShortInDb_ReturnsEmptyFileName()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        await using var ctx = db.Factory.CreateDbContext();
        ctx.StoredFiles.Add(new StoredFile
        {
            FileName       = new byte[20],   // 20 bytes: under the AEAD minimum length of 28 -> undecryptable guard
            ContentTypeCode    = FileTypeCode.OctetStream,
            FileSize       = 0,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UnixEpoch,
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        int id = await ctx.StoredFiles.MaxAsync(f => f.Id, TestContext.Current.CancellationToken);

        var file = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(file);
        Assert.Empty(file!.FileName);
    }

    // ── TC-SFR-02 ─────────────────────────────────────────────────────────────
    // FileName is exactly 28 bytes (AEAD header only) -> DecryptBlobToBytes's guard kicks in

    [Fact]
    public async Task GetByIdAsync_FileNameExactly28Bytes_ReturnsEmptyFileName()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        await using var ctx = db.Factory.CreateDbContext();
        ctx.StoredFiles.Add(new StoredFile
        {
            FileName       = new byte[28],   // 28 bytes: nonce+tag only = undecryptable
            ContentTypeCode    = FileTypeCode.OctetStream,
            FileSize       = 0,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UnixEpoch,
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        int id = await ctx.StoredFiles.MaxAsync(f => f.Id, TestContext.Current.CancellationToken);

        var file = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(file);
        Assert.Empty(file!.FileName);
    }

    // ── TC-SFR-03 ─────────────────────────────────────────────────────────────
    // FileName is 29 bytes (28-byte header + 1 plaintext byte) -> decrypts successfully with IdentityCrypto

    [Fact]
    public async Task GetByIdAsync_FileNameIs29Bytes_DecryptsTo1Byte()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);  // IdentityCryptoService
        var dek        = MakeDek();

        await using var ctx = db.Factory.CreateDbContext();
        // IdentityCrypto.Encrypt([0xAB]) = [28 zeros] + [0xAB]
        var enc = new byte[29];
        enc[28] = 0xAB;

        ctx.StoredFiles.Add(new StoredFile
        {
            FileName       = enc,
            ContentTypeCode    = FileTypeCode.OctetStream,  // no extension, so no consistency check
            FileSize       = 0,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UnixEpoch,
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        int id = await ctx.StoredFiles.MaxAsync(f => f.Id, TestContext.Current.CancellationToken);

        var file = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(file);
        Assert.Equal([0xAB], file!.FileName);
    }

    // ── TC-SFR-04 ─────────────────────────────────────────────────────────────
    // No extension (no dot) -> ValidateContentType is skipped

    [Fact]
    public async Task GetAllAsync_NoExtension_ValidateContentTypeSkipped_NoThrow()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        var file = MinimalFile("id_rsa", FileTypeCode.SshPrivateKey);
        await repo.AddAsync(file, dek);  // encrypted with IdentityCrypto and saved

        var ex = await Record.ExceptionAsync(() => repo.GetAllAsync(dek));
        Assert.Null(ex);
    }

    // ── TC-SFR-05 ─────────────────────────────────────────────────────────────
    // Unregistered extension (.xyz) -> ValidateContentType only logs a warning, no exception

    [Fact]
    public async Task GetAllAsync_UnknownExtension_DoesNotThrow()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        var file = new StoredFile
        {
            FileName       = Encoding.UTF8.GetBytes("data.xyz"),
            ContentTypeCode    = FileTypeCode.OctetStream,
            FileSize       = 0,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UnixEpoch,
        };
        await repo.AddAsync(file, dek);

        var ex = await Record.ExceptionAsync(() => repo.GetAllAsync(dek));
        Assert.Null(ex);
    }

    // ── TC-SFR-06 ─────────────────────────────────────────────────────────────
    // .png + ContentTypeCode=ImagePng -> passes ValidateContentType

    [Fact]
    public async Task GetByIdAsync_CorrectContentType_DoesNotThrow()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        var file = MinimalFile("photo.png", FileTypeCode.ImagePng);
        int id   = await repo.AddAsync(file, dek);

        var result = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(result);
        Assert.Equal(FileTypeCode.ImagePng, result!.ContentTypeCode);
    }

    // ── TC-SFR-07 ─────────────────────────────────────────────────────────────
    // .png + ContentTypeCode=ImageJpeg -> ValidateContentType sets IsQuarantined instead of throwing,
    // so one bad row can never abort a batch read (GetAllAsync/GetByIdsAsync) for the rest.

    [Fact]
    public async Task GetByIdAsync_ContentTypeMismatch_ReturnsQuarantinedInsteadOfThrowing()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        // IdentityCrypto: Encrypt(UTF8("photo.png")) = [28 zeros] + UTF8("photo.png")
        var rawName = Encoding.UTF8.GetBytes("photo.png");
        var enc     = new byte[28 + rawName.Length];
        rawName.CopyTo(enc.AsSpan(28));

        await using var ctx = db.Factory.CreateDbContext();
        ctx.StoredFiles.Add(new StoredFile
        {
            FileName       = enc,
            ContentTypeCode    = FileTypeCode.ImageJpeg,   // mismatches .png (simulating tampering)
            FileSize       = 0,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UnixEpoch,
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        int id = await ctx.StoredFiles.MaxAsync(f => f.Id, TestContext.Current.CancellationToken);

        var file = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(file);
        Assert.True(file!.IsQuarantined);
        Assert.Equal(FileTypeCode.ImageJpeg, file.ContentTypeCode);   // DB row itself is left untouched
    }

    // ── TC-SFR-08 ─────────────────────────────────────────────────────────────
    // Auth tag tampered -> CryptographicException is caught and FileName = []

    [Fact]
    public async Task GetByIdAsync_TamperedBlob_ReturnsEmptyFileName()
    {
        using var db   = TestDb.Create();
        var crypto     = new CryptoService(NullLogger<CryptoService>.Instance);
        var repo       = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var dek        = MakeDek();

        // Generate a valid ciphertext, then tamper with it
        var cipher = crypto.Encrypt(Encoding.UTF8.GetBytes("secret.pdf"), dek.Span);
        cipher[15] ^= 0xFF;  // corrupt the auth tag

        await using var ctx = db.Factory.CreateDbContext();
        ctx.StoredFiles.Add(new StoredFile
        {
            FileName       = cipher,
            ContentTypeCode    = FileTypeCode.OctetStream,
            FileSize       = 0,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UnixEpoch,
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        int id = await ctx.StoredFiles.MaxAsync(f => f.Id, TestContext.Current.CancellationToken);

        var file = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(file);
        Assert.Empty(file!.FileName);
    }

    // ── TC-SFR-09 ─────────────────────────────────────────────────────────────
    // AddAsync + GetByIdAsync real-crypto round-trip

    [Fact]
    public async Task AddThenGetById_RealCrypto_FileNamePreserved()
    {
        using var db   = TestDb.Create();
        var crypto     = new CryptoService(NullLogger<CryptoService>.Instance);
        var repo       = new StoredFileRepository(db.Factory, crypto, NullLogger<StoredFileRepository>.Instance);
        var dek        = MakeDek();

        var original   = MinimalFile("document.pdf", FileTypeCode.Pdf);
        int id         = await repo.AddAsync(original, dek);

        var loaded = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(loaded);
        Assert.Equal("document.pdf", Encoding.UTF8.GetString(loaded!.FileName));
    }

    // ── TC-SFR-10 ─────────────────────────────────────────────────────────────
    // AddAsync + GetAllAsync returns the file

    [Fact]
    public async Task AddThenGetAll_ReturnsAddedFile()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        // FileHash has a UNIQUE constraint, so use different values for the 2 files
        var fa = MinimalFile("a.png", FileTypeCode.ImagePng); fa.FileHash[0] = 0x01;
        var fb = MinimalFile("b.png", FileTypeCode.ImagePng); fb.FileHash[0] = 0x02;
        await repo.AddAsync(fa, dek);
        await repo.AddAsync(fb, dek);

        var all = await repo.GetAllAsync(dek);
        Assert.Equal(2, all.Count);
    }

    // ── TC-SFR-11 ─────────────────────────────────────────────────────────────
    // FindByHashAsync retrieves the correct file from its hash

    [Fact]
    public async Task FindByHashAsync_ReturnsCorrectFile()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        var hash1 = new byte[32]; hash1[0] = 0xAA;
        var hash2 = new byte[32]; hash2[0] = 0xBB;

        var file1 = new StoredFile
        {
            FileName       = Encoding.UTF8.GetBytes("file1.png"),
            ContentTypeCode    = FileTypeCode.ImagePng,
            FileSize       = 100,
            FileHash       = hash1,
            FileModifiedAt = DateTime.UnixEpoch,
        };
        var file2 = new StoredFile
        {
            FileName       = Encoding.UTF8.GetBytes("file2.png"),
            ContentTypeCode    = FileTypeCode.ImagePng,
            FileSize       = 200,
            FileHash       = hash2,
            FileModifiedAt = DateTime.UnixEpoch,
        };
        await repo.AddAsync(file1, dek);
        await repo.AddAsync(file2, dek);

        var found = await repo.FindByHashAsync(hash1, dek);
        Assert.NotNull(found);
        Assert.Equal(100, found!.FileSize);
    }

    // ── TC-SFR-12 ─────────────────────────────────────────────────────────────
    // GetByIdAsync returns null after DeleteAsync

    [Fact]
    public async Task DeleteAsync_FileDisappears()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        int id = await repo.AddAsync(MinimalFile("to_delete.png", FileTypeCode.ImagePng), dek);

        await repo.DeleteAsync(id);

        var result = await repo.GetByIdAsync(id, dek);
        Assert.Null(result);
    }

    // ── TC-SFR-17 ──────────────────────────────────────────────────
    // FileName is fully restored via IdentityCryptoService

    [Fact]
    public async Task PassThroughA_IdentityCrypto_FileNameRoundtrip()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);  // IdentityCryptoService
        var dek        = MakeDek();

        var originalName = "テスト画像ファイル.png";
        var file = new StoredFile
        {
            FileName       = Encoding.UTF8.GetBytes(originalName),
            ContentTypeCode    = FileTypeCode.ImagePng,
            FileSize       = 1234,
            FileHash       = new byte[32],
            FileModifiedAt = DateTime.UnixEpoch,
        };
        int id = await repo.AddAsync(file, dek);

        var loaded = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(loaded);
        Assert.Equal(originalName, Encoding.UTF8.GetString(loaded!.FileName));
    }

    // ── TC-SFR-18 ──────────────────────────────────────────────────
    // ContentTypeCode consistency check passes normally via IdentityCryptoService

    [Fact]
    public async Task PassThroughB_IdentityCrypto_ContentTypeValidationPasses()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);  // IdentityCryptoService
        var dek        = MakeDek();

        var files = new[]
        {
            new StoredFile { FileName = Encoding.UTF8.GetBytes("photo.jpg"),  ContentTypeCode = FileTypeCode.ImageJpeg, FileHash = new byte[32], FileModifiedAt = DateTime.UnixEpoch },
            new StoredFile { FileName = Encoding.UTF8.GetBytes("doc.pdf"),    ContentTypeCode = FileTypeCode.Pdf,       FileHash = new byte[32], FileModifiedAt = DateTime.UnixEpoch },
            new StoredFile { FileName = Encoding.UTF8.GetBytes("cert.pem"),   ContentTypeCode = FileTypeCode.PemFile,   FileHash = new byte[32], FileModifiedAt = DateTime.UnixEpoch },
            new StoredFile { FileName = Encoding.UTF8.GetBytes("archive.zip"),ContentTypeCode = FileTypeCode.Zip,       FileHash = new byte[32], FileModifiedAt = DateTime.UnixEpoch },
        };

        foreach (var (f, i) in files.Select((f, i) => (f, i)))
        {
            f.FileHash[0] = (byte)(i + 1);
            await repo.AddAsync(f, dek);
        }

        var ex = await Record.ExceptionAsync(() => repo.GetAllAsync(dek));
        Assert.Null(ex);

        var all = await repo.GetAllAsync(dek);
        Assert.Equal(4, all.Count);
    }

    // ── TC-SFR-19 ─────────────────────────────────────────────────────────────
    // Row stuck at the pre-FileTypeCode-update value (OctetStream) for a now-registered extension
    // -> RepairContentTypeMismatchesAsync corrects it and the row is no longer quarantined

    [Fact]
    public async Task RepairContentTypeMismatchesAsync_DriftedRow_CorrectsAndUnblocksLoad()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);  // IdentityCryptoService
        var dek        = MakeDek();

        var file = MinimalFile("notes.txt", FileTypeCode.OctetStream);   // simulates a pre-fix row
        int id   = await repo.AddAsync(file, dek);

        var beforeRepair = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(beforeRepair);
        Assert.True(beforeRepair!.IsQuarantined);

        var corrections = await repo.RepairContentTypeMismatchesAsync(dek);
        var correction  = Assert.Single(corrections);
        Assert.Equal(id, correction.Id);
        Assert.Equal("notes.txt", correction.FileName);
        Assert.Equal(FileTypeCode.OctetStream, correction.OldContentType);
        Assert.Equal(FileTypeCode.PlainText, correction.NewContentType);

        var loaded = await repo.GetByIdAsync(id, dek);
        Assert.NotNull(loaded);
        Assert.Equal(FileTypeCode.PlainText, loaded!.ContentTypeCode);
        Assert.False(loaded.IsQuarantined);
    }

    // ── TC-SFR-20 ─────────────────────────────────────────────────────────────
    // Already-consistent rows are left untouched -> 0 corrections, no throw

    [Fact]
    public async Task RepairContentTypeMismatchesAsync_NoDrift_ReturnsZeroAndLeavesDataIntact()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        await repo.AddAsync(MinimalFile("photo.png", FileTypeCode.ImagePng), dek);

        var corrections = await repo.RepairContentTypeMismatchesAsync(dek);
        Assert.Empty(corrections);

        var ex = await Record.ExceptionAsync(() => repo.GetAllAsync(dek));
        Assert.Null(ex);
    }

    // ── TC-SFR-21 ─────────────────────────────────────────────────────────────
    // One quarantined row must never abort a batch read for the rest (the 2026-08-20 incident:
    // a single mismatched .txt attachment made the whole Gallery/Secret/TimeMachine load fail)

    [Fact]
    public async Task GetAllAsync_OneQuarantinedRowAmongMany_StillReturnsAllRowsFlagged()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        var good1 = MinimalFile("a.png", FileTypeCode.ImagePng); good1.FileHash[0] = 0x01;
        var good2 = MinimalFile("b.pdf", FileTypeCode.Pdf);      good2.FileHash[0] = 0x02;
        var bad   = MinimalFile("c.png", FileTypeCode.ImageJpeg); bad.FileHash[0]  = 0x03; // mismatched
        await repo.AddAsync(good1, dek);
        await repo.AddAsync(good2, dek);
        int badId = await repo.AddAsync(bad, dek);

        var all = await repo.GetAllAsync(dek);
        Assert.Equal(3, all.Count);
        Assert.Equal(2, all.Count(f => !f.IsQuarantined));
        Assert.True(all.Single(f => f.Id == badId).IsQuarantined);

        var byIds = await repo.GetByIdsAsync([badId], dek);
        Assert.Single(byIds);
        Assert.True(byIds[0].IsQuarantined);
    }

    // ── TC-SFR-22 ─────────────────────────────────────────────────────────────
    // SoftDeleteAsync stamps DeletedAt and atomically severs SecretFileLinks/ProfileFileLinks

    [Fact]
    public async Task SoftDeleteAsync_StampsDeletedAt_AndSeversLinks()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        int fileId = await repo.AddAsync(MinimalFile("linked.png", FileTypeCode.ImagePng), dek);
        await using (var ctx = db.Factory.CreateDbContext())
        {
            ctx.SecretFileLinks.Add(new SecretFileLink { SecretId = 1, FileId = fileId });
            ctx.ProfileFileLinks.Add(new ProfileFileLink { FileId = fileId });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await repo.SoftDeleteAsync(fileId);

        await using var verifyCtx = db.Factory.CreateDbContext();
        var row = await verifyCtx.StoredFiles.SingleAsync(f => f.Id == fileId, TestContext.Current.CancellationToken);
        Assert.NotNull(row.DeletedAt);
        Assert.False(await verifyCtx.SecretFileLinks.AnyAsync(l => l.FileId == fileId, TestContext.Current.CancellationToken));
        Assert.False(await verifyCtx.ProfileFileLinks.AnyAsync(l => l.FileId == fileId, TestContext.Current.CancellationToken));
    }

    // ── TC-SFR-23 ─────────────────────────────────────────────────────────────
    // UndeleteAsync clears DeletedAt; the file reappears in GetAllAsync (alive-only)

    [Fact]
    public async Task UndeleteAsync_ClearsDeletedAt_FileReappearsInGetAllAsync()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        int fileId = await repo.AddAsync(MinimalFile("restore.png", FileTypeCode.ImagePng), dek);
        await repo.SoftDeleteAsync(fileId);
        Assert.Empty(await repo.GetAllAsync(dek));

        await repo.UndeleteAsync(fileId);

        var alive = await repo.GetAllAsync(dek);
        Assert.Single(alive);
        Assert.Equal(fileId, alive[0].Id);
    }

    // ── TC-SFR-24 ─────────────────────────────────────────────────────────────
    // GetAllAsync excludes soft-deleted files; GetAllIncludingDeletedAsync includes them

    [Fact]
    public async Task GetAllAsync_ExcludesSoftDeleted_GetAllIncludingDeletedAsync_IncludesThem()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        var alive   = MinimalFile("alive.png", FileTypeCode.ImagePng);   alive.FileHash[0]   = 0x01;
        var deleted = MinimalFile("deleted.png", FileTypeCode.ImagePng); deleted.FileHash[0] = 0x02;
        await repo.AddAsync(alive, dek);
        int deletedId = await repo.AddAsync(deleted, dek);
        await repo.SoftDeleteAsync(deletedId);

        var aliveOnly = await repo.GetAllAsync(dek);
        Assert.Single(aliveOnly);
        Assert.DoesNotContain(aliveOnly, f => f.Id == deletedId);

        var everything = await repo.GetAllIncludingDeletedAsync(dek);
        Assert.Equal(2, everything.Count);
        Assert.Contains(everything, f => f.Id == deletedId);
    }

    // ── TC-SFR-25 ─────────────────────────────────────────────────────────────
    // FindByHashAsync matches a soft-deleted file too - the precondition GalleryViewModel's
    // auto-salvage logic (AddFilesAsync) relies on to detect "same content already in the trash".

    [Fact]
    public async Task FindByHashAsync_MatchesSoftDeletedFile()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var dek        = MakeDek();

        var hash = new byte[32]; hash[0] = 0xCC;
        var file = new StoredFile
        {
            FileName       = Encoding.UTF8.GetBytes("salvageable.png"),
            ContentTypeCode    = FileTypeCode.ImagePng,
            FileSize       = 100,
            FileHash       = hash,
            FileModifiedAt = DateTime.UnixEpoch,
        };
        int fileId = await repo.AddAsync(file, dek);
        await repo.SoftDeleteAsync(fileId);

        var found = await repo.FindByHashAsync(hash, dek);
        Assert.NotNull(found);
        Assert.Equal(fileId, found!.Id);
        Assert.NotNull(found.DeletedAt);
    }
}
