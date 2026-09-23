// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NaimitsuVault.ViewModels;
using WinUIEx;

namespace NaimitsuVault.Views;

/// <summary>
/// Shared "operation in progress" window for bulk backup/plaintext export cleanup.
/// Has no standard control box and always cancels AppWindow.Closing, physically
/// blocking an unintended close by the user. The program can only close it
/// via <see cref="ForceClose"/>.
/// </summary>
public sealed partial class BackupProgressWindow : WindowEx
{
    public BackupProgressWindow(string message)
    {
        InitializeComponent();
        ApplyCurrentTheme();
        ApplyCurrentFont();

        MessageText.Text = message;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CustomTitleBar);
        AppWindow.SetIcon("Assets/NaimitsuVault.ico");
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable   = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.Closing += OnClosingBlocked;

        this.SetWindowSize(380, 180);
        this.CenterOnScreen();
    }

    /// <summary>Dynamically swaps the message based on the caller's context (backup/export).</summary>
    public void UpdateMessage(string message) => MessageText.Text = message;

    private void OnClosingBlocked(AppWindow sender, AppWindowClosingEventArgs args) => args.Cancel = true;

    /// <summary>The official close path, callable only by the program itself after processing completes.</summary>
    public void ForceClose()
    {
        AppWindow.Closing -= OnClosingBlocked;
        WeakReferenceMessenger.Default.Unregister<FontFamilyChangedMessage>(this);
        Close();
    }

    private void ApplyCurrentTheme()
    {
        var settingsVm = ((App)Application.Current).Services.GetRequiredService<AppSettingsViewModel>();
        var theme = settingsVm.ThemeMode switch
        {
            "Dark"  => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _       => ElementTheme.Default,
        };
        if (Content is FrameworkElement root)
            root.RequestedTheme = theme;
    }

    private void ApplyCurrentFont()
    {
        if (Content is not FrameworkElement root) return;
        var settingsVm = ((App)Application.Current).Services.GetRequiredService<AppSettingsViewModel>();
        ApplyFontFamily(root, settingsVm.FontFamily);
        WeakReferenceMessenger.Default.Register<FontFamilyChangedMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Content is FrameworkElement r) ApplyFontFamily(r, msg.FontFamily);
            }));
    }

    // FontFamily is only a first-class property on Control/TextBlock, not on FrameworkElement (Content
    // here is a plain Grid). Set the underlying inheritable DependencyProperty directly via SetValue so
    // it still cascades down to descendant Controls that don't already set FontFamily locally/via Style.
    private static void ApplyFontFamily(FrameworkElement root, string? fontFamily)
    {
        if (string.IsNullOrEmpty(fontFamily))
            root.ClearValue(Control.FontFamilyProperty);
        else
            root.SetValue(Control.FontFamilyProperty, new FontFamily(fontFamily));

        // Controls whose default Style sets FontFamily from {ThemeResource ContentControlThemeFontFamily}
        // (already updated by FontResourceService) won't re-evaluate from a raw dictionary edit alone -
        // ThemeResource only re-resolves on an actual theme change. Toggle-and-restore synchronously
        // (collapses into a single composited frame, no visible flicker) to force that re-evaluation.
        var current = root.RequestedTheme;
        root.RequestedTheme = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = current;
    }
}
