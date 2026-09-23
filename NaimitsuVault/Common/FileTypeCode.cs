// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;

namespace NaimitsuVault.Common;

/// <summary>
/// Integer identifier code constants for StoredFile.ContentTypeCode.
/// MIME scheme by range:
///   1000-1999 … image files (image/*)
///   2000-2099 … document files (application/pdf, etc.)
///   3000-3999 … certificate/key files
///   4000-4999 … structured text (JSON/XML/Markdown/SQL/license)
///   5000-5999 … config files (YAML/TOML/INI/env)
///   6000-6999 … archives (ZIP/7z/RAR/TAR/...)
///   9999      … unknown binary (application/octet-stream)
/// </summary>
internal static class FileTypeCode
{
    // ── 1000s: images ────────────────────────────────────────────────────
    internal const int ImageJpeg = 1000;
    internal const int ImagePng  = 1001;
    internal const int ImageGif  = 1002;
    internal const int ImageWebp = 1003;
    internal const int ImageBmp  = 1004;
    internal const int ImageTiff = 1005;

    // ── 2000s: documents ─────────────────────────────────────────────────
    internal const int Pdf = 2000;

    // ── 3000s: certificates/keys ─────────────────────────────────────────
    internal const int Pkcs12        = 3000;
    internal const int PemFile       = 3001;
    internal const int X509Cert      = 3002;
    internal const int SshPublicKey  = 3003;
    internal const int SshPrivateKey = 3004;
    internal const int PgpKeys       = 3005;
    internal const int PgpEncrypted  = 3006;
    internal const int OpenVpn       = 3007;

    // ── 4000s: structured text ───────────────────────────────────────────
    internal const int PlainText = 4000;
    internal const int Json      = 4001;
    internal const int Xml       = 4002;
    internal const int Markdown  = 4003;
    internal const int Sql       = 4004;
    internal const int License   = 4005;

    // ── 5000s: config files ──────────────────────────────────────────────
    internal const int GenericConfig = 5000;
    internal const int Yaml          = 5001;
    internal const int Toml          = 5002;
    internal const int Ini           = 5003;

    // ── 6000s: archives ───────────────────────────────────────────────────
    internal const int Zip      = 6000;
    internal const int SevenZip = 6001;
    internal const int Rar      = 6002;
    internal const int Lzh      = 6003;
    internal const int Tar      = 6004;
    internal const int Gzip     = 6005;
    internal const int Tgz      = 6006;
    internal const int Bzip2    = 6007;
    internal const int Xz       = 6008;
    internal const int Cab      = 6009;

    // ── 9999: unknown binary ─────────────────────────────────────────────
    internal const int OctetStream = 9999;

    // ── Range classification helpers ─────────────────────────────────────
    internal static bool IsImage(int code)             => code is >= 1000 and <= 1999;
    internal static bool IsPdf(int code)               => code == Pdf;
    internal static bool IsCertOrKeyCategory(int code) => code is >= 3000 and <= 3999;
    internal static bool IsConfigType(int code)        => code is >= 5000 and <= 5999;
    internal static bool IsArchive(int code)           => code is >= 6000 and <= 6999;

    /// <summary>Type that should be attempted as X.509 / PEM parsing for certificate files.</summary>
    internal static bool IsX509ParseCandidate(int code) => code is Pkcs12 or PemFile or X509Cert;

    /// <summary>Type that can be displayed as text.</summary>
    internal static bool IsTextType(int code) =>
        code is PemFile or X509Cert or SshPublicKey or SshPrivateKey
              or PgpKeys or OpenVpn or Json or License or Xml
              or GenericConfig or Yaml or Toml or Ini or Markdown or Sql or PlainText;

    /// <summary>Thumbnail generation target (image or PDF).</summary>
    internal static bool CanCreateThumbnail(int code) => IsImage(code) || IsPdf(code);

    private static bool EqualsIgnoreCase(ReadOnlySpan<char> s, string lit) =>
        MemoryExtensions.Equals(s, lit, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a file extension (with leading dot, e.g. <c>".jpg"</c>) to a code. Zero allocation.
    /// Returns 0 for an unknown extension (distinct from <see cref="OctetStream"/>).
    /// </summary>
    internal static int FromExtension(ReadOnlySpan<char> dotExt)
    {
        if (EqualsIgnoreCase(dotExt, ".jpg")  || EqualsIgnoreCase(dotExt, ".jpeg"))                                     return ImageJpeg;
        if (EqualsIgnoreCase(dotExt, ".png"))                                                                           return ImagePng;
        if (EqualsIgnoreCase(dotExt, ".gif"))                                                                           return ImageGif;
        if (EqualsIgnoreCase(dotExt, ".webp"))                                                                          return ImageWebp;
        if (EqualsIgnoreCase(dotExt, ".bmp"))                                                                           return ImageBmp;
        if (EqualsIgnoreCase(dotExt, ".tiff") || EqualsIgnoreCase(dotExt, ".tif"))                                     return ImageTiff;
        if (EqualsIgnoreCase(dotExt, ".pdf"))                                                                           return Pdf;
        if (EqualsIgnoreCase(dotExt, ".pfx")  || EqualsIgnoreCase(dotExt, ".p12"))                                     return Pkcs12;
        // .key here (not SshPrivateKey) covers generic PEM-encoded private keys with an extension.
        // SshPrivateKey is reserved for the extension-less OpenSSH naming convention (id_rsa etc.),
        // detected separately in FromFileName.
        if (EqualsIgnoreCase(dotExt, ".pem")  || EqualsIgnoreCase(dotExt, ".key") || EqualsIgnoreCase(dotExt, ".csr") || EqualsIgnoreCase(dotExt, ".p8")) return PemFile;
        if (EqualsIgnoreCase(dotExt, ".cer")  || EqualsIgnoreCase(dotExt, ".crt"))                                     return X509Cert;
        if (EqualsIgnoreCase(dotExt, ".pub"))                                                                           return SshPublicKey;
        if (EqualsIgnoreCase(dotExt, ".asc")  || EqualsIgnoreCase(dotExt, ".pgp"))                                     return PgpKeys;
        if (EqualsIgnoreCase(dotExt, ".gpg"))                                                                           return PgpEncrypted;
        if (EqualsIgnoreCase(dotExt, ".ovpn"))                                                                          return OpenVpn;
        if (EqualsIgnoreCase(dotExt, ".txt"))                                                                           return PlainText;
        if (EqualsIgnoreCase(dotExt, ".json") || EqualsIgnoreCase(dotExt, ".har"))                                     return Json;
        if (EqualsIgnoreCase(dotExt, ".xml"))                                                                           return Xml;
        if (EqualsIgnoreCase(dotExt, ".md"))                                                                            return Markdown;
        if (EqualsIgnoreCase(dotExt, ".sql"))                                                                           return Sql;
        if (EqualsIgnoreCase(dotExt, ".lic")  || EqualsIgnoreCase(dotExt, ".license"))                                 return License;
        if (EqualsIgnoreCase(dotExt, ".env")  || EqualsIgnoreCase(dotExt, ".conf"))                                    return GenericConfig;
        if (EqualsIgnoreCase(dotExt, ".yaml") || EqualsIgnoreCase(dotExt, ".yml"))                                     return Yaml;
        if (EqualsIgnoreCase(dotExt, ".toml"))                                                                          return Toml;
        if (EqualsIgnoreCase(dotExt, ".ini"))                                                                           return Ini;
        if (EqualsIgnoreCase(dotExt, ".zip"))                                                                           return Zip;
        if (EqualsIgnoreCase(dotExt, ".7z"))                                                                            return SevenZip;
        if (EqualsIgnoreCase(dotExt, ".rar"))                                                                           return Rar;
        if (EqualsIgnoreCase(dotExt, ".lzh"))                                                                           return Lzh;
        if (EqualsIgnoreCase(dotExt, ".tar"))                                                                           return Tar;
        if (EqualsIgnoreCase(dotExt, ".gz"))                                                                            return Gzip;
        if (EqualsIgnoreCase(dotExt, ".tgz"))                                                                           return Tgz;
        if (EqualsIgnoreCase(dotExt, ".bz2"))                                                                           return Bzip2;
        if (EqualsIgnoreCase(dotExt, ".xz"))                                                                            return Xz;
        if (EqualsIgnoreCase(dotExt, ".cab"))                                                                           return Cab;
        return 0;
    }

    /// <summary>
    /// Converts a file name (including one without an extension) to a code. Zero allocation.
    /// Additionally detects SSH private key naming conventions such as <c>id_rsa</c>.
    /// </summary>
    internal static int FromFileName(ReadOnlySpan<char> filename)
    {
        var ext = Path.GetExtension(filename);
        if (!ext.IsEmpty) return FromExtension(ext);
        var name = Path.GetFileName(filename);
        if (MemoryExtensions.StartsWith(name, "id_", StringComparison.OrdinalIgnoreCase))
            return SshPrivateKey;
        return 0;
    }
}
