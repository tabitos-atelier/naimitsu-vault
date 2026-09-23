// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using NaimitsuVault.Models;

namespace NaimitsuVault.ViewModels;

public record AuditLogDisplayItem(
    int            Id,
    DateTimeOffset CreatedAt,
    AuditEventCode EventCode,
    int            EventLevel,
    string         Summary
)
{
    public string CreatedAtDisplay => CreatedAt.LocalDateTime.ToString("g");
    public string EventCodeDisplay => $"0x{(int)EventCode:X4}";
}
