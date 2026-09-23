// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services.Interfaces;

public interface IRestoreAuthService
{
    /// <summary>
    /// Ownership: the caller retains ownership of <paramref name="password"/>.
    /// This method only reads it and does not clear it. The caller is responsible
    /// for clearing it with <c>CryptographicOperations.ZeroMemory</c> after completion.
    /// </summary>
    /// <returns>
    /// On success, a pinned buffer holding the plaintext K_shared recovered from the backup nkdb; the
    /// caller takes over ownership and is responsible for clearing it with
    /// <c>CryptographicOperations.ZeroMemory</c> after use. Null on authentication failure.
    /// </returns>
    Task<byte[]?> AuthenticateBackupNkdbAsync(string backupNkdbPath, char[] password);

    /// <summary>Validates every backup file (vault files + unified database, if present) before
    /// anything else happens. Throws a <c>RestoreValidationException</c> on the first failure.</summary>
    Task ValidateBackupFilesAsync(string backupNkdbPath, string backupSourceDir);

    /// <summary>Whether localDataDir already holds files a restore would overwrite (used to decide
    /// whether the destructive overwrite-confirmation dialog needs to appear).</summary>
    bool HasExistingLocalData(string localDataDir);

    /// <summary>
    /// Overwrite-copies the contents of the backup source folder directly to local data/. Callers
    /// must have already validated the backup files (<see cref="ValidateBackupFilesAsync"/>) and confirmed
    /// the backup's authenticity (<see cref="AuthenticateBackupNkdbAsync"/>) before calling this - this
    /// method itself performs neither and copies unconditionally, exactly like an IT professional
    /// copying the backup folder directly over data/.
    /// </summary>
    /// <param name="backupNkdbPath">Path to the backup unified database.</param>
    /// <param name="backupSourceDir">Backup source folder (parent folder of the .nkdb).</param>
    /// <param name="localDataDir">Absolute path of the local data/ directory.</param>
    /// <returns><c>Count</c>: the number of items successfully copied (vault file count, +1 if the nkdb copy also succeeded).
    /// <c>NkdbCopied</c>: whether the unified database itself was successfully copied.</returns>
    Task<(int Count, bool NkdbCopied)> RestoreAllFromBackupAsync(
        string backupNkdbPath, string backupSourceDir, string localDataDir);

    /// <summary>Enumerates the local unified database's VaultRegistries.DbNumber values in plaintext
    /// (no K_shared or password needed; DbNumber itself is a plaintext PK outside the scope of encryption).</summary>
    Task<IReadOnlyList<int>> GetLocalVaultDbNumbersAsync();
}
