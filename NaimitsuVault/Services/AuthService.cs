// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Services;

/// <summary>
/// Service responsible for setting/verifying the master password and Windows Hello integration.
/// </summary>
public partial class AuthService(
    IDbContextFactory<AppDbContext> factory,
    IDbContextFactory<UnifiedDbContext> unifiedFactory,
    IVaultConnectionProvider connectionProvider,
    ICryptoService crypto,
    ISecurityContext session,
    IAuditLogService auditLog,
    DatabaseInitializer initializer,
    ILogger<AuthService> logger) : IAuthService, IRestoreAuthService
{
    // Adapters for test injection (production implementation by default)
    private IUserConsentVerifierAdapter _helloAdapter = new WinRTUserConsentVerifierAdapter();
    private IProtectedDataService _protectedData = new DpapiProtectedDataService();

    internal void SetHelloAdapter(IUserConsentVerifierAdapter adapter) => _helloAdapter = adapter;
    internal void SetProtectedData(IProtectedDataService service) => _protectedData = service;

    public async Task<bool> IsSetupRequiredAsync()
    {
        // First-time setup is required if KSharedSlot_1 doesn't exist in the unified DB
        await using var db = await unifiedFactory.CreateDbContextAsync();
        bool hasSlot = await db.Metadata.AnyAsync(s => s.ConfigKey == UnifiedMetadataKey.KSharedSlot_1);
        if (!hasSlot)
            logger.LogInformation("First-time setup is required (KSharedSlot_1 not registered).");
        return !hasSlot;
    }

    /// <summary>
    /// Checks whether Windows Hello (PIN / biometric authentication) is available on this device/account.
    /// </summary>
    public async Task<bool> IsWindowsHelloSupportedAsync()
    {
        var availability = await _helloAdapter.CheckAvailabilityAsync();
        return availability == UserConsentVerifierAvailability.Available;
    }

    /// <summary>
    /// Checks whether Windows Hello is enabled.
    /// </summary>
    /// <remarks>
    /// [Absolute precondition] This method must only be called after a successful unlock,
    /// with Windows Hello already enabled.
    /// If called before unlock (ActiveVaultDbPath is null), AppDbContextFactory throws an
    /// InvalidOperationException and the app crashes.
    /// </remarks>
    public async Task<bool> IsWindowsHelloEnabledAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Metadata.AnyAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
    }

    /// <summary>
    /// Hello check used before unlock, to decide whether to show the button on the unlock screen.
    /// References KSharedHello in the unified DB, so it can be called even without ActiveVaultDbPath set.
    /// </summary>
    public async Task<bool> IsWindowsHelloConfiguredForUnlockAsync()
    {
        await using var db = await unifiedFactory.CreateDbContextAsync();
        return await db.Metadata.AnyAsync(s => s.ConfigKey == UnifiedMetadataKey.KSharedHello);
    }

    /// <summary>
    /// Enables unlocking via Windows Hello.
    /// Saves the DEK, DPAPI-protected, in vault DB VaultMetadata[0x1002], and K_shared, DPAPI-protected,
    /// in unified DB UnifiedMetadata[0x000A].
    /// </summary>
    public async Task EnableWindowsHelloAsync()
    {
        logger.LogInformation("Starting to enable Windows Hello.");

        // 1. vault_DEK → vault DB VaultMetadata[VaultDEKHello=0x1002]
        var protectedDek = _protectedData.Protect(session.GetKey().UnsafeArray, null, DataProtectionScope.CurrentUser);
        var base64Dek = Convert.ToBase64String(protectedDek);
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
        if (existing == null)
            db.Metadata.Add(new VaultMetadata { ConfigKey = VaultMetadataKey.VaultDEKHello, ConfigValue = Encoding.UTF8.GetBytes(base64Dek) });
        else
            existing.ConfigValue = Encoding.UTF8.GetBytes(base64Dek);
        await db.SaveChangesAsync();

        // 2. K_shared → unified DB UnifiedMetadata[KSharedHello=0x000A]
        if (session.CurrentVaultDbNumber is >= 1 and <= 3)
        {
            var protectedKShared = _protectedData.Protect(session.GetKShared().UnsafeArray, null, DataProtectionScope.CurrentUser);
            var base64KShared = Convert.ToBase64String(protectedKShared);
            await using var udb = await unifiedFactory.CreateDbContextAsync();
            var existingKSh = await udb.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.KSharedHello);
            if (existingKSh == null)
                udb.Metadata.Add(new UnifiedMetadata { ConfigKey = UnifiedMetadataKey.KSharedHello, ConfigValue = Encoding.UTF8.GetBytes(base64KShared) });
            else
                existingKSh.ConfigValue = Encoding.UTF8.GetBytes(base64KShared);
            await udb.SaveChangesAsync();
        }

        logger.LogInformation("Saved Windows Hello key protected with DPAPI.");
    }

    /// <summary>
    /// Disables Windows Hello for the current vault only (removes its VaultDEKHello row).
    /// </summary>
    public async Task DisableWindowsHelloAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
        if (existing != null)
        {
            db.Metadata.Remove(existing);
            await db.SaveChangesAsync();
        }

        // Deliberately leaves the unified DB's KSharedHello alone: other vaults (1-3) may still have
        // their own live VaultDEKHello sharing it. Deleting KSharedHello here on the inference that
        // "no vault references it anymore" is reference-count-based auto-deletion, which is forbidden
        // - a misjudgment would break Hello unlock for every other vault, not just this one. Clearing
        // KSharedHello everywhere stays the sole job of InvalidateWindowsHelloEverywhereAsync, which
        // only runs after the user explicitly confirms "disable everywhere".
        logger.LogInformation("Removed Windows Hello settings.");
    }

    /// <summary>
    /// Pre-unlock recovery from a Windows Hello profile mismatch (moved to a different PC/Windows
    /// account): clears the unified DB's KSharedHello plus every vault file's VaultDEKHello row.
    /// Unlike <see cref="DisableWindowsHelloAsync"/>, this does not require an active session
    /// (ActiveVaultDbPath/CurrentVaultDbNumber) - it can be called from UnlockViewModel before any
    /// vault has been entered.
    ///
    /// Clearing only KSharedHello is not enough: AcquireKSharedWithHelloAsync's hasDek check
    /// (line ~245) only tests row *existence*, not whether it's still decryptable. If any other
    /// vault sharing this K_shared still has its (now-stale, still-undecryptable) VaultDEKHello row,
    /// re-enabling Hello for one vault would make that stale vault reappear in the Hello vault
    /// picker and fail again with the same mismatch. Vault files are found by filename pattern
    /// alone (no K_shared/DEK needed) and touched via a raw SqliteConnection, since deleting a row
    /// by ConfigKey never requires decrypting it.
    /// </summary>
    public async Task InvalidateWindowsHelloEverywhereAsync(string dataDir)
    {
        await using (var udb = await unifiedFactory.CreateDbContextAsync())
        {
            var existingKSh = await udb.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.KSharedHello);
            if (existingKSh != null)
            {
                udb.Metadata.Remove(existingKSh);
                await udb.SaveChangesAsync();
            }
        }

        int clearedVaultCount = 0;
        foreach (var vaultPath in ScanVaultCandidates(dataDir))
        {
            try
            {
                var csb = new SqliteConnectionStringBuilder { DataSource = vaultPath };
                await using var conn = new SqliteConnection(csb.ToString());
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM VaultMetadata WHERE ConfigKey = {VaultMetadataKey.VaultDEKHello}";
                int rows = await cmd.ExecuteNonQueryAsync();
                if (rows > 0) clearedVaultCount++;
            }
            catch (Exception ex)
            {
                // One unreadable/locked vault file shouldn't block clearing the rest.
                logger.LogWarning("Failed to clear VaultDEKHello for a vault file. [{ExType}]", ex.GetType().Name);
            }
        }

        // Release the pooled connections opened above so callers can immediately copy/move/delete
        // the same files afterward (e.g. RestoreAllFromBackupAsync calls this right after copying
        // the vault files into place) without hitting a file-in-use error from a still-pooled handle.
        SqliteConnection.ClearAllPools();

        logger.LogInformation(
            "Invalidated Windows Hello everywhere (profile mismatch recovery). VaultFilesCleared={Count}", clearedVaultCount);
    }

    /// <summary>
    /// Restores K_shared from the unified DB via DPAPI after Windows Hello authentication and sets it in the session.
    /// This is stage 1 of the multi-vault Hello unlock flow.
    /// </summary>
    /// <returns>List of registered vault numbers. Empty if Hello is not configured or authentication fails.</returns>
    public async Task<List<int>> AcquireKSharedWithHelloAsync()
    {
        // Clear the vault-side auto-recovery/unrecoverable flags (prevent leftover state from a
        // previous failed retry, e.g. a password attempt on a different vault)
        session.VaultDbAutoRecovered       = false;
        session.VaultDbAutoRecoveredNumber = null;
        session.VaultDbUnrecoverable       = false;
        session.VaultDbUnrecoverableNumber = null;

        logger.LogInformation("Starting to acquire K_shared via Windows Hello.");

        var availability = await _helloAdapter.CheckAvailabilityAsync();
        if (availability != UserConsentVerifierAvailability.Available)
        {
            logger.LogWarning("Windows Hello is not available on this device.");
            await TryLogAuthFailedAsync();
            return [];
        }

        UserConsentVerificationResult result;
        try { result = await _helloAdapter.RequestVerificationAsync(LocalizationManager.Get("Common.AuthWithWindowsHelloPrompt")); }
        catch (Exception ex)
        {
            logger.LogError("An exception occurred while invoking the Windows Hello prompt. [{ExType}]", ex.GetType().Name);
            await TryLogAuthFailedAsync();
            return [];
        }
        if (result != UserConsentVerificationResult.Verified)
        {
            logger.LogWarning("Windows Hello authentication was canceled or failed.");
            await TryLogAuthFailedAsync();
            return [];
        }

        byte[]? kSharedPinned = null;
        byte[]? kSharedRaw    = null;
        try
        {
            await using var udb = await unifiedFactory.CreateDbContextAsync();
            var kshSetting = await udb.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == UnifiedMetadataKey.KSharedHello);
            if (kshSetting == null)
            {
                logger.LogWarning("KSharedHello does not exist in the unified DB. Windows Hello may not be configured.");
                await TryLogAuthFailedAsync();
                return [];
            }

            int maxDecoded = Base64.GetMaxDecodedFromUtf8Length(kshSetting.ConfigValue.Length);
            var protectedBuf = GC.AllocateArray<byte>(maxDecoded, pinned: true);
            try
            {
                Base64.DecodeFromUtf8(kshSetting.ConfigValue, protectedBuf, out _, out int decodedLen);
                try
                {
                    kSharedRaw = _protectedData.Unprotect(
                        protectedBuf.AsSpan(0, decodedLen).ToArray(),
                        null, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    // DPAPI CurrentUser-scope blobs only decrypt on the device/Windows account that
                    // created them. Distinguish this from a generic failure so the UI can offer to
                    // clear the stale Hello registration instead of just retrying.
                    throw new WindowsHelloProfileMismatchException();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBuf);
            }

            kSharedPinned = GC.AllocateArray<byte>(kSharedRaw.Length, pinned: true);
            kSharedRaw.AsSpan().CopyTo(kSharedPinned);
            CryptographicOperations.ZeroMemory(kSharedRaw);
            kSharedRaw = null;

            session.SetKShared(kSharedPinned.AsSpan());

            var allRegistries = await udb.VaultRegistries
                .Where(v => v.DbNumber > 0)
                .OrderBy(v => v.DbNumber)
                .ToListAsync();

            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            var helloVaults = new List<int>();
            foreach (var reg in allRegistries)
            {
                var payload = DecryptVaultPayload(reg.EncryptedPayload);
                if (payload == null) continue;

                var vaultPath = ComputeVaultDbPath(Convert.FromBase64String(payload.FileHash), dataDir);
                connectionProvider.SetActiveVault(vaultPath);
                try
                {
                    bool hasDek;
                    try
                    {
                        await using var vdb = await factory.CreateDbContextAsync();
                        hasDek = await vdb.Metadata.AnyAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
                    }
                    catch (Exception)
                    {
                        // The vault DB may be missing/corrupted - attempt the same shadow recovery as the
                        // password path before giving up on this one vault. A single broken vault must not
                        // take down Hello discovery for the other, unrelated vaults.
                        if (!await TryRecoverVaultFromShadowAsync(vaultPath, reg, reg.DbNumber, dataDir))
                            continue;
                        await using var vdb = await factory.CreateDbContextAsync();
                        hasDek = await vdb.Metadata.AnyAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
                    }
                    if (hasDek) helloVaults.Add(reg.DbNumber);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Vault {N}: could not check VaultDEKHello, skipping. [{ExType}]", reg.DbNumber, ex.GetType().Name);
                }
                finally
                {
                    connectionProvider.ClearActiveVault();
                }
            }

            logger.LogInformation("Acquired K_shared via Windows Hello. Hello-registered vault count={Count}", helloVaults.Count);
            return helloVaults;
        }
        catch (WindowsHelloProfileMismatchException)
        {
            logger.LogWarning("Windows Hello key material (K_shared) does not belong to this device/account.");
            await TryLogAuthFailedAsync();
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to restore K_shared via Windows Hello. [{ExType}]", ex.GetType().Name);
            await TryLogAuthFailedAsync();
            return [];
        }
        finally
        {
            if (kSharedRaw    != null) CryptographicOperations.ZeroMemory(kSharedRaw);
            if (kSharedPinned != null) CryptographicOperations.ZeroMemory(kSharedPinned);
        }
    }

    /// <summary>
    /// Stage 2 of the multi-vault Hello unlock flow.
    /// With K_shared already in the session, restores and sets the vault DB's VaultDEKHello via DPAPI.
    /// Call this after AcquireKSharedWithHelloAsync.
    /// </summary>
    public async Task<bool> EnterVaultWithHelloAsync(int dbNumber, string dataDir)
    {
        // Clear the vault-side auto-recovery/unrecoverable flags only if they refer to a DIFFERENT
        // vault than the one being entered now (leftover from a different vault's failure/recovery
        // during AcquireKSharedWithHelloAsync's scan, or a previous unlock attempt). If they already
        // name this same vault - e.g. Stage 1 just recovered it moments ago - preserve them: this
        // method's own tail needs VaultDbAutoRecovered intact to write the forced audit-log entry,
        // and it won't be re-set here since the file is already healthy by the time this call opens it.
        if (session.VaultDbAutoRecoveredNumber != dbNumber)
        {
            session.VaultDbAutoRecovered       = false;
            session.VaultDbAutoRecoveredNumber = null;
        }
        if (session.VaultDbUnrecoverableNumber != dbNumber)
        {
            session.VaultDbUnrecoverable       = false;
            session.VaultDbUnrecoverableNumber = null;
        }

        if (!session.HasKShared)
        {
            logger.LogError("K_shared is not set. Call AcquireKSharedWithHelloAsync first.");
            await TryLogAuthFailedAsync();
            return false;
        }

        await using var udb = await unifiedFactory.CreateDbContextAsync();
        var registry = await udb.VaultRegistries.FirstOrDefaultAsync(v => v.DbNumber == dbNumber);
        if (registry == null)
        {
            logger.LogWarning("VaultRegistry not found: DbNumber={N}", dbNumber);
            await TryLogAuthFailedAsync();
            return false;
        }

        VaultPayload? payload = DecryptVaultPayload(registry.EncryptedPayload);
        if (payload == null) { await TryLogAuthFailedAsync(); return false; }

        var vaultPath = ComputeVaultDbPath(Convert.FromBase64String(payload.FileHash), dataDir);
        connectionProvider.SetActiveVault(vaultPath);

        byte[]? dekPinned = null;
        byte[]? dekRaw    = null;
        try
        {
            VaultMetadata? setting;
            try
            {
                await using var vdb = await factory.CreateDbContextAsync();
                setting = await vdb.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
            }
            catch (Exception)
            {
                // The vault DB may be missing/corrupted - attempt the same shadow recovery as the
                // password path before giving up on this vault entry.
                if (!await TryRecoverVaultFromShadowAsync(vaultPath, registry, dbNumber, dataDir))
                {
                    await TryLogAuthFailedAsync();
                    connectionProvider.ClearActiveVault();
                    return false;
                }
                await using var vdb = await factory.CreateDbContextAsync();
                setting = await vdb.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
            }
            if (setting == null)
            {
                logger.LogWarning("VaultDEKHello does not exist for Vault {N}. Windows Hello may not be configured.", dbNumber);
                await TryLogAuthFailedAsync();
                connectionProvider.ClearActiveVault();
                return false;
            }

            int maxDecoded = Base64.GetMaxDecodedFromUtf8Length(setting.ConfigValue.Length);
            var protectedBuf = GC.AllocateArray<byte>(maxDecoded, pinned: true);
            try
            {
                Base64.DecodeFromUtf8(setting.ConfigValue, protectedBuf, out _, out int decodedLen);
                try
                {
                    dekRaw = _protectedData.Unprotect(
                        protectedBuf.AsSpan(0, decodedLen).ToArray(),
                        null, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    throw new WindowsHelloProfileMismatchException();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBuf);
            }

            dekPinned = GC.AllocateArray<byte>(dekRaw.Length, pinned: true);
            dekRaw.AsSpan().CopyTo(dekPinned);
            CryptographicOperations.ZeroMemory(dekRaw);
            dekRaw = null;

            session.SetKey(dekPinned.AsSpan());
            session.CurrentVaultDbNumber = dbNumber;
            session.DisplayedVaultNumber = dbNumber;

            registry.LastAccessedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await udb.SaveChangesAsync();

            // Write the auto-recovery audit log entry (at the end, after success, once the DEK is confirmed)
            await WriteAutoRecoveryAuditLogAsync();

            logger.LogInformation("Entered Vault {N} via Windows Hello.", dbNumber);
            await TryLogAuthSucceededAsync();
            return true;
        }
        catch (WindowsHelloProfileMismatchException)
        {
            logger.LogWarning("Windows Hello key material (Vault {N} DEK) does not belong to this device/account.", dbNumber);
            await TryLogAuthFailedAsync();
            connectionProvider.ClearActiveVault();
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to enter Vault {N} via Windows Hello. [{ExType}]", dbNumber, ex.GetType().Name);
            await TryLogAuthFailedAsync();
            connectionProvider.ClearActiveVault();
            return false;
        }
        finally
        {
            if (dekRaw    != null) CryptographicOperations.ZeroMemory(dekRaw);
            if (dekPinned != null) CryptographicOperations.ZeroMemory(dekPinned);
        }
    }

    /// <summary>
    /// First-time setup.
    /// Generates a random DEK, encrypts it with a KEK derived from the master password, and saves it.
    /// </summary>
    public Task SetupAsync(ReadOnlySpan<char> masterPassword, string dataDir, CancellationToken ct = default)
    {
        var pwdBuf = new SecureCharBuffer();
        pwdBuf.SetFromSpan(masterPassword);
        return SetupAsyncCore(pwdBuf, dataDir, ct);
    }

    private async Task SetupAsyncCore(SecureCharBuffer pwdBuf, string dataDir, CancellationToken ct)
    {
        logger.LogInformation("Starting first-time setup (Vault #1).");
        var kShared = GC.AllocateArray<byte>(32, pinned: true);
        try
        {
            RandomNumberGenerator.Fill(kShared);
            session.SetKShared(kShared);
            // Ownership of pwdBuf stays here - disposed in this method's own finally below, not
            // delegated to CreateNewVaultCoreAsync (which no longer disposes its pwBuf parameter).
            bool ok = await CreateNewVaultCoreAsync(1, pwdBuf, dataDir, ct);
            if (!ok) throw new InvalidOperationException("First-time setup of Vault #1 failed.");
            logger.LogInformation("First-time setup completed (Vault #1).");
        }
        catch
        {
            session.Lock(); // Zero K_shared immediately on failure
            throw;
        }
        finally
        {
            pwdBuf.Dispose();
            CryptographicOperations.ZeroMemory(kShared);
        }
    }

    /// <summary>
    /// Verifies the master password and, on success, decrypts the wrapped DEK and stores it in the session.
    /// </summary>
    public Task<bool> UnlockAsync(ReadOnlySpan<char> masterPassword)
    {
        var pwdBuf = new SecureCharBuffer();
        pwdBuf.SetFromSpan(masterPassword);
        return UnlockAsyncCore(pwdBuf);
    }

    private async Task<bool> UnlockAsyncCore(SecureCharBuffer pwdBuf)
    {
        byte[]? vaultSaltPwd = null;
        try
        {
            // Denial-of-service defense: fewer than 8 characters ends immediately at zero cost as "mismatch". Never proceeds to Argon2id / DB reads.
            if (pwdBuf.Span.Length < 8) { await TryLogAuthFailedAsync(); return false; }

            logger.LogDebug("Attempting to unlock with the master password.");

            // Read vault_salt_pwd from the vault DB Auth entry and pass it to UnlockCoreAsync
            await using var db = await factory.CreateDbContextAsync();
            var setting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.Auth);
            if (setting == null) { await TryLogAuthFailedAsync(); return false; }
            var authData = JsonSerializer.Deserialize(setting.ConfigValue.AsSpan(), AuthServiceJsonContext.Default.AuthData);
            if (authData == null) { await TryLogAuthFailedAsync(); return false; }
            vaultSaltPwd = Convert.FromBase64String(authData.Salt);
            bool unlockOk = await UnlockCoreAsync(pwdBuf, vaultSaltPwd);
            if (!unlockOk) await TryLogAuthFailedAsync();
            else await TryLogAuthSucceededAsync();
            return unlockOk;
        }
        finally
        {
            pwdBuf.Dispose();
            if (vaultSaltPwd != null) CryptographicOperations.ZeroMemory(vaultSaltPwd);
        }
    }

    /// <summary>
    /// Changes the master password.
    /// The current DEK is kept as-is; it is re-wrapped (rekeyed) with a new KEK.
    /// </summary>
    public Task<bool> ChangeMasterPasswordAsync(ReadOnlySpan<char> oldPassword, ReadOnlySpan<char> newPassword)
    {
        var oldBuf = new SecureCharBuffer(); oldBuf.SetFromSpan(oldPassword);
        var newBuf = new SecureCharBuffer(); newBuf.SetFromSpan(newPassword);
        return ChangeMasterPasswordStandardCoreAsync(oldBuf, newBuf);
    }

    private async Task<bool> ChangeMasterPasswordStandardCoreAsync(SecureCharBuffer oldBuf, SecureCharBuffer newBuf)
    {
        try
        {
            logger.LogInformation("Starting master password change.");
            // Captured synchronously, before the first await: the session state this change started from.
            bool wasUnlocked = session.IsUnlocked;

            await using var db = await factory.CreateDbContextAsync();
            var setting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.Auth);
            if (setting == null) return false;

            var authData = JsonSerializer.Deserialize(setting.ConfigValue.AsSpan(), AuthServiceJsonContext.Default.AuthData);
            if (authData == null) return false;

            var oldSalt       = Convert.FromBase64String(authData.Salt);
            var oldWrappedDek = Convert.FromBase64String(authData.WrappedDek);

            // Put oldKek / dek / newKek into GC-pinned fixed-address buffers
            var oldKek = GC.AllocateArray<byte>(32, pinned: true);
            var dek    = GC.AllocateArray<byte>(32, pinned: true);
            var newKek = GC.AllocateArray<byte>(32, pinned: true);
            // K_shared re-wrap inputs. Captured and derived BEFORE the first write (see below), so the
            // writes never have to read the session again; all zeroed in the finally at the end.
            byte[]? kSharedForUpdate = null;
            byte[]? newUnifiedSalt   = null;
            byte[]? newKMaster       = null;
            int vaultNum = 0;
            try
            {
                // Every Argon2id below runs off the UI thread. The buffers the lambdas touch are only
                // zeroed in the finally blocks, strictly after the awaited task completes.
                await Task.Run(() => crypto.DeriveKey(oldBuf.Span, oldSalt, oldKek)); // span-output
                try
                {
                    crypto.Decrypt(oldWrappedDek, oldKek, dek); // span-output (throws CryptographicException on failure)
                }
                catch (CryptographicException)
                {
                    logger.LogWarning("The old password is incorrect.");
                    return false;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(oldKek); // zero immediately after use
                }

                var newSalt = crypto.GenerateSalt(32); // salt: public data, no pinning needed
                await Task.Run(() => crypto.DeriveKey(newBuf.Span, newSalt, newKek)); // span-output
                byte[] newWrappedDek;
                try
                {
                    newWrappedDek = crypto.Encrypt(dek, newKek);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(newKek);
                }

                // Capture K_shared and derive K_master_N for the re-wrap now, before any write. Only
                // session.CurrentVaultDbNumber (the currently selected vault) is updated: since each vault
                // (1-3) is designed to have a completely independent password, the KSharedSlot_N /
                // VaultRegistries of other vaults are intentionally excluded (updating other vaults in the
                // background would violate the spec).
                if (session.HasKShared)
                {
                    vaultNum = session.CurrentVaultDbNumber ?? 0;
                    kSharedForUpdate = GC.AllocateArray<byte>(32, pinned: true);
                    session.GetKShared().Span.CopyTo(kSharedForUpdate);
                    if (vaultNum >= 1 && vaultNum <= 3)
                    {
                        var salt = crypto.GenerateSalt(32);
                        var km   = GC.AllocateArray<byte>(32, pinned: true);
                        newUnifiedSalt = salt;
                        newKMaster     = km; // assigned before the derivation so the finally always zeroes it
                        await Task.Run(() => DeriveArgon2id(newBuf.Span, salt, km));
                    }
                }

                // Stale-result guard, before the first write: with Argon2id off the UI thread a Lock() can
                // land during the derivations above. Abort now (nothing has been written yet) rather than
                // leave the Auth blob, the unified slot and the registry disagreeing about the password.
                // Past this point the writes below use only the local copies (kSharedForUpdate, dek,
                // newKMaster) and never read the session, so an interruption cannot leave them half-done.
                ThrowIfDerivationStale(wasUnlocked, CancellationToken.None);

                setting.ConfigValue = JsonSerializer.SerializeToUtf8Bytes(new AuthData(
                    Convert.ToBase64String(newSalt),
                    Convert.ToBase64String(newWrappedDek)), AuthServiceJsonContext.Default.AuthData);

                await db.SaveChangesAsync();

                if (kSharedForUpdate != null)
                {
                    if (newKMaster != null && newUnifiedSalt != null)
                    {
                        var wrapData = crypto.Encrypt(kSharedForUpdate, newKMaster);
                        var coarse   = ComputeCoarseHash(newBuf.Span);
                        var newSlot  = new byte[SlotTotalSize];
                        coarse.AsSpan(0, SlotCoarseHashSize)
                              .CopyTo(newSlot.AsSpan(SlotCoarseHashOffset, SlotCoarseHashSize));
                        newUnifiedSalt.AsSpan()
                                      .CopyTo(newSlot.AsSpan(SlotUnifiedSaltOffset, SlotUnifiedSaltSize));
                        wrapData.AsSpan()
                                .CopyTo(newSlot.AsSpan(SlotWrappedKShOffset, SlotWrappedKShSize));

                        await using var udb = await unifiedFactory.CreateDbContextAsync();
                        await UpsertUnifiedSettingAsync(udb, UnifiedMetadataKey.KSharedSlotForVault(vaultNum), newSlot);

                        // VaultRegistries[N].EncryptedPayload's VaultSaltPwd must also be updated
                        // to match the new password's salt. If this is not updated, a subsequent
                        // multi-vault unlock (EnterVaultCoreAsync) would derive the KEK using the
                        // old VaultSaltPwd, which would no longer match the file's WrappedDek
                        // wrapped with the new salt, causing failure (password change -> lock -> unable to unlock).
                        var registry = await udb.VaultRegistries.FirstOrDefaultAsync(v => v.DbNumber == vaultNum);
                        if (registry != null)
                        {
                            var oldPayload = DecryptVaultPayloadWithKey(registry.EncryptedPayload, kSharedForUpdate);
                            if (oldPayload != null)
                            {
                                int prefixLen = Math.Min(3, newBuf.Span.Length);
                                Span<byte> prefix3Utf8 = stackalloc byte[Encoding.UTF8.GetByteCount(newBuf.Span[..prefixLen])];
                                Encoding.UTF8.GetBytes(newBuf.Span[..prefixLen], prefix3Utf8);
                                var newPrefix3Hash = HMACSHA256.HashData(kSharedForUpdate, prefix3Utf8);

                                var newPayloadJson = JsonSerializer.SerializeToUtf8Bytes(
                                    new VaultPayload(
                                        oldPayload.FileHash,
                                        Convert.ToBase64String(newPrefix3Hash),
                                        Convert.ToBase64String(newSalt)),
                                    MultiVaultJsonContext.Default.VaultPayload);
                                registry.EncryptedPayload = crypto.Encrypt(newPayloadJson, kSharedForUpdate);
                                CryptographicOperations.ZeroMemory(newPayloadJson);
                            }
                            else
                            {
                                logger.LogError("Failed to decrypt VaultRegistries[{N}]'s EncryptedPayload, so VaultSaltPwd could not be updated.", vaultNum);
                            }
                        }

                        await udb.SaveChangesAsync();
                        logger.LogInformation("Re-wrapped KSharedSlot_{N} and VaultRegistries[{N}].VaultSaltPwd with the new password.", vaultNum, vaultNum);
                    }

                    // Also update KSharedEac with the DEK. The local dek (unwrapped with the old password, so
                    // the same DEK the session holds) is used instead of session.GetKey(): the latter throws
                    // if a Lock() landed since the guard, which would abort here with the Auth blob and
                    // the unified slot already rewritten.
                    var kshRecBlob = crypto.Encrypt(kSharedForUpdate, dek);
                    await UpsertSettingAsync(db, VaultMetadataKey.KSharedEac, kshRecBlob);
                    await db.SaveChangesAsync();
                    logger.LogInformation("Updated KSharedEac.");
                }
                logger.LogInformation("Master password change succeeded.");
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(oldKek); // double-zero: harmless
                CryptographicOperations.ZeroMemory(dek);
                CryptographicOperations.ZeroMemory(newKek); // double-zero: harmless
                if (kSharedForUpdate != null) CryptographicOperations.ZeroMemory(kSharedForUpdate);
                if (newKMaster != null) CryptographicOperations.ZeroMemory(newKMaster);
            }
        }
        finally
        {
            oldBuf.Dispose();
            newBuf.Dispose();
        }
    }

    /// <summary>
    /// Forcibly resets (re-wraps) the password using Windows Hello (biometric authentication), without knowing the current password.
    /// </summary>
    public Task<bool> ResetMasterPasswordWithHelloAsync(ReadOnlySpan<char> newPassword)
    {
        var pwdBuf = new SecureCharBuffer(); pwdBuf.SetFromSpan(newPassword);
        return ResetMasterPasswordWithHelloAsyncCore(pwdBuf);
    }

    private async Task<bool> ResetMasterPasswordWithHelloAsyncCore(SecureCharBuffer pwdBuf)
    {
        try
        {
        logger.LogInformation("Starting forced master password reset via Windows Hello.");
        // Captured synchronously, before the first await: the session state this reset started from.
        bool wasUnlocked = session.IsUnlocked;

        // 1. Windows Hello authentication
        var availability = await _helloAdapter.CheckAvailabilityAsync();
        if (availability != UserConsentVerifierAvailability.Available)
        {
            logger.LogWarning("Windows Hello is not available.");
            return false;
        }

        UserConsentVerificationResult result;
        try { result = await _helloAdapter.RequestVerificationAsync(LocalizationManager.Get("VaultSettings.Dialog.ForceResetViaHelloPrompt")); }
        catch (Exception ex)
        {
            logger.LogError("An exception occurred while invoking the Windows Hello prompt. [{ExType}]", ex.GetType().Name);
            return false;
        }
        if (result != UserConsentVerificationResult.Verified)
        {
            logger.LogWarning("Windows Hello authentication was canceled or failed.");
            return false;
        }

        // 2. Protect the DEK's entire lifetime, from creation to disposal, in a single try-finally
        byte[]? dekPinned = null;
        byte[]? dekRaw    = null;
        // K_shared re-wrap inputs. Captured and derived BEFORE the first write (see step 3), so the writes
        // never have to read the session again; zeroed in the finally at the end.
        byte[]? kSharedForUpdate = null;
        byte[]? newUnifiedSalt   = null;
        byte[]? newKMaster       = null;
        int vaultNum = 0;
        try
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                var helloSetting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello);
                if (helloSetting == null) return false;

                // Use Base64.DecodeFromUtf8 to avoid an immutable string intermediate
                int maxDecoded = Base64.GetMaxDecodedFromUtf8Length(helloSetting.ConfigValue.Length);
                var protectedKeyBuf = GC.AllocateArray<byte>(maxDecoded, pinned: true);
                try
                {
                    Base64.DecodeFromUtf8(helloSetting.ConfigValue, protectedKeyBuf, out _, out int decodedLen);
                    dekRaw = _protectedData.Unprotect(
                        protectedKeyBuf.AsSpan(0, decodedLen).ToArray(),
                        null, DataProtectionScope.CurrentUser);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedKeyBuf);
                }

                // Immediately move the Unprotect result into a pinned buffer
                dekPinned = GC.AllocateArray<byte>(dekRaw.Length, pinned: true);
                dekRaw.AsSpan().CopyTo(dekPinned);
                CryptographicOperations.ZeroMemory(dekRaw);
                dekRaw = null;
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to restore the DEK via DPAPI. [{ExType}]", ex.GetType().Name);
                return false;
            }

            // 3. Re-wrap the restored DEK with a KEK derived from the new password
            var newSalt = crypto.GenerateSalt(32);

            // Put newKek into a pinned fixed-address buffer
            var newKek = GC.AllocateArray<byte>(32, pinned: true);
            byte[] newWrappedDek;
            try
            {
                // Off the UI thread (see AcquireKSharedCoreAsync); pwdBuf/newSalt/newKek are only released
                // after this await completes.
                await Task.Run(() => crypto.DeriveKey(pwdBuf.Span, newSalt, newKek)); // span-output
                newWrappedDek = crypto.Encrypt(dekPinned!, newKek);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newKek);
            }

            // Capture K_shared and derive K_master_N for the re-wrap now, before any write. Only
            // session.CurrentVaultDbNumber is updated (same policy as the Standard path): other vaults keep
            // independent passwords, so they are intentionally excluded.
            if (session.HasKShared)
            {
                vaultNum = session.CurrentVaultDbNumber ?? 0;
                kSharedForUpdate = GC.AllocateArray<byte>(32, pinned: true);
                session.GetKShared().Span.CopyTo(kSharedForUpdate);
                if (vaultNum >= 1 && vaultNum <= 3)
                {
                    var salt = crypto.GenerateSalt(32);
                    var km   = GC.AllocateArray<byte>(32, pinned: true);
                    newUnifiedSalt = salt;
                    newKMaster     = km; // assigned before the derivation so the finally always zeroes it
                    await Task.Run(() => DeriveArgon2id(pwdBuf.Span, salt, km));
                }
            }

            // Stale-result guard, before the first write (and outside the catch below, which would report a
            // cancellation as a re-wrap failure): abort if a Lock() landed during the derivations, rather
            // than leave the Auth blob, the unified slot and the registry disagreeing about the password.
            // Past this point the writes use only the local copies and never read the session.
            ThrowIfDerivationStale(wasUnlocked, CancellationToken.None);

            try
            {
                var newAuthDataBytes = JsonSerializer.SerializeToUtf8Bytes(new AuthData(
                    Convert.ToBase64String(newSalt),
                    Convert.ToBase64String(newWrappedDek)), AuthServiceJsonContext.Default.AuthData);

                await using var db = await factory.CreateDbContextAsync();
                var setting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.Auth);
                if (setting != null)
                {
                    setting.ConfigValue = newAuthDataBytes;
                    await db.SaveChangesAsync();

                    // Re-wrap KSharedSlot_N / KSharedEac with the new password (Hello path).
                    // Performs the same update as ChangeMasterPasswordStandardCoreAsync.
                    if (kSharedForUpdate != null)
                    {
                        if (newKMaster != null && newUnifiedSalt != null)
                        {
                            var wrapData = crypto.Encrypt(kSharedForUpdate, newKMaster);
                            var coarse   = ComputeCoarseHash(pwdBuf.Span);
                            var newSlot  = new byte[SlotTotalSize];
                            coarse.AsSpan(0, SlotCoarseHashSize)
                                  .CopyTo(newSlot.AsSpan(SlotCoarseHashOffset, SlotCoarseHashSize));
                            newUnifiedSalt.AsSpan()
                                          .CopyTo(newSlot.AsSpan(SlotUnifiedSaltOffset, SlotUnifiedSaltSize));
                            wrapData.AsSpan()
                                    .CopyTo(newSlot.AsSpan(SlotWrappedKShOffset, SlotWrappedKShSize));

                            await using var udb = await unifiedFactory.CreateDbContextAsync();
                            await UpsertUnifiedSettingAsync(udb, UnifiedMetadataKey.KSharedSlotForVault(vaultNum), newSlot);

                            // As with ChangeMasterPasswordStandardCoreAsync (Hello path),
                            // also update VaultRegistries[N].EncryptedPayload's VaultSaltPwd to the new salt.
                            var registry = await udb.VaultRegistries.FirstOrDefaultAsync(v => v.DbNumber == vaultNum);
                            if (registry != null)
                            {
                                var oldPayload = DecryptVaultPayloadWithKey(registry.EncryptedPayload, kSharedForUpdate);
                                if (oldPayload != null)
                                {
                                    int prefixLen = Math.Min(3, pwdBuf.Span.Length);
                                    Span<byte> prefix3Utf8 = stackalloc byte[Encoding.UTF8.GetByteCount(pwdBuf.Span[..prefixLen])];
                                    Encoding.UTF8.GetBytes(pwdBuf.Span[..prefixLen], prefix3Utf8);
                                    var newPrefix3Hash = HMACSHA256.HashData(kSharedForUpdate, prefix3Utf8);

                                    var newPayloadJson = JsonSerializer.SerializeToUtf8Bytes(
                                        new VaultPayload(
                                            oldPayload.FileHash,
                                            Convert.ToBase64String(newPrefix3Hash),
                                            Convert.ToBase64String(newSalt)),
                                        MultiVaultJsonContext.Default.VaultPayload);
                                    registry.EncryptedPayload = crypto.Encrypt(newPayloadJson, kSharedForUpdate);
                                    CryptographicOperations.ZeroMemory(newPayloadJson);
                                }
                                else
                                {
                                    logger.LogError("Failed to decrypt VaultRegistries[{N}]'s EncryptedPayload, so VaultSaltPwd could not be updated (Hello path).", vaultNum);
                                }
                            }

                            await udb.SaveChangesAsync();
                            logger.LogInformation("Re-wrapped KSharedSlot_{N} and VaultRegistries[{N}].VaultSaltPwd with the new password (Hello path).", vaultNum, vaultNum);
                        }

                        var kshRecBlob = crypto.Encrypt(kSharedForUpdate, dekPinned!.AsSpan());
                        await UpsertSettingAsync(db, VaultMetadataKey.KSharedEac, kshRecBlob);
                        await db.SaveChangesAsync();
                        logger.LogInformation("Updated KSharedEac (Hello path).");
                    }

                    logger.LogInformation("Forced password reset via Windows Hello authentication succeeded.");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to re-wrap with the new password. [{ExType}]", ex.GetType().Name);
                return false;
            }
        }
        finally
        {
            if (dekRaw    != null) CryptographicOperations.ZeroMemory(dekRaw);
            if (dekPinned != null) CryptographicOperations.ZeroMemory(dekPinned);
            if (kSharedForUpdate != null) CryptographicOperations.ZeroMemory(kSharedForUpdate);
            if (newKMaster != null) CryptographicOperations.ZeroMemory(newKMaster);
        }
        }
        finally { pwdBuf.Dispose(); }
    }

    // ── Emergency access code ──────────────────────────────────────────────

    private const string EacScheme = "naimitsu-eac://v1/";

    public async Task<bool> HasEmergencyAccessCodeAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Metadata.AnyAsync(s => s.ConfigKey == VaultMetadataKey.EmergencyAccessCode);
    }

    /// <summary>
    /// Generates an emergency access code key (EAC) and wraps the DEK with it. This performs no DB
    /// I/O by itself — the caller must call CommitEmergencyAccessCodeAsync(WrappedDek) only after the
    /// QR PNG has been confirmed saved to disk. Committing before that point would silently replace
    /// (invalidate) any previously active EAC the instant this runs, even if the user then cancels
    /// the file save and never obtains a QR for the new one.
    /// Returns the QR payload string, a suggested filename (Naimitsu_vlt[VaultNumber]_yyyyMMdd_HHmmss.png), and
    /// the wrapped DEK to pass to CommitEmergencyAccessCodeAsync.
    /// </summary>
    public Task<(string QrPayload, string SuggestedFileName, byte[] WrappedDek)> GenerateEmergencyAccessCodeAsync(ReadOnlySpan<char> pin)
    {
        var pinBuf = new SecureCharBuffer(); pinBuf.SetFromSpan(pin);
        return GenerateEmergencyAccessCodeAsyncCore(pinBuf);
    }

    private async Task<(string QrPayload, string SuggestedFileName, byte[] WrappedDek)> GenerateEmergencyAccessCodeAsyncCore(SecureCharBuffer pinBuf)
    {
        try
        {
        logger.LogInformation("Starting generation of the emergency access code.");
        // Captured synchronously, before the first await: the session state this generation started from.
        bool wasUnlocked = session.IsUnlocked;

        // Put rk / salt / pinKey into GC-pinned fixed-address buffers (plain new byte[] is strictly forbidden)
        var rk     = GC.AllocateArray<byte>(32, pinned: true);
        var salt   = GC.AllocateArray<byte>(16, pinned: true);
        var pinKey = GC.AllocateArray<byte>(32, pinned: true);

        byte[] wrappedDek;
        byte[] payload;
        try
        {
            RandomNumberGenerator.Fill(rk);
            RandomNumberGenerator.Fill(salt);

            wrappedDek = crypto.Encrypt(session.GetKey().Span, rk);

            // Off the UI thread (see AcquireKSharedCoreAsync); salt/pinKey/pinBuf are only zeroed in the
            // finally blocks, strictly after this await completes.
            await Task.Run(() => DerivePinKey(pinBuf.Span, salt, pinKey)); // span-output, transfers the source to the stack
            byte[] encryptedRk;
            try
            {
                encryptedRk = crypto.Encrypt(rk, pinKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pinKey);
            }

            // Payload: version(1) + salt(16) + nonce+tag+encryptedRk(12+16+32=60) = 77 bytes
            payload = new byte[1 + 16 + encryptedRk.Length];
            payload[0] = 1;
            salt.AsSpan().CopyTo(payload.AsSpan(1));
            encryptedRk.CopyTo(payload, 17);
        }
        finally
        {
            // Physically wipe all key material before crossing an await
            CryptographicOperations.ZeroMemory(rk);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(pinKey); // double-zero: harmless
        }

        // Stale-result guard: the wrapped DEK above was built from the session's DEK before the derivation.
        // If Lock() landed meanwhile, do not hand back an emergency access code for a session that no
        // longer exists (the caller would go on to save the QR and commit it to the vault DB).
        ThrowIfDerivationStale(wasUnlocked, CancellationToken.None);

        logger.LogInformation("Generated the emergency access code.");
        var qrPayload = EacScheme + Convert.ToBase64String(payload);
        // Matches the "yyyyMMdd_HHmmss" date/time naming convention used elsewhere (AutoBackupService,
        // AuthService.Restore) - a timestamp distinguishes files from repeated generation as reliably
        // as a content hash would, without the extra computation.
        var suggestedFileName = $"Naimitsu_vlt{session.DisplayedVaultNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        return (qrPayload, suggestedFileName, wrappedDek);
        }
        finally { pinBuf.Dispose(); }
    }

    /// <summary>
    /// Persists the wrapped DEK produced by GenerateEmergencyAccessCodeAsync, replacing any existing
    /// emergency access code. Call only after the QR PNG has been confirmed saved to disk.
    /// </summary>
    public async Task CommitEmergencyAccessCodeAsync(byte[] wrappedDek)
    {
        var eacBytes = JsonSerializer.SerializeToUtf8Bytes(new EacData(Convert.ToBase64String(wrappedDek)), AuthServiceJsonContext.Default.EacData);
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.EmergencyAccessCode);
        if (existing == null)
            db.Metadata.Add(new VaultMetadata { ConfigKey = VaultMetadataKey.EmergencyAccessCode, ConfigValue = eacBytes });
        else
            existing.ConfigValue = eacBytes;
        await db.SaveChangesAsync();
        logger.LogInformation("Committed the emergency access code.");
    }

    public async Task RevokeEmergencyAccessCodeAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.EmergencyAccessCode);
        if (existing != null)
        {
            db.Metadata.Remove(existing);
            await db.SaveChangesAsync();
            logger.LogInformation("Removed the emergency access code.");
        }
    }

    private Task TryLogAuthFailedAsync()
    {
        // Audit log locality: when ActiveVaultDbPath is null (pre-auth), never write to the vault DB.
        // The failure count is kept in an in-memory counter and flushed via FlushPreAuthFailuresAsync after successful vault entry.
        if (connectionProvider.ActiveVaultDbPath == null)
        {
            session.FailedLoginAttempts++;
            logger.LogWarning("Authentication failure #{Count} (pre-auth: not persisted to vault DB)", session.FailedLoginAttempts);
            return Task.CompletedTask;
        }
        return TryLogAuthFailedToVaultAsync();
    }

    private async Task TryLogAuthFailedToVaultAsync()
    {
        try { await auditLog.LogAuthFailedAsync(); }
        catch (Exception ex) { logger.LogWarning("Failed to record the AuthFailed audit log entry. [{ExType}]", ex.GetType().Name); }
    }

    // Called only after vault_DEK is confirmed unwrapped (password or Windows Hello) - real-space
    // auth success only, mirroring AuthSucceeded's design intent (emergency-access recovery has its
    // own EmergencyAccessCodeExecuted event instead).
    private async Task TryLogAuthSucceededAsync()
    {
        try { await auditLog.LogAsync(AuditEventCode.AuthSucceeded, null, session.GetKey()); }
        catch (Exception ex) { logger.LogWarning("Failed to record the AuthSucceeded audit log entry. [{ExType}]", ex.GetType().Name); }
    }

    /// <summary>
    /// Derives an AES-256-GCM key from a PIN code and writes it directly to output.
    /// Avoids ordinary heap contamination from Encoding.UTF8.GetBytes(string).
    /// Expands within the stack via stackalloc, creating only the minimal heap copy needed for Argon2id's byte[] argument.
    /// </summary>
    private static void DerivePinKey(ReadOnlySpan<char> pin, ReadOnlySpan<byte> salt, Span<byte> output)
    {
        int byteCount = Encoding.UTF8.GetByteCount(pin);

        Span<byte> pinBytesStack = stackalloc byte[byteCount];
        Encoding.UTF8.GetBytes(pin, pinBytesStack);

        // Allocate on the POH (pinned object heap): even if a GC compaction runs during the Argon2id
        // computation, the address never moves, so the finally ZeroMemory always wipes the correct
        // address (same precaution as DeriveArgon2id above; plain new byte[]/ToArray() is not pinned).
        var pinBytesHeap = GC.AllocateArray<byte>(byteCount, pinned: true);
        pinBytesStack.CopyTo(pinBytesHeap);
        CryptographicOperations.ZeroMemory(pinBytesStack); // wipe the stack copy immediately

        try
        {
            var saltArr = GC.AllocateArray<byte>(salt.Length, pinned: true);
            salt.CopyTo(saltArr);
            try
            {
                using var argon2 = new Konscious.Security.Cryptography.Argon2id(pinBytesHeap)
                {
                    Salt                = saltArr,
                    DegreeOfParallelism = Argon2Parameters.PinParallelism,
                    MemorySize          = Argon2Parameters.PinMemorySize,
                    Iterations          = Argon2Parameters.PinIterations,
                };
                var keyArr = argon2.GetBytes(output.Length);
                try { keyArr.AsSpan().CopyTo(output); }
                finally { CryptographicOperations.ZeroMemory(keyArr.AsSpan()); }
            }
            finally { CryptographicOperations.ZeroMemory(saltArr.AsSpan()); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytesHeap.AsSpan());
        }
    }

    /// <summary>Session-independent VaultPayload decryption that takes K_shared directly. Used when recovering K_shared via the emergency access code.</summary>
    private VaultPayload? DecryptVaultPayloadWithKey(byte[] encryptedPayload, ReadOnlySpan<byte> kShared)
    {
        try
        {
            int plainLen = encryptedPayload.Length - ICryptoService.AeadOverhead;
            if (plainLen <= 0) return null;
            var plainBuf = GC.AllocateArray<byte>(plainLen, pinned: true);
            try
            {
                crypto.Decrypt(encryptedPayload, kShared, plainBuf);
                return JsonSerializer.Deserialize(plainBuf.AsSpan(), MultiVaultJsonContext.Default.VaultPayload);
            }
            finally { CryptographicOperations.ZeroMemory(plainBuf); }
        }
        catch { return null; }
    }

    // ── Restoring to restricted read-only mode via emergency access code (QR+PIN) ─────────────

    /// <summary>
    /// Scans all vault files under data/, identifies a vault that can be unlocked with the RK (QR+PIN),
    /// and restores it into in-memory restricted read-only mode. Scans directly via SQLite, without EF Core / factory.
    /// </summary>
    public Task<EmergencyAccessResult> EmergencyAccessUnlockAsync(
        ReadOnlySpan<char> qrPayload,
        ReadOnlySpan<char> pin,
        string dataDir,
        CancellationToken ct = default)
    {
        var qrBuf  = new SecureCharBuffer(); qrBuf.SetFromSpan(qrPayload);
        var pinBuf = new SecureCharBuffer(); pinBuf.SetFromSpan(pin);
        return EmergencyAccessUnlockAsyncCore(qrBuf, pinBuf, dataDir, ct);
    }

    private async Task<EmergencyAccessResult> EmergencyAccessUnlockAsyncCore(
        SecureCharBuffer qrBuf, SecureCharBuffer pinBuf, string dataDir, CancellationToken ct)
    {
        // Captured synchronously, before the first await: the session state this attempt started from.
        bool wasUnlocked = session.IsUnlocked;
        try
        {
        // ── Step 1: QR decode ───────────────────────────────────────────────
        ReadOnlySpan<char> qrSpan = qrBuf.Span;
        ReadOnlySpan<char> scheme = EacScheme.AsSpan();
        if (!qrSpan.StartsWith(scheme)) return EmergencyAccessResult.InvalidQr;

        ReadOnlySpan<char> b64Part = qrSpan.Slice(scheme.Length);
        Span<byte> payloadStack = stackalloc byte[128];
        if (!Convert.TryFromBase64Chars(b64Part, payloadStack, out int payloadLen))
            return EmergencyAccessResult.InvalidQr;
        if (payloadLen < 77 || payloadStack[0] != 1)
            return EmergencyAccessResult.InvalidQr;

        var payload = payloadStack.Slice(0, payloadLen).ToArray(); // public data

        // ── Step 2: PIN -> RK decryption ────────────────────────────────────────────
        var rk     = GC.AllocateArray<byte>(32, pinned: true);
        var pinKey = GC.AllocateArray<byte>(32, pinned: true);
        byte[]? foundDek = null; // declared in the outer scope so finally can reliably ZeroMemory it
        string? foundPath = null;
        try
        {
            // Off the UI thread (see AcquireKSharedCoreAsync). The salt is public data; a span over the
            // payload cannot be captured by the lambda, so hand it a copy. pinBuf/pinKey are only
            // zeroed in the finally blocks, strictly after this await completes.
            var pinSalt = payload.AsSpan(1, 16).ToArray();
            await Task.Run(() => DerivePinKey(pinBuf.Span, pinSalt, pinKey), ct);
            try
            {
                crypto.Decrypt(payload.AsSpan(17), pinKey, rk);
            }
            catch (CryptographicException)
            {
                logger.LogWarning("EAC: PIN mismatch or corrupted QR code.");
                return EmergencyAccessResult.WrongPin;
            }
            finally { CryptographicOperations.ZeroMemory(pinKey); }

            // ── Step 3: brute-force scan of the data/ folder ─────────────────────────
            foreach (var path in ScanVaultCandidates(dataDir))
            {
                var configValue = await TryReadEmergencyAccessCodeAsync(path);
                if (configValue == null) continue;

                // configValue is stored in JSON {"WrappedDek":"<base64>"} format.
                // Base64-decode it to extract the actual encrypted DEK bytes before decrypting.
                EacData? eacRecord;
                try { eacRecord = JsonSerializer.Deserialize(configValue, AuthServiceJsonContext.Default.EacData); }
                catch { continue; }
                if (eacRecord?.WrappedDek == null) continue;

                byte[] encryptedDek;
                try { encryptedDek = Convert.FromBase64String(eacRecord.WrappedDek); }
                catch (FormatException) { continue; }

                var dekCandidate = GC.AllocateArray<byte>(32, pinned: true);
                try
                {
                    crypto.Decrypt(encryptedDek, rk, dekCandidate);
                    foundPath = path;
                    foundDek  = dekCandidate;
                    break; // ZeroMemory guaranteed by the outer finally
                }
                catch (CryptographicException)
                {
                    CryptographicOperations.ZeroMemory(dekCandidate);
                }
            }

            if (foundPath == null || foundDek == null)
            {
                logger.LogWarning("EAC: No matching vault was found under data/.");
                return EmergencyAccessResult.NoMatchingVault;
            }

            // ── Step 4: finalize vault (restricted read-only mode: physical DB writes fully suppressed) ──────
            // Stale-result guard: the PIN derivation and the file scan above ran while the session was free
            // to change (window closed, another unlock finished). Check before touching the connection provider.
            ThrowIfDerivationStale(wasUnlocked, ct);

            // The scan loop above uses unpooled connections, so it leaves nothing behind. This still
            // releases any other pooled connection (e.g. from an earlier EF Core context) before opening
            // the EF Core context on the found file, avoiding a file-lock conflict on Windows.
            SqliteConnection.ClearAllPools();
            connectionProvider.SetActiveVault(foundPath);
            await using var db = await factory.CreateDbContextAsync();

            // Second guard, immediately before the write (the context creation above can yield). On
            // failure also undo SetActiveVault, the same cleanup the other abort paths do.
            try { ThrowIfDerivationStale(wasUnlocked, ct); }
            catch (OperationCanceledException) { connectionProvider.ClearActiveVault(); throw; }

            session.SetKey(foundDek.AsSpan());
            logger.LogInformation("EAC(ReadOnly): Skipping Auth update. DEK stays in memory only.");

            // ── Step 5: K_shared recovery (in-memory only) ────────────────────────────
            await ApplyKSharedAfterEmergencyAccessAsync(db, foundPath);

            // Step 5.1: DisplayedVaultNumber fallback (when K_shared recovery fails)
            // Even if ApplyKShared could not obtain K_shared, DbNumber can still be read from
            // VaultIndex(0x1005). Decryption is possible since the DEK is already set via SetKey.
            if (!session.DisplayedVaultNumber.HasValue)
                await TrySetVaultNumberFromIndexAsync(db);

            session.IsReadOnlyRestricted = true;
            logger.LogInformation("EAC: Restore succeeded. Vault={Path}", foundPath);

            // Write-back: flush the pre-auth failure count to the vault DB
            if (session.FailedLoginAttempts > 0)
            {
                try
                {
                    await auditLog.FlushPreAuthFailuresAsync(session.FailedLoginAttempts);
                    session.FailedLoginAttempts = 0;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("FlushPreAuthFailures failed (non-fatal). [{ExType}]", ex.GetType().Name);
                }
            }

            // If there are pending RestoreExecuted entries targeting this vault, flush them
            if (session.DisplayedVaultNumber.HasValue)
            {
                try
                {
                    await auditLog.FlushPendingRestoreAuditAsync(session.GetKey(), session.DisplayedVaultNumber.Value, dataDir);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("FlushPendingRestoreAudit failed (non-fatal). [{ExType}]", ex.GetType().Name);
                }
            }

            // Clear the auto-recovery/unrecoverable flags since this went through the recovery path
            session.UnifiedDbAutoRecovered      = false;
            session.VaultDbAutoRecovered        = false;
            session.VaultDbAutoRecoveredNumber  = null;
            session.VaultDbUnrecoverable        = false;
            session.VaultDbUnrecoverableNumber  = null;

            try
            {
                await auditLog.LogAsync(AuditEventCode.EmergencyAccessCodeExecuted, null, session.GetKey());
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to record the EmergencyAccessCodeExecuted audit log entry. [{ExType}]", ex.GetType().Name);
            }

            return EmergencyAccessResult.Success;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rk);
            CryptographicOperations.ZeroMemory(pinKey);
            if (foundDek != null) CryptographicOperations.ZeroMemory(foundDek);
        }
        }
        finally { qrBuf.Dispose(); pinBuf.Dispose(); }
    }

    /// <summary>
    /// Recovers K_shared using KSharedEac(0x1004).
    /// Since this is restricted read-only mode only, all writes to the unified DB / vault DB are skipped;
    /// everything is completed in memory only.
    /// </summary>
    private async Task ApplyKSharedAfterEmergencyAccessAsync(
        AppDbContext db, string foundVaultPath)
    {
        var kshRecRow = await db.Metadata
            .FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.KSharedEac);
        if (kshRecRow == null) return;

        var kSharedRecovered = GC.AllocateArray<byte>(32, pinned: true);
        try
        {
            bool decryptOk = false;
            {
                var dekScopeForDecrypt = session.GetKey();
                try
                {
                    crypto.Decrypt(kshRecRow.ConfigValue, dekScopeForDecrypt.Span, kSharedRecovered);
                    decryptOk = true;
                }
                catch (CryptographicException)
                {
                    logger.LogWarning("Failed to decrypt KSharedEac. Skipping K_shared update (graceful degradation).");
                }
            }

            if (decryptOk)
            {
                // Identify DbNumber from the vault filename
                var standardB64 = Path.GetFileName(foundVaultPath).Replace('-', '+').Replace('_', '/');
                Span<byte> decoded = stackalloc byte[36];
                if (Convert.TryFromBase64String(standardB64, decoded, out int written) && written == 36)
                {
                    var fileHashOfThis = decoded.Slice(4, 32).ToArray();
                    await using var udbForScan = await unifiedFactory.CreateDbContextAsync();
                    var allRegistries = await udbForScan.VaultRegistries.ToListAsync();
                    int targetDbNumber = 0;
                    foreach (var reg in allRegistries)
                    {
                        var regPayload = DecryptVaultPayloadWithKey(reg.EncryptedPayload, kSharedRecovered);
                        if (regPayload == null) continue;
                        var regFileHash = Convert.FromBase64String(regPayload.FileHash);
                        if (regFileHash.AsSpan().SequenceEqual(fileHashOfThis))
                        { targetDbNumber = reg.DbNumber; break; }
                    }

                    if (targetDbNumber >= 1 && targetDbNumber <= 3)
                    {
                        // Since this is restricted read-only mode only, writes to the unified DB are fully suppressed
                        logger.LogInformation("EAC(ReadOnly): Skipping KSharedSlot persistence. K_shared stays in memory only.");

                        session.SetKShared(kSharedRecovered);
                        session.CurrentVaultDbNumber = targetDbNumber;
                        session.DisplayedVaultNumber = targetDbNumber;
                    }
                    else
                    {
                        logger.LogWarning("Failed to identify DbNumber ({N}). Skipping KSharedSlot update.", targetDbNumber);
                    }
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kSharedRecovered);
        }
    }

    /// <summary>
    /// Fallback DbNumber read during EAC unlock.
    /// Decrypts the vault DB's VaultIndex(0x1005) with the session's DEK to set CurrentVaultDbNumber / DisplayedVaultNumber.
    /// Called as graceful degradation when K_shared recovery fails.
    /// </summary>
    private async Task TrySetVaultNumberFromIndexAsync(AppDbContext db)
    {
        try
        {
            var row = await db.Metadata
                .FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.VaultIndex);
            if (row?.ConfigValue == null || row.ConfigValue.Length <= ICryptoService.AeadOverhead) return;

            var dek     = session.GetKey();
            var jsonBuf = new byte[row.ConfigValue.Length - ICryptoService.AeadOverhead];
            crypto.Decrypt(row.ConfigValue, dek.Span, jsonBuf);

            var data = JsonSerializer.Deserialize(jsonBuf, AuthServiceJsonContext.Default.VaultIndexData);
            if (data is { OriginalDbNumber: >= 1 and <= 3 })
            {
                session.CurrentVaultDbNumber = data.OriginalDbNumber;
                session.DisplayedVaultNumber = data.OriginalDbNumber;
                logger.LogInformation("EAC: Restored DbNumber={N} from VaultIndex.", data.OriginalDbNumber);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("EAC: Failed to read VaultIndex (non-fatal). [{ExType}]", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reads VaultMetadata[EmergencyAccessCode(0x1003)] from a candidate vault file
    /// directly via SqliteConnection (ReadOnly), without EF Core.
    /// Can be called even without ActiveVaultDbPath set.
    /// Pooling is disabled so the file handle is released as soon as the connection is disposed. This
    /// scan visits every candidate vault file, and the only pool cleanup (ClearAllPools) sits on the
    /// success path of the caller - a pooled handle left on a non-matching file would block deleting
    /// or replacing it afterwards.
    /// </summary>
    private static async Task<byte[]?> TryReadEmergencyAccessCodeAsync(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource  = dbPath,
            Mode        = SqliteOpenMode.ReadOnly,
            ForeignKeys = false,
            Pooling     = false,
        };
        try
        {
            await using var conn = new SqliteConnection(csb.ToString());
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT ConfigValue FROM VaultMetadata WHERE ConfigKey = {VaultMetadataKey.EmergencyAccessCode} LIMIT 1";
            var result = await cmd.ExecuteScalarAsync();
            return result is byte[] b ? b : null;
        }
        catch { return null; }
    }

    private record AuthData(
        string Salt,
        string WrappedDek,
        [property: System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? SaltNkey = null);
    private record EacData(string WrappedDek);

    /// <summary>
    /// JSON payload of the VaultIndex(0x1005) BLOB.
    /// Saved encrypted with AES-256-GCM using vault_DEK.
    /// </summary>
    private record VaultIndexData(int OriginalDbNumber);

    [JsonSerializable(typeof(AuthData))]
    [JsonSerializable(typeof(EacData))]
    [JsonSerializable(typeof(VaultIndexData))]
    private partial class AuthServiceJsonContext : JsonSerializerContext { }
}

public enum EmergencyAccessResult
{
    Success,
    InvalidQr,           // Invalid QR scheme / version != 1
    WrongPin,            // AES-GCM authentication failure (PIN mismatch)
    NoMatchingVault,     // No vault under data/ can be unlocked with the RK
}
