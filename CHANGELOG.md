# Changelog

Changes that users of Naimitsu Vault can notice, newest version first.

Each version has a `## v<version>` heading that matches its release tag. The release workflow copies
the text under that heading into the GitHub release notes, and fails if the heading is missing.

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
