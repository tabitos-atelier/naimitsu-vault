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

namespace NaimitsuVault.Tests;

/// <summary>
/// Route A restricted read-only mode recovery via emergency access code (QR+PIN)
/// (AuthService.EmergencyAccessUnlockAsync).
///
/// TC-EAC-01: Route A recovery succeeds with a valid QR+PIN.
///            Verifies in-memory recovery of DEK/K_shared, IsReadOnlyRestricted=true, and
///            zero physical writes to either the vault DB or the unified DB.
/// TC-EAC-02: PIN mismatch → WrongPin (AES-GCM authentication tag mismatch).
/// TC-EAC-03: Malformed QR scheme → InvalidQr (no DB access at all).
/// TC-EAC-04: No matching vault under data/ → NoMatchingVault.
/// TC-EAC-05: GenerateEmergencyAccessCodeAsync alone (Phase 1) does not persist to the DB;
///            only CommitEmergencyAccessCodeAsync (Phase 2) does.
/// TC-EAC-06: The suggested QR filename uses a yyyyMMdd_HHmmss timestamp, not a content hash.
/// TC-EAC-07: NoMatchingVault leaves no pooled connection open on the scanned candidate files.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class EmergencyAccessUnlockTests : IDisposable
{
    private readonly string _dataDir;

    public EmergencyAccessUnlockTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"naimitsu_eac_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Generates a valid vault file name from an "nkdb" prefix + 32 random bytes.
    /// The 32-byte portion is used directly for the FileHash match check against
    /// VaultRegistry.EncryptedPayload.
    /// </summary>
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

    // ── TC-EAC-01 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EmergencyAccessUnlock_ValidQrAndPin_RecoversInMemory_WithZeroPhysicalWrites()
    {
        // Arrange — build a real file-backed vault DB
        var (fileName, fileHashBytes) = MakeValidVaultFileName();
        var dbPath  = Path.Combine(_dataDir, fileName);
        var options = CreateFileBackedVaultSchema(dbPath);

        var connProvider = new RecordingConnectionProvider();
        connProvider.SetActiveVault(dbPath);
        var factory = new SharedOptionsAppDbContextFactory(options);
        var crypto  = new CryptoService(NullLogger<CryptoService>.Instance);
        var session = new AppSession();

        var originalDek = new byte[32];
        RandomNumberGenerator.Fill(originalDek);
        session.SetKey(originalDek);

        // Seed VaultRegistry[3] into the unified DB, encrypted with K_shared (Bug-3 K_shared recovery path)
        using var unifiedDb = TestUnifiedDb.Create();
        var kShared = new byte[32];
        RandomNumberGenerator.Fill(kShared);
        await using (var udb = unifiedDb.Factory.CreateDbContext())
        {
            var payloadJson = JsonSerializer.SerializeToUtf8Bytes(
                new VaultPayload(
                    Convert.ToBase64String(fileHashBytes),
                    Convert.ToBase64String(new byte[32]),
                    Convert.ToBase64String(new byte[32])),
                MultiVaultJsonContext.Default.VaultPayload);
            udb.VaultRegistries.Add(new VaultRegistry
            {
                DbNumber         = 3,
                EncryptedPayload = crypto.Encrypt(payloadJson, kShared),
            });
            await udb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var auditLog = new StubAuditLogService();
        var auth = new AuthService(
            factory, unifiedDb.Factory, connProvider, crypto, session,
            auditLog, null!, NullLogger<AuthService>.Instance);

        const string pin = "246810";
        var (qrPayload, _, wrappedDek) = await auth.GenerateEmergencyAccessCodeAsync(pin);
        await auth.CommitEmergencyAccessCodeAsync(wrappedDek);

        // Wrap KSharedEac(0x1004) with vault_DEK and seed it
        await using (var ctx = factory.CreateDbContext())
        {
            ctx.Metadata.Add(new VaultMetadata
            {
                ConfigKey   = VaultMetadataKey.KSharedEac,
                ConfigValue = crypto.Encrypt(kShared, originalDek),
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        var dbBytesBeforeRecovery = File.ReadAllBytes(dbPath);

        // Simulate a lockout state where the normal password would not work
        session.Lock();

        // Act
        var result = await auth.EmergencyAccessUnlockAsync(qrPayload, pin, _dataDir, TestContext.Current.CancellationToken);

        // Assert — Step 4-6: in-memory recovery (DEK, K_shared, read-only mode flag)
        Assert.Equal(EmergencyAccessResult.Success, result);
        Assert.True(session.IsUnlocked);
        Assert.Equal(originalDek, session.GetKey().Span.ToArray());
        Assert.True(session.IsReadOnlyRestricted);
        Assert.True(session.HasKShared);
        Assert.Equal(kShared, session.GetKShared().Span.ToArray());
        Assert.Equal(3, session.CurrentVaultDbNumber);
        Assert.Equal(3, session.DisplayedVaultNumber);

        // Assert — vault DB file is byte-for-byte unchanged (Auth/CryptoMode update fully skipped)
        SqliteConnection.ClearAllPools();
        var dbBytesAfterRecovery = File.ReadAllBytes(dbPath);
        Assert.Equal(dbBytesBeforeRecovery, dbBytesAfterRecovery);

        // Assert — unified DB's VaultRegistries count is also unchanged (KSharedSlot persistence skipped)
        await using var verifyUdb = unifiedDb.Factory.CreateDbContext();
        Assert.Single(await verifyUdb.VaultRegistries.ToListAsync(TestContext.Current.CancellationToken));

        // Regression coverage: EmergencyAccessCodeExecuted (0x7000) was defined in AuditEventCode
        // with a matching payload record but never actually wired to LogAsync from any call site.
        Assert.Contains(auditLog.Calls, c => c.Code == AuditEventCode.EmergencyAccessCodeExecuted);

        CryptographicOperations.ZeroMemory(originalDek);
        CryptographicOperations.ZeroMemory(kShared);
    }

    // ── TC-EAC-02 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EmergencyAccessUnlock_WrongPin_ReturnsWrongPin()
    {
        var (fileName, _) = MakeValidVaultFileName();
        var dbPath  = Path.Combine(_dataDir, fileName);
        var options = CreateFileBackedVaultSchema(dbPath);

        var connProvider = new RecordingConnectionProvider();
        connProvider.SetActiveVault(dbPath);
        var factory = new SharedOptionsAppDbContextFactory(options);
        var crypto  = new CryptoService(NullLogger<CryptoService>.Instance);
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var auth = new AuthService(
            factory, new ExplodingUnifiedDbContextFactory(), connProvider, crypto, session,
            new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);

        var (qrPayload, _, wrappedDek) = await auth.GenerateEmergencyAccessCodeAsync("123456");
        await auth.CommitEmergencyAccessCodeAsync(wrappedDek);
        session.Lock();

        var result = await auth.EmergencyAccessUnlockAsync(qrPayload, "999999", _dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyAccessResult.WrongPin, result);
        Assert.False(session.IsUnlocked);
    }

    // ── TC-EAC-03 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EmergencyAccessUnlock_InvalidQrScheme_ReturnsInvalidQr()
    {
        var session = new AppSession();
        var auth = new AuthService(
            new FaultInjectionDbContextFactory(
                new InvalidOperationException("vault DB accessed — InvalidQr should short-circuit before any DB I/O")),
            new ExplodingUnifiedDbContextFactory(),
            new NullConnectionProvider(),
            new IdentityCryptoService(),
            session,
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);

        var result = await auth.EmergencyAccessUnlockAsync(
            "https://example.com/not-a-recovery-qr", "123456", _dataDir, TestContext.Current.CancellationToken);

        Assert.Equal(EmergencyAccessResult.InvalidQr, result);
    }

    // ── TC-EAC-04 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EmergencyAccessUnlock_NoVaultInDataDir_ReturnsNoMatchingVault()
    {
        // The QR/PIN themselves are generated normally, but the target vault file is placed outside _dataDir
        var outsideDir = Path.Combine(Path.GetTempPath(), $"naimitsu_eac_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var (fileName, _) = MakeValidVaultFileName();
            var dbPath  = Path.Combine(outsideDir, fileName);
            var options = CreateFileBackedVaultSchema(dbPath);

            var connProvider = new RecordingConnectionProvider();
            connProvider.SetActiveVault(dbPath);
            var factory = new SharedOptionsAppDbContextFactory(options);
            var crypto  = new CryptoService(NullLogger<CryptoService>.Instance);
            var session = new AppSession();
            session.SetKey(new byte[32]);

            var auth = new AuthService(
                factory, new ExplodingUnifiedDbContextFactory(), connProvider, crypto, session,
                new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);

            var (qrPayload, _, wrappedDek) = await auth.GenerateEmergencyAccessCodeAsync("123456");
            await auth.CommitEmergencyAccessCodeAsync(wrappedDek);
            session.Lock();

            // Let the scan run against an empty _dataDir
            var result = await auth.EmergencyAccessUnlockAsync(qrPayload, "123456", _dataDir, TestContext.Current.CancellationToken);

            Assert.Equal(EmergencyAccessResult.NoMatchingVault, result);
            Assert.False(session.IsUnlocked);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    // ── TC-EAC-07 ────────────────────────────────────────────────────────
    // Regression coverage: the brute-force scan opened one pooled raw SqliteConnection per candidate
    // file, but SqliteConnection.ClearAllPools() only ran on the success path. On NoMatchingVault the
    // pooled handles stayed open on every scanned file (Windows: no FILE_SHARE_DELETE), so a candidate
    // file could not be deleted/replaced afterwards until something else happened to clear the pools.

    [Fact]
    public async Task EmergencyAccessUnlock_NoMatchingVault_LeavesScannedCandidateFilesUnlocked()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"naimitsu_eac_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            // Vault A (outside data/) issues the QR that will be presented.
            var (fileNameA, _) = MakeValidVaultFileName();
            var dbPathA  = Path.Combine(outsideDir, fileNameA);
            var optionsA = CreateFileBackedVaultSchema(dbPathA);
            var connProviderA = new RecordingConnectionProvider();
            connProviderA.SetActiveVault(dbPathA);
            var crypto   = new CryptoService(NullLogger<CryptoService>.Instance);
            var sessionA = new AppSession();
            sessionA.SetKey(new byte[32]);
            var authA = new AuthService(
                new SharedOptionsAppDbContextFactory(optionsA), new ExplodingUnifiedDbContextFactory(),
                connProviderA, crypto, sessionA, new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);
            var (qrPayload, _, wrappedDekA) = await authA.GenerateEmergencyAccessCodeAsync("123456");
            await authA.CommitEmergencyAccessCodeAsync(wrappedDekA);
            sessionA.Lock();

            // Vault B (inside data/) has its own, different emergency access code: the scan reads it
            // successfully (opening a raw connection) but cannot decrypt it with A's RK.
            var (fileNameB, _) = MakeValidVaultFileName();
            var dbPathB  = Path.Combine(_dataDir, fileNameB);
            var optionsB = CreateFileBackedVaultSchema(dbPathB);
            var connProviderB = new RecordingConnectionProvider();
            connProviderB.SetActiveVault(dbPathB);
            var sessionB = new AppSession();
            sessionB.SetKey(new byte[32]);
            var authB = new AuthService(
                new SharedOptionsAppDbContextFactory(optionsB), new ExplodingUnifiedDbContextFactory(),
                connProviderB, crypto, sessionB, new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);
            var (_, _, wrappedDekB) = await authB.GenerateEmergencyAccessCodeAsync("123456");
            await authB.CommitEmergencyAccessCodeAsync(wrappedDekB);
            sessionB.Lock();

            // Drop the EF Core pools left by the setup above so only the scan's own connections can matter.
            SqliteConnection.ClearAllPools();

            var result = await authA.EmergencyAccessUnlockAsync(qrPayload, "123456", _dataDir, TestContext.Current.CancellationToken);

            Assert.Equal(EmergencyAccessResult.NoMatchingVault, result);
            // Would throw IOException (sharing violation) if the scan left a pooled handle open on B.
            File.Delete(dbPathB);
            Assert.False(File.Exists(dbPathB));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    // ── TC-EAC-05 ────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateEmergencyAccessCodeAsync_WithoutCommit_DoesNotPersistToDb()
    {
        var (fileName, _) = MakeValidVaultFileName();
        var dbPath  = Path.Combine(_dataDir, fileName);
        var options = CreateFileBackedVaultSchema(dbPath);

        var connProvider = new RecordingConnectionProvider();
        connProvider.SetActiveVault(dbPath);
        var factory = new SharedOptionsAppDbContextFactory(options);
        var crypto  = new CryptoService(NullLogger<CryptoService>.Instance);
        var session = new AppSession();
        session.SetKey(new byte[32]);

        var auth = new AuthService(
            factory, new ExplodingUnifiedDbContextFactory(), connProvider, crypto, session,
            new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);

        // Phase 1 alone (simulating a cancelled QR file save) must leave the DB untouched,
        // so that reissuing while a previously-committed code still exists can never
        // silently invalidate it before the new QR has actually been saved to disk.
        var (_, _, wrappedDek) = await auth.GenerateEmergencyAccessCodeAsync("123456");
        Assert.False(await auth.HasEmergencyAccessCodeAsync());

        // Phase 2 commits explicitly.
        await auth.CommitEmergencyAccessCodeAsync(wrappedDek);
        Assert.True(await auth.HasEmergencyAccessCodeAsync());
    }

    // ── TC-EAC-06 ────────────────────────────────────────────────────────
    // The suggested QR filename uses a yyyyMMdd_HHmmss timestamp (matching AutoBackupService/
    // AuthService.Restore's naming convention), not the retired content-hash thumbprint.

    [Fact]
    public async Task GenerateEmergencyAccessCodeAsync_SuggestedFileName_UsesDateTimeNotHash()
    {
        var (fileName, _) = MakeValidVaultFileName();
        var dbPath  = Path.Combine(_dataDir, fileName);
        var options = CreateFileBackedVaultSchema(dbPath);

        var connProvider = new RecordingConnectionProvider();
        connProvider.SetActiveVault(dbPath);
        var factory = new SharedOptionsAppDbContextFactory(options);
        var crypto  = new CryptoService(NullLogger<CryptoService>.Instance);
        var session = new AppSession { DisplayedVaultNumber = 3 };
        session.SetKey(new byte[32]);

        var auth = new AuthService(
            factory, new ExplodingUnifiedDbContextFactory(), connProvider, crypto, session,
            new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);

        var (_, suggestedFileName, _) = await auth.GenerateEmergencyAccessCodeAsync("123456");

        Assert.Matches(@"^Naimitsu_vlt3_\d{8}_\d{6}\.png$", suggestedFileName);
    }
}
