// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Windows.System;

namespace NaimitsuVault.Pages;

public sealed partial class AboutPage : Page
{
    public AboutViewModel ViewModel { get; }

    private readonly IAppNotificationService _notification;

    public AboutPage()
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<AboutViewModel>();
        _notification = ((App)Application.Current).Services.GetRequiredService<IAppNotificationService>();
        InitializeComponent();
    }

    private async void OpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { Tag: string url }) return;
        try
        {
            await Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            _notification.Show(LK.Common_Error,
                string.Format(LocalizationManager.GetById(LK.Common_ErrorBrowserOpenFailed), ex.GetType().Name),
                NotificationSeverity.Error);
        }
    }
}
