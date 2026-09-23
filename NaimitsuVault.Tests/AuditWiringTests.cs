// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text.RegularExpressions;
using NaimitsuVault.Models;

namespace NaimitsuVault.Tests;

/// <summary>
/// Verifies every AuditEventCode has an actual call site (LogAsync / LogSettingAsync /
/// LogSettingChanged / RestoreAuditMarker.Write) in production code. A newly added enum value
/// that nobody wires up fails this test immediately instead of surviving until a manual audit
/// stumbles onto it. A value with no production feature to wire it up to yet should not be added
/// to the enum in the first place - it should stay out until the feature exists.
///
/// Unlike AuditPayloadRegistrationTests this cannot be reflection-only: whether an enum value
/// is passed as an argument inside some method body is source-level information that compiled
/// metadata does not retain. This test reads the production .cs files directly instead.
/// Known limitation: a literal "AuditEventCode.Xxx" occurring inside a comment or string next to
/// one of the sink names would also count as wired (false negative on a real gap) - in practice
/// this codebase does not write that kind of comment.
///
/// Second known limitation, on the production-code side rather than the test: CallSitePattern only
/// matches a call whose leading 0-2 simple (non-nested) arguments are immediately followed by a bare
/// "AuditEventCode.Xxx" - e.g. LogAsync(cond ? AuditEventCode.A : AuditEventCode.B, ...) does not
/// match that shape, so if neither A nor B has any other, simpler call site elsewhere, this test
/// would wrongly report them as unwired (or, if some other call site happens to satisfy the pattern
/// for one of them, silently miss that this particular call is conditional and could pass the wrong
/// code). Production call sites must keep passing AuditEventCode.Xxx as a direct, literal argument
/// (no ternary or other computed expression) for this test to keep verifying what it claims to.
/// </summary>
public sealed class AuditWiringTests
{
    // Matches a call to a known audit-log sink (or a wrapper named LogSettingAsync*/LogSettingChanged*)
    // with AuditEventCode.<Name> as the first argument, or within the first couple of simple
    // (non-nested) leading arguments - e.g. RestoreAuditMarker.Write(dataDir, AuditEventCode.X, ...).
    private static readonly Regex CallSitePattern = new(
        @"(?:\bLogAsync|\bLogSettingAsync\w*|\bLogSettingChanged\w*|RestoreAuditMarker\.Write)\s*\(\s*(?:[\w.]+\s*,\s*){0,2}AuditEventCode\.(\w+)\b",
        RegexOptions.Compiled);

    // AuthFailed is the one exception: its Payload is always NULL, so AuditLogService exposes a
    // dedicated argument-less sink (LogAuthFailedAsync) instead of taking AuditEventCode as a
    // parameter - the code is a hardcoded constant inside that method, never passed at the call site.
    private const string AuthFailedSinkCall = "LogAuthFailedAsync(";

    [Fact]
    public void AllAuditEventCodes_HaveACallSite()
    {
        var wired = ScanWiredCodes();
        var missing = Enum.GetNames<AuditEventCode>()
            .Where(name => !wired.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The following AuditEventCode values have no LogAsync/LogSettingAsync/LogSettingChanged/"
            + "RestoreAuditMarker.Write call site anywhere in production code:\n"
            + string.Join("\n", missing.Select(m => $"  {m}")));
    }

    private static HashSet<string> ScanWiredCodes()
    {
        var root = FindProductionSourceRoot();
        var wired = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match m in CallSitePattern.Matches(text))
                wired.Add(m.Groups[1].Value);
            if (text.Contains(AuthFailedSinkCall, StringComparison.Ordinal))
                wired.Add(nameof(AuditEventCode.AuthFailed));
        }

        return wired;
    }

    private static string FindProductionSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "NaimitsuVault", "NaimitsuVault.csproj");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "NaimitsuVault");
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the NaimitsuVault production source directory by walking up from "
            + AppContext.BaseDirectory + ". This test reads production .cs files directly, so it "
            + "needs a full repo checkout (not a standalone copy of the test binary).");
    }
}
