// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NaimitsuVault.Services;
using NaimitsuVault.Services.Interfaces;
using Xunit;

namespace NaimitsuVault.Tests;

/// <summary>
/// Test class verifying DI resolution of the auto-type infrastructure (IAutoTypeService).
/// Calls production App.ConfigureServices directly, building a defense line that
/// mechanically detects missing registrations or typos with 100% certainty.
/// </summary>
public class AutoTypeInfrastructureTests
{
    /// <summary>
    /// TC-AT-01: IAutoTypeService must resolve successfully from a container built via
    /// production ConfigureServices.
    ///
    /// Because this calls production App.xaml.cs's ConfigureServices (internal static)
    /// directly, any missing registration or typo surfaces immediately as an
    /// InvalidOperationException. This is not a mirrored setup using a private container.
    /// </summary>
    [Fact]
    public void IAutoTypeService_ThroughProductionConfigureServices_ResolvesWithoutNull()
    {
        // The Win32AutoTypeService constructor calls SetWinEventHook (user32.dll) immediately,
        // which causes a DllNotFoundException on non-Windows CI environments.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Skip("Windows-only: skipped on non-Windows environments because Win32AutoTypeService calls user32.dll directly");
            return;
        }

        // Arrange: run the production DI registration logic as-is.
        // Passing ":memory:" as dbPath makes the SQLite connection string
        // "Data Source=:memory:;Foreign Keys=True". Since DbContextFactory builds lazily,
        // the connection is not opened when the container is built.
        var services = new ServiceCollection();
        App.ConfigureServices(services, ":memory:");
        using var provider = services.BuildServiceProvider();

        // Act: resolve IAutoTypeService.
        // If a registration is missing, GetRequiredService throws InvalidOperationException
        // and the test fails immediately (no false positives)
        var resolved = provider.GetRequiredService<IAutoTypeService>();

        // Assert
        Assert.NotNull(resolved);
        Assert.IsType<Win32AutoTypeService>(resolved);
    }
}
