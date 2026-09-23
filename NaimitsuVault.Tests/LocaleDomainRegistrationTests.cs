// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Localization;

namespace NaimitsuVault.Tests;

/// <summary>
/// Regression tests guarding LocalizationManager.PackKeyPrefix's hand-maintained Domain/SubDomain
/// dictionaries against silently falling back to code 0x00 for a locale key whose first or second
/// segment was never registered. That fallback is indistinguishable from the "Common" domain (0x00)
/// and has caused repeated real incidents (missing Mode(0x04) subdomain, missing DomainCodes entries)
/// that unit tests on individual functions could not catch, since the bug is a registration gap
/// rather than a computation error.
/// </summary>
public sealed class LocaleDomainRegistrationTests
{
    private static LocalizationService CreateService(TestUnifiedDb db)
        => new(db.Factory, NullLogger<LocalizationService>.Instance);

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void LoadBuiltinLocale_AllKeys_ResolveToARegisteredDomain(string locale)
    {
        using var db = TestUnifiedDb.Create();
        var messages = CreateService(db).LoadBuiltinLocale(locale);

        var unregistered = messages.Keys
            .Where(k => k.Split('.')[0] != "Common")
            .Where(k => (LocalizationManager.PackKeyPrefix(k) & 0xFF0000) == 0)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unregistered.Count == 0,
            "The following locale keys resolve to Domain=0x00, silently colliding with the "
            + "\"Common\" domain, because their first segment is missing from "
            + "LocalizationManager.DomainCodes: " + string.Join(", ", unregistered));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void LoadBuiltinLocale_ThreeSegmentKeys_ResolveToARegisteredSubDomain(string locale)
    {
        using var db = TestUnifiedDb.Create();
        var messages = CreateService(db).LoadBuiltinLocale(locale);

        var unregistered = messages.Keys
            .Where(k => k.Split('.').Length == 3)
            .Where(k => (LocalizationManager.PackKeyPrefix(k) & 0x00FF00) == 0)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unregistered.Count == 0,
            "The following 3-segment locale keys resolve to SubDomain=0x00, silently colliding "
            + "with 2-segment keys of the same domain, because their middle segment is missing "
            + "from SubDomainCodes/SystemSubDomainCodes: " + string.Join(", ", unregistered));
    }
}
