using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.Services;

public sealed record MigrationLegacySource(string Sha256, string Format, string ArchivePath);

/// <summary>Verify old bytes using their original digest; never repair a digest during reading.</summary>
public static class MigrationLegacyManifestCodec
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "transactionId", "createdAtUtc", "configRoot", "snapshotPath", "snapshotId", "rollbackEntry",
        "fileHashes", "changedFiles", "snapshotManifestHash", "baselineCompleted", "stage", "commitMarker",
        "rollbackRehearsed", "rehearsalScope", "blockedReason", "quiescedAtUtc", "quiesceSessionId",
        "quiesceGeneration", "manifestIntegrity", "referenceWriteSet", "activationRecord", "realEffectsRequired"
    };

    public static string CurrentDigest(MigrationManifest manifest)
    {
        var node = JsonSerializer.SerializeToNode(manifest)!.AsObject();
        node["manifestIntegrity"] = "";
        return MigrationOperationJournal.Hash(node);
    }

    public static bool VerifyDigest(MigrationManifest manifest)
    {
        if (manifest.SchemaVersion == 2) return manifest.ManifestIntegrity == CurrentDigest(manifest);
        if (manifest.SchemaVersion != 1 || manifest.LegacySource is not null) return false;
        return manifest.ManifestIntegrity == MigrationSwitchTransaction.ComputeLegacyManifestIntegrity(manifest, true) ||
            manifest.ActivationRecord?.RevertedHash is null &&
            manifest.ManifestIntegrity == MigrationSwitchTransaction.ComputeLegacyManifestIntegrity(manifest, false);
    }

    public static bool TryDecode(byte[] original, out MigrationManifest? manifest, out string format)
    {
        manifest = null;
        format = "";
        try
        {
            using var document = JsonDocument.Parse(original);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Duplicate(root)) return false;
            var version = root.GetProperty("schemaVersion").GetInt32();
            if (version is not (1 or 2)) return false;
            if (root.EnumerateObject().Any(p => !Fields.Contains(p.Name) && !(version == 2 && p.Name is
                "legacySource" or "baselineVersions" or "controlledBaseline" or "controlledLatest" or "journalBinding" or "legacyBaselineObservation"))) return false;
            if (root.TryGetProperty("activationRecord", out var activation) && activation.ValueKind != JsonValueKind.Null)
            {
                if (activation.ValueKind != JsonValueKind.Object || Duplicate(activation) ||
                    activation.EnumerateObject().Any(p => p.Name is not ("path" or "beforeStatus" or "afterStatus" or "afterHash" or "revertedHash"))) return false;
            }
            var parsed = JsonSerializer.Deserialize<MigrationManifest>(original);
            if (parsed is null || !VerifyDigest(parsed)) return false;
            if (version == 2) format = "runtime-v2-canonical";
            else if (parsed.ActivationRecord is null) format = "legacy-v1-equivalent-no-activation";
            else if (parsed.ManifestIntegrity == MigrationSwitchTransaction.ComputeLegacyManifestIntegrity(parsed, true))
                format = "legacy-v1-with-reverted";
            else
            {
                if (activation.TryGetProperty("revertedHash", out _)) return false;
                format = "legacy-v1-pre-reverted";
            }
            manifest = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        { return false; }
    }

    private static bool Duplicate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return element.EnumerateObject().Any(p => !names.Add(p.Name) || Duplicate(p.Value));
        }
        return element.ValueKind == JsonValueKind.Array && element.EnumerateArray().Any(Duplicate);
    }
}
