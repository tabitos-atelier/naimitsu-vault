// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.Views;

[JsonSerializable(typeof(List<CustomFieldModel>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class DraftCompareJsonContext : JsonSerializerContext { }

public sealed partial class SecretDraftCompareContent : UserControl
{
    private static readonly ILogger<SecretDraftCompareContent> Logger = AppLog.For<SecretDraftCompareContent>();

    internal HistorySlotContent? Draft { get; set; }
    internal HistorySlotContent? Gen0  { get; set; }
    public string DraftAtDisplay { get; set; } = string.Empty;
    public string Gen0AtDisplay  { get; set; } = string.Empty;
    public string? DraftCategoryName { get; set; }
    public string? Gen0CategoryName  { get; set; }
    // Both lists' ThumbnailBytes are POH-pinned buffers from ICryptoService.DecryptToPin (via
    // SecretsViewModel.BuildAttachmentThumbnailsAsync, which decrypts the union of both sides'
    // FileIds in one pass) - unlike ProfileDraftCompareContent, DraftFiles here is also a freshly
    // decrypted snapshot, not a reference to some longer-lived live-editing collection, so both lists
    // are this dialog's own buffers to wipe. Wiped in OnUnloaded/DisposeAll below.
    public List<CompareFileThumbnail> DraftFiles { get; set; } = [];
    public List<CompareFileThumbnail> Gen0Files  { get; set; } = [];

    public event EventHandler? DiscardRequested;
    public event EventHandler? CloseRequested;

    // Removed the Func<string?> delegate and replaced it with a SecureCharBuffer reference (a class reference, so it's closure-safe).
    // On reveal, apply Tag-cache geometry (a new string only the first time; the Tag is reused thereafter).
    private readonly List<(TextBlock DraftBlock, TextBlock Gen0Block, SecureCharBuffer? DraftBuf, SecureCharBuffer? Gen0Buf)> _passwordRowRefs = [];
    // Tracks SecureCharBuffers created for custom fields, ExpiresAt, etc., and disposes them all at once in the final Unloaded.
    // SecureCharBuffers belonging to Draft/Gen0 are excluded from tracking, since HistorySlotContent.Dispose() manages those.
    private readonly List<SecureCharBuffer> _trackedBuffers = [];
    private readonly List<(Button Btn, RoutedEventHandler Handler)> _copyBtnHandlers = [];
    private readonly ClipboardAutoEraser _clipboardEraser = new();
    private readonly IAuditLogService _auditLog;
    private readonly AppSession _session;
    private bool _passwordsRevealed;
    private bool _isLoaded;
    private bool _isFinalizing;

    private Dictionary<string, string>? _draftLabelOverrides;
    private Dictionary<string, string>? _gen0LabelOverrides;

    public SecretDraftCompareContent()
    {
        InitializeComponent();
        _auditLog = ((App)Application.Current).Services.GetRequiredService<IAuditLogService>();
        _session  = ((App)Application.Current).Services.GetRequiredService<AppSession>();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;

        // PreviewKeyDown (tunneling) on the control root, rather than a KeyboardAccelerator on the
        // toggle button, so this still fires reliably regardless of which element inside this
        // ContentDialog popup currently has focus (KeyboardAccelerator scope resolution and
        // focused-button key consumption are both known to be unreliable inside a ContentDialog popup layer).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.H || _passwordRowRefs.Count == 0 || !IsCtrlDown()) return;
            ToggleReveal_Click(null!, null!);
            e.Handled = true;
        };

        GettingFocus += OnGettingFocusInitial;
    }

    private static bool IsCtrlDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

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

        DraftHeaderLabel.Text = DraftAtDisplay;
        Gen0HeaderLabel.Text  = Gen0AtDisplay;

        if (Draft == null) return;

        _draftLabelOverrides = ParseLabelOverrides(Draft.LabelOverridesBuf.Span);
        _gen0LabelOverrides  = ParseLabelOverrides(Gen0 != null ? Gen0.LabelOverridesBuf.Span : ReadOnlySpan<char>.Empty);

        List<CustomFieldModel>? gen0Cfs = null;
        if (Gen0 != null && !Gen0.CustomFieldsBuf.IsEmpty)
        {
            try { gen0Cfs = JsonSerializer.Deserialize(Gen0.CustomFieldsBuf.Span, DraftCompareJsonContext.Default.ListCustomFieldModel); }
            catch { }
        }
        NormalizeFieldIds(gen0Cfs);

        // Compare passwords directly between PasswordBuf.Span values (zero intermediate string allocation)
        bool pwdChanged = !MemoryExtensions.Equals(
            Draft.PasswordBuf.Span,
            Gen0 != null ? Gen0.PasswordBuf.Span : ReadOnlySpan<char>.Empty,
            StringComparison.Ordinal);

        // Standard fields pass SecureCharBuffer.Span directly to AddRow as a ReadOnlySpan<char>.
        // A new string is only allocated momentarily inside AddRow when setting TextBlock.Text; it's never held in a field.
        // gen0CopyBuf passes Gen0's SecureCharBuffer directly as a class reference,
        // so clipboard writes also have zero string allocation.
        var (titleLabel, titleLabelChanged) = ResolveLabel("Common.Title", "title");
        AddRow(titleLabel,
            Draft.TitleBuf.Span,
            Gen0 != null ? Gen0.TitleBuf.Span : default,
            gen0CopyBuf: Gen0?.TitleBuf,
            labelChanged: titleLabelChanged);
        // Category: display only, no copy button (not sensitive, and copying a category name isn't useful).
        AddRow(LocalizationManager.Get("Secrets.CategorySelection"),
            (DraftCategoryName ?? string.Empty).AsSpan(),
            (Gen0CategoryName  ?? string.Empty).AsSpan());
        var (userIdLabel, userIdLabelChanged) = ResolveLabel("Common.Username", "userId");
        AddRow(userIdLabel,
            Draft.UserIdBuf.Span,
            Gen0 != null ? Gen0.UserIdBuf.Span : default,
            gen0CopyBuf: Gen0?.UserIdBuf,
            labelChanged: userIdLabelChanged);

        // Password row: displays the "●" presence-indicator span. The real value is only briefly
        // turned into a string on reveal, via the SecureCharBuffer reference.
        var (passwordLabel, passwordLabelChanged) = ResolveLabel("Common.Password", "password");
        AddRow(passwordLabel,
            Draft.PasswordBuf.IsEmpty          ? default : "●".AsSpan(),
            Gen0?.PasswordBuf.IsEmpty != false ? default : "●".AsSpan(),
            isPassword:  true,
            draftPwdBuf: Draft.PasswordBuf.IsEmpty          ? null : Draft.PasswordBuf,
            gen0PwdBuf:  Gen0?.PasswordBuf.IsEmpty != false ? null : Gen0?.PasswordBuf,
            forceIsChanged: pwdChanged,
            labelChanged: passwordLabelChanged);

        // GenSymbols (symbol set) placed directly under Password to emphasize the relationship -
        // it only affects password generation, matching the generator panel's layout in the edit screen.
        var draftGenSym = Draft.GenSymbolsBuf.IsEmpty ? PasswordGenerator.DefaultSymbols : new string(Draft.GenSymbolsBuf.Span);
        var gen0GenSym  = Gen0 != null
            ? (Gen0.GenSymbolsBuf.IsEmpty ? PasswordGenerator.DefaultSymbols : new string(Gen0.GenSymbolsBuf.Span))
            : PasswordGenerator.DefaultSymbols;
        SecureCharBuffer? genSymGen0Buf = null;
        if (gen0GenSym != null)
        {
            genSymGen0Buf = new SecureCharBuffer();
            genSymGen0Buf.SetFromSpan(gen0GenSym.AsSpan());
            _trackedBuffers.Add(genSymGen0Buf);
        }
        AddRow(LocalizationManager.Get("Secrets.SymbolSet"),
            draftGenSym.AsSpan(), gen0GenSym.AsSpan(), gen0CopyBuf: genSymGen0Buf);

        var (urlLabel, urlLabelChanged) = ResolveLabel("Common.Website", "url");
        AddRow(urlLabel,
            Draft.WebsiteBuf.Span,
            Gen0 != null ? Gen0.WebsiteBuf.Span : default,
            gen0CopyBuf: Gen0?.WebsiteBuf,
            labelChanged: urlLabelChanged);
        var (emailLabel, emailLabelChanged) = ResolveLabel("Common.Email", "email");
        AddRow(emailLabel,
            Draft.EmailBuf.Span,
            Gen0 != null ? Gen0.EmailBuf.Span : default,
            gen0CopyBuf: Gen0?.EmailBuf,
            labelChanged: emailLabelChanged);
        // ExpiresAt: format the ISO string into a temporary string and pass it as a span.
        //            The gen0 copy buffer is tracked as a POH-pinned SecureCharBuffer.
        var draftExpires = FormatExpiresAt(Draft.ExpiresAtBuf.Span);
        var gen0Expires  = FormatExpiresAt(Gen0 != null ? Gen0.ExpiresAtBuf.Span : default);
        SecureCharBuffer? expiresGen0Buf = null;
        if (gen0Expires != null)
        {
            expiresGen0Buf = new SecureCharBuffer();
            expiresGen0Buf.SetFromSpan(gen0Expires.AsSpan());
            _trackedBuffers.Add(expiresGen0Buf);
        }
        AddRow(LocalizationManager.Get("Common.ExpiresAt"),
            draftExpires.AsSpan(), gen0Expires.AsSpan(), gen0CopyBuf: expiresGen0Buf);

        List<CustomFieldModel>? draftCfs = null;
        if (!Draft.CustomFieldsBuf.IsEmpty)
        {
            try { draftCfs = JsonSerializer.Deserialize(Draft.CustomFieldsBuf.Span, DraftCompareJsonContext.Default.ListCustomFieldModel); }
            catch { }
        }
        NormalizeFieldIds(draftCfs);

        if (draftCfs != null)
        {
            var gen0CfLookup = gen0Cfs?.ToDictionary(cf => cf.FieldId) ?? [];
            foreach (var draftCf in draftCfs)
            {
                gen0CfLookup.TryGetValue(draftCf.FieldId, out var gen0Cf);
                var dv = draftCf.Value;
                var gv = gen0Cf?.Value;
                // A newly-added field (gen0Cf == null) has no committed label to compare against, so
                // it's always treated as label-changed - the field itself didn't exist before, and by
                // this point a label-only edit is the only way an all-empty-value draft field could
                // exist at all (an untouched placeholder field never gets auto-saved into a draft).
                bool cfLabelChanged = gen0Cf?.Label != draftCf.Label;

                if (draftCf.IsPassword)
                {
                    // Instead of holding the password CF value in a Func closure,
                    // transcribe it directly from the string span, right after JSON decryption, into a pinned SecureCharBuffer.
                    // The dv/gv strings become eligible for GC once transcribed here (no closure capture).
                    SecureCharBuffer? draftPwdBuf = null;
                    SecureCharBuffer? gen0PwdBuf  = null;
                    if (!string.IsNullOrEmpty(dv))
                    {
                        draftPwdBuf = new SecureCharBuffer();
                        draftPwdBuf.SetFromSpan(dv.AsSpan());
                        _trackedBuffers.Add(draftPwdBuf);
                    }
                    if (!string.IsNullOrEmpty(gv))
                    {
                        gen0PwdBuf = new SecureCharBuffer();
                        gen0PwdBuf.SetFromSpan(gv.AsSpan());
                        _trackedBuffers.Add(gen0PwdBuf);
                    }
                    bool cfPwdChanged = !MemoryExtensions.Equals(
                        draftPwdBuf != null ? draftPwdBuf.Span : ReadOnlySpan<char>.Empty,
                        gen0PwdBuf  != null ? gen0PwdBuf.Span  : ReadOnlySpan<char>.Empty,
                        StringComparison.Ordinal);
                    AddRow(draftCf.Label,
                        draftPwdBuf != null ? "●".AsSpan() : default,
                        gen0PwdBuf  != null ? "●".AsSpan() : default,
                        isPassword:     true,
                        draftPwdBuf:    draftPwdBuf,
                        gen0PwdBuf:     gen0PwdBuf,
                        forceIsChanged: cfPwdChanged,
                        labelChanged:   cfLabelChanged);
                }
                else
                {
                    // Non-password CF: transcribe the gen0 value's copy buffer into a pinned region.
                    //                  The dv/gv spans only live within the AddRow call frame.
                    SecureCharBuffer? gen0CfCopyBuf = null;
                    if (!string.IsNullOrEmpty(gv))
                    {
                        gen0CfCopyBuf = new SecureCharBuffer();
                        gen0CfCopyBuf.SetFromSpan(gv.AsSpan());
                        _trackedBuffers.Add(gen0CfCopyBuf);
                    }
                    AddRow(draftCf.Label, dv.AsSpan(), gv.AsSpan(), gen0CopyBuf: gen0CfCopyBuf, labelChanged: cfLabelChanged);
                }
            }
        }

        if (gen0Cfs != null)
        {
            var draftIds = draftCfs?.Select(cf => cf.FieldId).ToHashSet() ?? [];
            foreach (var gen0Cf in gen0Cfs.Where(cf => !draftIds.Contains(cf.FieldId)))
            {
                var gv = gen0Cf.Value;

                if (gen0Cf.IsPassword)
                {
                    SecureCharBuffer? gen0PwdBuf = null;
                    if (!string.IsNullOrEmpty(gv))
                    {
                        gen0PwdBuf = new SecureCharBuffer();
                        gen0PwdBuf.SetFromSpan(gv.AsSpan());
                        _trackedBuffers.Add(gen0PwdBuf);
                    }
                    // draft doesn't exist, so the draft span is empty (only gen0 exists -> always treated as changed)
                    bool cfChanged = gen0PwdBuf != null && !gen0PwdBuf.IsEmpty;
                    AddRow(gen0Cf.Label, default,
                        gen0PwdBuf != null ? "●".AsSpan() : default,
                        isPassword:     true,
                        gen0PwdBuf:     gen0PwdBuf,
                        forceIsChanged: cfChanged);
                }
                else
                {
                    SecureCharBuffer? gen0CfCopyBuf = null;
                    if (!string.IsNullOrEmpty(gv))
                    {
                        gen0CfCopyBuf = new SecureCharBuffer();
                        gen0CfCopyBuf.SetFromSpan(gv.AsSpan());
                        _trackedBuffers.Add(gen0CfCopyBuf);
                    }
                    AddRow(gen0Cf.Label, default, gv.AsSpan(), gen0CopyBuf: gen0CfCopyBuf);
                }
            }
        }

        // Notes placed after custom fields to match the edit screen (RecordExtrasControl: custom
        // fields, then Notes, then attached files).
        var (notesLabel, notesLabelChanged) = ResolveLabel("Common.Notes", "notes");
        AddRow(notesLabel,
            Draft.NotesBuf.Span,
            Gen0 != null ? Gen0.NotesBuf.Span : default,
            gen0CopyBuf: Gen0?.NotesBuf,
            labelChanged: notesLabelChanged);

        await AddAttachmentsRowAsync();

        RevealToggleBtn.Visibility = _passwordRowRefs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Attached files aren't CompareRowItem-compatible (SecureCharBuffer/text-only), so - like Profile's
    // avatar row (ProfileDraftCompareContent.AddAvatarRowAsync) - they're rendered as their own row
    // ahead of the text-diff rows, reusing the same label/draft/gen0 3-column layout. Rather than
    // added/removed badges per file, the whole gen0-side thumbnail strip gets the same amber "changed"
    // highlight as a text field would if the two FileId sets differ at all - consistent with how every
    // other row in this dialog signals "something changed here", nothing more granular.
    private async Task AddAttachmentsRowAsync()
    {
        if (DraftFiles.Count == 0 && Gen0Files.Count == 0) return;

        var draftIds = DraftFiles.Select(f => f.FileId).ToHashSet();
        var gen0Ids  = Gen0Files.Select(f => f.FileId).ToHashSet();
        bool filesDiffer = !draftIds.SetEquals(gen0Ids);

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

    private static string? FormatExpiresAt(ReadOnlySpan<char> iso) =>
        !iso.IsEmpty && DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToLocalTime().ToString("yyyy/MM/dd") : null;

    private static Dictionary<string, string>? ParseLabelOverrides(ReadOnlySpan<char> json)
    {
        if (json.IsEmpty) return null;
        try { return JsonSerializer.Deserialize(json, DraftCompareJsonContext.Default.DictionaryStringString); }
        catch { return null; }
    }

    // Returns the label to display (draft override > gen0 override > default, preserving prior
    // behavior) plus whether the draft's and gen0's effective labels actually differ - needed to
    // highlight the label column when only the label (not the field value) was edited, since
    // otherwise a label-only edit leaves the row looking completely unchanged.
    private (string Display, bool Changed) ResolveLabel(string defaultKey, string field)
    {
        string def = LocalizationManager.Get(defaultKey);
        string? draftOverride = _draftLabelOverrides?.TryGetValue(field, out var dl) == true && !string.IsNullOrEmpty(dl) ? dl : null;
        string? gen0Override  = _gen0LabelOverrides?.TryGetValue(field, out var gl) == true && !string.IsNullOrEmpty(gl) ? gl : null;
        string draftLabel = draftOverride ?? def;
        string gen0Label  = gen0Override  ?? def;
        return (draftOverride ?? gen0Override ?? def, draftLabel != gen0Label);
    }

    // All string parameters replaced with ReadOnlySpan<char> + SecureCharBuffer references.
    // draftSpan/gen0Span: display spans that get allocated as a new string exactly once, for
    // setting TextBlock.Text (only live within the call frame).
    // draftPwdBuf/gen0PwdBuf: POH-pinned buffers for password reveal (the Func delegate was removed).
    // gen0CopyBuf: POH-pinned buffer for clipboard writes (injected directly, never passing through a string).
    private void AddRow(
        string label,
        ReadOnlySpan<char> draftSpan,
        ReadOnlySpan<char> gen0Span,
        bool isPassword = false,
        SecureCharBuffer? draftPwdBuf = null,
        SecureCharBuffer? gen0PwdBuf  = null,
        SecureCharBuffer? gen0CopyBuf = null,
        bool? forceIsChanged = null,
        bool labelChanged = false)
    {
        if (draftSpan.IsEmpty && gen0Span.IsEmpty && draftPwdBuf == null && gen0PwdBuf == null && !labelChanged) return;

        const string mask = "●●●●●●●●"; // U+25CF — matches WinUI 3 PasswordBox default PasswordChar
        bool isChanged = forceIsChanged ?? !MemoryExtensions.Equals(draftSpan, gen0Span, StringComparison.Ordinal);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // A label-only edit (custom label changed, field value didn't) would otherwise leave the row
        // looking completely unchanged, so the label itself gets the same amber "changed" highlight
        // as a value cell when only the label differs between draft and gen0.
        var labelBlock = new TextBlock
        {
            Text              = label,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Opacity           = 0.65,
        };
        var labelBorder = new Border
        {
            Padding      = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(4),
            Background   = labelChanged ? ChangedHighlightBrush() : null,
            Margin       = new Thickness(0, 4, 8, 4),
            Child        = labelBlock,
        };
        Grid.SetColumn(labelBorder, 0);
        row.Children.Add(labelBorder);

        // Secret fields: always mask on initial display (never allocate a string from the span).
        //                Only on the first reveal is a string generated and cached in Tag; later toggles reuse the Tag.
        // Non-secret fields: WinUI 3 TextBlock.Text requires a string, so allocate exactly one for the dialog's lifetime.
        var draftBlock = new TextBlock
        {
            Text              = isPassword ? (draftSpan.IsEmpty ? "—" : mask) : (draftSpan.IsEmpty ? "—" : new string(draftSpan)),
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping      = TextWrapping.Wrap,
            Margin            = new Thickness(4, 4, 8, 4),
        };
        Grid.SetColumn(draftBlock, 1);
        row.Children.Add(draftBlock);

        var gen0Block = new TextBlock
        {
            Text              = isPassword ? (gen0Span.IsEmpty ? "—" : mask) : (gen0Span.IsEmpty ? "—" : new string(gen0Span)),
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Password fields are forbidden from writing directly to the clipboard
        bool canCopy = gen0CopyBuf != null && !gen0CopyBuf.IsEmpty && isChanged && !isPassword;
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
        // Injects gen0CopyBuf's (SecureCharBuffer's) Span directly into the Win32 clipboard as a ReadOnlySpan<char>.
        // Zero string allocation. Since SecureCharBuffer is a class reference, closure capture is safe.
        RoutedEventHandler copyHandler = (_, _) =>
        {
            if (gen0CopyBuf != null && !gen0CopyBuf.IsEmpty)
            {
                ClipboardHelper.SetText(gen0CopyBuf.Span);
                _clipboardEraser.ScheduleClear(gen0CopyBuf.Span);
                if (Gen0 != null)
                    _ = LogFieldCopiedAsync(Gen0.SecretId, label, Gen0.TitleBuf.IsEmpty ? null : new string(Gen0.TitleBuf.Span));
            }
        };
        copyBtn.Click += copyHandler;
        _copyBtnHandlers.Add((copyBtn, copyHandler));
        gen0Container.Children.Add(copyBtn);
        Grid.SetColumn(gen0Container, 2);
        row.Children.Add(gen0Container);

        if (isPassword && (draftPwdBuf != null || gen0PwdBuf != null))
            _passwordRowRefs.Add((draftBlock, gen0Block, draftPwdBuf, gen0PwdBuf));

        FieldRowsPanel.Children.Add(row);
    }

    private async Task LogFieldCopiedAsync(int targetId, string fieldLabel, string? name)
    {
        try
        {
            await _auditLog.LogAsync(
                AuditEventCode.SecretFieldCopiedToClipboard,
                new SecretFieldCopiedToClipboardPayload(targetId, fieldLabel, name),
                _session.GetKey());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("[SecretDraftCompareContent] Failed to log SecretFieldCopiedToClipboard. [{ExType}]", ex.GetType().Name);
        }
    }

    // Zeroes the POH-pinned attachment-thumbnail buffers both DraftFiles and Gen0Files own (see the
    // "Caller owns wiping" contract on ICryptoService.DecryptToPin, reached via
    // SecretsViewModel.BuildAttachmentThumbnailsAsync).
    private void WipeAttachmentThumbnailBuffers()
    {
        foreach (var f in DraftFiles)
        {
            if (f.ThumbnailBytes is { Length: > 0 })
                CryptographicOperations.ZeroMemory(f.ThumbnailBytes);
            f.ThumbnailBytes = null;
        }
        DraftFiles.Clear();

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
        _passwordsRevealed = !_passwordsRevealed;
        const string mask = "●●●●●●●●"; // U+25CF — matches WinUI 3 PasswordBox default PasswordChar

        foreach (var (draftBlock, gen0Block, draftBuf, gen0Buf) in _passwordRowRefs)
        {
            if (_passwordsRevealed)
            {
                // Tag-cache geometry - allocate exactly one new string(Span) on the first reveal only,
                // then reuse it from Tag on subsequent toggle-ONs (eliminates a per-toggle burst).
                if (draftBuf != null && !draftBuf.IsEmpty)
                {
                    if (draftBlock.Tag is not string cachedDraft)
                    {
                        cachedDraft    = new string(draftBuf.Span);
                        draftBlock.Tag = cachedDraft;
                    }
                    draftBlock.Text = cachedDraft;
                }
                else
                {
                    draftBlock.Text = "—";
                }

                if (gen0Buf != null && !gen0Buf.IsEmpty)
                {
                    if (gen0Block.Tag is not string cachedGen0)
                    {
                        cachedGen0    = new string(gen0Buf.Span);
                        gen0Block.Tag = cachedGen0;
                    }
                    gen0Block.Text = cachedGen0;
                }
                else
                {
                    gen0Block.Text = "—";
                }
            }
            else
            {
                // Switching back to the mask: "●●●●●●●●" is an interned literal, so this is zero-allocation
                draftBlock.Text = (draftBuf != null && !draftBuf.IsEmpty) ? mask : "—";
                gen0Block.Text  = (gen0Buf  != null && !gen0Buf.IsEmpty)  ? mask : "—";
            }
        }

        RevealIcon.Glyph = _passwordsRevealed ? "\uED1A" : "\uE7B3";
        ToolTipService.SetToolTip(RevealToggleBtn, LocalizationManager.Get("Common.ToggleVisibility"));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _draftLabelOverrides = null;
        _gen0LabelOverrides  = null;

        // During a ContentDialog template cycle (Loaded->Unloaded->Loaded), _isFinalizing=false,
        // so the full teardown below is skipped, and the toggle functionality survives into the second Loaded.
        // Calling _clipboardEraser.Dispose() before this guard would also cancel the pending
        // clipboard auto-clear timer on every non-final Unloaded, opening a path where, if a
        // transient Unloaded happens right after a copy, the secret value stays on the clipboard indefinitely.
        // Only call it on the final teardown.
        if (!_isFinalizing) return;

        _clipboardEraser.Dispose();

        // 1. Mask the password TextBlocks, zero any Tag-cached reveal string, then drop the reference
        const string mask = "●●●●●●●●"; // U+25CF — matches WinUI 3 PasswordBox default PasswordChar
        foreach (var (draftBlock, gen0Block, _, _) in _passwordRowRefs)
        {
            draftBlock.Text = mask;
            if (draftBlock.Tag is string cachedDraft && cachedDraft.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedDraft);
            draftBlock.Tag = null;

            gen0Block.Text = mask;
            if (gen0Block.Tag is string cachedGen0 && cachedGen0.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedGen0);
            gen0Block.Tag = null;
        }
        _passwordRowRefs.Clear();

        // 2. Explicitly unsubscribe the copy button event handlers to break the SecureCharBuffer reference graph
        foreach (var (btn, handler) in _copyBtnHandlers)
            btn.Click -= handler;
        _copyBtnHandlers.Clear();

        // 3. Physically break the UI tree's strong references
        FieldRowsPanel.Children.Clear();

        // 4. ZeroMemory and release the SecureCharBuffers generated from custom field values, ExpiresAt, etc.
        foreach (var buf in _trackedBuffers) buf.Dispose();
        _trackedBuffers.Clear();

        // 5. Dispose the slots to ZeroMemory their buffers
        Draft?.Dispose(); Draft = null;
        Gen0?.Dispose();  Gen0  = null;

        // 6. ZeroMemory the POH-pinned attachment-thumbnail buffers this control owns
        WipeAttachmentThumbnailBuffers();
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

        const string mask = "●●●●●●●●"; // U+25CF — matches WinUI 3 PasswordBox default PasswordChar
        foreach (var (draftBlock, gen0Block, _, _) in _passwordRowRefs)
        {
            draftBlock.Text = mask;
            if (draftBlock.Tag is string cachedDraft && cachedDraft.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedDraft);
            draftBlock.Tag = null;

            gen0Block.Text = mask;
            if (gen0Block.Tag is string cachedGen0 && cachedGen0.Length > 0)
                SecurePasswordHelper.ZeroStringInternals(cachedGen0);
            gen0Block.Tag = null;
        }
        _passwordRowRefs.Clear();

        foreach (var (btn, handler) in _copyBtnHandlers)
            btn.Click -= handler;
        _copyBtnHandlers.Clear();

        FieldRowsPanel.Children.Clear();

        foreach (var buf in _trackedBuffers) buf.Dispose();
        _trackedBuffers.Clear();

        Draft?.Dispose(); Draft = null;
        Gen0?.Dispose();  Gen0  = null;

        WipeAttachmentThumbnailBuffers();
    }

    // When imported data etc. has multiple fields with FieldId=0, ToDictionary crashes, so
    // normalize FieldIds into sequential numbers using the same algorithm as BuildEditModelFromSlotAsync.
    private static void NormalizeFieldIds(List<CustomFieldModel>? cfs)
    {
        if (cfs == null) return;
        int nextId = cfs.Max(f => (int?)f.FieldId) ?? 0;
        foreach (var cf in cfs)
            if (cf.FieldId == 0) cf.FieldId = ++nextId;
    }
}
