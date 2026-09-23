// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NaimitsuVault.Helpers;
using NaimitsuVault.Repositories;
using NaimitsuVault.Services.Interfaces;

namespace NaimitsuVault.Services;

/// <summary>
/// Provides password strength evaluation via zxcvbn-core and a full-DB scan for the dashboard.
/// Choke-and-release design: plaintext passwords are never accumulated in a List etc.; each record
/// is zeroed immediately. zxcvbn requires a string API, so a string is created, but its internal
/// buffer is zeroed via ZeroStringInternals immediately after evaluation.
/// </summary>
public class PasswordEvaluationService(
    SecretRepository secretRepository,
    ICryptoService crypto,
    ILogger<PasswordEvaluationService> logger) : IPasswordEvaluationService
{
    public int EvaluateStrength(ReadOnlySpan<char> plainText)
    {
        if (plainText.IsEmpty) return 0;
        var plain = new string(plainText);
        try   { return Zxcvbn.Core.EvaluatePassword(plain).Score; }
        finally { SecurePasswordHelper.ZeroStringInternals(plain); }
    }

    public async Task<DashboardScanResult> ScanAllSecretsAsync(DekScope dek, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var secrets = await secretRepository.GetAllAsync();
        ct.ThrowIfCancellationRequested();

        var weakItems   = new List<SecurityAlertItem>();
        var hashToItems = new Dictionary<string, List<(int Id, string Title)>>();

        foreach (var s in secrets)
        {
            ct.ThrowIfCancellationRequested();

            // Items with no password set are excluded from strength evaluation and duplicate detection.
            // Checked before decrypting the title so memo-only/password-less items never pay for a
            // title decryption that would just be discarded.
            if (s.Password == null || s.Password.Length == 0)
                continue;

            using var pwBuf = FieldCrypto.Open(s.Password, crypto, dek);
            if (pwBuf == null || pwBuf.Utf8.IsEmpty)
                continue;

            // Decrypt the title (for display in alert items)
            string title;
            using (var titleBuf = FieldCrypto.Open(s.Title, crypto, dek))
            {
                title = titleBuf != null ? Encoding.UTF8.GetString(titleBuf.Utf8) : string.Empty;
            }

            string plain = Encoding.UTF8.GetString(pwBuf.Utf8);
            int score;
            try   { score = Zxcvbn.Core.EvaluatePassword(plain).Score; }
            finally { SecurePasswordHelper.ZeroStringInternals(plain); }

            if (score <= 2)
                weakItems.Add(new SecurityAlertItem(s.Id, title, score));

            // HMAC-SHA256(dek, password) for duplicate detection
            byte[] hashBytes = HMACSHA256.HashData(dek.Span, pwBuf.Utf8);
            string hexHash   = Convert.ToHexString(hashBytes);
            CryptographicOperations.ZeroMemory(hashBytes); // zero the byte[] immediately

            if (!hashToItems.TryGetValue(hexHash, out var group))
            {
                group = [];
                hashToItems[hexHash] = group;
            }
            group.Add((s.Id, title));
        }

        // After the loop: tally duplicate groups (password types used in 2 or more entries).
        // Each group keeps a shared DuplicateGroupId so the dashboard can render "a, b" / "c, d" as
        // two separate rows instead of flattening every reused item into one undifferentiated list.
        int reusedGroupCount = hashToItems.Values.Count(l => l.Count > 1);
        var reusedItems = new List<SecurityAlertItem>();
        int groupId = 0;
        foreach (var group in hashToItems.Values.Where(l => l.Count > 1))
        {
            foreach (var (id, title) in group)
                reusedItems.Add(new SecurityAlertItem(id, title, null, groupId));
            groupId++;
        }

        logger.LogInformation(
            "[PasswordEvaluation] Scan complete: weak={W}, reused={R}",
            weakItems.Count, reusedGroupCount);

        return new DashboardScanResult(
            WeakPasswordCount:   weakItems.Count,
            ReusedPasswordCount: reusedGroupCount,
            WeakItems:  weakItems.AsReadOnly(),
            ReusedItems: reusedItems.AsReadOnly()
        );
    }
}
