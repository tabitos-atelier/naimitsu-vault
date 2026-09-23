// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Repositories;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only IDbContextFactory&lt;UnifiedDbContext&gt;.
/// Throws an exception when called. Used in tests that must never touch the unified DB,
/// to prove that "it was in fact never called."
/// </summary>
internal sealed class ExplodingUnifiedDbContextFactory : IDbContextFactory<UnifiedDbContext>
{
    private static readonly InvalidOperationException _ex =
        new("UnifiedDbContext was accessed — this test should not touch the unified DB");

    public UnifiedDbContext CreateDbContext() => throw _ex;

    public Task<UnifiedDbContext> CreateDbContextAsync(CancellationToken _ = default)
        => throw _ex;
}
