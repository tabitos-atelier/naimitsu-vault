// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Services;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression coverage (TC-FAV-01..03) for the 2026-09-17 fix: FaviconService.ComputeCacheKey
/// only guarded the upper length bound (domain.Length > 512), so an empty or whitespace-only
/// domain (length 0 or 1+) passed through and produced a valid HMAC cache key, which would have
/// let PrefetchAsync build and request a meaningless "https://icons.duckduckgo.com/ip3/.ico" URL.
/// </summary>
public sealed class FaviconServiceTests
{
    private static DekScope MakeDek()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return new DekScope(key, 32);
    }

    private static FaviconService MakeService(TestDb db)
        => new(db.Factory, new CryptoService(NullLogger<CryptoService>.Instance));

    // ── TC-FAV-01 ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCachedAsync_EmptyDomain_ReturnsNull()
    {
        using var db = TestDb.Create();
        var favicon = MakeService(db);
        var result = await favicon.GetCachedAsync("", MakeDek());
        Assert.Null(result);
    }

    // ── TC-FAV-02 ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCachedAsync_WhitespaceOnlyDomain_ReturnsNull()
    {
        using var db = TestDb.Create();
        var favicon = MakeService(db);
        var result = await favicon.GetCachedAsync("   ", MakeDek());
        Assert.Null(result);
    }

    // ── TC-FAV-03 ────────────────────────────────────────────────────────

    [Fact]
    public async Task PrefetchAsync_EmptyDomain_NeverTouchesDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = TestDb.Create();
        var favicon = MakeService(db);
        favicon.IsEnabled = true;

        // ComputeCacheKey returning null must short-circuit before any DB read/write or HTTP call.
        await favicon.PrefetchAsync("", MakeDek());

        await using var ctx = await db.Factory.CreateDbContextAsync(ct);
        Assert.Empty(await ctx.FaviconCache.ToListAsync(ct));
    }
}
