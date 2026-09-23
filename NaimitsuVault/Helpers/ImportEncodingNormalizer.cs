// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Encoding normalization logic for plaintext import files. Run once at the import entry point so the
/// CSV/JSON parsers only ever see valid UTF-8. UTF-16 with a BOM is converted rather than rejected (some
/// text editors save files that way, and the conversion is lossless); ANSI is rejected because its
/// content is already corrupted by the time it is read.
/// Called from VaultOperationsViewModel. Exposed as internal for testing.
/// </summary>
internal static class ImportEncodingNormalizer
{
    // Determines the file byte sequence's type and returns safe UTF-8 binary.
    // UTF-8 BOM    → strip via a zero-copy slice and continue
    // UTF-32 LE/BE → the first 2 bytes match a UTF-16 BOM, so this is checked before the UTF-16 check and fully rejected
    // UTF-16 LE/BE → direct byte-to-byte conversion via Encoding.Convert (never promotes an intermediate string to the heap)
    // ANSI         → Utf8.IsValid() is false → throws DecoderFallbackException and is fully rejected
    // The return value is guaranteed to always be a valid UTF-8 byte sequence.
    internal static ReadOnlyMemory<byte> NormalizeToUtf8(byte[] rawBytes)
    {
        var span = rawBytes.AsSpan();

        // UTF-8 BOM (0xEF 0xBB 0xBF): zero-copy slice
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            return rawBytes.AsMemory(3);

        // UTF-32 LE BOM (0xFF 0xFE 0x00 0x00): the first 2 bytes match a UTF-16 LE BOM, so this is
        // checked before the UTF-16 check to prevent mis-conversion (mojibake) and explicitly reject it as an unsupported format.
        if (span.Length >= 4 && span[0] == 0xFF && span[1] == 0xFE && span[2] == 0x00 && span[3] == 0x00)
            throw new DecoderFallbackException("UTF-32 encoded files are not supported.");

        // UTF-32 BE BOM (0x00 0x00 0xFE 0xFF): rejected for the same reason as above.
        if (span.Length >= 4 && span[0] == 0x00 && span[1] == 0x00 && span[2] == 0xFE && span[3] == 0xFF)
            throw new DecoderFallbackException("UTF-32 encoded files are not supported.");

        // UTF-16 LE BOM (0xFF 0xFE): direct byte-to-byte conversion via Encoding.Convert (no intermediate string created)
        if (span.Length >= 2 && span[0] == 0xFF && span[1] == 0xFE)
            return Encoding.Convert(Encoding.Unicode, Encoding.UTF8, rawBytes, 2, rawBytes.Length - 2);

        // UTF-16 BE BOM (0xFE 0xFF): same as above
        if (span.Length >= 2 && span[0] == 0xFE && span[1] == 0xFF)
            return Encoding.Convert(Encoding.BigEndianUnicode, Encoding.UTF8, rawBytes, 2, rawBytes.Length - 2);

        // No BOM: validate as UTF-8 (fully rejects ANSI upfront)
        if (!System.Text.Unicode.Utf8.IsValid(span))
            throw new DecoderFallbackException("The file is not valid UTF-8 (blocks ANSI contamination upfront).");

        return rawBytes;
    }
}
