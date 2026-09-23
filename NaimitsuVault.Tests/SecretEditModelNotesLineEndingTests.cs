// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// WinUI 3's multiline TextBox (AcceptsReturn="True") inserts a bare CR on Enter, not CRLF/LF.
/// SecretEditModel.Notes normalizes CR/CRLF to LF at the point of entry so exported JSON/CSV don't
/// carry a lone \r (which round-trips to a literal \r in the export file instead of a real newline).
/// </summary>
public sealed class SecretEditModelNotesLineEndingTests
{
    [Fact]
    public void Notes_BareCr_NormalizedToLf()
    {
        using var model = new SecretEditModel { Notes = "line1\rline2" };

        Assert.Equal("line1\nline2", model.Notes);
    }

    [Fact]
    public void Notes_Crlf_NormalizedToLf()
    {
        using var model = new SecretEditModel { Notes = "line1\r\nline2" };

        Assert.Equal("line1\nline2", model.Notes);
    }

    [Fact]
    public void Notes_MixedLineEndings_AllNormalizedToLf()
    {
        using var model = new SecretEditModel { Notes = "a\rb\r\nc\nd" };

        Assert.Equal("a\nb\nc\nd", model.Notes);
    }

    [Fact]
    public void Notes_NoCarriageReturn_UnchangedFastPath()
    {
        using var model = new SecretEditModel { Notes = "plain text" };

        Assert.Equal("plain text", model.Notes);
    }

    [Fact]
    public void Notes_SetTwiceWithSameNormalizedResult_SecondSetIsNoOp()
    {
        using var model = new SecretEditModel { Notes = "line1\rline2" };
        model.Notes = "line1\nline2"; // Already-normalized text equal to the stored value

        Assert.Equal("line1\nline2", model.Notes);
    }
}
