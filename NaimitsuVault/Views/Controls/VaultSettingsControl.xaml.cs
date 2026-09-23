// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Helpers;
using NaimitsuVault.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace NaimitsuVault.Views.Controls;

public sealed partial class VaultSettingsControl : UserControl
{
    public AppSettingsViewModel AppSettingsVm { get; }
    public VaultOperationsViewModel VaultOpsVm { get; }

    public VaultSettingsControl(AppSettingsViewModel appSettingsVm, VaultOperationsViewModel vaultOpsVm)
    {
        AppSettingsVm = appSettingsVm;
        VaultOpsVm    = vaultOpsVm;
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VaultOpsVm.PasswordChangeSucceeded    += OnPasswordChangeSucceeded;
        VaultOpsVm.EmergencyAccessCodeCreated += OnEmergencyAccessCodeCreated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VaultOpsVm.PasswordChangeSucceeded    -= OnPasswordChangeSucceeded;
        VaultOpsVm.EmergencyAccessCodeCreated -= OnEmergencyAccessCodeCreated;

        // VaultOpsVm is a scoped instance shared for the whole unlocked session (disposed only on
        // Lock), so an unconfirmed password/PIN entry left here would otherwise survive in its POH
        // buffers for as long as the vault stays unlocked. Scrub on tab-away, same as the confirmed path.
        OldPasswordBox.Password     = "";
        NewPasswordBox.Password     = "";
        ConfirmPasswordBox.Password = "";
        EacPinBox.Password          = "";
        EacPinConfirmBox.Password   = "";
        VaultOpsVm.ClearSensitiveInputBuffers();
    }

    /// <summary>Delegate for x:Bind. The actual decision logic is centralized in <see cref="SettingsUiHelper.BothIdle"/>.</summary>
    public bool BothIdle(bool vaultBusy, bool appBusy) => SettingsUiHelper.BothIdle(vaultBusy, appBusy);

    /// <summary>Delegate for x:Bind, combining AppSettingsVm.IsWindowsHelloEnabled with VaultOpsVm's field-length check.</summary>
    public bool CanResetWithHello(bool isHelloEnabled, bool fieldsValid) => isHelloEnabled && fieldsValid;

    // Windows Hello is a property belonging to AppSettingsViewModel (Singleton), but since the
    // actual data is stored in the vault DB's VaultDEKHello, it's placed on this tab (vault settings).
    //
    // IsOn is bound OneWay (VM -> UI only), so there is no write-back racing this handler. That
    // makes the equality guard below safe: ToggleSwitch fires Toggled even when the change comes
    // from the binding itself (page shown, vault switched, error rollback), and without the guard
    // every visit to this tab would re-run EnableWindowsHelloAsync / DisableWindowsHelloAsync,
    // show the "saved" notification and write an audit log entry. A real user toggle always
    // differs from the VM value, so it passes through.
    private void WindowsHello_Toggled(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleSwitch)sender;
        if (toggle.IsOn == AppSettingsVm.IsWindowsHelloEnabled) return;

        AppSettingsVm.IsWindowsHelloEnabled = toggle.IsOn;
        _ = AppSettingsVm.ChangeHelloSettingCommand.ExecuteAsync(null);
    }

    private void OnPasswordChangeSucceeded(object? sender, EventArgs e)
    {
        OldPasswordBox.Password     = "";
        NewPasswordBox.Password     = "";
        ConfirmPasswordBox.Password = "";
    }

    // PasswordBox.Password allocates a string, but the VM setter immediately transcribes it into
    // a POH-pinned buffer via SetFromSpan and zero-clears the string's internal buffer via ZeroStringInternals.
    private void OldPassword_Changed(object sender, RoutedEventArgs e)
        => VaultOpsVm.OldPassword = OldPasswordBox.Password;

    private void NewPassword_Changed(object sender, RoutedEventArgs e)
        => VaultOpsVm.NewPassword = NewPasswordBox.Password;

    private void ConfirmPassword_Changed(object sender, RoutedEventArgs e)
        => VaultOpsVm.ConfirmNewPassword = ConfirmPasswordBox.Password;

    // Ctrl+H toggles all three fields together so a mismatch (e.g. a stray key) is visible at a glance.
    private void MasterPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
        var mode = OldPasswordBox.PasswordRevealMode == PasswordRevealMode.Visible
            ? PasswordRevealMode.Hidden
            : PasswordRevealMode.Visible;
        OldPasswordBox.PasswordRevealMode     = mode;
        NewPasswordBox.PasswordRevealMode     = mode;
        ConfirmPasswordBox.PasswordRevealMode = mode;
        e.Handled = true;
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    // Ctrl+H toggles both EAC PIN fields together, mirroring MasterPasswordBox_KeyDown above.
    private void EacPinBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.H || !IsCtrlDown()) return;
        var mode = EacPinBox.PasswordRevealMode == PasswordRevealMode.Visible
            ? PasswordRevealMode.Hidden
            : PasswordRevealMode.Visible;
        EacPinBox.PasswordRevealMode        = mode;
        EacPinConfirmBox.PasswordRevealMode = mode;
        e.Handled = true;
    }

    private void EacPinBox_PasswordChanged(object sender, RoutedEventArgs e)
        => VaultOpsVm.EacPin = EacPinBox.Password;

    private void EacPinConfirmBox_PasswordChanged(object sender, RoutedEventArgs e)
        => VaultOpsVm.EacPinConfirm = EacPinConfirmBox.Password;

    private void OnEmergencyAccessCodeCreated(object? sender, EventArgs e)
    {
        EacPinBox.Password        = "";
        EacPinConfirmBox.Password = "";
    }
}
