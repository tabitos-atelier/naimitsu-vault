// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only recording implementation of IShellWindowProxy.
/// Records the call order of ScrubViewReferences / Close into a List&lt;string&gt;.
/// Used by LockCycleStepOrderTests to verify the ordering invariant.
/// </summary>
internal sealed class RecordingShellWindowProxy : IShellWindowProxy
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls.AsReadOnly();

    public void ScrubViewReferences() => _calls.Add("ScrubViewReferences");
    public void Close()               => _calls.Add("Close");
}
