// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// DB pre-seeding helper for TC-WH-01 and WindowsHelloDekMemoryTests.
/// Assumes FakeProtectedDataService (identity transform) and INSERTs a known DEK into VaultMetadata.Hello.
///
/// Precondition: FakeProtectedDataService must already be injected into AuthService.
///       Protect(x) = x, Unprotect(x) = x (returns the input as-is)
///
/// After seeding, the DB contains:
///   ConfigKey = VaultMetadataKey.VaultDEKHello
///   ConfigValue = UTF-8(Convert.ToBase64String(dek))
/// UnlockWithWindowsHelloAsync reads this back, Base64-decodes it, Unprotect()s it (identity), and
/// succeeds via SetKey.
///
/// Security note: test code only. Not used in production DI.
/// </summary>
internal static class WinHelloDbSeeder
{
    /// <summary>
    /// Inserts a valid WrappedDEK into the DB and returns the DEK used.
    /// The caller must call CryptographicOperations.ZeroMemory(dek) after verification.
    /// </summary>
    internal static async Task<byte[]> SeedWrappedDekAsync(AppDbContext db)
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);

        // Assumes identity transform: Protect(dek) = dek, so encode directly to Base64
        var encoded = Convert.ToBase64String(dek);
        db.Metadata.Add(new VaultMetadata
        {
            ConfigKey   = VaultMetadataKey.VaultDEKHello,
            ConfigValue = Encoding.UTF8.GetBytes(encoded),
        });
        await db.SaveChangesAsync();

        return dek; // the caller must ZeroMemory this
    }

    /// <summary>
    /// Seeds the unified DB with KSharedHello(0x000A) for Windows Hello and VaultRegistry[1].
    /// Assumes FakeProtectedDataService (identity transform) and IdentityCryptoService (skips the
    /// leading 28 bytes).
    ///
    /// After seeding, the unified DB contains:
    ///   ConfigKey = UnifiedMetadataKey.KSharedHello(0x000A)
    ///   ConfigValue = UTF-8(Convert.ToBase64String(kShared))
    /// and VaultRegistries[DbNumber=1].
    /// </summary>
    /// <returns>The seeded K_shared byte array (the caller must ZeroMemory it).</returns>
    internal static async Task<byte[]> SeedUnifiedDbForHelloAsync(UnifiedDbContext udb)
    {
        var kShared = new byte[32];
        RandomNumberGenerator.Fill(kShared);

        // KSharedHello: assumes identity transform -> encode directly to Base64
        var encoded = Convert.ToBase64String(kShared);
        udb.Metadata.Add(new UnifiedMetadata
        {
            ConfigKey   = UnifiedMetadataKey.KSharedHello,
            ConfigValue = Encoding.UTF8.GetBytes(encoded),
        });

        // VaultRegistry[1]: EncryptedPayload = [HeaderSize zeros] + UTF-8 JSON({VaultPayload})
        // IdentityCryptoService.Decrypt skips the leading HeaderSize bytes and copies the rest, so
        // placing dummy zeros at the front followed by the JSON makes DecryptVaultPayload succeed.
        // EnterVaultWithHelloAsync does not check File.Exists, so fileHash can be all zero bytes.
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(
            new VaultPayload(
                Convert.ToBase64String(new byte[32]),
                Convert.ToBase64String(new byte[32]),
                Convert.ToBase64String(new byte[32])),
            MultiVaultJsonContext.Default.VaultPayload);
        var encPayload = new byte[IdentityCryptoService.HeaderSize + payloadJson.Length];
        payloadJson.CopyTo(encPayload, IdentityCryptoService.HeaderSize);

        udb.VaultRegistries.Add(new VaultRegistry
        {
            DbNumber         = 1,
            EncryptedPayload = encPayload,
        });

        await udb.SaveChangesAsync();
        return kShared; // the caller must ZeroMemory this
    }

    /// <summary>
    /// Adds one more VaultRegistry row (beyond the DbNumber=1 row from SeedUnifiedDbForHelloAsync) with
    /// a distinct FileHash, so it resolves to a different vault DB path. Used to test that the per-vault
    /// Hello scan isolates a fault in one vault from the others (see TC-WH-21).
    /// </summary>
    internal static async Task SeedAdditionalVaultRegistryAsync(UnifiedDbContext udb, int dbNumber, byte fileHashFill)
    {
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(
            new VaultPayload(
                Convert.ToBase64String(Enumerable.Repeat(fileHashFill, 32).ToArray()),
                Convert.ToBase64String(new byte[32]),
                Convert.ToBase64String(new byte[32])),
            MultiVaultJsonContext.Default.VaultPayload);
        var encPayload = new byte[IdentityCryptoService.HeaderSize + payloadJson.Length];
        payloadJson.CopyTo(encPayload, IdentityCryptoService.HeaderSize);

        udb.VaultRegistries.Add(new VaultRegistry
        {
            DbNumber         = dbNumber,
            EncryptedPayload = encPayload,
        });
        await udb.SaveChangesAsync();
    }
}
