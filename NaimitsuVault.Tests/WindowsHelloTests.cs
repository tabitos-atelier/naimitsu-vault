// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Windows Hello integration" tests (ViewModel layer).
///
/// TC-WH-01..14 are already used by WindowsHelloStateTransitionTests / WindowsHelloDekMemoryTests
/// (AuthService layer), so the TC-WHV prefix is used here to indicate the ViewModel layer.
///
/// TC-WHV-01: MasterAuthResult.FromHello() returns IsHelloVerified=true, PasswordChars=null.
/// TC-WHV-02: MasterAuthResult(SecureCharBuffer) returns IsHelloVerified=false.
/// TC-WHV-03: When ChangeHelloSettingAsync's EnableWindowsHelloAsync fails,
///             IsWindowsHelloEnabled is rolled back to false.
/// TC-WHV-04: When ChangeHelloSettingAsync's DisableWindowsHelloAsync fails,
///             IsWindowsHelloEnabled is rolled back to true.
/// </summary>
public sealed class WindowsHelloTests
{
    // ── Stubs ───────────────────────────────────────────────────────────────

    private sealed class NullNotificationService : IAppNotificationService
    {
        public void Show(int _, int __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
        public void Show(int _, string __, NotificationSeverity ___ = default, TimeSpan? ____ = null) { }
    }

    private static AppSettingsViewModel BuildVm(IAuthService? auth = null) => new(
        factory:           null!,
        auth:              auth!,
        session:           null!,
        themeService:      null!,
        fontResource:      null!,
        localization:      null!,
        autoLock:          null!,
        favicon:           null!,
        captureProtection: null!,
        autoBackup:        null!,
        notification:      new NullNotificationService(),
        dialog:            null!,
        filePicker:        null!,
        auditLog:          null!,
        logger:            NullLogger<AppSettingsViewModel>.Instance);

    // ── TC-WHV-01 ───────────────────────────────────────────────────────────

    [Fact]
    public void MasterAuthResult_FromHello_IsHelloVerifiedTrueAndPasswordCharsNull()
    {
        using var result = MasterAuthResult.FromHello();

        Assert.True(result.IsHelloVerified);
        Assert.Null(result.PasswordChars);
    }

    // ── TC-WHV-02 ───────────────────────────────────────────────────────────

    [Fact]
    public void MasterAuthResult_PasswordCtor_IsHelloVerifiedFalseAndPasswordCharsSet()
    {
        var buf = new SecureCharBuffer();
        buf.SetFromSpan("password".AsSpan());
        using var result = new MasterAuthResult(buf);

        Assert.False(result.IsHelloVerified);
        Assert.NotNull(result.PasswordChars);
        Assert.False(result.PasswordChars!.IsEmpty);
    }

    // ── TC-WHV-03 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeHelloSetting_EnableWhenAuthThrows_RollsBackIsWindowsHelloEnabledToFalse()
    {
        // Arrange - ThrowOnHello=true, so EnableWindowsHelloAsync() throws
        var vm = BuildVm(auth: new StubAuthService { ThrowOnHello = true });
        vm.IsWindowsHelloEnabled = true; // intends to enable

        // Act - exception -> catch -> IsWindowsHelloEnabled = !true = false
        await vm.ChangeHelloSettingCommand.ExecuteAsync(null);

        // Assert - rolled back to false
        Assert.False(vm.IsWindowsHelloEnabled);
    }

    // ── TC-WHV-04 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeHelloSetting_DisableWhenAuthThrows_RollsBackIsWindowsHelloEnabledToTrue()
    {
        // Arrange - ThrowOnHello=true, so DisableWindowsHelloAsync() throws
        var vm = BuildVm(auth: new StubAuthService { ThrowOnHello = true });
        // vm.IsWindowsHelloEnabled starts at its default false (intends to disable)

        // Act - exception -> catch -> IsWindowsHelloEnabled = !false = true
        await vm.ChangeHelloSettingCommand.ExecuteAsync(null);

        // Assert - rolled back to true
        Assert.True(vm.IsWindowsHelloEnabled);
    }
}
