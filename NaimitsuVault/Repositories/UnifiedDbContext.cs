// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;

namespace NaimitsuVault.Repositories;

/// <summary>
/// EF Core context for the unified DB (NaimitsuVault.nkdb).
/// Tables: UnifiedMetadata (common settings, K_shared key wrap slots) / VaultRegistries
/// No session guard: writes to the unified DB are independent of the vault session.
/// </summary>
public class UnifiedDbContext(DbContextOptions<UnifiedDbContext> options) : DbContext(options)
{
    public DbSet<UnifiedMetadata> Metadata        => Set<UnifiedMetadata>();
    public DbSet<VaultRegistry>   VaultRegistries => Set<VaultRegistry>();

    public override int SaveChanges()
        => throw new NotSupportedException(
            "Synchronous writes are not supported in this app. Use SaveChangesAsync instead.");

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw new NotSupportedException(
            "Synchronous writes are not supported in this app. Use SaveChangesAsync instead.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnifiedMetadata>(e =>
        {
            e.ToTable("UnifiedMetadata");
            e.HasKey(m => m.ConfigKey);
        });

        modelBuilder.Entity<VaultRegistry>(e =>
        {
            e.ToTable("VaultRegistries");
            e.HasKey(v => v.DbNumber);
            e.Property(v => v.ShadowFileKey).HasColumnType("BLOB");
        });
    }
}
