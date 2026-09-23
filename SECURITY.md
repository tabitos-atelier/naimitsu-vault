# Security Policy — Naimitsu Vault

<p align="center">
  <strong>English</strong> | <a href="SECURITY-ja.md">日本語</a>
</p>

This document defines the security policy for Naimitsu Vault.
It covers the vulnerability reporting process, the threats this software is designed to defend against, and the threats explicitly out of scope.

---

## Reporting a Vulnerability

### Report privately

**Do not post discovered vulnerabilities to public GitHub Issues, Discussions, or Pull Requests.**
A public post gives attackers a chance to learn about the problem before it's fixed.

### Reporting channel

Please use GitHub's **Private Vulnerability Reporting** feature.

1. Go to [https://github.com/tabitos-atelier/naimitsu-vault/security/advisories/new](https://github.com/tabitos-atelier/naimitsu-vault/security/advisories/new)
2. Submit a private report via "Report a vulnerability"

If the URL above is unavailable, contact the following email address directly.

**Contact**: tabitos.chalice@gmail.com

Please use the subject line `[SECURITY] Naimitsu Vault — <summary of the issue>`.

### What to include in your report

* Vulnerability type (e.g. cryptographic misimplementation, memory residue, authentication bypass)
* Affected versions/components
* Steps to reproduce (steps, code, configuration, etc.)
* Potential impact for an attacker (secret disclosure, privilege escalation, etc.)
* A proposed fix or workaround, if available

### Response process

| Phase | Target timeline |
| --- | --- |
| Acknowledgment | Within 7 days of the report |
| Initial assessment (severity, reproduction) | Within 14 days of receipt |
| Fix release (for High/Critical severity) | Best effort target: within 90 days of assessment |
| Public disclosure | After the fix release, by agreement with the reporter |

As this is an individually developed project, the above is best-effort. The same SLA as a commercial product cannot be provided.

---

## Security Boundary (Threats This Software Defends Against)

The following defines the scope of threats that Naimitsu Vault is **designed to defend against**.

### 1. Encryption of locally persisted data and zero disk residue

**Threat defended against**: Physical theft/copying of the database file (`.nkdb`), and analysis of temporary-file residue

* All secret data is encrypted with **AES-256-GCM + Argon2id** and stored in SQLite.
* The data encryption key (DEK) is wrapped by a key-encrypting key (KEK) derived via **Argon2id** (64 MB memory, 4 lanes, 3 iterations), making brute-force attacks with GPUs/ASICs infeasible within a realistic timeframe.
* Even if the database is stolen, no data that could identify the user or infer their behavior can be read in plaintext (zero-knowledge design).
* **Fully in-memory rendering (zero disk residue)**: When viewing an attached PDF or configuration file, **no plaintext temporary file (cache) is ever written to disk**. Decrypted data exists only in memory and is completely erased and released the moment viewing ends or the vault locks.
* **Screen capture for TOTP QR import**: The feature that reads an authenticator app's QR code via screen capture processes the captured image entirely in memory without writing it to disk, and wipes the decoded secret with `CryptographicOperations.ZeroMemory` immediately after use.
* **Device binding for Windows Hello integration**: When Windows Hello is enabled, the DEK and K_shared are further encrypted with Windows DPAPI (`ProtectedData`, `DataProtectionScope.CurrentUser`) before being stored. Decryption is only possible on the same PC under the same Windows account, so extracting the database file alone to a different environment cannot unlock it via Windows Hello.

### 2. Reject-at-the-boundary for all input data (fail-safe)

**Threat defended against**: Runtime corruption caused by importing malformed files or data

* Import processing runs encoding validation, structural validation, and integrity validation on every byte sequence.
* Malformed data is rejected wholesale with an error message; the app does not crash, and the database is never corrupted (fail-safe design).
* Restoring an SQLite binary file only proceeds after passing 3 layers of validation (magic header, schema, `PRAGMA integrity_check`).

### 3. Hygiene of volatile memory space

**Threat defended against**: Exposure of the DEK/password through memory scanning

* The DEK is zero-cleared with `CryptographicOperations.ZeroMemory` on lock.
* Intermediate byte sequences for passwords and encryption keys are zero-cleared immediately after use.
* **Pinned Object Heap (POH) prevents GC-compaction ghosts**: Buffers holding key material or decrypted plaintext are allocated on the Pinned Object Heap via `GC.AllocateArray(pinned: true)`, preventing residue (a "GC ghost") from being left at the buffer's pre-move address during GC compaction, and are zero-cleared with `CryptographicOperations.ZeroMemory` immediately after use.
* AutoLock defaults to 5 minutes, automatically wiping the DEK after a period of inactivity.
* Clipboard writes are excluded from OS history and cloud sync (via Win32 P/Invoke).
* **30-second auto-erase of clipboard contents**: Secret text copied to the clipboard is **automatically wiped in the background after 30 seconds** (via `CancellationTokenSource`-based timing). This avoids creating plaintext `string` instances where possible, minimizing the risk of residue in memory.
* **Clipboard-free keyboard auto-type (Direct Injection)**: An auto-type feature sends secret text directly as keystrokes without ever placing it on the clipboard. The input structure used for sending is allocated on the stack and zero-cleared immediately after each send, and the destination window (foreground HWND) is verified immediately before sending to prevent misdirected input to the wrong window.
* **Screen capture protection (OS-level capture blocking)**: Uses the Windows API (`SetWindowDisplayAffinity`) to automatically prevent the window from being captured by screenshots (PrintScreen, Snipping Tool) and screen-sharing software (Discord, Teams, etc.). Enabled by default; can be toggled in Settings.

### 4. Outbound network communication boundary (site icon auto-fetch)

**Threat defended against**: Leaking the user's IP address and access history to a registered website

* Even with "Site icon auto-fetch" enabled, the app never communicates directly with the registered website. Icons are fetched only through DuckDuckGo's icon cache API (`icons.duckduckgo.com`).
* The cache key is an HMAC-SHA256 hash (derived from the DEK) rather than the raw domain name, so no plaintext domain name is ever stored in the local database either.
* Domains that API hasn't indexed cannot have their icon fetched. This fails silently and the domain is simply treated as not cached — there is no fallback that contacts the site directly.
* Disabling this feature in Settings brings this network path down to zero traffic.

---

## Outside the Security Boundary (Out-of-Scope Threats)

The following threats are **outside this software's designed scope of protection**.
Defending against them requires OS- or hardware-level countermeasures.

### 1. Compromise of the host OS itself

* **Kernel/root privilege escalation**: An attacker with administrator privileges can read process memory. This software's memory protections assume the OS correctly enforces process isolation.
* **Keyloggers**: Malware that records keystrokes while you type your master password is outside this software's protection.
* **Advanced display capture & physical recording**: Direct display-driver hooks with administrator privileges, kernel-level screen scraping, and physical recording with an external camera are outside this software's protection.
* **Direct process memory dumps**: Memory dumps taken by a process with administrator privileges are out of scope.

### 2. Physical seizure of the device

* If the device is seized while powered on and logged in, the vault may remain accessible for as long as the session stays active.
* If AutoLock (5 minutes) has triggered, or the vault has been locked manually, the DEK has already been wiped. Encryption functions as protection against seizure in that state.
* **Forced closure of all viewer windows on lock**: The instant an automatic or manual lock (`Ctrl + L`) is triggered, in lockstep with session teardown, **every currently open decrypted viewer window is force-closed immediately**. This structurally prevents visual data leakage when you step away or the device is seized. Open in-page `ContentDialog`s (e.g. the draft comparison views for secrets and the profile) are also actively closed at the same time, with their internal plaintext buffers wiped (bounded by an 800ms timeout so a stuck dialog can never stall the lock operation itself).

### 3. Leakage through the user's own carelessness

* Disclosing the master password to others, or mismanaging where it's recorded.
* Mismanaging exported plaintext files (CSV/JSON), leading to leakage.
* Losing or leaking the emergency access code (QR + PIN).

### 4. Supply chain attacks

* Vulnerabilities in the .NET runtime, NuGet packages, or Windows itself, which this software depends on. Dependencies are kept to a minimum, but complete elimination is impossible.

### 5. Internal native buffers of UI controls (WinUI 3)

* Standard UI controls such as `PasswordBox` and `TextBox` keep their own native (WinRT) internal buffers for display, undo history, and IME composition, and no managed API exists to reach or zero that buffer.
* For the master password field, the managed-side copies (the `string` returned by the `.Password` getter, and the `char[]` used for authentication) are reliably zero-cleared immediately after use, but this software cannot touch the native buffer that `PasswordBox` itself retains. Since the unlock window is re-created on every attempt, this residual copy accumulates across generations of unlock attempts.
* This is a structural limitation of the WinUI 3 framework, and no mitigation is currently available on this software's side.

---

## Supported Versions

| Version | Security support |
| --- | --- |
| Latest release | ✅ Supported |
| One release behind | Case by case (judged by severity) |
| Older | ❌ Not supported (please update to the latest version) |

---

## Honest Disclosure of This Software's Limitations

* **No audit performed**: A comprehensive third-party security audit has not been conducted. Argon2id and AES-256-GCM are industry-standard algorithms, but the correctness of this implementation depends on the author's knowledge and automated tests.
* **Individual development**: Compared to a commercial security product, response resources and specialized expertise are limited.
* **No warranty**: This software is provided under the terms of the MIT License, with no warranty of any kind (`THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND`).

**Do not use this software to manage confidential information whose loss would be unacceptable.**

---

## Acknowledgments

For reports made in accordance with the principles of Coordinated Vulnerability Disclosure, the reporter's name (or handle) will be credited in the public Security Advisory published after the fix, if desired.

---

*This policy may be updated without notice. Please check the repository for the latest version.*
