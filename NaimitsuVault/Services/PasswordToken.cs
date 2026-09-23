// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Thin wrapper to force DependencyProperty change detection.
/// Used so that instead of PasswordLargePreviewControl receiving a password string, a reference
/// to the pinned buffer (SecureCharBuffer) can be safely tunneled through.
/// The caller (CustomFieldModel.PasswordToken getter) returns a new PasswordToken(_buf) every time,
/// which makes the DependencyProperty system see "the value changed" and fire OnPasswordTokenChanged.
/// PasswordToken itself holds no sensitive data (only a reference to the SecureCharBuffer).
/// </summary>
public sealed class PasswordToken
{
    internal readonly SecureCharBuffer Buffer;

    internal PasswordToken(SecureCharBuffer buffer) => Buffer = buffer;
}
