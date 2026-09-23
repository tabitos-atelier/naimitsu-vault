// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// AuditLogService.BuildSummary tests for the per-item import/export failure, CSV formula-injection
/// guard, and custom-locale import event codes (TC-AL-25..32): AuditEventCode.ImportItemFailed,
/// ExportItemFailed, LocaleImportKeyMissing, LocaleImportValueTooLong, CsvFormulaGuardApplied,
/// LocaleImportSucceeded, LocaleImportFailed, LocaleImportPlaceholderBroken.
/// LocalizationManager has no locale loaded in this test process, so Get() falls back to "[key]" -
/// these tests assert on which locale KEY was selected (via that bracketed fallback), not on the
/// localized Japanese/English text.
/// </summary>
public sealed class AuditLogImportExportLocaleSummaryTests
{
    private static (TestDb db, AppSession session, AuditLogService service) BuildService()
    {
        var db      = TestDb.Create();
        var session = new AppSession();
        var crypto  = new IdentityCryptoService();
        var repo    = new AuditLogRepository(db.Factory);
        var secrets = new SecretRepository(db.Factory);
        var files   = new StoredFileRepository(db.Factory, crypto, Microsoft.Extensions.Logging.Abstractions.NullLogger<StoredFileRepository>.Instance);
        var service = new AuditLogService(repo, crypto, session, secrets, files);
        return (db, session, service);
    }

    // ── TC-AL-25 ─────────────────────────────────────────────────────────────
    // ImportItemFailed carries only the format and 1-based ordinal - the failed record's own
    // title can't be trusted as an identifier since the same failure may be why it's unreadable.

    [Fact]
    public async Task ImportItemFailed_UsesDedicatedKeyWithOrdinalAndFormat()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.ImportItemFailed,
                new ImportItemFailedPayload(Format: "JSON", Ordinal: 7),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.ImportItemFailed, item.EventCode);
            Assert.Contains("AuditLog.ImportItemFailed", item.Summary);
        }
    }

    // ── TC-AL-26 ─────────────────────────────────────────────────────────────
    // ExportItemFailed follows the same TargetId/Name-snapshot pattern as SecretSoftDeletedPayload
    // etc., so it's picked up by IsSecretEvent()'s live name-resolution fallback when Name is null.

    [Fact]
    public async Task ExportItemFailed_UsesDedicatedKey()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.ExportItemFailed,
                new ExportItemFailedPayload(TargetId: 42),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.ExportItemFailed, item.EventCode);
            Assert.Contains("AuditLog.ExportItemFailed", item.Summary);
        }
    }

    // ── TC-AL-27 ─────────────────────────────────────────────────────────────
    // LocaleImportKeyMissing: one row per missing key (expected rare), carrying the key name.

    [Fact]
    public async Task LocaleImportKeyMissing_UsesDedicatedKeyWithMissingKeyName()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.LocaleImportKeyMissing,
                new LocaleImportKeyMissingPayload(Key: "Common.Ok"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.LocaleImportKeyMissing, item.EventCode);
            Assert.Contains("AuditLog.LocaleImportKeyMissing", item.Summary);
        }
    }

    // ── TC-AL-28 ─────────────────────────────────────────────────────────────
    // LocaleImportValueTooLong: one row per rejected oversized value, carrying the key name.

    [Fact]
    public async Task LocaleImportValueTooLong_UsesDedicatedKeyWithKeyName()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.LocaleImportValueTooLong,
                new LocaleImportValueTooLongPayload(Key: "Common.Ok"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.LocaleImportValueTooLong, item.EventCode);
            Assert.Contains("AuditLog.LocaleImportValueTooLong", item.Summary);
        }
    }

    // ── TC-AL-29 ─────────────────────────────────────────────────────────────
    // CsvFormulaGuardApplied: one row per record with the already-decrypted title embedded
    // directly (unlike ExportItemFailedPayload, the record decrypted fine here).

    [Fact]
    public async Task CsvFormulaGuardApplied_UsesDedicatedKey()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.CsvFormulaGuardApplied,
                new CsvFormulaGuardAppliedPayload(TargetId: 7, Name: "=cmd|'/c calc'!A1"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.CsvFormulaGuardApplied, item.EventCode);
            Assert.Contains("AuditLog.CsvFormulaGuardApplied", item.Summary);
        }
    }

    // ── TC-AL-30 ─────────────────────────────────────────────────────────────
    // LocaleImportSucceeded: one row per import attempt that completed, carrying the total
    // patched-key count (regardless of whether any soft-patching actually happened).

    [Fact]
    public async Task LocaleImportSucceeded_UsesDedicatedKeyWithPatchedCount()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.LocaleImportSucceeded,
                new LocaleImportSucceededPayload(PatchedCount: 3, DisplayName: "Cyber Desert"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.LocaleImportSucceeded, item.EventCode);
            Assert.Contains("AuditLog.LocaleImportSucceeded", item.Summary);
        }
    }

    // ── TC-AL-31 ─────────────────────────────────────────────────────────────
    // LocaleImportFailed: one row per import attempt rejected outright at the encoding/JSON-
    // structure gate. Payload = NULL - the failure reason is already shown via notification.

    [Fact]
    public async Task LocaleImportFailed_UsesDedicatedKeyWithNoPayload()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(AuditEventCode.LocaleImportFailed, null, key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.LocaleImportFailed, item.EventCode);
            Assert.Contains("AuditLog.LocaleImportFailed", item.Summary);
        }
    }

    // ── TC-AL-32 ─────────────────────────────────────────────────────────────
    // LocaleImportPlaceholderBroken: one row per imported value that dropped a fallback
    // placeholder (e.g. "{0}"), carrying the key name.

    [Fact]
    public async Task LocaleImportPlaceholderBroken_UsesDedicatedKeyWithKeyName()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.LocaleImportPlaceholderBroken,
                new LocaleImportPlaceholderBrokenPayload(Key: "AppSettings.Dialog.VersionMismatchPatchNotice"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.LocaleImportPlaceholderBroken, item.EventCode);
            Assert.Contains("AuditLog.LocaleImportPlaceholderBroken", item.Summary);
        }
    }
}
