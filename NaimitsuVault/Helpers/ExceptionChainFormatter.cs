// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text;

namespace NaimitsuVault.Helpers;

/// <summary>
/// Formats an exception and its <see cref="Exception.InnerException"/> chain for the log as type names
/// and HRESULTs only, e.g. <c>TypeInitializationException(0x80131534) &lt;- FileLoadException(0x800711C7)</c>.
///
/// Startup failures are usually reported as a wrapper (TypeInitializationException, TargetInvocationException)
/// whose type name alone does not tell an OS-level block apart from real data corruption. The HRESULT of
/// the innermost exception does: 0x800711C7 is an application control policy block (Smart App Control),
/// 0x800700E1 an antivirus block, 0x80070005 access denied, 0x80070002 file not found.
///
/// Message, StackTrace, and properties such as FileLoadException.FileName are never included, because
/// they can carry file paths (with the OS user name), column names, and other content.
/// </summary>
internal static class ExceptionChainFormatter
{
    // Guards against a pathological or cyclic chain flooding the log line.
    private const int MaxDepth = 8;

    internal static string Format(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        for (int depth = 0; current is not null && depth < MaxDepth; depth++, current = current.InnerException)
        {
            if (depth > 0) sb.Append(" <- ");
            sb.Append(current.GetType().Name)
              .Append("(0x")
              .Append(current.HResult.ToString("X8"))
              .Append(')');
        }
        if (current is not null) sb.Append(" <- ...");
        return sb.ToString();
    }
}
