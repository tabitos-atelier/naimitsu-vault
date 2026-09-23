// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.Services.Interfaces;

public interface IAutoTypeService
{
    nint GetPreviousTargetHwnd();

    /// <summary>
    /// Injects the contents of <paramref name="buffer"/> into <paramref name="hwndTarget"/> via Win32 SendInput.
    /// <para>
    /// Ownership: the caller retains ownership of <paramref name="buffer"/>.
    /// This method only reads <paramref name="buffer"/> and never calls <c>Dispose()</c> on it.
    /// The caller is responsible for zeroing it via <c>using</c> or <c>Dispose()</c> after the method completes.
    /// </para>
    /// </summary>
    Task<DirectInjectionResult> InjectAsync(SecureCharBuffer buffer, nint hwndTarget, bool appendEnter, CancellationToken ct = default);
}
