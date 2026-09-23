// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// ViewerZoomTracker classifies each ViewChanged zoom observation as either the landing of a zoom the
/// viewer requested itself (fit, preset) or a zoom the user made on the ScrollViewer (Ctrl+wheel, pinch),
/// so "Auto" is released only by the latter.
/// TC-VZT-01: the first observation only becomes the baseline, whatever its value.
/// TC-VZT-02: an unchanged zoom (scroll-only ViewChanged) is not a user zoom.
/// TC-VZT-03: a changed zoom that no request explains is a user zoom.
/// TC-VZT-04: the landing of a registered request is not a user zoom, and a later user zoom still is.
/// TC-VZT-05: a request equal to the current zoom is not registered, so it can't swallow a later user zoom.
/// TC-VZT-06: with a burst of requests, an older request landing after a newer one was made is still
///            recognised as ours (window dragged to a new size).
/// TC-VZT-07: landing on the newest request discards the older, superseded ones.
/// TC-VZT-08: Reset() drops pending requests and makes the next observation a baseline (file switch).
/// TC-VZT-09: a landing within float rounding tolerance of the request still counts as ours.
/// TC-VZT-10: at most 8 requests are remembered; the oldest is forgotten.
/// </summary>
public sealed class ViewerZoomTrackerTests
{
    // ── TC-VZT-01 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_01_FirstObservation_IsOnlyABaseline()
    {
        var tracker = new ViewerZoomTracker();

        Assert.False(tracker.OnViewChanged(0.37f));
    }

    // ── TC-VZT-02 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_02_UnchangedZoom_IsNotUserZoom()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);

        Assert.False(tracker.OnViewChanged(1f)); // e.g. a scroll-only ViewChanged
    }

    // ── TC-VZT-03 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_03_ChangedZoomWithoutRequest_IsUserZoom()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);

        Assert.True(tracker.OnViewChanged(1.25f));
    }

    // ── TC-VZT-04 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_04_LandingOfRegisteredRequest_IsNotUserZoom_ButLaterUserZoomIs()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);

        tracker.RegisterRequest(currentZoom: 1f, targetZoom: 0.26f);
        Assert.False(tracker.OnViewChanged(0.26f));

        Assert.True(tracker.OnViewChanged(0.4f));
    }

    // ── TC-VZT-05 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_05_RequestEqualToCurrentZoom_LeavesNoStaleEntry()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(0.5f);

        tracker.RegisterRequest(currentZoom: 0.5f, targetZoom: 0.5f); // raises no zoom-changing ViewChanged
        Assert.True(tracker.OnViewChanged(1f));                       // user zooms in
        Assert.True(tracker.OnViewChanged(0.5f));                     // ...and back to the old value: still the user
    }

    // ── TC-VZT-06 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_06_BurstOfRequests_OlderLandingAfterNewerRequest_IsStillOurs()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);

        // The window is dragged: a second fit is requested before the first has landed, so the
        // ScrollViewer still reports the old zoom when it is registered.
        tracker.RegisterRequest(currentZoom: 1f, targetZoom: 0.6f);
        tracker.RegisterRequest(currentZoom: 1f, targetZoom: 0.5f);

        Assert.False(tracker.OnViewChanged(0.6f));
        Assert.False(tracker.OnViewChanged(0.5f));
        Assert.True(tracker.OnViewChanged(0.8f));
    }

    // ── TC-VZT-07 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_07_LandingOnNewestRequest_DiscardsSupersededOnes()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);

        tracker.RegisterRequest(currentZoom: 1f, targetZoom: 0.6f); // superseded, never lands
        tracker.RegisterRequest(currentZoom: 1f, targetZoom: 0.5f);
        Assert.False(tracker.OnViewChanged(0.5f));

        // 0.6 was superseded and must not linger and hide a genuine user zoom to that value.
        Assert.True(tracker.OnViewChanged(0.6f));
    }

    // ── TC-VZT-08 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_08_Reset_DropsPendingRequestsAndRestartsBaseline()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);
        tracker.RegisterRequest(currentZoom: 1f, targetZoom: 0.5f);

        tracker.Reset(); // a different file is shown

        // The ScrollViewer still reports the previous file's zoom: baseline only, not a change.
        Assert.False(tracker.OnViewChanged(2f));
        // The dropped request must not hide a real user zoom to its old target.
        Assert.True(tracker.OnViewChanged(0.5f));
    }

    // ── TC-VZT-09 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_09_LandingWithinFloatTolerance_IsStillOurs()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);

        tracker.RegisterRequest(currentZoom: 1f, targetZoom: 0.2643f);

        Assert.False(tracker.OnViewChanged(0.2645f));
    }

    // ── TC-VZT-10 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TC_VZT_10_OnlyEightRequestsAreRemembered()
    {
        var tracker = new ViewerZoomTracker();
        tracker.OnViewChanged(1f);

        for (int i = 1; i <= 9; i++)
            tracker.RegisterRequest(currentZoom: 1f, targetZoom: 1f - i * 0.05f); // 0.95 ... 0.55

        // The oldest (0.95) was forgotten: it now reads as a user zoom. The newest is still ours.
        Assert.True(tracker.OnViewChanged(0.95f));
        Assert.False(tracker.OnViewChanged(0.55f));
    }
}
