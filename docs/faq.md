# Frequently Asked Questions (FAQ)

<p align="center">
  <a href="faq-ja.md">日本語</a> | <strong>English</strong>
</p>

## Q. "Windows protected your PC" appears after downloading.

As a new open-source release, Windows SmartScreen may show a warning until reputation is built. Click **"More info"** and select **"Run anyway"** to launch.

## Q. "Smart App Control blocked an app that may be unsafe" appears and the app does not start.

Naimitsu Vault is not code-signed, so Smart App Control cannot confirm its publisher and may block `NaimitsuVault.exe` until Windows has evaluated the new file. This happens with both the installer version and the portable (ZIP) version (the installer itself finishes, and the installed app is blocked), and it can happen again with each new release, because each release is a new file to Windows. Unlike the SmartScreen warning above, this dialog has no "Run anyway" option. PCs with Smart App Control turned off are not affected.

**Wait a while and launch the app again.** In our test the app started about an hour after the first attempt, without changing any setting. How long it takes is decided by Windows and is not guaranteed.

If the app still does not start, you can turn Smart App Control off under **Windows Security → App & browser control → Smart App Control settings**. On current Windows 11 you can turn it back on afterwards, but on older versions without recent updates it may not be possible to re-enable it once disabled. Smart App Control protects the whole PC, so decide for yourself whether to turn it off.

## Q. The app does not start, or "The app's core data is corrupted" appears on first launch.

If this happens on the very first launch right after installing or extracting the ZIP, your data is not corrupted. A Windows security feature is preventing the app from loading some of its files or from writing to its data folder. **Do not restore with the wrench button.** Close the app and check the following.

1. **Smart App Control is on**
   Naimitsu Vault is not code-signed, so for a newly published version, some of the app's files may be blocked from loading until Microsoft's cloud reputation service has evaluated them. Wait a while and launch the app again. In one case the app started working after installing Windows updates and restarting the PC. You can see which file was blocked under **Windows Security → Protection history**.
   If the app still does not start, you can turn Smart App Control off as described in the previous question.
2. **Smart App Control is on, and the portable ZIP version does not start from a shortcut**
   If the app starts when you open `NaimitsuVault.exe` directly but not from a shortcut you created, the files extracted from the downloaded ZIP still carry the "downloaded from the internet" mark. Right-click `NaimitsuVault.exe` → **Properties** → on the **General** tab, check **Unblock** at the bottom and select **OK**. The shortcut will then work. If you have not extracted the ZIP yet, checking **Unblock** in the ZIP file's properties before extracting avoids the problem altogether. The installer version is not affected.
3. **Controlled folder access is on, and you extracted the portable ZIP into a protected folder such as Documents or Pictures**
   The app cannot write to its `data\` folder. Either move the app to a folder that is not protected, or add `NaimitsuVault.exe` under **Windows Security → Virus & threat protection → Manage ransomware protection → Allow an app through Controlled folder access → Add an allowed app**.

## Q. How do I update the portable (ZIP) version?

Extract the new ZIP and **overwrite** your existing folder directly. The distribution ZIP does not contain a `data\` folder, so overwriting will never delete your encrypted databases.

**Do not launch the app from the extracted folder before you copy it over.** Launching it even once creates an empty `data\` folder there, and the copy then replaces the registration of your existing data.

- **If you updated to v1.0.2 or later**: the registration is restored automatically on the next launch and the usual unlock screen appears. You do not need to do anything.
- **If you updated to v1.0.1 or earlier**: the app shows the first-time setup screen on the next launch. Your vault data is still there. **Do not go through the setup (doing so deletes your existing vaults).** Close the app and recover as follows.

Work in **the folder you have been using all along (the one you copied over)**, not in the folder you extracted from the download.

1. In the folder you have been using, copy the whole `data` folder to another location as a safety copy.
2. In the folder you have been using, move `data\NaimitsuVault.nkdb` out of the `data` folder (do not delete it). Leave the other files in the `data` folder untouched.
3. Launch the app from the folder you have been using. The registration is restored automatically and the usual unlock screen appears.

Once you have confirmed that everything is back, you can delete the safety copy from step 1 and the file you moved in step 2. The folder you extracted from the download is no longer needed either.

## Q. There is a folder named `orphaned_<date>_<time>` inside the `data` folder.

It holds files that were not registered as a vault, which the app moved there instead of deleting them (v1.0.2 or later). It is created, for example, when the registration is replaced as described under "How do I update the portable (ZIP) version?" above. The app never deletes this folder by itself.

Once you have confirmed that all your vaults are present and working, you can delete the whole folder. If a vault is missing, keep the folder.

## Q. Folders inside the folder I chose as the backup folder have disappeared (v1.0.3 or earlier).

In v1.0.0 to v1.0.3, the step that cleans up old backups after a backup **also deleted the other folders directly inside the backup folder, together with their contents**. This is fixed in v1.0.4. **Update to v1.0.4 or later.**

- **When it happens**: when a manual backup ("Backup") succeeds, or when the automatic backup runs at exit
- **What is deleted**: folders directly inside the folder you chose as the backup folder, with their contents. Backup folders named `Naimitsu_<date>_<time>` and files directly inside the backup folder are not affected
- **When you are not affected**: when you chose an empty folder used only for backups

Deleted folders do not go to the Recycle Bin. If you had another backup, for example in OneDrive or Windows File History, you may be able to restore them from there.

If you keep using v1.0.3 or earlier, choose an empty folder used only for backups as the backup folder.

## Q. Why does a full-width space show up as `\uXXXX` in exported JSON?

This is expected, not a bug. .NET's JSON encoder always escapes certain whitespace characters (including the full-width space) this way regardless of settings, while other CJK characters (kanji, hiragana, katakana) are exported as literal, readable text. The exported JSON is fully valid and re-imports correctly either way.
