// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Localization;

/// <summary>
/// Int code constants for locale keys used by the notification service (IAppNotificationService).
/// Avoids capturing a string into a closure; only an immediate (int) value is passed to the lambda.
/// Because each property looks up LocalizationManager.GetCode on every access,
/// accessing it before LocalizationManager.Initialize() will not leave it permanently stuck at 0.
/// </summary>
public static class LK
{
    // ── Common (0x00) ──
    public static int Common_Error                       => LocalizationManager.GetCode("Common.Error");
    public static int Common_Warning                     => LocalizationManager.GetCode("Common.Warning");
    public static int Common_InfoDeleteComplete          => LocalizationManager.GetCode("Common.InfoDeleteComplete");
    public static int Common_SuccessSaveComplete         => LocalizationManager.GetCode("Common.SuccessSaveComplete");
    public static int Common_SuccessExportedToPath       => LocalizationManager.GetCode("Common.SuccessExportedToPath");
    public static int Common_ImportSuccess               => LocalizationManager.GetCode("Common.ImportSuccess");
    public static int Common_ErrorAuthFailed             => LocalizationManager.GetCode("Common.ErrorAuthFailed");
    public static int Common_ErrorBrowserOpenFailed      => LocalizationManager.GetCode("Common.ErrorBrowserOpenFailed");
    public static int Common_ErrorDecryptionFailed       => LocalizationManager.GetCode("Common.ErrorDecryptionFailed");
    public static int Common_ErrorFileDataNotFound       => LocalizationManager.GetCode("Common.ErrorFileDataNotFound");
    public static int Common_ErrorPartialLoadTitle       => LocalizationManager.GetCode("Common.ErrorPartialLoadTitle");
    public static int Common_GeneralError                => LocalizationManager.GetCode("Common.GeneralError");
    public static int Common_ErrorIncorrectPassword      => LocalizationManager.GetCode("Common.ErrorIncorrectPassword");
    public static int Common_ErrorLockBlockedRunningTask => LocalizationManager.GetCode("Common.ErrorLockBlockedRunningTask");
    public static int Common_ErrorWriteAccessDenied      => LocalizationManager.GetCode("Common.ErrorWriteAccessDenied");
    public static int Common_WarningVersionMismatch      => LocalizationManager.GetCode("Common.WarningVersionMismatch");
    public static int Common_DropZonePrompt              => LocalizationManager.GetCode("Common.DropZonePrompt");

    // ── Shell (0x02) ──
    public static int Shell_LockNow                      => LocalizationManager.GetCode("Shell.LockNow");

    // ── Secrets (0x04) ──
    public static int Secrets_WarningTitleRequired       => LocalizationManager.GetCode("Secrets.WarningTitleRequired");

    // ── Gallery (0x06) ──
    public static int Gallery_InfoAlreadyRegistered      => LocalizationManager.GetCode("Gallery.InfoAlreadyRegistered");
    public static int Gallery_InfoUnsupportedSkipped     => LocalizationManager.GetCode("Gallery.InfoUnsupportedSkipped");
    public static int Gallery_SuccessImportedCount       => LocalizationManager.GetCode("Gallery.SuccessImportedCount");
    public static int Gallery_SuccessFileUndeleted       => LocalizationManager.GetCode("Gallery.SuccessFileUndeleted");
    public static int Gallery_InfoAutoUndeletedDuplicate => LocalizationManager.GetCode("Gallery.InfoAutoUndeletedDuplicate");

    // ── TimeMachine (0x05) ──
    public static int TimeMachine_SuccessRestoreComplete => LocalizationManager.GetCode("TimeMachine.SuccessRestoreComplete");
    public static int TimeMachine_SuccessItemRestored    => LocalizationManager.GetCode("TimeMachine.SuccessItemRestored");

    // ── AppSettings (0x0D) ──
    public static int AppSettings_Language                        => LocalizationManager.GetCode("AppSettings.Language");
    public static int AppSettings_Dialog_ImportLanguageSuccess    => LocalizationManager.GetCode("AppSettings.Dialog.ImportLanguageSuccess");
    public static int AppSettings_Dialog_ImportPatchedCountReport => LocalizationManager.GetCode("AppSettings.Dialog.ImportPatchedCountReport");
    public static int AppSettings_Dialog_RestartRequiredAlert     => LocalizationManager.GetCode("AppSettings.Dialog.RestartRequiredAlert");

    // ── VaultSettings (0x0E) ──
    public static int VaultSettings_VaultManagement                      => LocalizationManager.GetCode("VaultSettings.VaultManagement");
    public static int VaultSettings_ErrorImportExceptionReport           => LocalizationManager.GetCode("VaultSettings.ErrorImportExceptionReport");
    public static int VaultSettings_SuccessBackupSavedToPath             => LocalizationManager.GetCode("VaultSettings.SuccessBackupSavedToPath");
    public static int VaultSettings_SuccessImportDetailsReport           => LocalizationManager.GetCode("VaultSettings.SuccessImportDetailsReport");
    public static int VaultSettings_SuccessImportedCountReport           => LocalizationManager.GetCode("VaultSettings.SuccessImportedCountReport");
    public static int VaultSettings_WarningAutoBackupContext             => LocalizationManager.GetCode("VaultSettings.WarningAutoBackupContext");
    public static int VaultSettings_WarningLastExitBackupFailedAlert     => LocalizationManager.GetCode("VaultSettings.WarningLastExitBackupFailedAlert");
    public static int VaultSettings_Dialog_EmergencyAccessSetup          => LocalizationManager.GetCode("VaultSettings.Dialog.EmergencyAccessSetup");
    public static int VaultSettings_Dialog_EmergencyCodeRemainsValid     => LocalizationManager.GetCode("VaultSettings.Dialog.EmergencyCodeRemainsValid");
    public static int VaultSettings_Dialog_MasterPasswordChangedSuccessfully => LocalizationManager.GetCode("VaultSettings.Dialog.MasterPasswordChangedSuccessfully");
    public static int VaultSettings_Dialog_OldQrRevokedNotice            => LocalizationManager.GetCode("VaultSettings.Dialog.OldQrRevokedNotice");
    public static int VaultSettings_Dialog_PasswordResetViaHelloSuccessfully => LocalizationManager.GetCode("VaultSettings.Dialog.PasswordResetViaHelloSuccessfully");
    public static int VaultSettings_Dialog_QrCodeSavedToPath             => LocalizationManager.GetCode("VaultSettings.Dialog.QrCodeSavedToPath");
}
