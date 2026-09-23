// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

public sealed class NoOpAutoTypeService : IAutoTypeService
{
    public nint GetPreviousTargetHwnd() => nint.Zero;

    public Task<DirectInjectionResult> InjectAsync(
        SecureCharBuffer buffer, nint hwndTarget, bool appendEnter, CancellationToken ct = default)
        => Task.FromResult(DirectInjectionResult.Success());
}
