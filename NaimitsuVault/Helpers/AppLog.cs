// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace NaimitsuVault.Helpers;

/// <summary>
/// The single entry point for obtaining an <see cref="ILogger{T}"/> where constructor injection is not
/// possible: static classes, and XAML-instantiated Views/Controls/Converters. DI-managed classes receive
/// <see cref="ILogger{T}"/> through their constructor instead.
///
/// Both paths end up in the same NLog pipeline (nlog.config), so a message looks identical regardless of
/// which path produced it. This class owns its own factory rather than reading it from the service
/// provider so that it also works before the provider is built (App's unhandled-exception handlers) and
/// in unit tests that never start the application.
/// </summary>
internal static class AppLog
{
    private static readonly Lazy<ILoggerFactory> _factory = new(() => LoggerFactory.Create(Configure));

    /// <summary>Shared logging setup used by both this class and the service provider (<c>App.ConfigureServices</c>).</summary>
    internal static void Configure(ILoggingBuilder builder)
    {
        builder.ClearProviders();
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddNLog();
    }

    internal static ILogger<T> For<T>() => _factory.Value.CreateLogger<T>();

    /// <summary>For static classes, which cannot be used as the type argument of <see cref="ILogger{T}"/>. The category name is the same.</summary>
    internal static ILogger For(Type type) => _factory.Value.CreateLogger(type);
}
