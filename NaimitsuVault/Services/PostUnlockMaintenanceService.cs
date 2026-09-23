// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Bundles the housekeeping steps that must run once after every successful unlock (initial launch
/// and re-lock alike): confirm the vault DB's WAL migration, seed an empty profile if absent, purge
/// secrets soft-deleted more than <see cref="Common.AppConstants.DeletedSecretRetentionDays"/> days
/// ago, and repair StoredFiles rows whose FileModifiedAt was never set. Extracted out of App.xaml.cs
/// (which is not unit-testable) specifically so this sequence can be exercised directly in tests -
/// the 30-day purge silently lost its only production call site for 2.5 months during an unrelated
/// refactor without any test catching it, because the only existing coverage called the purge method
/// directly rather than through the real call path.
/// </summary>
public class PostUnlockMaintenanceService(
    DatabaseInitializer initializer,
    ProfileService profileService,
    StoredFileRepository storedFiles,
    IAuditLogService auditLog,
    ILogger<PostUnlockMaintenanceService> logger)
{
    public async Task RunAsync(DekScope dek)
    {
        await initializer.EnsureVaultDbMigratedAsync();
        await profileService.SeedEmptyProfileIfAbsentAsync(dek);
        var purgedCount = await initializer.CleanupDeletedSecretsAsync();
        if (purgedCount > 0)
        {
            try
            {
                await auditLog.LogAsync(AuditEventCode.SecretAutoPurgedByExpiry, new SecretAutoPurgedByExpiryPayload(purgedCount), dek);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[PostUnlockMaintenance] Failed to log SecretAutoPurgedByExpiry. [{ExType}]", ex.GetType().Name);
            }
        }
        var purgedFiles = await initializer.CleanupDeletedFilesAsync();
        if (purgedFiles > 0)
        {
            try
            {
                await auditLog.LogAsync(AuditEventCode.FileAutoPurgedByExpiry, new FileAutoPurgedByExpiryPayload(purgedFiles), dek);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[PostUnlockMaintenance] Failed to log FileAutoPurgedByExpiry. [{ExType}]", ex.GetType().Name);
            }
        }
        await storedFiles.RepairMissingFileModifiedAtAsync();
    }
}
