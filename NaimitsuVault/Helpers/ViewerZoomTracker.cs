// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Helpers;

/// <summary>
/// Tells apart zoom changes the viewer requested itself (fit-to-window/width, a chosen preset) from
/// zoom changes the user made directly on the ScrollViewer (Ctrl+wheel, pinch), so "Auto" can be
/// released only by the latter.
/// </summary>
/// <remarks>
/// ScrollViewer handles Ctrl+wheel and pinch internally, so there is no input event to hook: the only
/// signal is ViewChanged reporting a new zoom. The result of our own ChangeView call arrives on a later
/// dispatcher tick, and a burst of requests (the window being dragged to a new size) can land after a
/// newer request was already made, so a single "just requested" flag is not enough. Requested targets are
/// kept until a ViewChanged reports them. UI-independent so the classification can be unit tested.
/// </remarks>
internal sealed class ViewerZoomTracker
{
    private const float Epsilon = 0.002f;
    private const int MaxPending = 8;

    private readonly List<float> _pendingTargets = [];
    private float _observedZoom = float.NaN;

    /// <summary>
    /// Forgets everything. Call when a different file is shown: the ScrollViewer may still report the
    /// previous file's zoom, so the next observation must only become the new baseline.
    /// </summary>
    public void Reset()
    {
        _pendingTargets.Clear();
        _observedZoom = float.NaN;
    }

    /// <summary>
    /// Registers a zoom the viewer is about to request via ChangeView. A request equal to the current
    /// zoom is not registered: it raises no zoom-changing ViewChanged, so it would only linger.
    /// </summary>
    public void RegisterRequest(float currentZoom, float targetZoom)
    {
        if (Math.Abs(currentZoom - targetZoom) <= Epsilon) return;
        if (_pendingTargets.Count >= MaxPending) _pendingTargets.RemoveAt(0);
        _pendingTargets.Add(targetZoom);
    }

    /// <summary>
    /// Feeds one ViewChanged observation. Returns true when the zoom changed for a reason other than
    /// a registered request, i.e. the user zoomed.
    /// </summary>
    public bool OnViewChanged(float zoom)
    {
        bool changed = !float.IsNaN(_observedZoom) && Math.Abs(zoom - _observedZoom) > Epsilon;
        _observedZoom = zoom;

        bool requested = ConsumePending(zoom);
        return changed && !requested;
    }

    // Landing on the newest matching request supersedes everything requested before it (those either
    // landed already or were replaced before they could).
    private bool ConsumePending(float zoom)
    {
        var index = _pendingTargets.FindLastIndex(t => Math.Abs(t - zoom) <= Epsilon);
        if (index < 0) return false;
        _pendingTargets.RemoveRange(0, index + 1);
        return true;
    }
}
