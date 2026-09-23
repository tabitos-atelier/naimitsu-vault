// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NaimitsuVault.Models;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Service that fetches and caches domain icons (favicons).
/// Uses the DuckDuckGo Favicon API. No network traffic occurs when IsEnabled = false.
/// The cache key is HMAC-SHA256(DEK, domain); PngData is an AES-256-GCM encrypted blob.
/// </summary>
public class FaviconService(
    IDbContextFactory<AppDbContext> factory,
    ICryptoService crypto)
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Linked to the toggle on the settings page. No fetch occurs when false.</summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Returns HMAC-SHA256(DEK, domain) as the cache primary key (hides the domain name).
    /// Since <paramref name="domain"/> comes from free-form input via Secret.Website and isn't
    /// guaranteed to be a valid DNS name (253 characters or fewer), returns null and treats it as
    /// not cacheable once it exceeds what the stackalloc buffer can safely hold (avoids propagating
    /// ArgumentException; same approach as the stackalloc-overflow fallback in ProfileService.ScanDisplayName).
    /// </summary>
    private static byte[]? ComputeCacheKey(DekScope dek, string domain)
    {
        // char[512]: ample headroom for the DNS limit of 253 ASCII characters plus CJK labels (1 char = 1 char)
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 512) return null;

        // Perform ToLowerInvariant -> UTF-8 conversion entirely on the stack (no immutable string, zero regular heap allocation)
        Span<char> lower = stackalloc char[512];
        int charLen = domain.AsSpan().ToLowerInvariant(lower);

        // byte[512*3]: CJK is 1 char -> up to 3 bytes in UTF-8. Large enough even if all 512 chars are 3-byte characters.
        Span<byte> utf8 = stackalloc byte[512 * 3];
        int byteLen = Encoding.UTF8.GetBytes(lower[..charLen], utf8);
        try
        {
            return HMACSHA256.HashData(dek.Span, utf8[..byteLen]);
        }
        finally
        {
            // Physically wipe before the stack pointer unwinds. Scope exit alone does not zero it
            // (same discipline as FieldCrypto's stack-buffer paths) - the domain is protected data
            // under the zero-knowledge design, even though only its HMAC is ever persisted.
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(lower[..charLen]));
            CryptographicOperations.ZeroMemory(utf8[..byteLen]);
        }
    }

    /// <summary>Returns the cached byte array. Null if not cached, not yet fetched, or the domain is too long.</summary>
    public async Task<byte[]?> GetCachedAsync(string domain, DekScope dek)
    {
        // Read path: dek.Span is evaluated after the await (same design as IAuditLogService.GetRecentAsync).
        // DecryptToPin internally catches CryptographicException and returns null, so it fails safe by
        // returning null even if Lock() completes during the await.
        var cacheKey = ComputeCacheKey(dek, domain);
        if (cacheKey == null) return null;
        await using var db = await factory.CreateDbContextAsync();
        var cached = await db.FaviconCache.AsNoTracking().FirstOrDefaultAsync(f => f.DomainHmac == cacheKey);
        return cached?.PngData is { } blob ? crypto.DecryptToPin(blob, dek.Span) : null;
    }

    /// <summary>
    /// Fetches from the DuckDuckGo API and stores in cache if not already cached.
    /// Does nothing when IsEnabled = false, the domain is too long, or the fetch fails (exceptions never propagate out).
    /// </summary>
    public async Task PrefetchAsync(string domain, DekScope dek)
    {
        if (!IsEnabled) return;
        var cacheKey = ComputeCacheKey(dek, domain);
        if (cacheKey == null) return;
        await using var db = await factory.CreateDbContextAsync();
        if (await db.FaviconCache.AnyAsync(c => c.DomainHmac == cacheKey)) return;
        await FetchAndCacheAsync(domain, cacheKey, dek, db);
    }

    private async Task FetchAndCacheAsync(string domain, byte[] cacheKey, DekScope dek, AppDbContext db)
    {
        byte[]? bytes = null;
        try
        {
            // Build the URL as a single allocation via String.Create (eliminates the extra copy from string interpolation)
            const string urlPrefix = "https://icons.duckduckgo.com/ip3/";
            const string urlSuffix = ".ico";
            var url = string.Create(
                urlPrefix.Length + domain.Length + urlSuffix.Length,
                domain,
                (span, d) =>
                {
                    urlPrefix.AsSpan().CopyTo(span);
                    d.AsSpan().CopyTo(span[urlPrefix.Length..]);
                    urlSuffix.AsSpan().CopyTo(span[(urlPrefix.Length + d.Length)..]);
                });

            bytes = await _http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return;

            // dek.Span is only read here, right before the crypto call (never held across the await
            // above) - but Lock() may still have run during the HTTP wait. Unlike a read path, where
            // decrypting with a zeroed key just fails safely, encrypting with one would silently
            // commit a FaviconCache row that can never be decrypted again, poisoning the cache for
            // this domain. A real DEK being all-zero is cryptographically negligible, so treat that
            // as "session locked mid-fetch" and bail out without writing anything.
            var dekSpan = dek.Span;
            if (dekSpan.IndexOfAnyExcept((byte)0) < 0) return;

            var encrypted = crypto.Encrypt(bytes, dekSpan);
            CryptographicOperations.ZeroMemory(bytes.AsSpan()); // Wipe the plaintext image immediately after encryption
            bytes = null; // Prevents a double clear in finally

            db.FaviconCache.Add(new FaviconCache
            {
                DomainHmac = cacheKey,
                PngData    = encrypted,
                FetchedAt  = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch { /* Ignore all failures such as network unavailability or timeout */ }
        finally
        {
            // Physically wipe bytes if it's still present, even on exception or early return
            if (bytes != null) CryptographicOperations.ZeroMemory(bytes.AsSpan());
        }
    }
}
