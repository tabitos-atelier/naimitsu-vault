// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;

namespace NaimitsuVault.Repositories;

public class SecretHistoryRepository(IDbContextFactory<AppDbContext> factory)
{
    // SlotOrder packed-int encoding
    // bits[11:8]=Gen0 ID, bits[7:4]=Gen1 ID, bits[3:0]=Gen2 ID
    // 0=no slot, 1=A, 2=B, 3=C
    private const int SlotNone = 0, SlotIdA = 1, SlotIdB = 2, SlotIdC = 3;

    private static int Gen0Id(int packed) => (packed >> 8) & 0xF;
    private static int Gen1Id(int packed) => (packed >> 4) & 0xF;
    private static int Gen2Id(int packed) =>  packed       & 0xF;
    private static int GenCount(int packed)
        => Gen0Id(packed) == 0 ? 0 : Gen1Id(packed) == 0 ? 1 : Gen2Id(packed) == 0 ? 2 : 3;
    private static int Pack(int g0, int g1, int g2) => (g0 << 8) | (g1 << 4) | g2;

    // Returns the first ID among A/B/C not used by used1/used2/used3
    private static int UnusedSlot(int used1, int used2, int used3)
    {
        if (used1 != SlotIdA && used2 != SlotIdA && used3 != SlotIdA) return SlotIdA;
        if (used1 != SlotIdB && used2 != SlotIdB && used3 != SlotIdB) return SlotIdB;
        return SlotIdC;
    }

    private static (byte[]? Snap, DateTime? At) GetSlotData(SecretHistory row, int slotId) => slotId switch
    {
        SlotIdA => (row.SlotA, row.SlotASavedAt),
        SlotIdB => (row.SlotB, row.SlotBSavedAt),
        SlotIdC => (row.SlotC, row.SlotCSavedAt),
        _       => (null, null),
    };

    private static void SetSlotData(SecretHistory row, int slotId, byte[]? snap, DateTime? at)
    {
        switch (slotId)
        {
            case SlotIdA: row.SlotA = snap; row.SlotASavedAt = at; break;
            case SlotIdB: row.SlotB = snap; row.SlotBSavedAt = at; break;
            case SlotIdC: row.SlotC = snap; row.SlotCSavedAt = at; break;
        }
    }

    /// <summary>
    /// Returns the encrypted Gen1/Gen2 snapshots and their save times.
    /// Gen0 (authoritative data lives in the Secrets table) is skipped.
    /// </summary>
    public async Task<(byte[]? Gen1Enc, DateTime? Gen1At, byte[]? Gen2Enc, DateTime? Gen2At)> GetSnapshotsAsync(int secretId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.SecretHistory.AsNoTracking().FirstOrDefaultAsync(h => h.SecretId == secretId);
        if (row == null || row.SlotOrder == 0) return (null, null, null, null);

        int packed = row.SlotOrder;
        int gen1Id = Gen1Id(packed);
        int gen2Id = Gen2Id(packed);
        var (g1Snap, g1At) = gen1Id != SlotNone ? GetSlotData(row, gen1Id) : ((byte[]?)null, (DateTime?)null);
        var (g2Snap, g2At) = gen2Id != SlotNone ? GetSlotData(row, gen2Id) : ((byte[]?)null, (DateTime?)null);
        return (g1Snap, g1At, g2Snap, g2At);
    }

    private static string SlotLabel(int slotId) => slotId switch
    {
        SlotIdA => "A", SlotIdB => "B", SlotIdC => "C", _ => "?"
    };

    /// <summary>
    /// After a finalized save, writes the new Gen0 snapshot into the sacrifice slot and rotates SlotOrder,
    /// then atomically clears any pending draft (SecretDrafts row) in the same transaction.
    /// IMPORTANT: call with the new state only after the save to the Secrets table completes (do not call out of order).
    /// Returns whether an existing generation was actually rotated (false on the very first push, where
    /// nothing is displaced), and - only when all 3 slots were already full - the label (A/B/C) of the
    /// slot whose prior content was overwritten and lost. The caller uses this to raise
    /// TimeMachineGenRotated/TimeMachineSlotDeleted audit entries without this repository depending on IAuditLogService.
    /// </summary>
    public async Task<(bool Rotated, string? SacrificedSlotLabel)> PushAsync(int secretId, byte[] snapshotEncrypted, DateTime savedAt)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        // Lightweight fetch of SlotOrder only (never loads the large BLOB slot columns)
        var packedNullable = await db.SecretHistory
            .AsNoTracking()
            .Where(h => h.SecretId == secretId)
            .Select(h => (int?)h.SlotOrder)
            .FirstOrDefaultAsync();

        int packed = packedNullable ?? 0;
        int g0 = Gen0Id(packed), g1 = Gen1Id(packed), g2 = Gen2Id(packed);
        int count = GenCount(packed);
        bool rotated = count > 0;
        string? sacrificedSlotLabel = null;

        int sacrificeId, newPacked;
        switch (count)
        {
            case 0:
                sacrificeId = SlotIdA;
                newPacked   = Pack(SlotIdA, SlotNone, SlotNone);
                break;
            case 1:
                sacrificeId = UnusedSlot(g0, SlotNone, SlotNone);
                newPacked   = Pack(sacrificeId, g0, SlotNone);
                break;
            case 2:
                sacrificeId = UnusedSlot(g0, g1, SlotNone);
                newPacked   = Pack(sacrificeId, g0, g1);
                break;
            default: // All 3 slots full: sacrifice Gen2, pushing it out
                sacrificeId = g2;
                newPacked   = Pack(g2, g0, g1);
                sacrificedSlotLabel = SlotLabel(g2);
                break;
        }

        if (packedNullable == null)
        {
            // No row → set the sacrifice slot column and INSERT (no BLOB loading)
            var newRow = new SecretHistory { SecretId = secretId, SlotOrder = newPacked };
            SetSlotData(newRow, sacrificeId, snapshotEncrypted, savedAt);
            db.SecretHistory.Add(newRow);
            await db.SaveChangesAsync();
        }
        else
        {
            // Row exists → directly update only the sacrifice slot column and SlotOrder, targeted
            switch (sacrificeId)
            {
                case SlotIdA:
                    await db.SecretHistory.Where(h => h.SecretId == secretId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(h => h.SlotA, snapshotEncrypted)
                            .SetProperty(h => h.SlotASavedAt, savedAt)
                            .SetProperty(h => h.SlotOrder, newPacked));
                    break;
                case SlotIdB:
                    await db.SecretHistory.Where(h => h.SecretId == secretId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(h => h.SlotB, snapshotEncrypted)
                            .SetProperty(h => h.SlotBSavedAt, savedAt)
                            .SetProperty(h => h.SlotOrder, newPacked));
                    break;
                case SlotIdC:
                    await db.SecretHistory.Where(h => h.SecretId == secretId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(h => h.SlotC, snapshotEncrypted)
                            .SetProperty(h => h.SlotCSavedAt, savedAt)
                            .SetProperty(h => h.SlotOrder, newPacked));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected sacrificeId: {sacrificeId}");
            }
        }

        // Atomically clear any pending draft now that this save has finalized
        // (a 0-row delete is harmless when there was no draft).
        await db.SecretDrafts
            .Where(d => d.SecretId == secretId)
            .ExecuteDeleteAsync();

        await tx.CommitAsync();
        return (rotated, sacrificedSlotLabel);
    }

    /// <summary>
    /// Returns the current SlotOrder. null if no row exists, 0 if the row exists but has no history.
    /// Used by SaveSecretAsync to decide whether to create a pre-first-save snapshot.
    /// </summary>
    public async Task<int?> GetSlotOrderAsync(int secretId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.SecretHistory.AsNoTracking().FirstOrDefaultAsync(h => h.SecretId == secretId);
        return row?.SlotOrder;
    }

    /// <summary>
    /// Rolls back to the specified generation (pointer shuffle only).
    /// Does not rewrite a single bit of any slot's contents. Only updates SlotOrder's packed int.
    /// targetGenerationIndex: 0 = restore Gen1, 1 = restore Gen2.
    /// The old Gen0 is preserved as the new Gen1, so all 3 slots stay alive.
    /// </summary>
    public async Task SwapSlotOrderAsync(int secretId, int targetGenerationIndex)
    {
        await using var db = await factory.CreateDbContextAsync();

        // Lightweight fetch of SlotOrder only (never loads the BLOB columns)
        var packed = await db.SecretHistory
            .AsNoTracking()
            .Where(h => h.SecretId == secretId)
            .Select(h => (int?)h.SlotOrder)
            .FirstOrDefaultAsync();

        if (packed == null || packed == 0) return;
        if (GenCount(packed.Value) <= targetGenerationIndex + 1) return;   // boundary guard

        int g0 = Gen0Id(packed.Value), g1 = Gen1Id(packed.Value), g2 = Gen2Id(packed.Value);
        int newPacked = targetGenerationIndex == 0
            ? Pack(g1, g0, g2)   // Promote Gen1: [g1, g0, g2]
            : Pack(g2, g0, g1);  // Promote Gen2: [g2, g0, g1]

        await db.SecretHistory
            .Where(h => h.SecretId == secretId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.SlotOrder, newPacked));
    }

    /// <summary>Explicit cleanup of this Secret's finalized history row. Normally unnecessary (ON DELETE CASCADE handles it).</summary>
    public async Task DeleteAllForSecretAsync(int secretId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.SecretHistory
            .Where(h => h.SecretId == secretId)
            .ExecuteDeleteAsync();
    }
}
