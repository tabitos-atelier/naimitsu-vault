// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Search index boundary values" - 7 cases.
///
/// TC-SIX-01 / 04 / 06 / 07 test SpanHit (internal static), the pure-logic layer, directly.
/// TC-SIX-02 / 03 / 05 drive the real ApplySearch: they build a real SecretsViewModel
/// (SecretsViewModelTests.BuildVm), load seeded secrets, set SearchText, and assert on
/// FilteredSecrets. ApplySearch's keyword preprocessing (trim, token split on half-width and
/// full-width spaces) and its AND logic are therefore exercised in production code, not
/// re-implemented in the test.
///
/// Collection: SecretsViewModel registers on WeakReferenceMessenger.Default, so these tests must not
/// run in parallel with other tests that use it.
/// </summary>
[Collection("SequentialMessenger")]
public sealed class SearchIndexTests
{
    private const string TitleGoogleAccount = "Google Account";
    private const string TitleGoogleDrive   = "Google Drive";
    private const string TitleGitHub        = "GitHub";

    /// <summary>
    /// Seeds three secrets whose keywords are spread across different fields:
    /// "Google Account" (UserId login@...), "Google Drive" (UserId other@...), "GitHub" (UserId login@...).
    /// Only the first one has both "google" (Title) and "login" (UserId).
    /// </summary>
    private static async Task<(TestDb db, SecretsViewModel vm)> BuildLoadedVmAsync()
    {
        var (db, session, vm, _, _) = SecretsViewModelTests.BuildVm();
        session.SetKey(new byte[32]);

        (string Title, string UserId)[] rows =
        [
            (TitleGoogleAccount, "login@example.com"),
            (TitleGoogleDrive,   "other@example.com"),
            (TitleGitHub,        "login@example.com"),
        ];
        await using (var ctx = db.Factory.CreateDbContext())
        {
            foreach (var (title, userId) in rows)
            {
                ctx.Secrets.Add(new Secret
                {
                    Title    = SecretsViewModelTests.EncryptUtf8(title),
                    UserId   = SecretsViewModelTests.EncryptUtf8(userId),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await vm.LoadAsync();
        return (db, vm);
    }

    /// <summary>FilteredSecrets titles in a culture-independent order, so assertions don't depend on the list sort.</summary>
    private static string[] Titles(SecretsViewModel vm)
        => Ordered(vm.FilteredSecrets.Select(x => x.Title).ToArray());

    private static string[] Ordered(params string[] titles)
        => titles.Order(StringComparer.Ordinal).ToArray();

    private static readonly string[] AllTitles = Ordered(TitleGoogleAccount, TitleGoogleDrive, TitleGitHub);

    // ── TC-SIX-01 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Confirms the behavior of calling SpanHit with an empty keyword.
    /// ApplySearch never calls SpanHit for an empty keyword due to its IsNullOrEmpty check, but
    /// this documents that SpanHit alone returns Contains("", src) = true.
    /// </summary>
    [Fact]
    public void SpanHit_EmptyKeyword_ReturnsTrue_BecauseAnyStringContainsEmpty()
    {
        // MemoryExtensions.Contains(source, "".AsSpan(), OrdinalIgnoreCase) always returns true.
        Assert.True(SecretsViewModel.SpanHit("Google".AsSpan(), ""));
    }

    // ── TC-SIX-02 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// An empty SearchText restores the full list after a narrowing search. The narrowing step
    /// first proves ApplySearch actually filtered, so "everything shown" is not just the initial state.
    /// </summary>
    [Fact]
    public async Task ApplySearch_EmptySearchText_ReturnsAllEntries()
    {
        var (db, vm) = await BuildLoadedVmAsync();
        using (db)
        using (vm)
        {
            vm.SearchText = "no_such_keyword_xyz";
            Assert.Empty(vm.FilteredSecrets);

            vm.SearchText = "";

            Assert.Equal(AllTitles, Titles(vm));
        }
    }

    // ── TC-SIX-03 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whitespace-only input (half-width or full-width) is treated as "no keyword" and returns
    /// all entries instead of matching nothing.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("　")]
    [InlineData(" 　 ")]
    public async Task ApplySearch_WhitespaceOnly_ReturnsAllEntries(string whitespace)
    {
        var (db, vm) = await BuildLoadedVmAsync();
        using (db)
        using (vm)
        {
            vm.SearchText = "no_such_keyword_xyz";
            Assert.Empty(vm.FilteredSecrets);

            vm.SearchText = whitespace;

            Assert.Equal(AllTitles, Titles(vm));
        }
    }

    // ── TC-SIX-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SpanHit_CaseInsensitive_UpperKeyword_FindsLowerSource()
    {
        // OrdinalIgnoreCase means case is not distinguished
        Assert.True(SecretsViewModel.SpanHit("google".AsSpan(), "GOOGLE"));
        Assert.True(SecretsViewModel.SpanHit("GITHUB".AsSpan(), "github"));
    }

    // ── TC-SIX-05 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// ApplySearch splits the keyword on half-width and full-width spaces and ANDs the tokens, where
    /// each token may hit a different field. Only "Google Account" has "google" in its Title and
    /// "login" in its UserId, so an OR implementation (or a literal "google login" match) would
    /// return a different set.
    /// </summary>
    [Fact]
    public async Task ApplySearch_MultiWordKeyword_MatchesUsingAndLogicAcrossFields()
    {
        var (db, vm) = await BuildLoadedVmAsync();
        using (db)
        using (vm)
        {
            // Single-token controls: each token alone matches more than one entry
            vm.SearchText = "google";
            Assert.Equal(Ordered(TitleGoogleAccount, TitleGoogleDrive), Titles(vm));
            vm.SearchText = "login";
            Assert.Equal(Ordered(TitleGoogleAccount, TitleGitHub), Titles(vm));

            // AND across fields: Title hits "google", UserId hits "login"
            vm.SearchText = "google login";
            Assert.Equal(Ordered(TitleGoogleAccount), Titles(vm));

            // Full-width space separates tokens too
            vm.SearchText = "google　login";
            Assert.Equal(Ordered(TitleGoogleAccount), Titles(vm));

            // A token that hits nothing fails the whole AND
            vm.SearchText = "google login no_such_keyword_xyz";
            Assert.Empty(vm.FilteredSecrets);
        }
    }

    // ── TC-SIX-06 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SpanHit_NoMatch_ReturnsFalse()
    {
        Assert.False(SecretsViewModel.SpanHit("google".AsSpan(), "xyzxyz123"));

        // SpanHit is a single-keyword literal match; multi-word AND is ApplySearch's job (TC-SIX-05)
        Assert.False(SecretsViewModel.SpanHit("Google Account".AsSpan(), "google login"));
    }

    // ── TC-SIX-07 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full-width alphabetic normalization is not implemented. The half-width "google" and
    /// full-width "ｇｏｏｇｌｅ" do not match. This test exists to make that lack of
    /// implementation explicit.
    /// </summary>
    [Fact]
    public void SpanHit_FullwidthAlpha_IsNotNormalized_ReturnsFalse()
    {
        // Arrange - search with a full-width keyword against a half-width "google" entry
        const string halfWidth  = "google";
        const string fullWidth  = "ｇｏｏｇｌｅ";

        // Act & Assert - mismatched since there is no normalization (making the gap explicit)
        Assert.False(SecretsViewModel.SpanHit(halfWidth.AsSpan(), fullWidth));
    }
}
