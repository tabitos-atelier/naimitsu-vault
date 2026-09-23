// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// AuditLogService.BuildSummary tests for the clipboard-copy, AutoType, and vault-creation event
/// codes (TC-AL-33..37): AuditEventCode.SecretFieldCopiedToClipboard, SecretAutoTypeExecuted,
/// TimeMachineValueCopiedToClipboard, ProfileFieldCopiedToClipboard, VaultCreated.
/// LocalizationManager has no locale loaded in this test process, so Get() falls back to "[key]" -
/// these tests assert on which locale KEY was selected (via that bracketed fallback), not on the
/// localized Japanese/English text.
/// </summary>
public sealed class AuditLogClipboardAutoTypeVaultSummaryTests
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

    // ── TC-AL-33 ─────────────────────────────────────────────────────────────
    // SecretFieldCopiedToClipboard: FieldLabel is the field's display label, never the copied value.

    [Fact]
    public async Task SecretFieldCopiedToClipboard_UsesDedicatedKeyWithFieldLabel()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.SecretFieldCopiedToClipboard,
                new SecretFieldCopiedToClipboardPayload(TargetId: 1, FieldLabel: "Password", Name: "Example"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.SecretFieldCopiedToClipboard, item.EventCode);
            Assert.Contains("AuditLog.SecretFieldCopiedToClipboard", item.Summary);
        }
    }

    // ── TC-AL-34 ─────────────────────────────────────────────────────────────
    // SecretAutoTypeExecuted: one row per field (UserId/Password) sent via DirectInjectionDialog.

    [Fact]
    public async Task SecretAutoTypeExecuted_UsesDedicatedKeyWithFieldLabel()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.SecretAutoTypeExecuted,
                new SecretAutoTypeExecutedPayload(TargetId: 1, FieldLabel: "UserId", Name: "Example"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.SecretAutoTypeExecuted, item.EventCode);
            Assert.Contains("AuditLog.SecretAutoTypeExecuted", item.Summary);
        }
    }

    // ── TC-AL-35 ─────────────────────────────────────────────────────────────
    // TimeMachineValueCopiedToClipboard: GenerationLabel distinguishes Current/Gen1/Gen2.

    [Fact]
    public async Task TimeMachineValueCopiedToClipboard_UsesDedicatedKeyWithFieldAndGenerationLabel()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.TimeMachineValueCopiedToClipboard,
                new TimeMachineValueCopiedToClipboardPayload(TargetId: 1, FieldLabel: "Password", GenerationLabel: "Gen1", Name: "Example"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.TimeMachineValueCopiedToClipboard, item.EventCode);
            Assert.Contains("AuditLog.TimeMachineValueCopiedToClipboard", item.Summary);
        }
    }

    // ── TC-AL-36 ─────────────────────────────────────────────────────────────
    // ProfileFieldCopiedToClipboard: no TargetId (Profile is a per-vault singleton).

    [Fact]
    public async Task ProfileFieldCopiedToClipboard_UsesDedicatedKeyWithFieldLabel()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(
                AuditEventCode.ProfileFieldCopiedToClipboard,
                new ProfileFieldCopiedToClipboardPayload(FieldLabel: "Email"),
                key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.ProfileFieldCopiedToClipboard, item.EventCode);
            Assert.Contains("AuditLog.ProfileFieldCopiedToClipboard", item.Summary);
        }
    }

    // ── TC-AL-37 ─────────────────────────────────────────────────────────────
    // VaultCreated: Payload = NULL - recorded as the newly created vault's own first log row.

    [Fact]
    public async Task VaultCreated_UsesDedicatedKeyWithNoPayload()
    {
        var (db, session, service) = BuildService();
        using (db)
        {
            session.SetKey(new byte[32]);
            var key = session.GetKey();
            await service.LogAsync(AuditEventCode.VaultCreated, null, key, TestContext.Current.CancellationToken);

            var items = await service.GetRecentAsync(10, key, TestContext.Current.CancellationToken);

            var item = Assert.Single(items);
            Assert.Equal(AuditEventCode.VaultCreated, item.EventCode);
            Assert.Contains("AuditLog.VaultCreated", item.Summary);
        }
    }
}
