// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// AuditLogService.BuildSummary tests for the automatic-healing event codes (TC-AL-20..23):
/// AuditEventCode.FileContentTypeRepaired (one row per repaired StoredFile),
/// AuditEventCode.UnifiedDbAutoRecovered and AuditEventCode.VaultDbAutoRecovered (shadow-file DB
/// recovery, each with its own dedicated code rather than a shared code with a free-text payload).
/// LocalizationManager has no locale loaded in this test process, so Get() falls back to "[key]" -
/// these tests assert on which locale KEY was selected (via that bracketed fallback), not on the
/// localized Japanese/English text.
/// </summary>
public sealed class AuditLogSelfHealSummaryTests
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

    // ── TC-AL-20 ─────────────────────────────────────────────────────────────
    // A ContentType repair is its own event code (FileContentTypeRepaired), not lumped in with the
    // DB-shadow-recovery codes, specifically so each repaired file gets its own investigable row
    // (embedded Name + old/new ContentType) instead of an unverifiable aggregate count.

    [Fact]
    public async Task FileContentTypeRepaired_UsesDedicatedKeyWithFileNameAndCodes()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.FileContentTypeRepaired,
                new FileContentTypeRepairedPayload(TargetId: 1, OldContentType: 9999, NewContentType: 4000, Name: "notes.txt"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.FileContentTypeRepaired, item.EventCode);
            // No locale is loaded in this test process, so Get() falls back to the bracketed key
            // with no {0}/{1}/{2} placeholders - the Name/old/new values can't be asserted here,
            // only that the dedicated per-file key (not the generic self-heal fallback) was chosen.
            Assert.Contains("AuditLog.RepairFileContentType", item.Summary);
            Assert.DoesNotContain("ExecuteSilentSelfHealing", item.Summary);
        }
    }

    // ── TC-AL-21 ─────────────────────────────────────────────────────────────
    // UnifiedDbAutoRecovered has no Payload - the code alone selects the unified-DB-specific key.

    [Fact]
    public async Task UnifiedDbAutoRecovered_UsesUnifiedDbKey()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.UnifiedDbAutoRecovered,
                null,
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var summary = Assert.Single(items).Summary;
            Assert.Contains("AuditLog.SelfHealUnifiedDb", summary);
        }
    }

    // ── TC-AL-22 ─────────────────────────────────────────────────────────────
    // VaultDbAutoRecovered carries the vault number as a typed Payload field (not parsed out of a
    // free-text string), and the vault-DB-specific key is selected.

    [Fact]
    public async Task VaultDbAutoRecovered_UsesVaultDbKeyWithNumber()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.VaultDbAutoRecovered,
                new VaultDbAutoRecoveredPayload(DbNumber: 2),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var summary = Assert.Single(items).Summary;
            Assert.Contains("AuditLog.SelfHealVaultDb", summary);
        }
    }

    // ── TC-AL-23 ─────────────────────────────────────────────────────────────
    // A VaultDbAutoRecovered row with no decodable Payload (e.g. payload decryption failure) still
    // falls back to the generic message rather than crashing or showing a raw/undefined string.

    [Fact]
    public async Task VaultDbAutoRecovered_NullPayload_FallsBackToGenericMessage()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.VaultDbAutoRecovered,
                null,
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var summary = Assert.Single(items).Summary;
            Assert.Contains("AuditLog.ExecuteSilentSelfHealing", summary);
        }
    }

    // ── TC-AL-24 ─────────────────────────────────────────────────────────────
    // NoOpDraftDiscarded carries an aggregate count (not one row per secret), since a discarded
    // draft byte-for-byte identical to Gen0 held no information beyond what Gen0 already has - see
    // SecretsViewModel.LoadSecretAsync/CleanUpNoOpDraftsAsync/GetDraftCompareDataAsync.

    [Fact]
    public async Task NoOpDraftDiscarded_UsesDedicatedKeyWithCount()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.NoOpDraftDiscarded,
                new NoOpDraftDiscardedPayload(DiscardedCount: 3),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.NoOpDraftDiscarded, item.EventCode);
            Assert.Contains("AuditLog.SelfHealNoOpDraft", item.Summary);
        }
    }
}
