// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using NaimitsuVault.Views;
using NaimitsuVault.Services.Interfaces;
using WinRT.Interop;

namespace NaimitsuVault.Services;

public class WindowService : IWindowService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WindowService> _logger;
    private readonly List<ViewerWindow> _openViewers = [];
    // Centrally manages the CancellationTokenSource bound to each ViewerWindow.
    // CloseAllViewers() fires Cancel() across the board before running ClearSecretData(), ensuring
    // no plaintext image data can physically remain after a lock.
    private readonly Dictionary<ViewerWindow, CancellationTokenSource> _viewerCts = [];
    // The file a ViewerWindow was opened for, recorded synchronously at creation time - not read
    // from ViewerViewModel.CurrentFileId, which stays 0 until LoadFileAsync's decrypt pipeline
    // finishes. OpenViewer's already-open lookup used CurrentFileId directly, so a second OpenViewer
    // call for the same file arriving before that first decrypt completed (e.g. a double-click, or
    // two ItemClick events fired in quick succession) never matched the still-loading window and
    // spawned a duplicate ViewerWindow instead of reusing it.
    private readonly Dictionary<ViewerWindow, int> _viewerTargetFileIds = [];
    // Page-owned ContentDialogs (SecretDraftCompareContent/ProfileDraftCompareContent) register a
    // closer here so LockCycleBodyAsync can tear them down deliberately, the same way CloseAllViewers
    // does for ViewerWindow, instead of relying on them being incidentally destroyed along with
    // ShellWindow's visual tree.
    private readonly List<Func<Task>> _activeDialogClosers = [];

    public WindowService(IServiceProvider services, ILogger<WindowService> logger)
    {
        _services = services;
        _logger   = logger;
    }

    private (ViewerWindow window, CancellationTokenSource cts) CreateTrackedViewer(int fileId)
    {
        var window = _services.GetRequiredService<ViewerWindow>();
        var cts = new CancellationTokenSource();
        _openViewers.Add(window);
        _viewerCts[window] = cts;
        _viewerTargetFileIds[window] = fileId;
        window.Closed += (_, _) =>
        {
            // Natural close: cancel the decryption pipeline first, then clear the secret buffer
            if (_viewerCts.Remove(window, out var c)) { c.Cancel(); c.Dispose(); }
            _viewerTargetFileIds.Remove(window);
            window.ClearSecretData();
            _openViewers.Remove(window);
        };
        return (window, cts);
    }

    public void OpenViewer(int fileId)
    {
        var existing = _openViewers.FirstOrDefault(w => _viewerTargetFileIds.GetValueOrDefault(w) == fileId);
        if (existing != null)
        {
            _logger.LogInformation("[OpenViewer] Reusing already-open ViewerWindow for FileId={FileId} (OpenViewers={Count})", fileId, _openViewers.Count);
            existing.Activate();
            Win32InputSender.SetForegroundWindow(WindowNative.GetWindowHandle(existing));
            return;
        }

        _logger.LogInformation("[OpenViewer] Creating new ViewerWindow for FileId={FileId} (OpenViewers={Count})", fileId, _openViewers.Count);
        var (window, cts) = CreateTrackedViewer(fileId);
        _ = window.LoadFileAsync(fileId, cts.Token);
        window.Activate();
        Win32InputSender.SetForegroundWindow(WindowNative.GetWindowHandle(window));
    }

    /// <summary>Closes the single ViewerWindow showing the given file, if one is open. Called after
    /// a soft-delete or permanent-delete from Gallery - a file that was just deleted has no reason
    /// to stay open, and leaving it open let the user re-link it from the viewer's right pane despite
    /// the delete having just severed every link (see IsCurrentFileDeleted). A no-op if the file
    /// isn't currently open in any viewer.</summary>
    public void CloseViewer(int fileId)
    {
        var window = _openViewers.FirstOrDefault(w => _viewerTargetFileIds.GetValueOrDefault(w) == fileId);
        if (window == null) return;

        _logger.LogInformation("[CloseViewer] Closing ViewerWindow for FileId={FileId} (file was deleted).", fileId);

        // Same ordering as CloseAllViewers: cancel the decryption pipeline, remove from the tracked
        // collections first (so a re-entrant Closed handler is a no-op), then clear and close.
        _openViewers.Remove(window);
        _viewerCts.Remove(window, out var cts);
        _viewerTargetFileIds.Remove(window);
        cts?.Cancel();

        window.ClearSecretData();
        window.Close();

        cts?.Dispose();
    }

    public void CloseAllViewers()
    {
        // (1) First, fire a cancel signal to all decryption pipelines (prevents decrypted bytes from
        //    ever materializing on the heap). Cancel() synchronously marks the token as canceled
        //    immediately, so every subsequent ThrowIfCancellationRequested() throws, and
        //    decryptedBytes gets ZeroMemory'd in the finally block.
        _logger.LogInformation("[CloseAllViewers] Closing {Count} open viewer(s).", _openViewers.Count);
        var ctsList = _viewerCts.Values.ToList();
        foreach (var c in ctsList)
            c.Cancel();

        // (2) Clear the collections first: prevents double processing even if Closed fires afterward
        var toClose = _openViewers.ToList();
        _openViewers.Clear();
        _viewerCts.Clear();
        _viewerTargetFileIds.Clear();

        // (3) Physically clear the secret buffer before closing the window
        //    Even after Cancel() sends the cancellation signal, a task that already got past
        //    ThrowIfCancellationRequested() could still write to FileBytes, so ClearSecretData()
        //    provides double protection (idempotent).
        foreach (var w in toClose)
        {
            w.ClearSecretData();
            w.Close();
        }

        // (4) Dispose the CTS instances
        foreach (var c in ctsList)
            c.Dispose();
    }

    public void RegisterActiveDialogCloser(Func<Task> closer) => _activeDialogClosers.Add(closer);
    public void UnregisterActiveDialogCloser(Func<Task> closer) => _activeDialogClosers.Remove(closer);

    public async Task CloseActiveDialogsAsync()
    {
        if (_activeDialogClosers.Count == 0) return;
        var closers = _activeDialogClosers.ToList(); // snapshot - each caller unregisters itself in its own finally

        var closeTask = Task.WhenAll(closers.Select(async c =>
        {
            try { await c(); }
            catch (Exception ex) { _logger.LogError("[CloseActiveDialogsAsync] A dialog closer threw. [{ExType}]", ex.GetType().Name); }
        }));

        // 800ms matches LockCycleOrchestrator's existing barricade grace period. A stuck dialog
        // teardown must never stall Lock() itself - locking is a fail-safe operation.
        if (await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromMilliseconds(800))) != closeTask)
            _logger.LogWarning("[CloseActiveDialogsAsync] Timed out waiting for {Count} dialog(s) to close during lock.", closers.Count);
    }
}
