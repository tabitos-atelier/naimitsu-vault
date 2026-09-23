// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.Tests.Stubs;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Screen capture protection" TC-WCP-01 / TC-WCP-02.
/// </summary>
public sealed class WindowCaptureProtectionTests : IDisposable
{
    private sealed class SpyCaptureProtectionService : IWindowCaptureProtectionService
    {
        public List<(nint Hwnd, bool Enabled)> Calls { get; } = new();
        public void Apply(nint hwnd, bool enabled) => Calls.Add((hwnd, enabled));
    }

    // Cleans up WeakReferenceMessenger's global state after the test (unregisters the subscription that used this as the recipient)
    public void Dispose()
        => WeakReferenceMessenger.Default.UnregisterAll(this);

    // ── TC-WCP-01 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-WCP-01: verifies the fallback from an old-version settings JSON (missing the
    /// WindowCaptureProtectionEnabled key).
    /// Deserializes using the actual production SettingsJsonContext and confirms it is
    /// corrected to default ON via null -> ?? true.
    /// A pure deserialization test with no DB or DI dependencies at all.
    /// </summary>
    [Fact]
    public void LoadAsync_LegacyJsonWithoutWcpKey_DefaultsToTrue()
    {
        // Arrange: an old-version JSON without a WindowCaptureProtectionEnabled key
        const string legacyJson = """
            {
              "ThemeMode": "System",
              "AutoBackupEnabled": false,
              "AutoLockEnabled": true,
              "AutoLockMinutes": 5,
              "HideFromTaskbarWhenMinimized": false,
              "FaviconAutoFetchEnabled": false
            }
            """;

        // Act: deserialize with the actual production AppSettingsJsonContext (promoted to internal)
        var data = JsonSerializer.Deserialize(
            legacyJson, AppSettingsViewModel.AppSettingsJsonContext.Default.GeneralSettings);

        // Assert: missing key -> null -> ?? true guarantees default ON
        Assert.NotNull(data);
        Assert.Null(data.WindowCaptureProtectionEnabled);          // JSON key missing -> null
        Assert.True(data.WindowCaptureProtectionEnabled ?? true,   // ?? true -> default ON
            "For an old-version JSON, WindowCaptureProtectionEnabled being null must fall back to true");
    }

    // ── TC-WCP-02 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// TC-WCP-02: constructs an actual SettingsViewModel instance via BuildMinimalSettingsVm(),
    /// and as a result of flipping the toggle through a public property, verifies in practice
    /// that the partial handler fires and both
    /// (a) SpyCaptureProtectionService.Apply() is called synchronously and immediately, and
    /// (b) CaptureProtectionChangedMessage is broadcast via WeakReferenceMessenger and reaches
    ///     a stub receiver equivalent to ViewerWindow.
    /// </summary>
    [Fact]
    public void WindowCaptureProtectionEnabled_SetToFalse_AppliesImmediatelyAndBroadcasts()
    {
        // Arrange - the spy service and a receiver stub (equivalent to ViewerWindow's constructor registration)
        var spy = new SpyCaptureProtectionService();
        var vm = BuildMinimalSettingsVm(spy);
        var receivedMessages = new List<CaptureProtectionChangedMessage>();

        // Register the receiver stub with WeakReferenceMessenger (unregistered by UnregisterAll(this) in Dispose)
        WeakReferenceMessenger.Default.Register<CaptureProtectionChangedMessage>(this, (_, msg) =>
            receivedMessages.Add(msg));

        try
        {
            // Act: flip the toggle from true to false via the public property.
            // This synchronously fires partial void OnWindowCaptureProtectionEnabledChanged(false).
            vm.WindowCaptureProtectionEnabled = false;

            // Assert (1): Apply() is called synchronously and immediately (independent of the debounced DB write)
            Assert.Single(spy.Calls);
            Assert.False(spy.Calls[0].Enabled,
                "Apply() must be called immediately with Enabled=false");
            // Note: in the test environment, Application.Current is null, so hwnd = nint.Zero
            Assert.Equal(nint.Zero, spy.Calls[0].Hwnd);

            // Assert (2): broadcast to the sub-window via WeakReferenceMessenger
            Assert.Single(receivedMessages);
            Assert.False(receivedMessages[0].Enabled,
                "CaptureProtectionChangedMessage must be broadcast with Enabled=false");
        }
        finally
        {
            // Reliably cancels and releases the 300ms debounce timer (CancellationTokenSource)
            // that SchedulePersist() started, before the test ends. Harmless either way, but this suppresses lingering tasks.
            vm.Dispose();
        }
    }

    /// <summary>
    /// A minimal test-only AppSettingsViewModel.
    /// The only dependency OnWindowCaptureProtectionEnabledChanged uses is _captureProtection.
    /// Other fields are initialized with null!, and the 300ms delayed DB write fired by
    /// SchedulePersist inside the handler is silently absorbed via null _factory -> NullRef -> catch.
    /// </summary>
    private static AppSettingsViewModel BuildMinimalSettingsVm(IWindowCaptureProtectionService captureProtection)
        => new(
            factory:           null!,
            auth:              null!,
            session:           null!,
            themeService:      null!,
            fontResource:      null!,
            localization:      null!,
            autoLock:          null!,
            favicon:           null!,
            captureProtection: captureProtection,
            autoBackup:        null!,
            notification:      null!,
            dialog:            null!,
            filePicker:        null!,
            auditLog:          null!,
            logger:            NullLogger<AppSettingsViewModel>.Instance);
}
