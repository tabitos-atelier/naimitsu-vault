// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Tests.Stubs;

internal sealed class StubAuthService : IAuthService
{
    // ── Configurable return values ───────────────────────────────────────────────
    public bool UnlockAsyncResult           { get; init; } = true;
    public bool ChangeMasterPasswordResult  { get; init; } = true;
    public bool HasEmergencyAccessCodeValue { get; init; } = false;
    public List<int>? GetAvailableVaultNumbersAsyncResult { get; init; }
    public List<int>? AcquireKSharedAsyncResult            { get; init; }
    public bool CreateNewVaultAsyncResult                  { get; init; }
    public List<int>? AcquireKSharedWithHelloAsyncResult   { get; init; }
    public bool EnterVaultWithHelloAsyncResult             { get; init; } = true;

    /// <summary>When true, Enable/DisableWindowsHelloAsync throw an exception (for rollback tests).</summary>
    public bool ThrowOnHello                { get; init; } = false;

    // ── Implementation ───────────────────────────────────────────────────────────
    public Task<bool> UnlockAsync(ReadOnlySpan<char> _)
        => Task.FromResult(UnlockAsyncResult);

    public Task<bool> ChangeMasterPasswordAsync(ReadOnlySpan<char> _, ReadOnlySpan<char> __)
        => Task.FromResult(ChangeMasterPasswordResult);

    public Task<bool> HasEmergencyAccessCodeAsync()
        => Task.FromResult(HasEmergencyAccessCodeValue);

    public Task EnableWindowsHelloAsync()
        => ThrowOnHello ? Task.FromException(new InvalidOperationException("stub"))
                        : Task.CompletedTask;

    public Task DisableWindowsHelloAsync()
        => ThrowOnHello ? Task.FromException(new InvalidOperationException("stub"))
                        : Task.CompletedTask;

    public Task InvalidateWindowsHelloEverywhereAsync(string dataDir)
        => ThrowOnHello ? Task.FromException(new InvalidOperationException("stub"))
                        : Task.CompletedTask;

    // The rest are paths never reached by the tests
    public Task<bool> IsSetupRequiredAsync()              => throw new NotImplementedException();
    public Task SetupAsync(ReadOnlySpan<char> _, string __, CancellationToken ___ = default)
                                                          => throw new NotImplementedException();
    public Task<bool> ResetMasterPasswordWithHelloAsync(ReadOnlySpan<char> _)
                                                          => throw new NotImplementedException();
    public Task<bool> IsWindowsHelloSupportedAsync()      => throw new NotImplementedException();
    public Task<bool> IsWindowsHelloEnabledAsync()        => throw new NotImplementedException();
    public Task<bool> IsWindowsHelloConfiguredForUnlockAsync() => throw new NotImplementedException();
    public Task<List<int>> AcquireKSharedWithHelloAsync()
    {
        AcquireKSharedWithHelloCalls++;
        return AcquireKSharedWithHelloAsyncResult != null ? Task.FromResult(AcquireKSharedWithHelloAsyncResult) : throw new NotImplementedException();
    }
    /// <summary>The token most recently passed to AcquireKSharedAsync (null until it has been called).</summary>
    public CancellationToken? LastAcquireKSharedToken { get; private set; }
    public int AcquireKSharedWithHelloCalls { get; private set; }

    public Task<List<int>> AcquireKSharedAsync(ReadOnlySpan<char> _, CancellationToken cancellationToken = default)
    {
        LastAcquireKSharedToken = cancellationToken;
        // Mirrors the real service's contract: a cancelled attempt surfaces as OperationCanceledException.
        cancellationToken.ThrowIfCancellationRequested();
        return AcquireKSharedAsyncResult != null ? Task.FromResult(AcquireKSharedAsyncResult) : throw new NotImplementedException();
    }
    public Task<bool> EnterVaultAsync(int _, ReadOnlySpan<char> __, string ___, CancellationToken ____ = default) => throw new NotImplementedException();
    public Task<bool> EnterVaultWithHelloAsync(int _, string __) => Task.FromResult(EnterVaultWithHelloAsyncResult);
    public Task<bool> CreateNewVaultAsync(int _, ReadOnlySpan<char> __, string ___)
        => Task.FromResult(CreateNewVaultAsyncResult);
    public Task<List<int>> GetAvailableVaultNumbersAsync()
        => GetAvailableVaultNumbersAsyncResult != null ? Task.FromResult(GetAvailableVaultNumbersAsyncResult) : throw new NotImplementedException();
    public Task<List<int>> GetRegisteredVaultNumbersAsync() => throw new NotImplementedException();
    public Task<(string QrPayload, string SuggestedFileName, byte[] WrappedDek)> GenerateEmergencyAccessCodeAsync(ReadOnlySpan<char> _)
                                                          => throw new NotImplementedException();
    public Task CommitEmergencyAccessCodeAsync(byte[] _)  => throw new NotImplementedException();
    public Task RevokeEmergencyAccessCodeAsync()          => throw new NotImplementedException();
    public Task<EmergencyAccessResult> EmergencyAccessUnlockAsync(ReadOnlySpan<char> _, ReadOnlySpan<char> __, string ___, CancellationToken ____ = default)
                                                          => throw new NotImplementedException();
}
