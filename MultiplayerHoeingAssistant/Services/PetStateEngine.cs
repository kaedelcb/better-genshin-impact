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

    /// <summary>轮换池单条目：动画 key + 出现权重。</summary>
    public sealed record RotationEntry(string Key, int Weight);

    /// <summary>
    /// 状态轮换加权池：主片（首条目）约七成权重保证任务专属表情一眼可读，其余分给语义可信的情绪备片，
    /// 每拍加权随机抽取（不再固定顺序循环）。禁入片不进池：
    /// sleep=睡觉专属；sorrow/anger=告警持续尾专属（PetViewModel）；shock=惊吓爆发专属。
    /// 全部 15 套素材仍均在表内或爆发/告警位使用。
    /// 光晕/呼吸节奏挂语义状态标志，轮换到备片期间不丢。
    /// </summary>
    public static readonly IReadOnlyDictionary<PetState, RotationEntry[]> RotationPools = new Dictionary<PetState, RotationEntry[]>
    {
        [PetState.Sleeping] = [new("sleep", 1)],
        [PetState.Idle] = [new("tea", 70), new("interact", 10), new("joy", 8), new("helpless", 7), new("confused", 5)],
        [PetState.ScheduledWaiting] = [new("tea", 70), new("interact", 10), new("joy", 8), new("helpless", 7), new("confused", 5)],
        [PetState.ReadyOnline] = [new("interact", 70), new("joy", 10), new("smug", 8), new("shy", 7), new("confused", 5)],
        [PetState.Hoeing] = [new("act_hoeing", 70), new("smug", 8), new("joy", 8), new("interact", 5), new("helpless", 4), new("disdain", 3), new("confused", 2)],
        [PetState.WorkingArtifact] = [new("act_artifact", 70), new("joy", 8), new("smug", 8), new("interact", 5), new("helpless", 4), new("disdain", 3), new("confused", 2)],
        [PetState.WorkingAffection] = [new("shy", 70), new("smug", 8), new("joy", 8), new("interact", 5), new("helpless", 4), new("disdain", 3), new("confused", 2)],
        [PetState.WorkingGather] = [new("act_gather", 70), new("joy", 8), new("smug", 8), new("interact", 5), new("helpless", 4), new("disdain", 3), new("confused", 2)],
        [PetState.WorkingOther] = [new("interact", 70), new("joy", 8), new("smug", 8), new("helpless", 5), new("confused", 5), new("shy", 4)],
    };

    /// <summary>
    /// 加权随机取一拍轮换表情。previous 为上一拍 key：上一拍是备片时本拍强制回主片
    /// （备片永不连续，任务专属表情稳定过半）；池缺失时回退 Idle 池。
    /// 调用方保证 rng 非空且状态池存在主片。
    /// </summary>
    public static string PickRotationKey(PetState state, string? previous, Random rng)
    {
        var pool = RotationPools.TryGetValue(state, out var entries) ? entries : RotationPools[PetState.Idle];
        var main = pool[0].Key;
        var candidates = previous == null || previous == main
            ? pool
            : [pool[0]];
        var roll = rng.Next(candidates.Sum(e => e.Weight));
        foreach (var e in candidates)
        {
            roll -= e.Weight;
            if (roll < 0) return e.Key;
        }
        return candidates[^1].Key;
    }

    /// <summary>
    /// 上线信息 chip 三段拆分（桌宠下方信息条带图标用）：{定时时间|**:**} / {人数} / 三态词。
    /// 人数段：已联机（开锄中）且 SignalR 房间人数&gt;0 时显示 {SignalR人数}/{开锄人数}（实时房间人头）；
    /// 其余状态（或旧 BGI 无此字段）显示 {已上线人数}/{预期}。锄地结束 phase 离开 Connected 即自动回落。
    /// 定时时间空 → "**:**" 占位（固定三段式，宽度稳定）。
    /// </summary>
    public static (string Time, string Count, string Phase) ComposeOnlineChipParts(
        string? scheduledTime, int readyCount, int expected, PetOnlinePhase phase, int roomPlayerCount = 0)
    {
        var time = string.IsNullOrWhiteSpace(scheduledTime) ? "**:**" : scheduledTime!.Trim();
        var expectedClamped = phase == PetOnlinePhase.Connected && roomPlayerCount > 0
            ? Math.Max(expected, roomPlayerCount)
            : Math.Max(expected, readyCount);
        var numerator = phase == PetOnlinePhase.Connected && roomPlayerCount > 0 ? roomPlayerCount : readyCount;
        var phaseText = phase switch
        {
            PetOnlinePhase.Connected => "已联机",
            PetOnlinePhase.Ready => "已上线",
            _ => "未上线"
        };
        return (time, $"{numerator}/{expectedClamped}", phaseText);
    }

    /// <summary>上线信息 chip 纯文本（面板"上线信息"行用）。</summary>
    public static string ComposeOnlineChip(string? scheduledTime, int readyCount, int expected, PetOnlinePhase phase,
        int roomPlayerCount = 0)
    {
        var (time, count, phaseText) = ComposeOnlineChipParts(scheduledTime, readyCount, expected, phase, roomPlayerCount);
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
