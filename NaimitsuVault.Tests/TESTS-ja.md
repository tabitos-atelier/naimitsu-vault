# テストケースインデックス

各テストクラスのコメント内に現れる `TC-xxx-nn` 形式（`xxx` は2〜3文字の英字プレフィックス、`nn` は2桁の連番）の
テストケース ID を、このファイル内で解決できるようにするための索引。ID 番号はプロジェクト全体で一意であり、
プレフィックスは原則としてファイルごとに1つ割り当てるが、同一ドメインが複数ファイルにまたがる場合
（`TC-AL`・`TC-PGS`・`TC-WH`・`TC-SFR` 等）は、番号を使い回さずファイル間で番号帯を分割している。
一部のテストクラスでは廃止済みケースの番号を欠番のまま維持している（意図的な設計。詳細は各ファイルの
コメントを参照）。個々のテストクラスの詳細な検証内容は各ファイルの XML ドキュメントコメントおよび
テストメソッド名を参照すること。

ID 列の `—` は、そのクラスにケース ID を割り当てていないことを表す（個々のケースはテストメソッド名で識別する）。概要欄は1〜2文に留める。実機バグの根本原因や ID ごとの内訳など残すべき背景がある行は、末尾の[備考](#備考)にテストクラス名の見出しで移し、行末からリンクしている。

| ファイル | テストクラス | テストケース ID | 概要 |
|---|---|---|---|
| AppDbContextGenerationTests.cs | `AppDbContextGenerationTests` | TC-DBX-01〜10, 15 | `AppDbContext.SaveChangesAsync` オーバーライドにおける世代チェック（楽観的排他制御）を検証する |
| AppSessionMemoryHygieneTests.cs | `AppSessionMemoryHygieneTests` | TC-AS-01〜19 | `AppSession` の DEK GC ピン留め・`ZeroMemory`・`AvatarBytes` セッターのゼロ化・`ResetPerVaultSelectionState` などメモリ衛生を検証する |
| ArchiveHelperTests.cs | `ArchiveHelperTests` | TC-ZIP-01〜06 | `ArchiveHelper.TryListZipEntries` を検証する：UTF-8 フラグ付き・ASCII・ディレクトリのエントリ、ZIP でないバイト列での `null`、および UTF-8 フラグのない旧 ZIP のエントリ名が U+FFFD に化けず OS のレガシー（OEM）コードページで復号されること（CP932 / GBK のケース。テスト実行マシンのロケールに依存しないようコードページを明示的に渡す） |
| AsyncCancellationRaceTests.cs | `AsyncCancellationRaceTests` | TC-ACR-01, 05〜12 | Repository 層の `CancellationToken` 処理と `AppDbContext` 世代チェックの非同期競合を検証する。TC-ACR-02〜04 は欠番（対象だった GC 用ヘルパーメソッドを削除したため） |
| AuditEventCodeBandTests.cs | `AuditEventCodeBandTests` | — | 全 `AuditEventCode` の値が定義済み 0xXYZZ 帯域に収まることを検証し、将来のコード追加による帯域逸脱を検出する |
| AuditLogClipboardAutoTypeVaultSummaryTests.cs | `AuditLogClipboardAutoTypeVaultSummaryTests` | TC-AL-33〜37 | クリップボードコピー・AutoType・保管庫作成のイベントコード（`SecretFieldCopiedToClipboard`・`SecretAutoTypeExecuted`・`TimeMachineValueCopiedToClipboard`・`ProfileFieldCopiedToClipboard`・`VaultCreated`）に対する `AuditLogService.BuildSummary` の出力を検証する |
| AuditLogImportExportLocaleSummaryTests.cs | `AuditLogImportExportLocaleSummaryTests` | TC-AL-25〜32 | インポート・エクスポートの個別失敗、CSV数式インジェクション対策、カスタムロケールインポートに関するイベントコード（`ImportItemFailed`・`ExportItemFailed`・`LocaleImportKeyMissing`・`LocaleImportValueTooLong`・`CsvFormulaGuardApplied`・`LocaleImportSucceeded`・`LocaleImportFailed`・`LocaleImportPlaceholderBroken`）に対する `AuditLogService.BuildSummary` の出力を検証する |
| AuditLogSelfHealSummaryTests.cs | `AuditLogSelfHealSummaryTests` | TC-AL-20〜24 | 自動セルフヒーリングのイベントコード（`FileContentTypeRepaired`・`UnifiedDbAutoRecovered`・`VaultDbAutoRecovered`・`NoOpDraftDiscarded`）に対する `AuditLogService.BuildSummary` の出力を検証する |
| AuditLogServiceRestoreFlushTests.cs | `AuditLogServiceRestoreFlushTests` | — | `AuditLogService.FlushPendingRestoreAuditAsync` が復元マーカー（`RestoreAuditMarker`）の対象保管庫にのみログを書き込み、無関係な保管庫には触れないことを検証する |
| AuditLogViewModelTests.cs | `AuditLogViewModelTests` | TC-ALV-01–14 | `AuditLogViewModel` の3条件絞り込み（重要度・コード分類・テキスト。AND 結合）、空表示の「アクティビティなし」と「一致なし」の出し分け、`AuditLogWrittenMessage` に対する `IsActive`/`NeedsReload` 規律（他画面の表示中はクエリしない）、最新要求優先の読み込み順序、表示期間に `AppConstants.AuditLogRetentionDays` を使うことを検証する |
| AuditLogWrittenMessageTests.cs | `AuditLogWrittenMessageTests` | — | `AuditLogService` の各書き込み経路（`LogAsync`・`LogAuthFailedAsync`・`FlushPreAuthFailuresAsync`・`FlushPendingRestoreAuditAsync`）から `AuditLogWrittenMessage` が正しい `Code`/`EventLevel` で1回だけ発行されること、および何も書き込まれない場合は発行されないことを検証する |
| AuditPayloadRegistrationTests.cs | `AuditPayloadRegistrationTests` | — | リフレクションにより全 `AuditEventCode` に対応する `AuditPayload` レコードが `AuditPayloadJsonContext` に登録されていることを検証し、AOT用 `[JsonSerializable]` 登録漏れを検出する |
| AuditWiringTests.cs | `AuditWiringTests` | — | プロダクションの `.cs` ソースを直接走査し、全 `AuditEventCode` に実際の呼び出し箇所（`LogAsync`／`LogSettingAsync*`／`LogSettingChanged*`／`RestoreAuditMarker.Write`）が存在することを検証する（誰も配線していない新規コードの追加を検出する） |
| AutoBackupUnifiedBackupTests.cs | `AutoBackupUnifiedBackupTests` | TC-AB-01〜12 | 手動・自動バックアップ共通の一括バックアップ処理（tmp ステージング・アトミックリネーム・世代ローテーション）と、自動バックアップの起動を制御する保留中バックアップフラグを検証する。 詳細は[備考](#autobackupunifiedbackuptests)を参照。 |
| AutoTypeInfrastructureTests.cs | `AutoTypeInfrastructureTests` | TC-AT-01 | 本番 `App.ConfigureServices` で構築したコンテナから `IAutoTypeService` が正しく解決できることを検証する |
| CertificateHelperTests.cs | `CertificateHelperTests` | TC-CRT-01〜03 | `CertificateHelper.TryParse` の PKCS#12 処理を検証する：パスワードなし PFX はメタデータを返し、パスワード保護付き PFX は `null` を返すこと、および PFX を `EphemeralKeySet` でロードするためパース中にユーザーキーストア（`%APPDATA%\Microsoft\Crypto`）へ秘密鍵ファイルが書き出されないこと（`FileSystemWatcher` で観測） |
| ClipboardAutoEraserTests.cs | `ClipboardAutoEraserTests` | TC-CAE-01〜04 | クリップボード自動消去タイマー（`ClipboardAutoEraser`）の全4ケースを実クリップボードを用いて検証する |
| CryptoServiceTests.cs | `CryptoServiceTests` | TC-CRY-01〜08 | `CryptoService` の暗号化・復号の境界値を全8ケースで検証する |
| CryptoVectorTests.cs | `CryptoVectorTests` | TC-VEC-01〜04, TC-ARG-01〜03, TC-ROB-01〜06 | AES-256-GCM（NIST SP 800-38D）と Argon2id（RFC 9106）の公定ベクターで暗号プリミティブの正確性を検証する既知解答テストスイート |
| CustomFieldRoundtripTests.cs | `CustomFieldRoundtripTests` | TC-CFR-01〜09 | カスタムフィールドの AES-256-GCM 暗号化・復号往復を全7ケースで検証する（TC-CFR-01〜07）。加えて CustomFieldModel.FieldType（IsPassword/IsUrl/IsDate の bool 3本を廃止した enum 移行）の JSON 往復と整数シリアライズを検証する（TC-CFR-08〜09） |
| DbCorruptionTests.cs | `DbCorruptionTests` | TC-DBC-01〜09 | DB 破損・バックアップに関するシナリオをインメモリ／ファイルベース SQLite で検証する |
| DbRestoreValidatorTests.cs | `DbRestoreValidatorTests` | — | `DatabaseRestoreValidator` の3段防御（マジックヘッダー検査・SQLite接続/バイナリ構造検査・スキーマ+`PRAGMA integrity_check`）を検証する |
| DirectInjectionResultTests.cs | `DirectInjectionResultTests` | TC-DI-01〜04 | `DirectInjectionResult` のファクトリメソッドと相互排他的なプロパティ不変条件を検証する（Win32依存のない純粋ロジック） |
| EmergencyAccessViewModelTests.cs | `EmergencyAccessViewModelTests` | TC-EAV-01〜05 | 緊急アクセスコードの QR PNG を、ドロップの代わりにファイル選択で渡す経路（`SelectQrFileCommand`）を検証する。 詳細は[備考](#emergencyaccessviewmodeltests)を参照。 |
| EmergencyAccessUnlockTests.cs | `EmergencyAccessUnlockTests` | TC-EAC-01〜07 | 緊急アクセスコード（QR+PIN）による制限閲覧モード復元（`AuthService.EmergencyAccessUnlockAsync`）、生成/確定の2フェーズ分離、および推奨QRファイル名の書式を検証する。TC-EAC-07は、走査が `NoMatchingVault` で終わった場合でも、走査した候補ファイルにプール済み接続が残らないこと（走査後もそのファイルを削除できること）を検証する |
| ExportImportTests.cs | `ExportImportTests` | TC-EXP-01〜21 | エクスポート・インポートの境界値と純粋ロジック（CSV解析・重複タイトル解決・空行の扱い・復号失敗時のスキップ継続・CSV数式インジェクション対策・JSON/CSVのエンコードと改行の正規化）を検証する。 詳細は[備考](#exportimporttests)を参照。 |
| FaviconServiceTests.cs | `FaviconServiceTests` | TC-FAV-01〜03 | 2026-09-17是正の回帰テスト。`ComputeCacheKey` が上限長のみチェックしており、空文字・空白のみのドメインでも有効なHMACキャッシュキーが生成され、`PrefetchAsync` が無意味な `.../ip3/.ico` リクエストを構築しうる不具合の回帰を検証する。`GetCachedAsync` が空文字・空白ドメインで null を返すこと、`PrefetchAsync` がDBに一切触れないことを確認する |
| FieldCryptoBoundaryTests.cs | `FieldCryptoBoundaryTests` | TC-FC-01〜29 | `FieldCrypto` の512バイト境界値とゼロメモリ処理を検証する |
| FontSettingsTests.cs | `FontSettingsTests` | TC-FNT-01〜06 | `InstalledFontEnumerator` の実GDI列挙、ポータブル利用時のフォールバック判定（`AppSettingsViewModel.ResolveFontFamily`）、`FontFamilyChangedMessage` のブロードキャストを検証する |
| GalleryViewModelTests.cs | `GalleryViewModelTests` | TC-GAL-01〜05 | `GalleryViewModel.LoadAsync` のスロットル付き ContentType ミスマッチ修復処理とファイル単位の監査ログ記録を検証する |
| GenSymbolsTests.cs | `GenSymbolsTests` | TC-GEN-01〜04, TC-IMP-01〜02 | パスワードジェネレーターの記号個別化（GenSymbols）に関するデフォルト再注入・フィルタリング・ラウンドトリップ・後方互換性を検証する |
| GenerationRaceConditionTests.cs | `GenerationRaceConditionTests` | TC-GR-01〜07 | 世代競合・長寿命ステージング・GC ゴーストリーク・SQLite VACUUM に関する挙動を検証する |
| IdleTimeoutServiceTests.cs | `IdleTimeoutServiceTests` | TC-IDL-01〜07 | オートロックの境界値を `IdleTimeoutService` の時刻注入により全6ケースで検証する。TC-IDL-07 は、リセットを挟まず期限切れ判定が連続2回発生してもロックコールバックが1回しか発火しないことを検証する再入防止ケース |
| ImportEncodingNormalizerTests.cs | `ImportEncodingNormalizerTests` | — | `ImportEncodingNormalizer.NormalizeToUtf8` の全エンコーディング分岐（UTF-8 BOM・UTF-32 LE/BE の却下・UTF-16 LE/BE・ANSI・素のUTF-8）を検証する |
| IoEnvironmentDestructionTests.cs | `IoEnvironmentDestructionTests` | TC-SFR-13〜16 | I/O 環境破壊（`IOException`・`UnauthorizedAccessException`）に対する `AutoBackupService` の堅牢性を検証する |
| IsWindowsHelloConfiguredForUnlockTests.cs | `IsWindowsHelloConfiguredForUnlockTests` | — | `AuthService.IsWindowsHelloConfiguredForUnlockAsync` がアンロック前でも呼び出し可能で、保管庫DBに一切触れないことを検証する |
| KeyDerivationOffloadTests.cs | `KeyDerivationOffloadTests` | TC-KDO-01〜15 | すべての Argon2id 導出が呼び出しスレッドの外で実行されること、およびキャンセル・`Lock()`・別経路のアンロック完了の後に導出が完了した場合、結果がセッションへ書き込まれずにゼロ化されることを検証する。 詳細は[備考](#keyderivationoffloadtests)を参照。 |
| LocaleDomainRegistrationTests.cs | `LocaleDomainRegistrationTests` | — | 全ロケールキーのDomain/SubDomainセグメントが`LocalizationManager.PackKeyPrefix`の手書き辞書で非ゼロコードに解決できることを検証し、`DomainCodes`/`SubDomainCodes`への登録漏れが「Common」ドメインと静かに衝突する前に検出する |
| LocaleImportEncodingTests.cs | `LocaleImportEncodingTests` | — | `LocalizationService.ImportCustomAsync` の4段防御（BOM/UTF-8検証・JSON構造エラー・プレースホルダーのソフト修復・120バイト下限での異常長拒否）を検証する |
| LocaleTreeFlattenTests.cs | `LocaleTreeFlattenTests` | — | `LocalizationService.LoadBuiltinLocale` のネストJSON→フラットDictionary変換が、既存のドット区切りキー契約を正しく再構築することを検証する。あわせて `ExportAsync` の出力形（ネスト・`System.*` なし・インポートへの往復）、保存済みカスタムロケールのエクスポートが `"custom"`/`"Custom"` ではなくインポートしたパック自身の `locale`/`displayName` を保持すること、およびインポートファイルが偽の `System` ドメインを仕込んでも `System.LangJa`・`LangEn`（組み込み言語ラジオボタンの表示名）や `System.Product.*`（アプリ自身の名称・説明・著作権表示・ライセンス・免責事項）を上書きできないことを検証する |
| LocalizationManagerCategoryPresetsTests.cs | `LocalizationManagerCategoryPresetsTests` | — | `LocalizationManager.GetCategoryPresets()` がカテゴリコードを昇順で返すことを検証する（カテゴリプリセットの唯一の情報源） |
| LocalizationManagerGetByIdTests.cs | `LocalizationManagerGetByIdTests` | — | `Initialize()` の `_byCode` 構築の回帰テスト（2026-09-18）。フォールバックロケールにのみ存在するキーが `GetById` で解決されることを検証する。 詳細は[備考](#localizationmanagergetbyidtests)を参照。 |
| LocalizationManagerSanitizeTests.cs | `LocalizationManagerSanitizeTests` | — | `LocalizationManager.Initialize()` の制御文字サニタイズ（改行は保持・連続空白は1つに圧縮・制御文字は除去）を検証する |
| LocalizationServiceStartupResilienceTests.cs | `LocalizationServiceStartupResilienceTests` | — | 実機で発生した起動時クラッシュの回帰テスト（Rev.15）。統合DBが到達不能でも、`LoadForStartupAsync()` が例外を投げずビルトインロケールへフォールバックすることを検証する。 詳細は[備考](#localizationservicestartupresiliencetests)を参照。 |
| LockCycleStepOrderTests.cs | `LockCycleStepOrderTests` | TC-TSF-06〜12 | `LockCycleOrchestrator.ExecuteAsync` の5ステップ実行順序の不変律を検証する |
| MasterAuthTests.cs | `MasterAuthTests` | TC-MA-01〜05 | マスター認証ゲート（Hello認証パス／パスワードパス）のルーティングを検証する |
| MessageSyncAndLifecycleTests.cs | `MessageSyncAndLifecycleTests` | — | プロセス全体で共有される `WeakReferenceMessenger.Default` シングルトンを介したUIメッセージング同期/非同期ライフサイクルの4シナリオを検証する |
| MultiVaultAuthGateTests.cs | `MultiVaultAuthGateTests` | TC-MVG-01〜07 | アンロック最前線における8文字未満入力の即時遮断ゲートを検証する |
| NLogConfigRuleTests.cs | `NLogConfigRuleTests` | TC-NLC-01〜07 | 出荷する `nlog.config`（ルーティング規則と出力形式）を、ログファイルを書かずに検証する。 詳細は[備考](#nlogconfigruletests)を参照。 |
| PasswordChangeVaultRegistrySyncTests.cs | `PasswordChangeVaultRegistrySyncTests` | TC-PWC-01 | マスターパスワード変更後の `VaultRegistries` salt 追従更新に関する回帰を検証する |
| PasswordEvaluationServiceTests.cs | `PasswordEvaluationServiceTests` | TC-PE-01〜12 | `PasswordEvaluationService` によるパスワード強度評価ロジックを検証する |
| PasswordGeneratorEntropyTests.cs | `PasswordGeneratorEntropyTests` | TC-PG-01〜20 | `PasswordGenerator` のエントロピー・境界値・文字種保証を検証する |
| PasswordGeneratorStrengthTests.cs | `PasswordGeneratorStrengthTests` | TC-PGS-01, TC-PGS-03 | パスワード生成ボタン押下後の `EditingSecret` 強度プロパティ更新と呼び出し回数を検証する |
| PasswordGeneratorTests.cs | `PasswordGeneratorTests` | TC-PWG-01〜06 | `PasswordGenerator` のパスワード生成ロジックを全6ケースで検証する |
| PostUnlockMaintenanceServiceTests.cs | `PostUnlockMaintenanceServiceTests` | TC-PUM-01〜07 | 30日経過したソフト削除済みの機密情報・添付ファイルのパージと FileModifiedAt バックフィルが、実際の呼び出し経路（`App.xaml.cs` がアンロック後に呼ぶ `PostUnlockMaintenanceService.RunAsync`）を通しても確実に発火することを検証する回帰テスト。 詳細は[備考](#postunlockmaintenanceservicetests)を参照。 |
| ProfileServiceTests.cs | `ProfileServiceTests` | TC-PS-01〜03 | 2026-09-17是正の回帰テスト。`ParseIso(char[]?)` のゼロアロケーション化と、`ScanDisplayName` の stackalloc バッファへの明示的なゼロ化を検証する。 詳細は[備考](#profileservicetests)を参照。 |
| ProfileViewModelTests.cs | `ProfileViewModelTests` | TC-CFD-01〜11、TC-PLC-01〜03 | `ProfileViewModel` が、新規追加したカスタムフィールドを実際に編集した時点で初めて下書きモードに入ること、並び替えは安全な場合のみ TwinA へ直接コミットすること、セッションロック後の呼び出しで未観測の例外を出さないこと、`StorageChangedMessage` の受信が `IsActive` / `UnregisterAll` のライフサイクル規律に従うことを検証する。 詳細は[備考](#profileviewmodeltests)を参照。 |
| ReadOnlyRestrictedModeTests.cs | `ReadOnlyRestrictedModeTests` | TC-ROM-01〜10 | 制限閲覧モード（Route A）における完全読取専用化とシード書き込み抑止を検証する。TC-ROM-09〜10は、制限閲覧モードでは手動バックアップ（`BackupDatabaseCommand`）がフォルダ選択ダイアログを開く前に警告通知つきで拒否されること、通常モードでは通常どおり進むこと（対照実験）を検証する |
| RestoreAuditMarkerTests.cs | `RestoreAuditMarkerTests` | — | `RestoreExecuted` イベントの監査ログ記録を遅延させるマーカーファイル（`RestoreAuditMarker`）の回帰を検証する |
| RestoreHelperTests.cs | `RestoreHelperTests` | — | Rev.22 で個別リストア（Mode 2）を廃止した後の `AuthService.Restore` ヘルパー群（保管庫候補走査・バックアップ本人確認ゲート・事前検証・全体上書きコピー・復元後の Windows Hello 無効化）を検証する。 詳細は[備考](#restorehelpertests)を参照。 |
| RestoreViewModelTests.cs | `RestoreViewModelTests` | — | `RestoreViewModel` の単一リストア経路（Rev.22）の回帰を検証する: `ValidateAsync`・`AuthenticateAsync`（バックアップ認証ゲート）・`HasExistingLocalData`・`RestoreAsync` |
| SearchIndexTests.cs | `SearchIndexTests` | TC-SIX-01〜07 | 検索インデックスの境界値を全7ケースで検証する。`SpanHit` は直接テストし（TC-SIX-01/04/06/07）、空・空白のみキーワードの全件返し（TC-SIX-02/03）と複数フィールドにまたがるスペース区切り AND 検索（TC-SIX-05）は、実 `SecretsViewModel` を通じて本物の `ApplySearch` を駆動し `FilteredSecrets` を検証する |
| SecretDraftsRepositoryTests.cs | `SecretDraftsRepositoryTests` | TC-SDR-01〜07 | `SecretDraftsRepository`（2026-08-21 に SecretHistory から分離した SecretDrafts テーブル）の境界値シナリオを検証する（旧 TC-SHR-12〜16 から移動） |
| SecretEditModelCategoryFallbackTests.cs | `SecretEditModelCategoryFallbackTests` | — | 現在のプリセットに一致しない `CategoryNum` に対する `SecretEditModel.CategoryComboSelection` の表示専用フォールバック（実体は書き換えず「未分類」表示のみ）を検証する |
| SecretEditModelNotesLineEndingTests.cs | `SecretEditModelNotesLineEndingTests` | — | `SecretEditModel.Notes` のセッターが、WinUI 3 の `AcceptsReturn="True"` な `TextBox` が Enter で挿入する単独 CR / CRLF を、入力時点で LF へ正規化することを検証する。もとから LF のみのテキストと、CR を含まない高速パスは影響を受けない |
| SecretHistoryRepositoryTests.cs | `SecretHistoryRepositoryTests` | TC-SHR-01〜19（12〜16は SecretDraftsRepositoryTests.cs へ移動） | タイムマシンスロット（履歴スロット）の境界値シナリオを `SecretHistoryRepository` で検証する（PushAsync の SecretDrafts アトミッククリアを含む） |
| SecretsViewModelTests.cs | `SecretsViewModelTests` | TC-PGS-02, TC-PGS-04, TC-AL-16〜17, TC-SCF-01〜04, TC-CFO-01〜04, TC-LST-01〜02, TC-GSN-01, TC-EAT-01〜03, TC-LBL-01〜02 | `SecretsViewModel` の強度評価（オンデマンド・デバウンス）、閲覧監査ログ記録、カスタムフィールドのdirty判定と並び替え、一覧のソート、下書きのNoOp判定を検証する。大半のグループは実バグの回帰テスト。 詳細は[備考](#secretsviewmodeltests)を参照。 |
| SelfHealingIntegrationTests.cs | `SelfHealingIntegrationTests` | TC-SHI-01〜04, 08〜10 | 自律救命シーケンス（一次DB破損時のシャドウからの自動復旧・Fail-Fast）を統合テストで検証する。 詳細は[備考](#selfhealingintegrationtests)を参照。 |
| SelfHealingTests.cs | `SelfHealingTests` | TC-SH-01〜03 | `ShadowFileService` の静的ユーティリティ（マジック判定・シャドウ健全性確認・復元）を実SQLiteファイルで検証する |
| SessionGenerationGuardTests.cs | `SessionGenerationGuardTests` | TC-SC-01〜12 | `SessionGenerationGuard` のセッション ID 管理ロジックを直接検証する（旧 `ApplicationSecurityContext`。無関係な `ISecurityContext`/`AppSession` との混同を避けるため改名） |
| SessionLockGuardTests.cs | `SessionLockGuardTests` | TC-SLG-01〜14 | `SessionLockGuard` のバリケード・キャンセル動作を検証する |
| SettingsCryptoInfrastructureTests.cs | `SettingsCryptoInfrastructureTests` | TC-SCI-01〜04 | 保管庫新規追加インフラにおける `K_shared` 未設定時のガード動作を検証する |
| ShadowFileServiceCleanupTests.cs | `ShadowFileServiceCleanupTests` | TC-SHI-05〜07 | `ShadowFileService.WriteAll()` が1回の呼び出しで残存する孤児シャドウファイル候補をすべてパージすることを検証する（TC-SHI-07: フェイルセーフ不備の回帰テスト — VaultRegistries が読み取れない場合、全シャドウを未参照とみなして削除するのではなく削除スキャン自体をスキップすること） |
| SoftwareBitmapZeroerTests.cs | `SoftwareBitmapZeroerTests` | TC-BMZ-01〜08 | `SoftwareBitmapZeroer.TryZero` が書き込み可能な Bgra8 ビットマップの全ピクセルバイトをゼロ化すること（未初期化のゼロ用バッファを検出するため200回繰り返す）、エンコーダーに渡した後のビットマップと Bgra8 以外では例外を投げず `false` を返すことを検証する。あわせて、`ImageHelper` のアバター処理が `SetSoftwareBitmap` から `SetPixelData(byte[])` へ移る前後で出力（寸法・画素に焼き込まれた EXIF 回転・切り出し位置）が変わらないことを固定する |
| SqlitePoolCollectionRuleTests.cs | `SqlitePoolCollectionRuleTests` | TC-SPC-01 | `SequentialSqlitePool` コレクションの規則（`TestCollections.cs` 参照）を守るガードテスト。`SqliteConnection.ClearAllPools()` を直接呼ぶテストファイルがすべて排他的コレクションに属していることを検証する。リポジトリ全体のチェックアウトが必要。 詳細は[備考](#sqlitepoolcollectionruletests)を参照。 |
| StateTransitionMatrixTests.cs | `StateTransitionMatrixTests` | TC-STM-01〜07 | 多画面交差・状態遷移マトリクスの高優先度ケース（ソフト削除・完全削除）を検証する。TC-STM-08〜17 は欠番（対象だった孤児ファイル GC 用メソッドを削除したため） |
| StoredFileRepositoryBoundaryTests.cs | `StoredFileRepositoryBoundaryTests` | TC-SFR-01〜12, 17〜25 | `StoredFileRepository` の境界値・暗号ラウンドトリップ・`ContentType` 検証、ミスマッチ自動修復・バッチ隔離読み込み、およびギャラリーソフト削除3点セット（ソフト削除時のリンク切断・復元・生存/全件取得の切り分け・ハッシュ一致サルベージ）を確認する |
| TotpCalculatorBoundaryTests.cs | `TotpCalculatorBoundaryTests` | TC-TOT-01〜46 | `TotpCalculator` の Base32 デコード・TOTP生成・otpauth:// URIパース（未対応パラメーターの拒否を含む）・ペイロードの Pack/TryUnpack 往復・表示用の桁区切りを検証する。 詳細は[備考](#totpcalculatorboundarytests)を参照。 |
| TotpCalculatorTests.cs | `TotpCalculatorTests` | TC-TPC-01〜09 | TOTP の境界値を `TotpCalculator` で全9ケース検証する |
| TotpImportDefaultsTests.cs | `TotpImportDefaultsTests` | TC-TID-01 | 実機で発覚した回帰のテスト。`totpSecret` はあるが `totpDigits`/`totpPeriod`/`totpAlgorithm` を省略したJSONレコードをインポートしても、有効な6桁/30秒/SHA1のTOTPエントリになることを検証する。 詳細は[備考](#totpimportdefaultstests)を参照。 |
| UnlockViewModelWindowsHelloStateTests.cs | `UnlockViewModelWindowsHelloStateTests` | TC-WH-15〜18, 24〜25 | `UnlockWithWindowsHelloCommand` を通じた `UnlockViewModel` のフォーム状態遷移同期と、Helloスキャンがサイレントに除外した保管庫に対する非ブロッキング通知を検証する。 詳細は[備考](#unlockviewmodelwindowshellostatetests)を参照。 |
| VaultDbSchemaTests.cs | `VaultDbSchemaTests` | TC-VDS-01〜05 | `VaultDbContext` による保管庫DBスキーマ生成と `AuditLog.EventLevel` カラムの EF Core マッピング整合性を検証する |
| VaultOperationsAutoBackupTests.cs | `VaultOperationsAutoBackupTests` | TC-VOB-01〜03 | 実機で発覚した回帰（保管庫3つ目の新規作成・平文インポートが自動バックアップの差分検出フラグ `AutoBackupService.MarkContentChanged()` を一度も立てず、終了時の自動バックアップが永久に発火しなかった不具合）を検証する。`VaultOperationsViewModel.AddVaultAsync`／`ImportSecretsAsync` の成功時にフラグが立つこと、`AddVaultAsync` 失敗時には立たないことを確認する |
| ViewerProfileLinkSyncTests.cs | `ViewerProfileLinkSyncTests` | — | ビューワのピン切り替えでプロフィール添付ファイルの紐付けを解除した際、TwinA/TwinB の古い合成判定により「紐付け済み」へ揺り戻らないことを検証する |
| ViewerSecretLinkSyncTests.cs | `ViewerSecretLinkSyncTests` | — | ビューワからファイルの所有機密を解除した際、その機密が編集中であれば `SecretsViewModel.EditingSecret` へ即座に反映されることを検証する |
| ViewerZoomTrackerTests.cs | `ViewerZoomTrackerTests` | TC-VZT-01〜10 | ファイルビューワの `ViewChanged` で観測したズーム倍率の変化を、ビューワ自身が要求したズームの着地（フィット・プリセット）か、ユーザーが Ctrl+ホイール／ピンチで行ったズームかに判別する `ViewerZoomTracker` を検証する。これにより `Auto`（フィットウィンドウ）はユーザー操作のズームでのみ解除される。最初の観測は基準値にとどめる、倍率が変わらないイベントは無視する、連続要求（ウィンドウをドラッグ中）で古い要求の着地が遅れて届いても自前の要求と識別する、最新の要求の着地で置き換え済みの古い要求を破棄する、ファイル切り替え時の `Reset()`、float の丸め許容、記憶する要求は8件までという上限を確認する |
| WindowCaptureProtectionTests.cs | `WindowCaptureProtectionTests` | TC-WCP-01〜02 | 画面キャプチャ保護設定の旧バージョンJSONからのフォールバック等を検証する |
| WindowsHelloDekMemoryTests.cs | `WindowsHelloDekMemoryTests` | TC-WH-11〜14 | Windows Hello 認証における K_shared メモリ汚染防止・`ZeroMemory` 保証を検証する（`AcquireKSharedWithHelloAsync`） |
| WindowsHelloDisableMultiVaultTests.cs | `WindowsHelloDisableMultiVaultTests` | TC-WH-26〜28 | 2026-09-17是正の回帰テスト。1つの保管庫のWindows Hello無効化が他の保管庫のHello解錠を壊さないこと、およびHello経路が注入済みアダプター経由で動作することを検証する。 詳細は[備考](#windowshellodisablemultivaulttests)を参照。 |
| WindowsHelloInvalidateEverywhereTests.cs | `WindowsHelloInvalidateEverywhereTests` | — | プロファイル不一致時、`AuthService.InvalidateWindowsHelloEverywhereAsync` が統合DBの `KSharedHello` だけでなく全保管庫の `VaultDEKHello` 行を無効化することを検証する |
| WindowsHelloProfileMismatchTests.cs | `WindowsHelloProfileMismatchTests` | TC-WH-19〜20 | 生体認証成功後に `EnterVaultWithHelloAsync` の DPAPI 復号だけが失敗した場合（別PC/別アカウントへの保管庫持ち出し）に、汎用失敗ではなく `WindowsHelloProfileMismatchException` がスローされることを検証する |
| WindowsHelloSelfHealingTests.cs | `WindowsHelloSelfHealingTests` | TC-WH-22〜23 | Hello経路の両段階（`AcquireKSharedWithHelloAsync`・`EnterVaultWithHelloAsync`）にシャドウ自己修復を配線したことのエンドツーエンド検証。実際の本番データフォルダ配下に実ファイルのSQLiteを配置して検証する。 詳細は[備考](#windowshelloselfhealingtests)を参照。 |
| WindowsHelloStateTransitionTests.cs | `WindowsHelloStateTransitionTests` | TC-WH-01〜10, 21 | `UserConsentVerificationResult` の全パターンに対する Windows Hello 状態遷移を検証する（`AcquireKSharedWithHelloAsync`）。TC-WH-21（Rev.11）: 保管庫ごとのスキャン中に1つの保管庫DBが破損/読み取り不能でも、他の無関係な保管庫のループを中断してはならないことを検証する。1つの壊れた保管庫が全保管庫分のHelloを巻き添えでゼロ件にしてしまう不具合の回帰テスト |
| WindowsHelloTests.cs | `WindowsHelloTests` | TC-WHV-01〜04 | ViewModel 層における Windows Hello 統合（`MasterAuthResult` 生成・Hello設定変更失敗時のロールバック等）を検証する |

## 備考

上表で、行末にこの節へのリンクがある行の背景。

### AutoBackupUnifiedBackupTests

- **TC-AB-07〜10**（保留中バックアップフラグ）: `MarkContentChanged()` を呼んでいなければ `RunBackup()` は何もせずスキップし、呼ばれていれば世代を作成してフラグをクリアする。失敗時はフラグを残して再試行に備え、手動バックアップもフラグをクリアする。
- **TC-AB-11〜12**（同一秒衝突時のサフィックス）: 同一秒内の衝突では GUID ではなく連番 `-2` サフィックスを付与するようになり、`RotateGenerations` はこのサフィックス付きフォルダを命名規則違反と誤判定せず健全なフォルダとして認識する。同一秒内の衝突が次のローテーションで即座に自己削除されていた不具合の修正。

### EmergencyAccessViewModelTests

- キャンセルは何も変更しない。
- 本物の QR PNG（本番と同じ ZXing + WinRT エンコーダーで生成）を選ぶと読み込まれ、ピッカーには `.png` だけが要求される。
- 画像でないファイルとピッカーの例外は、いずれも一般エラーを報告し、ViewModel を処理中のままにせず再試行できる。
- 処理中はピッカーを開かない。

### ExportImportTests

- **TC-EXP-04**: Titleが空でも他の列に値がある行が、取りこぼされずに取り込まれる。
- **TC-EXP-14〜15**: 1件の復号失敗時に、その1件のみスキップして残りを継続出力する。
- **TC-EXP-16**: CSV数式インジェクション対策。
- **TC-EXP-17**: JSONの全Unicode範囲エンコーダーが、CJK文字は非エスケープで出力しつつ、全角スペース（U+3000）は仕様上引き続き`\uXXXX`エスケープされたままであることを固定化する。
- **TC-EXP-18**: その切り替え以前に保存されたレコードのCustomFields JSONでも、CSV出力時にrelaxedエンコーダーで再シリアライズされる。
- **TC-EXP-19**: インポートしたNotesの値に含まれるCRLF/CRが暗号化前にLFへ正規化され、`SecretEditModel.Notes`のセッターがUI入力時に常に生成する正規化済みの形式と一致する。
- **TC-EXP-20**: `ParseCsvRecordsAsync`が複数行にまたがるクォート付きCSVフィールドを、元ファイルの改行形式（CRLF/LF）に関わらず常にLFのみへ再結合することを固定化する（CSVインポートはTC-EXP-19がJSON向けに修正した不具合の影響を元々受けていなかったことの根拠）。
- **TC-EXP-21**: 空行・カンマだけの行（`,,,,`）・空白だけの行が、空の機密情報として取り込まれず無視される（`IsBlankCsvRecord`）。

### KeyDerivationOffloadTests

- **保証内容**: K_shared/DEK のゾンビ復旧なし。保管庫入場のキャンセル時は `SetActiveVault` を巻き戻す。
- **TC-KDO-01〜08**（アンロック経路）: `AcquireKSharedAsync` / `EnterVaultAsync` / `UnlockAsync`。あわせて `UnlockViewModel` がキャンセルトークンを渡すこと、別の解錠の実行中は Windows Hello コマンドが使えないこと。
- **TC-KDO-09〜15**（残りの経路）: パスワード変更（導出中の `Lock()` は書き込み前に中断し何も書かれない／書き込み中の `Lock()` でも一貫した状態で完了し新パスワードで解錠できる）、保管庫追加（導出中の `Lock()` は何も作らない／書き込み中は登録されたスロットがゼロではなく本物の K_shared をラップし、ロック済みセッションは切り替えない）、キャンセル済みトークンでの初回セットアップ、緊急アクセスコードの発行・復元。
- **手法**: `DeriveKey` をゲートで止める、または導出の後に必ず呼ばれる `GenerateSalt` / `Encrypt` / `Decrypt` にフックを挿す `ICryptoService` ラッパーを使い、割り込みをタイミングではなく決定的に再現する。

### LocalizationManagerGetByIdTests

- **不具合**: `_fallback`→`_messages`（「primaryが上書き」の意図）の2つの別々の `Dictionary.TryGetValue` 呼び出しで、2回目の呼び出しがキー未検出時に out 引数を default(T) で上書きしてしまい、1回目で見つけていた値が黙って null 化されていた。その結果 `_fallback` にのみ存在するキーは `_byCode` に一切登録されず、`GetById` が常に `[0x...]` の未検出表示を返していた。
- **検証内容**: 是正後、フォールバックのみ・primaryのみ・primary優先の3パターンが正しく解決される。

### LocalizationServiceStartupResilienceTests

- **不具合**: `LoadForStartupAsync()` が統合DBへtry/catchなしでクエリしていたため、統合DBが到達不能（起動時の破損凍結パス等）だと未処理例外がプロセス全体を巻き添えにし、凍結復旧ウィンドウが描画される前に落ちていた。

### NLogConfigRuleTests

- **TC-NLC-01〜03**（ルーティング規則。カテゴリ別に有効なレベルを NLog へ直接問い合わせて確認）: EF Core のロガーは、テーブル名・カラム名を含む実行済み SQL を出力するため全レベル破棄（01）、その他のフレームワーク（`Microsoft.*`・`System.*`）は Warn 以上のみ（02）、アプリケーション（`NaimitsuVault.*`）は Info 以上（03）。
- **TC-NLC-04〜05**（出力形式）: フレームワークが例外を渡してきても、レイアウトは例外の型名だけを出しスタックトレース・パス・メッセージは出さない（04）。例外がなければメッセージで終わる（05）。
- **TC-NLC-06**: ログ容量に上限がある（日次＋10MB 超過で分割、10 世代）。
- **TC-NLC-07**: NLog 内部ログは Off。

### PostUnlockMaintenanceServiceTests

- **趣旨**: 境界値ロジック自体は TC-DBC-08 で検証済み。ここでは実際の呼び出し経路でも発火することを確認する。
- **TC-PUM-04〜05**: ギャラリーのソフト削除に対応する TC-PUM-01〜02 の対（StoredFiles版）。
- **TC-PUM-06**: パージ対象の機密情報に紐づく `SecretFileLinks` 行が同時に削除される（同テーブルには外部キーカスケードが設定されておらず、`SecretRepository.HardDeleteAsync` の既存の対応と同じ扱いが必要）。
- **TC-PUM-07**: パージ対象のファイルを指したまま残存している `SecretFileLinks`/`ProfileFileLinks` 行も同時に削除される（`StoredFileRepository.DeleteAsync` と同じ防御的扱い）。保持期間内のファイルへのリンクは削除されない。

### ProfileServiceTests

- **是正内容**: `ParseIso(char[]?)` を、`new string` を確保して即ゼロ化していた実装からゼロアロケーションの `DateTime.TryParse(ReadOnlySpan<char>)` 呼び出しへ書き換え、`ScanDisplayName` のstackallocバッファにも return 前の明示的な `ZeroMemory` を追加した。
- **TC-PS-01**: `CommitProfileAsync`/`GetExpiryInfoAsync` を通した身分証有効期限日付の往復。
- **TC-PS-02〜03**: `LoadDisplayNameAsync` のNickname優先ロジックが変わらず動作する。

### ProfileViewModelTests

- **下書きモード移行**: 「＋」追加ボタン単体ではモデルをdirty化せず、新規フィールドのLabel/Valueを実際に編集した時点で初めてdirty化する。既に確定済みのフィールドを削除した場合は即座にdirty化する。
- **TC-CFD-05〜08**: カスタムフィールドの並び替えは、モデルが完全にcleanかつ全フィールドがTwinAに既存の場合のみTwinAへ直接コミットされ、それ以外はTwinAに一切触れない。TC-CFD-08はWinUI3の実際の並び替え機構（ObservableCollection.Moveではなく Remove→Insert）を再現した実バグの回帰テスト。
- **TC-CFD-09〜10**: `HasUnsavedChanges`（`ProfilePage.xaml.cs`のフォーカスアウトハンドラが`AutoSaveDraftAsync`呼び出し前に参照するdirty追跡フラグ）。修正として追加した。このフラグがないと、未編集の「＋」追加直後に無関係な項目からフォーカスが外れただけで、フィールド数の差分のみを理由に下書きモードへ誤って移行していた。
- **TC-CFD-11**: セッションロックのバリケード後に`AutoSaveDraftAsync`を呼んでも例外なく完了し下書きを書き込まない（fire-and-forget呼び出しで未観測の`OperationCanceledException`を生まない）。
- **TC-PLC-01〜03**: `StorageChangedMessage` の受信が `IsActive` ライフサイクル規律に従う。一時停止中（`Pause()` 後）の `ProfileViewModel` はメッセージを無視し（他画面の表示中に DB 読み取り・サムネイル復号を行わない）、再開後（`Resume()` 後）は `ProfileFiles` を `AppSession.PendingProfileImageIds` に合わせて再構築する（TC-PLC-01 の対照）、`Dispose()` 後はそもそも受信しない（`UnregisterAll`）。ハンドラに `IsActive` ガードがなく、ページ非表示中も通知のたびに無駄な再構築を行っていた漏れの回帰テスト。

### RestoreHelperTests

- **Rev.22 の対象**: 保管庫候補走査、`AuthenticateBackupNkdbAsync` バックアップ本人確認ゲート、`ValidateBackupFilesAsync`/`HasExistingLocalData`（Rev.22 で分離した事前検証・第1ガード発火条件）、全体上書きコピー、復元後の Windows Hello 無効化。
- **Rev.23 の回帰**: WALモードのバックアップファイルを素の `Mode=ReadOnly` で開くとバックアップ元フォルダに `-wal`/`-shm` が残留する不具合。`immutable=1` URIパラメータで修正。
- **Rev.24**: `archived_*` 退避フォルダの無条件3世代ローテーション（4件目以降が存在する状態からのリストアで最新3件のみ残ることを検証）。

### SecretsViewModelTests

- **TC-PGS-02, TC-PGS-04, TC-AL-16〜17**: オンデマンド評価・デバウンス評価・閲覧監査ログ記録。
- **TC-SCF-01〜04**: カスタムフィールドの「＋」追加単体ではモデルをdirty化せず、実際のLabel/Value編集または削除ではdirty化する。
- **TC-CFO-01〜04**: カスタムフィールドの並び替えは、モデルが完全にcleanかつ全フィールドがGen0に既存の場合のみ `Secrets.CustomFields` へタイムマシン世代を作らず直接コミットされ、それ以外はDBに一切触れない。TC-CFO-04はWinUI3の実際の並び替え機構（ObservableCollection.Moveではなく Remove→Insert）を再現した実バグの回帰テスト。
- **TC-LST-01〜02**: 左ペイン一覧 `FilteredSecrets` が、全件リロード（`LoadAsync`）と、追加必須の仮タイトル挿入→リネームを繰り返すインクリメンタル更新の両方でタイトル順ソートを維持する。TC-LST-02は同時に発見した実バグ2件の回帰テスト：①`RefreshListItem`/`GetOrCreatePoolItem` が `SecretEditModel.Title`（`TitleBuf` のキャッシュ済み表示用文字列）をそのまま長寿命のリスト項目へ代入していたため、その `SecretEditModel` が次に `Dispose()` される（選択変更のたび）とキャッシュ文字列がその場でゼロ化され表示中のタイトルが吹き飛ぶ、②カテゴリツリーの各ノードと `FilteredSecrets` が同一Idに対して同じプール済み `SecretListItemViewModel` インスタンスを共有しているため、常に先に走るツリー側の分岐が `.Title` をその場で上書きし、フラットリスト側の分岐が読む「タイトルが変わったか」の比較が常に「変化なし」と誤判定され、実際に左ペインが束縛している `FilteredSecrets` の再ソートだけが黙って抜け落ちる。
- **TC-GSN-01**: フィールドを編集して下書き化した後、元の値に戻すと下書きが解除される（`FindFirstMismatch` のNoOp判定）。実バグの回帰テスト：`GenSymbols` の表示専用デフォルト記号セットへのフォールバックが `SecretEditModel` 側にのみ適用されGen0の生エンティティには適用されないため、一度でも下書き保存が走るとNoOp比較が恒久的に不一致と誤判定していた。
- **TC-EAT-01〜03**: `SnapshotSerializer.WriteEntity` が機密情報の `ExpiresAt` を、編集モデル側の復元と同じ「ローカル日付の深夜0時」へ正規化する。実バグの回帰テスト：保存済み `ExpiresAt` がローカル深夜0時のUTC換算と厳密に一致しない機密情報（例：旧データ）では NoOp 判定が恒久的に不一致となり、追加項目を追加して即座に削除するだけで幽霊のような下書きが残り続けていた。
- **TC-LBL-01〜02**: 標準フィールド（ユーザー名・パスワード等）のラベルを変更すると下書きモードに入り、元に戻すと下書きが解除される。実バグの回帰テスト：`FindFirstMismatch` が `LabelOverridesBuf` を一切比較していなかったため、ラベルのみの変更はNoOpと誤判定され、下書き比較ダイアログのラベルハイライト機能はすでに実装済みだったにもかかわらず、下書き自体が一度も生存できず発動する機会がなかった。

### SelfHealingIntegrationTests

- **TC-SHI-08〜10**（Rev.10）: 保管庫DB側の同じ三分岐（破損だが存在する場合の自動復旧・シャドウなしFail-Fast・シャドウ2件以上の曖昧判定Fail-Fast）。「ファイルは存在するが破損」ケースで自動復旧が一度も発動しなかった不具合の回帰テスト。

### SqlitePoolCollectionRuleTests

- **理由**: `ClearAllPools()` の呼び出しはプロセス全体に作用し、同時実行中の別テストが使用中の接続を破棄しうる（`ObjectDisposedException: 'SQLitePCL.sqlite3'`）ため、呼び出し元は `DisableParallelization = true` のコレクションに属する必要がある。
- **方法**: テストのソースを読み込んで検証する。
- **限界**: 間接的な呼び出し元（`DatabaseInitializer`・`AutoBackupService`・`ShadowFileService`・`AuthService` 経由）はソースから判定できないため、レビュー時のチェックリスト項目として残る。

### TotpCalculatorBoundaryTests

- **TC-TOT-01〜28**: Base32 デコード境界値・TOTP生成・URIパーサを RFC 4226/4648 準拠テストベクタで検証する。
- **TC-TOT-29〜36**（8桁・SHA256・Period 完全対応の追加分）: otpauth:// の `algorithm=` パース、SHA1/SHA256/SHA512 のコード差異、Pack/TryUnpack/TryUnpackUtf8 ペイロード往復。TC-TOT-31 は、未対応の `algorithm`（MD5・SHA224・SHA-1・空）を SHA1 へ戻さず、URI 全体を拒否することを確認する。
- **TC-TOT-37〜39**: `FormatCodeForDisplay` の表示専用桁区切り（6桁→"123 456"、8桁→"1234 5678"、それ以外の桁数はそのまま）。
- **TC-TOT-40〜41**: TryUnpack/TryUnpackUtf8 が余剰な5番目のフィールドを含むペイロードを、Algorithm に取り込まず確実に拒否する。
- **TC-TOT-42〜46**（otpauth:// パラメーターの厳格な検証、2026-09-22）: 6〜8 以外・数値でない `digits`（TC-TOT-42）、受理する境界値 6/7/8（TC-TOT-43）、正の整数でない `period`（TC-TOT-44）は、既定値へ戻さず URI を拒否する。設定ダイアログへ貼り付ける URI（`digits=8&period=60&algorithm=SHA256`）から 4 つのパラメーターが得られること（TC-TOT-45）、アプリが使わないパラメーター（`image`・`lock`）は無視すること（TC-TOT-46）。ダイアログ側の貼り付け処理は WinUI の UI コードで、単体テストの対象外。

### TotpImportDefaultsTests

- **症状**: インポートしたTOTPエントリのコードが一切表示されなかった。
- **原因**: `ImportSecretDto` がこれらを非nullable型＋C#フィールド初期化子で宣言していたため、JSONにキーが存在しない場合に `DeserializeAsyncEnumerable` のソース生成デシリアライザーが初期値を適用せず `0`/`0`/`null` のまま読み込まれていた（本来の既定値 `6`/`30`/`SHA1` ではない）。
- **検証内容**: BuildSecret/FieldCrypto/TryUnpack の一連の往復で、正しく6桁/30秒/SHA1の設定になる。

### UnlockViewModelWindowsHelloStateTests

- **TC-WH-24**（Rev.12）: Helloスキャンが復旧不能な保管庫1件をサイレントに除外しても他の保管庫が使える場合、非ブロッキングの `Unlock.ErrorVaultDbCorrupted` 通知が表示され、かつ残った保管庫への解錠成功を妨げない。
- **TC-WH-25**（Rev.13）: Hello登録前のシャドウから自動復旧された保管庫も（例外なく`hasDek=false`という形で）サイレントに除外されるケースで、非ブロッキングの `Common.InfoVaultDbAutoRecovered` 通知が表示される。

### WindowsHelloDisableMultiVaultTests

- **TC-WH-26**: `DisableWindowsHelloAsync` が現在の保管庫の `VaultDEKHello` のみを削除し、統合DBの `KSharedHello` を一切削除しない。従来は旧デコイ保管庫向けの判定 `CurrentVaultDbNumber != 0`（通常の保管庫では常に真）が残存しており、`KSharedHello` を無条件削除していたため、同一K_sharedを共有する他保管庫のHello解錠が巻き添えで機能停止していた。
- **TC-WH-27**: `IsWindowsHelloSupportedAsync` が実際のWinRT `UserConsentVerifier` を直呼びせず、注入済みの `_helloAdapter` から読み取る。
- **TC-WH-28**: `ResetMasterPasswordWithHelloAsync` が実際のWinRT/DPAPI静的呼び出しおよび `App.UiDispatcherQueue`（テストホストでは null のためこのメソッドは従来テスト不能だった）を経由せず、`_helloAdapter`/`_protectedData` を通じてエンドツーエンドで動作する。

### WindowsHelloSelfHealingTests

- **TC-WH-22**: Hello登録済みかつ健全なシャドウを持つ「存在するが破損」保管庫が、Stage 1で自動復旧され、Stage 2で正常に解錠できる。（Rev.14追加分）Hello成功経路でも強制監査ログ`0xFF02`が書き込まれ（従来はパスワード経路のみ）、書き込み後にフラグがクリアされることも検証する。
- **TC-WH-23**: 使えるシャドウが無い破損保管庫がHello候補から除外され、サイレントスキップではなく `VaultDbUnrecoverable` として報告される。
