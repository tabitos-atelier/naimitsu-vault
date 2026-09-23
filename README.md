<p align="center">
  <img src="./NaimitsuVault/Assets/NaimitsuVault.png" width="128" alt="Naimitsu Vault">
</p>

<h1 align="center">Naimitsu Vault</h1>

<p align="center">
  <strong>English</strong> | <a href="README-ja.md">日本語</a>
</p>

<p align="center">
  <em>Naimitsu (内密) — Japanese for "confidential."</em>
</p>

<p align="center">
  Passwords, images, certificates — every secret, in one vault.<br>
  A completely free, fully local confidential information manager for Windows (WinUI 3)
</p>

> **Breaking the spell of the password manager — a golem born to guard your secrets.**

---

<p align="center">
  <strong>Is your vault only built to hold strings?</strong>
</p>

![Demo: drag-and-drop a file, launch the viewer](.github/assets/naimitsu-vault-demo-file-link.gif)

---

- ID photos, driver's licenses, passport scans, certificates, license keys — don't you want every one of these "file-based secrets" in the same vault as your passwords?
- Wouldn't you rather be free of the annoying "Save changes?" prompt, and compare your current edits against the existing data side by side?
- Restore an old password, only to find the latest data disappeared right along with it — have you ever mourned a loss like that?
- Are you forcing your work secrets and personal assets to cohabit in one vault, artificially split by category?
- Are you stuck with fixed input fields, unable to freely add items or drag-and-drop to reorder them?
- Can you prove what you touched a month ago with a tamper-proof record — not just your memory?

---

**The answer that frees you from every one of these constraints is right here.**

A shared pool that breaks beyond mere text to freely link files and images — one file can be referenced from many secrets.
Instant autosave & draft comparison that completely banishes the annoying "Save changes?" prompt.
And a zero-overwrite 3-generation Time Machine where restoring an old version never causes data loss.

Fully offline by design — it works just fine with no internet connection at all. You can also carry it around on a USB drive. The app itself never sends your data to the cloud.

---

## Features

<video src="https://github.com/user-attachments/assets/b8bf31f7-e5d4-44cc-ba15-84b0422e5f5b" autoplay loop muted playsinline width="100%"></video>

- **Encrypted file storage & viewer** — Images, PDFs, certificates, SSH/GPG keys, config files, and archives, encrypted with AES-256-GCM and viewable in-app
- **Image gallery (shared pool)** — One file can be referenced from many secrets, with HMAC-SHA256 deduplication
- **Dialog-free instant autosave** — Edits are staged as drafts automatically, with a side-by-side diff view against the current data
- **Time Machine (3-generation rotation)** — Restoring any generation never overwrites and loses the latest data
- **Vault separation & emergency access code** — Up to 3 fully independent vaults, plus a PIN-protected QR code for read-only emergency access
- **Tamper-proof audit log** — Every operation is encrypted and logged automatically (retained for 180 days)
- **2FA (TOTP), Windows Hello, keyboard auto-type, clipboard protection, auto backup, CSV/JSON export/import**, and more

See **[Features](docs/features.md)** for the complete list and known limitations.

---

## Distribution Formats

Choose whichever form fits your use case. Both forms share the same encryption boundary model and session security.

| Form               | Database (.nkdb) location                    | Use case                                                                                               |
| ------------------ | -------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| **Installer**      | `%LOCALAPPDATA%\TabitosWorks\NaimitsuVault\` | The standard form for stable use as a regular desktop app on a fixed home or office PC.                |
| **ZIP (portable)** | `data\` subfolder next to the executable     | Carry the entire app on a USB stick and use it safely anywhere, without touching the host environment. |

---

## Installation

**Download the latest release from [Releases](https://github.com/tabitos-atelier/naimitsu-vault/releases)**

- `NaimitsuVault-Setup-x64.exe` — Installer (recommended)
- `NaimitsuVault-x64.zip` — ZIP (portable)

**Requirements**

- Windows 11 x64 (Windows 10 is not a verified target)

> Naimitsu Vault is designed for native Windows operation in a fully local environment. As such, there are no plans to port or develop versions for other operating systems (macOS / Linux) or mobile platforms (iOS / Android).

If Windows SmartScreen blocks the first launch, or you want to update the portable version, see the **[FAQ](docs/faq.md)**.

---

## Documentation

| Document                                                             | Contents                                                           |
| -------------------------------------------------------------------- | ------------------------------------------------------------------ |
| [Features](docs/features.md)                                         | Complete feature list, limitations, and where data is stored       |
| [FAQ](docs/faq.md)                                                   | SmartScreen warning, updating the portable version, and more       |
| [Export / Import Format Specification](docs/import-export-format.md) | CSV/JSON data format for migrating from other tools                |
| [Languages - Custom Locales](docs/locale-guide.md)                   | Adding a language or restyling the UI wording with a locale file   |
| [SECURITY.md](SECURITY.md)                                           | Encryption specification, threat boundary, vulnerability reporting |

---

## Security and Encryption

Naimitsu Vault is a fully local vault app with no external transmission capability whatsoever (except for outbound favicon requests if you explicitly enable "Site icon auto-fetch" in Settings). From data persistence to memory management, the following security design is applied:

- **Data persistence & encryption**: All secret data and attached files are encrypted with **AES-256-GCM + Argon2id**. SQLite's TEXT type is avoided entirely; data is stored as **raw binary BLOBs** to prevent memory contamination from immutable strings.
- **Key derivation function**: **Argon2id** is used to derive the encryption key from your password, offering strong resistance against large-scale brute-force attacks using GPUs/ASICs.
- **Clipboard protection**: Copied secret text is excluded from Windows' built-in Clipboard History (Win+V) and cross-device sync. It is also **deterministically wiped by a background timer after 30 seconds**.
- **Zero disk residue**: Viewing an encrypted PDF or image never writes a plaintext temporary file (cache) to disk. Decrypted data exists only in memory and is fully erased the moment the viewer closes.

> ⚠️ **Disclaimer**
> This software is a personal project and has not undergone an independent security audit by a third-party organization. While reasonable design choices have been made, no guarantee of complete safety is provided. **Do not use this to manage critical data whose loss would be unacceptable.** Use is entirely at your own risk.

For the full encryption specification, this app's threat boundary, and how to report a vulnerability, please read the security policy: [SECURITY.md](SECURITY.md).

---

## ⚠️Important notes on backup and restore

- **Windows Hello (biometrics/PIN) does not carry over when you restore**
  Windows Hello credentials are protected by DPAPI, an encryption method tied to your specific PC (Windows account). Whether you're moving to a different PC or **restoring from a backup on the same PC**, Windows Hello is automatically disabled for safety. The first unlock after a restore always requires your master password (you can set Windows Hello back up from Settings once unlocked).
- **Always keep your master password stored somewhere safe**
  Naimitsu Vault is designed with complete zero-knowledge — no backdoor, not even for the developer. If you forget your master password, recovering your data is fundamentally impossible, even with the backup file in hand. Even if you normally unlock with Windows Hello alone, keep your master password stored somewhere safe.

---

## License and Legal

This software is released under the [MIT License](LICENSE). Forking, modifying, redistributing, and commercial use are all free (the only exception is how the name may be used — see [LEGAL.md](LEGAL.md)).

Also, **no prior notice or permission from the author is required** to feature or review this software on YouTube, a blog, social media, or any other web media. Use it freely.

Naimitsu Vault is an independent personal project by Tabito's Works. It was not developed as work for any company or organization the author belongs to or has belonged to, is not sponsored or endorsed by any of them, and does not represent their views. **No organization, including the author's current and past employers, may present this software or its development externally as its own achievement without the author's prior written permission.**

For the full disclaimer, governing law and jurisdiction, the rules for media coverage, the name and logo policy, and the statement of non-affiliation, see [LEGAL.md](LEGAL.md).

---

## Afterword

Naimitsu Vault was born as an app of me, by me, for me.

"I want a truly local vault that moves beyond the password manager, bringing files, images, and history together in complete safety."
If this becomes a trusted companion for someone out there who's sighed at feature-bloated tools and sought that same freedom, nothing would make me happier.

The story of how this golem was unearthed from the digital dunes and tempered into form — along with the author's profile and chronicles — is recorded in the parchment scrolls:

📜 [Tabito's Voyage — Profile (Ja)](https://tabitos-voyage.com/about)

---

<p align="center">
  <strong>Naimitsu keeps your database. Let's vault in!!!</strong>
</p>

<p align="center">
  Tabito's Works — <a href="https://github.com/tabitos-atelier">@tabitos-atelier</a>
</p>
