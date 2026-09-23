// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// AppSession memory hygiene tests (TC-AS-01 .. TC-AS-17).
/// Verifies DEK's GC pinning, ZeroMemory, and zeroing in the AvatarBytes setter.
/// </summary>
public sealed class AppSessionMemoryHygieneTests
{
    private static byte[] MakeKey(byte fill = 0x01)
    {
        var key = new byte[32];
        Array.Fill(key, fill);
        return key;
    }

    // ── TC-AS-01 ──────────────────────────────────────────────────────────────
    // Initial state: IsUnlocked = false

    [Fact]
    public void NewSession_IsUnlocked_IsFalse()
    {
        var session = new AppSession();
        Assert.False(session.IsUnlocked);
    }

    // ── TC-AS-02 ──────────────────────────────────────────────────────────────
    // SetKey -> IsUnlocked = true

    [Fact]
    public void SetKey_Valid32Bytes_IsUnlockedBecomesTrue()
    {
        var session = new AppSession();
        session.SetKey(MakeKey(0xAB));
        Assert.True(session.IsUnlocked);
    }

    // ── TC-AS-03 ──────────────────────────────────────────────────────────────
    // Lock -> IsUnlocked = false

    [Fact]
    public void Lock_AfterSetKey_IsUnlockedBecomesFalse()
    {
        var session = new AppSession();
        session.SetKey(MakeKey());
        session.Lock();
        Assert.False(session.IsUnlocked);
    }

    // ── TC-AS-04 ──────────────────────────────────────────────────────────────
    // Lock -> every byte of _pinnedKey becomes zero (since this can't be confirmed via GetKey,
    // read the span reference through DekScope)

    [Fact]
    public void Lock_ZerosThePinnedKeyBytes()
    {
        var session = new AppSession();
        session.SetKey(MakeKey(0xFF));

        // Get a DekScope before locking to hold a span reference
        var scope = session.GetKey();

        session.Lock();

        // After locking, the buffer the span points to must also be zero
        Assert.True(scope.Span.ToArray().All(b => b == 0),
            "The plaintext DEK still remains in _pinnedKey after Lock() (suspected ZeroMemory failure)");
    }

    // ── TC-AS-05 ──────────────────────────────────────────────────────────────
    // SetKey with an invalid size (anything other than 32 bytes) -> ArgumentException

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void SetKey_WrongSize_ThrowsArgumentException(int size)
    {
        var session = new AppSession();
        Assert.Throws<ArgumentException>(() => session.SetKey(new byte[size]));
    }

    // ── TC-AS-06 ──────────────────────────────────────────────────────────────
    // GetKey -> DekScope.Span references the actual DEK bytes

    [Fact]
    public void GetKey_ReturnsSpanWithCorrectBytes()
    {
        var key     = MakeKey(0xCC);
        var session = new AppSession();
        session.SetKey(key);

        var scope = session.GetKey();
        Assert.True(scope.Span.SequenceEqual(key));
    }

    // ── TC-AS-07 ──────────────────────────────────────────────────────────────
    // GetKey while locked -> InvalidOperationException

    [Fact]
    public void GetKey_WhenLocked_ThrowsInvalidOperationException()
    {
        var session = new AppSession();
        Assert.Throws<InvalidOperationException>(() => session.GetKey());
    }

    // ── TC-AS-08 ──────────────────────────────────────────────────────────────
    // AvatarBytes setter: the old buffer gets ZeroMemory'd

    [Fact]
    public void AvatarBytes_ReplacingExisting_OldBufferIsZeroed()
    {
        var session = new AppSession();

        // Set the first avatar image
        var first   = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        session.AvatarBytes = first;
        // The source array gets ZeroMemory'd by the setter
        Assert.True(first.All(b => b == 0),
            "The AvatarBytes setter did not ZeroMemory the source array");

        // The AvatarBytes property returns a reference to the internal pinned buffer
        var pinnedRef = session.AvatarBytes!;
        Assert.True(pinnedRef.All(b => b != 0));  // must not be 0, from [0xAA, 0xBB, 0xCC, 0xDD]

        // Set a second avatar image -> the old pinned buffer should get ZeroMemory'd
        var second = new byte[] { 0x11, 0x22 };
        session.AvatarBytes = second;

        // The old pinned buffer (pinnedRef) must also be zero
        Assert.True(pinnedRef.All(b => b == 0),
            "The old _pinnedAvatar was not ZeroMemory'd after updating AvatarBytes");
    }

    // ── TC-AS-09 ──────────────────────────────────────────────────────────────
    // AvatarBytes = null -> _pinnedAvatar gets ZeroMemory'd before becoming null

    [Fact]
    public void AvatarBytes_SetNull_PinnedBufferIsZeroed()
    {
        var session = new AppSession();
        var initial = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        session.AvatarBytes = initial;

        var pinnedRef = session.AvatarBytes!;

        session.AvatarBytes = null;

        Assert.Null(session.AvatarBytes);
        Assert.True(pinnedRef.All(b => b == 0),
            "The old data still remains in the _pinnedAvatar buffer after AvatarBytes = null");
    }

    // ── TC-AS-10 ──────────────────────────────────────────────────────────────
    // Lock() is equivalent to AvatarBytes = null -> AvatarBytes gets ZeroMemory'd

    [Fact]
    public void Lock_ZerosAvatarBytes()
    {
        var session = new AppSession();
        session.SetKey(MakeKey());
        var avatar = new byte[] { 0x01, 0x02, 0x03 };
        session.AvatarBytes = avatar;

        var pinnedRef = session.AvatarBytes!;
        session.Lock();

        Assert.Null(session.AvatarBytes);
        Assert.True(pinnedRef.All(b => b == 0),
            "AvatarBytes's _pinnedAvatar was not ZeroMemory'd after Lock()");
    }

    // ── TC-AS-11 ──────────────────────────────────────────────────────────────
    // Calling SetKey twice zero-clears the old DEK and overwrites it with the new one.
    // scope1 and scope2 point at the same _pinnedKey -> 0x22 must be visible after the second SetKey

    [Fact]
    public void SetKey_CalledTwice_OldKeyIsOverwritten()
    {
        var session = new AppSession();
        session.SetKey(MakeKey(0x11));
        var scope1  = session.GetKey();

        // Confirm scope1 references 0x11 (baseline)
        Assert.True(scope1.Span.ToArray().All(b => b == 0x11));

        session.SetKey(MakeKey(0x22));

        // SetKey ZeroMemory's _pinnedKey before copying in the new key.
        // Since scope1 is a reference to _pinnedKey, the old key 0x11 disappears and the new key 0x22 is visible.
        Assert.True(scope1.Span.ToArray().All(b => b == 0x22),
            "The old DEK 0x11 still remains in _pinnedKey after the second SetKey (suspected ZeroMemory failure)");
    }

    // ── TC-AS-12 ──────────────────────────────────────────────────────────────
    // The lifecycle: IsUnlocked -> Lock -> SetKey -> IsUnlocked

    [Fact]
    public void Lifecycle_SetLockSet_WorksCorrectly()
    {
        var session = new AppSession();
        Assert.False(session.IsUnlocked);

        session.SetKey(MakeKey(0x01));
        Assert.True(session.IsUnlocked);

        session.Lock();
        Assert.False(session.IsUnlocked);

        session.SetKey(MakeKey(0x02));
        Assert.True(session.IsUnlocked);

        var scope = session.GetKey();
        Assert.True(scope.Span.SequenceEqual(MakeKey(0x02)));
    }

    // ── TC-AS-13 ──────────────────────────────────────────────────────────────
    // Non-sensitive metadata (LastSelectedSecretId, etc.) is preserved after locking

    [Fact]
    public void Lock_NonSecretMetadata_IsPreservedAfterLock()
    {
        var session = new AppSession();
        session.SetKey(MakeKey());
        session.LastSelectedSecretId       = 42;
        session.LastActivePageTag          = "SecretListPage";
        session.LastSelectedTimeMachineId  = 99;

        session.Lock();

        Assert.Equal(42,               session.LastSelectedSecretId);
        Assert.Equal("SecretListPage", session.LastActivePageTag);
        Assert.Equal(99,               session.LastSelectedTimeMachineId);
    }

    // ── TC-AS-14 ──────────────────────────────────────────────────────────────
    // _pinnedKey is never moved by the GC (since it was allocated with pinned: true).
    // GetKey().Span must still point at the same data even after GC.Collect

    [Fact]
    public void PinnedKey_GcCollect_DataIntact()
    {
        var session = new AppSession();
        session.SetKey(MakeKey(0x55));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var scope = session.GetKey();
        Assert.True(scope.Span.ToArray().All(b => b == 0x55),
            "_pinnedKey's data changed after GC.Collect (suspected pinning failure)");
    }

    // ── TC-AS-15 ──────────────────────────────────────────────────────────────
    // Calling Lock() multiple times while already locked throws no exception; all DekSize bytes remain zero

    [Fact]
    public void Lock_CalledMultipleTimes_IsIdempotent()
    {
        var session = new AppSession();
        var ex = Record.Exception(() =>
        {
            session.Lock();
            session.Lock();
            session.Lock();
        });
        Assert.Null(ex);
        Assert.False(session.IsUnlocked);
    }

    // ── TC-AS-16 ──────────────────────────────────────────────────────────────
    // PendingProfileImageIds is preserved after locking (non-sensitive)

    [Fact]
    public void PendingProfileImageIds_PreservedAfterLock()
    {
        var session = new AppSession();
        session.SetKey(MakeKey());
        session.PendingProfileImageIds.Add(10);
        session.PendingProfileImageIds.Add(20);

        session.Lock();

        Assert.Contains(10, session.PendingProfileImageIds);
        Assert.Contains(20, session.PendingProfileImageIds);
    }

    // ── TC-AS-17 ──────────────────────────────────────────────────────────────
    // SetKey does not destroy K_shared (does not call Lock())

    [Fact]
    public void SetKey_DoesNotClearKShared()
    {
        var session = new AppSession();
        session.SetKShared(MakeKey(0xAA));
        Assert.True(session.HasKShared);

        session.SetKey(MakeKey(0x11));
        Assert.True(session.HasKShared,
            "HasKShared became false after SetKey (suspected internal call to Lock())");

        // Also confirm K_shared's content is preserved
        var ksh = session.GetKShared();
        Assert.True(ksh.Span.ToArray().All(b => b == 0xAA),
            "K_shared's content changed after SetKey");
    }

    // ── TC-AS-18 ──────────────────────────────────────────────────────────────
    // ResetPerVaultSelectionState clears all per-vault selection/pending fields at once

    [Fact]
    public void ResetPerVaultSelectionState_ClearsAllTargetFields()
    {
        var session = new AppSession();
        session.LastSelectedSecretId      = 42;
        session.LastSelectedTimeMachineId = 99;
        session.LastSelectedFileId        = 7;
        session.PendingJumpSecretId       = 13;
        session.PendingProfileImageIds.Add(10);
        session.PendingProfileImageIds.Add(20);

        session.ResetPerVaultSelectionState();

        Assert.Null(session.LastSelectedSecretId);
        Assert.Null(session.LastSelectedTimeMachineId);
        Assert.Null(session.LastSelectedFileId);
        Assert.Null(session.PendingJumpSecretId);
        Assert.Empty(session.PendingProfileImageIds);
    }

    // ── TC-AS-19 ──────────────────────────────────────────────────────────────
    // Lock() must NOT clear LastEnteredVaultDbNumber: AuthService.EnterVaultCoreAsync relies on it
    // surviving a lock cycle to tell an ordinary same-vault re-unlock apart from an actual vault
    // switch (unlike CurrentVaultDbNumber, which Lock() does clear)

    [Fact]
    public void Lock_DoesNotClear_LastEnteredVaultDbNumber()
    {
        var session = new AppSession();
        session.SetKey(MakeKey());
        session.LastEnteredVaultDbNumber = 2;

        session.Lock();

        Assert.Equal(2, session.LastEnteredVaultDbNumber);
        Assert.Null(session.CurrentVaultDbNumber);
    }
}
