// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using FC = NaimitsuVault.Common.FileTypeCode;

namespace NaimitsuVault.ViewModels;

/// <summary>One attached file shown in the draft-compare dialog's attachment thumbnail row.
/// Mirrors this dialog's existing avatar-bytes precedent (plain byte[]/string, not the
/// SecureCharBuffer/pinned-array discipline used for password/notes fields) - a thumbnail image
/// isn't PII text, and the filename is a short, always-visible caption rather than a masked field.
/// Uses mutable properties rather than a positional record: WinUI's XamlTypeInfo.g.cs generator
/// assumes settable properties for every public type in a bindable namespace and fails to compile
/// against init-only accessors, even though this type is never actually used with x:Bind.</summary>
public sealed class CompareFileThumbnail
{
    public int    FileId          { get; set; }
    public string FileName        { get; set; } = string.Empty;
    public int    ContentType     { get; set; }
    public byte[]? ThumbnailBytes { get; set; }

    /// <summary>ContentType/FileName-extension mismatch detected on load (suspected tampering or corruption).</summary>
    public bool IsQuarantined { get; set; }

    // Fallback glyph for file types that can't produce a visual thumbnail - matches
    // GalleryViewModel.FileItem.FileTypeGlyph exactly, so the same file type renders with the same
    // icon everywhere in the app (Secrets/Profile attached-file list, Gallery, and this dialog).
    // Built from codepoints rather than "\uXXXX" string literals, since these Private Use Area
    // characters have no visible glyph in most editors/diff viewers.
    public string FileTypeGlyph => char.ConvertFromUtf32(
        FC.IsCertOrKeyCategory(ContentType) ? 0xEB95 :
        ContentType is >= 4000 and <= 5999 ? 0xF000 :
        FC.IsArchive(ContentType)          ? 0xE8B3 :
        0xE160);
}
