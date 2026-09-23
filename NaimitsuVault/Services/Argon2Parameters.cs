// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

namespace NaimitsuVault.Services;

/// <summary>Single source of truth for Argon2id parameters, shared by CryptoService and AuthService.Modes.</summary>
public static class Argon2Parameters
{
    // OWASP 2023 recommended values
    public const int Parallelism = 4;
    public const int MemorySize = 65536;  // 64 MB
    public const int Iterations = 3;

    // Emergency access code PIN key derivation (AuthService.DerivePinKey): deliberately lighter than
    // the master-password parameters above so EAC unlock stays fast, since the PIN is only ever used
    // to decrypt a QR-embedded random key (rk), not as the sole line of defense.
    public const int PinParallelism = 2;
    public const int PinMemorySize = 32768;  // 32 MB
    public const int PinIterations = 2;
}
