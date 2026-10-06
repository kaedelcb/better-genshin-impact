using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 槲寄生 · 任务中心——流程文档模型（mistletoe.workflow schemaVersion 1 的消费方）。
/// 上游合同：Test/OneDragonMigration/README.md、总计划 §3.1a、R4 施工分解 D3/D9/D11。
///
/// 扩展纪律（锚点 6「加类型不改序列化框架」）：
/// - 节点/策略/触发器/终止动作均为 kind 字符串 + 扁平参数（[JsonExtensionData] 全量保留往返），
///   新增类型不需要改序列化框架；
/// - 未知字段一律经 ExtensionData 原样往返，不丢用户数据；
/// - 未知 kind 由 <see cref="WorkflowKindCatalog.FindUnsupportedKinds"/> 检出：
///   文档可加载、可预览，但阻止执行（D3 三级未知处理第二级）；
/// - schema/schemaVersion 不匹配由 WorkflowStore 在加载前按 DOM 判型隔离（第一/三级）。
///
/// v1 语义映射（R4 分解 D9）：nodes[] 数组 = 顺序链；顶层 loop = 结构性循环（非节点策略）；
/// 顶层 triggers/terminal 作用于隐式流程根；节点 strategies[] 维持各自作用域；
/// condition.weekdays 是过滤语义（不命中跳过该资源，不阻塞后续节点）。
/// </summary>
public sealed class WorkflowDocument
{
    /// <summary>固定 "mistletoe.workflow"。</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = WorkflowDocumentSchema.Name;

    /// <summary>当前仅支持 1；其他版本由 Store 判型隔离，不进本模型。</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = WorkflowDocumentSchema.Version;

    /// <summary>
    /// 稳定流程身份（D11）：由 WorkflowStore 首次保存时指派（"wf-" + 8 位十六进制），
    /// 之后跨改名/改内容不变。不靠文件名或重算 nodeId 维持身份。
    /// 加法字段：R1 dry-run 候选不含此字段，预览路径只读不补写。
    /// </summary>
    [JsonPropertyName("workflowId")]
    public string? WorkflowId { get; set; }

    /// <summary>流程名（C01 多计划管理：显示与选择用，非身份）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>资源节点链（顺序即执行顺序，C02/C05）。</summary>
    [JsonPropertyName("nodes")]
    public List<WorkflowNode> Nodes { get; set; } = [];

    /// <summary>流程级触发器（作用于隐式流程根，C07）。</summary>
    [JsonPropertyName("triggers")]
    public List<WorkflowTrigger> Triggers { get; set; } = [];

    /// <summary>结构性循环（C08；无则单次执行）。</summary>
    [JsonPropertyName("loop")]
    public WorkflowLoop? Loop { get; set; }

    /// <summary>流程终止动作（C17；仅流程成功边界触发）。</summary>
    [JsonPropertyName("terminal")]
    public List<WorkflowTerminalAction> Terminal { get; set; } = [];

    /// <summary>执行选项（含单配置收尾抑制，R4 分解 D10）。</summary>
    [JsonPropertyName("execution")]
    public WorkflowExecutionOptions? Execution { get; set; }

    /// <summary>激活状态（D13 三态：candidate-ready 只可预览）。</summary>
    [JsonPropertyName("activation")]
    public WorkflowActivation? Activation { get; set; }

    /// <summary>未识别字段袋（原样往返，入报告不静默丢弃）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public static class WorkflowDocumentSchema
{
    public const string Name = "mistletoe.workflow";
    public const int Version = 1;
}

/// <summary>资源节点：流程只持类型化引用（稳定标识 + 配置修订号），不复制配置内容（锚点 1）。</summary>
public sealed class WorkflowNode
{
    /// <summary>节点身份（R1 为内容哈希派生，跨进程稳定；运行身份另加出现位置/轮次/尝试，见 RunStore）。</summary>
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = "";

    /// <summary>节点类型键（见 <see cref="WorkflowKindCatalog.NodeKinds"/>）。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>类型化资源引用。</summary>
    [JsonPropertyName("ref")]
    public WorkflowResourceRef? Ref { get; set; }

    /// <summary>策略修饰实例（锚点 4：策略即节点修饰；同一策略实现、独立实例）。</summary>
    [JsonPropertyName("strategies")]
    public List<WorkflowStrategy> Strategies { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>类型化资源引用：稳定标识 + 配置修订号，不复制配置内容。</summary>
public sealed class WorkflowResourceRef
{
    /// <summary>配置名（显示/解析辅助）。</summary>
    [JsonPropertyName("config")]
    public string? Config { get; set; }

    /// <summary>
    /// 迁移映射键（Name#来源哈希，R1 产物）。注意：这是迁移映射键，不是永久资源 ID（D14），
    /// 执行期解析以 describe 返回的稳定标识为准。
    /// </summary>
    [JsonPropertyName("configKey")]
    public string? ConfigKey { get; set; }

    /// <summary>配置修订号（起步快照防 config_changed 错跑，锚点 2）。</summary>
    [JsonPropertyName("revision")]
    public string? Revision { get; set; }

    /// <summary>单项任务资源的稳定 taskId（resource.singleTask 用；v1 R1 不产出，加法预留）。</summary>
    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>策略修饰实例：kind + 扁平参数（参数全量经 ExtensionData 往返，新增策略不改框架）。</summary>
public sealed class WorkflowStrategy
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>策略参数袋（kind 之外的全部字段，原样往返）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Params { get; set; }

    /// <summary>读取字符串参数（不存在/类型不符返回 null）。</summary>
    public string? GetString(string key)
        => Params is not null && Params.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>读取布尔参数（不存在/类型不符返回 null）。</summary>
    public bool? GetBool(string key)
        => Params is not null && Params.TryGetValue(key, out var v) &&
           (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    /// <summary>读取字符串数组参数（不存在/形状不符返回 null）。</summary>
    public IReadOnlyList<string>? GetStringArray(string key)
    {
        if (Params is null || !Params.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            list.Add(item.GetString()!);
        }
        return list;
    }

    /// <summary>读取 int32 参数（不存在/类型不符/越界返回 null——R5.4 机制一 `schedule.priority` 用；加法读取器，不动序列化框架）。</summary>
    public int? GetInt(string key)
        => Params is not null && Params.TryGetValue(key, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : null;
}

/// <summary>流程触发器：kind + 扁平参数（trigger.time 等；missPolicy 必须明确，§7.3 时间合同）。</summary>
public sealed class WorkflowTrigger
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Params { get; set; }

    public string? GetString(string key)
        => Params is not null && Params.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>终止动作：kind + 扁平参数（仅流程成功边界触发；停止/失败/未知不触发，D10）。</summary>
public sealed class WorkflowTerminalAction
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Params { get; set; }

    public string? GetString(string key)
        => Params is not null && Params.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>结构性循环（C08）：mode + 扁平参数。业务循环仅由退出条件控制，不设轮次封顶（D9）。</summary>
public sealed class WorkflowLoop
{
    /// <summary>循环模式（scheduled=到点开始每轮 / immediate=立即接续下一轮）。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Params { get; set; }

    public string? GetString(string key)
        => Params is not null && Params.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public bool? GetBool(string key)
        => Params is not null && Params.TryGetValue(key, out var v) &&
           (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}

/// <summary>执行选项（D10 收尾抑制合同的流程侧表达）。</summary>
public sealed class WorkflowExecutionOptions
{
    /// <summary>抑制单配置收尾：流程引用整龙/配置时，其原生 CompletionAction 不在配置边界执行。</summary>
    [JsonPropertyName("suppressConfigCompletionAction")]
    public bool? SuppressConfigCompletionAction { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>激活状态（D13）：candidate-ready = dry-run 候选，只可预览，不代表可生产执行。</summary>
public sealed class WorkflowActivation
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 已知类型目录（锚点 6 开放集合的当前注册表）。
/// 新增能力 = 在此登记 + Planner/策略层一个分支，不改序列化框架。
/// </summary>
public static class WorkflowKindCatalog
{
    public static readonly IReadOnlySet<string> NodeKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "resource.oneDragonConfig",
        "resource.configGroup",
        "resource.singleTask",
    };

    public static readonly IReadOnlySet<string> StrategyKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "condition.weekdays",
        "prerequisite.account",
        "prerequisite.redeemCode",
        // R5.4 机制一：节点级优先级修饰（int32 `priority`，数值大者优先，缺省 0；§2③1/§2③3）。
        "schedule.priority",
        "schedule.time",
    };

    public static readonly IReadOnlySet<string> TriggerKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "trigger.time",
        // R5.4 机制二：时间固定型到点（结构性层级＝Fixed；missPolicy 默认 skip+留痕，§2②6）。
        "trigger.timeFixed",
        // R5.4 机制三：灵活型窗口触发（结构性层级＝Plan；窗口内取得执行权即锁定本轮，可越窗完成，§2③6）。
        "trigger.timeFlexible",
    };

    public static readonly IReadOnlySet<string> TerminalKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "terminal.completionAction",
    };

    public static readonly IReadOnlySet<string> LoopModes = new HashSet<string>(StringComparer.Ordinal)
    {
        "scheduled",
        "immediate",
    };

    /// <summary>
    /// **已登记但尚未接线执行**的 kind（会诊整改：**目录登记 ≠ 可执行**）。
    /// 登记只用于解析/往返与**可检出**；引擎消费接线落地前，`FindUnsupportedKinds` **仍须检出并阻止执行**——
    /// 否则当前执行器会**静默忽略新语义**（例如「到点/窗口/优先级」被当噪声跳过）＝fail-open。
    /// 接线完成并有夹具验证后，从此集合移除（同步 §19 登记）。
    /// </summary>
    public static readonly IReadOnlySet<string> RegisteredNotExecutable = new HashSet<string>(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// 检出**不可执行**的类型键（D3 第二级：可加载可预览，阻止执行）——包含两类：
    /// ①完全未登记的 kind；②**已登记但尚未接线执行**的 kind（见 <see cref="RegisteredNotExecutable"/>）。
    /// 返回稳定排序的 kind 列表（含来源前缀，如 "node:resource.x"）。
    /// </summary>
    public static IReadOnlyList<string> FindUnsupportedKinds(WorkflowDocument doc)
    {
        var bad = new List<string>();
        foreach (var n in doc.Nodes)
        {
            if (!NodeKinds.Contains(n.Kind)) bad.Add("node:" + n.Kind);
            foreach (var s in n.Strategies)
            {
                if (!StrategyKinds.Contains(s.Kind)) bad.Add("strategy:" + s.Kind);
                else if (RegisteredNotExecutable.Contains("strategy:" + s.Kind)) bad.Add("strategy:" + s.Kind);
            }
        }
        foreach (var t in doc.Triggers)
        {
            if (!TriggerKinds.Contains(t.Kind)) bad.Add("trigger:" + t.Kind);
            else if (RegisteredNotExecutable.Contains("trigger:" + t.Kind)) bad.Add("trigger:" + t.Kind);
        }
        foreach (var t in doc.Terminal)
            if (!TerminalKinds.Contains(t.Kind)) bad.Add("terminal:" + t.Kind);
        if (doc.Loop is not null && !LoopModes.Contains(doc.Loop.Mode)) bad.Add("loop:" + doc.Loop.Mode);
        bad.Sort(StringComparer.Ordinal);
        return bad;
    }
}
