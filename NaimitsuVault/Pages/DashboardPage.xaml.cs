// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Common;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Windows.System;

namespace NaimitsuVault.Pages;

public sealed partial class DashboardPage : Page, IPageActivationAware
{
    public DashboardViewModel ViewModel { get; }

    // Tracks the delta of DB integrity warnings already sent to the title bar (prevents duplicate sends on reload)
    private int _lastSentDbIntegrityDelta = 0;

    public DashboardPage(DashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += DashboardPage_Loaded;
    }

    private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadBasicStatsAsync();

        // Notify the title bar of the DB integrity warning increment
        int delta     = (ViewModel.UnifiedDbIntegrityWarning ? 1 : 0)
                      + (ViewModel.VaultDbIntegrityWarning   ? 1 : 0);
        int sendDelta = delta - _lastSentDbIntegrityDelta;
        if (sendDelta > 0)
        {
            WeakReferenceMessenger.Default.Send(new DbIntegrityWarningMessage(sendDelta));
            _lastSentDbIntegrityDelta = delta;
        }

        await ViewModel.TryRunInitialScanAsync();
    }

    // ShellWindow assigns NavFrame.Content directly (never Frame.Navigate), so OnNavigatedTo/OnNavigatedFrom
    // never fire - it calls these explicitly at the swap point instead.
    void IPageActivationAware.Activated()   => ViewModel.Resume();
    void IPageActivationAware.Deactivated() => ViewModel.Pause();

    // J/K vi-style nav, same key convention as the Secrets/TimeMachine/Gallery/Categories item lists.
    private void RecentActivityListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.J:
                if (RecentActivityListView.SelectedIndex < RecentActivityListView.Items.Count - 1)
                {
                    RecentActivityListView.SelectedIndex++;
                    RecentActivityListView.ScrollIntoView(RecentActivityListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.K:
                if (RecentActivityListView.SelectedIndex > 0)
                {
                    RecentActivityListView.SelectedIndex--;
                    RecentActivityListView.ScrollIntoView(RecentActivityListView.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }

    // J/K vi-style nav + Enter to jump to the underlying secret/gallery-file/profile section.
    private void ExpiryAlertListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.J:
                if (ExpiryAlertListView.SelectedIndex < ExpiryAlertListView.Items.Count - 1)
                {
                    ExpiryAlertListView.SelectedIndex++;
                    ExpiryAlertListView.ScrollIntoView(ExpiryAlertListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.K:
                if (ExpiryAlertListView.SelectedIndex > 0)
                {
                    ExpiryAlertListView.SelectedIndex--;
                    ExpiryAlertListView.ScrollIntoView(ExpiryAlertListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (ExpiryAlertListView.SelectedItem is ExpiryAlertItem item)
                    ViewModel.NavigateToExpiryItemCommand.Execute(item);
                e.Handled = true;
                break;
        }
    }

    // J/K vi-style nav + Enter to jump to the underlying secret.
    private void SecurityScanListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.J:
                if (SecurityScanListView.SelectedIndex < SecurityScanListView.Items.Count - 1)
                {
                    SecurityScanListView.SelectedIndex++;
                    SecurityScanListView.ScrollIntoView(SecurityScanListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.K:
                if (SecurityScanListView.SelectedIndex > 0)
                {
                    SecurityScanListView.SelectedIndex--;
                    SecurityScanListView.ScrollIntoView(SecurityScanListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (SecurityScanListView.SelectedItem is SecurityScanRowItem item)
                    ViewModel.NavigateToSecurityRowCommand.Execute(item);
                e.Handled = true;
                break;
        }
    }
}
