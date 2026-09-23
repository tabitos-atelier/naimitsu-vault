// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Tests;

/// <summary>
/// Serialized, mutually-exclusive collection for tests that occupy a Win32 global resource
/// (e.g. the clipboard).
/// Classes belonging to the same-named collection never run concurrently with each other.
/// </summary>
[CollectionDefinition("SequentialClipboard", DisableParallelization = true)]
public sealed class SequentialClipboardCollection { }

/// <summary>
/// Serialized, mutually-exclusive collection for tests that manipulate LocalizationManager
/// (static state).
/// Initialize() overwrites the dictionary globally, so running in parallel would cause
/// cross-test interference.
/// </summary>
[CollectionDefinition("SequentialLocale", DisableParallelization = true)]
public sealed class SequentialLocaleCollection { }

/// <summary>
/// Serialized, mutually-exclusive collection for tests that route through
/// WeakReferenceMessenger.Default (a process-wide shared singleton), so running them in
/// parallel would let one test's registered receivers observe another test's messages.
/// </summary>
[CollectionDefinition("SequentialMessenger", DisableParallelization = true)]
public sealed class SequentialMessengerCollection { }

/// <summary>
/// Serialized, mutually-exclusive collection for every test class that reaches
/// SqliteConnection.ClearAllPools() - a process-wide call that closes the connection pool, and can
/// dispose the native handle of a connection another test is actively using
/// (ObjectDisposedException: 'SQLitePCL.sqlite3' at sqlite3_changes, seen intermittently, typically on
/// the first run after a build). DisableParallelization = true makes the collection exclusive of ALL
/// other tests, so it is enough to put the CALLERS here: the tests that merely use SQLite need no
/// attribute, because nothing can run alongside a caller.
///
/// Membership rule - a class belongs here if it either:
///   1. calls SqliteConnection.ClearAllPools() itself (enforced by SqlitePoolCollectionRuleTests), or
///   2. runs a production path that calls it: DatabaseInitializer.InitializeAsync/InitializeVaultDbAsync,
///      AutoBackupService.PerformUnifiedBackup/RotateGenerations (RunBackup once the pending flag is set),
///      ShadowFileService.WriteAll/TryRestoreFromShadowAsync/SetPragmaUserVersion,
///      AuthService.EmergencyAccessUnlockAsync (a found vault), InvalidateWindowsHelloEverywhereAsync,
///      RestoreAllFromBackupAsync, or CreateNewVaultAsync/SetupAsync past their argument checks.
/// This is not covered by the automatic rule, so check it when adding tests that use those APIs.
/// (Formerly "IoDestructionSequential", which applied this rule to a single class.)
/// </summary>
[CollectionDefinition("SequentialSqlitePool", DisableParallelization = true)]
public sealed class SequentialSqlitePoolCollection { }
