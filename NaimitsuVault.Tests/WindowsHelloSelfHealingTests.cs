// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Tests;

/// <summary>
/// End-to-end regression coverage (TC-WH-22..23) for the 2026-08-28 fix that wires the same
/// shadow-based self-healing used by the password path (EnterVaultCoreAsync) into both stages of
/// the Windows Hello unlock flow (AcquireKSharedWithHelloAsync, EnterVaultWithHelloAsync). Before
/// this fix, a corrupted-but-present vault DB made Hello fail outright with no recovery attempt at
/// all, and the UI reported a generic "Hello auth failed" instead of the corruption-specific
/// message the password path already showed.
///
/// AcquireKSharedWithHelloAsync (unlike EnterVaultWithHelloAsync) takes no dataDir parameter - it
/// always resolves AppDomain.CurrentDomain.BaseDirectory/data internally (exactly like production,
/// see UnlockViewModel). Files are placed there with randomly generated 48-character names to avoid
/// collisions with other tests, and Dispose() removes only the specific files this test created.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class WindowsHelloSelfHealingTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    private readonly List<string> _ownedFiles = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _ownedFiles)
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + "-shm"); } catch { }
            try { File.Delete(path + "-wal"); } catch { }
        }
    }

    private static (string FileName, byte[] HashBytes) MakeValidVaultFileName()
    {
        var raw = new byte[36];
        "nkdb"u8.CopyTo(raw.AsSpan(0, 4));
        RandomNumberGenerator.Fill(raw.AsSpan(4));
        var fileName = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (fileName, raw.AsSpan(4, 32).ToArray());
    }

    private static DbContextOptions<AppDbContext> CreateFileBackedVaultSchema(string dbPath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        using var ctx = new AppDbContext(options, new SessionGenerationGuardStub());
        ctx.Database.EnsureCreated();
        return options;
    }

    private async Task<(string DbPath, byte[] FileHashBytes, IDbContextFactory<AppDbContext> Factory, AppSession Session)>
        SeedHelloVaultAsync()
    {
        Directory.CreateDirectory(_dataDir);
        var (fileName, fileHashBytes) = MakeValidVaultFileName();
        var dbPath = Path.Combine(_dataDir, fileName);
        _ownedFiles.Add(dbPath);
        var options = CreateFileBackedVaultSchema(dbPath);
        var factory = new SharedOptionsAppDbContextFactory(options);
        var session = new AppSession();

        await using (var vdb = factory.CreateDbContext())
        {
            await WinHelloDbSeeder.SeedWrappedDekAsync(vdb); // seeds VaultDEKHello with its own random DEK
        }

        return (dbPath, fileHashBytes, factory, session);
    }

    private async Task<(AuthService Auth, StubAuditLogService AuditLog)> BuildHelloAuthAsync(
        IDbContextFactory<AppDbContext> factory, byte[] fileHashBytes, int dbNumber, AppSession session,
        TestUnifiedDb unifiedDb)
    {
        byte[] kShared;
        await using (var uctx = unifiedDb.Factory.CreateDbContext())
        {
            kShared = await WinHelloDbSeeder.SeedUnifiedDbForHelloAsync(uctx); // seeds DbNumber=1 with an all-zero FileHash by default
        }
        // Overwrite VaultRegistries[dbNumber]'s payload so its FileHash matches the real file-backed vault above
        await using (var uctx = unifiedDb.Factory.CreateDbContext())
        {
            var reg = await uctx.VaultRegistries.SingleAsync(v => v.DbNumber == dbNumber, TestContext.Current.CancellationToken);
            var payloadJson = JsonSerializer.SerializeToUtf8Bytes(
                new VaultPayload(
                    Convert.ToBase64String(fileHashBytes),
                    Convert.ToBase64String(new byte[32]),
                    Convert.ToBase64String(new byte[32])),
                MultiVaultJsonContext.Default.VaultPayload);
            var encPayload = new byte[IdentityCryptoService.HeaderSize + payloadJson.Length];
            payloadJson.CopyTo(encPayload, IdentityCryptoService.HeaderSize);
            reg.EncryptedPayload = encPayload;
            await uctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        CryptographicOperations.ZeroMemory(kShared);

        var connProvider = new RecordingConnectionProvider();
        var crypto       = new IdentityCryptoService();
        var adapter      = new FakeUserConsentVerifierAdapter
        {
            AvailabilityResult = UserConsentVerifierAvailability.Available,
            VerificationResult = UserConsentVerificationResult.Verified,
        };
        var dpapi     = new FakeProtectedDataService();
        var auditLog  = new StubAuditLogService();
        var auth  = new AuthService(
            factory, unifiedDb.Factory, connProvider, crypto, session,
            auditLog, null!, NullLogger<AuthService>.Instance);
        auth.SetHelloAdapter(adapter);
        auth.SetProtectedData(dpapi);
        return (auth, auditLog);
    }

    // ── TC-WH-22 ─────────────────────────────────────────────────────────
    // Corrupted-but-present vault DB (Hello-registered) + a healthy VAULT_SHADOW ->
    // AcquireKSharedWithHelloAsync recovers it and reports the vault; EnterVaultWithHelloAsync
    // then successfully unlocks it. Also regression coverage for the audit-log gap: the forced
    // 0xFF02 (VaultDbAutoRecovered) entry must be written on the Hello success path too - it was
    // previously only wired into the password path (EnterVaultCoreAsync).

    [Fact]
    public async Task Hello_CorruptedVaultWithHealthyShadow_AutoRecoversAndUnlocks()
    {
        const int dbNumber = 1;
        using var unifiedDb = TestUnifiedDb.Create();
        var (dbPath, fileHashBytes, factory, session) = await SeedHelloVaultAsync();

        // Snapshot the healthy vault DB as its shadow (VAULT_SHADOW magic) before corrupting the origin.
        // Uses the SQLite online backup API (same technique as ShadowFileService.WriteShadow), not a
        // naive File.Copy, so any data not yet checkpointed out of the WAL is captured correctly.
        var shadowPath = Path.Combine(_dataDir, ShadowFileService.GenerateBase64UrlName());
        _ownedFiles.Add(shadowPath);
        using (var src = new SqliteConnection($"Data Source={dbPath}"))
        using (var dst = new SqliteConnection($"Data Source={shadowPath}"))
        {
            src.Open();
            dst.Open();
            src.BackupDatabase(dst);
        }
        SqliteConnection.ClearAllPools();
        ShadowFileService.SetPragmaUserVersion(shadowPath, ShadowFileService.VAULT_SHADOW);

        // Corrupt the origin (present but unreadable as SQLite)
        SqliteConnection.ClearAllPools();
        await File.WriteAllBytesAsync(dbPath, [], TestContext.Current.CancellationToken);

        var (auth, auditLog) = await BuildHelloAuthAsync(factory, fileHashBytes, dbNumber, session, unifiedDb);

        // Act: Stage 1 - discovery must recover the corrupted vault from its shadow
        var registeredVaults = await auth.AcquireKSharedWithHelloAsync();

        Assert.Contains(dbNumber, registeredVaults);
        Assert.True(session.VaultDbAutoRecovered, "Stage 1 must auto-recover the corrupted vault");
        Assert.Equal(dbNumber, session.VaultDbAutoRecoveredNumber);
        Assert.False(session.VaultDbUnrecoverable);

        // Act: Stage 2 - entering the now-recovered vault must succeed
        var entered = await auth.EnterVaultWithHelloAsync(dbNumber, _dataDir);

        Assert.True(entered, "Stage 2 must successfully unlock the recovered vault");
        Assert.True(session.IsUnlocked);

        // The forced audit-log entry (0xFF02) must be written on the Hello success path, exactly
        // like the password path already does in EnterVaultCoreAsync.
        Assert.Equal(1, auditLog.AutoRecoveredCount);
        Assert.Contains(auditLog.Calls, c => c.Code == AuditEventCode.VaultDbAutoRecovered);
        Assert.False(session.VaultDbAutoRecovered, "The flag must be cleared after the audit entry is written");
    }

    // ── TC-WH-23 ─────────────────────────────────────────────────────────
    // Corrupted vault DB with no usable shadow at all -> AcquireKSharedWithHelloAsync excludes it
    // and sets VaultDbUnrecoverable (so the UI can show the corruption-specific message instead of
    // a generic "Hello auth failed").

    [Fact]
    public async Task Hello_CorruptedVaultWithNoShadow_ReportsUnrecoverable()
    {
        const int dbNumber = 1;
        using var unifiedDb = TestUnifiedDb.Create();
        var (dbPath, fileHashBytes, factory, session) = await SeedHelloVaultAsync();

        // Corrupt the origin; no shadow file is created at all
        SqliteConnection.ClearAllPools();
        await File.WriteAllBytesAsync(dbPath, [], TestContext.Current.CancellationToken);

        var (auth, _) = await BuildHelloAuthAsync(factory, fileHashBytes, dbNumber, session, unifiedDb);

        var registeredVaults = await auth.AcquireKSharedWithHelloAsync();

        Assert.DoesNotContain(dbNumber, registeredVaults);
        Assert.False(session.VaultDbAutoRecovered);
        Assert.True(session.VaultDbUnrecoverable, "No usable shadow -> must be reported as unrecoverable, not a silent skip");
        Assert.Equal(dbNumber, session.VaultDbUnrecoverableNumber);
    }
}
