// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
//
// DEK wrapping implementation (Argon2id key derivation, AES-256-GCM encryption, WrappedDEK 60B format)

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public partial class AuthService
{
    // WrappedDEK format constants: nonce(12B) + EncryptedDEK(32B) + Tag(16B) = 60B
    private const int WDekNonceOffset  = 0;
    private const int WDekNonceSize    = 12;
    private const int WDekCipherOffset = 12;
    private const int WDekCipherSize   = 32; // DEK is always 32B
    private const int WDekTagOffset    = 44;
    private const int WDekTagSize      = 16;
    private const int WDekTotalSize    = 60; // 12 + 32 + 16

    // ── Unlock ─────────────────────────────────────────────────────────

    /// <summary>
    /// vault_salt_pwd is provided by the caller (avoids the DB reading itself).
    /// UnlockAsyncCore reads it from the DB and passes it in. EnterVaultCoreAsync passes it from VaultPayload.
    /// </summary>
    internal async Task<bool> UnlockCoreAsync(SecureCharBuffer pwdBuf, byte[] vaultSaltPwd, CancellationToken ct = default)
    {
        // Captured synchronously, before the first await, so it reflects the state the caller started from
        // (locked for a fresh unlock, unlocked for a re-authentication such as export/import).
        bool wasUnlocked = session.IsUnlocked;

        await using var db = await factory.CreateDbContextAsync();
        var setting = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == VaultMetadataKey.Auth);
        if (setting == null) return false;

        var authData = JsonSerializer.Deserialize(setting.ConfigValue.AsSpan(), AuthServiceJsonContext.Default.AuthData);
        if (authData == null) return false;

        var wDek    = Convert.FromBase64String(authData.WrappedDek);
        if (wDek.Length != WDekTotalSize)
        {
            logger.LogError("WrappedDEK byte length is invalid. (Expected={E}, Actual={A})", WDekTotalSize, wDek.Length);
            return false;
        }
        var kekPw   = GC.AllocateArray<byte>(32, pinned: true);
        var dek     = GC.AllocateArray<byte>(32, pinned: true);
        try
        {
            // Off the UI thread (see AcquireKSharedCoreAsync): pwdBuf/vaultSaltPwd/kekPw are only zeroed
            // in the finally below, strictly after this await completes.
            await Task.Run(() => crypto.DeriveKey(pwdBuf.Span, vaultSaltPwd, kekPw), ct);
            crypto.Decrypt(wDek, kekPw, dek);

            // Stale-result guard, immediately before the write: if Lock() ran (re-auth case) or another
            // unlock finished (fresh-unlock case) while deriving, do not put a DEK into the session.
            ThrowIfDerivationStale(wasUnlocked, ct);

            session.SetKey(dek.AsSpan());
            logger.LogInformation("Authentication succeeded.");
            return true;
        }
        catch (OperationCanceledException)
        {
            // Must precede the generic handler below, which would otherwise report a cancelled or
            // stale attempt as an authentication failure. The finally still wipes kekPw/dek.
            throw;
        }
        catch (CryptographicException)
        {
            logger.LogWarning("Incorrect password.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError("Unexpected error during authentication. [{ExType}]", ex.GetType().Name);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kekPw);
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a key with Argon2id and writes it to <paramref name="output"/>.
    /// Implemented directly without ICryptoService so it can be called from a static method.
    /// </summary>
    internal static void DeriveArgon2id(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, Span<byte> output)
    {
        // Allocate on the POH (pinned object heap): even if a GC compaction runs during the heavy
        // Argon2id computation, the address never moves, so the finally ZeroMemory always wipes
        // the correct address (same precaution as CryptoService.DeriveKey; the old implementation
        // used new byte[]/ToArray(), which was not pinned).
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(password);
        var pwBytes = GC.AllocateArray<byte>(byteCount, pinned: true);
        System.Text.Encoding.UTF8.GetBytes(password, pwBytes.AsSpan());
        try
        {
            var saltArr = GC.AllocateArray<byte>(salt.Length, pinned: true);
            salt.CopyTo(saltArr);
            try
            {
                using var argon2 = new Konscious.Security.Cryptography.Argon2id(pwBytes)
                {
                    Salt                = saltArr,
                    DegreeOfParallelism = Argon2Parameters.Parallelism,
                    MemorySize          = Argon2Parameters.MemorySize,
                    Iterations          = Argon2Parameters.Iterations,
                };
                var key = argon2.GetBytes(output.Length);
                try { key.AsSpan().CopyTo(output); }
                finally { CryptographicOperations.ZeroMemory(key.AsSpan()); }
            }
            finally { CryptographicOperations.ZeroMemory(saltArr.AsSpan()); }
        }
        finally { CryptographicOperations.ZeroMemory(pwBytes.AsSpan()); }
    }

    /// <summary>Updates the entry in the VaultMetadata table if the given key exists, otherwise adds it (TEXT type is forbidden; always BLOB).</summary>
    private static async Task UpsertSettingAsync(AppDbContext db, int key, byte[] value)
    {
        var existing = await db.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == key);
        if (existing == null)
            db.Metadata.Add(new VaultMetadata { ConfigKey = key, ConfigValue = value });
        else
            existing.ConfigValue = value;
    }

    /// <summary>UPSERT helper for the unified DB (takes byte[] = always BLOB).</summary>
    private static async Task UpsertUnifiedSettingAsync(UnifiedDbContext udb, int key, byte[] value)
    {
        var existing = await udb.Metadata.FirstOrDefaultAsync(s => s.ConfigKey == key);
        if (existing == null)
            udb.Metadata.Add(new UnifiedMetadata { ConfigKey = key, ConfigValue = value });
        else
            existing.ConfigValue = value;
    }
}
