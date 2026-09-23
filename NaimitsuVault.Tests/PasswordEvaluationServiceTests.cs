// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// PasswordEvaluationService test suite (TC-PE-01 .. TC-PE-12).
/// Creates Password BLOBs with IdentityCryptoService (identity cipher) and verifies the real PasswordEvaluationService.
/// </summary>
public sealed class PasswordEvaluationServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PasswordEvaluationService MakeSvc(SecretRepository repo)
        => new(repo, new IdentityCryptoService(), NullLogger<PasswordEvaluationService>.Instance);

    private static SecretRepository MakeRepo(TestDb db)
        => new(db.Factory);

    private static DekScope MakeDek() => new(new byte[32], 32);

    /// <summary>Generates a Password BLOB in IdentityCryptoService format (28-byte dummy header + plaintext).</summary>
    private static byte[] MakePwBlob(string plaintext)
    {
        var crypto = new IdentityCryptoService();
        return crypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), new byte[32]);
    }

    /// <summary>Generates a title BLOB in IdentityCryptoService format.</summary>
    private static byte[] MakeTitleBlob(string title) => MakePwBlob(title);

    private static async Task<int> InsertSecretAsync(TestDb db, string title, string? password,
        DateTime? expiresAt = null, DateTime? updateAt = null)
    {
        await using var ctx = db.Factory.CreateDbContext();
        var s = new Secret
        {
            Title     = MakeTitleBlob(title),
            Password  = password != null ? MakePwBlob(password) : null,
            ExpiresAt = expiresAt,
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = updateAt ?? DateTime.UtcNow,
        };
        ctx.Secrets.Add(s);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return s.Id;
    }

    // ── TC-PE-01: score for a strong password ────────────────────────────────────

    [Fact]
    public void EvaluateStrength_StrongPassphrase_ScoreAtLeast3()
    {
        var svc = MakeSvc(null!);
        Assert.True(svc.EvaluateStrength("correct-horse-battery-staple") >= 3);
    }

    // ── TC-PE-02: score for the weakest password ─────────────────────────────────────

    [Fact]
    public void EvaluateStrength_CommonPassword_ScoreAtMost1()
    {
        var svc = MakeSvc(null!);
        Assert.True(svc.EvaluateStrength("password") <= 1);
    }

    // ── TC-PE-03: empty-string score (no exception) ────────────────────────────────────

    [Fact]
    public void EvaluateStrength_EmptyString_Returns0WithoutException()
    {
        var svc   = MakeSvc(null!);
        var score = svc.EvaluateStrength(string.Empty);
        Assert.Equal(0, score);
    }

    // ── TC-PE-04: choke-point relay: title only, no plaintext field exists ─────

    [Fact]
    public async Task ScanAllSecretsAsync_WeakItems_ContainsTitleStringAndScore()
    {
        using var db   = TestDb.Create();
        var repo       = MakeRepo(db);
        var svc        = MakeSvc(repo);
        var dek        = MakeDek();

        await InsertSecretAsync(db, "MyAccount", "password"); // score <= 1 -> weak

        var result = await svc.ScanAllSecretsAsync(dek, TestContext.Current.CancellationToken);

        Assert.True(result.WeakPasswordCount > 0);
        var item = Assert.Single(result.WeakItems);
        Assert.Equal("MyAccount", item.Title);
        Assert.NotNull(item.StrengthScore);
        Assert.True(item.StrengthScore <= 2);
    }

    // ── TC-PE-05: duplicate detection - 2 secrets with the same password -> ReusedPasswordCount == 1 ────

    [Fact]
    public async Task ScanAllSecretsAsync_TwoSecretsWithSamePassword_ReusedCount1()
    {
        using var db = TestDb.Create();
        var svc      = MakeSvc(MakeRepo(db));
        var dek      = MakeDek();

        await InsertSecretAsync(db, "A", "shared_pw_alpha");
        await InsertSecretAsync(db, "B", "shared_pw_alpha");

        var result = await svc.ScanAllSecretsAsync(dek, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.ReusedPasswordCount);
    }

    // ── TC-PE-06: duplicate detection - 2 groups -> ReusedPasswordCount == 2 ────────────

    [Fact]
    public async Task ScanAllSecretsAsync_TwoGroupsOfReused_ReusedCount2()
    {
        using var db = TestDb.Create();
        var svc      = MakeSvc(MakeRepo(db));
        var dek      = MakeDek();

        await InsertSecretAsync(db, "A", "pw_alpha");
        await InsertSecretAsync(db, "B", "pw_alpha");
        await InsertSecretAsync(db, "C", "pw_beta");
        await InsertSecretAsync(db, "D", "pw_beta");

        var result = await svc.ScanAllSecretsAsync(dek, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.ReusedPasswordCount);
    }

    // ── TC-PE-07: duplicate detection - 3 secrets with the same password -> ReusedPasswordCount == 1 ──

    [Fact]
    public async Task ScanAllSecretsAsync_ThreeSecretsWithSamePassword_ReusedCount1()
    {
        using var db = TestDb.Create();
        var svc      = MakeSvc(MakeRepo(db));
        var dek      = MakeDek();

        await InsertSecretAsync(db, "X", "triple_pw");
        await InsertSecretAsync(db, "Y", "triple_pw");
        await InsertSecretAsync(db, "Z", "triple_pw");

        var result = await svc.ScanAllSecretsAsync(dek, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.ReusedPasswordCount);
    }

    // ── TC-PE-08: no duplicates ────────────────────────────────────────────────────

    [Fact]
    public async Task ScanAllSecretsAsync_AllUniquePws_ReusedCount0()
    {
        using var db = TestDb.Create();
        var svc      = MakeSvc(MakeRepo(db));
        var dek      = MakeDek();

        await InsertSecretAsync(db, "A", "pw_uno");
        await InsertSecretAsync(db, "B", "pw_dos");
        await InsertSecretAsync(db, "C", "pw_tres");
        await InsertSecretAsync(db, "D", "pw_cuatro");
        await InsertSecretAsync(db, "E", "pw_cinco");

        var result = await svc.ScanAllSecretsAsync(dek, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ReusedPasswordCount);
    }

    // TC-PE-09 (verification of the expiration count ExpiredSecretsCount) is a retired/skipped number.
    // Expiration diagnostics were reorganized out of DashboardScanResult's scope (now managed in a
    // separate section), and the ExpiredSecretsCount property itself was removed, so the corresponding
    // test was removed as well.

    // TC-PE-10 (stale count) is a retired/skipped number. The "not updated in a long time" check was
    // removed entirely (2026-08-05): UpdatedAt is bumped by any edit, not just a password change, so it
    // never actually measured password age, and mandating periodic password rotation is no longer
    // recommended practice (NIST SP 800-63B). StaleSecretsCount/StaleItems no longer exist.

    // ── TC-PE-11: excludes records with no password set ──────────────────────────────────────

    [Fact]
    public async Task ScanAllSecretsAsync_NullPasswordRecords_ExcludedFromWeakAndReused()
    {
        using var db = TestDb.Create();
        var svc      = MakeSvc(MakeRepo(db));
        var dek      = MakeDek();

        await InsertSecretAsync(db, "N1", password: null);
        await InsertSecretAsync(db, "N2", password: null);
        await InsertSecretAsync(db, "N3", password: null);

        var result = await svc.ScanAllSecretsAsync(dek, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.WeakPasswordCount);
        Assert.Equal(0, result.ReusedPasswordCount);
    }

    // ── TC-PE-12: CancellationToken propagation ────────────────────────────────────

    [Fact]
    public async Task ScanAllSecretsAsync_CancelledToken_ThrowsOperationCancelled()
    {
        using var db  = TestDb.Create();
        var svc       = MakeSvc(MakeRepo(db));
        var dek       = MakeDek();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.ScanAllSecretsAsync(dek, cts.Token));
    }
}
