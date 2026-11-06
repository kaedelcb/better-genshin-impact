using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>与流程宿主无关的寄存器参数。R1–R10 是稳定身份，名称只用于显示。</summary>
public static class RegisterBank
{
    public const int Count = 10;
    public const int MaxDelaySeconds = 31 * 24 * 60 * 60;
    public const string Never = "none";
    public const string Daily = "daily";
    public const string AfterChange = "afterChange";

    public static List<int?> EmptySelection() => Enumerable.Repeat<int?>(null, Count).ToList();

    public static void ValidateSelection(IReadOnlyList<int?>? values)
    {
        if (values is null || values.Count != Count || values.Any(v => v is not null and not 0 and not 1))
            throw new ArgumentException("寄存器参数必须包含10位，每位只能保持/忽略、0或1。");
        if (values.All(v => v is null)) throw new ArgumentException("请至少选择一位寄存器。");
    }
}

public sealed class RegisterWriteSettings
{
    [JsonPropertyName("values")]
    public List<int?> Values { get; set; } = RegisterBank.EmptySelection();
}

/// <summary>归零只改状态。全部归零必须明确 All=true，空参数不能被解释为清空全部。</summary>
public sealed record RegisterResetRequest(int? Number = null, bool All = false);

/// <summary>明确设置单个位。缺失值使用非法默认值，不能被解释为归零。</summary>
public sealed record RegisterSetRequest(int Number, int Value = -1);

public sealed class RegisterConditionSettings
{
    [JsonPropertyName("values")]
    public List<int?> Values { get; set; } = RegisterBank.EmptySelection();
    [JsonPropertyName("match")]
    public string Match { get; set; } = "all";
}

public sealed class RegisterEntry
{
    [JsonPropertyName("number")]
    public int Number { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("value")]
    public int Value { get; set; }
    [JsonPropertyName("resetMode")]
    public string ResetMode { get; set; } = RegisterBank.Never;
    [JsonPropertyName("resetHour")]
    public int ResetHour { get; set; } = 4;
    [JsonPropertyName("resetMinute")]
    public int ResetMinute { get; set; }
    [JsonPropertyName("delaySeconds")]
    public int DelaySeconds { get; set; } = 1800;
    [JsonPropertyName("changedAtUtc")]
    public DateTimeOffset? ChangedAtUtc { get; set; }
    [JsonPropertyName("nextResetAtUtc")]
    public DateTimeOffset? NextResetAtUtc { get; set; }
    [JsonPropertyName("settingsRevision")]
    public string SettingsRevision { get; set; } = "initial";

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Name) ? $"R{Number}" : $"R{Number} · {Name}";
}

public sealed class RegisterBankSnapshot
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
    [JsonPropertyName("entries")]
    public List<RegisterEntry> Entries { get; set; } = [];
}

public sealed class RegisterConfigurationChange
{
    [JsonPropertyName("number")]
    public int Number { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("resetMode")]
    public string ResetMode { get; set; } = RegisterBank.Never;
    [JsonPropertyName("resetHour")]
    public int ResetHour { get; set; } = 4;
    [JsonPropertyName("resetMinute")]
    public int ResetMinute { get; set; }
    [JsonPropertyName("delaySeconds")]
    public int DelaySeconds { get; set; } = 1800;
    [JsonPropertyName("expectedSettingsRevision")]
    public string ExpectedSettingsRevision { get; set; } = "initial";
}
