// Copyright (c) 2026 Tabito's Works
// Licensed under the MIT License. See LICENSE file in the project root for full license information.

using System.Text.Json;

namespace NaimitsuVault.Repositories;

/// <summary>
/// Reader-side logic for the "FileIds" array that SnapshotSerializer writes into a Secret
/// snapshot's JSON (SecretHistory slots A/B/C, SecretDrafts). Currently used by
/// SecretDraftsRepository to answer "is this fileId referenced by any snapshot" without decoding the
/// whole snapshot into an object.
/// </summary>
internal static class SnapshotFileIdScanner
{
    /// <summary>
    /// Scans the JSON at minimal cost with Utf8JsonReader and returns whether fileId appears in the
    /// "FileIds" array. SnapshotSerializer writes it in the form "FileIds": [int, ...].
    /// </summary>
    internal static bool ContainsFileId(byte[] utf8Json, int fileId)
    {
        var reader = new Utf8JsonReader(utf8Json.AsSpan());
        bool inFileIds = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    // CurrentDepth == 1 restricts the match to a top-level property (SnapshotSerializer
                    // only ever writes "FileIds" at the snapshot root). Without this, a same-named
                    // property nested inside a free-form object (e.g. CustomFields) would falsely trigger.
                    inFileIds = reader.CurrentDepth == 1 && reader.ValueTextEquals("FileIds"u8);
                    break;
                case JsonTokenType.Number when inFileIds:
                    if (reader.TryGetInt32(out int id) && id == fileId) return true;
                    break;
                case JsonTokenType.EndArray when inFileIds:
                    return false;   // Not found through the end of the array
            }
        }
        return false;
    }
}
