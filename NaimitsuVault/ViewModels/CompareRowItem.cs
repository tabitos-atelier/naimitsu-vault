// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NaimitsuVault.ViewModels;

/// <summary>
/// One row of data for the profile comparison dialog.
/// gen0Value / draftValue are copied into pinned char[] buffers immune to GC compaction.
/// Dispose/Clear physically wipes all buffers with ZeroMemory regardless of IsSensitive.
/// </summary>
internal sealed class CompareRowItem : IDisposable
{
    public string Label        { get; }
    public bool IsDiffer       { get; }
    public bool IsSensitive    { get; }
    // True when only the row's custom label (not its value) differs between gen0 and draft -
    // otherwise a label-only edit would leave the row looking completely unchanged in the dialog.
    public bool IsLabelDiffer  { get; }

    private char[]? _gen0Value;
    private char[]? _draftValue;

    public bool IsGen0Empty  => _gen0Value  == null || _gen0Value.Length  == 0;
    public bool IsDraftEmpty => _draftValue == null || _draftValue.Length == 0;

    // Allocation-free span access. Consume via new string(item.Gen0Span) when copying to TextBlock.Text
    public ReadOnlySpan<char> Gen0Span  => _gen0Value  is { Length: > 0 } ? _gen0Value.AsSpan()  : ReadOnlySpan<char>.Empty;
    public ReadOnlySpan<char> DraftSpan => _draftValue is { Length: > 0 } ? _draftValue.AsSpan() : ReadOnlySpan<char>.Empty;

    // string implicitly converts to ReadOnlySpan<char>, so existing callers need no changes
    public CompareRowItem(string label, ReadOnlySpan<char> gen0Value, ReadOnlySpan<char> draftValue, bool isDiffer, bool isSensitive, bool isLabelDiffer = false)
    {
        Label       = label;
        IsDiffer    = isDiffer;
        IsSensitive = isSensitive;
        IsLabelDiffer = isLabelDiffer;

        if (gen0Value.Length > 0)
        {
            _gen0Value = GC.AllocateArray<char>(gen0Value.Length, pinned: true);
            gen0Value.CopyTo(_gen0Value);
        }
        else
        {
            _gen0Value = [];
        }

        if (draftValue.Length > 0)
        {
            _draftValue = GC.AllocateArray<char>(draftValue.Length, pinned: true);
            draftValue.CopyTo(_draftValue);
        }
        else
        {
            _draftValue = [];
        }
    }

    /// <summary>Physically wipes all buffers with ZeroMemory regardless of IsSensitive (idempotent).</summary>
    public void Clear()
    {
        if (_gen0Value is { Length: > 0 })
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_gen0Value.AsSpan()));
        if (_draftValue is { Length: > 0 })
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_draftValue.AsSpan()));
        _gen0Value  = null;
        _draftValue = null;
    }

    public void Dispose() => Clear();
}
