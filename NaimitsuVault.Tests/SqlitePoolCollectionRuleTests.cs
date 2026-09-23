// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text.RegularExpressions;

namespace NaimitsuVault.Tests;

/// <summary>
/// Guards the rule documented on the "SequentialSqlitePool" collection (TestCollections.cs).
///
/// SqliteConnection.ClearAllPools() is process-wide: called from one test class it can dispose the native
/// handle of a connection another, concurrently running test class is using (intermittent
/// ObjectDisposedException: 'SQLitePCL.sqlite3'). A collection defined with DisableParallelization = true
/// runs alone, so every test file that calls it directly must belong to such a collection.
///
/// TC-SPC-01: every test file that calls ClearAllPools() itself is in a DisableParallelization collection.
///
/// Scope: only DIRECT calls are decidable from the source. A test that reaches ClearAllPools() through a
/// production path (DatabaseInitializer.Initialize*, AutoBackupService.PerformUnifiedBackup, ShadowFileService,
/// some AuthService flows - see the list on the collection definition) is not caught here; that half of the
/// rule is a review checklist item.
/// </summary>
public sealed class SqlitePoolCollectionRuleTests
{
    [Fact]
    public void EveryTestFileCallingClearAllPools_BelongsToAnExclusiveCollection()
    {
        var testDir = FindTestProjectDirectory();
        var exclusive = ReadExclusiveCollectionNames(Path.Combine(testDir, "TestCollections.cs"));
        Assert.NotEmpty(exclusive); // guards against the definitions regex silently matching nothing

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) ||
                file.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
                continue;
            // The definitions file and this rule file only mention the call in prose/regex, not as a call.
            var name = Path.GetFileName(file);
            if (name is "TestCollections.cs" or "SqlitePoolCollectionRuleTests.cs") continue;

            var text = File.ReadAllText(file);
            if (!CallsClearAllPools(text)) continue;

            var collections = Regex.Matches(text, "\\[Collection\\(\"([^\"]+)\"\\)\\]")
                .Select(m => m.Groups[1].Value).ToList();
            if (!collections.Any(exclusive.Contains))
                offenders.Add($"{name} (collection: {(collections.Count == 0 ? "none" : string.Join(", ", collections))})");
        }

        Assert.True(
            offenders.Count == 0,
            "These test files call SqliteConnection.ClearAllPools() but are not in a collection defined with "
            + "DisableParallelization = true. Add [Collection(\"SequentialSqlitePool\")] (see TestCollections.cs):\n"
            + string.Join("\n", offenders.Select(o => $"  {o}")));
    }

    /// <summary>True if the text contains a real call, ignoring comment-only lines.</summary>
    private static bool CallsClearAllPools(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.Contains("ClearAllPools(", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static HashSet<string> ReadExclusiveCollectionNames(string collectionsFile)
        => Regex.Matches(File.ReadAllText(collectionsFile),
                "CollectionDefinition\\(\"([^\"]+)\",\\s*DisableParallelization\\s*=\\s*true")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindTestProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NaimitsuVault.Tests", "NaimitsuVault.Tests.csproj")))
                return Path.Combine(dir.FullName, "NaimitsuVault.Tests");
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the NaimitsuVault.Tests source directory by walking up from "
            + AppContext.BaseDirectory + ". This test reads the test .cs files directly, so it "
            + "needs a full repo checkout (not a standalone copy of the test binary).");
    }
}
