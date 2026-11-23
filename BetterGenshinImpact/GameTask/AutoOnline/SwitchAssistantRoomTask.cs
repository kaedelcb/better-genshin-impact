using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoOnline;

/// <summary>请求本机联机助手切换到一个已保存的房间档案。</summary>
public sealed class SwitchAssistantRoomTask : ISoloTask
{
    public const string TaskName = "切换助手房间";
    private const string TargetRoomName = "targetRoomName";
    private readonly string? _targetRoomName;

    public SwitchAssistantRoomTask(Dictionary<string, object?>? settings)
    {
        if (settings?.TryGetValue(TargetRoomName, out var value) != true) return;
        _targetRoomName = (value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            Newtonsoft.Json.Linq.JValue { Value: string text } => text,
            _ => null,
        })?.Trim();
    }

    public string Name => TaskName;

    public Task Start(CancellationToken ct) => ConfigurationGroupRoomActions.SwitchAsync(_targetRoomName, ct);

    public static List<SoloTaskSettingItem> GetSettingDefinitions() =>
    [
        new() { Name = TargetRoomName, Label = "目标房间名称（必填，须与助手内已保存的名称一致）", DefaultValue = "" },
    ];
}
