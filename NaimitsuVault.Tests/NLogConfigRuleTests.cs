// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace NaimitsuVault.Tests;

/// <summary>
/// Verifies the shipped nlog.config (the copy next to the test executable, which is the production file):
/// the routing rules, the file layout, the size bound, and NLog's internal log. Rule evaluation is asked of
/// NLog directly through IsXxxEnabled and the layout is rendered in memory, so no log file is written and
/// nothing depends on async flushing.
/// </summary>
public class NLogConfigRuleTests : IDisposable
{
    private readonly LogFactory _factory = new();

    public NLogConfigRuleTests()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "nlog.config");
        Assert.True(File.Exists(path), "nlog.config must be copied next to the executable");
        _factory.Configuration = new XmlLoggingConfiguration(path, _factory);
    }

    public void Dispose() => _factory.Shutdown();

    /// <summary>
    /// TC-NLC-01: EF Core loggers are discarded at every level. Its command logger writes each executed
    /// SQL statement (table and column names) at Info, and error text can embed the statement as well.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Microsoft.EntityFrameworkCore.Infrastructure")]
    public void EntityFrameworkCoreLoggers_AreDiscardedAtEveryLevel(string category)
    {
        var logger = _factory.GetLogger(category);

        Assert.False(logger.IsInfoEnabled);
        Assert.False(logger.IsWarnEnabled);
        Assert.False(logger.IsErrorEnabled);
        Assert.False(logger.IsFatalEnabled);
    }

    /// <summary>TC-NLC-02: Other framework loggers keep Warn and above only.</summary>
    [Theory]
    [InlineData("Microsoft.Data.Sqlite")]
    [InlineData("System.Net.Http.HttpClient.Default.ClientHandler")]
    public void OtherFrameworkLoggers_KeepWarnAndAboveOnly(string category)
    {
        var logger = _factory.GetLogger(category);

        Assert.False(logger.IsInfoEnabled);
        Assert.True(logger.IsWarnEnabled);
        Assert.True(logger.IsErrorEnabled);
        Assert.True(logger.IsFatalEnabled);
    }

    /// <summary>
    /// TC-NLC-03: Application loggers (ILogger&lt;T&gt; categories are fully qualified NaimitsuVault.* names)
    /// keep Info and above, and Debug is still dropped.
    /// </summary>
    [Fact]
    public void ApplicationLoggers_KeepInfoAndAbove()
    {
        var logger = _factory.GetLogger("NaimitsuVault.Services.AuthService");

        Assert.False(logger.IsDebugEnabled);
        Assert.True(logger.IsInfoEnabled);
        Assert.True(logger.IsWarnEnabled);
        Assert.True(logger.IsErrorEnabled);
        Assert.True(logger.IsFatalEnabled);
    }

    private FileTarget FileTarget => _factory.Configuration!.AllTargets.OfType<FileTarget>().Single();

    /// <summary>
    /// TC-NLC-04: The layout renders only the exception type. Application code never passes exception
    /// objects, but a framework logger (Warn and above is kept) can; a full ToString() would write the stack
    /// trace, file paths including the OS user name, and the exception message into a file users may send in.
    /// </summary>
    [Fact]
    public void Layout_WithException_RendersTypeNameOnly()
    {
        Exception ex;
        try
        {
            throw new FileNotFoundException(
                @"Could not find file 'C:\Users\alice\secret.txt'.", @"C:\Users\alice\secret.txt");
        }
        catch (Exception caught) { ex = caught; }

        var line = FileTarget.Layout.Render(
            new LogEventInfo(LogLevel.Warn, "Microsoft.Data.Sqlite", "Something failed") { Exception = ex });

        Assert.EndsWith("Something failed [System.IO.FileNotFoundException]", line);
        Assert.DoesNotContain("alice", line);
        Assert.DoesNotContain("secret.txt", line);
        Assert.DoesNotContain(" at ", line);
    }

    /// <summary>TC-NLC-05: Without an exception the line ends at the message (no stray brackets).</summary>
    [Fact]
    public void Layout_WithoutException_EndsAtMessage()
    {
        var line = FileTarget.Layout.Render(
            new LogEventInfo(LogLevel.Info, "NaimitsuVault.Services.AuthService", "Unlocked"));

        Assert.EndsWith("AuthService - Unlocked", line);
    }

    /// <summary>
    /// TC-NLC-06: The log size is bounded: it rolls daily and also above 10 MB, keeping at most 10 archives
    /// (a runaway loop must not grow one day's file without limit).
    /// </summary>
    [Fact]
    public void FileTarget_IsSizeBounded()
    {
        var target = FileTarget;

        Assert.Equal(10 * 1024 * 1024, target.ArchiveAboveSize);
        Assert.Equal(FileArchivePeriod.Day, target.ArchiveEvery);
        Assert.Equal(10, target.MaxArchiveFiles);
    }

    /// <summary>
    /// TC-NLC-07: NLog's own diagnostics are off. The internal log is unrotated and records full file paths and
    /// stack traces (for example when the log file itself cannot be written).
    /// </summary>
    [Fact]
    public void InternalLog_IsOff()
    {
        Assert.Equal(LogLevel.Off, InternalLogger.LogLevel);
    }
}
