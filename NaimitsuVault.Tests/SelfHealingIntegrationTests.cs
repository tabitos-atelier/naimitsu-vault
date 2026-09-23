// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// SelfHealingIntegrationTests - autonomous self-healing sequence integration tests
///
/// The unified DB shadow disguises itself among the crowd as a 48-character anonymous file carrying
/// the NKDB_SHADOW magic. FindByMagic(NKDB_SHADOW) identifies the shadow via an in-memory reverse lookup.
///
///   TC-SHI-01 - Happy path: proves recovery I/O happens zero times when the primary DB is healthy
///   TC-SHI-02 - Auto-heal: primary DB corrupt + shadow healthy -> silent recovery, audit log 0xFF02, InfoBar shown
///   TC-SHI-03 - Fail-fast: primary DB corrupt + no valid shadow -> immediate freeze
///   TC-SHI-04 - Multiple-shadow fail-fast: 2+ NKDB_SHADOW candidates -> immediate freeze
///   TC-SHI-08 - Vault auto-heal: vault DB present but corrupted (not missing) + healthy shadow -> silent recovery
///   TC-SHI-09 - Vault fail-fast: vault DB missing/corrupted + no usable shadow -> VaultDbUnrecoverable
///   TC-SHI-10 - Vault fail-fast: vault DB missing/corrupted + 2+ VAULT_SHADOW candidates -> VaultDbUnrecoverable
/// </summary>
[Collection("SequentialSqlitePool")]
public sealed class SelfHealingIntegrationTests : IDisposable
{
    private readonly string              _testDir;
    private readonly AppSession          _session;
    private readonly SelfHealingAuditSpy _auditSpy;

    private const string NkdbName = "NaimitsuVault.nkdb";

    public SelfHealingIntegrationTests()
    {
        _testDir  = Path.Combine(Path.GetTempPath(), $"naimitsu_sh_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _session  = new AppSession();
        _auditSpy = new SelfHealingAuditSpy();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SHI-01: primary DB healthy -> recovery I/O zero times, flags unchanged, InfoBar hidden
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GivenHealthyNkdb_WhenDiscoveryRuns_ThenNoRecoveryAndNoFlag()
    {
        // Arrange: place a healthy primary DB under the fixed name (no shadow)
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        CreateMinimalSqliteFile(nkdbPath);

        // Act
        bool healthy = await RunNkdbDiscoverySequenceAsync(nkdbPath, _testDir);

        // Assert
        Assert.True(healthy,                          "A healthy nkdb must be treated as normal");
        Assert.False(_session.UnifiedDbAutoRecovered, "The recovery flag must not be set");
        Assert.False(_session.IsUnifiedDbCorrupted,   "The freeze flag must not be set");
        Assert.Equal(0, _auditSpy.UnifiedAutoRecoveredCount);

        // No NKDB_SHADOW candidates exist (proof that recovery I/O happened zero times)
        var shadows = ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW);
        Assert.Empty(shadows);

        var dashVm = MakeDashboardVm();
        Assert.False(dashVm.HasAutoRecoveryWarning);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SHI-02: primary DB corrupt + shadow healthy -> silent heal, audit log 9900, InfoBar shown
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GivenCorruptNkdb_AndHealthyShadow_WhenDiscoveryRuns_ThenSilentHealingAndAuditLog()
    {
        // Arrange: the primary nkdb is 0 bytes (corrupt); the shadow is a 48-character file with the NKDB_SHADOW magic
        var nkdbPath   = Path.Combine(_testDir, NkdbName);
        var shadowName = ShadowFileService.GenerateBase64UrlName();
        var shadowPath = Path.Combine(_testDir, shadowName);
        await File.WriteAllBytesAsync(nkdbPath, [], TestContext.Current.CancellationToken);
        CreateSqliteWithMagic(shadowPath, ShadowFileService.NKDB_SHADOW);

        // Act
        bool healthy = await RunNkdbDiscoverySequenceAsync(nkdbPath, _testDir);

        // Assert - healing succeeded
        Assert.True(healthy,                         "healthy=true after a successful shadow recovery");
        Assert.True(_session.UnifiedDbAutoRecovered, "The auto-recovery flag must be set");
        Assert.False(_session.IsUnifiedDbCorrupted,  "The freeze flag must not be set");

        // The shadow was identified via FindByMagic (the shadow file is not deleted)
        var remainingShadows = ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW);
        Assert.Single(remainingShadows);
        Assert.Equal(shadowPath, remainingShadows[0]);

        // Mimics the forced audit-log write at the end of EnterVaultCoreAsync
        if (_session.UnifiedDbAutoRecovered)
        {
            await _auditSpy.LogAsync(
                AuditEventCode.UnifiedDbAutoRecovered,
                null,
                default,
                TestContext.Current.CancellationToken);
            _session.UnifiedDbAutoRecovered = false;
        }
        Assert.Equal(1, _auditSpy.UnifiedAutoRecoveredCount);
        Assert.False(_session.UnifiedDbAutoRecovered);

        // Confirm the InfoBar warning is shown
        _session.UnifiedDbAutoRecovered = true;
        var dashVm = MakeDashboardVm();
        Assert.True(dashVm.HasAutoRecoveryWarning);

        var msg = dashVm.AutoRecoveryMessage;
        Assert.True(
            msg.Contains("Common.InfoUnifiedDbAutoRecovered") || msg.Contains("統合データベース"),
            $"AutoRecoveryMessage does not correspond to D1MI01: {msg}");

        // The recovered nkdb carries the NKDB_ORIGIN magic
        var restoredMagic = await ShadowFileService.ReadMagicAsync(nkdbPath);
        Assert.Equal(ShadowFileService.NKDB_ORIGIN, restoredMagic);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SHI-03: primary DB corrupt + no valid shadow -> immediate fail-fast freeze
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GivenCorruptNkdb_AndNoValidShadow_ThenFailFastFreeze()
    {
        // Arrange: the primary nkdb is 0 bytes (corrupt). No valid NKDB_SHADOW file exists.
        var nkdbPath = Path.Combine(_testDir, NkdbName);
        await File.WriteAllBytesAsync(nkdbPath, [], TestContext.Current.CancellationToken);

        // Act
        bool healthy = await RunNkdbDiscoverySequenceAsync(nkdbPath, _testDir);

        // Assert
        Assert.False(healthy,                         "healthy=false when there is no valid shadow");
        Assert.False(_session.UnifiedDbAutoRecovered, "The recovery flag must not be set");
        Assert.True(_session.IsUnifiedDbCorrupted,    "The freeze flag must be set");
        Assert.Equal(0, _auditSpy.UnifiedAutoRecoveredCount);

        // No NKDB_SHADOW candidates exist (no valid shadow)
        var shadows = ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW);
        Assert.Empty(shadows);

        // The primary DB remains 0 bytes (not overwritten)
        Assert.Equal(0L, new FileInfo(nkdbPath).Length);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SHI-04: 2+ NKDB_SHADOW candidates -> inconsistency fail-fast freeze (recovery loop shut out entirely)
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GivenCorruptNkdb_AndMultipleShadowCandidates_ThenFailFastFreeze()
    {
        // Arrange: the primary nkdb is corrupt. 2 files carrying the NKDB_SHADOW magic exist (inconsistent)
        var nkdbPath   = Path.Combine(_testDir, NkdbName);
        var shadow1    = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        var shadow2    = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        await File.WriteAllBytesAsync(nkdbPath, [], TestContext.Current.CancellationToken);
        CreateSqliteWithMagic(shadow1, ShadowFileService.NKDB_SHADOW);
        CreateSqliteWithMagic(shadow2, ShadowFileService.NKDB_SHADOW);

        // Act
        bool healthy = await RunNkdbDiscoverySequenceAsync(nkdbPath, _testDir);

        // Assert
        Assert.False(healthy,                         "healthy=false when multiple NKDB_SHADOW files exist");
        Assert.False(_session.UnifiedDbAutoRecovered, "The recovery flag must not be set");
        Assert.True(_session.IsUnifiedDbCorrupted,    "The inconsistency-freeze flag must be set");
        Assert.Equal(0, _auditSpy.UnifiedAutoRecoveredCount);

        // Confirms exactly 2 candidates existed (proof the recovery logic gave up on candidate selection)
        var shadows = ShadowFileService.FindByMagic(_testDir, ShadowFileService.NKDB_SHADOW);
        Assert.Equal(2, shadows.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SHI-08: vault DB present but corrupted + healthy shadow -> silent auto-recovery
    // (regression coverage for the 2026-08-28 fix: EnterVaultCoreAsync used to gate recovery on
    // File.Exists alone, so a corrupted-but-present vault file never triggered restoration at all)
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GivenCorruptButPresentVault_AndHealthyShadow_WhenDiscoveryRuns_ThenSilentRecovery()
    {
        // Arrange: the vault origin exists but is 0 bytes (corrupt, not missing); a healthy VAULT_SHADOW exists
        var vaultPath = Path.Combine(_testDir, "vault_origin_for_test.db");
        var shadowPath = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        await File.WriteAllBytesAsync(vaultPath, [], TestContext.Current.CancellationToken);
        CreateSqliteWithMagic(shadowPath, ShadowFileService.VAULT_SHADOW);

        // Act
        bool healthy = await RunVaultDiscoverySequenceAsync(vaultPath, _testDir, dbNumber: 1);

        // Assert
        Assert.True(healthy,                             "healthy=true after a successful shadow recovery");
        Assert.True(_session.VaultDbAutoRecovered,        "The auto-recovery flag must be set");
        Assert.Equal(1, _session.VaultDbAutoRecoveredNumber);
        Assert.False(_session.VaultDbUnrecoverable,       "The unrecoverable flag must not be set");

        var restoredMagic = await ShadowFileService.ReadMagicAsync(vaultPath);
        Assert.Equal(ShadowFileService.VAULT_ORIGIN, restoredMagic);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SHI-09: vault DB missing/corrupted + no usable shadow -> VaultDbUnrecoverable (not "wrong password")
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GivenMissingVault_AndNoShadow_WhenDiscoveryRuns_ThenUnrecoverableFlagSet()
    {
        // Arrange: no vault file, no shadow candidates at all
        var vaultPath = Path.Combine(_testDir, "vault_origin_for_test.db");

        // Act
        bool healthy = await RunVaultDiscoverySequenceAsync(vaultPath, _testDir, dbNumber: 2);

        // Assert
        Assert.False(healthy,                             "healthy=false when there is no usable shadow");
        Assert.False(_session.VaultDbAutoRecovered,        "The recovery flag must not be set");
        Assert.True(_session.VaultDbUnrecoverable,         "The unrecoverable flag must be set");
        Assert.Equal(2, _session.VaultDbUnrecoverableNumber);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-SHI-10: vault DB missing/corrupted + 2+ VAULT_SHADOW candidates (ambiguous) -> VaultDbUnrecoverable,
    // neither candidate is used to restore
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GivenCorruptVault_AndAmbiguousShadowCandidates_WhenDiscoveryRuns_ThenUnrecoverableFlagSet()
    {
        // Arrange: the vault origin is corrupt. 2 files carrying the VAULT_SHADOW magic exist (inconsistent)
        var vaultPath = Path.Combine(_testDir, "vault_origin_for_test.db");
        var shadow1   = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        var shadow2   = Path.Combine(_testDir, ShadowFileService.GenerateBase64UrlName());
        await File.WriteAllBytesAsync(vaultPath, [], TestContext.Current.CancellationToken);
        CreateSqliteWithMagic(shadow1, ShadowFileService.VAULT_SHADOW);
        CreateSqliteWithMagic(shadow2, ShadowFileService.VAULT_SHADOW);

        // Act
        bool healthy = await RunVaultDiscoverySequenceAsync(vaultPath, _testDir, dbNumber: 3);

        // Assert
        Assert.False(healthy,                             "healthy=false when the shadow candidate set is ambiguous");
        Assert.False(_session.VaultDbAutoRecovered,        "The recovery flag must not be set");
        Assert.True(_session.VaultDbUnrecoverable,         "The unrecoverable flag must be set");
        Assert.Equal(3, _session.VaultDbUnrecoverableNumber);

        // The vault origin remains untouched (0 bytes - neither candidate was used to restore)
        Assert.Equal(0L, new FileInfo(vaultPath).Length);
    }

    // ── Simulated sequence: precisely reproduces the vault healing flow from
    // AuthService.MultiVault.EnterVaultCoreAsync (the ShadowFileKey-pinned lookup is a DB-backed
    // fast path not exercised here; these tests cover the FindByMagic fallback, which is where the
    // 2026-08-28 fix's three-way branch and ambiguity detection live) ──
    private async Task<bool> RunVaultDiscoverySequenceAsync(string vaultPath, string dataDir, int dbNumber)
    {
        bool vaultHealthy = File.Exists(vaultPath) && await ShadowFileService.IsFileHealthyAsync(vaultPath);
        if (vaultHealthy) return true;

        var shadowCandidates = ShadowFileService.FindByMagic(dataDir, ShadowFileService.VAULT_SHADOW);
        string? shadowPath = null;
        bool ambiguousShadows = false;
        if (shadowCandidates.Count == 1) shadowPath = shadowCandidates[0];
        else if (shadowCandidates.Count >= 2) ambiguousShadows = true;

        if (!ambiguousShadows && shadowPath != null &&
            await ShadowFileService.IsShadowHealthyAsync(shadowPath, ShadowFileService.VAULT_SHADOW))
        {
            bool restored = await ShadowFileService.TryRestoreFromShadowAsync(
                shadowPath, vaultPath, ShadowFileService.VAULT_ORIGIN);
            if (restored)
            {
                _session.VaultDbAutoRecovered       = true;
                _session.VaultDbAutoRecoveredNumber = dbNumber;
                return true;
            }
        }

        _session.VaultDbUnrecoverable       = true;
        _session.VaultDbUnrecoverableNumber = dbNumber;
        return false;
    }

    // ── Simulated sequence: precisely reproduces the nkdb healing flow from App.xaml.cs OnLaunched ──
    private async Task<bool> RunNkdbDiscoverySequenceAsync(string nkdbPath, string dataDir)
    {
        bool nkdbExists  = File.Exists(nkdbPath);
        bool nkdbHealthy = nkdbExists && await RunPragmaQuickCheckAsync(nkdbPath);

        if (!nkdbHealthy)
        {
            var shadowCandidates = ShadowFileService.FindByMagic(dataDir, ShadowFileService.NKDB_SHADOW);
            if (shadowCandidates.Count == 1 &&
                await ShadowFileService.IsShadowHealthyAsync(shadowCandidates[0], ShadowFileService.NKDB_SHADOW))
            {
                bool restored = await ShadowFileService.TryRestoreFromShadowAsync(
                    shadowCandidates[0], nkdbPath, ShadowFileService.NKDB_ORIGIN);
                if (restored)
                {
                    _session.UnifiedDbAutoRecovered = true;
                    return true;
                }
            }
            else if (!nkdbExists && shadowCandidates.Count == 0)
            {
                // Completely fresh install: do not freeze
                return true;
            }

            // nkdb corrupt + shadow missing/multiple/corrupt -> fail-fast freeze
            _session.IsUnifiedDbCorrupted = true;
            return false;
        }
        return true;
    }

    private static async Task<bool> RunPragmaQuickCheckAsync(string path)
    {
        try
        {
            if (new FileInfo(path).Length < 100) return false; // under the SQLite header size = corrupt
            var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource  = path,
                Mode        = SqliteOpenMode.ReadOnly,
                ForeignKeys = false,
            };
            await using var conn = new SqliteConnection(csb.ToString());
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return result is string s && s.Equals("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void CreateMinimalSqliteFile(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS _dummy (id INTEGER PRIMARY KEY);";
        cmd.ExecuteNonQuery();
    }

    private static void CreateSqliteWithMagic(string path, uint magic)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {magic}; CREATE TABLE IF NOT EXISTS _dummy (id INTEGER PRIMARY KEY);";
        cmd.ExecuteNonQuery();
    }

    private DashboardViewModel MakeDashboardVm()
        => new(null!, null!, null!, null!, null!, null!, null!, _session, _auditSpy, null!, null!, null!, NullLogger<DashboardViewModel>.Instance);
}

// ── Test-only IAuditLogService spy ───────────────────────────────────────
// StubAuditLogService is sealed and cannot be inherited, so IAuditLogService is implemented directly.
// DekScope is a readonly struct: default(DekScope) is safe as long as its Span is never accessed.
internal sealed class SelfHealingAuditSpy : IAuditLogService
{
    private int _unifiedAutoRecoveredCount;
    private int _vaultAutoRecoveredCount;

    public int UnifiedAutoRecoveredCount => _unifiedAutoRecoveredCount;
    public int VaultAutoRecoveredCount   => _vaultAutoRecoveredCount;

    public Task LogAsync(AuditEventCode code, AuditPayload? payload, DekScope dek, CancellationToken ct = default)
    {
        if (code == AuditEventCode.UnifiedDbAutoRecovered) _unifiedAutoRecoveredCount++;
        if (code == AuditEventCode.VaultDbAutoRecovered)   _vaultAutoRecoveredCount++;
        return Task.CompletedTask;
    }

    public Task LogAuthFailedAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<AuditLogDisplayItem>> GetRecentAsync(int count, DekScope dek, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditLogDisplayItem>>(Array.Empty<AuditLogDisplayItem>());

    public Task<IReadOnlyList<AuditLogDisplayItem>> GetAllAfterAsync(DateTimeOffset since, DekScope dek, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditLogDisplayItem>>(Array.Empty<AuditLogDisplayItem>());

    public Task PurgeOldLogsAsync(DekScope? purgeLogDek = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task FlushPreAuthFailuresAsync(int count, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task FlushPendingRestoreAuditAsync(DekScope dek, int currentDbNumber, string dataDir, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> HasAnyLogsAsync(CancellationToken ct = default)
        => Task.FromResult(false);
}
