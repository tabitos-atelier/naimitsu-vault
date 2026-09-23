// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services;
using NaimitsuVault.Tests.Stubs;
using Windows.Security.Credentials.UI;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage for the 2026-09-17 fix:
///
/// TC-WH-26: DisableWindowsHelloAsync() must remove only the current vault's VaultDEKHello and must
///           never delete the unified DB's KSharedHello. Before the fix, an unconditional deletion
///           (guarded only by a stale decoy-vault check, `CurrentVaultDbNumber != 0`, that is true for
///           every normal vault 1-3) meant disabling Hello on one vault silently broke Hello unlock for
///           every other vault sharing the same K_shared.
///
/// TC-WH-27: IsWindowsHelloSupportedAsync() must read from the injected _helloAdapter, not the real
///           WinRT UserConsentVerifier static class directly - otherwise the adapter injection point
///           (SetHelloAdapter) exists but this method can never be unit tested.
///
/// TC-WH-28: ResetMasterPasswordWithHelloAsync() must go through _helloAdapter / _protectedData end to
///           end. Before the fix, this method called UserConsentVerifier and ProtectedData directly and
///           also depended on App.UiDispatcherQueue (null outside a running WinUI 3 App), so calling it
///           from a test host threw a NullReferenceException before any assertion could run.
/// </summary>
public sealed class WindowsHelloDisableMultiVaultTests
{
    private static (TestUnifiedDb unifiedDb, AppSession session, AuthService auth,
                    FakeUserConsentVerifierAdapter adapter, FakeProtectedDataService dpapi)
        CreateAuth(IDbContextFactory<AppDbContext> vaultFactory)
    {
        var unifiedDb = TestUnifiedDb.Create();
        var session   = new AppSession();
        var crypto    = new IdentityCryptoService();
        var adapter   = new FakeUserConsentVerifierAdapter();
        var dpapi     = new FakeProtectedDataService();
        var auth = new AuthService(
            vaultFactory, unifiedDb.Factory,
            new NullConnectionProvider(), crypto, session,
            new StubAuditLogService(),
            null!,
            NullLogger<AuthService>.Instance);
        auth.SetHelloAdapter(adapter);
        auth.SetProtectedData(dpapi);
        return (unifiedDb, session, auth, adapter, dpapi);
    }

    // ── TC-WH-26 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DisableWindowsHello_RemovesOnlyOwnVaultDEKHello_LeavesKSharedHelloForOtherVaults()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vaultDb = TestDb.Create();
        var (unifiedDb, session, auth, _, _) = CreateAuth(vaultDb.Factory);
        using (unifiedDb)
        {
            session.CurrentVaultDbNumber = 1;

            await using (var uctx = await unifiedDb.Factory.CreateDbContextAsync(ct))
            {
                var kShared = await WinHelloDbSeeder.SeedUnifiedDbForHelloAsync(uctx);
                CryptographicOperations.ZeroMemory(kShared);
            }

            await using (var vctx = await vaultDb.Factory.CreateDbContextAsync(ct))
            {
                var dek = await WinHelloDbSeeder.SeedWrappedDekAsync(vctx);
                CryptographicOperations.ZeroMemory(dek);
            }

            await auth.DisableWindowsHelloAsync();

            await using (var vctx = await vaultDb.Factory.CreateDbContextAsync(ct))
            {
                bool vaultHelloStillPresent =
                    await vctx.Metadata.AnyAsync(s => s.ConfigKey == VaultMetadataKey.VaultDEKHello, ct);
                Assert.False(vaultHelloStillPresent); // this vault's own Hello key is removed
            }

            await using (var uctx = await unifiedDb.Factory.CreateDbContextAsync(ct))
            {
                bool kSharedHelloStillPresent =
                    await uctx.Metadata.AnyAsync(s => s.ConfigKey == UnifiedMetadataKey.KSharedHello, ct);
                // Other vaults may still depend on KSharedHello - must not be touched here.
                Assert.True(kSharedHelloStillPresent);
            }
        }
    }

    // ── TC-WH-27 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task IsWindowsHelloSupportedAsync_ReflectsInjectedAdapter_NotRealWinRTApi()
    {
        using var vaultDb = TestDb.Create();
        var (unifiedDb, _, auth, adapter, _) = CreateAuth(vaultDb.Factory);
        using (unifiedDb)
        {
            adapter.AvailabilityResult = UserConsentVerifierAvailability.DeviceNotPresent;
            Assert.False(await auth.IsWindowsHelloSupportedAsync());

            adapter.AvailabilityResult = UserConsentVerifierAvailability.Available;
            Assert.True(await auth.IsWindowsHelloSupportedAsync());
        }
    }

    // ── TC-WH-28 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetMasterPasswordWithHelloAsync_SucceedsViaInjectedAdapterAndDpapi_NoRealApiTouched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var vaultDb = TestDb.Create();
        var (unifiedDb, _, auth, adapter, _) = CreateAuth(vaultDb.Factory);
        using (unifiedDb)
        {
            adapter.AvailabilityResult = UserConsentVerifierAvailability.Available;
            adapter.VerificationResult = UserConsentVerificationResult.Verified;

            var dek = new byte[32];
            RandomNumberGenerator.Fill(dek);
            var dekCopyForAssert = dek.ToArray();

            await using (var vctx = await vaultDb.Factory.CreateDbContextAsync(ct))
            {
                // FakeProtectedDataService is an identity transform, so the "DPAPI-protected" blob is
                // just the raw DEK, base64-encoded (same convention as WinHelloDbSeeder.SeedWrappedDekAsync).
                vctx.Metadata.Add(new VaultMetadata
                {
                    ConfigKey   = VaultMetadataKey.VaultDEKHello,
                    ConfigValue = Encoding.UTF8.GetBytes(Convert.ToBase64String(dek)),
                });
                // A placeholder Auth row must pre-exist: ResetMasterPasswordWithHelloAsyncCore only
                // overwrites an existing row, it does not insert a new one.
                vctx.Metadata.Add(new VaultMetadata
                {
                    ConfigKey   = VaultMetadataKey.Auth,
                    ConfigValue = Encoding.UTF8.GetBytes(
                        """{"Salt":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","WrappedDek":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}"""),
                });
                await vctx.SaveChangesAsync(ct);
            }
            CryptographicOperations.ZeroMemory(dek);

            // Before the fix, this call reached UserConsentVerifier/ProtectedData directly and touched
            // App.UiDispatcherQueue (null in a test host), throwing a NullReferenceException here
            // instead of returning a result.
            var result = await auth.ResetMasterPasswordWithHelloAsync("new-master-password-99".AsSpan());
            Assert.True(result);

            await using var finalCtx = await vaultDb.Factory.CreateDbContextAsync(ct);
            var authSetting = await finalCtx.Metadata.FirstAsync(s => s.ConfigKey == VaultMetadataKey.Auth, ct);
            using var authDoc = System.Text.Json.JsonDocument.Parse(authSetting.ConfigValue);
            var newWrappedDek = Convert.FromBase64String(authDoc.RootElement.GetProperty("WrappedDek").GetString()!);
            // IdentityCryptoService.Encrypt prefixes a HeaderSize-byte dummy header and appends the
            // plaintext unchanged, so stripping it must recover the original DEK bytes - proving the
            // re-wrap pipeline (DPAPI-unwrap via _protectedData -> re-encrypt -> persist) actually ran.
            Assert.Equal(dekCopyForAssert, newWrappedDek[IdentityCryptoService.HeaderSize..]);
        }
    }
}
