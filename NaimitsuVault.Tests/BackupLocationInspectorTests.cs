// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Backup location classification for the dashboard notice" TC-BLI-01–14.
/// The decision order is tested through the internal overload with injected probes, because which
/// physical disks a test machine has cannot be controlled. Comparing real disk numbers (USB disks,
/// network drives, spanned volumes) is confirmed on real machines instead. TC-BLI-11 runs the real
/// probes once, on two folders that are certainly on the same volume. TC-BLI-12–14 cover the OneDrive
/// folder case, which is checked only after the disks are found to be the same.
/// </summary>
public sealed class BackupLocationInspectorTests
{
    private const string Folder = @"X:\Backup";
    private const string VaultDir = @"Y:\App\data";

    private static BackupLocationStatus Inspect(
        bool enabled = true,
        string? folder = Folder,
        bool exists = true,
        bool network = false,
        int[]? backupDisks = null,
        int[]? vaultDisks = null,
        Action? onDiskProbe = null,
        bool cloudSync = false)
        => BackupLocationInspector.Inspect(
            enabled, folder, VaultDir,
            _ => exists,
            _ => network,
            path =>
            {
                onDiskProbe?.Invoke();
                return path == VaultDir ? vaultDisks : backupDisks;
            },
            _ => cloudSync);

    // TC-BLI-01: automatic backup turned off is "not configured", even when a folder is still stored.
    [Fact]
    public void Inspect_Disabled_IsNotConfigured()
        => Assert.Equal(BackupLocationStatus.NotConfigured, Inspect(enabled: false));

    // TC-BLI-02: turned on but with no folder is "not configured".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Inspect_EnabledWithoutFolder_IsNotConfigured(string? folder)
        => Assert.Equal(BackupLocationStatus.NotConfigured, Inspect(folder: folder));

    // TC-BLI-03: a missing folder (e.g. an unplugged external disk) is reported before any disk probing.
    [Fact]
    public void Inspect_FolderMissing_IsFolderMissingWithoutProbingDisks()
    {
        bool probed = false;
        Assert.Equal(BackupLocationStatus.FolderMissing, Inspect(exists: false, onDiskProbe: () => probed = true));
        Assert.False(probed);
    }

    // TC-BLI-04: a network folder is reported without probing physical disks.
    [Fact]
    public void Inspect_NetworkFolder_IsNetworkWithoutProbingDisks()
    {
        bool probed = false;
        Assert.Equal(BackupLocationStatus.Network, Inspect(network: true, onDiskProbe: () => probed = true));
        Assert.False(probed);
    }

    // TC-BLI-05: the same physical disk number as the vault data folder is "same disk".
    [Fact]
    public void Inspect_SameDiskNumber_IsSameDiskAsVault()
        => Assert.Equal(BackupLocationStatus.SameDiskAsVault, Inspect(backupDisks: [0], vaultDisks: [0]));

    // TC-BLI-06: different physical disk numbers are "different disk".
    [Fact]
    public void Inspect_DifferentDiskNumber_IsDifferentDisk()
        => Assert.Equal(BackupLocationStatus.DifferentDisk, Inspect(backupDisks: [1], vaultDisks: [0]));

    // TC-BLI-07: a volume spanning several disks counts as the same disk if any one of them overlaps.
    [Fact]
    public void Inspect_SpannedVolumeOverlapping_IsSameDiskAsVault()
        => Assert.Equal(BackupLocationStatus.SameDiskAsVault, Inspect(backupDisks: [2, 3], vaultDisks: [3]));

    // TC-BLI-08: when either side cannot be probed (virtual drive, IOCTL failure), the result is unknown,
    // so the dashboard shows no notice rather than guessing.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Inspect_UnprobeableSide_IsUnknown(bool backupProbed, bool vaultProbed)
        => Assert.Equal(BackupLocationStatus.Unknown,
            Inspect(backupDisks: backupProbed ? [0] : null, vaultDisks: vaultProbed ? [0] : null));

    // TC-BLI-09: an exception from any probe is absorbed as unknown.
    [Fact]
    public void Inspect_ProbeThrows_IsUnknown()
    {
        var result = BackupLocationInspector.Inspect(
            true, Folder, VaultDir,
            _ => throw new UnauthorizedAccessException(),
            _ => false,
            _ => [0]);
        Assert.Equal(BackupLocationStatus.Unknown, result);
    }

    // TC-BLI-10: UNC paths (plain and \\?\UNC\) are network paths; \\?\ and \\.\ device paths are not.
    [Theory]
    [InlineData(@"\\server\share\backup", true)]
    [InlineData(@"\\?\UNC\server\share\backup", true)]
    [InlineData(@"\\?\C:\backup", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    public void IsNetworkPath_ClassifiesPathForms(string path, bool expected)
        => Assert.Equal(expected, BackupLocationInspector.IsNetworkPath(path));

    // TC-BLI-11: with the real probes, two folders on the same volume are never reported as different disks.
    // (Unknown is accepted: the test machine's system volume may be virtual, e.g. on a CI runner.)
    [Fact]
    public void Inspect_RealProbes_SameVolume_IsNeverDifferentDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "NaimitsuVault.Tests", Guid.NewGuid().ToString("N"));
        var backup = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
        var vault = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        try
        {
            var result = BackupLocationInspector.Inspect(true, backup, vault);
            Assert.Contains(result, new[] { BackupLocationStatus.SameDiskAsVault, BackupLocationStatus.CloudSyncFolder, BackupLocationStatus.Unknown });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // TC-BLI-12: on the same disk, a OneDrive folder is reported as CloudSyncFolder (information, not a warning).
    [Fact]
    public void Inspect_SameDiskInOneDrive_IsCloudSyncFolder()
        => Assert.Equal(BackupLocationStatus.CloudSyncFolder, Inspect(backupDisks: [0], vaultDisks: [0], cloudSync: true));

    // TC-BLI-13: a OneDrive folder on another disk stays DifferentDisk - the OneDrive check runs only after
    // the disks are found to be the same, so a safe setup shows nothing.
    [Fact]
    public void Inspect_DifferentDiskInOneDrive_IsDifferentDisk()
        => Assert.Equal(BackupLocationStatus.DifferentDisk, Inspect(backupDisks: [1], vaultDisks: [0], cloudSync: true));

    // TC-BLI-14: a folder is "in" a OneDrive folder only by whole path segments, ignoring case and a trailing separator.
    [Theory]
    [InlineData(@"C:\Users\a\OneDrive", @"C:\Users\a\OneDrive", true)]
    [InlineData(@"C:\Users\a\OneDrive\Apps\NaimitsuVault", @"C:\Users\a\OneDrive", true)]
    [InlineData(@"c:\users\a\onedrive\backup", @"C:\Users\a\OneDrive", true)]
    [InlineData(@"C:\Users\a\OneDrive\backup", @"C:\Users\a\OneDrive\", true)]
    [InlineData(@"C:\Users\a\OneDriveBackup", @"C:\Users\a\OneDrive", false)]
    [InlineData(@"C:\Users\a", @"C:\Users\a\OneDrive", false)]
    [InlineData(@"D:\OneDrive\backup", @"C:\Users\a\OneDrive", false)]
    public void IsSameOrUnder_ComparesWholeSegments(string path, string root, bool expected)
        => Assert.Equal(expected, BackupLocationInspector.IsSameOrUnder(path, root));
}
