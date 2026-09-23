// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
//
// Multi-vault crypto DB infrastructure — K_shared acquisition, vault entry, new DB creation, orphan file GC

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public partial class AuthService
{
    // ── K_shared Key Wrap slot constants ─────────────────────────────────────
    // 96B BLOB = coarse_hash[4] + unified_salt[32] + ICryptoService.Encrypt(K_shared, K_master)[60]
    private const int SlotCoarseHashOffset  = 0;
    private const int SlotCoarseHashSize    = 4;
    private const int SlotUnifiedSaltOffset = 4;
    private const int SlotUnifiedSaltSize   = 32;
    private const int SlotWrappedKShOffset  = 36;
    private const int SlotWrappedKShSize    = 60; // nonce[12] + tag[16] + cipher[32]
    private const int SlotTotalSize         = 96;

    // ── coarse_hash pre-filter ────────────────────────────────────────────

    /// <summary>
    /// SHA-256(UTF-8(pw[0:3]))[0:4] — coarse hash for an O(1) pre-filter.
    /// Stored as plaintext, so it can also be computed by an attacker who does not hold K_shared (acceptable by design).
    /// </summary>
    private static byte[] ComputeCoarseHash(ReadOnlySpan<char> password)
    {
        int prefixLen = Math.Min(3, password.Length);
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetByteCount(password[..prefixLen])];
        Encoding.UTF8.GetBytes(password[..prefixLen], utf8);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(utf8, hash);
        return hash[..SlotCoarseHashSize].ToArray();
    }

    // ── K_shared acquisition ──────────────────────────────────────────────────────

    /// <summary>
    /// Pre-filters all KSharedSlots in the unified DB by coarse_hash, then decrypts K_shared with
    /// Argon2id + AES-256-GCM and sets it in the session.
    /// If multiple slots match the coarse_hash, the caller shows a dialog.
    /// </summary>
    /// <returns>
    /// On success: the list of matched vault numbers (a dialog is needed if it contains more than one).
    /// On password mismatch: an empty list.
    /// </returns>
    public Task<List<int>> AcquireKSharedAsync(ReadOnlySpan<char> password, CancellationToken ct = default)
    {
        var pwBuf = new SecureCharBuffer();
        pwBuf.SetFromSpan(password);
        return AcquireKSharedCoreAsync(pwBuf, ct); // pwBuf is disposed in core's finally block
    }

    /// <summary>
    /// Throws OperationCanceledException if the caller cancelled, or if the session was locked/unlocked
    /// since <paramref name="wasUnlocked"/> was captured. Argon2id now runs off the UI thread, so unlike
    /// before (when the frozen UI thread implicitly serialized everything) a Lock(), a window close, or a
    /// concurrent unlock can land while a derivation is in flight. Call this immediately before writing a
    /// derived key into the session so a stale result is wiped instead of resurrecting a locked session.
    /// </summary>
    private void ThrowIfDerivationStale(bool wasUnlocked, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (session.IsUnlocked != wasUnlocked)
            throw new OperationCanceledException("The session state changed while the key was being derived.");
    }

    private async Task<List<int>> AcquireKSharedCoreAsync(SecureCharBuffer pwBuf, CancellationToken ct)
    {
        try
        {

        // Denial-of-service defense: fewer than 8 characters ends immediately at zero cost as "mismatch". Never proceeds to coarse_hash / slot scanning.
        if (pwBuf.Span.Length < 8) { await TryLogAuthFailedAsync(); return []; }

        // Captured synchronously, before the first await, so it reflects the state the caller started from.
        bool wasUnlocked = session.IsUnlocked;

        var coarse = ComputeCoarseHash(pwBuf.Span);
        var matchedVaults = new List<int>();

        await using var db = await unifiedFactory.CreateDbContextAsync();
        var slots = await db.Metadata
            .Where(s => s.ConfigKey >= UnifiedMetadataKey.KSharedSlot_1 &&
                        s.ConfigKey <= UnifiedMetadataKey.KSharedSlot_3)
            .ToListAsync();

        byte[]? foundKShared = null;
        int     foundVaultNum = 0;

        // The outer finally wipes foundKShared on every exit path (including an exception or
        // cancellation on a later slot after an earlier slot already matched).
        try
        {
        foreach (var slot in slots)
        {
            if (slot.ConfigValue is not { Length: SlotTotalSize } sv) continue;
            if (!sv.AsSpan(SlotCoarseHashOffset, SlotCoarseHashSize).SequenceEqual(coarse)) continue;

            // coarse_hash matched -> derive K_master with Argon2id and attempt to decrypt K_shared
            var unifiedSalt  = sv.AsSpan(SlotUnifiedSaltOffset, SlotUnifiedSaltSize).ToArray();
            var wrappedKShared = sv.AsSpan(SlotWrappedKShOffset, SlotWrappedKShSize).ToArray();

            var kMaster = GC.AllocateArray<byte>(32, pinned: true);
            try
            {
                // Argon2id (64MB x 3 passes) is far too heavy for the UI thread this method resumes on.
                // Everything the lambda touches (pwBuf, unifiedSalt, kMaster) is only released by the
                // finally blocks below, which run strictly after this await completes - so the
                // background thread can never read a buffer that has already been zeroed.
                await Task.Run(() => DeriveArgon2id(pwBuf.Span, unifiedSalt, kMaster), ct);
                var kSharedBuf = GC.AllocateArray<byte>(32, pinned: true);
                try
                {
                    crypto.Decrypt(wrappedKShared, kMaster, kSharedBuf);

                    // decryption succeeded -> compute this slot's vault number (KSharedSlot_N = N = DbNumber)
                    int vaultNum = slot.ConfigKey;
                    matchedVaults.Add(vaultNum);

                    // keep the first match as the candidate
                    if (foundKShared == null)
                    {
                        foundKShared  = kSharedBuf;
                        foundVaultNum = vaultNum;
                    }
                    else
                    {
                        CryptographicOperations.ZeroMemory(kSharedBuf);
                    }
                }
                catch (CryptographicException)
                {
                    CryptographicOperations.ZeroMemory(kSharedBuf);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kMaster);
                CryptographicOperations.ZeroMemory(unifiedSalt.AsSpan());
            }
        }

        // Stale-result guard: do not touch the session (nor log an auth failure for an attempt nobody is
        // waiting on any more) if the caller cancelled or the session state changed during derivation.
        ThrowIfDerivationStale(wasUnlocked, ct);

        if (foundKShared != null)
        {
            session.SetKShared(foundKShared);
            logger.LogInformation("Acquired K_shared. Matched vault count={Count}", matchedVaults.Count);
        }
        else
        {
            await TryLogAuthFailedAsync();
        }

        return matchedVaults;
        }
        finally
        {
            if (foundKShared != null) CryptographicOperations.ZeroMemory(foundKShared);
        }
    }
    finally { pwBuf.Dispose(); }
    }

    // ── Vault entry (vault_DEK acquisition) ─────────────────────────────────────────

    /// <summary>
    /// Decrypts the specified vault's EncryptedPayload with K_shared to obtain the FileHash,
    /// then opens the vault DB file and unlocks vault_DEK.
    /// </summary>
    public Task<bool> EnterVaultAsync(int dbNumber, ReadOnlySpan<char> password, string dataDir, CancellationToken ct = default)
    {
        var pwBuf = new SecureCharBuffer();
        pwBuf.SetFromSpan(password);
        return EnterVaultCoreAsync(dbNumber, pwBuf, dataDir, ct);
    }

    private async Task<bool> EnterVaultCoreAsync(int dbNumber, SecureCharBuffer pwBuf, string dataDir, CancellationToken ct)
    {
        // Clear the vault-side auto-recovery/unrecoverable flags (prevent leftover state from a previous failed retry)
        session.VaultDbAutoRecovered       = false;
        session.VaultDbAutoRecoveredNumber = null;
        session.VaultDbUnrecoverable       = false;
        session.VaultDbUnrecoverableNumber = null;

        try
        {
        if (!session.HasKShared)
        {
            logger.LogError("K_shared is not set. Call AcquireKSharedAsync first.");
            return false;
        }

        await using var udb = await unifiedFactory.CreateDbContextAsync();
        var registry = await udb.VaultRegistries.FirstOrDefaultAsync(v => v.DbNumber == dbNumber);
        if (registry == null)
        {
            logger.LogWarning("VaultRegistry not found: DbNumber={N}", dbNumber);
            return false;
        }

        // Decrypt EncryptedPayload with K_shared to compute the file path
        VaultPayload? payload = DecryptVaultPayload(registry.EncryptedPayload);
        if (payload == null) return false;

        var vaultPath = ComputeVaultDbPath(Convert.FromBase64String(payload.FileHash), dataDir);
        bool vaultHealthy = File.Exists(vaultPath) && await ShadowFileService.IsFileHealthyAsync(vaultPath);
        if (!vaultHealthy && !await TryRecoverVaultFromShadowAsync(vaultPath, registry, dbNumber, dataDir))
        {
            return false;
        }

        // Orphan file GC must complete before SetActiveVault, while K_shared is still valid.
        //   BuildRegisteredHashesAsync decrypts the unified DB's VaultRegistries with K_shared to build the hash set.
        //   This call is completed by "awaiting on the caller's thread", and only the pure disk scan
        //   (RunOrphanGcDiskScanSync), which does not depend on K_shared's clearing timing, is deferred to the background.
        //   After SetActiveVault, focus exclusively on deriving vault_DEK; GC processing must never be mixed in.
        //
        // Fail-safe direction: if the registered-hash set cannot be built completely and reliably
        // (any exception, or even a single VaultRegistries row that fails to decrypt), skip the GC
        // scan entirely for this unlock rather than running it with an incomplete set. Substituting
        // an empty set on failure previously meant the scan ran against zero known vaults - which
        // would delete every vault DB file on disk, including the one being unlocked right now.
        HashSet<string>? orphanHashes;
        try
        {
            orphanHashes = await BuildRegisteredHashesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Skipping orphan file GC: could not safely build the registered-hash set. [{ExType}]", ex.GetType().Name);
            orphanHashes = null;
        }
        if (orphanHashes != null)
            _ = Task.Run(() => RunOrphanGcDiskScanSync(dataDir, orphanHashes));

        // Unlock vault_DEK (receive vault_salt_pwd from VaultPayload, avoiding the DB reading itself)
        connectionProvider.SetActiveVault(vaultPath);
        var rawVaultSalt = Convert.FromBase64String(payload.VaultSaltPwd);
        var vaultSaltPwdBytes = GC.AllocateArray<byte>(rawVaultSalt.Length, pinned: true);
        rawVaultSalt.AsSpan().CopyTo(vaultSaltPwdBytes.AsSpan());
        CryptographicOperations.ZeroMemory(rawVaultSalt); // zero the managed copy immediately
        try
        {
            bool unlocked = await UnlockCoreAsync(pwBuf, vaultSaltPwdBytes, ct);
            if (!unlocked)
            {
                await TryLogAuthFailedAsync();
                connectionProvider.ClearActiveVault();
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled / stale: nothing was written to the session, so also undo SetActiveVault above
            // (the same cleanup the wrong-password branch does) and let the caller see the cancellation.
            connectionProvider.ClearActiveVault();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultSaltPwdBytes);
        }

        session.CurrentVaultDbNumber = dbNumber;
        session.DisplayedVaultNumber = dbNumber;

        // Only reset per-vault selection state on an actual switch to a different vault, not on an
        // ordinary same-vault re-unlock after auto-lock (Lock() already clears CurrentVaultDbNumber,
        // so LastEnteredVaultDbNumber - which Lock() does not touch - is compared instead).
        if (session.LastEnteredVaultDbNumber != dbNumber)
            session.ResetPerVaultSelectionState();
        session.LastEnteredVaultDbNumber = dbNumber;

        // ── Retroactive KSharedEac write (only on first entry or when missing) ─────────────
        try
        {
            await using var vaultDb = await factory.CreateDbContextAsync();
            var kshRecExists = await vaultDb.Metadata
                .AnyAsync(s => s.ConfigKey == VaultMetadataKey.KSharedEac);
            if (!kshRecExists && session.HasKShared)
            {
                var kSharedBuf = GC.AllocateArray<byte>(32, pinned: true);
                try
                {
                    var kSharedScope = session.GetKShared();
                    kSharedScope.Span.CopyTo(kSharedBuf.AsSpan());
                    var dekScope = session.GetKey();
                    var kshRecBlob = crypto.Encrypt(kSharedBuf, dekScope.Span);
                    await UpsertSettingAsync(vaultDb, VaultMetadataKey.KSharedEac, kshRecBlob);
                    await vaultDb.SaveChangesAsync();
                    logger.LogInformation("Retroactively wrote KSharedEac. Vault={N}", dbNumber);
                }
                finally { CryptographicOperations.ZeroMemory(kSharedBuf); }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Retroactive KSharedEac write failed (non-fatal). [{ExType}]", ex.GetType().Name);
        }

        // ── Flush the pre-auth failure count to the vault DB ──────────────────────
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
        try
        {
            await auditLog.FlushPendingRestoreAuditAsync(session.GetKey(), dbNumber, dataDir);
        }
        catch (Exception ex)
        {
            logger.LogWarning("FlushPendingRestoreAudit failed (non-fatal). [{ExType}]", ex.GetType().Name);
        }

        // Update LastAccessedAt
        registry.LastAccessedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await udb.SaveChangesAsync();

        // Write the auto-recovery audit log entry (at the end, after success, once the DEK is confirmed)
        await WriteAutoRecoveryAuditLogAsync();

        logger.LogInformation("Entered Vault {N}.", dbNumber);
        return true;
        }
        finally { pwBuf.Dispose(); }
    }

    /// <summary>
    /// Decrypts EncryptedPayload with the session's K_shared and returns VaultPayload.
    /// For the session-independent version, use DecryptVaultPayloadWithKey in AuthService.cs.
    /// </summary>
    private VaultPayload? DecryptVaultPayload(byte[] encryptedPayload)
    {
        if (!session.HasKShared) return null;
        return DecryptVaultPayloadWithKey(encryptedPayload, session.GetKShared().Span);
    }

    /// <summary>
    /// Writes the forced audit-log entries for any auto-recovery that occurred during this unlock
    /// attempt (nkdb and/or vault-level), then clears the corresponding session flags. Must be called
    /// only after the DEK is confirmed (session.GetKey() requires it). Shared by both the password
    /// path (EnterVaultCoreAsync) and the Windows Hello path (EnterVaultWithHelloAsync) - both must
    /// record the same system-level audit trail regardless of which auth method actually unlocked.
    /// </summary>
    private async Task WriteAutoRecoveryAuditLogAsync()
    {
        var dekForAudit = session.GetKey();
        if (session.UnifiedDbAutoRecovered)
        {
            try
            {
                await auditLog.LogAsync(AuditEventCode.UnifiedDbAutoRecovered, null, dekForAudit);
                session.UnifiedDbAutoRecovered = false;
            }
            catch (Exception ex) { logger.LogWarning("Failed to write audit log 0xFF01 (non-fatal). [{ExType}]", ex.GetType().Name); }
        }
        if (session.VaultDbAutoRecovered && session.VaultDbAutoRecoveredNumber.HasValue)
        {
            try
            {
                await auditLog.LogAsync(AuditEventCode.VaultDbAutoRecovered,
                    new VaultDbAutoRecoveredPayload(session.VaultDbAutoRecoveredNumber.Value), dekForAudit);
                session.VaultDbAutoRecovered = false;
            }
            catch (Exception ex) { logger.LogWarning("Failed to write audit log 0xFF02 (non-fatal). [{ExType}]", ex.GetType().Name); }
        }
    }

    /// <summary>
    /// Attempts to recover vaultPath from its shadow. Shared by the password path
    /// (EnterVaultCoreAsync) and both stages of the Windows Hello path
    /// (AcquireKSharedWithHelloAsync, EnterVaultWithHelloAsync).
    /// ShadowFileKey (set by every WriteAll()) pins the shadow unambiguously; the FindByMagic scan is
    /// only a fallback for the rare case where no key was ever recorded (e.g. first shadow write never
    /// happened). Unlike the ShadowFileKey path, a magic scan can turn up more than one candidate, which
    /// is unresolvable by construction (no way to tell which one is authoritative) - treated the same as
    /// "no usable shadow", not silently narrowed to one guess.
    /// Sets session.VaultDbAutoRecovered on success, session.VaultDbUnrecoverable on failure. Call this
    /// only once the caller already knows vaultPath is unhealthy/unopenable - it does not check health itself.
    /// </summary>
    private async Task<bool> TryRecoverVaultFromShadowAsync(string vaultPath, VaultRegistry registry, int dbNumber, string dataDir)
    {
        string? shadowPath = null;
        bool ambiguousShadows = false;
        if (registry.ShadowFileKey is { Length: 36 } key)
        {
            var shadowName = ShadowFileService.KeyToFileName(key);
            if (shadowName != null)
            {
                var candidate = Path.Combine(dataDir, shadowName);
                if (File.Exists(candidate)) shadowPath = candidate;
            }
        }
        if (shadowPath == null)
        {
            var shadowCandidates = ShadowFileService.FindByMagic(dataDir, ShadowFileService.VAULT_SHADOW);
            if (shadowCandidates.Count == 1) shadowPath = shadowCandidates[0];
            else if (shadowCandidates.Count >= 2) ambiguousShadows = true;
        }

        if (!ambiguousShadows && shadowPath != null &&
            await ShadowFileService.IsShadowHealthyAsync(shadowPath, ShadowFileService.VAULT_SHADOW))
        {
            bool restored = await ShadowFileService.TryRestoreFromShadowAsync(
                shadowPath, vaultPath, ShadowFileService.VAULT_ORIGIN);
            if (restored)
            {
                logger.LogInformation("Auto-recovered Vault {N} from a shadow file.", dbNumber);
                session.VaultDbAutoRecovered       = true;
                session.VaultDbAutoRecoveredNumber = dbNumber;
                return true;
            }
            logger.LogError("Vault {N}: failed to recover from a shadow file.", dbNumber);
            session.VaultDbUnrecoverable       = true;
            session.VaultDbUnrecoverableNumber = dbNumber;
            return false;
        }

        logger.LogError(
            "Vault {N}: the vault DB file is missing or corrupted and no usable shadow was found ({Reason}). Path={Path}",
            dbNumber, ambiguousShadows ? "ambiguous shadows" : "no usable shadow", vaultPath);
        session.VaultDbUnrecoverable       = true;
        session.VaultDbUnrecoverableNumber = dbNumber;
        return false;
    }

    /// <summary>Computes the vault DB file path from FileHash[32B].</summary>
    public static string ComputeVaultDbPath(byte[] fileHash, string dataDir)
    {
        // Base64UrlNoPad("nkdb" || FileHash[32B]) = 48 chars
        var prefix = "nkdb"u8.ToArray();
        var raw    = new byte[4 + fileHash.Length];
        prefix.CopyTo(raw, 0);
        fileHash.CopyTo(raw, 4);
        var fileName = Base64UrlNoPad(raw);
        return Path.Combine(dataDir, fileName);
    }

    private static string Base64UrlNoPad(byte[] input)
        => Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    // ── Creating a new vault DB ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a new vault DB and registers it in VaultRegistries.
    /// Assumes K_shared is already present in the session.
    /// </summary>
    public Task<bool> CreateNewVaultAsync(
        int dbNumber, ReadOnlySpan<char> password, string dataDir)
    {
        if (!session.HasKShared)
        {
            logger.LogError("K_shared is missing. Creating a new vault requires K_shared.");
            return Task.FromResult(false);
        }
        if (dbNumber < 1 || dbNumber > 3)
            throw new ArgumentOutOfRangeException(nameof(dbNumber), "Vault number must be 1-3.");

        var pwBuf = new SecureCharBuffer();
        pwBuf.SetFromSpan(password);
        return CreateNewVaultAsyncWithDisposal(dbNumber, pwBuf, dataDir);
    }

    private async Task<bool> CreateNewVaultAsyncWithDisposal(int dbNumber, SecureCharBuffer pwBuf, string dataDir)
    {
        try { return await CreateNewVaultCoreAsync(dbNumber, pwBuf, dataDir); }
        finally { pwBuf.Dispose(); }
    }

    private async Task<bool> CreateNewVaultCoreAsync(
        int dbNumber, SecureCharBuffer pwBuf, string dataDir, CancellationToken ct = default)
    {
        string? vaultPath = null;

        // Captured synchronously, before the first await: the state this creation started from (locked
        // for first-time setup, unlocked for adding a vault from the settings screen).
        bool wasUnlocked = session.IsUnlocked;

        // a-b. Randomly generate FileHash and vault_DEK
        var fileHash  = GC.AllocateArray<byte>(32, pinned: true);
        var vaultDek  = GC.AllocateArray<byte>(32, pinned: true);
        var vaultKek  = GC.AllocateArray<byte>(32, pinned: true);
        var kMasterN  = GC.AllocateArray<byte>(32, pinned: true);
        // Local copy of K_shared, taken once after the derivations below. The session's own buffer is
        // never read again after that: an offloaded derivation can be overtaken by Lock(), which zeroes
        // it, and wrapping/encrypting with a zeroed K_shared would silently commit a corrupt slot.
        var kSharedLocal = GC.AllocateArray<byte>(32, pinned: true);
        try
        {
            RandomNumberGenerator.Fill(fileHash);
            RandomNumberGenerator.Fill(vaultDek);

            var vaultSaltPwd = crypto.GenerateSalt(32);
            var unifiedSaltN = crypto.GenerateSalt(32);

            // c. vault_KEK = Argon2id(pw, vault_salt_pwd)
            // k. K_master_N = Argon2id(pw, unified_salt_N)
            // Both derivations run first, in one background hop, before anything is created on disk or
            // committed to either DB: two Argon2id passes are far too heavy for the UI thread this method
            // resumes on, and finishing them up front means a Lock()/cancellation during the heavy part
            // aborts cleanly with nothing to roll back. The buffers the lambda touches are only zeroed
            // in the finally below, strictly after this await completes.
            await Task.Run(() =>
            {
                DeriveArgon2id(pwBuf.Span, vaultSaltPwd, vaultKek);
                DeriveArgon2id(pwBuf.Span, unifiedSaltN, kMasterN);
            }, ct);

            // Stale-result guard, before the first write: abort if the caller cancelled or the session
            // state changed (locked / unlocked elsewhere) while deriving, or K_shared is already gone.
            ThrowIfDerivationStale(wasUnlocked, ct);
            if (!session.HasKShared)
                throw new OperationCanceledException("K_shared is no longer available.");
            session.GetKShared().Span.CopyTo(kSharedLocal);

            // d. WrappedDek = AES-256-GCM(vault_DEK, vault_KEK) — Standard format
            byte[] wrappedDek = crypto.Encrypt(vaultDek, vaultKek);
            CryptographicOperations.ZeroMemory(vaultKek);

            // e. Atomically create the vault DB's SQLite file
            vaultPath = ComputeVaultDbPath(fileHash, dataDir);
            await initializer.InitializeVaultDbAsync(vaultPath);

            // g. Write vault DB VaultMetadata[Auth]
            connectionProvider.SetActiveVault(vaultPath);
            await using var vdb = await factory.CreateDbContextAsync();
            var authJson = JsonSerializer.Serialize(
                new AuthData(
                    Convert.ToBase64String(vaultSaltPwd),
                    Convert.ToBase64String(wrappedDek)),
                AuthServiceJsonContext.Default.AuthData);
            vdb.Metadata.Add(new VaultMetadata { ConfigKey = VaultMetadataKey.Auth,       ConfigValue = Encoding.UTF8.GetBytes(authJson) });
            // VaultIndex(0x1005): used to restore the original DbNumber during recovery. Encrypted with vault_DEK.
            var vaultIndexJson = JsonSerializer.SerializeToUtf8Bytes(
                new VaultIndexData(dbNumber), AuthServiceJsonContext.Default.VaultIndexData);
            vdb.Metadata.Add(new VaultMetadata
            {
                ConfigKey   = VaultMetadataKey.VaultIndex,
                ConfigValue = crypto.Encrypt(vaultIndexJson, vaultDek),
            });
            CryptographicOperations.ZeroMemory(vaultIndexJson.AsSpan());
            await vdb.SaveChangesAsync();

            // h. Prefix3Hash = HMAC-SHA256(K_shared, UTF8(pw[0:3]))
            int prefixLen = Math.Min(3, pwBuf.Span.Length);
            Span<byte> prefix3Utf8 = stackalloc byte[Encoding.UTF8.GetByteCount(pwBuf.Span[..prefixLen])];
            Encoding.UTF8.GetBytes(pwBuf.Span[..prefixLen], prefix3Utf8);
            var prefix3Hash = HMACSHA256.HashData(kSharedLocal, prefix3Utf8);

            // i. Payload_M = AES-256-GCM(K_shared, JSON{FileHash, Prefix3Hash, VaultSaltPwd, ...})
            var payloadJson = JsonSerializer.SerializeToUtf8Bytes(
                new VaultPayload(
                    Convert.ToBase64String(fileHash),
                    Convert.ToBase64String(prefix3Hash),
                    Convert.ToBase64String(vaultSaltPwd)),
                MultiVaultJsonContext.Default.VaultPayload);
            var encPayload = crypto.Encrypt(payloadJson, kSharedLocal);

            // j. INSERT into VaultRegistries
            await using var udb = await unifiedFactory.CreateDbContextAsync();
            udb.VaultRegistries.Add(new VaultRegistry
            {
                DbNumber         = dbNumber,
                LastAccessedAt   = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                EncryptedPayload = encPayload,
            });

            // k-n. INSERT the K_shared Key Wrap slot into the unified DB (K_master_N was derived up front)
            var wrappedKSh = crypto.Encrypt(kSharedLocal, kMasterN);
            var coarseN    = ComputeCoarseHash(pwBuf.Span);
            var slotBlob   = BuildKSharedSlot(coarseN, unifiedSaltN, wrappedKSh);
            udb.Metadata.Add(new UnifiedMetadata
            {
                ConfigKey   = UnifiedMetadataKey.KSharedSlotForVault(dbNumber),
                ConfigValue = slotBlob,
            });

            await udb.SaveChangesAsync();
            CryptographicOperations.ZeroMemory(kMasterN);

            // Set vault_DEK in the session - unless the session state changed while the vault was being
            // written (e.g. Lock() landed). The vault is fully created and registered by now, so this must
            // not throw (the catch below would delete the file while the registry row is already committed);
            // only the switch of the session to the new vault is skipped, never resurrecting a locked session.
            if (session.IsUnlocked == wasUnlocked)
            {
                session.SetKey(vaultDek);
                session.CurrentVaultDbNumber = dbNumber;
                session.DisplayedVaultNumber = dbNumber;
            }
            else
            {
                logger.LogWarning("The session state changed while Vault {N} was being created; the session was not switched to it.", dbNumber);
            }

            logger.LogInformation("Finished creating new Vault {N}.", dbNumber);
            return true;
        }
        catch
        {
            // Fail-safe: physically delete any partial file
            if (vaultPath != null) TryDeleteVaultFile(vaultPath);
            throw;
        }
        finally
        {
            // pwBuf is not disposed here - ownership stays with the caller (CreateNewVaultAsync /
            // SetupAsyncCore), each of which completes its own disposal in its own try-finally.
            CryptographicOperations.ZeroMemory(fileHash);
            CryptographicOperations.ZeroMemory(vaultDek);
            CryptographicOperations.ZeroMemory(vaultKek);
            CryptographicOperations.ZeroMemory(kMasterN);
            CryptographicOperations.ZeroMemory(kSharedLocal);
        }
    }

    private static byte[] BuildKSharedSlot(byte[] coarse, byte[] unifiedSalt, byte[] wrappedKShared)
    {
        var blob = new byte[SlotTotalSize];
        coarse.CopyTo(blob,       SlotCoarseHashOffset);
        unifiedSalt.CopyTo(blob,  SlotUnifiedSaltOffset);
        wrappedKShared.CopyTo(blob, SlotWrappedKShOffset);
        return blob;
    }

    // ── Orphan file GC ──────────────────────────────────────────────────

    /// <summary>
    /// Builds the set of registered FileHashes while K_shared is still valid (the DB access part).
    /// Called by EnterVaultCoreAsync before Task.Run, passing the set to the background task (avoiding races).
    /// </summary>
    private async Task<HashSet<string>> BuildRegisteredHashesAsync()
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        await using var udb = await unifiedFactory.CreateDbContextAsync();

        var registries = await udb.VaultRegistries.ToListAsync();
        foreach (var reg in registries)
        {
            var payload = DecryptVaultPayload(reg.EncryptedPayload);
            // A single undecryptable registry must abort the whole GC pass, not just be excluded
            // from the hash set - excluding it would make the disk scan treat that vault's real,
            // still-registered DB file as an orphan and delete it.
            if (payload == null)
                throw new InvalidOperationException(
                    $"Failed to decrypt VaultRegistries[DbNumber={reg.DbNumber}]'s EncryptedPayload during orphan file GC hash collection.");
            hashes.Add(payload.FileHash);
        }

        return hashes;
    }

    /// <summary>
    /// Pure disk scan only (no DB access). Operates on the result passed in from BuildRegisteredHashesAsync.
    /// Call from within Task.Run.
    /// </summary>
    private void RunOrphanGcDiskScanSync(string dataDir, HashSet<string> registeredHashes)
    {
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(dataDir))
            {
                try
                {
                    var fileName = Path.GetFileName(filePath);
                    if (fileName.StartsWith("NaimitsuVault.nkdb", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (fileName.Length != 48) continue;
                    if (!IsBase64Url(fileName)) continue;

                    byte[] raw;
                    try { raw = DecodeBase64UrlNoPad(fileName); }
                    catch { continue; }

                    if (raw.Length < 4) continue;
                    if (raw[0] != 'n' || raw[1] != 'k' || raw[2] != 'd' || raw[3] != 'b') continue;

                    var fileHash = Convert.ToBase64String(raw[4..]);
                    if (registeredHashes.Contains(fileHash)) continue;

                    SqliteConnection.ClearAllPools();
                    File.Delete(filePath);
                    logger.LogInformation("Deleted orphan residual file: {Name}", fileName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Orphan file GC: error while processing a file (skipped). [{ExType}]", ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("An error occurred during orphan file GC as a whole (ignored). [{ExType}]", ex.GetType().Name);
        }
    }

    // ── Getting available slots ─────────────────────────────────────────────────

    /// <summary>
    /// Returns vault numbers (1-3) not yet registered in the unified DB. Used to check for free slots before creating a new vault.
    /// </summary>
    public async Task<List<int>> GetAvailableVaultNumbersAsync()
    {
        await using var udb = await unifiedFactory.CreateDbContextAsync();
        var registered = await udb.VaultRegistries.Select(v => v.DbNumber).ToListAsync();
        return Enumerable.Range(1, 3).Where(n => !registered.Contains(n)).ToList();
    }

    /// <summary>
    /// Returns the vault numbers registered in the unified DB in ascending order.
    /// </summary>
    public async Task<List<int>> GetRegisteredVaultNumbersAsync()
    {
        await using var udb = await unifiedFactory.CreateDbContextAsync();
        return await udb.VaultRegistries
            .Select(v => v.DbNumber)
            .OrderBy(n => n)
            .ToListAsync();
    }

    private static bool IsBase64Url(string s)
    {
        foreach (char c in s)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }

    private static byte[] DecodeBase64UrlNoPad(string s)
    {
        string std = s.Replace('-', '+').Replace('_', '/');
        int pad = (4 - std.Length % 4) % 4;
        std += new string('=', pad);
        return Convert.FromBase64String(std);
    }

    private static void TryDeleteVaultFile(string path)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var f = path + suffix;
                if (File.Exists(f)) File.Delete(f);
            }
        }
        catch { /* Fail-safe: ignore deletion failures */ }
    }
}

// ── JSON serialization types ────────────────────────────────────────────────────

/// <summary>The decrypted JSON of VaultRegistries.EncryptedPayload.</summary>
internal record VaultPayload(
    [property: JsonPropertyName("fileHash")]    string FileHash,
    [property: JsonPropertyName("prefix3Hash")] string Prefix3Hash,
    [property: JsonPropertyName("vaultSaltPwd")]string VaultSaltPwd);

[JsonSerializable(typeof(VaultPayload))]
internal partial class MultiVaultJsonContext : JsonSerializerContext { }
