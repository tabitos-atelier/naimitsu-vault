# Frequently Asked Questions (FAQ)

<p align="center">
  <a href="faq-ja.md">日本語</a> | <strong>English</strong>
</p>

## Q. "Windows protected your PC" appears after downloading.

As a new open-source release, Windows SmartScreen may show a warning until reputation is built. Click **"More info"** and select **"Run anyway"** to launch.

## Q. How do I update the portable (ZIP) version?

Extract the new ZIP and **overwrite** your existing folder directly. The distribution ZIP does not contain a `data\` folder, so overwriting will never delete your encrypted databases.

## Q. Why does a full-width space show up as `\uXXXX` in exported JSON?

This is expected, not a bug. .NET's JSON encoder always escapes certain whitespace characters (including the full-width space) this way regardless of settings, while other CJK characters (kanji, hiragana, katakana) are exported as literal, readable text. The exported JSON is fully valid and re-imports correctly either way.
