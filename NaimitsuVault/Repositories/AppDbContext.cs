// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Repositories;

/// <summary>
/// EF Core context for the active vault (secret DB).
/// Acts as a thin subclass of VaultDbContext, letting repositories/services that inject
/// IDbContextFactory&lt;AppDbContext&gt; access the vault DB in a type-safe way.
///
/// AppDbContextFactory creates instances of this class using a dynamic path
/// (via IVaultConnectionProvider). This is not dead code slated for removal.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options,
                          ISessionGenerationGuard sessionGuard)
    : VaultDbContext(options, sessionGuard)
{
}
