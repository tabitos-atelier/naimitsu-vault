// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Windows.System;

namespace NaimitsuVault.Pages;

public sealed partial class AuditLogPage : Page, ISearchFocusable, IPageActivationAware
{
    public AuditLogViewModel ViewModel { get; }

    public void FocusSearchBox() => SearchBox.Focus(FocusState.Programmatic);

    public AuditLogPage()
    {
        var app = (App)Application.Current;
        ViewModel = app.ShellScopeServices!.GetRequiredService<AuditLogViewModel>();
        InitializeComponent();
        Loaded += AuditLogPage_Loaded;
    }

    // Loaded re-fires every time the cached page is put back into NavFrame.Content, so reload only when
    // an audit entry was written since the last load (NeedsReload starts true, so the first visit loads).
    private async void AuditLogPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NeedsReload)
            await ViewModel.LoadAsync();
    }

    // ShellWindow assigns NavFrame.Content directly (never Frame.Navigate), so OnNavigatedTo/OnNavigatedFrom
    // never fire - it calls these explicitly at the swap point instead.
    void IPageActivationAware.Activated()   => ViewModel.Resume();
    void IPageActivationAware.Deactivated() => ViewModel.Pause();

    // J/K vi-style nav, same key convention as the Secrets/TimeMachine/Gallery/Categories item lists.
    private void AuditLogListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.J:
                if (AuditLogListView.SelectedIndex < AuditLogListView.Items.Count - 1)
                {
                    AuditLogListView.SelectedIndex++;
                    AuditLogListView.ScrollIntoView(AuditLogListView.SelectedItem);
                }
                e.Handled = true;
                break;
            case VirtualKey.K:
                if (AuditLogListView.SelectedIndex > 0)
                {
                    AuditLogListView.SelectedIndex--;
                    AuditLogListView.ScrollIntoView(AuditLogListView.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }
}
