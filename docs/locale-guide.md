# Languages — Custom Locale Packs

<p align="center">
  <a href="locale-guide-ja.md">日本語</a> | <strong>English</strong>
</p>

This folder contains sample custom locales (languages / themed worldviews) you can use with Naimitsu Vault.

Custom locales serve two broad purposes:

1. **Multi-language support** — Add a language beyond the built-in English and Japanese (French, Chinese, Spanish, etc.). Translate every string in the locale file (JSON) into the target language and import it, and the app's UI runs entirely in that language.
2. **Themed worldview** — Keep the language the same but freely restyle the wording (dialect, industry jargon, a game-style UI, etc.) — the sample files below are examples of this.

Both uses share the exact same file format and import steps. Everything below applies to both.

---

## Sample Files

| File                           | Description                                                                                                                   |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------ |
| `locale-cyber-desert-ja.json`  | Cyber Desert Theme (Japanese) — a cyberfantasy worldview where a password becomes a "Secret Incantation" and permanent deletion becomes "returning to the void." |
| `locale-cyber-desert.json`  | Cyber Desert Theme (English) — Recasts passwords as "Secret Incantations" and attached files as "Exhibits" kept in a "Museum," for global nomads.   |

> Both are examples of a "themed worldview." A finished multi-language sample (a French pack, for example) isn't bundled yet, but the steps to make one are below.

> Feel free to create, fork, publish, and redistribute your own samples or derivative works within the terms of the license. No notice or sharing with the author is required.

<video src="https://github.com/user-attachments/assets/7769adc6-c58a-4f48-8e2a-f364b7b43bbf" autoplay loop muted playsinline width="100%"></video>

---

## How to Use

1. Save the `.json` file you want to use.
2. Naimitsu Vault → Settings → Language → **"Import"**.
3. It takes effect the next time you launch the app.

---

## Making Your Own Locale

### 1. Export a template

Settings → Language → "Export" exports the JSON for your current language (Japanese or English).

### 2. Edit the JSON

> ⚠️ **Important: encoding**
> Always save the file as **UTF-8** when editing. Files saved in any other encoding cannot be imported.

```json
{
  "locale": "en",
  "displayName": "Your Theme Name",
  "localeVersion": 1,
  "messages": {
    "Common": {
      "#01-01: Common Actions": {
        "Ok": "The button text you want to change",
        "Cancel": "The button text you want to change"
      }
    },
    "Unlock": {
      "#02: Unlock": {
        "Main": "The button text you want to change"
      }
    },
    "TimeMachine": {
      "#07: Time Machine": {
        "Dialog": {
          "PurgeConfirmText": "The dialog text you want to change"
        }
      }
    }
  }
}
```

The example above is trimmed for readability. The exported file contains every key, in this same nested form.

| Field           | Description                                                                                                                                                                |
| --------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `locale`        | Base language code (`"ja"` or `"en"`). Even when creating an entirely new language (French, etc.), pick whichever built-in is the closer template (sentence structure, formality, etc.) — this base language is what's used to auto-fill missing keys and check `{0}` placeholder counts. |
| `displayName`   | The name shown in the settings screen (a theme name, or the language name itself).                                                                                        |
| `localeVersion` | Keep the value exactly as exported.                                                                                                                                        |
| `messages`      | Key/text pairs (only change what you need — anything missing is auto-filled).                                                                                             |

### Key naming convention (reference only)

Keys are organized by the nesting of JSON objects, not by dot-separated strings. Each text sits at the bottom of the following hierarchy.

```
messages                          ← root
└─ Domain                         ← object: a functional area (Common / Unlock / Shell / Secrets / Totp, 17 in total)
   └─ "#NN-NN: Section name"      ← object: a section group (heading for human scanning)
      ├─ Identifier               ← string: the text itself (Ok / Cancel / GeneralError, etc.)
      └─ Subdomain                ← object: a presentation form (Dialog: modal / Navi: navigation) — optional
         └─ Identifier            ← string: the text itself
```

- **Domain** — a top-level key directly under `messages`. (The app's internal `System` domain is never exported, and is ignored if it appears in an imported file.)
- **Section group** — the key starting with `#` inside a Domain (e.g. `"#01-01: Common Actions"`). It is just a heading that groups related texts together. The app ignores the group's name, so it does **not** become part of the key and is **not a translation target**.
- **Identifier** — the key whose value is the text. **Do not rename it**; only rewrite the value.
- **Subdomain** — an optional object (`Dialog` / `Navi`) that sits inside a section group and holds the identifiers for that presentation form.

Example: `TimeMachine` → `"#07: Time Machine"` → `Dialog` → `PurgeConfirmText` = the Time Machine domain / a modal (Dialog) / the permanent-delete confirmation text (PurgeConfirmText).
Example: `Common` → `"#01-01: Common Actions"` → `Ok` = the Common domain / no subdomain / the OK button (Ok).

Keep the exported structure as it is. The app identifies a text by the combination of its Domain, optional Subdomain, and Identifier, so keeping every Identifier under its original Domain (and Subdomain) is what matters.

### Categories (`Categories` domain)

The `Categories` domain is the one exception to the naming convention above: its identifiers are **two-digit category codes** (`01`–`99`) rather than English names, and each value is the category name shown in the app. There is no separate category setting — this domain is the only place categories are defined.

```json
"Categories": {
  "#11: Categories": {
    "01": "Login",
    "10": "IT / Infrastructure",
    "15": "Games",
    "Uncategorized": "Uncategorized"
  }
}
```

The example is an excerpt; `15` is a newly added category, not part of the default set.

- **Rename a category** — change the value.
- **Add a category** — add a new key made of exactly two digits. Categories are listed in ascending code order, so the default codes are spaced apart (`01`, `10`, `20`, …) to leave room to insert your own between them (such as `15`).
- **Do not change the codes of existing categories.** Each entry stores the code, not the name (and the code is also what appears in [export/import files](import-export-format.md)). If a code is no longer defined, entries using it are shown as **Uncategorized**; their stored data is not rewritten.
- **Deleting a category from your file has no effect.** Any key missing from your file is filled in from the base language (`locale`) on import, so the default category comes back. Only add or rename.
- **`Uncategorized`** is the fixed key for code `0` (entries with no category) and the only non-numeric key in this domain. You can change its wording, but it cannot be removed or renumbered. Do not add a `00` key.
- Only keys made of exactly two digits count as categories. Keys such as `1`, `100` or `AB` are not treated as categories.

### `{0}` placeholders

Any text containing `{0}`, `{1}`, etc. has a dynamic value inserted there. **Leave these exactly as they are** (you may move them to a different position within the sentence).

If you delete a `{0}`-style placeholder or break its syntax (for example, an unclosed `{`), then to prevent broken screens **that one entry will not use your wording and falls back to the standard text of the base language** (the language set in `locale`: Japanese or English). All other entries are imported normally.

```json
"SuccessExportedToPath": "Etched into the parchment at \"{0}\"."
```

### Overly long text

To prevent text from overflowing buttons and dialogs, a text that is **120 bytes or more in UTF-8** (roughly 40 Japanese characters) **and at least twice as long as the base-language text** is also replaced with the base-language text on import. Wording of ordinary length is unaffected.

---

## Idea Examples (themed worldviews)

| Theme                  | Example                                                                    |
| ----------------------- | --------------------------------------------------------------------------- |
| A regional dialect      | "Reckon it's deleted now!" / "Saved 'er up good!"                          |
| Kid-friendly superhero  | "That spell's too short to defeat the enemy!" — learn security through play |
| Space opera style       | "That passphrase won't hold the starship's shields!"                      |
| Gacha-game style        | "Your password has been promoted to SSR!"                                   |
| Overly formal service-industry speech | "Might I confirm — you would like to proceed with the deletion?"    |

It's just JSON, so asking an AI to "rewrite this in the style of ___" is enough to produce a working draft. Making a full multi-language version (a French pack, for example) works the same way — just ask an AI to "translate this JSON into French" to get a working first draft.

---

## License

The files in this folder are published under the MIT License. Modification, redistribution, and publishing derivative works are all free.

The author has no obligation to support or fix issues (garbled text, broken layouts, etc.) in locale files that users create themselves.

---

*"The less input required, the better." — Tabito's Works*
