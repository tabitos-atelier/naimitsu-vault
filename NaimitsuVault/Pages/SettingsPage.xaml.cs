// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;
using NaimitsuVault.Views;
using NaimitsuVault.Views.Controls;

namespace NaimitsuVault.Pages;

public sealed partial class SettingsPage : Page
{
    public AppSettingsViewModel AppSettingsVm { get; }
    public VaultOperationsViewModel VaultOpsVm { get; }
    private bool _isFirstLoad = true;

    private readonly AppCommonSettingsControl _appCommonControl;
    private readonly VaultSettingsControl _vaultControl;

    public SettingsPage()
    {
        var app = (App)Application.Current;
        AppSettingsVm = app.Services.GetRequiredService<AppSettingsViewModel>();
        VaultOpsVm    = app.ShellScopeServices!.GetRequiredService<VaultOperationsViewModel>();
        InitializeComponent();

        var session = app.Services.GetRequiredService<AppSession>();
        NavItemVault.Content = string.Format(LocalizationManager.Get("VaultSettings.TabTitleFormat"), session.DisplayedVaultNumber);

        // Pass both controls the same instances SettingsPage has already resolved (do not re-resolve or new one up).
        _appCommonControl = new AppCommonSettingsControl(AppSettingsVm, VaultOpsVm) { Visibility = Visibility.Visible };
        _vaultControl     = new VaultSettingsControl(AppSettingsVm, VaultOpsVm) { Visibility = Visibility.Collapsed };
        ContentHost.Children.Add(_appCommonControl);
        ContentHost.Children.Add(_vaultControl);

        Loaded   += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        bool isVault = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string == "vault";
        _appCommonControl.Visibility = isVault ? Visibility.Collapsed : Visibility.Visible;
        _vaultControl.Visibility     = isVault ? Visibility.Visible   : Visibility.Collapsed;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        VaultOpsVm.RunWithBackupProgressAsync = RunWithBackupProgressAsync;
        if (_isFirstLoad)
        {
            _isFirstLoad = false;
            await AppSettingsVm.LoadAsync();
        }
        // Unlike AppSettingsVm (app-global settings, unaffected by other pages), VaultOpsVm.HasSecrets/
        // AvailableVaultLabels/HasEmergencyAccessCode reflect vault DB state that SecretsPage etc. can
        // change while this page (NavigationCacheMode=Required) sits cached off-screen. Gating this
        // behind _isFirstLoad left HasSecrets stuck at its first-visit value forever, disabling the
        // backup/export buttons even after secrets existed.
        await VaultOpsVm.LoadAsync();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        VaultOpsVm.RunWithBackupProgressAsync = null;
    }

    /// <summary>
    /// Controls the BackupProgressWindow's modal display.
    /// Blocks ShellWindow's input and runs work() while BackupProgressWindow is pinned to the foreground.
    /// </summary>
    private async Task RunWithBackupProgressAsync(string message, Func<Task> work)
    {
        ShellWindow? shell = null;
        BackupProgressWindow? progressWin = null;
        try
        {
            shell = ((App)Application.Current).ActiveShellWindow as ShellWindow;
            shell?.SetInputEnabled(false);

            progressWin = new BackupProgressWindow(message);
            progressWin.Activate();

            await work();
        }
        finally
        {
            progressWin?.ForceClose();
            shell?.SetInputEnabled(true);
        }
    }
}
