// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NaimitsuVault.Helpers;
using NaimitsuVault.Services;
using NaimitsuVault.ViewModels;

namespace NaimitsuVault.Tests;

/// <summary>
/// "Custom field boundary values" - all 7 cases for AES-256-GCM encrypt/decrypt round-trips.
/// CustomFields is verified by round-tripping a UTF-8 JSON string through Encrypt/Decrypt.
/// </summary>
public sealed class CustomFieldRoundtripTests
{
    private static CryptoService Svc() => new(NullLogger<CryptoService>.Instance);

    private static byte[] RandomDek()
    {
        var k = new byte[32];
        RandomNumberGenerator.Fill(k);
        return k;
    }

    /// <summary>Verifies that JSON string -> encrypt -> decrypt -> UTF-8 string matches the original.</summary>
    private static void AssertRoundtrip(string originalJson)
    {
        var svc      = Svc();
        var dek      = RandomDek();
        var plainBytes = Encoding.UTF8.GetBytes(originalJson);
        var cipher   = svc.Encrypt(plainBytes, dek);
        var output   = new byte[plainBytes.Length];
        svc.Decrypt(cipher, dek, output);
        var restored = Encoding.UTF8.GetString(output);
        Assert.Equal(originalJson, restored);
    }

    // ── TC-CFR-01 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_NullCustomFields_EmptyBytesEncryptThenDecryptToEmpty()
    {
        // Arrange - null CustomFields is represented as an empty byte array (length 0)
        var svc    = Svc();
        var dek    = RandomDek();
        var empty  = Array.Empty<byte>();

        // Act
        var cipher = svc.Encrypt(empty, dek);
        var output = new byte[0];
        svc.Decrypt(cipher, dek, output);

        // Assert - still length 0 after decryption
        Assert.Empty(output);
        Assert.Equal(28, cipher.Length); // nonce(12)+tag(16)+ciphertext(0)
    }

    // ── TC-CFR-02 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_SingleField_JsonPreserved()
    {
        AssertRoundtrip("""{"key":"value"}""");
    }

    // ── TC-CFR-03 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_50Fields_JsonPreserved()
    {
        var obj = new Dictionary<string, string>();
        for (int i = 0; i < 50; i++) obj[$"key{i:D2}"] = $"value{i:D2}";
        var json = JsonSerializer.Serialize(obj);
        AssertRoundtrip(json);
    }

    // ── TC-CFR-04 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_JapaneseMultibyte_NoGarbling()
    {
        AssertRoundtrip("""{"値":"パスワード123"}""");
    }

    // ── TC-CFR-05 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_Emoji_Utf8Preserved()
    {
        AssertRoundtrip("""{"key":"🔐🗝️"}""");
    }

    // ── TC-CFR-06 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_JsonSpecialChars_EscapeIntact()
    {
        // Arrange - a JSON-escaped nested string
        const string json = """{"key":"{\"nested\":\"json\"}"}""";

        // Act
        AssertRoundtrip(json);

        // Assert - can be re-parsed as JSON after decryption
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("{\"nested\":\"json\"}", doc.RootElement.GetProperty("key").GetString());
    }

    // ── TC-CFR-07 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [GAP-04] Confirms the behavior of a JSON value containing a NULL byte.
    /// AES-256-GCM encryption/decryption handles a byte sequence containing a NULL byte without issue.
    /// The behavior when converted to a string by EF Core + SQLite needs separate verification.
    /// </summary>
    [Fact]
    public void Roundtrip_NullByteInValue_CryptoLayerHandlesItGracefully()
    {
        // Arrange - a raw byte sequence containing a NULL byte (deliberately invalid as a JSON string)
        var svc       = Svc();
        var dek       = RandomDek();
        var withNul   = new byte[] { (byte)'a', 0x00, (byte)'b' }; // a\0b

        // Act - encryption/decryption works without issue
        var cipher = svc.Encrypt(withNul, dek);
        var output = new byte[withNul.Length];
        svc.Decrypt(cipher, dek, output);

        // Assert - the crypto layer preserves the NULL byte
        Assert.Equal(withNul, output);
        // GAP-04: the behavior when stored in a SQLite TEXT column is verified separately in SecretHistoryRepositoryTests
    }

    // ── TC-CFR-08 ─────────────────────────────────────────────────────────────────
    // CustomFieldModel.FieldType (the bool IsPassword/IsUrl/IsDate -> single FieldType enum migration)
    // round-trips through the same AOT-generated JsonSerializerContext used to persist CustomFields.

    [Theory]
    [InlineData(CustomFieldType.Text)]
    [InlineData(CustomFieldType.Password)]
    [InlineData(CustomFieldType.Url)]
    [InlineData(CustomFieldType.Date)]
    public void CustomFieldModel_FieldType_RoundTripsThroughJson(CustomFieldType fieldType)
    {
        var original = new List<CustomFieldModel>
        {
            new() { FieldId = 1, Label = "label", Value = "value", FieldType = fieldType },
        };
        var json     = JsonSerializer.Serialize(original, SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);
        var restored = JsonSerializer.Deserialize(json, SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);

        Assert.NotNull(restored);
        Assert.Single(restored!);
        Assert.Equal(fieldType, restored![0].FieldType);
        Assert.Equal(fieldType == CustomFieldType.Password, restored[0].IsPassword);
        Assert.Equal(fieldType == CustomFieldType.Url,      restored[0].IsUrl);
        Assert.Equal(fieldType == CustomFieldType.Date,     restored[0].IsDate);
    }

    // ── TC-CFR-09 ─────────────────────────────────────────────────────────────────
    // FieldType is serialized as a plain integer (not a string), keeping the JSON payload minimal
    // and avoiding a JsonStringEnumConverter dependency in the AOT-safe JsonSerializerContext.

    [Fact]
    public void CustomFieldModel_FieldType_SerializedAsInteger()
    {
        var fields = new List<CustomFieldModel> { new() { FieldId = 1, FieldType = CustomFieldType.Password } };
        var json   = JsonSerializer.Serialize(fields, SecretsViewModel.SecretListJsonContext.Default.ListCustomFieldModel);
        Assert.Contains("\"FieldType\":1", json);
    }
}
