// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NaimitsuVault.Common;
using NaimitsuVault.Services;

namespace NaimitsuVault.Helpers;

public sealed record CertInfo(
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Thumbprint,
    string KeyUsage,
    int DaysUntilExpiry);

public sealed record ServiceAccountInfo(
    string Type,
    string ProjectId,
    string ClientEmail,
    string ClientId);

public static class CertificateHelper
{
    public static CertInfo? TryParse(byte[] data, int contentType)
    {
        X509Certificate2? cert = null;
        try
        {
            if (contentType == FileTypeCode.PemFile)
            {
                // POH-pinned char[] (same rationale as PinnedBufferWriter below): a managed System.String
                // is immutable and can't be reliably zeroed - a GC compaction that moves it before this
                // method's finally block runs leaves an unzeroed ghost copy at the old address, which no
                // C# API can reach afterward. Decoding straight into a pinned array avoids ever creating
                // that ghost-prone string in the first place.
                var pemChars = GC.AllocateArray<char>(Encoding.UTF8.GetCharCount(data), pinned: true);
                try
                {
                    Encoding.UTF8.GetChars(data, pemChars);
                    ReadOnlySpan<char> pemSpan = pemChars;
                    cert = pemSpan.IndexOf("-----BEGIN CERTIFICATE-----") >= 0
                        ? X509Certificate2.CreateFromPem(pemSpan)
                        : null;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pemChars.AsSpan()));
                }
            }
            else
            {
                // EphemeralKeySet: only the certificate metadata is displayed, never the private key. With the
                // default key storage flags Windows writes the PFX's private key to the user key store on disk
                // (%APPDATA%\Microsoft\Crypto\Keys) while the certificate is loaded, which would leave it behind
                // if the process dies before Dispose. Ephemeral keeps the key in memory only.
                cert = contentType switch
                {
                    FileTypeCode.Pkcs12   => X509CertificateLoader.LoadPkcs12(data, password: null,
                                                 keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet),
                    FileTypeCode.X509Cert => X509CertificateLoader.LoadCertificate(data),
                    _                     => null
                };
            }
            if (cert == null) return null;

            // Math.Floor (not a plain (int) cast, which truncates toward zero) so a certificate expired
            // by e.g. 0.1-0.9 days rounds to -1, not 0 - otherwise it would read as "expires today" and
            // under-report the alert severity instead of "already expired".
            var days = (int)Math.Floor((cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
            return new CertInfo(
                cert.GetNameInfo(X509NameType.SimpleName, false),
                cert.GetNameInfo(X509NameType.SimpleName, true),
                cert.NotBefore,
                cert.NotAfter,
                cert.Thumbprint,
                FormatKeyUsage(cert),
                days);
        }
        catch
        {
            return null;
        }
        finally
        {
            cert?.Dispose();
        }
    }

    public static ServiceAccountInfo? TryParseServiceAccount(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return null;
            var type = typeEl.GetString() ?? string.Empty;
            if (type is not ("service_account" or "authorized_user" or "external_account"))
                return null;

            root.TryGetProperty("project_id",   out var projectEl);
            root.TryGetProperty("client_email", out var emailEl);
            root.TryGetProperty("client_id",    out var clientIdEl);

            return new ServiceAccountInfo(
                type,
                projectEl.ValueKind  == JsonValueKind.String ? projectEl.GetString()!  : string.Empty,
                emailEl.ValueKind    == JsonValueKind.String ? emailEl.GetString()!     : string.Empty,
                clientIdEl.ValueKind == JsonValueKind.String ? clientIdEl.GetString()!  : string.Empty);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pretty-prints JSON and transcribes it directly into <paramref name="buf"/>.
    /// Zero intermediate string generation. The formatted byte sequence is immediately erased with ZeroMemory.
    /// </summary>
    internal static void PrettyPrintJsonToBuffer(byte[] data, SecureCharBuffer buf)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            using var pinnedBuf = new PinnedBufferWriter();
            using (var writer = new Utf8JsonWriter(pinnedBuf, new JsonWriterOptions
            {
                Indented = true,
                Encoder  = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                doc.WriteTo(writer);
            }
            FieldCrypto.FillBufferFromUtf8(buf, pinnedBuf.WrittenSpan);
        }
        catch
        {
            FieldCrypto.FillBufferFromUtf8(buf, data.AsSpan());
        }
    }

    private static string FormatKeyUsage(X509Certificate2 cert)
    {
        var ext = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (ext == null) return string.Empty;
        var parts = new List<string>();
        if (ext.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)) parts.Add("Digital Signature");
        if (ext.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment))  parts.Add("Key Encipherment");
        if (ext.KeyUsages.HasFlag(X509KeyUsageFlags.DataEncipherment))  parts.Add("Data Encipherment");
        if (ext.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))       parts.Add("Cert Sign");
        if (ext.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign))           parts.Add("CRL Sign");
        return string.Join(", ", parts);
    }
}
