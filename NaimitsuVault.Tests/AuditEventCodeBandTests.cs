// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression-prevention tests for AuditEventCode's hex-band scheme.
///
/// Automatically detects, by iterating over all Enum.GetValues, whether a future code
/// addition introduces an incorrect band (out of band, or outside the integer's range).
/// </summary>
public sealed class AuditEventCodeBandTests
{
    // 0xXYZZ scheme: high nibble = category band
    private static readonly (int Min, int Max)[] DefinedBands =
    [
        (0x1000, 0x1FFF),   // browsing
        (0x3000, 0x3FFF),   // configuration/settings changes
        (0x4000, 0x4FFF),   // authentication
        (0x5000, 0x5FFF),   // saves
        (0x6000, 0x6FFF),   // deletion/purge
        (0x7000, 0x7FFF),   // high-risk operations
        (0xF000, 0xFFFF),   // system
    ];

    // ── Exhaustive check: every enum value must fall within one of the bands ──────────────────────

    [Fact]
    public void AllCodes_AreInDefinedBand()
    {
        foreach (var code in Enum.GetValues<AuditEventCode>())
        {
            int v = (int)code;
            bool inBand = DefinedBands.Any(b => v >= b.Min && v <= b.Max);
            Assert.True(inBand, $"AuditEventCode.{code} (0x{v:X4}) does not belong to any band");
        }
    }

    // ── Browsing category (0x1000 band) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(AuditEventCode.SecretViewed)]
    [InlineData(AuditEventCode.TimeMachineViewed)]
    [InlineData(AuditEventCode.FileViewed)]
    [InlineData(AuditEventCode.ProfileViewed)]
    public void BrowsingCodes_AreIn0x1000Band(AuditEventCode code)
        => Assert.InRange((int)code, 0x1000, 0x1FFF);

    // ── Configuration/settings change category (0x3000 band) ─────────────────────────────────────────

    [Theory]
    [InlineData(AuditEventCode.MasterPasswordChanged)]
    [InlineData(AuditEventCode.AutoBackupSettingChanged)]
    [InlineData(AuditEventCode.AutoLockSettingChanged)]
    [InlineData(AuditEventCode.ScreenCaptureProtectionChanged)]
    [InlineData(AuditEventCode.WindowsHelloChanged)]
    [InlineData(AuditEventCode.FaviconAutoFetchChanged)]
    public void SettingsChangedCodes_AreIn0x3000Band(AuditEventCode code)
        => Assert.InRange((int)code, 0x3000, 0x3FFF);

    // ── Authentication category (0x4000 band) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(AuditEventCode.AuthFailed)]
    [InlineData(AuditEventCode.AuthSucceeded)]
    public void AuthCodes_AreIn0x4000Band(AuditEventCode code)
        => Assert.InRange((int)code, 0x4000, 0x4FFF);

    // ── Save category (0x5000 band) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(AuditEventCode.SecretSaved)]
    [InlineData(AuditEventCode.ProfileSaved)]
    [InlineData(AuditEventCode.TimeMachineRestored)]
    [InlineData(AuditEventCode.FileAdded)]
    public void SaveCodes_AreIn0x5000Band(AuditEventCode code)
        => Assert.InRange((int)code, 0x5000, 0x5FFF);

    // ── Deletion/purge category (0x6000 band) ──────────────────────────────────────────

    [Theory]
    [InlineData(AuditEventCode.SecretSoftDeleted)]
    [InlineData(AuditEventCode.SecretUndeleted)]
    [InlineData(AuditEventCode.SecretPermanentlyDeleted)]
    [InlineData(AuditEventCode.SecretAutoPurgedByExpiry)]
    [InlineData(AuditEventCode.TimeMachineSlotDeleted)]
    [InlineData(AuditEventCode.TimeMachineGenRotated)]
    [InlineData(AuditEventCode.FilePermanentlyDeleted)]
    [InlineData(AuditEventCode.FileSoftDeleted)]
    [InlineData(AuditEventCode.FileUndeleted)]
    [InlineData(AuditEventCode.FileAutoPurgedByExpiry)]
    public void DeletionCodes_AreIn0x6000Band(AuditEventCode code)
        => Assert.InRange((int)code, 0x6000, 0x6FFF);

    // ── High-risk operation category (0x7000 band) ──────────────────────────────────────────

    [Theory]
    [InlineData(AuditEventCode.EmergencyAccessCodeExecuted)]
    [InlineData(AuditEventCode.PlaintextImportExecuted)]
    [InlineData(AuditEventCode.PlaintextExportExecuted)]
    [InlineData(AuditEventCode.ImportItemFailed)]
    [InlineData(AuditEventCode.ExportItemFailed)]
    [InlineData(AuditEventCode.CsvFormulaGuardApplied)]
    [InlineData(AuditEventCode.SecretFieldCopiedToClipboard)]
    [InlineData(AuditEventCode.SecretAutoTypeExecuted)]
    [InlineData(AuditEventCode.TimeMachineValueCopiedToClipboard)]
    [InlineData(AuditEventCode.ProfileFieldCopiedToClipboard)]
    [InlineData(AuditEventCode.LocaleImportSucceeded)]
    [InlineData(AuditEventCode.LocaleImportFailed)]
    [InlineData(AuditEventCode.LocaleImportKeyMissing)]
    [InlineData(AuditEventCode.LocaleImportValueTooLong)]
    [InlineData(AuditEventCode.LocaleImportPlaceholderBroken)]
    [InlineData(AuditEventCode.VaultCreated)]
    [InlineData(AuditEventCode.FileExported)]
    [InlineData(AuditEventCode.EmergencyAccessCodeGenerated)]
    [InlineData(AuditEventCode.EmergencyAccessCodeRevoked)]
    [InlineData(AuditEventCode.BackupExecuted)]
    [InlineData(AuditEventCode.RestoreExecuted)]
    public void HighRiskOperationCodes_AreIn0x7000Band(AuditEventCode code)
        => Assert.InRange((int)code, 0x7000, 0x7FFF);

    // ── System category (0xF000 band) ──────────────────────────────────────────

    [Theory]
    [InlineData(AuditEventCode.NoOpDraftDiscarded)]
    [InlineData(AuditEventCode.FileContentTypeRepaired)]
    [InlineData(AuditEventCode.AuditLogsPurged)]
    [InlineData(AuditEventCode.UnifiedDbAutoRecovered)]
    [InlineData(AuditEventCode.VaultDbAutoRecovered)]
    public void SystemCodes_AreIn0xF000Band(AuditEventCode code)
        => Assert.InRange((int)code, 0xF000, 0xFFFF);
}
