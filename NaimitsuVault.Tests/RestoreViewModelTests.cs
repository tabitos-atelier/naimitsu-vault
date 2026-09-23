// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Helpers;
using NaimitsuVault.Localization;
using NaimitsuVault.Services.Interfaces;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression tests for RestoreViewModel (Rev.22: single-path restore only - validate → backup
/// authentication gate → conditional overwrite guard → overwrite copy).
/// </summary>
public sealed class RestoreViewModelTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private sealed class StubRestoreAuthService : IRestoreAuthService
    {
        public byte[]? KSharedToReturn { get; set; } = new byte[32];
        public bool ValidationThrows { get; set; }
        public bool HasExistingLocalDataToReturn { get; set; }
        public int RestoreAllCallCount { get; private set; }
        public (int Count, bool NkdbCopied) RestoreResult { get; set; } = (1, true);
        public IReadOnlyList<int> LocalVaultDbNumbersToReturn { get; set; } = [];

        public Task<byte[]?> AuthenticateBackupNkdbAsync(string backupNkdbPath, char[] password)
            => Task.FromResult(KSharedToReturn);

        public Task ValidateBackupFilesAsync(string backupNkdbPath, string backupSourceDir)
        {
            if (ValidationThrows)
                throw new RestoreValidationException("bad.nkdb", new Exception("corrupt"));
            return Task.CompletedTask;
        }

        public bool HasExistingLocalData(string localDataDir) => HasExistingLocalDataToReturn;

        public Task<(int Count, bool NkdbCopied)> RestoreAllFromBackupAsync(
            string backupNkdbPath, string backupSourceDir, string localDataDir)
        {
            RestoreAllCallCount++;
            return Task.FromResult(RestoreResult);
        }

        public Task<IReadOnlyList<int>> GetLocalVaultDbNumbersAsync()
            => Task.FromResult(LocalVaultDbNumbersToReturn);
    }

    private string CreateDummyBackupFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naimitsu_vm_test_{Guid.NewGuid():N}.nkdb");
        File.WriteAllBytes(path, []);
        _tempFiles.Add(path);
        return path;
    }

    private RestoreViewModel CreateViewModelWithBackup(
        StubRestoreAuthService stub, out string backupPath)
    {
        var vm = new RestoreViewModel(stub, NullLogger<RestoreViewModel>.Instance);
        backupPath = CreateDummyBackupFile();
        vm.LoadBackupPath(backupPath);
        return vm;
    }

    // ── RestoreCommand.CanExecute ────────────────────────────────────────────────

    [Fact]
    public void RestoreCommand_CanExecute_FalseWithoutBackup()
    {
        var vm = new RestoreViewModel(new StubRestoreAuthService(), NullLogger<RestoreViewModel>.Instance);
        Assert.False(vm.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public void RestoreCommand_CanExecute_TrueOnceBackupLoaded()
    {
        var stub = new StubRestoreAuthService();
        var vm = CreateViewModelWithBackup(stub, out _);

        Assert.True(vm.RestoreCommand.CanExecute(null));
    }

    // ── ValidateAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ValidBackup_ReturnsTrue()
    {
        var stub = new StubRestoreAuthService();
        var vm = CreateViewModelWithBackup(stub, out _);

        Assert.True(await vm.ValidateAsync());
        Assert.False(vm.IsError);
    }

    [Fact]
    public async Task ValidateAsync_InvalidBackup_ReturnsFalseAndSetsError()
    {
        var stub = new StubRestoreAuthService { ValidationThrows = true };
        var vm = CreateViewModelWithBackup(stub, out _);

        Assert.False(await vm.ValidateAsync());
        Assert.True(vm.IsError);
        Assert.Equal(
            string.Format(LocalizationManager.Get("Restore.ErrorInvalidBackupFileAborted"), "bad.nkdb"),
            vm.StatusMessage);
    }

    // ── AuthenticateAsync (backup authentication gate) ─────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_CorrectPassword_ReturnsTrue()
    {
        var stub = new StubRestoreAuthService { KSharedToReturn = new byte[32] };
        var vm = CreateViewModelWithBackup(stub, out _);

        Assert.True(await vm.AuthenticateAsync("correct-password".ToCharArray()));
        Assert.False(vm.IsError);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongPassword_ReturnsFalseAndSetsError()
    {
        var stub = new StubRestoreAuthService { KSharedToReturn = null };
        var vm = CreateViewModelWithBackup(stub, out _);

        Assert.False(await vm.AuthenticateAsync("wrong-password".ToCharArray()));
        Assert.True(vm.IsError);
    }

    // ── HasExistingLocalData (first guard's trigger condition) ─────────────────────

    [Fact]
    public void HasExistingLocalData_DelegatesToAuthService()
    {
        var stub = new StubRestoreAuthService { HasExistingLocalDataToReturn = true };
        var vm = new RestoreViewModel(stub, NullLogger<RestoreViewModel>.Instance);

        Assert.True(vm.HasExistingLocalData());
    }

    // ── RestoreAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreAsync_Success_FiresRestoreSucceeded()
    {
        var stub = new StubRestoreAuthService { RestoreResult = (2, true) };
        var vm = CreateViewModelWithBackup(stub, out _);

        bool fired = false;
        vm.RestoreSucceeded += (_, _) => fired = true;

        await vm.RestoreAsync();

        Assert.Equal(1, stub.RestoreAllCallCount);
        Assert.True(fired);
        Assert.False(vm.IsError);
    }

    [Fact]
    public async Task RestoreAsync_ZeroFilesCopied_SetsErrorAndDoesNotFireSucceeded()
    {
        var stub = new StubRestoreAuthService { RestoreResult = (0, false) };
        var vm = CreateViewModelWithBackup(stub, out _);

        bool fired = false;
        vm.RestoreSucceeded += (_, _) => fired = true;

        await vm.RestoreAsync();

        Assert.True(vm.IsError);
        Assert.False(fired);
    }
}
