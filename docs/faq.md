# Frequently Asked Questions (FAQ)

<p align="center">
  <a href="faq-ja.md">日本語</a> | <strong>English</strong>
</p>

## Q. "Windows protected your PC" appears after downloading.

As a new open-source release, Windows SmartScreen may show a warning until reputation is built. Click **"More info"** and select **"Run anyway"** to launch.

## Q. The app does not start, or "The app's core data is corrupted" appears on first launch.

If this happens on the very first launch right after installing or extracting the ZIP, your data is not corrupted. A Windows security feature is preventing the app from loading some of its files or from writing to its data folder. **Do not restore with the wrench button.** Close the app and check the following.

1. **Smart App Control is on**
   Naimitsu Vault is not code-signed, so for a newly published version, some of the app's files may be blocked from loading until Microsoft's cloud reputation service has evaluated them. Wait a while and launch the app again. In one case the app started working after installing Windows updates and restarting the PC. You can see which file was blocked under **Windows Security → Protection history**.
   If the app still does not start, you can turn Smart App Control off under **Windows Security → App & browser control → Smart App Control settings**. On current Windows 11 you can turn it back on afterwards, but on older versions without recent updates it may not be possible to re-enable it once disabled.
2. **Smart App Control is on, and the portable ZIP version does not start from a shortcut**
   If the app starts when you open `NaimitsuVault.exe` directly but not from a shortcut you created, the files extracted from the downloaded ZIP still carry the "downloaded from the internet" mark. Right-click `NaimitsuVault.exe` → **Properties** → on the **General** tab, check **Unblock** at the bottom and select **OK**. The shortcut will then work. If you have not extracted the ZIP yet, checking **Unblock** in the ZIP file's properties before extracting avoids the problem altogether. The installer version is not affected.
3. **Controlled folder access is on, and you extracted the portable ZIP into a protected folder such as Documents or Pictures**
   The app cannot write to its `data\` folder. Either move the app to a folder that is not protected, or add `NaimitsuVault.exe` under **Windows Security → Virus & threat protection → Manage ransomware protection → Allow an app through Controlled folder access → Add an allowed app**.

## Q. How do I update the portable (ZIP) version?

Extract the new ZIP and **overwrite** your existing folder directly. The distribution ZIP does not contain a `data\` folder, so overwriting will never delete your encrypted databases.

## Q. Why does a full-width space show up as `\uXXXX` in exported JSON?

This is expected, not a bug. .NET's JSON encoder always escapes certain whitespace characters (including the full-width space) this way regardless of settings, while other CJK characters (kanji, hiragana, katakana) are exported as literal, readable text. The exported JSON is fully valid and re-imports correctly either way.
