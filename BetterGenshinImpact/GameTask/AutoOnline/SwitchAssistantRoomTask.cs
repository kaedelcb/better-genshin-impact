using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoOnline;

/// <summary>请求本机联机助手切换到一个已保存的房间档案。</summary>
public sealed class SwitchAssistantRoomTask : ISoloTask
{
    public const string TaskName = "切换助手房间";
    internal const string TargetRoomName = "targetRoomName";
    private readonly string? _targetRoomName;

    public SwitchAssistantRoomTask(Dictionary<string, object?>? settings)
    {
        _targetRoomName = ReadTargetRoomName(settings);
    }

    internal static string? ReadTargetRoomName(Dictionary<string, object?>? settings)
    {
        if (settings?.TryGetValue(TargetRoomName, out var value) != true) return null;
        return (value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            Newtonsoft.Json.Linq.JValue { Value: string text } => text,
            _ => null,
        })?.Trim();
    }

    private sealed record AssistantRoomIndex(List<SavedRoom?>? RoomProfiles);
    private sealed record SavedRoom(string? Id, string? Name, string? RoomCode);
    private static readonly JsonSerializerOptions RoomIndexJsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static IReadOnlyList<string> ReadSavedRoomNames(string? explicitRoot, string roamingAppData)
    {
        try
        {
            var root = string.IsNullOrEmpty(explicitRoot) ? Path.Combine(roamingAppData, "NexusBGI") : explicitRoot;
            if (!Path.IsPathFullyQualified(root)) return [];
            using var file = File.Open(Path.Combine(root, "assistant-config.json"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var profiles = JsonSerializer.Deserialize<AssistantRoomIndex>(file, RoomIndexJsonOptions)?.RoomProfiles;
            if (profiles is null) return [];
            // 与助手按名称查找时的大小写规则一致。重名不能自动选中其中一间。
            return profiles.OfType<SavedRoom>()
                .Where(room => !string.IsNullOrWhiteSpace(room.Name))
                .GroupBy(room => room.Name!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .Select(group => group.First())
                .Where(room => room.Name == room.Name!.Trim() && !string.IsNullOrWhiteSpace(room.Id)
                    && room.RoomCode is { Length: 6 } code && code.All(char.IsAsciiLetterOrDigit))
                .Select(room => room.Name!).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return [];
        }
    }

    public string Name => TaskName;

    public Task Start(CancellationToken ct) => ConfigurationGroupRoomActions.SwitchAsync(_targetRoomName, ct);

    public static List<SoloTaskSettingItem> GetSettingDefinitions() =>
    [
        new() { Name = TargetRoomName, Label = "目标房间", DefaultValue = "" },
    ];
}
