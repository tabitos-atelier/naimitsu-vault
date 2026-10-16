# Changelog

Changes that users of Naimitsu Vault can notice, newest version first.

Each version has a `## v<version>` heading that matches its release tag. The release workflow copies
the text under that heading into the GitHub release notes, and fails if the heading is missing.

## v1.0.3

> **Known issue: Smart App Control may block the app for a while after a new release.** The app is not code-signed, so on PCs with Smart App Control turned on, `NaimitsuVault.exe` can be blocked until Windows has evaluated the new file. In our test the app started about an hour after the first attempt. This affects both the installer version and the portable version. PCs with Smart App Control turned off are not affected. See the FAQ for what you can do.

### Fixed

- **A generated password was sometimes shown without the gap after every four characters.** This happened when the generated password contained only one kind of symbol and that symbol occurred two or more times, because the enlarged preview treats such a value as a license key or a passphrase. The password generator now generates another password in that case.

### Improved

- The feature list describes the gaps in the enlarged view, including the values that are shown without gaps.

## v1.0.2

> **Known issue: Smart App Control may block the app for a while after a new release.** The app is not code-signed, so on PCs with Smart App Control turned on, `NaimitsuVault.exe` can be blocked until Windows has evaluated the new file. In our test the app started about an hour after the first attempt. This affects both the installer version and the portable version. PCs with Smart App Control turned off are not affected. See the FAQ for what you can do.

### Fixed

- **Existing vaults could be deleted after an update of the portable version.** If the newly extracted folder had been launched once before it was copied over the existing folder, the app's list of vaults was replaced by an empty one and the first-time setup screen appeared. Completing that setup made the app delete the existing vaults and their internal backup copies, without asking, when it was closed and unlocked again. The app now detects this state at startup and restores the list of vaults automatically, so the usual unlock screen appears.
- **The app no longer deletes vault files it does not recognize.** Files in the `data` folder that the app cannot match to a registered vault are moved to a `data\orphaned_<date>_<time>` folder instead, and the dashboard shows a notice when that happens.
- **A custom language file with malformed curly braces could break a message.** For example, after a restore from a backup had finished, the app could show an error and stay on the restore screen. On import, the app now replaces a text with the built-in one when it contains a curly brace that is not one of the placeholders (`{0}`, `{1}`, …) of the built-in text. This includes an unclosed `{`, an added placeholder, and forms such as `{0:N0}` or `{0,10}`. Language files that are already imported are not checked again; import the file again to apply the check.
- **A copy or the automatic clearing of the clipboard could silently do nothing.** This could happen when another program, such as the Windows clipboard history, was using the clipboard at that moment. A value copied from the app could then stay in the clipboard after the 30 seconds. The app now retries for up to about 0.1 seconds.

### Improved

- The FAQ describes how to update the portable version safely and how to recover on v1.0.1 or earlier.
- The FAQ describes what to do when Smart App Control blocks the app.

### Changed

- The language file version is now 2, because a message was added. A custom language file made for an earlier version can still be imported: the app shows a version notice and uses the built-in text for the new message.

## v1.0.1

> **Known issue: Smart App Control may block the app for a while after a new release.** The app is not code-signed, so on PCs with Smart App Control turned on, `NaimitsuVault.exe` can be blocked until Windows has evaluated the new file. In our test the app started about an hour after the first attempt. This affects both the installer version and the portable version. PCs with Smart App Control turned off are not affected. See the FAQ for what you can do.

### Fixed

- **The bundled libraries were blocked on PCs with Smart App Control turned on, and the app reported "The app's core data is corrupted".** The build process had invalidated the vendor signatures of the bundled libraries. The libraries now ship with their original signatures intact. As a side effect the downloads are smaller (installer: 83.2 MB → 70.1 MB, ZIP: 117 MB → 100 MB).
- **Dates in file names, stored values, and CSV files depended on the Windows regional settings.** On systems whose default calendar is not Gregorian (for example Arabic, Thai, or Persian), the backup folder created by a restore could be treated as the oldest one and removed, and CSV date columns and date-type custom fields could not be read on a PC with different regional settings. These dates are now written in a fixed format.
- The notification bar showed the same text twice when the title and the message were identical.
- The shortcut list was missing `Ctrl+D` (keyboard auto-type) and, on the Profile page, `Ctrl+E` and `Ctrl+S`.
- When the About page fails to open a link in the browser, the message now shows the URL.

### Improved

- **The enlarged password view is split into groups of four characters**, which makes a password easier to read aloud or type by hand. Only the enlarged view changes: the stored value and the copied value are untouched. Values that already contain their own separators are left as they are.
- **Enter now confirms** in the identity confirmation dialog, the emergency access PIN fields, and the master password change fields, when the matching button is enabled.
- When the app fails to start, the log now records the error type and code, which makes the cause easier to identify.
- The FAQ explains what to do when the app does not start on first launch, and when the portable ZIP version does not start from a shortcut.

## v1.0.0

Initial release of Naimitsu Vault, a fully offline, zero-knowledge secret manager for Windows 11.

- **Encrypted file vault with in-memory viewing**: PDFs, images, certificates, and configuration files are stored encrypted with AES-256-GCM and Argon2id, and are displayed from memory without writing plaintext to disk.
- **Image gallery**: images are shared between vault items and deduplicated by content hash.
- **Automatic drafts and side-by-side comparison**: unsaved edits are kept as a draft and can be compared with the saved values.
- **Three-generation Time Machine**: each item keeps three earlier versions, so an accidental overwrite can be undone.
- **Built-in 2FA (TOTP) authenticator**: QR codes can be imported by screen capture, and the codes of the adjacent time windows are shown for clocks that are out of sync.
- **Windows Hello and screen capture protection**: unlock with biometrics or a PIN, and keep the app's windows out of screenshots and recordings.
- **No network use by default**: the app works offline. The optional favicon lookup is turned off unless you enable it.
