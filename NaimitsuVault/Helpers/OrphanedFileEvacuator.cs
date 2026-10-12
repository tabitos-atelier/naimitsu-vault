// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Moves files under data/ that nothing refers to any more into a dated subfolder
/// (data/orphaned_yyyyMMdd_HHmmss/) instead of deleting them.
///
/// "Not referenced" is a judgement the app can get wrong: a unified DB that was replaced by an empty
/// one, for example, makes every existing vault look unregistered. Moving keeps such a mistake
/// recoverable. The folder is never rotated or cleaned up automatically; removing it is left to the user.
///
/// One instance is one batch: every file moved through it lands in the same folder, which is created
/// when the first file is moved. Scans over data/ only look at its top level, so moved files are no
/// longer treated as live, shadow, or orphaned files.
/// </summary>
internal sealed class OrphanedFileEvacuator(string dataDir, ILogger logger)
{
    internal const string FolderPrefix = "orphaned_";

    private static readonly string[] SidecarSuffixes = ["-wal", "-shm"];

    private string? _folderPath;

    /// <summary>Name (not path) of the folder files were moved to, or null if nothing was moved.</summary>
    public string? FolderName => MovedCount > 0 && _folderPath != null ? Path.GetFileName(_folderPath) : null;

    /// <summary>Number of files moved so far, not counting -wal/-shm sidecars.</summary>
    public int MovedCount { get; private set; }

    /// <summary>
    /// Moves the file, together with its -wal/-shm sidecars, into this batch's folder.
    /// On failure the file stays where it is; it is never deleted.
    /// </summary>
    public bool TryEvacuate(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;

            _folderPath ??= CreateFolder();
            var name = Path.GetFileName(filePath);
            File.Move(filePath, Path.Combine(_folderPath, name));
            MovedCount++;

            foreach (var suffix in SidecarSuffixes)
            {
                try
                {
                    if (File.Exists(filePath + suffix))
                        File.Move(filePath + suffix, Path.Combine(_folderPath, name + suffix));
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not move a sidecar of an unreferenced file; it was left in place. [{ExType}]", ex.GetType().Name);
                }
            }

            logger.LogInformation("Moved an unreferenced file to {Folder}: {Name}", Path.GetFileName(_folderPath), name);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not move an unreferenced file; it was left in place. [{ExType}]", ex.GetType().Name);
            RemoveFolderIfUnused();
            return false;
        }
    }

    /// <summary>
    /// Puts a file moved by <see cref="TryEvacuate"/> back at its original path. Used when the step
    /// the move was preparing for (restoring over that path) failed.
    /// </summary>
    public void TryMoveBack(string originalPath)
    {
        if (_folderPath == null) return;
        var name = Path.GetFileName(originalPath);
        try
        {
            var evacuated = Path.Combine(_folderPath, name);
            if (!File.Exists(evacuated) || File.Exists(originalPath)) return;

            File.Move(evacuated, originalPath);
            MovedCount--;
            foreach (var suffix in SidecarSuffixes)
            {
                if (File.Exists(evacuated + suffix) && !File.Exists(originalPath + suffix))
                    File.Move(evacuated + suffix, originalPath + suffix);
            }
            RemoveFolderIfUnused();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not move a file back from {Folder}; it stays there. [{ExType}]", Path.GetFileName(_folderPath), ex.GetType().Name);
        }
    }

    /// <summary>
    /// Moves every vault DB file directly under data/ whose FileHash is not in
    /// <paramref name="registeredHashes"/> into one orphaned_* folder.
    /// A vault DB file is a 48-character Base64Url name that decodes to "nkdb" + FileHash.
    /// </summary>
    /// <returns>The folder name, or null if no file was moved.</returns>
    internal static string? EvacuateUnregisteredVaultFiles(string dataDir, HashSet<string> registeredHashes, ILogger logger)
    {
        var evacuator = new OrphanedFileEvacuator(dataDir, logger);
        try
        {
            // Materialized first: files are moved out of the folder while iterating.
            foreach (var filePath in Directory.GetFiles(dataDir))
            {
                try
                {
                    var fileName = Path.GetFileName(filePath);
                    if (fileName.Length != 48) continue;

                    var raw = DecodeBase64UrlNoPad(fileName);
                    if (raw == null || raw.Length < 4) continue;
                    if (raw[0] != 'n' || raw[1] != 'k' || raw[2] != 'd' || raw[3] != 'b') continue;

                    var fileHash = Convert.ToBase64String(raw[4..]);
                    if (registeredHashes.Contains(fileHash)) continue;

                    SqliteConnection.ClearAllPools();
                    evacuator.TryEvacuate(filePath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Unregistered vault file scan: error while processing a file (skipped). [{ExType}]", ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Unregistered vault file scan failed as a whole (ignored). [{ExType}]", ex.GetType().Name);
        }
        return evacuator.FolderName;
    }

    private string CreateFolder()
    {
        var baseName = FolderPrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(dataDir, baseName);
        for (int suffix = 2; Directory.Exists(path); suffix++)
            path = Path.Combine(dataDir, $"{baseName}-{suffix}");
        Directory.CreateDirectory(path);
        return path;
    }

    // Only ever removes the folder this instance created, and only while it is still empty.
    private void RemoveFolderIfUnused()
    {
        if (_folderPath == null || MovedCount > 0) return;
        try { Directory.Delete(_folderPath, recursive: false); } catch { }
        _folderPath = null;
    }

    private static byte[]? DecodeBase64UrlNoPad(string s)
    {
        foreach (char c in s)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return null;

        var std = s.Replace('-', '+').Replace('_', '/');
        std += new string('=', (4 - std.Length % 4) % 4);
        var buf = new byte[std.Length];
        return Convert.TryFromBase64String(std, buf, out int written) ? buf[..written] : null;
    }
}
