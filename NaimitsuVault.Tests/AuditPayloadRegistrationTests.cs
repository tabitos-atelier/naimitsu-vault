// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Reflection;
using System.Text.Json.Serialization;
using NaimitsuVault.Models;

namespace NaimitsuVault.Tests;

/// <summary>
/// Structural consistency tests for AuditEventCode, Payload types, and
/// AuditPayloadJsonContext registration.
///
/// If a corresponding Payload record is not created, or the [JsonSerializable] registration
/// in AuditPayloadJsonContext is forgotten, when a new AuditEventCode is added, this test
/// fails in CI, catching a silent runtime degradation (serialization dropping to null)
/// before it ships.
///
/// Never touches product code; relies solely on reflection-based structural cross-checking.
/// </summary>
public sealed class AuditPayloadRegistrationTests
{
    // The assembly the AuditPayload abstract record belongs to (= home of the NaimitsuVault.Models namespace)
    private static readonly Type PayloadBase = typeof(AuditPayload);

    // The set of all types registered via [JsonSerializable(typeof(T))] on AuditPayloadJsonContext.
    // Retrieves the constructor argument directly via GetCustomAttributesData() so it does not
    // depend on JsonSerializableAttribute's property names.
    private static readonly HashSet<Type> ContextRegistered =
        typeof(AuditPayloadJsonContext)
            .GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(JsonSerializableAttribute))
            .Select(a => (Type)a.ConstructorArguments[0].Value!)
            .ToHashSet();

    /// <summary>
    /// Forward check: every AuditEventCode that has a corresponding {CodeName}Payload type
    /// must have that type registered via [JsonSerializable] on AuditPayloadJsonContext.
    ///
    /// Catches the mistake of "added to the enum → created the Payload record too → but forgot
    /// to register it with JsonContext."
    /// </summary>
    [Fact]
    public void AllPayloadTypes_AreRegisteredInContext()
    {
        var assembly = PayloadBase.Assembly;
        var failures = new List<string>();

        foreach (var code in Enum.GetValues<AuditEventCode>())
        {
            var payloadType = assembly.GetType($"NaimitsuVault.Models.{code}Payload");
            if (payloadType is null) continue;                            // no Payload for this code → skip
            if (!PayloadBase.IsAssignableFrom(payloadType)) continue;    // skip if not derived from AuditPayload

            if (!ContextRegistered.Contains(payloadType))
                failures.Add($"  AuditEventCode.{code} → {code}Payload is not registered in AuditPayloadJsonContext");
        }

        Assert.True(
            failures.Count == 0,
            "The following Payload types are not registered via [JsonSerializable] in AuditPayloadJsonContext:\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Reverse check: every AuditPayload-derived type registered on AuditPayloadJsonContext
    /// must have a corresponding AuditEventCode member.
    ///
    /// Catches orphaned registrations left behind when a "Payload record was deleted or
    /// renamed, but the [JsonSerializable] entry in JsonContext was not removed."
    /// </summary>
    [Fact]
    public void AllRegisteredPayloadTypes_HaveCorrespondingEventCode()
    {
        var codeNames = Enum.GetNames<AuditEventCode>().ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var type in ContextRegistered)
        {
            if (!PayloadBase.IsAssignableFrom(type)) continue; // skip if not derived from AuditPayload
            if (type == PayloadBase) continue;                 // exclude the abstract base class itself

            var name = type.Name;
            if (!name.EndsWith("Payload", StringComparison.Ordinal)) continue;

            var expectedCode = name[..^"Payload".Length]; // "{CodeName}Payload" → "{CodeName}"
            if (!codeNames.Contains(expectedCode))
                failures.Add($"  {name} is registered in AuditPayloadJsonContext, but AuditEventCode.{expectedCode} does not exist (orphaned registration)");
        }

        Assert.True(
            failures.Count == 0,
            "No corresponding AuditEventCode exists for the following Payload types:\n"
            + string.Join("\n", failures));
    }
}
