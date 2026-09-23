# Export / Import Format Specification

<p align="center">
  <a href="import-export-format-ja.md">日本語</a> | <strong>English</strong>
</p>

This document specifies the data formats handled by Naimitsu Vault's unencrypted export/import feature.
Refer to this spec if you want to build a migration converter from another password manager.

> **Note:** Profile data (My Number, passport number, driver's license number, etc.) is excluded from unencrypted export for security reasons.

---

## Common notes

* Encoding (export): **UTF-8** (CSV with a BOM, JSON without one)
* Encoding (import): the export output rule above is **not** a requirement for input. Both CSV and JSON accept **UTF-8 with or without a BOM**, and also **UTF-16 LE/BE with a BOM** (converted to UTF-8 automatically). Anything else is unsupported: UTF-32 and files that are not valid UTF-8 (Shift_JIS and other ANSI code pages) are rejected with an error before any record is imported, and UTF-16 is only recognized when the BOM is present. A converter should emit UTF-8
* Timestamp formats differ between JSON and CSV. **JSON** uses ISO 8601 with a UTC offset (local time, e.g. `2026-01-01T09:00:00+09:00`; on input, a value without an offset is treated as local time). **CSV** uses `yyyy-MM-dd HH:mm:ss` (`yyyy-MM-dd` for the expiry date only), as local time with no time-zone information
* A `null` field is treated as an empty string or the field's own default value
* **Export** of `totpSecret` (and `totpDigits`/`totpPeriod`/`totpAlgorithm`) only happens when the "Include 2FA secrets" toggle on the settings screen is **ON** (default OFF). This toggle is **export-only** — **import** has no corresponding toggle, and a `totpSecret` present in the file is always imported

---

## JSON format (recommended)

Use the JSON format when migrating with TOTP secrets included.

### Structure

```json
[
  {
    "title":        "GitHub",
    "category":     1,
    "userId":       "tabito",
    "password":     "KJdWe%oLu7C$vlhjMBa#",
    "website":      "https://tabitos-voyage.com",
    "email":        "tabito@example.com",
    "notes":        "Memo",
    "customFields": [
      { "Label": "Note", "Value": "Test", "FieldType": 0 }
    ],
    "createdAt":    "2026-01-01T09:00:00+09:00",
    "updatedAt":    "2026-03-15T12:34:56+09:00",
    "isFavorite":   true,
    "expiresAt":    "2027-01-01T00:00:00+09:00",
    "totpSecret":     "JBSWY3DPEHPK3PXP",
    "totpDigits":     8,
    "totpPeriod":     30,
    "totpAlgorithm":  "SHA256"
  }
]
```

### Field definitions

| Field | Type | Description |
| --- | --- | --- |
| `title` | string | Title. Leading and trailing whitespace is trimmed on import |
| `category` | number / null | Category code (see "Category codes" below). Left unset if `null` or omitted |
| `userId` | string / null | Login ID (username) |
| `password` | string / null | Password |
| `website` | string / null | Website URL |
| `email` | string / null | Email address |
| `notes` | string / null | Notes. Line endings are normalized to LF on import |
| `customFields` | array / null | Array of custom fields (structure in the next section) |
| `createdAt` | ISO 8601 | Creation time. The import's execution time is used if omitted |
| `updatedAt` | ISO 8601 | Last-updated time. The import's execution time is used if omitted |
| `isFavorite` | boolean | Favorite flag (`false` if omitted) |
| `expiresAt` | ISO 8601 / null | Expiry date. No expiry if `null` or omitted |
| `totpSecret`     | string  | Bare Base32 secret (see "`totpSecret` format" below). The key is only emitted when the toggle is ON; omitted entirely when OFF |
| `totpDigits`     | number  | Code length (6-8). Defaults to `6` if omitted. Omitted from output when it equals the default |
| `totpPeriod`     | number  | Refresh period in seconds (RFC 6238 default `30`). Defaults to `30` if omitted. Omitted from output when it equals the default |
| `totpAlgorithm`  | string  | Hash algorithm. `"SHA1"` (default) / `"SHA256"` / `"SHA512"`. Defaults to `"SHA1"` if omitted. Omitted from output when it equals the default |

A type marked `/ null` means the field is written as `null` on export when it has no value, and `null` or omission is accepted on import.

### `totpSecret` format

* The value is the **bare Base32 secret** (RFC 4648 alphabet: `A`–`Z` and `2`–`7`). It is **not** an `otpauth://` URI. If the source password manager exports the secret as a URI, the converter must extract the `secret` parameter itself and map `digits`, `period` and `algorithm` onto `totpDigits`, `totpPeriod` and `totpAlgorithm`.
* Import does **not** normalize or validate this string; it is stored exactly as given. Code generation later tolerates lowercase letters, whitespace and `=` padding, but any other character makes the app unable to generate a code for that record (the record itself is still imported). To be safe, a converter should emit the secret in uppercase, with no whitespace and no `=` padding.

### `customFields` structure

```json
[
  {
    "FieldId":   1,
    "Label":     "Field name",
    "Value":     "Value",
    "FieldType": 0
  }
]
```

`FieldType` is an integer (not a string). `0` = text (default), `1` = password, `2` = URL, `3` = date.

> Key names are PascalCase by convention. Import is case-insensitive.
> If `FieldId` is `0`, non-numeric, or omitted, it is auto-numbered **immediately during import** (continuing from the current highest `FieldId`). Numbering is not deferred until the record is first edited in the app.

---

## CSV format

> **Note:** The CSV format does not support `totpSecret`. Use the **JSON format** to migrate TOTP entries.

### Header row

```
Title,Category,UserId,Password,Website,Email,Notes,CustomFields,CreatedAt,UpdatedAt,IsFavorite,ExpiresAt
```

### Column definitions

| Column | Type | Notes |
| --- | --- | --- |
| Title | string | Whitespace is trimmed. An empty title is still imported (the record just has an empty title). A row whose columns are all empty is ignored (see below) |
| Category | integer | Empty or non-numeric leaves it unset (`null`). A code not in the current presets becomes `0` (Uncategorized) |
| UserId | string | Left unset if empty |
| Password | string | Left unset if empty |
| Website | string | Left unset if empty |
| Email | string | Left unset if empty |
| Notes | string | Left unset if empty |
| CustomFields | JSON string | Left unset if empty |
| CreatedAt | `yyyy-MM-dd HH:mm:ss` | Local time. The import's execution time is used if empty or the format doesn't match |
| UpdatedAt | `yyyy-MM-dd HH:mm:ss` | Local time. The import's execution time is used if empty or the format doesn't match |
| IsFavorite | boolean (`true` / `false`) | Case-insensitive. Empty or any other value is `false` |
| ExpiresAt | `yyyy-MM-dd` | Local date. No expiry if empty or the format doesn't match |

If a row has fewer columns than expected, the missing columns are treated as empty. A row in which every column is empty or whitespace-only (a blank line, or a commas-only line such as `,,,,`) is ignored as a blank row.

### Example

The same record as the JSON example above (12 columns). `CustomFields` holds a JSON array as a string, so the whole cell is wrapped in double quotes and every `"` inside it is doubled to `""`. `FieldId` can be omitted (it is auto-numbered on import).

```csv
Title,Category,UserId,Password,Website,Email,Notes,CustomFields,CreatedAt,UpdatedAt,IsFavorite,ExpiresAt
GitHub,1,tabito,KJdWe%oLu7C$vlhjMBa#,https://tabitos-voyage.com,tabito@example.com,Memo,"[{""Label"":""Note"",""Value"":""Test"",""FieldType"":0}]",2026-01-01 09:00:00,2026-03-15 12:34:56,true,2027-01-01
```

### Escaping rules

* A field containing `,`, `"`, or a line break is wrapped in double quotes
* A `"` inside a field is escaped as `""`
* **Formula-injection guard (export only):** if the value of `Title`, `UserId`, `Password`, `Website`, `Email`, `Notes`, or `CustomFields` starts with `=`, `+`, `-`, or `@`, the field is wrapped in double quotes and a single apostrophe (`'`) is inserted right after the opening quote, to stop spreadsheet apps like Excel from evaluating it as a formula (e.g. `=SUM(A1)` → `"'=SUM(A1)"`). **This changes the value itself.** Import performs no corresponding strip of this apostrophe, so round-tripping export→import repeatedly leaves the apostrophe in the value permanently. The JSON format has no such guard (prefer JSON for migrations that include `totpSecret` anyway)

---

## Category codes

Categories have no dedicated DB table — the locale JSON's (`locale-ja.json`/`locale-en.json`) `Categories.NN` keys (**two-digit sequential codes**) are the sole source of truth. The set of valid codes is not fixed: a custom locale pack can add codes and rename existing ones (see [the locale README](locale/README.md)). Below is the default (Japanese locale) set.

| Code | Meaning              |
| ---- | -------------------- |
| `1`  | Login                |
| `10` | IT / Infrastructure  |
| `20` | Licenses             |
| `30` | Projects             |
| `40` | Education / Self-development |
| `50` | Finance / Cards      |
| `60` | Identification       |
| `70` | Contracts / Documents |
| `80` | Membership / Life    |
| `90` | Secret notes         |
| `99` | Other                |
| `0`  | Uncategorized (cannot be deleted, the system catch-all) |

A code that doesn't exist in the destination's current category presets (plus `0`) is automatically remapped to `0` (Uncategorized).

> **Note (pre-2026-08-19 legacy spec):** This document previously listed a **4-digit integer** code scheme (e.g. `1100` = Login, `9000` = Uncategorized). That scheme was replaced by the two-digit sequential scheme above in the 2026-08-19 category preset overhaul. Importing JSON generated with the old 4-digit codes will map every record to "Uncategorized," since none of those codes match the current preset set. A migration converter must always source its codes from the current locale JSON's `Categories.NN` keys (or the values shown in the app's settings screen).

---

## Import-time behavior

| Condition | Behavior |
| --- | --- |
| Title duplicates an existing one (case-insensitive) | Renamed by appending ` (Import)`; further duplicates get ` (Import2)`, ` (Import3)`, … (see the note below) |
| Title is empty, `null`, omitted, or whitespace-only | Not skipped — imported as a record with an empty title, so no data is lost. Duplicate empty titles are renamed like any other (from the second one on: ` (Import)` with a leading space) |
| CSV row with every column empty (blank line, commas-only line, whitespace-only line) | Ignored as a blank row (not counted as a record) |
| Category code doesn't exist in the current presets (when a value is given) | Remapped to `0` (Uncategorized). If the category is omitted entirely, it stays `null` |
| `createdAt` / `updatedAt` omitted              | The import's execution time is used instead     |
| `totpSecret` present                           | Always imported regardless of format or any toggle — there is no import-side equivalent of the "include 2FA" toggle |
| `totpDigits`/`totpPeriod`/`totpAlgorithm` omitted | Defaults to `6`/`30`/`"SHA1"` respectively (RFC 6238 defaults) |
| `customFields` is neither an array nor an object | Stored as null                                  |

> The word appended for duplicates is the value of `Common.Import` in the UI language selected at import time (Japanese: `インポート`, English: `Import`; a custom locale may differ). From the second duplicate on, the number follows the word directly with no space (`(Import2)`).
