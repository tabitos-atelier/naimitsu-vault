// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace NaimitsuVault.Helpers;

/// <summary>Where the automatic backup folder is, relative to the vault data folder.</summary>
public enum BackupLocationStatus
{
    /// <summary>Automatic backup is off, or no folder is set.</summary>
    NotConfigured,
    /// <summary>The folder does not exist right now (e.g. an external disk is unplugged).</summary>
    FolderMissing,
    /// <summary>The folder is on the same physical disk as the vault data folder.</summary>
    SameDiskAsVault,
    /// <summary>
    /// The folder is on the same physical disk as the vault data folder, but inside a OneDrive folder,
    /// so it is also synced to the cloud. Shown as information rather than a warning.
    /// </summary>
    CloudSyncFolder,
    /// <summary>The folder is a UNC path or on a mapped network drive.</summary>
    Network,
    /// <summary>The folder is on a different physical disk from the vault data folder.</summary>
    DifferentDisk,
    /// <summary>Could not be determined (virtual drive, access error, IOCTL failure, ...).</summary>
    Unknown,
}

/// <summary>
/// Classifies the automatic backup folder for the dashboard notice. Uses only Win32 APIs via P/Invoke
/// (no WMI, which would need the System.Management package) and never requests administrator rights:
/// volumes are opened with an access mask of 0, which allows the storage query IOCTLs without elevation.
/// Every failure maps to <see cref="BackupLocationStatus.Unknown"/>, for which the dashboard shows no
/// notice - a notice that cannot be closed must not be shown on a guess.
/// May block (Directory.Exists on an unreachable network path can take tens of seconds), so callers
/// must run it off the UI thread.
/// </summary>
public static class BackupLocationInspector
{
    private static readonly ILogger Logger = AppLog.For(typeof(BackupLocationInspector));

    // OneDrive sets these per signed-in account: OneDrive (the default account), OneDriveConsumer
    // (personal) and OneDriveCommercial (work or school). All three are checked because a PC can have both.
    private static readonly string[] OneDriveEnvironmentVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    public static BackupLocationStatus Inspect(bool enabled, string? folder, string vaultDataDir)
        => Inspect(enabled, folder, vaultDataDir, Directory.Exists, IsNetworkPath, TryGetPhysicalDiskNumbers, IsInOneDriveFolder);

    /// <summary>Decision order only; the OS-dependent probes are injected for testability.</summary>
    internal static BackupLocationStatus Inspect(
        bool enabled,
        string? folder,
        string vaultDataDir,
        Func<string, bool> directoryExists,
        Func<string, bool> isNetworkPath,
        Func<string, int[]?> getPhysicalDiskNumbers,
        Func<string, bool>? isCloudSyncFolder = null)
    {
        if (!enabled || string.IsNullOrWhiteSpace(folder)) return BackupLocationStatus.NotConfigured;

        try
        {
            if (!directoryExists(folder)) return BackupLocationStatus.FolderMissing;
            if (isNetworkPath(folder)) return BackupLocationStatus.Network;

            var backupDisks = getPhysicalDiskNumbers(folder);
            var vaultDisks = getPhysicalDiskNumbers(vaultDataDir);
            if (backupDisks is null || vaultDisks is null) return BackupLocationStatus.Unknown;

            if (!backupDisks.Intersect(vaultDisks).Any()) return BackupLocationStatus.DifferentDisk;

            // Checked only after the disks are known to be the same: a OneDrive folder moved to another
            // disk is already a safe setup and shows nothing, like any other folder on another disk.
            return isCloudSyncFolder?.Invoke(folder) == true
                ? BackupLocationStatus.CloudSyncFolder
                : BackupLocationStatus.SameDiskAsVault;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Backup location inspection failed. [{ExType}]", ex.GetType().Name);
            return BackupLocationStatus.Unknown;
        }
    }

    internal static bool IsNetworkPath(string path)
    {
        // \\server\share and \\?\UNC\server\share are network paths; \\?\C:\ and \\.\ device paths are not.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
    }

    /// <summary>True when <paramref name="path"/> is a OneDrive folder or inside one. Missing variables count as no OneDrive.</summary>
    internal static bool IsInOneDriveFolder(string path)
    {
        foreach (var name in OneDriveEnvironmentVariables)
        {
            var root = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(root) && IsSameOrUnder(path, root)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> itself or a folder below it. Compares
    /// whole path segments, so "C:\Users\a\OneDriveBackup" is not under "C:\Users\a\OneDrive".
    /// </summary>
    internal static bool IsSameOrUnder(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the physical disk numbers backing the volume that contains <paramref name="path"/>,
    /// or null when they cannot be determined.
    /// </summary>
    private static int[]? TryGetPhysicalDiskNumbers(string path)
    {
        // Resolve the real volume (not the drive letter), so folder mount points and junctions are handled.
        var mountPoint = new StringBuilder(MaxPath);
        if (!GetVolumePathNameW(path, mountPoint, mountPoint.Capacity)) return null;
        var volumeName = new StringBuilder(MaxPath);
        if (!GetVolumeNameForVolumeMountPointW(mountPoint.ToString(), volumeName, volumeName.Capacity)) return null;

        // \\?\Volume{GUID}\ -> \\?\Volume{GUID} (a trailing backslash would open the root directory instead)
        using var volume = CreateFileW(volumeName.ToString().TrimEnd('\\'), 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (volume.IsInvalid) return null;

        if (DeviceIoControl(volume, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out StorageDeviceNumber number, Marshal.SizeOf<StorageDeviceNumber>(), out _, IntPtr.Zero))
        {
            // Virtual drives can report a disk number that collides with a real disk: Google Drive for
            // desktop reports device type 8 and disk number 0, the same number as a typical C: drive.
            return number.DeviceType == FileDeviceDisk ? [number.DeviceNumber] : null;
        }

        // Volumes spanning several disks (Storage Spaces, dynamic spanned/mirrored volumes) have no single
        // device number; fall back to the list of disks the volume's extents live on.
        return TryGetExtentDiskNumbers(volume);
    }

    private static int[]? TryGetExtentDiskNumbers(SafeFileHandle volume)
    {
        const int maxExtents = 32;
        int size = 8 + maxExtents * ExtentSize;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, size, out _, IntPtr.Zero)) return null;
            int count = Marshal.ReadInt32(buffer, 0);
            if (count <= 0 || count > maxExtents) return null;
            var disks = new int[count];
            for (int i = 0; i < count; i++) disks[i] = Marshal.ReadInt32(buffer, 8 + i * ExtentSize);
            return disks;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int MaxPath = 261;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const int FileDeviceDisk = 7;
    // DISK_EXTENT: DWORD DiskNumber + (padding) + LARGE_INTEGER StartingOffset + LARGE_INTEGER ExtentLength
    private const int ExtentSize = 24;

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize, out StorageDeviceNumber outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
