// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Common;
using NaimitsuVault.Models;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// AuditLogViewModel tests: the three-way filter (severity / code group / text), the empty-state
/// placeholder split, the IsActive / NeedsReload discipline for AuditLogWrittenMessage, the
/// latest-request-wins load ordering, and the shared retention-window constant.
/// (TC-ALV-01..14)
/// AuditLogViewModel subscribes on WeakReferenceMessenger.Default (a process-wide singleton), so this
/// class runs in the serialized "SequentialMessenger" collection like the other messenger-driven tests.
/// The fake service completes synchronously unless a test gates it, so a message-triggered
/// fire-and-forget LoadAsync has finished by the time Send returns.
/// </summary>
[Collection("SequentialMessenger")]
public sealed class AuditLogViewModelTests : IDisposable
{
    private readonly FakeAuditLogService _service = new();
    private readonly AppSession _session = new();
    private readonly List<AuditLogViewModel> _created = [];

    public AuditLogViewModelTests()
    {
        _session.SetKey(new byte[32]);
    }

    public void Dispose()
    {
        foreach (var vm in _created) vm.Dispose();
    }

    private AuditLogViewModel MakeVm()
    {
        var vm = new AuditLogViewModel(_service, _session, NullLogger<AuditLogViewModel>.Instance);
        _created.Add(vm);
        return vm;
    }

    private static AuditLogDisplayItem Item(int id, AuditEventCode code, int level = 0, string summary = "summary")
        => new(id, DateTimeOffset.UtcNow, code, level, summary);

    private static Task<IReadOnlyList<AuditLogDisplayItem>> Result(params AuditLogDisplayItem[] items)
        => Task.FromResult<IReadOnlyList<AuditLogDisplayItem>>(items);

    private async Task<AuditLogViewModel> LoadedVmAsync(params AuditLogDisplayItem[] items)
    {
        _service.OnGetAllAfter = (_, _) => Result(items);
        var vm = MakeVm();
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    private static void SendWritten()
        => WeakReferenceMessenger.Default.Send(new AuditLogWrittenMessage(AuditEventCode.SecretViewed, 0));

    // ── Filtering ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0)] // Normal    -> EventLevel 0
    [InlineData(2, 1)] // Notice    -> EventLevel 1
    [InlineData(3, 2)] // Important -> EventLevel 2
    public async Task TC_ALV_01_SeverityFilter_KeepsOnlyMatchingLevel(int levelIndex, int expectedLevel)
    {
        var vm = await LoadedVmAsync(
            Item(1, AuditEventCode.SecretViewed, level: 0),
            Item(2, AuditEventCode.SecretViewed, level: 1),
            Item(3, AuditEventCode.SecretViewed, level: 2));

        vm.SelectedLevelIndex = levelIndex;

        var only = Assert.Single(vm.FilteredLogs);
        Assert.Equal(expectedLevel, only.EventLevel);
    }

    [Theory]
    [InlineData(1, AuditEventCode.SecretViewed)]                  // 0x1100 view
    [InlineData(2, AuditEventCode.AutoBackupSettingChanged)]      // 0x3800 config
    [InlineData(3, AuditEventCode.AuthSucceeded)]                 // 0x4001 auth
    [InlineData(4, AuditEventCode.SecretSaved)]                   // 0x5100 save
    [InlineData(5, AuditEventCode.SecretSoftDeleted)]             // 0x6100 delete
    [InlineData(6, AuditEventCode.EmergencyAccessCodeExecuted)]   // 0x7000 critical operation
    [InlineData(7, AuditEventCode.AuditLogsPurged)]               // 0xF700 system
    public async Task TC_ALV_02_CodeGroupFilter_KeepsOnlyThatGroup(int codeIndex, AuditEventCode expectedCode)
    {
        var vm = await LoadedVmAsync(
            Item(1, AuditEventCode.SecretViewed),
            Item(2, AuditEventCode.AutoBackupSettingChanged),
            Item(3, AuditEventCode.AuthSucceeded),
            Item(4, AuditEventCode.SecretSaved),
            Item(5, AuditEventCode.SecretSoftDeleted),
            Item(6, AuditEventCode.EmergencyAccessCodeExecuted),
            Item(7, AuditEventCode.AuditLogsPurged));

        vm.SelectedCodeIndex = codeIndex;

        var only = Assert.Single(vm.FilteredLogs);
        Assert.Equal(expectedCode, only.EventCode);
    }

    [Fact]
    public async Task TC_ALV_03_CodeGroupFilter_IndexZeroShowsAllGroups()
    {
        var vm = await LoadedVmAsync(
            Item(1, AuditEventCode.SecretViewed),
            Item(2, AuditEventCode.AuthSucceeded));

        vm.SelectedCodeIndex = 3;
        Assert.Single(vm.FilteredLogs);

        vm.SelectedCodeIndex = 0;
        Assert.Equal(2, vm.FilteredLogs.Count);
    }

    [Fact]
    public async Task TC_ALV_04_TextSearch_MatchesSummaryOrCode_CaseInsensitive_AndTrims()
    {
        var vm = await LoadedVmAsync(
            Item(1, AuditEventCode.SecretViewed, summary: "Viewed Bank Login"),
            Item(2, AuditEventCode.AuthSucceeded, summary: "Authentication succeeded"));

        vm.SearchText = "  bank LOGIN ";           // summary, case-insensitive, surrounding spaces ignored
        Assert.Equal(1, Assert.Single(vm.FilteredLogs).Id);

        vm.SearchText = "0x4001";                  // event code as displayed
        Assert.Equal(2, Assert.Single(vm.FilteredLogs).Id);

        vm.SearchText = "no such text";
        Assert.Empty(vm.FilteredLogs);

        vm.SearchText = "   ";                     // whitespace only = no filter
        Assert.Equal(2, vm.FilteredLogs.Count);
    }

    [Fact]
    public async Task TC_ALV_05_Filters_AreCombinedWithAnd()
    {
        var vm = await LoadedVmAsync(
            Item(1, AuditEventCode.SecretViewed, level: 0, summary: "alpha"),
            Item(2, AuditEventCode.SecretViewed, level: 1, summary: "alpha"),
            Item(3, AuditEventCode.AuthSucceeded, level: 1, summary: "alpha"),
            Item(4, AuditEventCode.SecretViewed, level: 1, summary: "beta"));

        vm.SelectedLevelIndex = 2;   // Notice (level 1): ids 2, 3, 4
        vm.SelectedCodeIndex  = 1;   // View group:        ids 2, 4
        vm.SearchText         = "alpha";

        Assert.Equal(2, Assert.Single(vm.FilteredLogs).Id);
    }

    [Fact]
    public async Task TC_ALV_06_FiltersSurviveReload()
    {
        var vm = await LoadedVmAsync(
            Item(1, AuditEventCode.SecretViewed),
            Item(2, AuditEventCode.AuthSucceeded));
        vm.SelectedCodeIndex = 3;

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, vm.SelectedCodeIndex);
        Assert.Equal(2, Assert.Single(vm.FilteredLogs).Id);
    }

    // ── Empty-state placeholders ─────────────────────────────────────────────

    [Fact]
    public async Task TC_ALV_07_EmptyState_DistinguishesNoActivityFromNoMatch()
    {
        // No audit log at all, no filters -> "no activity"
        var empty = await LoadedVmAsync();
        Assert.False(empty.HasActiveFilters);
        Assert.True(empty.ShowNoActivityPlaceholder);
        Assert.False(empty.ShowNoMatchPlaceholder);

        // Filters typed while the log is completely empty -> still "no activity" (nothing exists to match)
        empty.SearchText = "anything";
        Assert.True(empty.HasActiveFilters);
        Assert.True(empty.ShowNoActivityPlaceholder);
        Assert.False(empty.ShowNoMatchPlaceholder);

        // Rows exist and are visible -> neither placeholder
        var vm = await LoadedVmAsync(Item(1, AuditEventCode.SecretViewed, summary: "alpha"));
        Assert.False(vm.ShowNoActivityPlaceholder);
        Assert.False(vm.ShowNoMatchPlaceholder);

        // Rows exist but the filter hides every one -> "no match"
        vm.SearchText = "zzz";
        Assert.False(vm.HasLogs);
        Assert.True(vm.HasActiveFilters);
        Assert.False(vm.ShowNoActivityPlaceholder);
        Assert.True(vm.ShowNoMatchPlaceholder);

        // Clearing the filter restores the rows and clears both placeholders
        vm.SearchText = string.Empty;
        Assert.False(vm.ShowNoActivityPlaceholder);
        Assert.False(vm.ShowNoMatchPlaceholder);
    }

    [Fact]
    public async Task TC_ALV_08_HasActiveFilters_ReflectsEachFilter()
    {
        var vm = await LoadedVmAsync();
        Assert.False(vm.HasActiveFilters);

        vm.SearchText = "   ";              // whitespace alone does not count
        Assert.False(vm.HasActiveFilters);
        vm.SearchText = "x";
        Assert.True(vm.HasActiveFilters);
        vm.SearchText = string.Empty;
        Assert.False(vm.HasActiveFilters);

        vm.SelectedLevelIndex = 1;
        Assert.True(vm.HasActiveFilters);
        vm.SelectedLevelIndex = 0;
        Assert.False(vm.HasActiveFilters);

        vm.SelectedCodeIndex = 2;
        Assert.True(vm.HasActiveFilters);
        vm.SelectedCodeIndex = 0;
        Assert.False(vm.HasActiveFilters);
    }

    // ── Retention window / NeedsReload ───────────────────────────────────────

    [Fact]
    public async Task TC_ALV_09_LoadAsync_UsesSharedRetentionConstantAsWindow()
    {
        await LoadedVmAsync();

        var expected = DateTimeOffset.UtcNow - TimeSpan.FromDays(AppConstants.AuditLogRetentionDays);
        var since = Assert.Single(_service.SinceValues);
        Assert.True(Math.Abs((since - expected).TotalMinutes) < 1,
            $"since={since:O} should be ~{AppConstants.AuditLogRetentionDays} days before now ({expected:O})");
    }

    [Fact]
    public async Task TC_ALV_10_NeedsReload_StartsTrue_ClearedByLoad_ReArmedByFailure()
    {
        var vm = MakeVm();
        Assert.True(vm.NeedsReload);                     // first Page.Loaded must load

        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(vm.NeedsReload);

        _service.OnGetAllAfter = (_, _) => throw new InvalidOperationException("boom");
        await vm.LoadAsync(TestContext.Current.CancellationToken);   // must not throw (async void caller)
        Assert.True(vm.NeedsReload);                     // next display retries
        Assert.False(vm.IsLoading);
    }

    // ── IsActive / NeedsReload discipline ────────────────────────────────────

    [Fact]
    public async Task TC_ALV_11_InactiveVm_OnWrittenMessage_OnlyFlagsNeedsReload_NoQuery()
    {
        var vm = await LoadedVmAsync(Item(1, AuditEventCode.SecretViewed));
        Assert.False(vm.IsActive);                       // never resumed (another page is in front)
        Assert.False(vm.NeedsReload);
        int callsBefore = _service.GetAllAfterCallCount;

        SendWritten();

        Assert.Equal(callsBefore, _service.GetAllAfterCallCount);   // no DB query while hidden
        Assert.True(vm.NeedsReload);                                // recorded for the next display
    }

    [Fact]
    public async Task TC_ALV_12_ActiveVm_OnWrittenMessage_ReloadsImmediately_ThenPauseStopsIt()
    {
        var vm = await LoadedVmAsync(Item(1, AuditEventCode.SecretViewed));
        vm.Resume();
        int calls = _service.GetAllAfterCallCount;

        _service.OnGetAllAfter = (_, _) => Result(
            Item(1, AuditEventCode.SecretViewed),
            Item(2, AuditEventCode.AuthSucceeded));
        SendWritten();

        Assert.Equal(calls + 1, _service.GetAllAfterCallCount);     // queried right away
        Assert.Equal(2, vm.FilteredLogs.Count);
        Assert.False(vm.NeedsReload);                               // LoadAsync consumed the flag

        vm.Pause();
        SendWritten();

        Assert.Equal(calls + 1, _service.GetAllAfterCallCount);     // paused again: no further query
        Assert.True(vm.NeedsReload);
    }

    [Fact]
    public async Task TC_ALV_13_DisposedVm_DoesNotReceiveWrittenMessage()
    {
        var vm = await LoadedVmAsync(Item(1, AuditEventCode.SecretViewed));
        vm.Resume();
        vm.Dispose();
        int calls = _service.GetAllAfterCallCount;

        SendWritten();

        Assert.Equal(calls, _service.GetAllAfterCallCount);
        Assert.Empty(vm.FilteredLogs);                               // Dispose released the rows
    }

    // ── Load re-entrancy: the latest request wins ────────────────────────────

    [Fact]
    public async Task TC_ALV_14_OverlappingLoads_LatestWins_StaleResultAndSpinnerIgnored()
    {
        var gateOld = new TaskCompletionSource<IReadOnlyList<AuditLogDisplayItem>>();
        var gateNew = new TaskCompletionSource<IReadOnlyList<AuditLogDisplayItem>>();
        var gates = new Queue<TaskCompletionSource<IReadOnlyList<AuditLogDisplayItem>>>([gateOld, gateNew]);
        // Deliberately ignores the CancellationToken, like a service that finishes a query it can no longer abort.
        _service.OnGetAllAfter = (_, _) => gates.Dequeue().Task;
        var vm = MakeVm();

        var oldLoad = vm.LoadAsync(TestContext.Current.CancellationToken);
        var newLoad = vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(_service.Tokens[0].IsCancellationRequested);     // the first load was superseded
        Assert.False(_service.Tokens[1].IsCancellationRequested);

        // The superseded query finishes first with an OLD result: it must be discarded, and must not
        // hide the spinner of the newer load that is still running.
        gateOld.SetResult(new[] { Item(1, AuditEventCode.SecretViewed, summary: "old") });
        await oldLoad;
        Assert.Empty(vm.FilteredLogs);
        Assert.True(vm.IsLoading);

        // The newest query finishes: its result is shown and the spinner stops.
        gateNew.SetResult(new[] { Item(2, AuditEventCode.AuthSucceeded, summary: "new") });
        await newLoad;
        Assert.Equal("new", Assert.Single(vm.FilteredLogs).Summary);
        Assert.False(vm.IsLoading);
    }

    // ── Fake ─────────────────────────────────────────────────────────────────

    /// <summary>Only GetAllAfterAsync is used by AuditLogViewModel; every other member is unsupported.</summary>
    private sealed class FakeAuditLogService : IAuditLogService
    {
        public Func<DateTimeOffset, CancellationToken, Task<IReadOnlyList<AuditLogDisplayItem>>> OnGetAllAfter { get; set; }
            = (_, _) => Task.FromResult<IReadOnlyList<AuditLogDisplayItem>>(Array.Empty<AuditLogDisplayItem>());

        public int GetAllAfterCallCount { get; private set; }
        public List<DateTimeOffset> SinceValues { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<IReadOnlyList<AuditLogDisplayItem>> GetAllAfterAsync(
            DateTimeOffset since, DekScope dek, CancellationToken ct = default)
        {
            GetAllAfterCallCount++;
            SinceValues.Add(since);
            Tokens.Add(ct);
            return OnGetAllAfter(since, ct);
        }

        public Task LogAsync(AuditEventCode code, AuditPayload? payload, DekScope dek, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task LogAuthFailedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AuditLogDisplayItem>> GetRecentAsync(int count, DekScope dek, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task PurgeOldLogsAsync(DekScope? purgeLogDek = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> HasAnyLogsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task FlushPreAuthFailuresAsync(int count, CancellationToken ct = default) => throw new NotSupportedException();
        public Task FlushPendingRestoreAuditAsync(DekScope dek, int currentDbNumber, string dataDir, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
