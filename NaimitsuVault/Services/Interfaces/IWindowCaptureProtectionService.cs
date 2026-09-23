// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public interface IWindowCaptureProtectionService
{
    /// <summary>
    /// Applies capture protection to the specified HWND.
    /// enabled=true -> WDA_EXCLUDEFROMCAPTURE (blacked out in Snipping Tool, OBS, etc.),
    /// enabled=false -> WDA_NONE (normal display).
    /// Silently skips if hwnd is Zero.
    /// </summary>
    void Apply(nint hwnd, bool enabled);
}
