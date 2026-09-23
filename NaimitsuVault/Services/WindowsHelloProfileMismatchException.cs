// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>
/// Thrown when Windows Hello biometric verification succeeds but the DPAPI-protected key material
/// cannot be decrypted on the current device/Windows account (e.g. the vault was moved to a
/// different PC, or the Windows account/profile changed).
/// </summary>
public sealed class WindowsHelloProfileMismatchException() : Exception(
    "Windows Hello key material could not be decrypted on this device/account.");
