// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Lightweight registry that tracks ISessionSaveTracker instances already materialized within the session scope.
/// Introduced to eliminate forced wake-up of un-materialized VMs via GetServices&lt;ISessionSaveTracker&gt;().
///
/// Thread safety:
/// LockAsync (UI thread) and the VM constructor (UI thread) run on the same thread, so no additional
/// lock is needed for Read/Write on the List.
/// Rev7's HasThreadAccess guard ensures LockAsync always runs on the UI thread.
/// </summary>
public sealed class SessionTaskRegistry
{
    private readonly List<ISessionSaveTracker> _trackers = new();

    /// <summary>
    /// Called from a VM's constructor. Registers with the registry the moment it is materialized.
    /// </summary>
    public void Register(ISessionSaveTracker tracker)
        => _trackers.Add(tracker);

    /// <summary>
    /// Returns a snapshot of all currently registered ISessionSaveTracker instances.
    /// Called only during LockAsync's Step 1.
    /// </summary>
    public IReadOnlyList<ISessionSaveTracker> Snapshot()
        => _trackers.ToList();
}
