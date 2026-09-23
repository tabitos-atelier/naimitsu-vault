// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
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
/// Regression test for Bug-5: after a master password change, VaultRegistries[N].EncryptedPayload's
/// VaultSaltPwd was not updated to match, causing subsequent multi-vault unlocks
/// (EnterVaultAsync) to derive the KEK from the stale salt and fail.
///
/// TC-PWC-01: After password change → lock → AcquireKSharedAsync + EnterVaultAsync with the
///            new password must succeed (before the fix, EnterVaultAsync returned false,
///            reproducing the bug).
///
/// [Scope note] This test intentionally verifies only the single vault DbNumber=1.
/// Each vault (1-3) can have a completely independent password by design, and
/// ChangeMasterPasswordAsync correctly updates only session.CurrentVaultDbNumber
/// (the currently selected vault). Extending this test to seed multiple vaults
/// simultaneously and assert that "inactive vaults can also be opened with the new
/// password" would violate the spec (other vaults correctly remain openable only
/// with their old password).
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class PasswordChangeVaultRegistrySyncTests : IDisposable
{
    private readonly string _dataDir;

    public PasswordChangeVaultRegistrySyncTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"naimitsu_pwc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
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

    private static byte[] BuildAuthDataJson(byte[] salt, byte[] wrappedDek) =>
        Encoding.UTF8.GetBytes(
            $$"""{"Salt":"{{Convert.ToBase64String(salt)}}","WrappedDek":"{{Convert.ToBase64String(wrappedDek)}}"}""");

    private static byte[] ComputeCoarseHash(string password)
    {
        int prefixLen = Math.Min(3, password.Length);
        return SHA256.HashData(Encoding.UTF8.GetBytes(password[..prefixLen]))[..4];
    }

    private static byte[] BuildKSharedSlot(string password, byte[] unifiedSalt, byte[] kShared, CryptoService crypto)
    {
        var kMaster = new byte[32];
        AuthService.DeriveArgon2id(password, unifiedSalt, kMaster);
        var wrapData = crypto.Encrypt(kShared, kMaster);
        var coarse = ComputeCoarseHash(password);

        var blob = new byte[96];
        coarse.CopyTo(blob, 0);
        unifiedSalt.CopyTo(blob, 4);
        wrapData.CopyTo(blob, 36);
        return blob;
    }

    [Fact]
    public async Task ChangeMasterPassword_ThenLockAndReenterViaMultiVaultFlow_Succeeds()
    {
        const int dbNumber = 1;
        const string oldPassword = "old-password-1";
        const string newPassword = "new-password-2";

        // Arrange: real file-backed vault DB (seed the Auth blob with the original password)
        var (fileName, fileHashBytes) = MakeValidVaultFileName();
        var dbPath = Path.Combine(_dataDir, fileName);
        var options = CreateFileBackedVaultSchema(dbPath);

        var connProvider = new RecordingConnectionProvider();
        connProvider.SetActiveVault(dbPath);
        var factory = new SharedOptionsAppDbContextFactory(options);
        var crypto = new CryptoService(NullLogger<CryptoService>.Instance);
        var session = new AppSession();

        var oldSalt = crypto.GenerateSalt(32);
        var oldKek = new byte[32];
        crypto.DeriveKey(oldPassword, oldSalt, oldKek);
        var vaultDek = new byte[32];
        RandomNumberGenerator.Fill(vaultDek);
        var oldWrappedDek = crypto.Encrypt(vaultDek, oldKek);

        await using (var vdb = factory.CreateDbContext())
        {
            vdb.Metadata.Add(new VaultMetadata
            {
                ConfigKey   = VaultMetadataKey.Auth,
                ConfigValue = BuildAuthDataJson(oldSalt, oldWrappedDek),
            });
            await vdb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Unified DB: seed VaultRegistries[1] (VaultSaltPwd=oldSalt) and KSharedSlot_1 (derived with oldPassword)
        using var unifiedDb = TestUnifiedDb.Create();
        var kShared = new byte[32];
        RandomNumberGenerator.Fill(kShared);

        await using (var udb = unifiedDb.Factory.CreateDbContext())
        {
            var payloadJson = JsonSerializer.SerializeToUtf8Bytes(
                new VaultPayload(
                    Convert.ToBase64String(fileHashBytes),
                    Convert.ToBase64String(new byte[32]),
                    Convert.ToBase64String(oldSalt)),
                MultiVaultJsonContext.Default.VaultPayload);
            udb.VaultRegistries.Add(new VaultRegistry
            {
                DbNumber         = dbNumber,
                EncryptedPayload = crypto.Encrypt(payloadJson, kShared),
            });

            var unifiedSalt = crypto.GenerateSalt(32);
            udb.Metadata.Add(new UnifiedMetadata
            {
                ConfigKey   = dbNumber, // KSharedSlotForVault(1) == 1
                ConfigValue = BuildKSharedSlot(oldPassword, unifiedSalt, kShared, crypto),
            });

            await udb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var auth = new AuthService(
            factory, unifiedDb.Factory, connProvider, crypto, session,
            new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);

        // Precondition check: entering the normal multi-vault flow with the old password must succeed
        var matchedBefore = await auth.AcquireKSharedAsync(oldPassword, TestContext.Current.CancellationToken);
        Assert.Contains(dbNumber, matchedBefore);
        var enteredBefore = await auth.EnterVaultAsync(dbNumber, oldPassword, _dataDir, TestContext.Current.CancellationToken);
        Assert.True(enteredBefore, "Precondition: entry with the old password must succeed");
        Assert.Equal(dbNumber, session.CurrentVaultDbNumber);

        // Act: change the password
        var changed = await auth.ChangeMasterPasswordAsync(oldPassword, newPassword);
        Assert.True(changed);

        // Lock (clears DEK, K_shared, and CurrentVaultDbNumber)
        session.Lock();
        Assert.False(session.HasKShared);
        Assert.Null(session.CurrentVaultDbNumber);

        // Assert: K_shared recovery + vault entry must succeed again with the new password (Bug-5 regression)
        var matchedAfter = await auth.AcquireKSharedAsync(newPassword, TestContext.Current.CancellationToken);
        Assert.Contains(dbNumber, matchedAfter);

        var enteredAfter = await auth.EnterVaultAsync(dbNumber, newPassword, _dataDir, TestContext.Current.CancellationToken);
        Assert.True(enteredAfter, "After password change then lock, re-unlocking with the new password must succeed (Bug-5 regression)");
        Assert.Equal(dbNumber, session.CurrentVaultDbNumber);
        Assert.Equal(vaultDek, session.GetKey().Span.ToArray());

        CryptographicOperations.ZeroMemory(vaultDek);
        CryptographicOperations.ZeroMemory(kShared);
    }
}
