using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 锄地数据统计模型（嘟嘟可 P3 / F5-B）。
/// 由 HoeingStatsService 从 BGI 日志流配对提取（语料见设计文档 §2.2），
/// 批次结束时追加写 log/dodoco_stats.{yyyy-MM-dd}.jsonl。
/// </summary>

/// <summary>一条路线的耗时记录（路线级配对：开始 ↔ 脚本执行结束/出错中断）。</summary>
public class RouteStat
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>所属配置组名（可空：无法归属时）。</summary>
    [JsonPropertyName("group")] public string? GroupName { get; set; }
    [JsonPropertyName("start")] public DateTime StartTime { get; set; }
    [JsonPropertyName("end")] public DateTime? EndTime { get; set; }
    /// <summary>是否中断（出现"执行路线 X 出错"而无正常结束）。</summary>
    [JsonPropertyName("interrupted")] public bool Interrupted { get; set; }

    [JsonIgnore] public double? DurationSeconds => EndTime != null ? (EndTime.Value - StartTime).TotalSeconds : null;
}

/// <summary>一个配置组的运行记录（组级：启动 ↔ 下一组启动/取消/批次结束）。</summary>
public class GroupStat
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("start")] public DateTime StartTime { get; set; }
    [JsonPropertyName("end")] public DateTime? EndTime { get; set; }

    [JsonIgnore] public double? DurationSeconds => EndTime != null ? (EndTime.Value - StartTime).TotalSeconds : null;
}

/// <summary>一次联机锄地会话（人齐开始 ↔ 联机退出，含退出原因）。</summary>
public class MultiplayerSessionStat
{
    [JsonPropertyName("playerCount")] public int? PlayerCount { get; set; }
    [JsonPropertyName("start")] public DateTime StartTime { get; set; }
    [JsonPropertyName("end")] public DateTime? EndTime { get; set; }
    [JsonPropertyName("exitReason")] public string? ExitReason { get; set; }

    [JsonIgnore] public double? DurationSeconds => EndTime != null ? (EndTime.Value - StartTime).TotalSeconds : null;
}

/// <summary>收益预估（"路线组合结果：精英 E, 小怪 M, 收益 G 摩拉, 预计用时 ..."，P5 报表用）。</summary>
public class ProfitEstimate
{
    [JsonPropertyName("elite")] public int Elite { get; set; }
    [JsonPropertyName("mobs")] public int Mobs { get; set; }
    [JsonPropertyName("mora")] public int Mora { get; set; }
    [JsonPropertyName("etaText")] public string EtaText { get; set; } = "";
    [JsonPropertyName("time")] public DateTime Time { get; set; }
}

/// <summary>
/// 一次联机锄地批次：从组启动到结束/取消的一个会话。
/// 批次键优先取联机上线 generation，拿不到用时间聚类（组间间隔 >10 分钟视为新批次）。
/// </summary>
public class HoeingBatchRecord
{
    [JsonPropertyName("batchId")] public string BatchId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>联机上线代序号（拿到时为批次键；null=时间聚类）。</summary>
    [JsonPropertyName("generation")] public int? Generation { get; set; }
    [JsonPropertyName("startTime")] public DateTime StartTime { get; set; }
    [JsonPropertyName("endTime")] public DateTime? EndTime { get; set; }
    /// <summary>批次结束原因（任务被取消 / 联机退出原因 / 空闲超时收尾）。</summary>
    [JsonPropertyName("endReason")] public string? EndReason { get; set; }
    [JsonPropertyName("groups")] public List<GroupStat> Groups { get; set; } = [];
    [JsonPropertyName("routes")] public List<RouteStat> Routes { get; set; } = [];
    [JsonPropertyName("sessions")] public List<MultiplayerSessionStat> Sessions { get; set; } = [];
    /// <summary>批次内最近一次收益预估（兼容旧 JSONL；新数据请读 Estimates）。</summary>
    [JsonPropertyName("estimate")] public ProfitEstimate? Estimate { get; set; }
    /// <summary>批次内全部收益预估（P5 报表用：每条路线选择时各记一条）。</summary>
    [JsonPropertyName("estimates")] public List<ProfitEstimate> Estimates { get; set; } = [];
    /// <summary>中断路线数（出现"出错"而无正常结束）。</summary>
    [JsonPropertyName("interruptedCount")] public int InterruptedCount { get; set; }

    [JsonIgnore] public double? DurationSeconds => EndTime != null ? (EndTime.Value - StartTime).TotalSeconds : null;
}
