// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Helpers;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Exception chain formatting for the log" TC-EXC-01–04.
/// The formatter exists so that a startup failure can be diagnosed from the log alone, without ever
/// writing exception messages or file paths to it.
/// </summary>
public sealed class ExceptionChainFormatterTests
{
    private sealed class HResultException : Exception
    {
        public HResultException(int hresult, string message, Exception? inner = null) : base(message, inner)
            => HResult = hresult;
    }

    // TC-EXC-01
    [Fact]
    public void Format_SingleException_ReturnsTypeNameAndHResult()
    {
        var ex = new HResultException(unchecked((int)0x800711C7), "blocked");

        Assert.Equal("HResultException(0x800711C7)", ExceptionChainFormatter.Format(ex));
    }

    // TC-EXC-02
    [Fact]
    public void Format_NestedExceptions_ListsOuterToInner()
    {
        var inner = new HResultException(unchecked((int)0x800711C7), "blocked");
        var middle = new FileLoadException("load failed", inner);
        var outer = new TypeInitializationException("Some.Type", middle);

        var expected =
            $"TypeInitializationException(0x{outer.HResult:X8}) <- " +
            $"FileLoadException(0x{middle.HResult:X8}) <- " +
            "HResultException(0x800711C7)";
        Assert.Equal(expected, ExceptionChainFormatter.Format(outer));
    }

    // TC-EXC-03
    [Fact]
    public void Format_NeverIncludesMessageOrFileName()
    {
        const string path = @"C:\Users\someone\secret-folder\Some.Assembly.dll";
        var inner = new FileLoadException("Could not load " + path, path);
        var outer = new InvalidOperationException("wrapper mentions " + path, inner);

        var text = ExceptionChainFormatter.Format(outer);

        Assert.DoesNotContain("someone", text);
        Assert.DoesNotContain("secret-folder", text);
        Assert.DoesNotContain("Some.Assembly", text);
        Assert.DoesNotContain("wrapper", text);
    }

    // TC-EXC-04
    [Fact]
    public void Format_DeepChain_IsTruncatedWithEllipsis()
    {
        Exception ex = new InvalidOperationException("level 0");
        for (int i = 1; i < 20; i++) ex = new InvalidOperationException("level " + i, ex);

        var text = ExceptionChainFormatter.Format(ex);

        Assert.Equal(8, text.Split("InvalidOperationException").Length - 1);
        Assert.EndsWith(" <- ...", text);
    }
}
