// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NaimitsuVault.Common;
using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// "PKCS#12 metadata parsing" TC-CRT-01–03.
/// The Viewer only shows certificate metadata, so <see cref="CertificateHelper.TryParse"/> must load a
/// PFX with <see cref="X509KeyStorageFlags.EphemeralKeySet"/>: with the default flags Windows writes the
/// private key to the user key store on disk while the certificate is loaded.
/// </summary>
public sealed class CertificateHelperTests
{
    private const string Subject = "keystore-probe";

    private static byte[] BuildPfx(string? password)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={Subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return password == null ? cert.Export(X509ContentType.Pfx) : cert.Export(X509ContentType.Pfx, password);
    }

    // TC-CRT-01: a passwordless PFX still yields its metadata under EphemeralKeySet.
    [Fact]
    public void TryParse_PasswordlessPfx_ReturnsMetadata()
    {
        var info = CertificateHelper.TryParse(BuildPfx(password: null), FileTypeCode.Pkcs12);

        Assert.NotNull(info);
        Assert.Equal(Subject, info.Subject);
        Assert.InRange(info.DaysUntilExpiry, 29, 30);
    }

    // TC-CRT-02: parsing a PFX creates no file in the user key store (%APPDATA%\Microsoft\Crypto).
    // The key file would exist only inside TryParse (removed on Dispose), so a FileSystemWatcher records
    // any creation instead of diffing the directory afterwards.
    [Fact]
    public async Task TryParse_PasswordlessPfx_WritesNoPrivateKeyFileToUserKeyStore()
    {
        var cryptoRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto");
        Directory.CreateDirectory(cryptoRoot);
        var pfx = BuildPfx(password: null);

        var created = new ConcurrentBag<string>();
        using var watcher = new FileSystemWatcher(cryptoRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.FileName,
            EnableRaisingEvents   = true,
        };
        watcher.Created += (_, e) => created.Add(e.FullPath);

        var info = CertificateHelper.TryParse(pfx, FileTypeCode.Pkcs12);
        await Task.Delay(500, TestContext.Current.CancellationToken); // let queued watcher events drain

        Assert.NotNull(info);
        Assert.Empty(created);
    }

    // TC-CRT-03: a password-protected PFX cannot be opened (no password dialog in the Viewer) and yields null.
    [Fact]
    public void TryParse_PasswordProtectedPfx_ReturnsNull()
    {
        Assert.Null(CertificateHelper.TryParse(BuildPfx(password: "secret"), FileTypeCode.Pkcs12));
    }
}
