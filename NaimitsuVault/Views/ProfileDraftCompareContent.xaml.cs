// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Microsoft.Extensions.Logging;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace NaimitsuVault.Views;

public sealed partial class ProfileDraftCompareContent : UserControl
{
    private static readonly ILogger<ProfileDraftCompareContent> Logger = AppLog.For<ProfileDraftCompareContent>();

    internal List<CompareRowItem> CompareItems { get; set; } = [];
    public string TwinBSavedAt { get; set; } = string.Empty;
    public string TwinAUpdatedAt { get; set; } = string.Empty;

    // Avatar bytes bypass CompareRowItem (which is char-span-only), but they're still POH-pinned
    // buffers from ICryptoService.DecryptToPin (via AvatarService.LoadCommittedRawAsync /
    // LoadDraftRawAsync, both documented "Caller owns wiping") - a decrypted face photo is biometric
    // PII, not exempt from the ZeroMemory discipline. Wiped in OnUnloaded/DisposeAll below.
    // Gen0AvatarBytes and DraftAvatarBytes may reference the *same* array (when the draft mirrors the
    // committed avatar - see ProfileViewModel.BuildCompareItemsAsync's shownDraftAvatar); zeroing both
    // independently is safe since CryptographicOperations.ZeroMemory is idempotent.
    public byte[]? Gen0AvatarBytes { get; set; }
    public byte[]? DraftAvatarBytes { get; set; }
    public bool AvatarChanged { get; set; }
    public List<CompareFileThumbnail> Gen0Files  { get; set; } = [];
    public List<CompareFileThumbnail> DraftFiles { get; set; } = [];

    public event EventHandler? DiscardRequested;
    public event EventHandler? CloseRequested;

    // Holds the TextBlock pair and CompareRowItem for every displayed row.
    // Establishes the wipe order on Unload: clear all TextBlock.Text first, then ZeroMemory the CompareRowItem's pinned buffer.
    private readonly List<(TextBlock DraftBlock, TextBlock Gen0Block, CompareRowItem Item)> _allRowRefs = [];
    // Holds the copy button handlers dynamically registered in AddRow, explicitly unsubscribed with -= on Unload.
    private readonly List<(Button Btn, RoutedEventHandler Handler)> _copyBtnHandlers = [];
    private readonly ClipboardAutoEraser _clipboardEraser = new();
    private readonly IAuditLogService _auditLog;
    private readonly AppSession _session;
    private bool _sensitiveRevealed;
    private bool _isLoaded;
    private bool _isFinalizing;

    public ProfileDraftCompareContent()
    {
        InitializeComponent();
        _auditLog = ((App)Application.Current).Services.GetRequiredService<IAuditLogService>();
        _session  = ((App)Application.Current).Services.GetRequiredService<AppSession>();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;

        // PreviewKeyDown (tunneling) on the control root, rather than a bubbling KeyDown, so this still
        // fires reliably regardless of which element inside this ContentDialog popup currently has focus
        // (the constructor's own GettingFocus handler below forcibly moves focus to CloseBtn on open,
        // and a focused Button consuming the keystroke before it bubbles back up here is a known
        // ContentDialog popup-layer pitfall).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || !_allRowRefs.Any(r => r.Item.IsSensitive)) return;
            var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            if ((ctrl & CoreVirtualKeyStates.Down) == 0) return;
            ToggleReveal_Click(null!, null!);
            e.Handled = true;
        };

        GettingFocus += OnGettingFocusInitial;
    }

    private void OnGettingFocusInitial(UIElement sender, GettingFocusEventArgs e)
    {
        GettingFocus -= OnGettingFocusInitial;
        if (e.NewFocusedElement == CloseBtn) return;
        e.Cancel  = true;
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() => CloseBtn.Focus(FocusState.Programmatic));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;

        DraftHeaderLabel.Text = TwinBSavedAt;
        Gen0HeaderLabel.Text  = TwinAUpdatedAt;

        // Row order mirrors the edit screen (ProfilePage.xaml): avatar at the top, attached files
        // last (RecordExtrasControl sits below every text field there, alongside custom fields/Notes).
        await AddAvatarRowAsync();

        foreach (var item in CompareItems)
            AddRow(item);

        await AddAttachmentsRowAsync();

        RevealToggleBtn.Visibility = _allRowRefs.Any(r => r.Item.IsSensitive)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // Avatar isn't a CompareRowItem (text-only), so it's rendered as its own row ahead of the
    // field rows, reusing the same label/draft/gen0 3-column layout and amber-changed highlight.
    private async Task AddAvatarRowAsync()
    {
        if (Gen0AvatarBytes == null && DraftAvatarBytes == null) return;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text              = LocalizationManager.Get("Profile.Avatar"),
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Opacity           = 0.65,
            Margin            = new Thickness(0, 4, 8, 4),
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);

        var draftPic = await BuildAvatarThumbnailAsync(DraftAvatarBytes);
        draftPic.Margin = new Thickness(4, 4, 8, 4);
        Grid.SetColumn(draftPic, 1);
        row.Children.Add(draftPic);

        var gen0Pic = await BuildAvatarThumbnailAsync(Gen0AvatarBytes);
        var gen0Border = new Border
        {
            Padding      = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Background   = AvatarChanged ? ChangedHighlightBrush() : null,
            Child        = gen0Pic,
        };
        var gen0Container = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 4, 8, 4) };
        gen0Container.Children.Add(gen0Border);
        Grid.SetColumn(gen0Container, 2);
        row.Children.Add(gen0Container);

        FieldRowsPanel.Children.Add(row);
    }

    private static async Task<PersonPicture> BuildAvatarThumbnailAsync(byte[]? bytes)
    {
        var pic = new PersonPicture
        {
            Width               = 40,
            Height              = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (bytes is { Length: > 0 })
        {
            using var ms  = new MemoryStream(bytes);
            var       ras = ms.AsRandomAccessStream();
            var       bmp = new BitmapImage();
            await bmp.SetSourceAsync(ras);
            pic.ProfilePicture = bmp;
        }
        return pic;
    }

    // Attached documents (ID card scans etc.) aren't a CompareRowItem (text-only), so - like the
    // avatar row above - they're rendered as their own row ahead of the field rows. The whole
    // gen0-side thumbnail strip gets the same amber "changed" highlight as a text field would if the
    // two FileId sets differ at all, rather than per-file added/removed badges.
    private async Task AddAttachmentsRowAsync()
    {
        if (Gen0Files.Count == 0 && DraftFiles.Count == 0) return;

        var gen0Ids  = Gen0Files.Select(f => f.FileId).ToHashSet();
        var draftIds = DraftFiles.Select(f => f.FileId).ToHashSet();
        bool filesDiffer = !gen0Ids.SetEquals(draftIds);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text              = LocalizationManager.Get("Common.AttachedFiles"),
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Opacity           = 0.65,
            Margin            = new Thickness(0, 4, 8, 4),
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);

        var draftPanel = await BuildFileThumbnailPanelAsync(DraftFiles);
        draftPanel.Margin = new Thickness(4, 4, 8, 4);
        Grid.SetColumn(draftPanel, 1);
        row.Children.Add(draftPanel);

        var gen0Panel = await BuildFileThumbnailPanelAsync(Gen0Files);
        var gen0Border = new Border
        {
            Padding      = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Background   = filesDiffer ? ChangedHighlightBrush() : null,
            Child        = gen0Panel,
        };
        var gen0Container = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 4, 8, 4) };
        gen0Container.Children.Add(gen0Border);
        Grid.SetColumn(gen0Container, 2);
        row.Children.Add(gen0Container);

        FieldRowsPanel.Children.Add(row);
    }

    // Application.Current.Resources[key] ignores theme context and always resolves against the
    // Default resource bucket - in light mode this falls back to the raw Windows system accent color
    // (often light blue/cyan) instead of the app's defined accent (#0066CC). A recurring issue across
    // this app's light-mode brushes; fixed the same way as ViewerWindow.GetLinkIconBrush - branch on
    // ActualTheme and use a fixed brush in light mode.
    private static readonly SolidColorBrush _accentBrushLight = new(Color.FromArgb(255, 0, 102, 204));

    // Highlighter-marker gold for a "changed" cell - the same #FFD700 used for the favorite star, at
    // an alpha bright enough in each theme to read as a proper highlight rather than a faint wash.
    // Light needs more alpha than dark to reach the same perceived vividness: mixing with a white
    // background dilutes saturation far more per unit alpha than mixing with a near-black one.
    // 0x66 is intentionally tuned lower than TimeMachinePage's 0x85 to match the ContentDialog popup surface.
    private static readonly SolidColorBrush _changedHighlightLight = new(Color.FromArgb(0xB0, 255, 215, 0));
    private static readonly SolidColorBrush _changedHighlightDark  = new(Color.FromArgb(0x66, 255, 215, 0));

    private Brush ChangedHighlightBrush()
        => ActualTheme == ElementTheme.Dark ? _changedHighlightDark : _changedHighlightLight;

    private async Task<StackPanel> BuildFileThumbnailPanelAsync(List<CompareFileThumbnail> files)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (files.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "—", Opacity = 0.65 });
            return panel;
        }
        foreach (var file in files)
            panel.Children.Add(await BuildFileThumbnailAsync(file));
        return panel;
    }

    // 40x40 thumbnail (matching the avatar row's PersonPicture size) with the filename as a tooltip.
    // Files whose type can't produce a visual thumbnail (text, generic binaries) fall back to the
    // same per-type glyph + accent color as the attached-file list (RecordExtrasControl) and Gallery,
    // so a given file type always renders identically everywhere in the app.
    private async Task<FrameworkElement> BuildFileThumbnailAsync(CompareFileThumbnail file)
    {
        if (file.IsQuarantined)
        {
            var warnIcon = new FontIcon
            {
                Glyph      = "\uE7BA",
                FontSize   = 24,
                Width      = 40,
                Height     = 40,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            };
            ToolTipService.SetToolTip(warnIcon, LocalizationManager.Get("Common.AttachedFileQuarantined"));
            return warnIcon;
        }

        if (file.ThumbnailBytes is { Length: > 0 })
        {
            using var ms  = new MemoryStream(file.ThumbnailBytes);
            var       ras = ms.AsRandomAccessStream();
            var       bmp = new BitmapImage();
            await bmp.SetSourceAsync(ras);
            var image = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Width = 40, Height = 40 };
            ToolTipService.SetToolTip(image, file.FileName);
            return image;
        }

        var icon = new FontIcon
        {
            Glyph      = file.FileTypeGlyph,
            FontSize   = 24,
            Width      = 40,
            Height     = 40,
            Foreground = ActualTheme == ElementTheme.Light
                ? _accentBrushLight
                : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };
        ToolTipService.SetToolTip(icon, file.FileName);
        return icon;
    }

    private void AddRow(CompareRowItem item)
    {
        if (item.IsDraftEmpty && item.IsGen0Empty && !item.IsLabelDiffer) return;

        const string mask = "●●●●●●●●"; // U+25CF — matches WinUI 3 PasswordBox default PasswordChar

        bool isChanged = !MemoryExtensions.Equals(item.Gen0Span, item.DraftSpan, StringComparison.Ordinal);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // A label-only edit (custom label changed, field value didn't) would otherwise leave the row
        // looking completely unchanged, so the label itself gets the same amber "changed" highlight
        // as a value cell when only the label differs between draft and gen0.
        var labelBlock = new TextBlock
        {
            Text              = item.Label,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Opacity           = 0.65,
        };
        var labelBorder = new Border
        {
            Padding      = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(4),
            Background   = item.IsLabelDiffer ? ChangedHighlightBrush() : null,
            Margin       = new Thickness(0, 4, 8, 4),
            Child        = labelBlock,
        };
        Grid.SetColumn(labelBorder, 0);
        row.Children.Add(labelBorder);

        // Draft column
        // Sensitive fields: always mask on initial display (never allocate a string from the span).
        //                   Only on the first reveal is a string generated and cached in Tag; later toggles reuse the Tag (0 additional allocations).
        // Non-sensitive fields: WinUI 3 TextBlock.Text requires a string, so allocate exactly one for the dialog's lifetime.
        var draftBlock = new TextBlock
        {
            Text              = item.IsDraftEmpty ? "—" : (item.IsSensitive ? mask : new string(item.DraftSpan)),
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(4, 4, 8, 4),
        };
        Grid.SetColumn(draftBlock, 1);
        row.Children.Add(draftBlock);

        // Current (Gen0) column
        var gen0Block = new TextBlock
        {
            Text              = item.IsGen0Empty ? "—" : (item.IsSensitive ? mask : new string(item.Gen0Span)),
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bool canCopy = !item.IsGen0Empty && isChanged && !item.IsSensitive;
        var gen0Border = new Border
        {
            // Extra right padding reserves room for the copy button overlaid at the value's right edge
            Padding      = new Thickness(4, 2, canCopy ? 32 : 4, 2),
            CornerRadius = new CornerRadius(4),
            Background   = isChanged
                ? ChangedHighlightBrush()
                : null,
            Child        = gen0Block,
        };
        var gen0Container = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 4, 8, 4) };
        gen0Container.Children.Add(gen0Border);

        var copyBtn = new Button
        {
            Content             = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
            Width               = 28,
            Height              = 28,
            Padding             = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Style               = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Visibility          = canCopy ? Visibility.Visible : Visibility.Collapsed,
        };
        ToolTipService.SetToolTip(copyBtn, LocalizationManager.Get("Secrets.Dialog.CopyCommitted"));
        // Inject Gen0Span directly into the Win32 API as a ReadOnlySpan<char> - zero string allocation
        RoutedEventHandler copyHandler = (_, _) =>
        {
            if (!item.Gen0Span.IsEmpty)
            {
                ClipboardHelper.SetText(item.Gen0Span);
                _clipboardEraser.ScheduleClear(item.Gen0Span);
                _ = LogFieldCopiedAsync(item.Label);
            }
        };
        copyBtn.Click += copyHandler;
        _copyBtnHandlers.Add((copyBtn, copyHandler));
        gen0Container.Children.Add(copyBtn);
        Grid.SetColumn(gen0Container, 2);
        row.Children.Add(gen0Container);

        _allRowRefs.Add((draftBlock, gen0Block, item));

        FieldRowsPanel.Children.Add(row);
    }

    private async Task LogFieldCopiedAsync(string fieldLabel)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.ProfileFieldCopiedToClipboard,
                new ProfileFieldCopiedToClipboardPayload(fieldLabel),
                _session.GetKey());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("[ProfileDraftCompareContent] Failed to log ProfileFieldCopiedToClipboard. [{ExType}]", ex.GetType().Name);
        }
    }

    // Zeroes the POH-pinned avatar and attachment-thumbnail buffers this control owns (see the
    // "Caller owns wiping" contracts on AvatarService.LoadCommittedRawAsync/LoadDraftRawAsync and
    // ICryptoService.DecryptToPin). DraftFiles is excluded: its ThumbnailBytes come from ProfileFiles,
    // the page's live in-memory editing state, which this dialog doesn't own and outlives it.
    private void WipeAvatarAndAttachmentBuffers()
    {
        if (Gen0AvatarBytes is { Length: > 0 })
            CryptographicOperations.ZeroMemory(Gen0AvatarBytes);
        Gen0AvatarBytes = null;

        if (DraftAvatarBytes is { Length: > 0 })
            CryptographicOperations.ZeroMemory(DraftAvatarBytes);
        DraftAvatarBytes = null;

        foreach (var f in Gen0Files)
        {
            if (f.ThumbnailBytes is { Length: > 0 })
                CryptographicOperations.ZeroMemory(f.ThumbnailBytes);
            f.ThumbnailBytes = null;
        }
        Gen0Files.Clear();
    }

    private void ToggleReveal_Click(object sender, RoutedEventArgs e)
    {
        _sensitiveRevealed = !_sensitiveRevealed;
        const string mask = "●●●●●●●●"; // U+25CF — matches WinUI 3 PasswordBox default PasswordChar

        foreach (var (draftBlock, gen0Block, item) in _allRowRefs)
        {
            if (!item.IsSensitive) continue;

            if (_sensitiveRevealed)
            {
                // Tag cache - allocate exactly one new string on the first reveal only,
                // then reuse it from Tag on subsequent toggle-ONs (eliminates a per-toggle burst).
                if (!item.IsDraftEmpty)
                {
                    if (draftBlock.Tag is not string cachedDraft)
                    {
                        cachedDraft    = new string(item.DraftSpan);
                        draftBlock.Tag = cachedDraft;
                    }
                    draftBlock.Text = cachedDraft;
                }
                if (!item.IsGen0Empty)
                {
                    if (gen0Block.Tag is not string cachedGen0)
                    {
                        cachedGen0    = new string(item.Gen0Span);
                        gen0Block.Tag = cachedGen0;
                    }
                    gen0Block.Text = cachedGen0;
                }
            }
            else
            {
                // Switching back to the mask: "●●●●●●●●" is a string literal (interned), so this adds zero allocation
                draftBlock.Text = item.IsDraftEmpty ? "—" : mask;
                gen0Block.Text  = item.IsGen0Empty  ? "—" : mask;
            }
        }

        RevealIcon.Glyph = _sensitiveRevealed ? "\uED1A" : "\uE7B3";
        ToolTipService.SetToolTip(RevealToggleBtn, LocalizationManager.Get("Common.ToggleVisibility"));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // During a ContentDialog template cycle (Loaded->Unloaded->[Loaded doesn't necessarily fire
        // again]), _isFinalizing=false, so the full teardown below is skipped.
        // A previous fix cleared the display tree, row references, and button handlers here,
        // assuming "the rows get rebuilt on the second Loaded" - but on real devices there are
        // cases where a second Loaded never fires, and in that case everything stayed cleared with
        // no one to rebuild it, causing a regression where the compare screen went blank (2026-07-12).
        // As a safe implementation that doesn't depend on whether a second Loaded arrives, exit
        // immediately here without changing any state, including _isLoaded. Even if ContentDialog
        // interleaves a transient Unloaded/Loaded, rows that are already built stay on screen as-is
        // (the Loaded side skips rebuilding via the _isLoaded==true guard).
        // Calling _clipboardEraser.Dispose() before this guard would also cancel the pending
        // clipboard auto-clear timer on every non-final Unloaded, opening a path where, if a
        // transient Unloaded happens right after a copy, the secret value stays on the clipboard indefinitely.
        // Only call it on the final teardown.
        if (!_isFinalizing) return;

        _clipboardEraser.Dispose();

        // 1. Clear all displayed rows' TextBlock.Text first and break the Tag cache too
        //    -> establishes the wipe order by ZeroMemory'ing the CompareRowItem's pinned buffer afterward
        foreach (var (draftBlock, gen0Block, item) in _allRowRefs)
        {
            draftBlock.Text = "";
            if (draftBlock.Tag is string cachedDraft && cachedDraft.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedDraft);
            draftBlock.Tag = null; // break the strong reference to the Tag-cached string to make it GC-eligible

            gen0Block.Text = "";
            if (gen0Block.Tag is string cachedGen0 && cachedGen0.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedGen0);
            gen0Block.Tag = null;

            item.Clear();
        }
        _allRowRefs.Clear();

        // 2. Dispose every CompareRowItem, including rows that were never displayed (both values empty)
        foreach (var item in CompareItems)
            item.Dispose();

        // 3. Explicitly unsubscribe the copy button event handlers to break the reference graph of lambdas and captured variables
        foreach (var (btn, handler) in _copyBtnHandlers)
            btn.Click -= handler;
        _copyBtnHandlers.Clear();

        // 4. Physically break the UI tree's strong references so the control objects become GC-eligible
        FieldRowsPanel.Children.Clear();

        // 5. Physically break the strong references to the data objects
        CompareItems.Clear();

        // 6. ZeroMemory the POH-pinned avatar/attachment-thumbnail buffers this control owns
        WipeAvatarAndAttachmentBuffers();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        _isFinalizing = true;
        DiscardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _isFinalizing = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // Called after ShowCompareDialogAsync's await dialog.ShowAsync() returns, for when the dialog
    // closes via a route other than a button, e.g. Escape or Alt+F4.
    // A no-op when OnUnloaded already did the full cleanup via a button (_isFinalizing=true).
    internal void DisposeAll()
    {
        if (_isFinalizing) return;
        _isFinalizing = true;

        _clipboardEraser.Dispose();

        foreach (var (draftBlock, gen0Block, item) in _allRowRefs)
        {
            draftBlock.Text = "";
            if (draftBlock.Tag is string cachedDraft && cachedDraft.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedDraft);
            draftBlock.Tag = null;

            gen0Block.Text = "";
            if (gen0Block.Tag is string cachedGen0 && cachedGen0.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedGen0);
            gen0Block.Tag = null;

            item.Clear();
        }
        _allRowRefs.Clear();

        foreach (var item in CompareItems)
            item.Dispose();
        CompareItems.Clear();

        foreach (var (btn, handler) in _copyBtnHandlers)
            btn.Click -= handler;
        _copyBtnHandlers.Clear();

        FieldRowsPanel.Children.Clear();

        WipeAvatarAndAttachmentBuffers();
    }
}
