// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Repositories;

namespace NaimitsuVault.Tests;

/// <summary>
/// A test-only IDbContextFactory&lt;AppDbContext&gt;.
/// Throws the configured exception whenever <see cref="CreateDbContext"/> /
/// <see cref="CreateDbContextAsync"/> is called, used to verify the repository layer's
/// exception handling.
/// </summary>
internal sealed class FaultInjectionDbContextFactory(Exception toThrow)
    : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext()
        => throw toThrow;

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => throw toThrow;
}
