// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Services;

namespace NaimitsuVault.ViewModels;

/// <summary>
/// Time Machine generation snapshot.
/// Holds all string fields in SecureCharBuffer (POH-pinned) and applies a blanket
/// lifecycle that ZeroMemories all 13 buffers together on Dispose.
/// Callers that need a string must call new string(XxxBuf.Span) themselves.
/// </summary>
internal sealed class HistorySlotContent : IDisposable
{
    // ── Timestamps (SecureCharBuffer — unified ZeroMemory management) ────────────
    internal readonly SecureCharBuffer TimestampBuf = new();  // ISO 8601 UTC
    internal readonly SecureCharBuffer ExpiresAtBuf = new();  // ISO 8601 UTC
    internal readonly SecureCharBuffer CreatedAtBuf  = new();  // ISO 8601 UTC

    // ── All string fields (SecureCharBuffer: ZeroMemory on Dispose) ──────
    internal readonly SecureCharBuffer TitleBuf          = new();
    internal readonly SecureCharBuffer UserIdBuf         = new();
    internal readonly SecureCharBuffer WebsiteBuf        = new();
    internal readonly SecureCharBuffer EmailBuf          = new();
    internal readonly SecureCharBuffer CustomFieldsBuf   = new();
    internal readonly SecureCharBuffer LabelOverridesBuf = new();
    internal readonly SecureCharBuffer PasswordBuf       = new();
    internal readonly SecureCharBuffer NotesBuf          = new();
    internal readonly SecureCharBuffer TotpSecretBuf     = new();
    internal readonly SecureCharBuffer GenSymbolsBuf     = new();

    // ── Self-contained fields ────────────────────────────────────────────────
    public int[]? FileIds     { get; internal set; }
    public int    SecretId    { get; internal set; }
    public int?   CategoryNum { get; internal set; }
    public bool   IsFavorite  { get; internal set; }

    // ── bool accessors (no string exposure) ─────────────────────────────────────
    public bool HasTotpSecret => !TotpSecretBuf.IsEmpty;

    public void Dispose()
    {
        TimestampBuf.Dispose();
        ExpiresAtBuf.Dispose();
        CreatedAtBuf.Dispose();
        TitleBuf.Dispose();
        UserIdBuf.Dispose();
        WebsiteBuf.Dispose();
        EmailBuf.Dispose();
        CustomFieldsBuf.Dispose();
        LabelOverridesBuf.Dispose();
        PasswordBuf.Dispose();
        NotesBuf.Dispose();
        TotpSecretBuf.Dispose();
        GenSymbolsBuf.Dispose();
        GC.SuppressFinalize(this);
    }
}
