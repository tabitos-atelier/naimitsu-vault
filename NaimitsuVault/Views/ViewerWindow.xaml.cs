// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Microsoft.Extensions.Logging;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;
using WinUIEx;

namespace NaimitsuVault.Views;

public sealed partial class ViewerWindow : WindowEx
{
    private static readonly ILogger<ViewerWindow> Logger = AppLog.For<ViewerWindow>();

    public ViewerViewModel ViewModel { get; }

    // Preset zoom levels for the single ZoomComboBox (image/PDF: percent; text: point size).
    // "Auto" fits the page/image to the viewport (FitToWindow / ApplyPdfFitToWidth).
    private static readonly string[] _imageZoomLevels = ["Auto", "500%", "300%", "200%", "150%", "100%", "75%", "50%", "25%", "10%"];
    private static readonly string[] _textZoomLevels   = ["28pt", "24pt", "20pt", "18pt", "16pt", "14pt", "12pt", "10pt", "8pt"];

    private float  _zoomFactor = 1f;
    private double _textFontSize = 14.0;
    private bool   _textMode;
    // Guards ZoomComboBox_SelectionChanged while a selection is set programmatically (to reflect a
    // drag/wheel zoom or a keyboard Ctrl+/- step), so it doesn't re-apply the zoom it's only echoing.
    private bool _suppressZoomComboSelection;
    // True from the moment "Auto" is selected (explicitly, or as ResetAllPanels' default for a newly
    // opened image/PDF) until a concrete preset/step replaces it, or the user zooms directly on the
    // ScrollViewer (Ctrl+wheel/pinch, detected via _zoomTracker in SyncImageZoomCombo). While true, the
    // view is re-fitted whenever the viewer is resized. A fit-to-window/width zoom is
    // essentially never an exact preset percent, so SyncImageZoomCombo can't tell "Auto" apart from
    // "no matching preset" by the resulting zoom value alone - this flag is the source of truth
    // instead, checked every time ViewChanged fires (which, for ChangeView, can land on a later
    // dispatcher tick well after the call that triggered it returns, so a transient suppress-flag
    // window around that one call isn't enough to keep the selection pinned on "Auto").
    private bool _zoomIsAuto;

    // Separates zoom changes we requested via RequestZoom from ones the user made on the ScrollViewer.
    private readonly ViewerZoomTracker _zoomTracker = new();

    // PDF page images (managed via ZeroMemory/Dispose)
    private List<SoftwareBitmap>? _pdfBitmaps;
    private List<PdfPageSource>?  _pdfSources;
    private CancellationTokenSource? _pdfRenderCts;
    private IDispatcherService _dispatcher = null!;

    // Mouse drag-to-pan, shared by ImageScrollViewer and PdfScrollViewer (the PanViewer_* handlers).
    // The mouse only drags one viewer at a time, so a single set of drag state is enough.
    private bool _isPanning;
    private Windows.Foundation.Point _panOrigin;
    private double _panOffsetH, _panOffsetV;

    private enum PanCursor { Default, Hand, Panning }
    private PanCursor _panCursor = PanCursor.Default;
    private ScrollViewer? _panCursorOwner;

    // UIElement.ProtectedCursor is protected, so set it via reflection.
    // In WinUI 3, ScrollViewer is sealed and cannot be subclassed,
    // and a WndProc hook never receives WM_SETCURSOR because XAML input is handled by the child HWND (ContentIsland).
    private static readonly PropertyInfo? _protectedCursorProp =
        typeof(UIElement).GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic);

    // Skips the reflection call when the same viewer already shows the requested state (this is re-evaluated
    // on every mouse move while hovering).
    private void SetPanCursor(ScrollViewer viewer, PanCursor state)
    {
        if (_panCursor == state && ReferenceEquals(_panCursorOwner, viewer)) return;
        _panCursor = state;
        _panCursorOwner = viewer;

        InputCursor? cursor = state switch
        {
            PanCursor.Hand    => InputSystemCursor.Create(InputSystemCursorShape.Hand),
            PanCursor.Panning => InputSystemCursor.Create(InputSystemCursorShape.SizeAll),
            _                 => null,
        };
        _protectedCursorProp?.SetValue(viewer, cursor);
    }

    public ViewerWindow()
    {
        var app = (App)Application.Current;
        // Not registered in DI on purpose: a disposable transient resolved from the root provider is
        // tracked by the container until the app exits, so every opened viewer would leak its
        // ViewerViewModel instance. This window owns the VM and disposes it in Closed.
        // Dependencies are resolved from the session scope (not the root provider) so the AddScoped
        // repositories are the same per-session instances the pages use and go away on lock,
        // instead of degenerating into root-lifetime singletons (captive dependency).
        ViewModel = ActivatorUtilities.CreateInstance<ViewerViewModel>(app.ShellScopeServices!);
        var dialogService = (WinUIDialogService)app.Services.GetRequiredService<IDialogService>();
        _dispatcher = app.Services.GetRequiredService<IDispatcherService>();
        InitializeComponent();

        this.SetWindowSize(900, 600);
        this.MinWidth = 700;
        this.MinHeight = 500;
        this.CenterOnScreen();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Taskbar/Alt+Tab icon (ICO). The title bar icon is already set in XAML via TitleBar.IconSource + ImageIconSource(PNG)
        AppWindow.SetIcon("Assets/NaimitsuVault.ico");
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // Mica backdrop (WinUIEx.WindowEx.SystemBackdrop: CS0612 obsolete, but suppressed in csproj)
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        ApplyCurrentTheme();

        // Capture protection: apply the same setting as ShellWindow immediately on open
        var captureProtection = app.Services.GetRequiredService<IWindowCaptureProtectionService>();
        var settingsVm = app.Services.GetRequiredService<AppSettingsViewModel>();
        captureProtection.Apply(WindowNative.GetWindowHandle(this), settingsVm.WindowCaptureProtectionEnabled);
        WeakReferenceMessenger.Default.Register<CaptureProtectionChangedMessage>(this, (_, msg) =>
        {
            captureProtection.Apply(WindowNative.GetWindowHandle(this), msg.Enabled);
        });

        // Font family: apply the current setting immediately, then track live changes
        if (Content is FrameworkElement fontRoot)
            ApplyFontFamily(fontRoot, settingsVm.FontFamily);
        WeakReferenceMessenger.Default.Register<FontFamilyChangedMessage>(this, (_, msg) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Content is FrameworkElement root) ApplyFontFamily(root, msg.FontFamily);
            }));

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateWindowTitle();

        // Set XamlRoot immediately on Loaded (Activated fires 2-3 seconds after Activate(),
        // so if the first button press happens before that, the dialog would show on ShellWindow instead)
        if (Content is FrameworkElement contentRoot)
        {
            contentRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Window_KeyDown), handledEventsToo: true);
            contentRoot.Loaded += (_, _) =>
            {
                if (contentRoot.XamlRoot != null)
                    dialogService.SetXamlRoot(contentRoot);
            };
        }

        // Also update XamlRoot when the window is reactivated
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated
                && Content is FrameworkElement root && root.XamlRoot != null)
                dialogService.SetXamlRoot(root);
        };

        Closed += async (_, _) =>
        {
            WeakReferenceMessenger.Default.Unregister<CaptureProtectionChangedMessage>(this); // prevent a race after Close
            WeakReferenceMessenger.Default.Unregister<FontFamilyChangedMessage>(this);
            ViewModel.Pause();  // Discipline 2: stop receiving messages before closing
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;  // detach first to prevent UI write-back
            ClearSecretData();

            // LinkToggleButton_Click/keyboard shortcuts invoke these commands fire-and-forget, so a
            // toggle immediately followed by closing the window can still be mid-flight here. Wait for
            // it to finish before Dispose() - otherwise Dispose()'s _linkLock.Wait() races the same
            // lock the toggle is holding, times out, and the draft write can be lost silently.
            if (ViewModel.ToggleLinkCommand.ExecutionTask is { IsCompleted: false } linkTask)
                await linkTask;
            if (ViewModel.ToggleProfileLinkCommand.ExecutionTask is { IsCompleted: false } profileLinkTask)
                await profileLinkTask;

            ViewModel.Dispose();
        };
        ViewModel.Resume();  // Discipline 2: start receiving messages once window construction completes
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // After LoadLinksAsync rebuilds the list following a link toggle (e.g. Ctrl+5),
        // the ViewModel restores the selection, but ScrollIntoView is the View's responsibility.
        // Without this call, the selected item stays off-screen and the list shows the top instead (a recurring known issue).
        if (e.PropertyName == nameof(ViewModel.LinkSelectedItem) && ViewModel.LinkSelectedItem is { } sel)
        {
            LinkListView.ScrollIntoView(sel);
            return;
        }

        if (e.PropertyName == nameof(ViewModel.FileName) || e.PropertyName == nameof(ViewModel.FileSizeDisplay))
        {
            UpdateWindowTitle();
            return;
        }

        if (e.PropertyName != nameof(ViewModel.FileBytes) && e.PropertyName != nameof(ViewModel.IsQuarantined)) return;

        ResetAllPanels();
        if (ViewModel.IsQuarantined)
        {
            ZoomComboBox.Visibility = Visibility.Collapsed;
            ImagePlaceholder.Text = LocalizationManager.Get("Viewer.PreviewQuarantined");
        }
        else if (ViewModel.IsPdf)
        {
            UpdatePdfDisplay();
        }
        else if (ViewModel.IsImage)
        {
            UpdateImageDisplay();
        }
        else if (ViewModel.IsCert || ViewModel.IsText || ViewModel.ServiceAccountData != null)
        {
            _textMode = true;
            _zoomIsAuto = false; // no Auto concept in font-size mode
            _textFontSize = 14.0;
            _suppressZoomComboSelection = true;
            ZoomComboBox.ItemsSource = _textZoomLevels;
            ZoomComboBox.SelectedItem = "14pt";
            _suppressZoomComboSelection = false;
            ApplyTextFontSize((int)_textFontSize);
            if (ViewModel.IsCert) UpdateCertSection();
            if (ViewModel.ServiceAccountData != null) UpdateJsonSection();
            if (ViewModel.IsText) UpdateTextSection();
            SecureFileViewer.Visibility = Visibility.Visible;
            ImagePlaceholder.Visibility = Visibility.Collapsed;
        }
        else if (ViewModel.IsArchive)
        {
            UpdateArchiveSection();
            SecureFileViewer.Visibility = Visibility.Visible;
            ImagePlaceholder.Visibility = Visibility.Collapsed;
            ZoomComboBox.Visibility     = Visibility.Collapsed;
        }
        else
        {
            // Binary files with no preview support (GPG, etc.)
            ZoomComboBox.Visibility = Visibility.Collapsed;
            ImagePlaceholder.Text = LocalizationManager.Get("Viewer.PreviewUnsupported");
        }
    }

    private void ResetAllPanels()
    {
        _textMode = false;
        ImagePlaceholder.Text        = LocalizationManager.Get("Common.Loading");
        _pdfRenderCts?.Cancel();
        ClearPdfPages();
        ImageScrollViewer.Content    = ViewerImage;
        ImageScrollViewer.Visibility = Visibility.Collapsed;
        SecureFileViewer.Visibility  = Visibility.Collapsed;
        CertSection.Visibility       = Visibility.Collapsed;
        CertTextDivider.Visibility   = Visibility.Collapsed;
        JsonSection.Visibility       = Visibility.Collapsed;
        JsonTextDivider.Visibility   = Visibility.Collapsed;
        TextSection.Visibility       = Visibility.Collapsed;
        ArchiveSection.Visibility    = Visibility.Collapsed;
        ImagePlaceholder.Visibility  = Visibility.Visible;
        ZoomComboBox.Visibility      = Visibility.Visible;
        // Default for image/PDF is Auto (fit-to-window); UpdateImageDisplay/RenderPdfAsync apply the
        // actual fit once the bitmap/page is loaded. Text mode overrides this to _textZoomLevels below.
        _zoomIsAuto = true;
        // The ScrollViewer may still report the previous file's zoom; the next observation is only a baseline.
        _zoomTracker.Reset();
        _suppressZoomComboSelection = true;
        ZoomComboBox.ItemsSource = _imageZoomLevels;
        ZoomComboBox.SelectedItem = "Auto";
        _suppressZoomComboSelection = false;
        // Clear leftover secret text/metadata from the previous file out of the UI on file switch
        TextViewerBox.Text           = string.Empty;
        // Clear Run.Text first to break the reference graph, then clear Blocks
        ClearRichTextBlocks(JsonRichViewer);
        ClearRichTextBlocks(XmlRichViewer);
        ClearRichTextBlocks(ConfigRichViewer);
        ClearRichTextBlocks(MarkdownRichViewer);
        ClearRichTextBlocks(SqlRichViewer);
        // Clear certificate/service-account metadata TextBlocks up front
        CertSubjectText.Text    = string.Empty;
        CertIssuerText.Text     = string.Empty;
        CertNotBeforeText.Text  = string.Empty;
        CertNotAfterText.Text   = string.Empty;
        CertThumbprintText.Text = string.Empty;
        CertKeyUsageText.Text   = string.Empty;
        JsonTypeText.Text       = string.Empty;
        JsonProjectText.Text    = string.Empty;
        JsonEmailText.Text      = string.Empty;
        JsonClientIdText.Text   = string.Empty;
    }

    private void UpdateCertSection()
    {
        var cert = ViewModel.CertData!;
        CertSection.Visibility = Visibility.Visible;

        CertSubjectText.Text    = cert.Subject;
        CertIssuerText.Text     = cert.Issuer;
        CertNotBeforeText.Text  = cert.NotBefore.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        CertNotAfterText.Text   = cert.NotAfter.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        CertThumbprintText.Text = cert.Thumbprint;

        if (!string.IsNullOrEmpty(cert.KeyUsage))
        {
            CertKeyUsageText.Text            = cert.KeyUsage;
            CertKeyUsageLabel.Visibility     = Visibility.Visible;
            CertKeyUsageText.Visibility      = Visibility.Visible;
        }

        if (cert.DaysUntilExpiry < 0)
        {
            CertExpiryBar.Severity = InfoBarSeverity.Error;
            CertExpiryBar.Message  = string.Format(LocalizationManager.Get("Viewer.ErrorCertExpired"), -cert.DaysUntilExpiry);
            CertExpiryBar.IsOpen   = true;
        }
        else if (cert.DaysUntilExpiry < AppConstants.CertExpirationWarnDays)
        {
            CertExpiryBar.Severity = InfoBarSeverity.Warning;
            CertExpiryBar.Message  = string.Format(LocalizationManager.Get("Viewer.WarningCertExpiringSoon"), cert.DaysUntilExpiry);
            CertExpiryBar.IsOpen   = true;
        }
        else
        {
            CertExpiryBar.IsOpen = false;
        }
    }

    private void UpdateJsonSection()
    {
        var sa = ViewModel.ServiceAccountData!;
        JsonSection.Visibility = Visibility.Visible;
        JsonTypeText.Text = sa.Type;

        if (!string.IsNullOrEmpty(sa.ProjectId))
        {
            JsonProjectText.Text          = sa.ProjectId;
            JsonProjectLabel.Visibility   = Visibility.Visible;
            JsonProjectText.Visibility    = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(sa.ClientEmail))
        {
            JsonEmailText.Text           = sa.ClientEmail;
            JsonEmailLabel.Visibility    = Visibility.Visible;
            JsonEmailText.Visibility     = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(sa.ClientId))
        {
            JsonClientIdText.Text        = sa.ClientId;
            JsonClientIdLabel.Visibility = Visibility.Visible;
            JsonClientIdText.Visibility  = Visibility.Visible;
        }
    }

    private void UpdateArchiveSection()
    {
        ArchiveSection.Visibility = Visibility.Visible;
        ArchiveEntriesList.ItemsSource = ViewModel.ArchiveEntries;
        ArchiveEntryCountText.Text = ViewModel.ArchiveTotalFileCount == 0
            ? LocalizationManager.Get("Viewer.ArchiveEmpty")
            : ViewModel.ArchiveEntries.Count < ViewModel.ArchiveTotalFileCount
                ? string.Format(LocalizationManager.Get("Viewer.ArchiveEntryCountTruncated"), ViewModel.ArchiveEntries.Count, ViewModel.ArchiveTotalFileCount)
                : string.Format(LocalizationManager.Get("Viewer.ArchiveEntryCount"), ViewModel.ArchiveTotalFileCount);
    }

    private void UpdateTextSection()
    {
        TextSection.Visibility = Visibility.Visible;
        TextViewerBox.Visibility       = Visibility.Collapsed;
        JsonRichViewer.Visibility      = Visibility.Collapsed;
        XmlRichViewer.Visibility       = Visibility.Collapsed;
        ConfigRichViewer.Visibility    = Visibility.Collapsed;
        MarkdownRichViewer.Visibility  = Visibility.Collapsed;
        SqlRichViewer.Visibility       = Visibility.Collapsed;

        // TextContentSpan is a direct span into the POH-pinned buffer. No string is allocated.
        if (ViewModel.IsJson)
        {
            JsonRichViewer.Visibility = Visibility.Visible;
            PopulateJsonHighlight(ViewModel.TextContentSpan);
        }
        else if (ViewModel.IsXml)
        {
            XmlRichViewer.Visibility = Visibility.Visible;
            PopulateXmlHighlight(ViewModel.TextContentSpan);
        }
        else if (ViewModel.IsConfig)
        {
            ConfigRichViewer.Visibility = Visibility.Visible;
            PopulateConfigHighlight(ViewModel.TextContentSpan);
        }
        else if (ViewModel.IsMarkdown)
        {
            MarkdownRichViewer.Visibility = Visibility.Visible;
            PopulateMarkdownHighlight(ViewModel.TextContentSpan);
        }
        else if (ViewModel.IsSql)
        {
            SqlRichViewer.Visibility = Visibility.Visible;
            PopulateSqlHighlight(ViewModel.TextContentSpan);
        }
        else
        {
            TextViewerBox.Visibility = Visibility.Visible;
            // TextBox.Text requires a string, so allocate exactly one (a span cannot be assigned directly)
            TextViewerBox.Text = new string(ViewModel.TextContentSpan);
        }
        if (ViewModel.IsCert)
            CertTextDivider.Visibility = Visibility.Visible;
        if (ViewModel.ServiceAccountData != null)
            JsonTextDivider.Visibility = Visibility.Visible;
    }

    // Argument changed to ReadOnlySpan<char>. Slice gives allocation-free range selection,
    // and Run.Text gets exactly one new string(span) allocation. This eliminates stray full-text string copies.
    private void PopulateJsonHighlight(ReadOnlySpan<char> json)
    {
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        Brush keyBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x9c, 0xdc, 0xfe)
            : Color.FromArgb(255, 0x04, 0x51, 0xa5));
        Brush strBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0xce, 0x91, 0x78)
            : Color.FromArgb(255, 0xa3, 0x15, 0x15));
        Brush numBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0xb5, 0xce, 0xa8)
            : Color.FromArgb(255, 0x09, 0x86, 0x58));
        Brush kwBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x56, 0x9c, 0xd6)
            : Color.FromArgb(255, 0x00, 0x00, 0xff));

        var para = new Paragraph();
        int i = 0, len = json.Length;
        var sb = new System.Text.StringBuilder();

        void FlushPlain()
        {
            if (sb.Length == 0) return;
            para.Inlines.Add(new Run { Text = sb.ToString() });
            sb.Clear();
        }

        while (i < len)
        {
            char c = json[i];

            if (c == '"')
            {
                FlushPlain();
                int start = i++;
                while (i < len)
                {
                    if (json[i] == '\\') { i += 2; continue; }
                    if (json[i] == '"')  { i++; break; }
                    i++;
                }
                var str = new string(json.Slice(start, i - start));
                int j = i;
                while (j < len && (json[j] == ' ' || json[j] == '\t')) j++;
                bool isKey = j < len && json[j] == ':';
                para.Inlines.Add(new Run { Text = str, Foreground = isKey ? keyBrush : strBrush });
            }
            else if (c == '\r' || c == '\n')
            {
                FlushPlain();
                para.Inlines.Add(new LineBreak());
                if (c == '\r' && i + 1 < len && json[i + 1] == '\n') i++;
                i++;
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                FlushPlain();
                int start = i;
                if (c == '-') i++;
                while (i < len && (char.IsAsciiDigit(json[i]) || json[i] is '.' or 'e' or 'E' or '+' or '-'))
                    i++;
                para.Inlines.Add(new Run { Text = new string(json.Slice(start, i - start)), Foreground = numBrush });
            }
            else if (c == 't' && i + 4 <= len && json.Slice(i, 4).SequenceEqual("true".AsSpan()))
            {
                FlushPlain();
                para.Inlines.Add(new Run { Text = "true", Foreground = kwBrush });
                i += 4;
            }
            else if (c == 'f' && i + 5 <= len && json.Slice(i, 5).SequenceEqual("false".AsSpan()))
            {
                FlushPlain();
                para.Inlines.Add(new Run { Text = "false", Foreground = kwBrush });
                i += 5;
            }
            else if (c == 'n' && i + 4 <= len && json.Slice(i, 4).SequenceEqual("null".AsSpan()))
            {
                FlushPlain();
                para.Inlines.Add(new Run { Text = "null", Foreground = kwBrush });
                i += 4;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        FlushPlain();

        JsonRichViewer.Blocks.Clear();
        JsonRichViewer.Blocks.Add(para);
    }

    // Argument changed to ReadOnlySpan<char>. Slice gives allocation-free range selection.
    // Add/AddBreakable take a ReadOnlySpan<char>.
    // FindTagEnd is a static local function (fully avoids capturing the ref struct xml).
    // RenderTag receives xml as a parameter to avoid capturing the ref struct.
    private void PopulateXmlHighlight(ReadOnlySpan<char> xml)
    {
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        Brush tagBrush     = new SolidColorBrush(isDark ? Color.FromArgb(255,0x56,0x9c,0xd6) : Color.FromArgb(255,0x80,0x00,0x00));
        Brush attrBrush    = new SolidColorBrush(isDark ? Color.FromArgb(255,0x9c,0xdc,0xfe) : Color.FromArgb(255,0xe5,0x00,0x00));
        Brush valBrush     = new SolidColorBrush(isDark ? Color.FromArgb(255,0xce,0x91,0x78) : Color.FromArgb(255,0xa3,0x15,0x15));
        Brush commentBrush = new SolidColorBrush(isDark ? Color.FromArgb(255,0x6a,0x99,0x55) : Color.FromArgb(255,0x00,0x80,0x00));
        Brush punctBrush   = new SolidColorBrush(isDark ? Color.FromArgb(255,0x80,0x80,0x80) : Color.FromArgb(255,0x80,0x80,0x80));

        var para = new Paragraph();
        int i = 0, len = xml.Length;

        void Add(ReadOnlySpan<char> text, Brush? brush)
        {
            if (text.IsEmpty) return;
            var s = new string(text);
            para.Inlines.Add(brush != null ? new Run { Text = s, Foreground = brush } : new Run { Text = s });
        }

        void AddBreakable(ReadOnlySpan<char> text, Brush? brush)
        {
            bool first = true;
            foreach (var lineSpan in text.EnumerateLines())
            {
                if (!first) para.Inlines.Add(new LineBreak());
                first = false;
                Add(lineSpan, brush);
            }
        }

        // static: receives xml as a parameter to fully avoid capturing the ref struct
        static int FindTagEnd(ReadOnlySpan<char> xml, int len, int start)
        {
            int j = start + 1;
            while (j < len)
            {
                if (xml[j] == '"' || xml[j] == '\'') { char q = xml[j++]; while (j < len && xml[j] != q) j++; if (j < len) j++; }
                else if (xml[j] == '>') return j + 1;
                else j++;
            }
            return len;
        }

        // Receives xml as a parameter to avoid capturing the ref struct, while still being able to use Add (which captures para)
        void RenderTag(ReadOnlySpan<char> xml, int start, int end)
        {
            int j = start;
            Add("<".AsSpan(), punctBrush);
            j++;

            if (j < end && xml[j] == '/') { Add("/".AsSpan(), punctBrush); j++; }
            bool isPI = j < end && xml[j] == '?';
            if (isPI) { Add("?".AsSpan(), punctBrush); j++; }

            int nameStart = j;
            while (j < end - 1 && xml[j] != ' ' && xml[j] != '\t' && xml[j] != '\n' && xml[j] != '\r'
                   && xml[j] != '>' && xml[j] != '/' && xml[j] != '?') j++;
            Add(xml.Slice(nameStart, j - nameStart), tagBrush);

            while (j < end - 1)
            {
                char c = xml[j];
                if (c == '>' || c == '/') break;
                if (c == '?') break;

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    int wsStart = j;
                    while (j < end && (xml[j] == ' ' || xml[j] == '\t' || xml[j] == '\r' || xml[j] == '\n')) j++;
                    Add(xml.Slice(wsStart, j - wsStart), null);
                    continue;
                }

                int attrStart = j;
                while (j < end && xml[j] != '=' && xml[j] != ' ' && xml[j] != '\t' && xml[j] != '>'
                       && xml[j] != '/' && xml[j] != '\n' && xml[j] != '\r' && xml[j] != '?') j++;
                Add(xml.Slice(attrStart, j - attrStart), attrBrush);

                if (j < end && xml[j] == '=')
                {
                    Add("=".AsSpan(), null);
                    j++;
                    if (j < end && (xml[j] == '"' || xml[j] == '\''))
                    {
                        char q = xml[j];
                        int valStart = j++;
                        while (j < end && xml[j] != q) j++;
                        if (j < end) j++;
                        Add(xml.Slice(valStart, j - valStart), valBrush);
                    }
                }
            }

            if (j < end) Add(xml.Slice(j, end - j), punctBrush);
        }

        while (i < len)
        {
            if (xml[i] == '<')
            {
                if (i + 4 <= len && xml.Slice(i, 4).SequenceEqual("<!--".AsSpan()))
                {
                    int relClose = xml.Slice(i + 4).IndexOf("-->".AsSpan());
                    int closeEnd = relClose < 0 ? len : i + 4 + relClose + 3;
                    AddBreakable(xml.Slice(i, closeEnd - i), commentBrush);
                    i = closeEnd;
                }
                else if (i + 9 <= len && xml.Slice(i, 9).SequenceEqual("<![CDATA[".AsSpan()))
                {
                    int relClose = xml.Slice(i + 9).IndexOf("]]>".AsSpan());
                    int closeEnd = relClose < 0 ? len : i + 9 + relClose + 3;
                    AddBreakable(xml.Slice(i, closeEnd - i), null);
                    i = closeEnd;
                }
                else
                {
                    int tagEnd = FindTagEnd(xml, len, i);
                    RenderTag(xml, i, tagEnd);
                    i = tagEnd;
                }
            }
            else
            {
                int textStart = i;
                while (i < len && xml[i] != '<') i++;
                AddBreakable(xml.Slice(textStart, i - textStart), null);
            }
        }

        XmlRichViewer.Blocks.Clear();
        XmlRichViewer.Blocks.Add(para);
    }

    // .env / .conf / .yaml / .yml / .toml / .ini
    // Color coding: # and ; comments (green), KEY part (blue), = / : separator (plain), VALUE part (reddish)
    // ReadOnlySpan<char> + EnumerateLines() for allocation-free line enumeration. Slice for sub-ranges.
    private void PopulateConfigHighlight(ReadOnlySpan<char> text)
    {
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        Brush commentBrush = new SolidColorBrush(isDark ? Color.FromArgb(255,0x6a,0x99,0x55) : Color.FromArgb(255,0x00,0x80,0x00));
        Brush keyBrush     = new SolidColorBrush(isDark ? Color.FromArgb(255,0x9c,0xdc,0xfe) : Color.FromArgb(255,0x04,0x51,0xa5));
        Brush valBrush     = new SolidColorBrush(isDark ? Color.FromArgb(255,0xce,0x91,0x78) : Color.FromArgb(255,0xa3,0x15,0x15));
        Brush secBrush     = new SolidColorBrush(isDark ? Color.FromArgb(255,0xc5,0x86,0xc0) : Color.FromArgb(255,0x80,0x00,0x80));

        var para = new Paragraph();
        bool firstLine = true;

        foreach (var lineSpan in text.EnumerateLines())
        {
            if (!firstLine) para.Inlines.Add(new LineBreak());
            firstLine = false;

            if (lineSpan.IsEmpty) continue;

            var trimmed = lineSpan.TrimStart();

            if (!trimmed.IsEmpty && (trimmed[0] == '#' || trimmed[0] == ';'))
            {
                para.Inlines.Add(new Run { Text = new string(lineSpan), Foreground = commentBrush });
            }
            else if (!trimmed.IsEmpty && trimmed[0] == '[' && trimmed.IndexOf(']') >= 0)
            {
                para.Inlines.Add(new Run { Text = new string(lineSpan), Foreground = secBrush });
            }
            else
            {
                int sep = lineSpan.IndexOfAny('=', ':');
                if (sep > 0)
                {
                    para.Inlines.Add(new Run { Text = new string(lineSpan.Slice(0, sep)), Foreground = keyBrush });
                    para.Inlines.Add(new Run { Text = new string(lineSpan.Slice(sep, 1)) });
                    if (sep + 1 < lineSpan.Length)
                        para.Inlines.Add(new Run { Text = new string(lineSpan.Slice(sep + 1)), Foreground = valBrush });
                }
                else
                {
                    para.Inlines.Add(new Run { Text = new string(lineSpan) });
                }
            }
        }

        ConfigRichViewer.Blocks.Clear();
        ConfigRichViewer.Blocks.Add(para);
    }

    // .md - headings (blue), code blocks (orange), quotes (gray), plain otherwise
    // ReadOnlySpan<char> + EnumerateLines(). Inline code is also span-controlled via Slice + IndexOf.
    private void PopulateMarkdownHighlight(ReadOnlySpan<char> text)
    {
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        Brush headBrush    = new SolidColorBrush(isDark ? Color.FromArgb(255,0x56,0x9c,0xd6) : Color.FromArgb(255,0x00,0x00,0x80));
        Brush codeBrush    = new SolidColorBrush(isDark ? Color.FromArgb(255,0xce,0x91,0x78) : Color.FromArgb(255,0xa3,0x15,0x15));
        Brush quoteBrush   = new SolidColorBrush(isDark ? Color.FromArgb(255,0x80,0x80,0x80) : Color.FromArgb(255,0x60,0x60,0x60));
        Brush fenceBrush   = new SolidColorBrush(isDark ? Color.FromArgb(255,0x6a,0x99,0x55) : Color.FromArgb(255,0x00,0x80,0x00));

        var para = new Paragraph();
        bool inCodeFence = false;
        bool firstLine = true;

        foreach (var lineSpan in text.EnumerateLines())
        {
            if (!firstLine) para.Inlines.Add(new LineBreak());
            firstLine = false;

            if (lineSpan.StartsWith("```".AsSpan()))
            {
                inCodeFence = !inCodeFence;
                para.Inlines.Add(new Run { Text = new string(lineSpan), Foreground = fenceBrush });
            }
            else if (inCodeFence)
            {
                para.Inlines.Add(new Run { Text = new string(lineSpan), Foreground = codeBrush });
            }
            else if (!lineSpan.IsEmpty && lineSpan[0] == '#')
            {
                para.Inlines.Add(new Run { Text = new string(lineSpan), Foreground = headBrush, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            }
            else if (!lineSpan.IsEmpty && lineSpan[0] == '>')
            {
                para.Inlines.Add(new Run { Text = new string(lineSpan), Foreground = quoteBrush });
            }
            else
            {
                // Color inline code ` ... `
                int i = 0;
                while (i < lineSpan.Length)
                {
                    int tick = lineSpan.Slice(i).IndexOf('`');
                    if (tick < 0)
                    {
                        if (i < lineSpan.Length)
                            para.Inlines.Add(new Run { Text = new string(lineSpan.Slice(i)) });
                        break;
                    }
                    int tickAbs = i + tick;
                    if (tick > 0)
                        para.Inlines.Add(new Run { Text = new string(lineSpan.Slice(i, tick)) });
                    int end = lineSpan.Slice(tickAbs + 1).IndexOf('`');
                    if (end < 0)
                    {
                        para.Inlines.Add(new Run { Text = new string(lineSpan.Slice(tickAbs)), Foreground = codeBrush });
                        i = lineSpan.Length;
                    }
                    else
                    {
                        int endAbs = tickAbs + 1 + end;
                        para.Inlines.Add(new Run { Text = new string(lineSpan.Slice(tickAbs, endAbs - tickAbs + 1)), Foreground = codeBrush });
                        i = endAbs + 1;
                    }
                }
            }
        }

        MarkdownRichViewer.Blocks.Clear();
        MarkdownRichViewer.Blocks.Add(para);
    }

    // .sql - keywords (blue), comments (green), strings (reddish), numbers (greenish)
    // ReadOnlySpan<char>. Slice for allocation-free range selection.
    // Block comments also use EnumerateLines() instead of Split('\n').
    private void PopulateSqlHighlight(ReadOnlySpan<char> text)
    {
        bool isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        Brush kwBrush      = new SolidColorBrush(isDark ? Color.FromArgb(255,0x56,0x9c,0xd6) : Color.FromArgb(255,0x00,0x00,0xff));
        Brush commentBrush = new SolidColorBrush(isDark ? Color.FromArgb(255,0x6a,0x99,0x55) : Color.FromArgb(255,0x00,0x80,0x00));
        Brush strBrush     = new SolidColorBrush(isDark ? Color.FromArgb(255,0xce,0x91,0x78) : Color.FromArgb(255,0xa3,0x15,0x15));
        Brush numBrush     = new SolidColorBrush(isDark ? Color.FromArgb(255,0xb5,0xce,0xa8) : Color.FromArgb(255,0x09,0x86,0x58));

        var keywords = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","FROM","WHERE","AND","OR","NOT","IN","EXISTS","BETWEEN","LIKE","IS","NULL",
            "INSERT","INTO","VALUES","UPDATE","SET","DELETE","MERGE",
            "CREATE","DROP","ALTER","TABLE","VIEW","INDEX","DATABASE","SCHEMA","TRIGGER","PROCEDURE","FUNCTION",
            "JOIN","LEFT","RIGHT","INNER","OUTER","CROSS","ON","AS","USING",
            "ORDER","BY","GROUP","HAVING","LIMIT","OFFSET","DISTINCT","ALL","UNION","EXCEPT","INTERSECT",
            "TRUE","FALSE","DEFAULT","PRIMARY","KEY","FOREIGN","REFERENCES","UNIQUE","CONSTRAINT","CHECK","NOT NULL",
            "BEGIN","COMMIT","ROLLBACK","TRANSACTION","SAVEPOINT",
            "CASE","WHEN","THEN","ELSE","END","IF","RETURN","DECLARE",
            "WITH","RECURSIVE","OVER","PARTITION","WINDOW","ROWS","RANGE",
            "CAST","CONVERT","COALESCE","NULLIF","COUNT","SUM","AVG","MIN","MAX",
        };

        var para = new Paragraph();
        int i = 0, len = text.Length;
        var sb = new System.Text.StringBuilder();

        void FlushPlain()
        {
            if (sb.Length == 0) return;
            para.Inlines.Add(new Run { Text = sb.ToString() });
            sb.Clear();
        }

        while (i < len)
        {
            char c = text[i];

            if (c == '\r' || c == '\n')
            {
                FlushPlain();
                para.Inlines.Add(new LineBreak());
                if (c == '\r' && i + 1 < len && text[i + 1] == '\n') i++;
                i++;
                continue;
            }

            // -- line comment
            if (c == '-' && i + 1 < len && text[i + 1] == '-')
            {
                FlushPlain();
                int relEnd = text.Slice(i + 2).IndexOfAny('\r', '\n');
                int endAbs = relEnd < 0 ? len : i + 2 + relEnd;
                para.Inlines.Add(new Run { Text = new string(text.Slice(i, endAbs - i)), Foreground = commentBrush });
                i = endAbs;
                continue;
            }

            // /* block comment */
            if (c == '/' && i + 1 < len && text[i + 1] == '*')
            {
                FlushPlain();
                int relClose = text.Slice(i + 2).IndexOf("*/".AsSpan());
                int closeEnd = relClose < 0 ? len : i + 2 + relClose + 2;
                var comment = text.Slice(i, closeEnd - i);
                bool firstCL = true;
                foreach (var commentLine in comment.EnumerateLines())
                {
                    if (!firstCL) para.Inlines.Add(new LineBreak());
                    firstCL = false;
                    if (!commentLine.IsEmpty)
                        para.Inlines.Add(new Run { Text = new string(commentLine), Foreground = commentBrush });
                }
                i = closeEnd;
                continue;
            }

            // String literal '...'
            if (c == '\'')
            {
                FlushPlain();
                int start = i++;
                while (i < len)
                {
                    if (text[i] == '\'' && i + 1 < len && text[i + 1] == '\'') { i += 2; continue; }
                    if (text[i] == '\'') { i++; break; }
                    i++;
                }
                para.Inlines.Add(new Run { Text = new string(text.Slice(start, i - start)), Foreground = strBrush });
                continue;
            }

            // Number
            if (char.IsAsciiDigit(c))
            {
                FlushPlain();
                int start = i;
                while (i < len && (char.IsAsciiDigit(text[i]) || text[i] is '.' or '_')) i++;
                para.Inlines.Add(new Run { Text = new string(text.Slice(start, i - start)), Foreground = numBrush });
                continue;
            }

            // Identifier or keyword
            // HashSet<string> does not accept a span directly, so allocate one new string and share it with Run.Text
            if (char.IsLetter(c) || c == '_')
            {
                FlushPlain();
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = new string(text.Slice(start, i - start));
                if (keywords.Contains(word))
                    para.Inlines.Add(new Run { Text = word, Foreground = kwBrush });
                else
                    para.Inlines.Add(new Run { Text = word });
                continue;
            }

            sb.Append(c);
            i++;
        }
        FlushPlain();

        SqlRichViewer.Blocks.Clear();
        SqlRichViewer.Blocks.Add(para);
    }

    private void UpdateImageDisplay()
    {
        var bytes = ViewModel.FileBytes;
        if (bytes is not { Length: > 0 }) return;

        var bitmap = new BitmapImage();
        bitmap.ImageOpened += (_, _) =>
        {
            ViewerImage.Width  = bitmap.PixelWidth;
            ViewerImage.Height = bitmap.PixelHeight;
            ImageScrollViewer.Visibility = Visibility.Visible;
            ImagePlaceholder.Visibility  = Visibility.Collapsed;

            // Fit after layout completes. If the viewport isn't settled yet this does nothing, and
            // ImageScrollViewer_SizeChanged fits once it is.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_zoomIsAuto) FitToWindow();
            });
        };
        _ = LoadBitmapAsync(bitmap, bytes);
        ViewerImage.Source = bitmap;
    }

    private void FitToWindow()
    {
        var vw = ImageScrollViewer.ViewportWidth;
        var vh = ImageScrollViewer.ViewportHeight;
        var iw = ViewerImage.Width;
        var ih = ViewerImage.Height;
        if (vw <= 0 || vh <= 0 || iw <= 0 || ih <= 0) return;

        var zoom = (float)Math.Clamp(Math.Min(vw / iw, vh / ih), 0.1, 1.0);
        RequestZoom(ImageScrollViewer, zoom);
    }

    // While "Auto" is selected the view follows the viewer's size: this is both the first fit once the
    // viewport is known (a Low-priority fit at open can run before layout has settled) and every later
    // window/pane resize. It is not raised by zoom or scrollbar changes, so RequestZoom can't retrigger it.
    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomIsAuto && ViewModel.IsImage && ImageScrollViewer.Visibility == Visibility.Visible)
            FitToWindow();
    }

    private void PdfScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomIsAuto && ViewModel.IsPdf) ApplyPdfFitToWidth();
    }

    // Single entry point for every zoom the viewer applies itself (fit and presets). Relies on
    // _zoomIsAuto (already set by the caller) rather than pinning the combo selection here directly:
    // ChangeView's resulting ViewChanged can land on a later dispatcher tick well after this call
    // returns (disableAnimation only removes the animated in-between frames, it doesn't make the
    // callback synchronous), so anything this method did to the selection itself could still be
    // overwritten by that later, unsuppressed firing. SyncImageZoomCombo checking _zoomIsAuto on every
    // firing - now and later - is what actually holds the selection on "Auto" no matter when
    // ViewChanged shows up. The request is registered with _zoomTracker so that landing isn't
    // mistaken for the user zooming.
    private void RequestZoom(ScrollViewer viewer, float zoom)
    {
        _zoomFactor = zoom;
        _zoomTracker.RegisterRequest(viewer.ZoomFactor, zoom);
        viewer.ChangeView(null, null, zoom, disableAnimation: true);
    }

    // ── PDF display pipeline ─────────────────────────────────────────────────────

    private void UpdatePdfDisplay()
    {
        _pdfRenderCts?.Cancel();
        ClearPdfPages();
        _pdfRenderCts = new CancellationTokenSource();
        _ = RenderPdfAsync(_pdfRenderCts.Token);
    }

    // Fits the first page's width to the viewport (used on initial open, for the "Auto" combo item and,
    // via PdfScrollViewer_SizeChanged, on every resize while Auto). Does nothing if the viewport isn't
    // settled yet - PdfScrollViewer_SizeChanged fits as soon as it is.
    private void ApplyPdfFitToWidth()
    {
        var fitW = _pdfBitmaps is { Count: > 0 } ? _pdfBitmaps[0].PixelWidth : 0;
        if (fitW <= 0 || PdfScrollViewer.ViewportWidth <= 0) return;

        var fitZoom = (float)Math.Clamp(
            PdfScrollViewer.ViewportWidth / fitW,
            PdfScrollViewer.MinZoomFactor,
            PdfScrollViewer.MaxZoomFactor);
        RequestZoom(PdfScrollViewer, fitZoom);
    }

    private async Task RenderPdfAsync(CancellationToken ct)
    {
        var bytes = ViewModel.FileBytes;
        if (bytes is not { Length: > 0 }) return;

        var rawBitmaps = new List<SoftwareBitmap>();
        var sources    = new List<PdfPageSource>();

        try
        {
            // Step 1: bytes -> InMemoryRandomAccessStream (zero disk writes)
            var pdfStream = new InMemoryRandomAccessStream();
            var writer    = new DataWriter(pdfStream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();

            // Step 2: PdfDocument (fast native OS parsing)
            PdfDocument? doc = null;
            try
            {
                doc = await PdfDocument.LoadFromStreamAsync(pdfStream);
            }
            catch
            {
                // Includes cases where a malformed or encrypted PDF throws
                await _dispatcher.EnqueueAsync(() =>
                {
                    ImagePlaceholder.Text       = LocalizationManager.Get("Viewer.PreviewUnsupported");
                    ImagePlaceholder.Visibility = Visibility.Visible;
                    return Task.CompletedTask;
                });
                return;
            }
            finally
            {
                pdfStream.Dispose(); // Once LoadFromStreamAsync completes, doc holds its own copy internally; the stream is no longer needed.
            }

            // Even when LoadFromStreamAsync succeeds, an encrypted PDF can still end up with IsPasswordProtected=true
            if (doc.IsPasswordProtected)
            {
                await _dispatcher.EnqueueAsync(() =>
                {
                    ImagePlaceholder.Text       = LocalizationManager.Get("Viewer.PreviewUnsupported");
                    ImagePlaceholder.Visibility = Visibility.Visible;
                    return Task.CompletedTask;
                });
                return;
            }

            ct.ThrowIfCancellationRequested();

            // Step 3: rasterize all pages on a background thread (avoids STA contamination)
            // The ct passed as Task.Run's second argument only cancels before the task starts.
            // The actual cancellation point is ThrowIfCancellationRequested inside the loop.
            var pageCount = Math.Min(doc.PageCount, 100u); // upper bound to prevent OOM
            await Task.Run(async () =>
            {
                for (uint i = 0; i < pageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var page = doc.GetPage(i);
                    // page.Size is in DIP (1/96 inch) units. Rendering at 144 DPI = 1.5x scale.
                    var renderW = (uint)Math.Clamp(page.Size.Width * 1.5, 400.0, 1600.0);
                    var renderH = (uint)(page.Size.Height / page.Size.Width * renderW);

                    using var pageStream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(pageStream, new PdfPageRenderOptions
                    {
                        DestinationWidth  = renderW,
                        DestinationHeight = renderH,
                        BitmapEncoderId   = BitmapEncoder.BmpEncoderId, // BMP: fastest encoding (no compression)
                        BackgroundColor   = Color.FromArgb(255, 255, 255, 255),
                    });
                    pageStream.Seek(0);

                    ct.ThrowIfCancellationRequested();

                    // BMP -> SoftwareBitmap (BGRA8 Premultiplied: required by SoftwareBitmapSource)
                    var decoder = await BitmapDecoder.CreateAsync(pageStream);
                    var bitmap  = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    rawBitmaps.Add(bitmap);
                }
            }, ct);

            // Step 4: use a separate EnqueueAsync per page to guarantee it starts on the UI thread
            // (split up because a single batched EnqueueAsync gives no guarantee of returning to the UI thread after an await)
            foreach (var bmp in rawBitmaps)
            {
                ct.ThrowIfCancellationRequested();
                await _dispatcher.EnqueueAsync(async () =>
                {
                    var src = new SoftwareBitmapSource();
                    await src.SetBitmapAsync(bmp); // guaranteed to start on the UI thread
                    sources.Add(new PdfPageSource(src));
                });
            }

            // Update the UI in one batch once all pages are done
            await _dispatcher.EnqueueAsync(() =>
            {
                _pdfBitmaps = rawBitmaps;
                _pdfSources = sources;
                PdfItemsControl.ItemsSource = sources;
                PdfScrollViewer.Visibility  = Visibility.Visible;
                ImagePlaceholder.Visibility = Visibility.Collapsed;

                // Fit to Width on open (ZoomComboBox already defaults to "Auto" from ResetAllPanels)
                DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (_zoomIsAuto) ApplyPdfFitToWidth();
                });
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            // On cancellation: dispose any SoftwareBitmapSource/SoftwareBitmap created mid-loop instead of leaking them.
            // Zero the bitmaps BEFORE disposing the sources (disposing a SoftwareBitmapSource also closes its bitmap).
            foreach (var bmp in rawBitmaps) SoftwareBitmapZeroer.TryZero(bmp);
            foreach (var ps  in sources)    ps.Source.Dispose();
            foreach (var bmp in rawBitmaps) bmp.Dispose();
        }
        catch (Exception ex)
        {
            // On a general exception (e.g. OOM) too, don't leave accumulated raw pixels orphaned on the heap
            Logger.LogError("Unexpected exception during PDF rendering. [{ExType}]", ex.GetType().Name);
            // Zero the bitmaps BEFORE disposing the sources (disposing a SoftwareBitmapSource also closes its bitmap).
            foreach (var bmp in rawBitmaps) SoftwareBitmapZeroer.TryZero(bmp);
            foreach (var ps  in sources)    ps.Source.Dispose();
            foreach (var bmp in rawBitmaps) bmp.Dispose();

            // ResetAllPanels() left ImagePlaceholder showing "Loading..."; without this the user
            // sees a placeholder stuck on "Loading" forever instead of a failure state.
            await _dispatcher.EnqueueAsync(() =>
            {
                ImagePlaceholder.Text       = LocalizationManager.Get("Viewer.PreviewFailed");
                ImagePlaceholder.Visibility = Visibility.Visible;
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// Detaches UI references to the PDF pages, ZeroMemory's the pixel data, then disposes them.
    /// The bitmaps must be zeroed before their SoftwareBitmapSource is disposed: disposing the source
    /// also closes the SoftwareBitmap, after which it can no longer be zeroed.
    /// Cancelling the CTS is the caller's responsibility (not done inside this method).
    /// </summary>
    private void ClearPdfPages()
    {
        PdfItemsControl.ItemsSource = null;
        PdfScrollViewer.Visibility  = Visibility.Collapsed;

        if (_pdfBitmaps is { Count: > 0 })
        {
            foreach (var bmp in _pdfBitmaps)
            {
                SoftwareBitmapZeroer.TryZero(bmp);
                bmp.Dispose();
            }
            _pdfBitmaps.Clear();
            _pdfBitmaps = null;
        }

        if (_pdfSources is { Count: > 0 })
        {
            foreach (var ps in _pdfSources)
                ps.Source.Dispose();
            _pdfSources.Clear();
            _pdfSources = null;
        }
    }

    private void PdfScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => SyncImageZoomCombo(PdfScrollViewer.ZoomFactor);

    // ── Image display ─────────────────────────────────────────────────────────────────

    private static async Task LoadBitmapAsync(BitmapImage bitmap, byte[] bytes)
    {
        using var ms = new System.IO.MemoryStream(bytes);
        await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
    }

    private void ImageScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => SyncImageZoomCombo(ImageScrollViewer.ZoomFactor);

    // Reflects a zoom change (wheel/pinch, or our own RequestZoom landing asynchronously) back onto
    // ZoomComboBox. A zoom change none of our own requests explains is the user zooming on the
    // ScrollViewer, which ends "Auto" (the view no longer follows the window size). While _zoomIsAuto is
    // set, the selection stays pinned to "Auto" regardless of the actual zoom value - a
    // fit-to-window/width ratio is essentially never an exact preset percent, so percent-matching would
    // otherwise blank the selection the moment the fit is applied. Once _zoomIsAuto is false, the
    // matching preset is selected if the zoom lands exactly on one, otherwise the selection is cleared -
    // the combo only ever offers discrete presets, so there's nothing meaningful to show for values in
    // between.
    private void SyncImageZoomCombo(float zoom)
    {
        if (_zoomTracker.OnViewChanged(zoom)) _zoomIsAuto = false;
        _zoomFactor = zoom;
        _suppressZoomComboSelection = true;
        if (_zoomIsAuto)
        {
            ZoomComboBox.SelectedItem = "Auto";
        }
        else
        {
            var pct = (int)Math.Round(zoom * 100);
            ZoomComboBox.SelectedItem = _imageZoomLevels.FirstOrDefault(s => s.EndsWith('%') && int.Parse(s[..^1]) == pct);
        }
        _suppressZoomComboSelection = false;
    }

    private void ZoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressZoomComboSelection) return;
        if (ZoomComboBox.SelectedItem is not string sel) return;

        if (_textMode)
        {
            if (sel.EndsWith("pt") && int.TryParse(sel[..^2], out var pt))
                ApplyTextFontSize(pt);
        }
        else if (sel == "Auto")
        {
            _zoomIsAuto = true;
            if (ViewModel.IsPdf) ApplyPdfFitToWidth();
            else FitToWindow();
        }
        else if (sel.EndsWith('%') && int.TryParse(sel[..^1], out var pct))
        {
            _zoomIsAuto = false;
            ApplyImagePdfZoom(pct);
        }
    }

    private void ApplyImagePdfZoom(int pct)
    {
        var viewer = ViewModel.IsPdf ? PdfScrollViewer : ImageScrollViewer;
        var zoom = (float)Math.Clamp(pct / 100.0, viewer.MinZoomFactor, viewer.MaxZoomFactor);
        // disableAnimation: an animated transition fires ViewChanged repeatedly with in-between zoom
        // values, which would flicker the combo selection through whichever preset the animation
        // happens to pass on the way to the target.
        RequestZoom(viewer, zoom);
    }

    // Ctrl+Plus/Minus: step to the next/previous concrete percent preset ("Auto" isn't reachable this
    // way - selecting it is an explicit combo choice, not a step target). Works off the live continuous
    // zoom, not the combo's current selection, so it also does the right thing after a drag/wheel zoom
    // that landed between presets.
    private void StepImagePdfZoom(int direction)
    {
        _zoomIsAuto = false;
        int[] percents = [10, 25, 50, 75, 100, 150, 200, 300, 500];
        var current = (int)Math.Round(_zoomFactor * 100);
        var target = direction > 0
            ? percents.FirstOrDefault(p => p > current, percents[^1])
            : percents.LastOrDefault(p => p < current, percents[0]);
        ApplyImagePdfZoom(target);
    }

    private void StepTextFontSize(int direction)
    {
        int[] sizes = [8, 10, 12, 14, 16, 18, 20, 24, 28];
        var current = (int)_textFontSize;
        var target = direction > 0
            ? sizes.FirstOrDefault(s => s > current, sizes[^1])
            : sizes.LastOrDefault(s => s < current, sizes[0]);
        ApplyTextFontSize(target);
    }

    private void ApplyTextFontSize(int pt)
    {
        _textFontSize = pt;
        TextViewerBox.FontSize  = pt;
        JsonRichViewer.FontSize = pt;
        XmlRichViewer.FontSize  = pt;
        _suppressZoomComboSelection = true;
        ZoomComboBox.SelectedItem = $"{pt}pt";
        _suppressZoomComboSelection = false;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_textMode) StepTextFontSize(+1);
        else StepImagePdfZoom(+1);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_textMode) StepTextFontSize(-1);
        else StepImagePdfZoom(-1);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        // Since IsEnabled is only referenced by the UI binding, place the same guard here as
        // defense against direct invocation (structural block on restricted view mode).
        if (!ViewModel.CanExport) return;
        var bytes = ViewModel.FileBytes;
        if (bytes == null) return;

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedFileName = string.IsNullOrWhiteSpace(ViewModel.FileName) ? "export" : ViewModel.FileName;

        if (ViewModel.IsPdf)
        {
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add(string.Format(LocalizationManager.Get("Common.FileFilter"), "PDF", "pdf"), new List<string> { ".pdf" });
        }
        else if (ViewModel.IsCert || ViewModel.IsText)
        {
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add(LocalizationManager.Get("Common.FileTypeCertKey"), new List<string> { ".pfx", ".p12", ".pem", ".cer", ".crt", ".key", ".pub" });
            picker.FileTypeChoices.Add(LocalizationManager.Get("Common.FileTypeXmlJson"), new List<string> { ".xml", ".json" });
            picker.FileTypeChoices.Add(LocalizationManager.Get("Common.AllFiles"), new List<string> { "." });
        }
        else
        {
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add(LocalizationManager.Get("Common.FileTypeImage"), new List<string> { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" });
            picker.FileTypeChoices.Add(LocalizationManager.Get("Common.AllFiles"), new List<string> { "." });
        }

        try
        {
            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
                await ViewModel.LogFileExportedAsync();
            }
        }
        catch (Exception ex)
        {
            // async void: an uncaught exception here would tear down the whole process.
            Logger.LogError("Export_Click failed. [{ExType}]", ex.GetType().Name);
            var notification = ((App)Application.Current).Services.GetRequiredService<IAppNotificationService>();
            notification.Show(LK.Common_Error, LK.Common_GeneralError, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    public Task LoadFileAsync(int imageId, CancellationToken ct) => ViewModel.LoadFileAsync(imageId, ct);

    /// <summary>
    /// Physically wipes the decrypted secret buffer from the heap and nulls out the image source reference (idempotent).
    /// Invoked automatically just before Close from WindowService, and from the Closed handler.
    /// </summary>
    public void ClearSecretData()
    {
        ViewModel.ClearSecretData();
        ViewerImage.Source = null;
        _pdfRenderCts?.Cancel();
        ClearPdfPages();
        ImageScrollViewer.Content = null;
        TextViewerBox.Text        = string.Empty;
        // Clear Run.Text to break the plaintext reference graph, then clear Blocks
        ClearRichTextBlocks(JsonRichViewer);
        ClearRichTextBlocks(XmlRichViewer);
        ClearRichTextBlocks(ConfigRichViewer);
        ClearRichTextBlocks(MarkdownRichViewer);
        ClearRichTextBlocks(SqlRichViewer);
        // Explicitly clear certificate/service-account metadata TextBlocks
        CertSubjectText.Text    = string.Empty;
        CertIssuerText.Text     = string.Empty;
        CertNotBeforeText.Text  = string.Empty;
        CertNotAfterText.Text   = string.Empty;
        CertThumbprintText.Text = string.Empty;
        CertKeyUsageText.Text   = string.Empty;
        JsonTypeText.Text       = string.Empty;
        JsonProjectText.Text    = string.Empty;
        JsonEmailText.Text      = string.Empty;
        JsonClientIdText.Text   = string.Empty;
    }

    // Before Blocks.Clear(), clear each Run.Text to break the strong reference to the plaintext string from the reference graph,
    // then ZeroMemory the string's own internal buffer (same pattern as PasswordLargePreviewControl/
    // SecretDraftCompareContent) - dropping the reference alone leaves the plaintext (secret file
    // content rendered as JSON/XML/.env/SQL etc.) sitting on the heap until GC happens to reclaim it.
    private static void ClearRichTextBlocks(RichTextBlock rtb)
    {
        foreach (var block in rtb.Blocks)
            if (block is Paragraph p)
                foreach (var inline in p.Inlines)
                    if (inline is Run r)
                    {
                        var s = r.Text;
                        r.Text = string.Empty;
                        if (!string.IsNullOrEmpty(s))
                            SecurePasswordHelper.ZeroStringInternals(s);
                    }
        rtb.Blocks.Clear();
    }

    // Window.Title, shown in Alt+Tab/the taskbar, is independent of AppTitleBar's (the TitleBar control's)
    // Title/Subtitle, so sync it explicitly here.
    private void UpdateWindowTitle()
    {
        var product = LocalizationManager.Get("System.Product.Name");
        Title = string.IsNullOrEmpty(ViewModel.FileName)
            ? product
            : $"{ViewModel.FileName} {ViewModel.FileSizeDisplay} - {product}";
    }

    private void ApplyCurrentTheme()
    {
        var settingsVm = ((App)Application.Current).Services.GetRequiredService<ViewModels.AppSettingsViewModel>();
        var theme = settingsVm.ThemeMode switch
        {
            "Dark"  => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _       => ElementTheme.Default,
        };
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;

            // Hook ActualThemeChanged to propagate to the ViewModel/Converter/title bar
            root.ActualThemeChanged -= Root_ActualThemeChanged; // prevent double registration
            root.ActualThemeChanged += Root_ActualThemeChanged;

            // Apply the initial state immediately (ActualTheme resolves right after RequestedTheme is set)
            ApplyThemeState(root.ActualTheme);
        }
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args)
        => ApplyThemeState(sender.ActualTheme);

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

    // ─── Centralized theme change handling ────────────────────────────────────────────────
    private void ApplyThemeState(ElementTheme actualTheme)
    {
        bool isLight = actualTheme == ElementTheme.Light;

        // Reflect into the ViewModel's Brush computed property (an accurate value independent of ApplicationTheme)
        ViewModel.IsLightTheme = isLight;

        // Sync the AppWindow title bar's caption button colors to the theme.
        // With ExtendsContentIntoTitleBar=true, the OS doesn't always apply this automatically, so set it explicitly.
        UpdateTitleBarButtonColors(isLight);

        // Immediately refresh the icon colors of list items already on screen
        RefreshLinkListColors();
    }

    private static readonly SolidColorBrush _fallbackAccent = new(Color.FromArgb(255, 0, 102, 204));
    // Ensures visibility against the light-mode white background. Dark colors like #444444 read as "black" and must not be used (a recurring known issue)
    private static readonly SolidColorBrush _fallbackGrey   = new(Color.FromArgb(255, 144, 144, 144));

    private Microsoft.UI.Xaml.Media.Brush GetLinkIconBrush(bool isLinked)
    {
        if (!isLinked)
            return Application.Current.Resources.TryGetValue("NvInactiveBrush", out var b) && b is Microsoft.UI.Xaml.Media.Brush br
                ? br : _fallbackGrey;
        // In light mode, reject the system accent (light blue) and return royal blue directly
        if (ViewModel.IsLightTheme)
            return _fallbackAccent;
        return Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var ab) && ab is Microsoft.UI.Xaml.Media.Brush abr
            ? abr : _fallbackAccent;
    }

    private void UpdateTitleBarButtonColors(bool isLight)
    {
        var tb = AppWindow.TitleBar;
        if (isLight)
        {
            tb.ButtonForegroundColor           = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonHoverBackgroundColor      = Color.FromArgb(255, 210, 210, 210);
            tb.ButtonHoverForegroundColor      = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonPressedBackgroundColor    = Color.FromArgb(255, 180, 180, 180);
            tb.ButtonPressedForegroundColor    = Color.FromArgb(255,   0,   0,   0);
            tb.ButtonInactiveForegroundColor   = Color.FromArgb(255, 120, 120, 120);
        }
        else
        {
            // Dark mode: revert to the system default
            tb.ButtonForegroundColor           = null;
            tb.ButtonHoverBackgroundColor      = null;
            tb.ButtonHoverForegroundColor      = null;
            tb.ButtonPressedBackgroundColor    = null;
            tb.ButtonPressedForegroundColor    = null;
            tb.ButtonInactiveForegroundColor   = null;
        }
    }

    // Immediately update the icon colors of visible list items when the theme changes
    private void RefreshLinkListColors()
    {
        if (LinkListView?.ItemsPanelRoot == null) return;
        foreach (var child in LinkListView.ItemsPanelRoot.Children)
        {
            if (child is not ListViewItem container) continue;
            if (container.Content is not LinkableSecretItem item) continue;
            if (container.ContentTemplateRoot is not Grid grid) continue;
            if (grid.Children.Count > 2 && grid.Children[2] is Button btn && btn.Content is FontIcon fi)
                fi.Foreground = GetLinkIconBrush(item.IsLinked);
        }
    }

    private void Window_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();

        // Pan the image with arrow keys (no Ctrl needed, only while an image is displayed)
        if (!ctrl && ViewModel.IsImage && ImageScrollViewer.Visibility == Visibility.Visible)
        {
            const double step = 64;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    ImageScrollViewer.ChangeView(ImageScrollViewer.HorizontalOffset - step, null, null, true);
                    e.Handled = true; return;
                case Windows.System.VirtualKey.Right:
                    ImageScrollViewer.ChangeView(ImageScrollViewer.HorizontalOffset + step, null, null, true);
                    e.Handled = true; return;
                case Windows.System.VirtualKey.Up:
                    ImageScrollViewer.ChangeView(null, ImageScrollViewer.VerticalOffset - step, null, true);
                    e.Handled = true; return;
                case Windows.System.VirtualKey.Down:
                    ImageScrollViewer.ChangeView(null, ImageScrollViewer.VerticalOffset + step, null, true);
                    e.Handled = true; return;
            }
        }

        // J/K: move up/down the right-pane secret list (bypassed while a TextBox has focus)
        // Active only without Ctrl and when a TextBox doesn't have focus. Same vi key convention as Secrets/TimeMachine.
        // WARNING: this block must always come before `if (!ctrl) return;`.
        //    Moving it after that turns it into dead code, blocked by the early return (a recurring known issue).
        if (!ctrl && (e.Key == Windows.System.VirtualKey.J || e.Key == Windows.System.VirtualKey.K))
        {
            if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is TextBox) return;
            var items = ViewModel.LinkFilteredItems;
            if (items.Count == 0) return;
            var currentIdx = ViewModel.LinkSelectedItem is { } sel ? items.IndexOf(sel) : -1;
            var nextIdx = e.Key == Windows.System.VirtualKey.J
                ? Math.Min(currentIdx + 1, items.Count - 1)
                : Math.Max(currentIdx - 1, 0);
            ViewModel.LinkSelectedItem = items[nextIdx];
            LinkListView.ScrollIntoView(items[nextIdx]);
            e.Handled = true;
            return;
        }

        // Space: toggle pin/unpin (ToggleLinkCommand) for the selected right-pane item.
        // Same TextBox-focus bypass and Ctrl exclusion as the J/K block above.
        if (!ctrl && e.Key == Windows.System.VirtualKey.Space)
        {
            if (FocusManager.GetFocusedElement(this.Content.XamlRoot) is TextBox) return;
            if (ViewModel.LinkSelectedItem is { } linkItem)
                _ = ViewModel.ToggleLinkCommand.ExecuteAsync(linkItem);
            e.Handled = true;
            return;
        }

        if (!ctrl) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)0xBB: // OEM_PLUS (the = / + key on the main keyboard)
                if (ViewModel.IsImage || ViewModel.IsPdf || _textMode)
                {
                    ZoomIn_Click(sender, null!);
                    e.Handled = true;
                }
                break;
            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)0xBD: // OEM_MINUS (the - / _ key on the main keyboard)
                if (ViewModel.IsImage || ViewModel.IsPdf || _textMode)
                {
                    ZoomOut_Click(sender, null!);
                    e.Handled = true;
                }
                break;
            case Windows.System.VirtualKey.F:
                // Auto-expand the right pane first - focusing LinkSearchBox while its column is
                // collapsed to width 0 would move keyboard focus onto a control the user can't see.
                if (ContentGrid.ColumnDefinitions[2].Width.Value <= 0)
                    ToggleRightPane();
                LinkSearchBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.S:
                Export_Click(sender, null!);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.X:
                this.Close();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.L:
                ((App)Application.Current).Lock();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.B:
                ToggleRightPane();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Number3:
                if (ViewModel.LinkSelectedItem is { } linkItem)
                    _ = ViewModel.ToggleLinkCommand.ExecuteAsync(linkItem);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Number4:
                ViewModel.ToggleLinkedFilterCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ── Right pane: link list ─────────────────────────────────────────────────

    private void LinkListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // ListView reuses containers on scroll, and this method re-fires for every reuse (recycle pass
        // and the following real-content pass alike). The IsLinked-change subscription below used to
        // have no matching unsubscription, so handlers accumulated on `item` on every reuse (a leak),
        // and a stale item's later IsLinked change could repaint `btn` after the container had since
        // been recycled to show a different item (a UI ghost bug). The (item, handler) pair from the
        // previous pass is stashed on the container's own Tag (not the button's - that's already bound
        // to the item via x:Bind for LinkToggleButton_Click) so it can be unsubscribed here first.
        if (args.ItemContainer.Tag is (LinkableSecretItem prevItem, PropertyChangedEventHandler prevHandler))
        {
            prevItem.PropertyChanged -= prevHandler;
            args.ItemContainer.Tag = null;
        }

        if (args.InRecycleQueue)
        {
            // On container reuse: immediately clear the plaintext title
            if (args.ItemContainer.ContentTemplateRoot is Grid g &&
                g.Children.Count > 0 && g.Children[0] is TextBlock tb)
                tb.Text = string.Empty;
            return;
        }
        if (args.Item is not LinkableSecretItem item) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid grid) return;

        // Set the title TextBlock
        if (grid.Children.Count > 0 && grid.Children[0] is TextBlock textBlock)
            textBlock.Text = item.GetOrCreateDisplayTitle();

        // Pencil icon: shown only when IsDraftLink=true
        if (grid.Children.Count > 1 && grid.Children[1] is FontIcon draftIcon)
            draftIcon.Visibility = item.IsDraftLink ? Visibility.Visible : Visibility.Collapsed;

        // Set the FontIcon Foreground/IsEnabled inside the right-edge button (ToolTip is managed via XAML x:Bind)
        if (grid.Children.Count > 2 && grid.Children[2] is Button btn)
        {
            if (btn.Content is FontIcon icon)
                icon.Foreground = GetLinkIconBrush(item.IsLinked);

            // Immediately redraw the container when IsLinked changes
            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName != nameof(LinkableSecretItem.IsLinked)) return;
                if (btn.Content is FontIcon fi)
                    fi.Foreground = GetLinkIconBrush(item.IsLinked);
            };
            item.PropertyChanged += handler;
            args.ItemContainer.Tag = (item, handler);
        }
    }

    private void LinkToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is LinkableSecretItem item)
            _ = ViewModel.ToggleLinkCommand.ExecuteAsync(item);
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e) => ToggleRightPane();

    private void ToggleRightPane()
    {
        var col = ContentGrid.ColumnDefinitions[2];
        bool willBeVisible = col.Width.Value <= 0;
        col.Width = willBeVisible ? new GridLength(260) : new GridLength(0);
        PaneDivider.Visibility = willBeVisible ? Visibility.Visible : Visibility.Collapsed;
        PaneToggleButton.IsChecked = !willBeVisible;
        PaneToggleGlyph.Glyph = willBeVisible ? ((char)0xE740).ToString() : ((char)0xE73F).ToString();
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    // ── Drag-to-pan (image and PDF) ───────────────────────────────────────────────────
    // Only the scroll offset changes (ChangeView with no zoom argument), so the zoom - and therefore
    // "Auto" - is untouched: ViewerZoomTracker only reacts to zoom changes.

    // Whether the content overflows the viewport, i.e. there is something to drag.
    private static bool CanPan(ScrollViewer viewer)
        => viewer.ScrollableWidth > 0 || viewer.ScrollableHeight > 0;

    // Hand only while there is something to drag. Re-evaluated on mouse move as well as on entry, because
    // zooming or resizing under a stationary pointer changes whether the content overflows.
    private void UpdatePanHoverCursor(ScrollViewer viewer)
        => SetPanCursor(viewer, CanPan(viewer) ? PanCursor.Hand : PanCursor.Default);

    private void PanViewer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            UpdatePanHoverCursor((ScrollViewer)sender);
    }

    private void PanViewer_PointerExited(object sender, PointerRoutedEventArgs e)
        => SetPanCursor((ScrollViewer)sender, PanCursor.Default);

    private void PanViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        var pt = e.GetCurrentPoint(viewer);
        if (pt.PointerDeviceType != PointerDeviceType.Mouse || !pt.Properties.IsLeftButtonPressed) return;
        if (!CanPan(viewer)) return;

        _isPanning  = true;
        _panOrigin  = pt.Position;
        _panOffsetH = viewer.HorizontalOffset;
        _panOffsetV = viewer.VerticalOffset;
        viewer.CapturePointer(e.Pointer);
        SetPanCursor(viewer, PanCursor.Panning);
        e.Handled = true;
    }

    private void PanViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        var pt = e.GetCurrentPoint(viewer);
        if (pt.PointerDeviceType != PointerDeviceType.Mouse) return;

        if (!_isPanning)
        {
            UpdatePanHoverCursor(viewer);
            return;
        }

        viewer.ChangeView(
            _panOffsetH - (pt.Position.X - _panOrigin.X),
            _panOffsetV - (pt.Position.Y - _panOrigin.Y),
            null, true);
        e.Handled = true;
    }

    private void PanViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        var viewer = (ScrollViewer)sender;
        _isPanning = false;
        viewer.ReleasePointerCapture(e.Pointer);
        UpdatePanHoverCursor(viewer);
    }

    private void PanViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPanning = false;
        UpdatePanHoverCursor((ScrollViewer)sender);
    }
}

/// <summary>Lightweight wrapper that exposes a PDF page image to the ItemsControl DataTemplate (trim-safe for x:Bind).</summary>
internal sealed record PdfPageSource(SoftwareBitmapSource Source);
