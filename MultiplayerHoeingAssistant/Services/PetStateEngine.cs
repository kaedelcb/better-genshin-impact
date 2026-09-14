namespace MultiplayerHoeingAssistant.Services;

/// <summary>任务类型分类（按 任务名/配置组名 关键词；驱动桌宠任务专属表情）。</summary>
public enum PetTaskKind
{
    /// <summary>联机/单机锄地类（锄地、传奇、次数盾、精英、小怪）。</summary>
    Hoeing,
    /// <summary>圣遗物/狗粮类（圣遗物、狗粮、调查、分解、强化）。</summary>
    Artifact,
    /// <summary>好感类 JS 任务。</summary>
    Affection,
    /// <summary>采集类（采集、矿、特产、食材、钓鱼等）。</summary>
    Gather,
    /// <summary>其他任务（通用执行表情兜底）。</summary>
    Other
}

/// <summary>桌宠语义状态（基础态；爆发态由导演层按事件时间窗叠加，不进本枚举）。</summary>
public enum PetState
{
    /// <summary>BGI 未运行。</summary>
    Sleeping,
    /// <summary>空闲（喝茶）。</summary>
    Idle,
    /// <summary>定时上线待命。</summary>
    ScheduledWaiting,
    /// <summary>已上线待开锄。</summary>
    ReadyOnline,
    /// <summary>锄地执行中。</summary>
    Hoeing,
    /// <summary>狗粮类任务执行中。</summary>
    WorkingArtifact,
    /// <summary>好感任务执行中。</summary>
    WorkingAffection,
    /// <summary>采集任务执行中。</summary>
    WorkingGather,
    /// <summary>其他/未分类任务执行中（不占用三类工作表情——严格绑定对应任务类型）。</summary>
    WorkingOther
}

/// <summary>上线 chip 的三态词。</summary>
public enum PetOnlinePhase
{
    /// <summary>未上线。</summary>
    NotReady,
    /// <summary>已上线（待开锄）。</summary>
    Ready,
    /// <summary>已联机（开锄中）。</summary>
    Connected
}

/// <summary>状态判定输入快照（纯数据，PBT 友好）。</summary>
public readonly record struct PetFacts(
    bool BgiAlive,
    bool TaskRunning,
    PetTaskKind TaskKind,
    bool OnlineReady,
    bool HoeingRunning,
    bool HasScheduled);

/// <summary>
/// 奥黛塔状态机纯函数层：基础状态判定 / 任务类型分类 / 上线信息 chip 组装。
/// 并发契约：调用方保证在 UI 线程串行调用（PetViewModel 统一经 Dispatcher 汇聚输入）。
/// </summary>
public static class PetStateEngine
{
    // —— 关键词表（小写包含匹配；顺序即优先级：好感 > 锄地 > 狗粮 > 采集）——
    public static readonly string[] AffectionKeywords = ["好感", "邀约"];
    public static readonly string[] HoeingKeywords = ["锄地", "传奇", "次数盾", "精英", "小怪"];
    public static readonly string[] ArtifactKeywords = ["圣遗物", "狗粮", "调查", "分解", "强化"];
    public static readonly string[] GatherKeywords = ["采集", "矿物", "挖矿", "特产", "食材", "钓鱼", "捕捉", "晶蝶"];

    /// <summary>任务类型分类。autoHoeingRunning 优先（联机锄地是权威信号）；其余按关键词；
    /// 全不命中归 Other（通用执行表情）。text 为 null/空不参与匹配。</summary>
    public static PetTaskKind ClassifyTask(bool autoHoeingRunning, string? taskName, string? groupName)
    {
        if (autoHoeingRunning) return PetTaskKind.Hoeing;
        var text = (groupName ?? "") + "\n" + (taskName ?? "");
        return ContainsAny(text, AffectionKeywords) ? PetTaskKind.Affection
             : ContainsAny(text, HoeingKeywords) ? PetTaskKind.Hoeing
             : ContainsAny(text, ArtifactKeywords) ? PetTaskKind.Artifact
             : ContainsAny(text, GatherKeywords) ? PetTaskKind.Gather
             : PetTaskKind.Other;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var k in keywords)
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>基础状态判定。优先级：睡觉 &gt; 任务执行（按类型）&gt; 待开锄 &gt; 定时待命 &gt; 空闲。</summary>
    public static PetState ResolveBase(in PetFacts f)
    {
        if (!f.BgiAlive) return PetState.Sleeping;
        if (f.TaskRunning) return f.TaskKind switch
        {
            PetTaskKind.Hoeing => PetState.Hoeing,
            PetTaskKind.Artifact => PetState.WorkingArtifact,
            PetTaskKind.Affection => PetState.WorkingAffection,
            PetTaskKind.Gather => PetState.WorkingGather,
            _ => PetState.WorkingOther
        };
        if (f.OnlineReady) return PetState.ReadyOnline;
        if (f.HasScheduled) return PetState.ScheduledWaiting;
        return PetState.Idle;
    }

    /// <summary>基础状态 → 动画 key（对应 Anims/ 目录名）。</summary>
    public static string ToAnimKey(PetState state) => state switch
    {
        PetState.Sleeping => "sleep",
        PetState.Idle => "tea",
        PetState.ScheduledWaiting => "tea",
        PetState.ReadyOnline => "interact",
        PetState.Hoeing => "act_hoeing",
        PetState.WorkingArtifact => "act_artifact",
        PetState.WorkingAffection => "shy",
        // 采集/通用任务执行必须显式映射（曾落入 _ => "tea"：任务运行显示喝茶空闲表情，
        // 光晕/节奏的状态判定也跟着失配——"只有锄地时有光晕"的根因）
        PetState.WorkingGather => "act_gather",
        // 其他/未分类任务：中性互动表情——三类工作表情严格绑定对应任务类型，不外借
        PetState.WorkingOther => "interact",
        _ => "tea"
    };

    /// <summary>
    /// 状态轮换池：导演层按停留时长在池内循环切换（pool[0] 为主片，重复项=提高出现权重）。
    /// 设计原则：①主片占半，保证状态一眼可读；②备片选语义可信的情绪切镜（干活中偶尔得意/开心）；
    /// ③全部 15 套素材均在表内使用（哀=告警持续尾，见 PetViewModel）。
    /// 光晕/呼吸节奏挂语义状态标志，轮换到备片期间不丢。
    /// </summary>
    public static string[] GetRotationPool(PetState state) => state switch
    {
        PetState.Sleeping => ["sleep"],
        PetState.Idle or PetState.ScheduledWaiting
            => ["tea", "interact", "tea", "joy"],
        PetState.ReadyOnline
            => ["interact", "joy", "interact", "smug"],
        PetState.Hoeing
            => ["act_hoeing", "smug", "act_hoeing", "joy"],
        PetState.WorkingArtifact
            => ["act_artifact", "joy", "act_artifact", "smug"],
        PetState.WorkingAffection
            => ["shy", "smug", "shy", "joy"],
        PetState.WorkingGather
            => ["act_gather", "joy", "act_gather", "smug"],
        PetState.WorkingOther
            => ["interact", "joy", "interact", "confused"],
        _ => ["tea"]
    };

    /// <summary>
    /// 上线信息 chip 三段拆分（桌宠下方信息条带图标用）：{定时时间|**:**} / {已上线}/{预期} / 三态词。
    /// 定时时间空 → "**:**" 占位（固定三段式，宽度稳定）。
    /// </summary>
    public static (string Time, string Count, string Phase) ComposeOnlineChipParts(
        string? scheduledTime, int readyCount, int expected, PetOnlinePhase phase)
    {
        var time = string.IsNullOrWhiteSpace(scheduledTime) ? "**:**" : scheduledTime!.Trim();
        var expectedClamped = Math.Max(expected, readyCount);
        var phaseText = phase switch
        {
            PetOnlinePhase.Connected => "已联机",
            PetOnlinePhase.Ready => "已上线",
            _ => "未上线"
        };
        return (time, $"{readyCount}/{expectedClamped}", phaseText);
    }

    /// <summary>上线信息 chip 纯文本（面板"上线信息"行用）。</summary>
    public static string ComposeOnlineChip(string? scheduledTime, int readyCount, int expected, PetOnlinePhase phase)
    {
        var (time, count, phaseText) = ComposeOnlineChipParts(scheduledTime, readyCount, expected, phase);
        return $"{time} · {count} · {phaseText}";
    }

    /// <summary>状态说明文案（tooltip）。</summary>
    public static string Describe(PetState state) => state switch
    {
        PetState.Sleeping => "BGI 没在运行，奥黛塔睡着了…",
        PetState.ReadyOnline => "已上线，等待开锄",
        PetState.Hoeing => "联机锄地中…",
        PetState.WorkingArtifact => "收集狗粮中…",
        PetState.WorkingAffection => "好感任务进行中…",
        PetState.ScheduledWaiting => "定时上线待命中",
        _ => "待机中"
    };

    /// <summary>爆发态动画 key（导演层时间窗内覆盖基础态显示）。</summary>
    public static string BurstToAnimKey(string burstKey) => burstKey switch
    {
        "celebrate" => "joy",
        "cancel" => "disdain",
        "drag" => "shock",
        "hover" => "confused",
        "online" => "smug",
        "alertHeavy" => "anger",
        _ => "shock" // alert
    };

    /// <summary>
    /// 从日志行提取好感任务轮次："第3/10轮" 或 "轮次:3/10"。无匹配 → null。
    /// 时间不从日志猜（不可靠），由任务开始时刻本地计时。
    /// </summary>
    public static (int Cur, int Total)? ParseAffectionRound(string? logLine)
    {
        if (string.IsNullOrEmpty(logLine)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(logLine, @"第\s*(\d+)\s*/\s*(\d+)\s*轮");
        if (!m.Success)
            m = System.Text.RegularExpressions.Regex.Match(logLine, @"轮次[:：]?\s*(\d+)\s*/\s*(\d+)");
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var cur) || !int.TryParse(m.Groups[2].Value, out var total))
            return null;
        if (cur <= 0 || total <= 0 || cur > total) return null;
        return (cur, total);
    }
}
