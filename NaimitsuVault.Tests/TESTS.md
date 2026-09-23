# Test Case Index

An index that resolves the test case IDs (`TC-xxx-nn` format — `xxx` is a 2–3 letter prefix, `nn` is a
2-digit sequence number) that appear in each test class's comments, entirely within this file. ID numbers
are unique project-wide: a prefix is normally dedicated to a single file, but where a domain spans multiple
files (e.g. `TC-AL`, `TC-PGS`, `TC-WH`, `TC-SFR`), those files split the number range between them instead
of reusing numbers. Some test classes deliberately keep gaps in the numbering for retired cases (see
each file's comments for details). For the detailed intent behind each test class, see its XML doc
comments and test method names.

In the ID column, `—` means no case ID is assigned to that class; its cases are identified by test method names. Summaries are kept to one or two sentences. Where a row has background worth keeping (the root cause of a real-machine bug, a per-ID breakdown), the detail is under a heading named after the test class in [Notes](#notes) below, and the row ends with a link to it.

| File | Test Class | Test Case IDs | Summary |
|---|---|---|---|
| AppDbContextGenerationTests.cs | `AppDbContextGenerationTests` | TC-DBX-01–10, 15 | Verifies the generation check (optimistic concurrency control) in the `AppDbContext.SaveChangesAsync` override |
| AppSessionMemoryHygieneTests.cs | `AppSessionMemoryHygieneTests` | TC-AS-01–19 | Verifies `AppSession` memory hygiene: DEK GC pinning, `ZeroMemory`, `AvatarBytes` setter zeroing, `ResetPerVaultSelectionState`, etc. |
| ArchiveHelperTests.cs | `ArchiveHelperTests` | TC-ZIP-01–06 | Verifies `ArchiveHelper.TryListZipEntries`: UTF-8-flagged, ASCII and directory entries, `null` for non-ZIP bytes, and that entry names of a legacy ZIP without the UTF-8 flag are decoded with the system's legacy (OEM) code page (CP932 / GBK cases, passed explicitly so the result does not depend on the test machine's locale) instead of turning into U+FFFD |
| AsyncCancellationRaceTests.cs | `AsyncCancellationRaceTests` | TC-ACR-01, 05–12 | Verifies `CancellationToken` handling in the repository layer against `AppDbContext` generation-check races. TC-ACR-02–04 are retired numbers (the GC helper methods they covered were removed) |
| AuditEventCodeBandTests.cs | `AuditEventCodeBandTests` | — | Verifies every `AuditEventCode` value falls within its defined 0xXYZZ hex band, catching a misplaced band on future code additions |
| AuditLogClipboardAutoTypeVaultSummaryTests.cs | `AuditLogClipboardAutoTypeVaultSummaryTests` | TC-AL-33–37 | Verifies `AuditLogService.BuildSummary` output for the clipboard-copy, AutoType, and vault-creation event codes (`SecretFieldCopiedToClipboard`, `SecretAutoTypeExecuted`, `TimeMachineValueCopiedToClipboard`, `ProfileFieldCopiedToClipboard`, `VaultCreated`) |
| AuditLogImportExportLocaleSummaryTests.cs | `AuditLogImportExportLocaleSummaryTests` | TC-AL-25–32 | Verifies `AuditLogService.BuildSummary` output for per-item import/export failure, the CSV formula-injection guard, and custom-locale import event codes (`ImportItemFailed`, `ExportItemFailed`, `LocaleImportKeyMissing`, `LocaleImportValueTooLong`, `CsvFormulaGuardApplied`, `LocaleImportSucceeded`, `LocaleImportFailed`, `LocaleImportPlaceholderBroken`) |
| AuditLogSelfHealSummaryTests.cs | `AuditLogSelfHealSummaryTests` | TC-AL-20–24 | Verifies `AuditLogService.BuildSummary` output for the automatic self-healing event codes (`FileContentTypeRepaired`, `UnifiedDbAutoRecovered`, `VaultDbAutoRecovered`, `NoOpDraftDiscarded`) |
| AuditLogServiceRestoreFlushTests.cs | `AuditLogServiceRestoreFlushTests` | — | Verifies `AuditLogService.FlushPendingRestoreAuditAsync` writes a log entry only for the vault targeted by the restore marker (`RestoreAuditMarker`), leaving unrelated vaults untouched |
| AuditLogViewModelTests.cs | `AuditLogViewModelTests` | TC-ALV-01–14 | Verifies `AuditLogViewModel`'s three-way filter (severity / code group / text, combined with AND), the "no activity" vs. "no match" empty-state split, the `IsActive`/`NeedsReload` discipline for `AuditLogWrittenMessage` (no query while another page is in front), latest-request-wins load ordering, and that the display window uses `AppConstants.AuditLogRetentionDays` |
| AuditLogWrittenMessageTests.cs | `AuditLogWrittenMessageTests` | — | Verifies `AuditLogWrittenMessage` is broadcast exactly once (with the right `Code`/`EventLevel`) from each of `AuditLogService`'s write paths (`LogAsync`, `LogAuthFailedAsync`, `FlushPreAuthFailuresAsync`, `FlushPendingRestoreAuditAsync`), and not at all when nothing was written |
| AuditPayloadRegistrationTests.cs | `AuditPayloadRegistrationTests` | — | Verifies via reflection that every `AuditEventCode` has a matching `AuditPayload` record registered in `AuditPayloadJsonContext`, catching a forgotten AOT `[JsonSerializable]` registration |
| AuditWiringTests.cs | `AuditWiringTests` | — | Scans production `.cs` source directly to verify every `AuditEventCode` has an actual call site (`LogAsync`/`LogSettingAsync*`/`LogSettingChanged*`/`RestoreAuditMarker.Write`), catching a newly added enum value nobody wired up |
| AutoBackupUnifiedBackupTests.cs | `AutoBackupUnifiedBackupTests` | TC-AB-01–12 | Verifies the unified backup routine shared by manual and automatic backup (tmp staging, atomic rename, generation rotation) and the pending-backup flag that gates the automatic path. See [Notes](#autobackupunifiedbackuptests). |
| AutoTypeInfrastructureTests.cs | `AutoTypeInfrastructureTests` | TC-AT-01 | Verifies `IAutoTypeService` resolves successfully from a container built via production `App.ConfigureServices` |
| CertificateHelperTests.cs | `CertificateHelperTests` | TC-CRT-01–03 | Verifies `CertificateHelper.TryParse` for PKCS#12: a passwordless PFX yields its metadata, a password-protected PFX yields `null`, and parsing writes no private-key file to the user key store (`%APPDATA%\Microsoft\Crypto`, observed with a `FileSystemWatcher`) because the PFX is loaded with `EphemeralKeySet` |
| ClipboardAutoEraserTests.cs | `ClipboardAutoEraserTests` | TC-CAE-01–04 | Verifies all 4 cases of the clipboard auto-erase timer (`ClipboardAutoEraser`) against the real OS clipboard |
| CryptoServiceTests.cs | `CryptoServiceTests` | TC-CRY-01–08 | Verifies boundary values of `CryptoService` encryption/decryption across all 8 cases |
| CryptoVectorTests.cs | `CryptoVectorTests` | TC-VEC-01–04, TC-ARG-01–03, TC-ROB-01–06 | A known-answer test suite verifying cryptographic primitive correctness against official AES-256-GCM (NIST SP 800-38D) and Argon2id (RFC 9106) vectors |
| CustomFieldRoundtripTests.cs | `CustomFieldRoundtripTests` | TC-CFR-01–09 | Verifies AES-256-GCM encryption/decryption roundtrips for custom fields (TC-CFR-01–07), plus CustomFieldModel.FieldType's JSON round-trip and its plain-integer serialization (TC-CFR-08–09, the IsPassword/IsUrl/IsDate bool -> FieldType enum migration) |
| DbCorruptionTests.cs | `DbCorruptionTests` | TC-DBC-01–09 | Verifies DB corruption and backup scenarios using in-memory and file-based SQLite |
| DbRestoreValidatorTests.cs | `DbRestoreValidatorTests` | — | Verifies `DatabaseRestoreValidator`'s 3 lines of defense (magic header check, SQLite connection/binary structure check, schema + `PRAGMA integrity_check`) |
| DirectInjectionResultTests.cs | `DirectInjectionResultTests` | TC-DI-01–04 | Verifies `DirectInjectionResult`'s factory methods and mutually-exclusive property invariants (pure logic, no Win32 dependency) |
| EmergencyAccessViewModelTests.cs | `EmergencyAccessViewModelTests` | TC-EAV-01–05 | Verifies the file-picker route for supplying the emergency access QR PNG (`SelectQrFileCommand`, the alternative to dropping it). See [Notes](#emergencyaccessviewmodeltests). |
| EmergencyAccessUnlockTests.cs | `EmergencyAccessUnlockTests` | TC-EAC-01–07 | Verifies restricted read-only mode recovery via emergency access code (QR+PIN) (`AuthService.EmergencyAccessUnlockAsync`), the two-phase generate/commit split, and the suggested QR filename format. TC-EAC-07 verifies that a scan ending in `NoMatchingVault` leaves no pooled connection open on the scanned candidate files (a scanned file can still be deleted afterwards) |
| ExportImportTests.cs | `ExportImportTests` | TC-EXP-01–21 | Verifies export/import boundary values and pure logic: CSV parsing, duplicate title resolution, blank-row handling, skip-and-continue on decrypt failure, the CSV formula-injection guard, and JSON/CSV encoding and line-ending normalization. See [Notes](#exportimporttests). |
| FaviconServiceTests.cs | `FaviconServiceTests` | TC-FAV-01–03 | Regression coverage for the 2026-09-17 fix: `ComputeCacheKey` only checked the upper length bound, so an empty or whitespace-only domain produced a valid HMAC cache key and would have let `PrefetchAsync` build a meaningless `.../ip3/.ico` request. Confirms `GetCachedAsync` returns null for empty/whitespace domains and `PrefetchAsync` never touches the DB for one |
| FieldCryptoBoundaryTests.cs | `FieldCryptoBoundaryTests` | TC-FC-01–29 | Verifies `FieldCrypto`'s 512-byte boundary handling and zero-memory behavior |
| FontSettingsTests.cs | `FontSettingsTests` | TC-FNT-01–06 | Verifies `InstalledFontEnumerator`'s real GDI enumeration, the portable-use fallback decision (`AppSettingsViewModel.ResolveFontFamily`), and the `FontFamilyChangedMessage` broadcast |
| GalleryViewModelTests.cs | `GalleryViewModelTests` | TC-GAL-01–05 | Verifies `GalleryViewModel.LoadAsync`'s throttled ContentType-mismatch repair pass and its per-file audit log trail |
| GenSymbolsTests.cs | `GenSymbolsTests` | TC-GEN-01–04, TC-IMP-01–02 | Verifies default symbol re-injection, filtering, roundtrip, and backward compatibility for per-secret symbol customization (GenSymbols) |
| GenerationRaceConditionTests.cs | `GenerationRaceConditionTests` | TC-GR-01–07 | Verifies generation races, long-lived staging, GC ghost leaks, and SQLite VACUUM behavior |
| IdleTimeoutServiceTests.cs | `IdleTimeoutServiceTests` | TC-IDL-01–07 | Verifies auto-lock boundary values via time injection into `IdleTimeoutService` across all 6 cases, plus a re-entrancy case (TC-IDL-07) confirming the lock callback fires only once when two expiry checks land back-to-back without a reset |
| ImportEncodingNormalizerTests.cs | `ImportEncodingNormalizerTests` | — | Verifies `ImportEncodingNormalizer.NormalizeToUtf8` across every encoding branch (UTF-8 BOM, UTF-32 LE/BE rejection, UTF-16 LE/BE, ANSI, plain UTF-8) |
| IoEnvironmentDestructionTests.cs | `IoEnvironmentDestructionTests` | TC-SFR-13–16 | Verifies `AutoBackupService` robustness against I/O environment destruction (`IOException`, `UnauthorizedAccessException`) |
| IsWindowsHelloConfiguredForUnlockTests.cs | `IsWindowsHelloConfiguredForUnlockTests` | — | Verifies `AuthService.IsWindowsHelloConfiguredForUnlockAsync` is callable before unlock and never touches the vault DB |
| KeyDerivationOffloadTests.cs | `KeyDerivationOffloadTests` | TC-KDO-01–15 | Verifies every Argon2id derivation runs off the calling thread, and that a derivation finishing after a cancellation, a `Lock()` or a concurrent unlock is wiped instead of being written into the session. See [Notes](#keyderivationoffloadtests). |
| LocaleDomainRegistrationTests.cs | `LocaleDomainRegistrationTests` | — | Verifies every locale key's Domain/SubDomain segments resolve to a non-zero code in `LocalizationManager.PackKeyPrefix`'s hand-maintained dictionaries, catching a forgotten `DomainCodes`/`SubDomainCodes` registration before it silently collides with the "Common" domain |
| LocaleImportEncodingTests.cs | `LocaleImportEncodingTests` | — | Verifies `LocalizationService.ImportCustomAsync`'s 4 lines of defense (BOM/UTF-8 validation, JSON structural errors, placeholder soft-repair, abnormal-length rejection at the 120-byte floor) |
| LocaleTreeFlattenTests.cs | `LocaleTreeFlattenTests` | — | Verifies `LocalizationService.LoadBuiltinLocale`'s nested-JSON-to-flat-Dictionary conversion reconstructs the same dotted-string key contract. Also verifies `ExportAsync` output shape (nested, no `System.*`, round-trips through import), that exporting a saved custom locale keeps the imported pack's own `locale`/`displayName` instead of `"custom"`/`"Custom"`, and that an imported file cannot smuggle in an override of `System.LangJa`/`LangEn` (the built-in-language radio captions) or `System.Product.*` (the app's own name/description/copyright/license/disclaimer) via a forged `System` domain node |
| LocalizationManagerCategoryPresetsTests.cs | `LocalizationManagerCategoryPresetsTests` | — | Verifies `LocalizationManager.GetCategoryPresets()` returns category codes sorted ascending (the sole source of truth for category presets) |
| LocalizationManagerGetByIdTests.cs | `LocalizationManagerGetByIdTests` | — | Regression coverage (2026-09-18) for `Initialize()`'s `_byCode` population: a key present only in the fallback locale must resolve through `GetById`. See [Notes](#localizationmanagergetbyidtests). |
| LocalizationManagerSanitizeTests.cs | `LocalizationManagerSanitizeTests` | — | Verifies `LocalizationManager.Initialize()`'s control-character sanitization (line breaks preserved, whitespace runs collapsed, control chars stripped) |
| LocalizationServiceStartupResilienceTests.cs | `LocalizationServiceStartupResilienceTests` | — | Regression coverage (Rev.15) for a real-machine startup crash: `LoadForStartupAsync()` must fall back to the built-in locale, without throwing, when the unified DB is unreachable. See [Notes](#localizationservicestartupresiliencetests). |
| LockCycleStepOrderTests.cs | `LockCycleStepOrderTests` | TC-TSF-06–12 | Verifies the invariant 5-step execution order of `LockCycleOrchestrator.ExecuteAsync` |
| MasterAuthTests.cs | `MasterAuthTests` | TC-MA-01–05 | Verifies master authentication gate routing (Hello auth path / password path) |
| MessageSyncAndLifecycleTests.cs | `MessageSyncAndLifecycleTests` | — | Covers 4 UI messaging sync/async lifecycle scenarios via the process-wide `WeakReferenceMessenger.Default` singleton |
| MultiVaultAuthGateTests.cs | `MultiVaultAuthGateTests` | TC-MVG-01–07 | Verifies the immediate-rejection gate for sub-8-character input at the unlock front line |
| NLogConfigRuleTests.cs | `NLogConfigRuleTests` | TC-NLC-01–07 | Verifies the shipped `nlog.config` (routing rules and output layout) without writing any log file. See [Notes](#nlogconfigruletests). |
| PasswordChangeVaultRegistrySyncTests.cs | `PasswordChangeVaultRegistrySyncTests` | TC-PWC-01 | Verifies a regression in `VaultRegistries` salt follow-up updates after a master password change |
| PasswordEvaluationServiceTests.cs | `PasswordEvaluationServiceTests` | TC-PE-01–12 | Verifies password strength evaluation logic in `PasswordEvaluationService` |
| PasswordGeneratorEntropyTests.cs | `PasswordGeneratorEntropyTests` | TC-PG-01–20 | Verifies `PasswordGenerator` entropy, boundary values, and character-class guarantees |
| PasswordGeneratorStrengthTests.cs | `PasswordGeneratorStrengthTests` | TC-PGS-01, TC-PGS-03 | Verifies `EditingSecret` strength property updates and call counts after pressing the password generator button |
| PasswordGeneratorTests.cs | `PasswordGeneratorTests` | TC-PWG-01–06 | Verifies `PasswordGenerator`'s password generation logic across all 6 cases |
| PostUnlockMaintenanceServiceTests.cs | `PostUnlockMaintenanceServiceTests` | TC-PUM-01–07 | Regression coverage proving the 30-day soft-deleted secret/file purge and the FileModifiedAt backfill fire through `PostUnlockMaintenanceService.RunAsync`, the real call path `App.xaml.cs` uses after every unlock. See [Notes](#postunlockmaintenanceservicetests). |
| ProfileServiceTests.cs | `ProfileServiceTests` | TC-PS-01–03 | Regression coverage for the 2026-09-17 rewrite of `ParseIso(char[]?)` to zero-allocation parsing, and the explicit zeroing added to `ScanDisplayName`'s stackalloc buffer. See [Notes](#profileservicetests). |
| ProfileViewModelTests.cs | `ProfileViewModelTests` | TC-CFD-01–11, TC-PLC-01–03 | Verifies `ProfileViewModel` enters draft mode only when a newly-added custom field is actually edited, commits custom-field reorders directly to TwinA only when that is safe, never leaks an unobserved exception after the session lock, and follows the `IsActive` / `UnregisterAll` lifecycle discipline for `StorageChangedMessage`. See [Notes](#profileviewmodeltests). |
| ReadOnlyRestrictedModeTests.cs | `ReadOnlyRestrictedModeTests` | TC-ROM-01–10 | Verifies full read-only enforcement and seed-write suppression in restricted read-only mode (Route A). TC-ROM-09–10 verify that a manual backup (`BackupDatabaseCommand`) is refused with a warning notification before the folder picker is ever opened in restricted mode, and proceeds normally otherwise (control experiment) |
| RestoreAuditMarkerTests.cs | `RestoreAuditMarkerTests` | — | Regression tests for the `RestoreAuditMarker` file used to defer audit logging of `RestoreExecuted` events |
| RestoreHelperTests.cs | `RestoreHelperTests` | — | Verifies the `AuthService.Restore` helper functions after Rev.22 removed selective (Mode 2) restore: candidate scanning, the backup-ownership gate, pre-copy validation, the whole-tree overwrite copy, and post-restore Windows Hello invalidation. See [Notes](#restorehelpertests). |
| RestoreViewModelTests.cs | `RestoreViewModelTests` | — | Regression tests for `RestoreViewModel`'s single restore path (Rev.22): `ValidateAsync`, `AuthenticateAsync` (backup authentication gate), `HasExistingLocalData`, and `RestoreAsync` |
| SearchIndexTests.cs | `SearchIndexTests` | TC-SIX-01–07 | Verifies search index boundary values across all 7 cases. `SpanHit` is tested directly (TC-SIX-01/04/06/07); the empty and whitespace-only keyword fallbacks (TC-SIX-02/03) and the multi-word AND search across fields (TC-SIX-05) drive the real `ApplySearch` through a real `SecretsViewModel` and assert on `FilteredSecrets` |
| SecretDraftsRepositoryTests.cs | `SecretDraftsRepositoryTests` | TC-SDR-01–07 | Verifies `SecretDraftsRepository` (the SecretDrafts table split out of SecretHistory on 2026-08-21) boundary scenarios, moved from TC-SHR-12–16 |
| SecretEditModelCategoryFallbackTests.cs | `SecretEditModelCategoryFallbackTests` | — | Verifies `SecretEditModel.CategoryComboSelection`'s display-only fallback to "Uncategorized" for a `CategoryNum` no longer matching any preset, without rewriting the underlying value |
| SecretEditModelNotesLineEndingTests.cs | `SecretEditModelNotesLineEndingTests` | — | Verifies `SecretEditModel.Notes`'s setter normalizes a bare CR / CRLF (inserted by WinUI 3's `AcceptsReturn="True"` `TextBox` on Enter) to LF at the point of entry, leaving pre-existing LF-only text and the no-CR fast path unaffected |
| SecretHistoryRepositoryTests.cs | `SecretHistoryRepositoryTests` | TC-SHR-01–19 (12–16 moved to SecretDraftsRepositoryTests.cs) | Verifies Time Machine slot (history slot) boundary scenarios in `SecretHistoryRepository`, including PushAsync's atomic SecretDrafts clear |
| SecretsViewModelTests.cs | `SecretsViewModelTests` | TC-PGS-02, TC-PGS-04, TC-AL-16–17, TC-SCF-01–04, TC-CFO-01–04, TC-LST-01–02, TC-GSN-01, TC-EAT-01–03, TC-LBL-01–02 | Verifies `SecretsViewModel`'s strength evaluation (on-demand and debounced), view-audit logging, custom-field dirty tracking and reordering, list sorting, and draft NoOp detection. Most groups are regression tests for real bugs. See [Notes](#secretsviewmodeltests). |
| SelfHealingIntegrationTests.cs | `SelfHealingIntegrationTests` | TC-SHI-01–04, 08–10 | Verifies the autonomous self-healing sequence (automatic recovery from shadow on primary DB corruption, Fail-Fast) via an integration test. See [Notes](#selfhealingintegrationtests). |
| SelfHealingTests.cs | `SelfHealingTests` | TC-SH-01–03 | Verifies `ShadowFileService`'s static utilities (magic detection, shadow integrity check, restore) against real SQLite files |
| SessionGenerationGuardTests.cs | `SessionGenerationGuardTests` | TC-SC-01–12 | Directly verifies `SessionGenerationGuard`'s session ID management logic (formerly `ApplicationSecurityContext` — renamed to avoid confusion with the unrelated `ISecurityContext`/`AppSession`) |
| SessionLockGuardTests.cs | `SessionLockGuardTests` | TC-SLG-01–14 | Verifies `SessionLockGuard` barricade and cancellation behavior |
| SettingsCryptoInfrastructureTests.cs | `SettingsCryptoInfrastructureTests` | TC-SCI-01–04 | Verifies guard behavior when `K_shared` is unset in the new-vault-addition infrastructure |
| ShadowFileServiceCleanupTests.cs | `ShadowFileServiceCleanupTests` | TC-SHI-05–07 | Verifies `ShadowFileService.WriteAll()` purges every stale orphan shadow-file candidate in one pass, not just a single one (TC-SHI-07: fail-unsafe fallback regression — an unreadable VaultRegistries must skip the deletion scan entirely rather than treating every shadow as unreferenced) |
| SoftwareBitmapZeroerTests.cs | `SoftwareBitmapZeroerTests` | TC-BMZ-01–08 | Verifies `SoftwareBitmapZeroer.TryZero` zeroes every pixel byte of a writable Bgra8 bitmap (repeated 200 times to catch an uninitialized zero buffer) and returns `false` without throwing for an encoder-locked or non-Bgra8 bitmap; and pins the `ImageHelper` avatar pipeline output (dimensions, EXIF orientation baked into pixels, crop position) before/after its move from `SetSoftwareBitmap` to `SetPixelData(byte[])` |
| SqlitePoolCollectionRuleTests.cs | `SqlitePoolCollectionRuleTests` | TC-SPC-01 | Guards the `SequentialSqlitePool` collection rule (see `TestCollections.cs`): every test file that calls `SqliteConnection.ClearAllPools()` directly must belong to an exclusive collection. Needs a full repo checkout. See [Notes](#sqlitepoolcollectionruletests). |
| StateTransitionMatrixTests.cs | `StateTransitionMatrixTests` | TC-STM-01–07 | Verifies high-priority cases of the multi-screen cross state-transition matrix (soft delete, hard delete). TC-STM-08–17 are retired numbers (the orphan-file GC helpers they covered were removed) |
| StoredFileRepositoryBoundaryTests.cs | `StoredFileRepositoryBoundaryTests` | TC-SFR-01–12, 17–25 | Confirms `StoredFileRepository` boundary values, cryptographic roundtrips, `ContentType` validation, mismatch auto-repair/batch-quarantine loading, and the Gallery soft-delete trio (soft-delete link severance, undelete, alive-vs-including-deleted listing, hash-match salvage) |
| TotpCalculatorBoundaryTests.cs | `TotpCalculatorBoundaryTests` | TC-TOT-01–46 | Verifies `TotpCalculator`'s Base32 decoding, TOTP generation, otpauth:// URI parsing (including rejection of unsupported parameters), payload Pack/TryUnpack round-trip, and display digit-grouping. See [Notes](#totpcalculatorboundarytests). |
| TotpCalculatorTests.cs | `TotpCalculatorTests` | TC-TPC-01–09 | Verifies TOTP boundary values in `TotpCalculator` across all 9 cases |
| TotpImportDefaultsTests.cs | `TotpImportDefaultsTests` | TC-TID-01 | Regression test for a real-usage bug: importing a JSON record with a `totpSecret` but no `totpDigits`/`totpPeriod`/`totpAlgorithm` must yield a valid 6-digit/30s/SHA1 TOTP entry. See [Notes](#totpimportdefaultstests). |
| UnlockViewModelWindowsHelloStateTests.cs | `UnlockViewModelWindowsHelloStateTests` | TC-WH-15–18, 24–25 | Verifies `UnlockViewModel` form state-transition synchronization via `UnlockWithWindowsHelloCommand`, including the non-blocking notices for vaults the Hello scan silently excludes. See [Notes](#unlockviewmodelwindowshellostatetests). |
| VaultDbSchemaTests.cs | `VaultDbSchemaTests` | TC-VDS-01–05 | Verifies `VaultDbContext` vault DB schema generation and EF Core mapping consistency for the `AuditLog.EventLevel` column |
| VaultOperationsAutoBackupTests.cs | `VaultOperationsAutoBackupTests` | TC-VOB-01–03 | Verifies a regression found in real usage: creating a 3rd vault and importing secrets from plaintext never marked `AutoBackupService.MarkContentChanged()`, so the shutdown-time automatic backup never fired. Confirms the flag is set on `VaultOperationsViewModel.AddVaultAsync`/`ImportSecretsAsync` success, and not set when `AddVaultAsync` fails |
| ViewerProfileLinkSyncTests.cs | `ViewerProfileLinkSyncTests` | — | Verifies unlinking a profile-attached file from the Viewer's pin toggle does not snap back to "linked" due to a stale TwinA/TwinB union |
| ViewerSecretLinkSyncTests.cs | `ViewerSecretLinkSyncTests` | — | Verifies unlinking a file's owning secret from the Viewer is reflected live in `SecretsViewModel.EditingSecret` when that secret is open for editing |
| ViewerZoomTrackerTests.cs | `ViewerZoomTrackerTests` | TC-VZT-01–10 | Verifies `ViewerZoomTracker`, which classifies each `ViewChanged` zoom observation in the file viewer as either the landing of a zoom the viewer requested itself (fit, preset) or a zoom the user made with Ctrl+wheel/pinch, so the `Auto` (fit-to-window) state is released only by the latter: the first observation is only a baseline, unchanged zoom is ignored, a burst of requests (window being dragged) still recognises an older request landing late, landing on the newest request discards superseded ones, `Reset()` on file switch, float-rounding tolerance, and the 8-request memory limit |
| WindowCaptureProtectionTests.cs | `WindowCaptureProtectionTests` | TC-WCP-01–02 | Verifies fallback behavior for screen capture protection settings from older JSON versions, etc. |
| WindowsHelloDekMemoryTests.cs | `WindowsHelloDekMemoryTests` | TC-WH-11–14 | Verifies K_shared memory contamination prevention and `ZeroMemory` guarantees during Windows Hello authentication (`AcquireKSharedWithHelloAsync`) |
| WindowsHelloDisableMultiVaultTests.cs | `WindowsHelloDisableMultiVaultTests` | TC-WH-26–28 | Regression coverage for the 2026-09-17 fix: disabling Windows Hello for one vault must not break Hello unlock for the others, and the Hello paths must go through the injected adapters. See [Notes](#windowshellodisablemultivaulttests). |
| WindowsHelloInvalidateEverywhereTests.cs | `WindowsHelloInvalidateEverywhereTests` | — | Verifies `AuthService.InvalidateWindowsHelloEverywhereAsync` clears every vault's `VaultDEKHello` row (not just the unified DB's `KSharedHello`) on a Windows Hello profile mismatch |
| WindowsHelloProfileMismatchTests.cs | `WindowsHelloProfileMismatchTests` | TC-WH-19–20 | Verifies that a DPAPI decrypt failure on `EnterVaultWithHelloAsync` after successful biometric verification (vault moved to a different PC/account) throws `WindowsHelloProfileMismatchException` instead of a generic failure |
| WindowsHelloSelfHealingTests.cs | `WindowsHelloSelfHealingTests` | TC-WH-22–23 | End-to-end coverage for wiring shadow-based self-healing into both stages of the Hello flow (`AcquireKSharedWithHelloAsync`, `EnterVaultWithHelloAsync`), using real file-backed SQLite files under the production data dir. See [Notes](#windowshelloselfhealingtests). |
| WindowsHelloStateTransitionTests.cs | `WindowsHelloStateTransitionTests` | TC-WH-01–10, 21 | Verifies Windows Hello state transitions for all `UserConsentVerificationResult` patterns (`AcquireKSharedWithHelloAsync`). TC-WH-21 (Rev.11): a corrupted/unreadable vault DB in the per-vault scan must not abort the loop for other, unrelated vaults — regression coverage for a bug where one broken vault silently zeroed out Hello for every vault |
| WindowsHelloTests.cs | `WindowsHelloTests` | TC-WHV-01–04 | Verifies Windows Hello integration at the ViewModel layer (`MasterAuthResult` generation, rollback on Hello setting-change failure, etc.) |

## Notes

Background for the rows above that end with a link to this section.

### AutoBackupUnifiedBackupTests

- **TC-AB-07–10** (pending-backup flag): `RunBackup()` skips entirely unless `MarkContentChanged()` was called; once marked, it creates a generation and clears the flag; a failed attempt leaves the flag intact for retry; a manual backup also clears it.
- **TC-AB-11–12** (same-second collision suffix): a same-second collision now appends a numbered `-2` suffix (not a GUID), and `RotateGenerations` recognizes that suffix as healthy instead of deleting the folder as a naming-convention violation. Fixes a bug where a same-second backup collision was immediately self-deleted by the next rotation pass.

### EmergencyAccessViewModelTests

- A cancelled pick changes nothing.
- Picking a real QR PNG (rendered with the same ZXing + WinRT encoder as production) loads it, and the picker is asked for `.png` only.
- A non-image file and a throwing picker each report a general error without leaving the ViewModel busy (the operation stays retryable).
- While busy, the picker is never opened.

### ExportImportTests

- **TC-EXP-04**: a CSV row with an empty Title but a value in another column is imported rather than dropped.
- **TC-EXP-14–15**: when one record fails to decrypt, only that record is skipped and the rest continue.
- **TC-EXP-16**: the CSV formula-injection guard.
- **TC-EXP-17**: pins the full-Unicode JSON encoder's behavior: CJK text is written unescaped, but the full-width space (U+3000) is still escaped as `\uXXXX`.
- **TC-EXP-18**: the CSV CustomFields column is re-serialized with the relaxed encoder even for records whose stored JSON predates that switch.
- **TC-EXP-19**: `BuildSecret` normalizes CRLF/CR in an imported Notes value to LF before encryption, so it matches the normalized form `SecretEditModel.Notes`'s setter always produces for UI-entered text.
- **TC-EXP-20**: pins that `ParseCsvRecordsAsync` already reassembles a quoted multi-line CSV field as LF-only regardless of the source file's own line-ending style, so CSV import was never exposed to the bug TC-EXP-19 fixes for JSON.
- **TC-EXP-21**: blank lines, delimiter-only lines (`,,,,`) and whitespace-only lines are ignored instead of being imported as empty secrets (`IsBlankCsvRecord`).

### KeyDerivationOffloadTests

- **Guarantee**: no zombie K_shared/DEK; `SetActiveVault` is undone on cancelled vault entry.
- **TC-KDO-01–08** (unlock path): `AcquireKSharedAsync` / `EnterVaultAsync` / `UnlockAsync`, plus `UnlockViewModel` passing its cancellation token down and the Windows Hello command being unavailable while another unlock is in flight.
- **TC-KDO-09–15** (remaining sites): password change (a `Lock()` during the derivations aborts before any write; a `Lock()` during the writes still completes consistently and the new password unlocks); add vault (a `Lock()` during the derivations creates nothing; during the writes the registered slot still wraps the real K_shared, not a zeroed one, and a locked session is not switched); first-time setup with a cancelled token; emergency access code generation/unlock.
- **Technique**: an `ICryptoService` wrapper holds `DeriveKey` at a gate, or hooks the `GenerateSalt` / `Encrypt` / `Decrypt` calls that always follow a derivation, so the interruption is reproduced deterministically rather than by timing.

### LocalizationManagerGetByIdTests

- **Bug**: two separate `Dictionary.TryGetValue` calls (`_fallback`, then `_messages` "primary overrides") let the second call's default(T)-on-miss assignment silently null out a value the first call had already found, so a key present only in the fallback locale was never registered and `GetById` always returned the `[0x...]` not-found placeholder for it.
- **Verifies**: fallback-only, primary-only, and primary-overrides-fallback all resolve correctly after the fix.

### LocalizationServiceStartupResilienceTests

- **Bug**: `LoadForStartupAsync()` queried the unified DB with no try/catch, so when the unified DB is unreachable (e.g. the startup corruption-freeze path) the unhandled exception took down the whole process before the frozen recovery window ever got a chance to render.

### NLogConfigRuleTests

- **TC-NLC-01–03** (routing rules, checked by asking NLog directly which levels are enabled per category): EF Core loggers are discarded at every level because they emit executed SQL with table and column names (01); other framework loggers (`Microsoft.*`, `System.*`) keep Warn and above only (02); application loggers (`NaimitsuVault.*`) keep Info and above (03).
- **TC-NLC-04–05** (layout): when a framework logger passes an exception, only the exception type is rendered, never the stack trace, paths or message (04); with no exception the line ends at the message (05).
- **TC-NLC-06**: the log is size-bounded (daily and above 10 MB, 10 archives).
- **TC-NLC-07**: NLog's internal log is off.

### PostUnlockMaintenanceServiceTests

- **Why**: the pure boundary logic is already covered by TC-DBC-08; these cases prove the real call path actually fires it.
- **TC-PUM-04–05**: mirror TC-PUM-01–02 for `StoredFiles` (Gallery soft-delete).
- **TC-PUM-06**: the purge also deletes the now-dangling `SecretFileLinks` row for a purged secret (no FK cascade is configured on that table, mirroring the existing handling in `SecretRepository.HardDeleteAsync`).
- **TC-PUM-07**: the file purge also sweeps any lingering `SecretFileLinks`/`ProfileFileLinks` rows pointing at a purged file (mirroring `StoredFileRepository.DeleteAsync`), while leaving links to a file still inside its retention window untouched.

### ProfileServiceTests

- **Background**: `ParseIso(char[]?)` now calls `DateTime.TryParse(ReadOnlySpan<char>)` (previously it allocated a `new string` just to zero it again), and `ScanDisplayName`'s stackalloc buffer now gets an explicit `ZeroMemory` before returning.
- **TC-PS-01**: identity expiry dates round-trip through `CommitProfileAsync`/`GetExpiryInfoAsync`.
- **TC-PS-02–03**: `LoadDisplayNameAsync`'s Nickname-over-Name precedence still works.

### ProfileViewModelTests

- **Draft-mode entry**: a bare "+" add must not dirty the model until the new field's Label or Value is actually edited, while removing an already-committed field dirties it immediately.
- **TC-CFD-05–08**: reordering custom fields commits directly to TwinA only when the model is fully clean and every field already exists in TwinA; otherwise TwinA is left untouched. TC-CFD-08 reproduces WinUI 3's actual Remove-then-Insert reorder mechanics (never `ObservableCollection.Move`) as regression coverage for a real bug.
- **TC-CFD-09–10**: `HasUnsavedChanges`, the dirty-tracking flag `ProfilePage.xaml.cs`'s blur handlers gate on before calling `AutoSaveDraftAsync`. Added as a fix: without it, blurring any unrelated field after an unedited "+" add falsely entered draft mode from the field-count difference alone.
- **TC-CFD-11**: `AutoSaveDraftAsync` called after the session-lock barricade completes without throwing and writes no draft, so fire-and-forget callers never produce an unobserved `OperationCanceledException`.
- **TC-PLC-01–03**: `StorageChangedMessage` handling follows the `IsActive` lifecycle discipline. A paused `ProfileViewModel` ignores the message (no DB read / thumbnail decryption while another page is in front), a resumed one reconciles `ProfileFiles` with `AppSession.PendingProfileImageIds` (positive control for TC-PLC-01), and a disposed one no longer receives it (`UnregisterAll`). Regression coverage for a gap where the handler had no `IsActive` guard and ran a wasted refresh on every notification while the page was hidden.

### RestoreHelperTests

- **Rev.22 scope**: vault candidate scanning; the `AuthenticateBackupNkdbAsync` backup-ownership gate; `ValidateBackupFilesAsync`/`HasExistingLocalData` (the pre-copy validation and first-guard trigger split out in Rev.22); the whole-tree overwrite copy; the post-restore Windows Hello invalidation.
- **Rev.23 regression**: opening a WAL-mode backup file with plain `Mode=ReadOnly` left `-wal`/`-shm` junk in the backup source folder; fixed via the `immutable=1` URI parameter.
- **Rev.24**: unconditional 3-generation rotation of `archived_*` safety-net folders (a restore with a 4th+ existing folder keeps only the newest 3).

### SecretsViewModelTests

- **TC-PGS-02, TC-PGS-04, TC-AL-16–17**: on-demand evaluation, debounced evaluation, and view-audit logging.
- **TC-SCF-01–04**: a bare custom-field "+" Add doesn't dirty the model, while an actual Label/Value edit or a Remove does.
- **TC-CFO-01–04**: reordering custom fields commits directly to `Secrets.CustomFields` without pushing a TimeMachine generation only when the model is fully clean and every field already exists in Gen0 (otherwise the DB is left untouched). TC-CFO-04 reproduces WinUI 3's actual Remove-then-Insert reorder mechanics (never `ObservableCollection.Move`) as regression coverage for a real bug.
- **TC-LST-01–02**: `FilteredSecrets` (the left-pane list) stays sorted by Title across both a full `LoadAsync` and repeated incremental Add+rename cycles. TC-LST-02 is regression coverage for two real bugs found together: (1) `RefreshListItem`/`GetOrCreatePoolItem` aliased `SecretEditModel.Title`'s cached display string into a long-lived list item, so it was zeroed in place on the next `Dispose()` of that model; (2) `CategoryItems`' tree nodes share the same pooled item as `FilteredSecrets`, so the tree branch's in-place `.Title` write made the flat-list branch's own change check always see "no change" and skip its re-sort.
- **TC-GSN-01**: editing a field then reverting it to its original value clears the draft (`FindFirstMismatch` NoOp check). Regression coverage for a real bug: `GenSymbols`' display-only default-symbols fallback (applied to `SecretEditModel` but not to the raw Gen0 entity) made the NoOp comparison permanently report a mismatch once any draft save had run.
- **TC-EAT-01–03**: `SnapshotSerializer.WriteEntity` normalizes a Secret's `ExpiresAt` to local midnight the same way the edit-model reconstruction does. Regression coverage for a real bug: a Secret whose stored `ExpiresAt` wasn't exactly local-midnight-in-UTC (e.g. legacy data) permanently failed the NoOp comparison, so adding a custom field and immediately removing it again left a phantom draft behind forever.
- **TC-LBL-01–02**: renaming a standard field's label (Username/Password/etc.) enters draft mode, and reverting it clears the draft again. Regression coverage for a real bug: `FindFirstMismatch` never compared `LabelOverridesBuf`, so a label-only edit was silently discarded as a NoOp before the draft could ever persist, even though the compare dialog's label-highlight support was already fully implemented.

### SelfHealingIntegrationTests

- **TC-SHI-08–10** (Rev.10): the vault-DB side of the same three-way branch: corrupted-but-present auto-recovery, no-shadow fail-fast, and ambiguous (2+) shadow-candidate fail-fast. Regression coverage for a bug where a corrupted-but-present vault file never triggered recovery at all.

### SqlitePoolCollectionRuleTests

- **Why**: `ClearAllPools()` is process-wide and can dispose a connection another concurrently running test is using (`ObjectDisposedException: 'SQLitePCL.sqlite3'`), so its callers must run in a `DisableParallelization = true` collection.
- **How**: reads the test sources.
- **Limit**: indirect callers (via `DatabaseInitializer`, `AutoBackupService`, `ShadowFileService`, `AuthService`) are not detectable from source and remain a review checklist item.

### TotpCalculatorBoundaryTests

- **TC-TOT-01–28**: Base32 decoding boundaries, TOTP generation, and URI parsing against RFC 4226/4648-compliant test vectors.
- **TC-TOT-29–36** (added for full 8-digit/SHA256/Period support): otpauth:// `algorithm=` parsing, SHA1/SHA256/SHA512 code divergence, and the Pack/TryUnpack/TryUnpackUtf8 payload round-trip. TC-TOT-31 now asserts that an unsupported `algorithm` (MD5, SHA224, SHA-1, empty) rejects the whole URI instead of falling back to SHA1.
- **TC-TOT-37–39**: `FormatCodeForDisplay`'s digit-grouping, for display only (6 → "123 456", 8 → "1234 5678", any other length unchanged).
- **TC-TOT-40–41**: TryUnpack/TryUnpackUtf8 reject a payload with an extra (5th) field instead of silently absorbing it into Algorithm.
- **TC-TOT-42–46** (strict otpauth:// parameter validation, 2026-09-22): a `digits` outside 6–8 or not a plain number (TC-TOT-42), the accepted boundary values 6/7/8 (TC-TOT-43), and a `period` that is not a positive integer (TC-TOT-44) each reject the URI instead of falling back to a default; the URI a user pastes into the setup dialog (`digits=8&period=60&algorithm=SHA256`) yields all four parameters (TC-TOT-45); parameters the app doesn't use (`image`, `lock`) are ignored (TC-TOT-46). The dialog's paste handling itself is WinUI UI code and is not covered by unit tests.

### TotpImportDefaultsTests

- **Symptom**: the imported TOTP entry's code never displayed.
- **Cause**: `ImportSecretDto` declared these as non-nullable with C# field initializers, which `DeserializeAsyncEnumerable`'s source-generated deserializer does not apply for absent JSON keys (leaves them at 0/0/null instead of 6/30/SHA1).
- **Verifies**: the full BuildSecret/FieldCrypto/TryUnpack round trip now yields a valid 6-digit/30s/SHA1 config.

### UnlockViewModelWindowsHelloStateTests

- **TC-WH-24** (Rev.12): when the Hello scan silently drops one unrecoverable vault but others remain usable, a non-blocking `Unlock.ErrorVaultDbCorrupted` notice must still surface, without blocking a successful entry into the remaining vault.
- **TC-WH-25** (Rev.13): a vault auto-recovered from a shadow that predates its Hello registration is also silently excluded (no exception, just `hasDek=false`); a non-blocking `Common.InfoVaultDbAutoRecovered` notice must surface for this case too.

### WindowsHelloDisableMultiVaultTests

- **TC-WH-26**: `DisableWindowsHelloAsync` removes only the current vault's `VaultDEKHello` and never deletes the unified DB's `KSharedHello`. Previously a stale decoy-vault check (`CurrentVaultDbNumber != 0`, always true for a real vault) deleted `KSharedHello` unconditionally, breaking Hello unlock for every other vault sharing K_shared.
- **TC-WH-27**: `IsWindowsHelloSupportedAsync` reads from the injected `_helloAdapter` instead of calling the real WinRT `UserConsentVerifier` directly.
- **TC-WH-28**: `ResetMasterPasswordWithHelloAsync` goes through `_helloAdapter`/`_protectedData` end-to-end instead of the real WinRT/DPAPI statics and `App.UiDispatcherQueue` (null in a test host, so this method was previously untestable).

### WindowsHelloSelfHealingTests

- **TC-WH-22**: a corrupted-but-present Hello-registered vault with a healthy shadow is auto-recovered by Stage 1 and then successfully entered by Stage 2. Also asserts (Rev.14) that the forced `0xFF02` audit-log entry is written on the Hello success path (it previously only fired on the password path) and that the flag is cleared afterward.
- **TC-WH-23**: a corrupted vault with no usable shadow is excluded from the Hello candidate list and reported via `VaultDbUnrecoverable` instead of a silent skip.
