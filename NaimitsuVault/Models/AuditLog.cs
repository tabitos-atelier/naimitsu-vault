// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Models;

public class AuditLog
{
    public int     Id         { get; set; }
    public long    CreatedAt  { get; set; }  // Unix seconds UTC (ValueConverter → DateTimeOffset)
    public int     EventCode  { get; set; }  // Cast to AuditEventCode
    public int     EventLevel { get; set; }  // 0=info, 1=warning, 2=critical
    public byte[]? Payload    { get; set; }  // AES-256-GCM BLOB or NULL (for AuthFailed)
}
