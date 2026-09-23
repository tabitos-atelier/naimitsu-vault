// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using NaimitsuVault.Common;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Services;

public class AuditLogService(
    AuditLogRepository repo,
    ICryptoService crypto,
    ISecurityContext session,
    SecretRepository secretsRepo,
    StoredFileRepository filesRepo) : IAuditLogService
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(AppConstants.AuditLogRetentionDays);

    // ── Physical layer ───────────────────────────────────────────────────────

    public async Task LogAsync(
        AuditEventCode code,
        AuditPayload? payload,
        DekScope dek,
        CancellationToken ct = default)
    {
        // DekScope is consumed synchronously (encryption completes before the await)
        var blob = SerializePayload(code, payload, dek);

        var entity = new AuditLog
        {
            CreatedAt  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            EventCode  = (int)code,
            EventLevel = GetEventLevel(code),
            Payload    = blob,
        };
        await repo.InsertAsync(entity, ct);
        WeakReferenceMessenger.Default.Send(new AuditLogWrittenMessage(code, entity.EventLevel));
    }

    public async Task LogAuthFailedAsync(CancellationToken ct = default)
    {
        await repo.InsertAuthFailedRawAsync(ct);
        WeakReferenceMessenger.Default.Send(new AuditLogWrittenMessage(AuditEventCode.AuthFailed, 1));
    }

    /// <summary>
    /// Writes the failure count accumulated in memory before unlock back to the vault DB.
    /// Call this right after EnterVaultCoreAsync completes vault entry (when session.FailedLoginAttempts > 0).
    /// </summary>
    public async Task FlushPreAuthFailuresAsync(int count, CancellationToken ct = default)
    {
        var jsonBytes = Encoding.UTF8.GetBytes("{\"pre_auth\":true}");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < count; i++)
        {
            // The Span from GetKey() is consumed synchronously without crossing an await.
            var dekScope = session.GetKey();
            var encPayload = crypto.Encrypt(jsonBytes, dekScope.Span);
            await repo.InsertAsync(new AuditLog
            {
                EventCode  = (int)AuditEventCode.AuthFailed,
                EventLevel = 1,
                Payload    = encPayload,
                CreatedAt  = ts - (count - 1 - i),
            }, ct);
        }
        if (count > 0)
            WeakReferenceMessenger.Default.Send(new AuditLogWrittenMessage(AuditEventCode.AuthFailed, 1));
    }

    public async Task FlushPendingRestoreAuditAsync(
        DekScope dek, int currentDbNumber, string dataDir, CancellationToken ct = default)
    {
        var pending = RestoreAuditMarker.TryRead(dataDir);
        if (pending is not { } entry || !entry.DbNumbers.Contains(currentDbNumber))
            return;

        // DekScope is consumed synchronously (completed before the await). Payload is null by design (see AuditPayload.cs).
        var blob = SerializePayload(entry.Code, null, dek);
        await repo.InsertAsync(new AuditLog
        {
            CreatedAt  = entry.TimestampUnix,
            EventCode  = (int)entry.Code,
            EventLevel = GetEventLevel(entry.Code),
            Payload    = blob,
        }, ct);
        WeakReferenceMessenger.Default.Send(new AuditLogWrittenMessage(entry.Code, GetEventLevel(entry.Code)));

        RestoreAuditMarker.RemoveAndRewrite(dataDir, entry, currentDbNumber);
    }

    // ── Read layer ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<AuditLogDisplayItem>> GetRecentAsync(
        int count,
        DekScope dek,
        CancellationToken ct = default)
    {
        var rows = await repo.GetRecentAsync(count, ct);
        return await BuildDisplayItemsAsync(rows, dek, ct);
    }

    public async Task<IReadOnlyList<AuditLogDisplayItem>> GetAllAfterAsync(
        DateTimeOffset since,
        DekScope dek,
        CancellationToken ct = default)
    {
        var rows = await repo.GetAllAfterAsync(since, ct);
        return await BuildDisplayItemsAsync(rows, dek, ct);
    }

    public async Task PurgeOldLogsAsync(DekScope? purgeLogDek = null, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionPeriod;
        var deleted = await repo.DeleteBeforeAsync(cutoff, ct);
        if (deleted > 0 && purgeLogDek.HasValue)
        {
            await LogAsync(
                AuditEventCode.AuditLogsPurged,
                new AuditLogsPurgedPayload(deleted),
                purgeLogDek.Value,
                ct);
        }
    }

    public async Task<bool> HasAnyLogsAsync(CancellationToken ct = default)
        => await repo.CountAsync(ct) > 0;

    // ── private helpers ────────────────────────────────────────────────────

    private byte[]? SerializePayload(AuditEventCode code, AuditPayload? payload, DekScope dek)
    {
        if (payload is null) return null;

        var ctx = AuditPayloadJsonContext.Default;
        return code switch
        {
            // Viewing (0x1000)
            AuditEventCode.SecretViewed =>
                FieldCrypto.SealJson((SecretViewedPayload)payload, ctx.SecretViewedPayload, crypto, dek),
            AuditEventCode.TimeMachineViewed =>
                FieldCrypto.SealJson((TimeMachineViewedPayload)payload, ctx.TimeMachineViewedPayload, crypto, dek),
            AuditEventCode.FileViewed =>
                FieldCrypto.SealJson((FileViewedPayload)payload, ctx.FileViewedPayload, crypto, dek),

            // Configuration/settings changes (0x3000)
            AuditEventCode.AutoBackupSettingChanged =>
                FieldCrypto.SealJson((AutoBackupSettingChangedPayload)payload, ctx.AutoBackupSettingChangedPayload, crypto, dek),
            AuditEventCode.AutoLockSettingChanged =>
                FieldCrypto.SealJson((AutoLockSettingChangedPayload)payload, ctx.AutoLockSettingChangedPayload, crypto, dek),
            AuditEventCode.ScreenCaptureProtectionChanged =>
                FieldCrypto.SealJson((ScreenCaptureProtectionChangedPayload)payload, ctx.ScreenCaptureProtectionChangedPayload, crypto, dek),
            AuditEventCode.WindowsHelloChanged =>
                FieldCrypto.SealJson((WindowsHelloChangedPayload)payload, ctx.WindowsHelloChangedPayload, crypto, dek),
            AuditEventCode.FaviconAutoFetchChanged =>
                FieldCrypto.SealJson((FaviconAutoFetchChangedPayload)payload, ctx.FaviconAutoFetchChangedPayload, crypto, dek),

            // Save (0x5000)
            AuditEventCode.SecretSaved =>
                FieldCrypto.SealJson((SecretSavedPayload)payload, ctx.SecretSavedPayload, crypto, dek),
            AuditEventCode.TimeMachineRestored =>
                FieldCrypto.SealJson((TimeMachineRestoredPayload)payload, ctx.TimeMachineRestoredPayload, crypto, dek),
            AuditEventCode.FileAdded =>
                FieldCrypto.SealJson((FileAddedPayload)payload, ctx.FileAddedPayload, crypto, dek),

            // Delete/purge (0x6000)
            AuditEventCode.SecretSoftDeleted =>
                FieldCrypto.SealJson((SecretSoftDeletedPayload)payload, ctx.SecretSoftDeletedPayload, crypto, dek),
            AuditEventCode.SecretUndeleted =>
                FieldCrypto.SealJson((SecretUndeletedPayload)payload, ctx.SecretUndeletedPayload, crypto, dek),
            AuditEventCode.SecretPermanentlyDeleted =>
                FieldCrypto.SealJson((SecretPermanentlyDeletedPayload)payload, ctx.SecretPermanentlyDeletedPayload, crypto, dek),
            AuditEventCode.SecretAutoPurgedByExpiry =>
                FieldCrypto.SealJson((SecretAutoPurgedByExpiryPayload)payload, ctx.SecretAutoPurgedByExpiryPayload, crypto, dek),
            AuditEventCode.TimeMachineSlotDeleted =>
                FieldCrypto.SealJson((TimeMachineSlotDeletedPayload)payload, ctx.TimeMachineSlotDeletedPayload, crypto, dek),
            AuditEventCode.TimeMachineGenRotated =>
                FieldCrypto.SealJson((TimeMachineGenRotatedPayload)payload, ctx.TimeMachineGenRotatedPayload, crypto, dek),
            AuditEventCode.FilePermanentlyDeleted =>
                FieldCrypto.SealJson((FilePermanentlyDeletedPayload)payload, ctx.FilePermanentlyDeletedPayload, crypto, dek),
            AuditEventCode.FileSoftDeleted =>
                FieldCrypto.SealJson((FileSoftDeletedPayload)payload, ctx.FileSoftDeletedPayload, crypto, dek),
            AuditEventCode.FileUndeleted =>
                FieldCrypto.SealJson((FileUndeletedPayload)payload, ctx.FileUndeletedPayload, crypto, dek),
            AuditEventCode.FileAutoPurgedByExpiry =>
                FieldCrypto.SealJson((FileAutoPurgedByExpiryPayload)payload, ctx.FileAutoPurgedByExpiryPayload, crypto, dek),

            // Critical operations (0x7000)
            AuditEventCode.PlaintextImportExecuted =>
                FieldCrypto.SealJson((PlaintextImportExecutedPayload)payload, ctx.PlaintextImportExecutedPayload, crypto, dek),
            AuditEventCode.PlaintextExportExecuted =>
                FieldCrypto.SealJson((PlaintextExportExecutedPayload)payload, ctx.PlaintextExportExecutedPayload, crypto, dek),
            AuditEventCode.ImportItemFailed =>
                FieldCrypto.SealJson((ImportItemFailedPayload)payload, ctx.ImportItemFailedPayload, crypto, dek),
            AuditEventCode.ExportItemFailed =>
                FieldCrypto.SealJson((ExportItemFailedPayload)payload, ctx.ExportItemFailedPayload, crypto, dek),
            AuditEventCode.CsvFormulaGuardApplied =>
                FieldCrypto.SealJson((CsvFormulaGuardAppliedPayload)payload, ctx.CsvFormulaGuardAppliedPayload, crypto, dek),
            AuditEventCode.SecretFieldCopiedToClipboard =>
                FieldCrypto.SealJson((SecretFieldCopiedToClipboardPayload)payload, ctx.SecretFieldCopiedToClipboardPayload, crypto, dek),
            AuditEventCode.SecretAutoTypeExecuted =>
                FieldCrypto.SealJson((SecretAutoTypeExecutedPayload)payload, ctx.SecretAutoTypeExecutedPayload, crypto, dek),
            AuditEventCode.TimeMachineValueCopiedToClipboard =>
                FieldCrypto.SealJson((TimeMachineValueCopiedToClipboardPayload)payload, ctx.TimeMachineValueCopiedToClipboardPayload, crypto, dek),
            AuditEventCode.ProfileFieldCopiedToClipboard =>
                FieldCrypto.SealJson((ProfileFieldCopiedToClipboardPayload)payload, ctx.ProfileFieldCopiedToClipboardPayload, crypto, dek),
            AuditEventCode.LocaleImportSucceeded =>
                FieldCrypto.SealJson((LocaleImportSucceededPayload)payload, ctx.LocaleImportSucceededPayload, crypto, dek),
            AuditEventCode.LocaleImportKeyMissing =>
                FieldCrypto.SealJson((LocaleImportKeyMissingPayload)payload, ctx.LocaleImportKeyMissingPayload, crypto, dek),
            AuditEventCode.LocaleImportValueTooLong =>
                FieldCrypto.SealJson((LocaleImportValueTooLongPayload)payload, ctx.LocaleImportValueTooLongPayload, crypto, dek),
            AuditEventCode.LocaleImportPlaceholderBroken =>
                FieldCrypto.SealJson((LocaleImportPlaceholderBrokenPayload)payload, ctx.LocaleImportPlaceholderBrokenPayload, crypto, dek),
            AuditEventCode.FileExported =>
                FieldCrypto.SealJson((FileExportedPayload)payload, ctx.FileExportedPayload, crypto, dek),

            // System (0xF000)
            AuditEventCode.NoOpDraftDiscarded =>
                FieldCrypto.SealJson((NoOpDraftDiscardedPayload)payload, ctx.NoOpDraftDiscardedPayload, crypto, dek),
            AuditEventCode.FileContentTypeRepaired =>
                FieldCrypto.SealJson((FileContentTypeRepairedPayload)payload, ctx.FileContentTypeRepairedPayload, crypto, dek),
            AuditEventCode.AuditLogsPurged =>
                FieldCrypto.SealJson((AuditLogsPurgedPayload)payload, ctx.AuditLogsPurgedPayload, crypto, dek),
            AuditEventCode.VaultDbAutoRecovered =>
                FieldCrypto.SealJson((VaultDbAutoRecoveredPayload)payload, ctx.VaultDbAutoRecoveredPayload, crypto, dek),

            // Codes with no payload, or undefined codes
            _ => null,
        };
    }

    private async Task<IReadOnlyList<AuditLogDisplayItem>> BuildDisplayItemsAsync(
        IEnumerable<AuditLog> rows, DekScope dek, CancellationToken ct)
    {
        // Pass 1: fully deserialize payloads + collect TargetId
        var decoded       = new List<(AuditLog Row, AuditPayload? Payload)>();
        var secretIds     = new HashSet<int>();
        var fileIds       = new HashSet<int>();

        foreach (var row in rows)
        {
            var code    = (AuditEventCode)row.EventCode;
            var payload = row.Payload == null ? null : DeserializePayload(code, row.Payload, dek);
            decoded.Add((row, payload));
            // A live lookup is only needed as a fallback for rows logged before the Name snapshot
            // field existed - skip collecting an ID that already has an embedded name.
            if (TryGetEmbeddedName(payload) != null) continue;
            if (payload != null && TryGetTargetId(payload, out var id))
            {
                if (IsSecretEvent(code)) secretIds.Add(id);
                else if (IsFileEvent(code)) fileIds.Add(id);
            }
        }

        // Pass 2: batch name resolution
        var secretNames   = await ResolveSecretNamesAsync(secretIds, dek, ct);
        var fileNames     = await ResolveFileNamesAsync(fileIds, dek);

        // Pass 3: build display items
        var result = new List<AuditLogDisplayItem>(decoded.Count);
        foreach (var (row, payload) in decoded)
        {
            var code = (AuditEventCode)row.EventCode;
            string? resolvedName = TryGetEmbeddedName(payload);
            if (resolvedName == null && payload != null && TryGetTargetId(payload, out var id))
            {
                if (IsSecretEvent(code)) secretNames.TryGetValue(id, out resolvedName);
                else if (IsFileEvent(code)) fileNames.TryGetValue(id, out resolvedName);
            }
            var summary = BuildSummary(code, payload, resolvedName);
            var created = DateTimeOffset.FromUnixTimeSeconds(row.CreatedAt);
            result.Add(new AuditLogDisplayItem(row.Id, created, code, row.EventLevel, summary));
        }
        return result;
    }

    // TimeMachineRestored/TimeMachineSlotDeleted/TimeMachineGenRotated are deliberately excluded:
    // their payloads carry no Name field and BuildSummary renders them as fixed generic text that
    // never reads resolvedName, so including them here would only cost an unused DB lookup + decrypt.
    private static bool IsSecretEvent(AuditEventCode code) => code is
        AuditEventCode.SecretViewed or AuditEventCode.TimeMachineViewed or
        AuditEventCode.SecretSaved or AuditEventCode.SecretSoftDeleted or
        AuditEventCode.SecretUndeleted or AuditEventCode.SecretPermanentlyDeleted or
        AuditEventCode.ExportItemFailed or AuditEventCode.CsvFormulaGuardApplied or
        AuditEventCode.SecretFieldCopiedToClipboard or AuditEventCode.SecretAutoTypeExecuted or
        AuditEventCode.TimeMachineValueCopiedToClipboard;

    private static bool IsFileEvent(AuditEventCode code) => code is
        AuditEventCode.FileViewed or AuditEventCode.FileAdded or
        AuditEventCode.FilePermanentlyDeleted or AuditEventCode.FileSoftDeleted or AuditEventCode.FileUndeleted or
        AuditEventCode.FileExported or AuditEventCode.FileContentTypeRepaired;

    // Name snapshot sealed inside the payload at log time (see AuditPayload.cs remarks). Only the
    // Secret/File payload types that BuildSummary formats with a resolvedName carry this field.
    // Returns null for payload types without a Name property, or for rows logged before this field
    // existed - both cases fall back to the live-lookup path in BuildDisplayItemsAsync.
    private static string? TryGetEmbeddedName(AuditPayload? payload) => payload switch
    {
        SecretViewedPayload p             => p.Name,
        TimeMachineViewedPayload p        => p.Name,
        FileViewedPayload p               => p.Name,
        SecretSavedPayload p              => p.Name,
        SecretSoftDeletedPayload p        => p.Name,
        SecretUndeletedPayload p          => p.Name,
        SecretPermanentlyDeletedPayload p => p.Name,
        FileAddedPayload p                => p.Name,
        FilePermanentlyDeletedPayload p   => p.Name,
        FileSoftDeletedPayload p          => p.Name,
        FileUndeletedPayload p            => p.Name,
        FileExportedPayload p             => p.Name,
        FileContentTypeRepairedPayload p  => p.Name,
        ExportItemFailedPayload p         => p.Name,
        CsvFormulaGuardAppliedPayload p   => p.Name,
        SecretFieldCopiedToClipboardPayload p     => p.Name,
        SecretAutoTypeExecutedPayload p           => p.Name,
        TimeMachineValueCopiedToClipboardPayload p => p.Name,
        _                                 => null,
    };

    private static bool TryGetTargetId(AuditPayload payload, out int id)
    {
        id = payload switch
        {
            SecretViewedPayload p             => p.TargetId,
            TimeMachineViewedPayload p        => p.TargetId,
            SecretSavedPayload p              => p.TargetId,
            SecretSoftDeletedPayload p        => p.TargetId,
            SecretUndeletedPayload p          => p.TargetId,
            SecretPermanentlyDeletedPayload p => p.TargetId,
            TimeMachineRestoredPayload p      => p.TargetId,
            TimeMachineSlotDeletedPayload p   => p.TargetId,
            TimeMachineGenRotatedPayload p    => p.TargetId,
            FileViewedPayload p               => p.TargetId,
            FileAddedPayload p                => p.TargetId,
            FilePermanentlyDeletedPayload p   => p.TargetId,
            FileSoftDeletedPayload p          => p.TargetId,
            FileUndeletedPayload p            => p.TargetId,
            FileExportedPayload p             => p.TargetId,
            FileContentTypeRepairedPayload p  => p.TargetId,
            ExportItemFailedPayload p         => p.TargetId,
            CsvFormulaGuardAppliedPayload p   => p.TargetId,
            SecretFieldCopiedToClipboardPayload p     => p.TargetId,
            SecretAutoTypeExecutedPayload p           => p.TargetId,
            TimeMachineValueCopiedToClipboardPayload p => p.TargetId,
            _                                 => 0,
        };
        return id != 0;
    }

    private async Task<Dictionary<int, string>> ResolveSecretNamesAsync(
        HashSet<int> ids, DekScope dek, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var blobs  = await secretsRepo.GetTitleBlobsByIdsAsync(ids, ct);
        var result = new Dictionary<int, string>(blobs.Count);
        foreach (var (id, blob) in blobs)
        {
            try
            {
                using var pt = FieldCrypto.Open(blob, crypto, dek);
                if (pt != null) result[id] = Encoding.UTF8.GetString(pt.Utf8);
            }
            catch { /* No entry on decryption failure -> BuildSummary falls back to AuditLog.DeletedItemPlaceholder */ }
        }
        return result;
    }

    private async Task<Dictionary<int, string>> ResolveFileNamesAsync(HashSet<int> ids, DekScope dek)
    {
        if (ids.Count == 0) return [];
        var files = await filesRepo.GetByIdsAsync(ids, dek);
        return files.ToDictionary(
            f => f.Id,
            f => f.FileName.Length > 0 ? Encoding.UTF8.GetString(f.FileName) : string.Empty);
    }

    private AuditPayload? DeserializePayload(AuditEventCode code, byte[] blob, DekScope dek)
    {
        var ctx = AuditPayloadJsonContext.Default;
        try
        {
            return code switch
            {
                // Viewing (0x1000)
                AuditEventCode.SecretViewed =>
                    FieldCrypto.OpenJson(blob, ctx.SecretViewedPayload, crypto, dek),
                AuditEventCode.TimeMachineViewed =>
                    FieldCrypto.OpenJson(blob, ctx.TimeMachineViewedPayload, crypto, dek),
                AuditEventCode.FileViewed =>
                    FieldCrypto.OpenJson(blob, ctx.FileViewedPayload, crypto, dek),

                // Configuration/settings changes (0x3000)
                AuditEventCode.AutoBackupSettingChanged =>
                    FieldCrypto.OpenJson(blob, ctx.AutoBackupSettingChangedPayload, crypto, dek),
                AuditEventCode.AutoLockSettingChanged =>
                    FieldCrypto.OpenJson(blob, ctx.AutoLockSettingChangedPayload, crypto, dek),
                AuditEventCode.ScreenCaptureProtectionChanged =>
                    FieldCrypto.OpenJson(blob, ctx.ScreenCaptureProtectionChangedPayload, crypto, dek),
                AuditEventCode.WindowsHelloChanged =>
                    FieldCrypto.OpenJson(blob, ctx.WindowsHelloChangedPayload, crypto, dek),
                AuditEventCode.FaviconAutoFetchChanged =>
                    FieldCrypto.OpenJson(blob, ctx.FaviconAutoFetchChangedPayload, crypto, dek),

                // Save (0x5000)
                AuditEventCode.SecretSaved =>
                    FieldCrypto.OpenJson(blob, ctx.SecretSavedPayload, crypto, dek),
                AuditEventCode.TimeMachineRestored =>
                    FieldCrypto.OpenJson(blob, ctx.TimeMachineRestoredPayload, crypto, dek),
                AuditEventCode.FileAdded =>
                    FieldCrypto.OpenJson(blob, ctx.FileAddedPayload, crypto, dek),

                // Delete/purge (0x6000)
                AuditEventCode.SecretSoftDeleted =>
                    FieldCrypto.OpenJson(blob, ctx.SecretSoftDeletedPayload, crypto, dek),
                AuditEventCode.SecretUndeleted =>
                    FieldCrypto.OpenJson(blob, ctx.SecretUndeletedPayload, crypto, dek),
                AuditEventCode.SecretPermanentlyDeleted =>
                    FieldCrypto.OpenJson(blob, ctx.SecretPermanentlyDeletedPayload, crypto, dek),
                AuditEventCode.SecretAutoPurgedByExpiry =>
                    FieldCrypto.OpenJson(blob, ctx.SecretAutoPurgedByExpiryPayload, crypto, dek),
                AuditEventCode.TimeMachineSlotDeleted =>
                    FieldCrypto.OpenJson(blob, ctx.TimeMachineSlotDeletedPayload, crypto, dek),
                AuditEventCode.TimeMachineGenRotated =>
                    FieldCrypto.OpenJson(blob, ctx.TimeMachineGenRotatedPayload, crypto, dek),
                AuditEventCode.FilePermanentlyDeleted =>
                    FieldCrypto.OpenJson(blob, ctx.FilePermanentlyDeletedPayload, crypto, dek),
                AuditEventCode.FileSoftDeleted =>
                    FieldCrypto.OpenJson(blob, ctx.FileSoftDeletedPayload, crypto, dek),
                AuditEventCode.FileUndeleted =>
                    FieldCrypto.OpenJson(blob, ctx.FileUndeletedPayload, crypto, dek),
                AuditEventCode.FileAutoPurgedByExpiry =>
                    FieldCrypto.OpenJson(blob, ctx.FileAutoPurgedByExpiryPayload, crypto, dek),

                // Critical operations (0x7000)
                AuditEventCode.PlaintextImportExecuted =>
                    FieldCrypto.OpenJson(blob, ctx.PlaintextImportExecutedPayload, crypto, dek),
                AuditEventCode.PlaintextExportExecuted =>
                    FieldCrypto.OpenJson(blob, ctx.PlaintextExportExecutedPayload, crypto, dek),
                AuditEventCode.ImportItemFailed =>
                    FieldCrypto.OpenJson(blob, ctx.ImportItemFailedPayload, crypto, dek),
                AuditEventCode.ExportItemFailed =>
                    FieldCrypto.OpenJson(blob, ctx.ExportItemFailedPayload, crypto, dek),
                AuditEventCode.CsvFormulaGuardApplied =>
                    FieldCrypto.OpenJson(blob, ctx.CsvFormulaGuardAppliedPayload, crypto, dek),
                AuditEventCode.SecretFieldCopiedToClipboard =>
                    FieldCrypto.OpenJson(blob, ctx.SecretFieldCopiedToClipboardPayload, crypto, dek),
                AuditEventCode.SecretAutoTypeExecuted =>
                    FieldCrypto.OpenJson(blob, ctx.SecretAutoTypeExecutedPayload, crypto, dek),
                AuditEventCode.TimeMachineValueCopiedToClipboard =>
                    FieldCrypto.OpenJson(blob, ctx.TimeMachineValueCopiedToClipboardPayload, crypto, dek),
                AuditEventCode.ProfileFieldCopiedToClipboard =>
                    FieldCrypto.OpenJson(blob, ctx.ProfileFieldCopiedToClipboardPayload, crypto, dek),
                AuditEventCode.LocaleImportSucceeded =>
                    FieldCrypto.OpenJson(blob, ctx.LocaleImportSucceededPayload, crypto, dek),
                AuditEventCode.LocaleImportKeyMissing =>
                    FieldCrypto.OpenJson(blob, ctx.LocaleImportKeyMissingPayload, crypto, dek),
                AuditEventCode.LocaleImportValueTooLong =>
                    FieldCrypto.OpenJson(blob, ctx.LocaleImportValueTooLongPayload, crypto, dek),
                AuditEventCode.LocaleImportPlaceholderBroken =>
                    FieldCrypto.OpenJson(blob, ctx.LocaleImportPlaceholderBrokenPayload, crypto, dek),
                AuditEventCode.FileExported =>
                    FieldCrypto.OpenJson(blob, ctx.FileExportedPayload, crypto, dek),

                // System (0xF000)
                AuditEventCode.NoOpDraftDiscarded =>
                    FieldCrypto.OpenJson(blob, ctx.NoOpDraftDiscardedPayload, crypto, dek),
                AuditEventCode.FileContentTypeRepaired =>
                    FieldCrypto.OpenJson(blob, ctx.FileContentTypeRepairedPayload, crypto, dek),
                AuditEventCode.AuditLogsPurged =>
                    FieldCrypto.OpenJson(blob, ctx.AuditLogsPurgedPayload, crypto, dek),
                AuditEventCode.VaultDbAutoRecovered =>
                    FieldCrypto.OpenJson(blob, ctx.VaultDbAutoRecoveredPayload, crypto, dek),

                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string BuildSummary(AuditEventCode code, AuditPayload? payload, string? resolvedName = null)
    {
        static string L(string key) => LocalizationManager.Get(key);

        return code switch
        {
            // Viewing (0x1000)
            AuditEventCode.SecretViewed when payload is SecretViewedPayload =>
                string.Format(L("AuditLog.ReadSecret"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.TimeMachineViewed when payload is TimeMachineViewedPayload =>
                string.Format(L("AuditLog.ReadSecretHistory"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.FileViewed when payload is FileViewedPayload =>
                string.Format(L("AuditLog.ReadSecretAttachment"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.ProfileViewed =>
                L("AuditLog.ReadProfile"),

            // Configuration/settings changes (0x3000)
            AuditEventCode.MasterPasswordChanged =>
                L("AuditLog.ChangeMasterPassword"),
            AuditEventCode.AutoBackupSettingChanged =>
                L("AuditLog.ChangeAutoBackupConfig"),
            AuditEventCode.AutoLockSettingChanged =>
                L("AuditLog.ChangeAutoLockConfig"),
            AuditEventCode.ScreenCaptureProtectionChanged =>
                L("AuditLog.ChangeCaptureProtectionConfig"),
            AuditEventCode.WindowsHelloChanged =>
                L("AuditLog.ChangeWindowsHelloConfig"),
            AuditEventCode.FaviconAutoFetchChanged =>
                L("AuditLog.ChangeFaviconFetchConfig"),

            // Authentication (0x4000)
            AuditEventCode.AuthFailed =>
                L("AuditLog.AuthenticationFailed"),
            AuditEventCode.AuthSucceeded =>
                L("AuditLog.AuthenticationSuccess"),

            // Save (0x5000)
            AuditEventCode.SecretSaved when payload is SecretSavedPayload =>
                string.Format(L("AuditLog.SaveSecret"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.TimeMachineRestored =>
                L("AuditLog.RestoreTimeMachineGeneration"),
            AuditEventCode.FileAdded when payload is FileAddedPayload =>
                string.Format(L("AuditLog.AddFileLink"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.ProfileSaved =>
                L("AuditLog.SaveProfile"),

            // Delete/purge (0x6000)
            AuditEventCode.SecretSoftDeleted when payload is SecretSoftDeletedPayload =>
                string.Format(L("AuditLog.MoveSecretToTrash"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.SecretUndeleted when payload is SecretUndeletedPayload =>
                string.Format(L("AuditLog.UndoDeleteSecret"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.SecretPermanentlyDeleted when payload is SecretPermanentlyDeletedPayload =>
                string.Format(L("AuditLog.PurgeSecretPermanently"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.SecretAutoPurgedByExpiry =>
                L("AuditLog.AutoPurgeExpiredSecret"),
            AuditEventCode.TimeMachineSlotDeleted =>
                L("AuditLog.DeleteTimeMachineSlot"),
            AuditEventCode.TimeMachineGenRotated =>
                L("AuditLog.RotateTimeMachineGeneration"),
            AuditEventCode.FilePermanentlyDeleted when payload is FilePermanentlyDeletedPayload =>
                string.Format(L("AuditLog.PurgeFilePermanently"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.FileSoftDeleted when payload is FileSoftDeletedPayload =>
                string.Format(L("AuditLog.MoveFileToTrash"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.FileUndeleted when payload is FileUndeletedPayload =>
                string.Format(L("AuditLog.UndoDeleteFile"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.FileAutoPurgedByExpiry when payload is FileAutoPurgedByExpiryPayload afp =>
                string.Format(L("AuditLog.AutoPurgeExpiredFile"), afp.PurgedCount),

            // Critical operations (0x7000)
            AuditEventCode.EmergencyAccessCodeExecuted =>
                L("AuditLog.ExecuteEmergencyAccessRecovery"),
            AuditEventCode.PlaintextImportExecuted when payload is PlaintextImportExecutedPayload pi =>
                string.Format(L("AuditLog.ImportPlainText"), pi.ItemCount),
            AuditEventCode.PlaintextExportExecuted when payload is PlaintextExportExecutedPayload pe =>
                string.Format(L("AuditLog.ExportPlainText"), pe.ItemCount),
            AuditEventCode.ImportItemFailed when payload is ImportItemFailedPayload ifp =>
                string.Format(L("AuditLog.ImportItemFailed"), ifp.Ordinal, ifp.Format),
            AuditEventCode.ExportItemFailed =>
                string.Format(L("AuditLog.ExportItemFailed"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.CsvFormulaGuardApplied =>
                string.Format(L("AuditLog.CsvFormulaGuardApplied"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.SecretFieldCopiedToClipboard when payload is SecretFieldCopiedToClipboardPayload sfc =>
                string.Format(L("AuditLog.SecretFieldCopiedToClipboard"), sfc.FieldLabel, resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.SecretAutoTypeExecuted when payload is SecretAutoTypeExecutedPayload sat =>
                string.Format(L("AuditLog.SecretAutoTypeExecuted"), sat.FieldLabel, resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.TimeMachineValueCopiedToClipboard when payload is TimeMachineValueCopiedToClipboardPayload tvc =>
                string.Format(L("AuditLog.TimeMachineValueCopiedToClipboard"), tvc.FieldLabel, tvc.GenerationLabel, resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.ProfileFieldCopiedToClipboard when payload is ProfileFieldCopiedToClipboardPayload pfc =>
                string.Format(L("AuditLog.ProfileFieldCopiedToClipboard"), pfc.FieldLabel),
            AuditEventCode.VaultCreated =>
                L("AuditLog.VaultCreated"),
            AuditEventCode.LocaleImportSucceeded when payload is LocaleImportSucceededPayload lis =>
                string.Format(L("AuditLog.LocaleImportSucceeded"), lis.DisplayName ?? "Custom", lis.PatchedCount),
            AuditEventCode.LocaleImportFailed =>
                L("AuditLog.LocaleImportFailed"),
            AuditEventCode.LocaleImportKeyMissing when payload is LocaleImportKeyMissingPayload lkm =>
                string.Format(L("AuditLog.LocaleImportKeyMissing"), lkm.Key),
            AuditEventCode.LocaleImportValueTooLong when payload is LocaleImportValueTooLongPayload lvt =>
                string.Format(L("AuditLog.LocaleImportValueTooLong"), lvt.Key),
            AuditEventCode.LocaleImportPlaceholderBroken when payload is LocaleImportPlaceholderBrokenPayload lpb =>
                string.Format(L("AuditLog.LocaleImportPlaceholderBroken"), lpb.Key),
            AuditEventCode.FileExported when payload is FileExportedPayload =>
                string.Format(L("AuditLog.ExportFileFromGallery"), resolvedName ?? L("AuditLog.DeletedItemPlaceholder")),
            AuditEventCode.EmergencyAccessCodeGenerated =>
                L("AuditLog.GenerateEmergencyCode"),
            AuditEventCode.EmergencyAccessCodeRevoked =>
                L("AuditLog.RevokeEmergencyCode"),
            AuditEventCode.BackupExecuted =>
                L("AuditLog.ExecuteManualBackup"),
            AuditEventCode.RestoreExecuted =>
                L("AuditLog.ExecuteManualRestore"),

            // System (0xF000)
            AuditEventCode.NoOpDraftDiscarded when payload is NoOpDraftDiscardedPayload nd =>
                string.Format(L("AuditLog.SelfHealNoOpDraft"), nd.DiscardedCount),
            AuditEventCode.FileContentTypeRepaired when payload is FileContentTypeRepairedPayload ct =>
                string.Format(L("AuditLog.RepairFileContentType"),
                    resolvedName ?? L("AuditLog.DeletedItemPlaceholder"), ct.OldContentType, ct.NewContentType),
            AuditEventCode.AuditLogsPurged =>
                L("AuditLog.ExecuteLogRotation"),
            AuditEventCode.UnifiedDbAutoRecovered =>
                L("AuditLog.SelfHealUnifiedDb"),
            AuditEventCode.VaultDbAutoRecovered when payload is VaultDbAutoRecoveredPayload v =>
                string.Format(L("AuditLog.SelfHealVaultDb"), v.DbNumber),
            // Unlike the other guarded arms above, VaultDbAutoRecovered deliberately keeps this
            // second, unguarded arm: if the payload fails to decrypt, this still tells the user
            // self-healing ran instead of falling through to the raw hex fallback below.
            AuditEventCode.VaultDbAutoRecovered =>
                L("AuditLog.ExecuteSilentSelfHealing"),
            AuditEventCode.AutoBackupExecuted =>
                L("AuditLog.ExecuteAutoBackup"),

            // Fail-safe fallback (for undefined codes / data corruption protection)
            _ => $"[Code: 0x{(int)code:X4}]",
        };
    }

    private static int GetEventLevel(AuditEventCode code) => code switch
    {
        AuditEventCode.MasterPasswordChanged         => 2,
        AuditEventCode.EmergencyAccessCodeGenerated         => 2,
        AuditEventCode.EmergencyAccessCodeRevoked           => 2,
        AuditEventCode.PlaintextExportExecuted       => 2,
        AuditEventCode.RestoreExecuted               => 2,
        AuditEventCode.SecretPermanentlyDeleted      => 2,
        AuditEventCode.FilePermanentlyDeleted        => 2,
        AuditEventCode.EmergencyAccessCodeExecuted  => 2,
        AuditEventCode.PlaintextImportExecuted       => 1,
        AuditEventCode.AuthFailed                    => 1,
        AuditEventCode.SecretSoftDeleted             => 1,
        AuditEventCode.FileSoftDeleted                => 1,
        AuditEventCode.TimeMachineSlotDeleted        => 1,
        AuditEventCode.FileExported                  => 1,
        AuditEventCode.FileContentTypeRepaired       => 1,
        AuditEventCode.ImportItemFailed               => 1,
        AuditEventCode.ExportItemFailed               => 1,
        AuditEventCode.CsvFormulaGuardApplied         => 1,
        AuditEventCode.SecretFieldCopiedToClipboard   => 1,
        AuditEventCode.SecretAutoTypeExecuted         => 1,
        AuditEventCode.TimeMachineValueCopiedToClipboard => 1,
        AuditEventCode.ProfileFieldCopiedToClipboard  => 1,
        AuditEventCode.VaultCreated                   => 1,
        AuditEventCode.LocaleImportFailed             => 1,
        AuditEventCode.LocaleImportValueTooLong       => 1,
        AuditEventCode.LocaleImportPlaceholderBroken  => 1,
        _                                            => 0,
    };
}
