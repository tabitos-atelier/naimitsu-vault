// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Argon2id derivations run off the calling (UI) thread. That removes the implicit serialization the
/// frozen UI thread used to give, so a Lock(), a window close or a concurrent unlock can now land while a
/// derivation is in flight. These tests pin down that (1) the derivation really is off the caller's
/// thread, and (2) a derivation that finishes after such an event is wiped instead of being written into
/// the session, and that no multi-step write is left half-done.
///
/// Phase 1 - unlock path:
/// TC-KDO-01: AcquireKSharedAsync with an already-cancelled token throws and sets no K_shared.
/// TC-KDO-02: token cancelled after the derivation finished, before the session write → discarded.
/// TC-KDO-03: session locked after the derivation finished (re-auth-style start) → K_shared not resurrected.
/// TC-KDO-04: UnlockAsync returns while the derivation is still running (caller thread is free).
/// TC-KDO-05: Lock() during UnlockAsync's derivation → no DEK is written back (no zombie session).
/// TC-KDO-06: EnterVaultAsync cancelled after the derivation → OperationCanceledException, no DEK,
///            and SetActiveVault is undone.
/// TC-KDO-07: UnlockViewModel passes its cancellation token down; a cancelled unlock shows no error.
/// TC-KDO-08: UnlockWithWindowsHelloCommand is unavailable while another unlock is in flight.
///
/// Phase 2 - the remaining derivation sites:
/// TC-KDO-09: password change - Lock() during the derivations aborts before ANY write (Auth blob,
///            unified slot and registry all unchanged).
/// TC-KDO-10: password change - Lock() landing during the writes does not leave them half-done; the new
///            password still unlocks afterwards.
/// TC-KDO-11: add vault - Lock() during the derivations aborts before anything is created.
/// TC-KDO-12: add vault - Lock() landing during the writes still registers a correct slot (wrapping the
///            real K_shared, not a zeroed one) and does not switch a locked session.
/// TC-KDO-13: first-time setup with a cancelled token throws and releases K_shared.
/// TC-KDO-14: emergency access code generation - Lock() during the PIN derivation → no code is returned.
/// TC-KDO-15: emergency access unlock - another unlock finishing during the derivation → aborted before
///            the connection provider / session are touched; a cancelled token aborts immediately.
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class KeyDerivationOffloadTests : IDisposable
{
    private const string Password = "correct-horse-1";

    private readonly string _dataDir;

    public KeyDerivationOffloadTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"naimitsu_kdo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    // ── Test doubles / fixtures ──────────────────────────────────────────

    /// <summary>
    /// Wraps the real CryptoService. DeriveKey can be held at a gate. The hooks run at points that, in the
    /// code under test, are always reached after a derivation has finished and before the stale-result
    /// guard / the writes - a deterministic "something happened while deriving" without relying on timing:
    /// OnKeyDecrypt fires when a 32-byte key is about to be decrypted, OnGenerateSalt on every salt
    /// generation (the salt for the last derivation is generated just before it), OnEncrypt on every
    /// Encrypt with the call index and plaintext length.
    /// </summary>
    private sealed class HookableCryptoService(ICryptoService inner) : ICryptoService
    {
        private int _saltCalls;
        private int _encryptCalls;

        public ManualResetEventSlim? DeriveGate { get; set; }
        public ManualResetEventSlim DeriveEntered { get; } = new(false);
        public Action? OnKeyDecrypt { get; set; }
        public Action<int>? OnGenerateSalt { get; set; }
        public Action<int, int>? OnEncrypt { get; set; }

        /// <summary>Restarts the call counters (call after fixture setup, before the operation under test).</summary>
        public void ResetCounters() { _saltCalls = 0; _encryptCalls = 0; }

        public void DeriveKey(ReadOnlySpan<char> masterPassword, ReadOnlySpan<byte> salt, Span<byte> output)
        {
            DeriveEntered.Set();
            DeriveGate?.Wait(TimeSpan.FromSeconds(5));
            inner.DeriveKey(masterPassword, salt, output);
        }

        public byte[] GenerateSalt(int size = 32)
        {
            OnGenerateSalt?.Invoke(++_saltCalls);
            return inner.GenerateSalt(size);
        }

        public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData = default)
        {
            OnEncrypt?.Invoke(++_encryptCalls, plaintext.Length);
            return inner.Encrypt(plaintext, key, associatedData);
        }

        public void Decrypt(ReadOnlySpan<byte> cipherData, ReadOnlySpan<byte> key, Span<byte> output, ReadOnlySpan<byte> associatedData = default)
        {
            if (output.Length == 32) OnKeyDecrypt?.Invoke();
            inner.Decrypt(cipherData, key, output, associatedData);
        }
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

        var blob = new byte[96];
        ComputeCoarseHash(password).CopyTo(blob, 0);
        unifiedSalt.CopyTo(blob, 4);
        wrapData.CopyTo(blob, 36);
        return blob;
    }

    /// <summary>A unified DB holding one KSharedSlot for <see cref="Password"/>, plus an AuthService over it.</summary>
    private static async Task<(AuthService Auth, AppSession Session, HookableCryptoService Crypto, TestUnifiedDb Unified)>
        CreateAcquireFixtureAsync()
    {
        var real    = new CryptoService(NullLogger<CryptoService>.Instance);
        var crypto  = new HookableCryptoService(real);
        var session = new AppSession();

        var unified = TestUnifiedDb.Create();
        var kShared = new byte[32];
        RandomNumberGenerator.Fill(kShared);
        await using (var udb = unified.Factory.CreateDbContext())
        {
            udb.Metadata.Add(new UnifiedMetadata
            {
                ConfigKey   = UnifiedMetadataKey.KSharedSlot_1,
                ConfigValue = BuildKSharedSlot(Password, real.GenerateSalt(32), kShared, real),
            });
            await udb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var auth = new AuthService(
            new FaultInjectionDbContextFactory(new InvalidOperationException("vault DB must not be touched")),
            unified.Factory, new NullConnectionProvider(), crypto, session,
            new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);
        return (auth, session, crypto, unified);
    }

    /// <summary>A vault DB whose Auth entry is wrapped with <see cref="Password"/>, plus an AuthService over it.</summary>
    private static async Task<(AuthService Auth, AppSession Session, HookableCryptoService Crypto, TestDb Db)>
        CreateUnlockFixtureAsync()
    {
        var real    = new CryptoService(NullLogger<CryptoService>.Instance);
        var crypto  = new HookableCryptoService(real);
        var session = new AppSession();

        var db   = TestDb.Create();
        var salt = real.GenerateSalt(32);
        var kek  = new byte[32];
        real.DeriveKey(Password, salt, kek);
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        await using (var vdb = db.Factory.CreateDbContext())
        {
            vdb.Metadata.Add(new VaultMetadata
            {
                ConfigKey   = VaultMetadataKey.Auth,
                ConfigValue = BuildAuthDataJson(salt, real.Encrypt(dek, kek)),
            });
            await vdb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var auth = new AuthService(
            db.Factory, new ExplodingUnifiedDbContextFactory(), new NullConnectionProvider(), crypto, session,
            new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);
        return (auth, session, crypto, db);
    }

    /// <summary>
    /// A complete single-vault setup: a real file-backed vault DB (vault #1, Auth wrapped with
    /// <see cref="Password"/>) in the test data dir, the unified DB's registry row and K_shared slot, and an
    /// AuthService wired the way production is (the vault factory resolves the active vault through the
    /// connection provider, and a real DatabaseInitializer creates further vault files).
    /// </summary>
    private sealed class FullVault : IDisposable
    {
        public required AuthService Auth { get; init; }
        public required AppSession Session { get; init; }
        public required HookableCryptoService Crypto { get; init; }
        public required RecordingConnectionProvider ConnProvider { get; init; }
        public required TestUnifiedDb Unified { get; init; }
        public required IDbContextFactory<AppDbContext> SeedVaultFactory { get; init; }
        public required byte[] KShared { get; init; }
        public required byte[] VaultDek { get; init; }
        public void Dispose() => Unified.Dispose();
    }

    private async Task<FullVault> CreateFullVaultAsync()
    {
        const int dbNumber = 1;
        var real    = new CryptoService(NullLogger<CryptoService>.Instance);
        var crypto  = new HookableCryptoService(real);
        var session = new AppSession();

        var raw = new byte[36];
        "nkdb"u8.CopyTo(raw.AsSpan(0, 4));
        RandomNumberGenerator.Fill(raw.AsSpan(4));
        var fileName = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var dbPath   = Path.Combine(_dataDir, fileName);
        var options  = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        using (var ctx = new AppDbContext(options, new SessionGenerationGuardStub()))
            ctx.Database.EnsureCreated();
        var seedFactory = new SharedOptionsAppDbContextFactory(options);

        var vaultSalt = real.GenerateSalt(32);
        var kek       = new byte[32];
        real.DeriveKey(Password, vaultSalt, kek);
        var vaultDek = new byte[32];
        RandomNumberGenerator.Fill(vaultDek);
        await using (var vdb = seedFactory.CreateDbContext())
        {
            vdb.Metadata.Add(new VaultMetadata
            {
                ConfigKey   = VaultMetadataKey.Auth,
                ConfigValue = BuildAuthDataJson(vaultSalt, real.Encrypt(vaultDek, kek)),
            });
            await vdb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var unified = TestUnifiedDb.Create();
        var kShared = new byte[32];
        RandomNumberGenerator.Fill(kShared);
        await using (var udb = unified.Factory.CreateDbContext())
        {
            var payloadJson = JsonSerializer.SerializeToUtf8Bytes(
                new VaultPayload(
                    Convert.ToBase64String(raw.AsSpan(4, 32).ToArray()),
                    Convert.ToBase64String(new byte[32]),
                    Convert.ToBase64String(vaultSalt)),
                MultiVaultJsonContext.Default.VaultPayload);
            udb.VaultRegistries.Add(new VaultRegistry
            {
                DbNumber         = dbNumber,
                EncryptedPayload = real.Encrypt(payloadJson, kShared),
            });
            udb.Metadata.Add(new UnifiedMetadata
            {
                ConfigKey   = UnifiedMetadataKey.KSharedSlot_1,
                ConfigValue = BuildKSharedSlot(Password, real.GenerateSalt(32), kShared, real),
            });
            await udb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var connProvider = new RecordingConnectionProvider();
        var vaultFactory = new AppDbContextFactory(connProvider, new SessionGenerationGuardStub());
        var initializer  = new DatabaseInitializer(
            vaultFactory, unified.Factory, new SessionGenerationGuardStub(), NullLogger<DatabaseInitializer>.Instance);
        var auth = new AuthService(
            vaultFactory, unified.Factory, connProvider, crypto, session,
            new StubAuditLogService(), initializer, NullLogger<AuthService>.Instance);

        return new FullVault
        {
            Auth = auth, Session = session, Crypto = crypto, ConnProvider = connProvider, Unified = unified,
            SeedVaultFactory = seedFactory, KShared = kShared, VaultDek = vaultDek,
        };
    }

    /// <summary>Unlocks vault #1 for real (K_shared + DEK set, vault entered), then restarts the hook counters.</summary>
    private async Task EnterVault1Async(FullVault v)
    {
        Assert.Contains(1, await v.Auth.AcquireKSharedAsync(Password));
        Assert.True(await v.Auth.EnterVaultAsync(1, Password, _dataDir));
        Assert.True(v.Session.IsUnlocked);
        v.Crypto.ResetCounters();
    }

    // ── TC-KDO-01 ────────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireKShared_AlreadyCancelledToken_ThrowsAndSetsNoKShared()
    {
        var (auth, session, _, unified) = await CreateAcquireFixtureAsync();
        using (unified)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => auth.AcquireKSharedAsync(Password, cts.Token));

            Assert.False(session.HasKShared);
        }
    }

    // ── TC-KDO-02 ────────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireKShared_CancelledAfterDerivation_DiscardsResult()
    {
        var (auth, session, crypto, unified) = await CreateAcquireFixtureAsync();
        using (unified)
        {
            using var cts = new CancellationTokenSource();
            crypto.OnKeyDecrypt = cts.Cancel; // fires after Argon2id finished, before the session write

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => auth.AcquireKSharedAsync(Password, cts.Token));

            Assert.False(session.HasKShared);
        }
    }

    // ── TC-KDO-03 ────────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireKShared_SessionLockedAfterDerivation_DoesNotResurrectKShared()
    {
        var (auth, session, crypto, unified) = await CreateAcquireFixtureAsync();
        using (unified)
        {
            // Started while unlocked (the add-vault flow re-derives K_shared this way).
            session.SetKey(new byte[32]);
            crypto.OnKeyDecrypt = session.Lock;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => auth.AcquireKSharedAsync(Password, TestContext.Current.CancellationToken));

            Assert.False(session.HasKShared);
            Assert.False(session.IsUnlocked);
        }
    }

    // ── TC-KDO-04 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unlock_DerivationRunsOffTheCallingThread()
    {
        var (auth, session, crypto, db) = await CreateUnlockFixtureAsync();
        using (db)
        {
            using var gate = new ManualResetEventSlim(false);
            crypto.DeriveGate = gate;

            // If the derivation ran on the calling thread, this call would block inside DeriveKey (at
            // the gate) and only return - completed - after the gate timed out.
            var task = auth.UnlockAsync(Password);
            Assert.False(task.IsCompleted);
            Assert.True(crypto.DeriveEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            gate.Set();
            Assert.True(await task);
            Assert.True(session.IsUnlocked);
        }
    }

    // ── TC-KDO-05 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unlock_LockDuringDerivation_DoesNotWriteDekBack()
    {
        var (auth, session, crypto, db) = await CreateUnlockFixtureAsync();
        using (db)
        {
            // Re-authentication style: the session is already unlocked when the derivation starts.
            session.SetKey(new byte[32]);
            using var gate = new ManualResetEventSlim(false);
            crypto.DeriveGate = gate;

            var task = auth.UnlockAsync(Password);
            Assert.True(crypto.DeriveEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            session.Lock(); // auto-lock / manual lock landing mid-derivation
            gate.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(session.IsUnlocked); // a zombie DEK would make this true
        }
    }

    // ── TC-KDO-06 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnterVault_CancelledAfterDerivation_WritesNoDekAndClearsActiveVault()
    {
        using var v = await CreateFullVaultAsync();
        Assert.Contains(1, await v.Auth.AcquireKSharedAsync(Password, TestContext.Current.CancellationToken));
        v.ConnProvider.SetActiveVault("sentinel"); // must not survive a cancelled entry

        using var cts = new CancellationTokenSource();
        v.Crypto.OnKeyDecrypt = cts.Cancel; // fires after the vault KEK derivation, before SetKey

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => v.Auth.EnterVaultAsync(1, Password, _dataDir, cts.Token));

        Assert.False(v.Session.IsUnlocked);
        Assert.Null(v.ConnProvider.ActiveVaultDbPath); // SetActiveVault was undone
    }

    // ── TC-KDO-07 ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockViewModel_CancelPendingAuth_PassesCancelledTokenAndShowsNoError()
    {
        var auth = new StubAuthService { AcquireKSharedAsyncResult = [1] };
        var vm   = new UnlockViewModel(auth, isSetupMode: false, new AppSession(), NullLogger<UnlockViewModel>.Instance)
        {
            GetPasswordChars = () => Password.ToCharArray(),
        };

        vm.CancelPendingAuth(); // the window was closed
        await ((IAsyncRelayCommand)vm.ExecuteCommand).ExecuteAsync(null);

        Assert.True(auth.LastAcquireKSharedToken?.IsCancellationRequested);
        Assert.Null(vm.ErrorMessage); // cancellation is neither a wrong password nor a general error
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsSuccess);
    }

    // ── TC-KDO-08 ────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockViewModel_HelloUnavailableWhileAnotherUnlockIsInFlight()
    {
        var auth = new StubAuthService { AcquireKSharedWithHelloAsyncResult = [1] };
        var vm   = new UnlockViewModel(auth, isSetupMode: false, new AppSession(), NullLogger<UnlockViewModel>.Instance);

        vm.IsBusy = true; // a password unlock is in flight

        Assert.False(vm.UnlockWithWindowsHelloCommand.CanExecute(null));
        await ((IAsyncRelayCommand)vm.UnlockWithWindowsHelloCommand).ExecuteAsync(null); // direct call: guarded too

        Assert.Equal(0, auth.AcquireKSharedWithHelloCalls);
        Assert.True(vm.IsBusy); // the guard returned before touching the in-flight unlock's state
    }

    // ── Phase 2 ──────────────────────────────────────────────────────────

    private async Task<byte[]> ReadVaultAuthBlobAsync(FullVault v)
    {
        await using var vdb = v.SeedVaultFactory.CreateDbContext();
        return (await vdb.Metadata.AsNoTracking().SingleAsync(m => m.ConfigKey == VaultMetadataKey.Auth,
            TestContext.Current.CancellationToken)).ConfigValue;
    }

    // ── TC-KDO-09 ────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_LockDuringDerivations_AbortsBeforeAnyWrite()
    {
        using var v = await CreateFullVaultAsync();
        await EnterVault1Async(v);

        var authBefore = await ReadVaultAuthBlobAsync(v);
        byte[] slotBefore, registryBefore;
        await using (var udb = v.Unified.Factory.CreateDbContext())
        {
            slotBefore     = (await udb.Metadata.AsNoTracking().SingleAsync(m => m.ConfigKey == UnifiedMetadataKey.KSharedSlot_1, TestContext.Current.CancellationToken)).ConfigValue;
            registryBefore = (await udb.VaultRegistries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).EncryptedPayload;
        }

        // The salt for the LAST derivation (K_master_N) is generated just before it: Lock() there means
        // the session changes while that derivation is in flight, after K_shared was already captured.
        v.Crypto.OnGenerateSalt = n => { if (n == 2) v.Session.Lock(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => v.Auth.ChangeMasterPasswordAsync(Password, "brand-new-pass-2"));

        Assert.Equal(authBefore, await ReadVaultAuthBlobAsync(v));
        await using var verify = v.Unified.Factory.CreateDbContext();
        Assert.Equal(slotBefore, (await verify.Metadata.AsNoTracking().SingleAsync(m => m.ConfigKey == UnifiedMetadataKey.KSharedSlot_1, TestContext.Current.CancellationToken)).ConfigValue);
        Assert.Equal(registryBefore, (await verify.VaultRegistries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).EncryptedPayload);
    }

    // ── TC-KDO-10 ────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_LockDuringWrites_StaysConsistentAndNewPasswordUnlocks()
    {
        const string newPassword = "brand-new-pass-2";
        using var v = await CreateFullVaultAsync();
        await EnterVault1Async(v);

        // First Encrypt with a plaintext longer than a key = the registry payload JSON: it runs after the
        // Auth blob was already saved, so Lock() here lands in the middle of the write sequence.
        v.Crypto.OnEncrypt = (_, len) => { if (len > 32) v.Session.Lock(); };

        Assert.True(await v.Auth.ChangeMasterPasswordAsync(Password, newPassword));
        Assert.False(v.Session.IsUnlocked); // the lock was honoured...
        v.Crypto.OnEncrypt = null;

        // ...but the change itself completed consistently: the Auth blob, the unified slot and the
        // registry all agree on the new password, so the full multi-vault unlock works with it.
        Assert.Contains(1, await v.Auth.AcquireKSharedAsync(newPassword, TestContext.Current.CancellationToken));
        Assert.True(await v.Auth.EnterVaultAsync(1, newPassword, _dataDir, TestContext.Current.CancellationToken));
        Assert.Equal(v.VaultDek, v.Session.GetKey().Span.ToArray());
    }

    // ── TC-KDO-11 ────────────────────────────────────────────────────────

    [Fact]
    public async Task AddVault_LockDuringDerivations_CreatesNothing()
    {
        using var v = await CreateFullVaultAsync();
        await EnterVault1Async(v);
        var filesBefore = Directory.GetFiles(_dataDir).Length;

        v.Crypto.OnGenerateSalt = n => { if (n == 2) v.Session.Lock(); }; // before the last derivation

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => v.Auth.CreateNewVaultAsync(2, "another-pass-2", _dataDir));

        Assert.Equal(filesBefore, Directory.GetFiles(_dataDir).Length);
        await using var udb = v.Unified.Factory.CreateDbContext();
        Assert.Equal(1, await udb.VaultRegistries.CountAsync(TestContext.Current.CancellationToken));
    }

    // ── TC-KDO-12 ────────────────────────────────────────────────────────

    [Fact]
    public async Task AddVault_LockDuringWrites_RegistersSlotWrappingTheRealKShared()
    {
        const string vault2Password = "another-pass-2";
        using var v = await CreateFullVaultAsync();
        await EnterVault1Async(v);

        // First Encrypt with a plaintext longer than a key = the registry payload JSON, after the new vault
        // DB was created and written: Lock() here zeroes the session's K_shared mid-creation.
        v.Crypto.OnEncrypt = (_, len) => { if (len > 32) v.Session.Lock(); };

        Assert.True(await v.Auth.CreateNewVaultAsync(2, vault2Password, _dataDir));
        v.Crypto.OnEncrypt = null;

        // The lock was honoured: a locked session is never switched to the new vault.
        Assert.False(v.Session.IsUnlocked);
        Assert.Null(v.Session.CurrentVaultDbNumber);

        // The slot must wrap the REAL K_shared. Reading the session's already-zeroed buffer instead would
        // have committed a slot wrapping zeros, which unlocks "fine" but into a different key domain.
        Assert.Contains(2, await v.Auth.AcquireKSharedAsync(vault2Password, TestContext.Current.CancellationToken));
        Assert.Equal(v.KShared, v.Session.GetKShared().Span.ToArray());
    }

    // ── TC-KDO-13 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Setup_CancelledToken_ThrowsAndReleasesKShared()
    {
        var session = new AppSession();
        var auth = new AuthService(
            new FaultInjectionDbContextFactory(new InvalidOperationException("vault DB must not be touched")),
            new ExplodingUnifiedDbContextFactory(), new NullConnectionProvider(),
            new CryptoService(NullLogger<CryptoService>.Instance), session,
            new StubAuditLogService(), null!, NullLogger<AuthService>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auth.SetupAsync(Password, _dataDir, cts.Token));

        Assert.False(session.HasKShared); // the random K_shared SetupAsync installed was released
    }

    // ── TC-KDO-14 ────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateEmergencyAccessCode_LockDuringPinDerivation_ReturnsNoCode()
    {
        using var v = await CreateFullVaultAsync();
        await EnterVault1Async(v);

        // Encrypt #1 wraps the DEK (before the PIN derivation); #2 wraps the RK with the PIN key, i.e.
        // after the derivation finished: Lock() there is "Lock() landed while deriving".
        v.Crypto.OnEncrypt = (n, _) => { if (n == 2) v.Session.Lock(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => v.Auth.GenerateEmergencyAccessCodeAsync("123456"));
    }

    // ── TC-KDO-15 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EmergencyAccessUnlock_StateChangedDuringDerivation_AbortsBeforeTouchingSession()
    {
        using var v = await CreateFullVaultAsync();
        await EnterVault1Async(v);
        var (qr, _, wrappedDek) = await v.Auth.GenerateEmergencyAccessCodeAsync("123456");
        await v.Auth.CommitEmergencyAccessCodeAsync(wrappedDek);
        v.Session.Lock();
        v.ConnProvider.ClearActiveVault();

        // The first 32-byte decrypt is the RK, right after the PIN derivation. Another unlock finishing
        // there flips IsUnlocked; the guard before Step 4 must notice, before SetActiveVault / SetKey.
        v.Crypto.OnKeyDecrypt = () => v.Session.SetKey(new byte[32]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => v.Auth.EmergencyAccessUnlockAsync(qr, "123456", _dataDir, TestContext.Current.CancellationToken));

        Assert.False(v.Session.IsReadOnlyRestricted);
        Assert.Null(v.ConnProvider.ActiveVaultDbPath);

        // And a cancelled token aborts immediately, before any of that.
        v.Crypto.OnKeyDecrypt = null;
        v.Session.Lock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => v.Auth.EmergencyAccessUnlockAsync(qr, "123456", _dataDir, cts.Token));
        Assert.False(v.Session.IsUnlocked);
    }
}
