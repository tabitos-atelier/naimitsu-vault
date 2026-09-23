// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NaimitsuVault.Models;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Repositories;

/// <summary>
/// EF Core context for the vault DB ([48-character Base64Url] file).
/// Tables: Secrets / StoredFiles / SecretFileLinks / ProfileFileLinks
///          / VaultMetadata / FaviconCache / SecretHistory / SecretDrafts / AuditLogs
/// </summary>
public class VaultDbContext : DbContext
{
    private readonly ISessionGenerationGuard _sessionGuard;
    private readonly long _capturedSessionId;

    public VaultDbContext(DbContextOptions options,
                          ISessionGenerationGuard sessionGuard)
        : base(options)
    {
        _sessionGuard   = sessionGuard;
        _capturedSessionId = sessionGuard.CurrentSessionId;
    }

    public override async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        _sessionGuard.ThrowIfInvalid(_capturedSessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        _sessionGuard.ThrowIfInvalid(_capturedSessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges()
        => throw new NotSupportedException(
            "Synchronous writes are not supported in this app. Use SaveChangesAsync instead.");

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw new NotSupportedException(
            "Synchronous writes are not supported in this app. Use SaveChangesAsync instead.");

    public DbSet<Secret>         Secrets       => Set<Secret>();
    public DbSet<StoredFile>     StoredFiles   => Set<StoredFile>();
    public DbSet<SecretFileLink> SecretFileLinks  => Set<SecretFileLink>();
    public DbSet<ProfileFileLink> ProfileFileLinks => Set<ProfileFileLink>();
    public DbSet<VaultMetadata> Metadata => Set<VaultMetadata>();
    public DbSet<FaviconCache>   FaviconCache  => Set<FaviconCache>();
    public DbSet<SecretHistory>  SecretHistory => Set<SecretHistory>();
    public DbSet<SecretDraft>    SecretDrafts  => Set<SecretDraft>();
    public DbSet<AuditLog>       AuditLogs     => Set<AuditLog>();

    // ToUniversalTime() normalizes DateTimeKind.Local/Unspecified inputs before construction - the
    // DateTimeOffset(DateTime, TimeSpan) constructor throws ArgumentException for Kind.Local unless
    // the offset argument matches the local time zone's own offset, which TimeSpan.Zero rarely does.
    private static readonly ValueConverter<DateTime, long> DateTimeToUnix = new(
        v => new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds(),
        v => DateTimeOffset.FromUnixTimeSeconds(v).UtcDateTime);

    private static readonly ValueConverter<DateTime?, long?> NullableDateTimeToUnix = new(
        v => v.HasValue ? new DateTimeOffset(v.Value.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds() : (long?)null,
        v => v.HasValue ? (DateTime?)DateTimeOffset.FromUnixTimeSeconds(v.Value).UtcDateTime : null);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Secret>(e =>
        {
            e.ToTable("Secrets");
            e.HasIndex(s => s.CategoryNum);
            e.HasIndex(s => s.UpdatedAt);
            e.HasIndex(s => s.DeletedAt);
            e.Property(s => s.CreatedAt).HasConversion(DateTimeToUnix);
            e.Property(s => s.UpdatedAt).HasConversion(DateTimeToUnix);
            e.Property(s => s.DeletedAt).HasConversion(NullableDateTimeToUnix);
            e.Property(s => s.ExpiresAt).HasConversion(NullableDateTimeToUnix);
            e.Property(s => s.GeneratorSymbols).HasColumnName("GenSymbols");
        });

        modelBuilder.Entity<StoredFile>(e =>
        {
            e.ToTable("StoredFiles");
            e.HasIndex(f => f.CreatedAt);
            e.HasIndex(f => f.FileHash).IsUnique();
            e.HasIndex(f => f.DeletedAt);
            e.Property(f => f.FileModifiedAt).HasConversion(DateTimeToUnix);
            e.Property(f => f.CreatedAt).HasConversion(DateTimeToUnix);
            e.Property(f => f.UpdatedAt).HasConversion(DateTimeToUnix);
            e.Property(f => f.DeletedAt).HasConversion(NullableDateTimeToUnix);
            e.Property(f => f.ContentTypeCode).HasColumnName("ContentType");
        });

        modelBuilder.Entity<SecretFileLink>(e =>
        {
            e.ToTable("SecretFileLinks");
            e.HasKey(l => new { l.SecretId, l.FileId });
            e.HasIndex(l => l.FileId);
        });

        modelBuilder.Entity<ProfileFileLink>(e =>
        {
            e.ToTable("ProfileFileLinks");
            e.HasKey(l => l.FileId);
        });

        modelBuilder.Entity<VaultMetadata>(e =>
        {
            e.ToTable("VaultMetadata");
            e.HasKey(m => m.ConfigKey);
        });

        modelBuilder.Entity<FaviconCache>(e =>
        {
            e.ToTable("FaviconCache");
            e.HasKey(f => f.DomainHmac);
            e.Property(f => f.DomainHmac).HasColumnName("Domain");
            e.Property(f => f.FetchedAt).HasConversion(DateTimeToUnix);
            var bytesComparer = new ValueComparer<byte[]>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                v => ByteArrayHashCode(v),
                v => v.ToArray());
            e.Property(f => f.DomainHmac).Metadata.SetValueComparer(bytesComparer);
        });

        modelBuilder.Entity<SecretHistory>(e =>
        {
            e.ToTable("SecretHistory");
            e.HasKey(h => h.SecretId);
            e.HasOne<Secret>()
             .WithOne()
             .HasForeignKey<SecretHistory>(h => h.SecretId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(h => h.SlotASavedAt).HasConversion(NullableDateTimeToUnix);
            e.Property(h => h.SlotBSavedAt).HasConversion(NullableDateTimeToUnix);
            e.Property(h => h.SlotCSavedAt).HasConversion(NullableDateTimeToUnix);
        });

        modelBuilder.Entity<SecretDraft>(e =>
        {
            e.ToTable("SecretDrafts");
            e.HasKey(d => d.SecretId);
            e.HasOne<Secret>()
             .WithOne()
             .HasForeignKey<SecretDraft>(d => d.SecretId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Property(d => d.SavedAt).HasConversion(DateTimeToUnix);
            e.Property(d => d.SnapshotBlob).HasColumnName("Content");
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasKey(a => a.Id);
            // EventLevel is auto-mapped by EF Core convention, but this explicitly guarantees consistency with the raw INSERT.
            e.Property(a => a.EventLevel).IsRequired().HasColumnName("EventLevel");
            e.HasIndex(a => a.CreatedAt).IsDescending().HasDatabaseName("idx_auditlogs_createdat");
        });
    }

    private static int ByteArrayHashCode(byte[] v)
    {
        if (v is null) return 0;
        var hc = new HashCode();
        hc.AddBytes(v);
        return hc.ToHashCode();
    }
}
