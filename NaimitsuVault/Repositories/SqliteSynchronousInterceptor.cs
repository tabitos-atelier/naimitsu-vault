// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NaimitsuVault.Repositories;

/// <summary>
/// Unlike journal_mode, PRAGMA synchronous is not persisted to the DB file and resets on every
/// connection, so it must be re-set each time a physical connection is opened. Applies NORMAL — the
/// standard pairing with WAL mode — uniformly across all DbContexts (UnifiedDbContext / AppDbContext / VaultDbContext).
/// </summary>
public sealed class SqliteSynchronousInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SetSynchronousNormal(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=NORMAL";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void SetSynchronousNormal(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=NORMAL";
        cmd.ExecuteNonQuery();
    }
}
