using System.Collections.ObjectModel;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 流程编辑草稿（R4.8 §4.7 保留式修改，一轮 I4/I6）：
/// 持有 LoadSnapshot 的序列化往返深拷贝（Draft），UI 只改触碰字段——未知字段/多触发器/多策略/loop 参数/
/// activation 元数据原样保留；uid/bindingCode 三分（原值驻 Draft / 掩码展示 / 编辑框空=不修改，掩码不落盘）。
/// </summary>
public sealed class WorkflowEditVm : ViewModelBase
{
    private static readonly string[] WeekdayNames = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
    private static readonly string[] LoopModes = ["", "scheduled", "immediate"]; // 下拉：无循环/到点开始每轮/立即接续
    private static readonly string[] TerminalActions = ["", "closeGame", "closeSoftware", "closeGameAndSoftware", "shutdown"];

    /// <summary>Loop 下拉「保留自定义（不修改）」项索引（二轮 阻断1：不可映射的自定义 mode 绝不回写）。</summary>
    private const int LoopPreserveIndex = 3;
    /// <summary>收尾下拉「保留自定义（不修改）」项索引（二轮 阻断1：未知 action/自定义 kind 绝不回写）。</summary>
    private const int TerminalPreserveIndex = 5;

    // 字段级基线（二轮 阻断1：ApplyToDraft 只回写相对基线发生变化的字段，未触碰字段原样保留）
    private readonly string _origName;
    private readonly string _origTriggerTime;
    private readonly string _origTriggerUntil;
    private readonly int _origTriggerMode;
    private readonly bool _origMissNextDay;
    private readonly int _origLoopModeIndex;
    private readonly string _origLoopTime;
    private readonly string _origLoopDeadline;
    private string _loopTimeText;
    private string _loopDeadlineText;
    public string LoopTimeText { get => _loopTimeText; set => SetProperty(ref _loopTimeText, value); }
    public string LoopDeadlineText { get => _loopDeadlineText; set => SetProperty(ref _loopDeadlineText, value); }
    private readonly int _origTerminalActionIndex;

    internal WorkflowEditVm(WorkflowDocument draft, string baseRevision, ResourceCatalogService catalog)
    {
        Draft = draft;
        BaseRevision = baseRevision;
        _origName = draft.Name;
        _nameText = draft.Name;
        _origTriggerTime = ReadTriggerTime(draft);
        _triggerTimeText = _origTriggerTime;
        _triggerEditable = draft.Triggers.Count <= 1
            && (draft.Triggers.Count == 0 || draft.Triggers[0].Kind is "trigger.time" or "trigger.timeFixed" or "trigger.timeFlexible");
        var trigger = draft.Triggers.Count == 1 ? draft.Triggers[0] : null;
        _origTriggerMode = _triggerModeIndex = trigger?.Kind == "trigger.timeFixed" ? 1 : trigger?.Kind == "trigger.timeFlexible" ? 2 : 0;
        _origTriggerUntil = _triggerUntilText = trigger?.GetString("until") ?? "";
        _origMissNextDay = _missNextDay = (trigger?.GetString("missPolicy") ?? (_triggerModeIndex == 0 ? "nextDay" : "skip")) == "nextDay";
        _loopCustomPreserved = draft.Loop is not null && draft.Loop.Mode is not ("scheduled" or "immediate");
        _loopModeIndex = draft.Loop is null ? 0 : draft.Loop.Mode == "scheduled" ? 1 : draft.Loop.Mode == "immediate" ? 2 : LoopPreserveIndex;
        _origLoopModeIndex = _loopModeIndex;
        _origLoopTime = _loopTimeText = draft.Loop?.GetString("time") ?? "";
        _origLoopDeadline = _loopDeadlineText = draft.Loop?.GetString("deadline") ?? "";
        _terminalActionIndex = draft.Terminal.Count == 0 ? 0
            : (draft.Terminal[0].Kind == "terminal.completionAction"
               && Array.IndexOf(TerminalActions, draft.Terminal[0].GetString("action")) is var ai && ai > 0) ? ai : TerminalPreserveIndex;
        _origTerminalActionIndex = _terminalActionIndex;
        foreach (var node in draft.Nodes)
            Nodes.Add(new NodeEditVm(node));
        Renumber(); // 二轮（建议1）：初始序号 1..n（此前全部显示 0）
        RefreshCatalog(catalog);
    }

    internal WorkflowDocument Draft { get; }
    internal string BaseRevision { get; }

    public string Title => $"流程编辑 · {Draft.Name}";

    // ---- 名称 ----
    private string _nameText;
    public string NameText { get => _nameText; set => SetProperty(ref _nameText, value); }

    // ---- 触发器（trigger.time HH:mm；空=无触发器；多触发器/自定义 kind 保留不在此编辑，一轮 I4） ----
    private string _triggerTimeText;
    private readonly bool _triggerEditable;
    public string TriggerTimeText { get => _triggerTimeText; set => SetProperty(ref _triggerTimeText, value); }
    public bool TriggerEditable => _triggerEditable;
    private int _triggerModeIndex;
    public int TriggerModeIndex
    {
        get => _triggerModeIndex;
        set
        {
            if (value is < 0 or > 2) return;
            if (SetProperty(ref _triggerModeIndex, value))
            {
                if (value != _origTriggerMode) MissNextDay = value == 0;
                OnPropertyChanged(nameof(IsFlexibleTrigger));
            }
        }
    }
    public bool IsFlexibleTrigger => _triggerEditable && TriggerModeIndex == 2;
    private string _triggerUntilText;
    public string TriggerUntilText { get => _triggerUntilText; set => SetProperty(ref _triggerUntilText, value); }
    private bool _missNextDay;
    public bool MissNextDay { get => _missNextDay; set => SetProperty(ref _missNextDay, value); }
    public string TriggerNote => _triggerEditable
        ? "时间使用HH:mm；留空取消定时。灵活型仅在窗口内空闲时启动，开始后可越过窗口完成。"
        : $"触发器 {Draft.Triggers.Count} 个（含自定义类型，保留原样不在此编辑）";

    // ---- 循环（C08） ----
    private int _loopModeIndex;
    private readonly bool _loopCustomPreserved;
    public int LoopModeIndex { get => _loopModeIndex; set { if (value >= 0 && value <= LoopPreserveIndex) SetProperty(ref _loopModeIndex, value); } }
    public string LoopNote => _loopCustomPreserved
        ? "既有循环模式为自定义（默认「保留自定义」不修改；显式改选才覆盖 mode，其余参数保留）"
        : "循环（C08）：无循环 / scheduled 到点开始每轮 / immediate 立即接续（其余参数保留；未修改不回写）";

    // ---- 收尾动作（E3' 至多一个） ----
    private int _terminalActionIndex;
    public int TerminalActionIndex { get => _terminalActionIndex; set { if (value >= 0 && value <= TerminalPreserveIndex) SetProperty(ref _terminalActionIndex, value); } }

    // ---- 节点 ----
    public ObservableCollection<NodeEditVm> Nodes { get; } = [];

    public RelayCommand MoveNodeUpCommand => new(p => MoveNode(p as NodeEditVm, -1));
    public RelayCommand MoveNodeDownCommand => new(p => MoveNode(p as NodeEditVm, +1));
    public RelayCommand RemoveNodeCommand => new(p =>
    {
        if (p is not NodeEditVm vm) return;
        Nodes.Remove(vm);
        Draft.Nodes.Remove(vm.Model); // 调序/删除保其余节点 NodeId（一轮 I4）
        Renumber();
    });

    private void MoveNode(NodeEditVm? vm, int delta)
    {
        if (vm is null) return;
        var i = Nodes.IndexOf(vm);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Nodes.Count) return;
        Nodes.Move(i, j);
        (Draft.Nodes[i], Draft.Nodes[j]) = (Draft.Nodes[j], Draft.Nodes[i]);
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < Nodes.Count; i++) Nodes[i].Index = i + 1;
    }

    // ---- 追加节点（来源=资源目录快照；SingleTask 禁追加） ----
    public ObservableCollection<CatalogSourceVm> AppendSources { get; } = [];

    private int _appendSourceIndex = -1;
    public int AppendSourceIndex { get => _appendSourceIndex; set => SetProperty(ref _appendSourceIndex, value); }

    private string? _appendStatusText;
    public string? AppendStatusText { get => _appendStatusText; private set => SetProperty(ref _appendStatusText, value); }

    private ResourceCatalogSnapshot? _lastCatalogSnapshot;

    internal void RefreshCatalog(ResourceCatalogService catalog)
    {
        var snap = catalog.Current;
        // 二轮（重要3）：快照未变不重建下拉（避免 2s 一拍 Clear 闪烁/打断选择）
        if (ReferenceEquals(snap, _lastCatalogSnapshot)) return;
        _lastCatalogSnapshot = snap;
        var selectedKey = AppendSourceIndex >= 0 && AppendSourceIndex < AppendSources.Count
            ? AppendSources[AppendSourceIndex].StableId : null;
        AppendSources.Clear();
        foreach (var e in snap.Entries.Where(e => e.Kind != TaskCenterResourceKind.SingleTask)) // 单项禁追加
            AppendSources.Add(new CatalogSourceVm(e));
        // 二轮（重要3）：已选资源消失 → 置未选择并提示（不得静默改选第一项，防止追加错资源）
        var found = selectedKey is null ? -1
            : AppendSources.Select((v, i) => (v, i)).FirstOrDefault(x => x.v.StableId == selectedKey, (null, -1)).i;
        AppendSourceIndex = selectedKey is null ? (AppendSources.Count > 0 ? 0 : -1) : found;
        AppendStatusText = snap.IsDegraded
            ? $"资源目录降级（{snap.DegradedReason}）——追加引用来自缓存展示，执行时按实时目录校验"
            : found < 0 && selectedKey is not null
                ? "此前选择的资源已从目录消失，请重新选择后再追加"
                : null;
    }

    public RelayCommand AppendNodeCommand => new(_ =>
    {
        if (AppendSourceIndex < 0 || AppendSourceIndex >= AppendSources.Count) return;
        var src = AppendSources[AppendSourceIndex];
        var node = new WorkflowNode
        {
            NodeId = NewNodeId(),
            Kind = src.Kind switch
            {
                TaskCenterResourceKind.OneDragonConfig => "resource.oneDragonConfig",
                TaskCenterResourceKind.ConfigGroup => "resource.configGroup",
                _ => "resource.oneDragonConfig",
            },
            Ref = new WorkflowResourceRef
            {
                Config = src.DisplayName,   // BGI 启动合同寻址名（与 R1 产物一致）
                Revision = src.ConfigRevision,
                // ConfigKey 不充当资源 ID（D14）
            },
        };
        Draft.Nodes.Add(node);
        Nodes.Add(new NodeEditVm(node));
        Renumber();
    });

    private string NewNodeId()
    {
        string id;
        do { id = "n-" + Guid.NewGuid().ToString("N")[..8]; }
        while (Draft.Nodes.Any(n => n.NodeId == id)); // 冲突检查（一轮 I4）
        return id;
    }

    /// <summary>触碰字段回写 Draft（保存前调用一次；未触碰字段/未知字段原样保留）。</summary>
    /// <summary>读取唯一 trigger.time 的时刻文本（多触发器/非时间触发器返回空串，编辑禁用）。</summary>
    private static string ReadTriggerTime(WorkflowDocument draft)
        => draft.Triggers.Count == 1 && draft.Triggers[0].Kind is "trigger.time" or "trigger.timeFixed" or "trigger.timeFlexible"
            ? draft.Triggers[0].GetString("time") ?? draft.Triggers[0].GetString("at") ?? ""
            : "";

    private static readonly JsonSerializerOptions CloneOptions = new();

    /// <summary>
    /// 生成提交副本（二轮 阻断1/重要4）：从 Draft（始终保持构造时基线）深拷贝出提交文档，把编辑应用到副本上。
    /// 保存失败不污染 Draft——重试时从干净基线重新生成，「留空=不修改」等语义不被前次失败破坏；
    /// 未修改字段、不可映射自定义值（自定义 loop mode/未知收尾 action/自定义 kind/缺 time 的既有触发器）原样保留。
    /// 节点按位置对齐应用（Nodes[k] 与 Draft.Nodes[k] 恒同引用：调序/删除/追加均双边同步，见 MoveNode/RemoveNode/AppendNode）。
    /// </summary>
    internal WorkflowDocument BuildSubmissionCopy()
    {
        var copy = JsonSerializer.Deserialize<WorkflowDocument>(
            JsonSerializer.Serialize(Draft, CloneOptions), CloneOptions)
            ?? throw new InvalidOperationException("提交副本深拷贝失败");

        if (NameText != _origName)
            copy.Name = NameText.Trim();

        // 仅回写实际编辑的字段，多触发器与自定义类型继续原样保留。
        if (_triggerEditable && (TriggerTimeText.Trim() != _origTriggerTime || TriggerModeIndex != _origTriggerMode
            || TriggerUntilText.Trim() != _origTriggerUntil || MissNextDay != _origMissNextDay))
        {
            var time = TriggerTimeText.Trim();
            var existing = copy.Triggers.FirstOrDefault();
            if (time.Length == 0)
            {
                if (existing is not null) copy.Triggers.Remove(existing);
            }
            else
            {
                if (!TimeOnly.TryParse(time, out var start)) throw new InvalidOperationException("启动时间须为HH:mm。");
                if (TriggerModeIndex == 2 && (!TimeOnly.TryParse(TriggerUntilText.Trim(), out var end) || end == start))
                    throw new InvalidOperationException("灵活窗口须填写不同于启动时间的结束时间。");
                var created = existing is null;
                existing ??= new WorkflowTrigger();
                existing.Kind = TriggerModeIndex == 1 ? "trigger.timeFixed" : TriggerModeIndex == 2 ? "trigger.timeFlexible" : "trigger.time";
                existing.Params ??= new Dictionary<string, JsonElement>();
                existing.Params["time"] = JsonSerializer.SerializeToElement(time);
                if (created || TriggerModeIndex != _origTriggerMode || MissNextDay != _origMissNextDay)
                    existing.Params["missPolicy"] = JsonSerializer.SerializeToElement(MissNextDay ? "nextDay" : "skip");
                if (TriggerModeIndex == 2)
                    existing.Params["until"] = JsonSerializer.SerializeToElement(TriggerUntilText.Trim());
                if (created) copy.Triggers.Add(existing);
            }
        }

        // 循环：保留自定义/未变化 → 不触碰；显式改选才回写 mode（其余参数保留）
        if (LoopModeIndex != LoopPreserveIndex && LoopModeIndex != _origLoopModeIndex)
        {
            if (LoopModeIndex == 0) copy.Loop = null;
            else
            {
                copy.Loop ??= new WorkflowLoop();
                copy.Loop.Mode = LoopModes[LoopModeIndex];
            }
        }

        if (LoopModeIndex is 1 or 2 && copy.Loop is { } loop)
        {
            if (LoopTimeText.Trim() != _origLoopTime || LoopDeadlineText.Trim() != _origLoopDeadline)
            {
                loop.Params ??= new Dictionary<string, JsonElement>();
                if (LoopTimeText.Trim() != _origLoopTime) loop.Params["time"] = JsonSerializer.SerializeToElement(LoopTimeText.Trim());
                if (LoopDeadlineText.Trim().Length == 0) loop.Params.Remove("deadline");
                else
                {
                    if (!TimeOnly.TryParse(LoopDeadlineText.Trim(), out _)) throw new InvalidOperationException("循环截止时间须为HH:mm。");
                    loop.Params["deadline"] = JsonSerializer.SerializeToElement(LoopDeadlineText.Trim());
                }
            }
        }

        // 收尾：保留自定义/未变化 → 不触碰；显式改选才回写（空文档新建；自定义 kind 仅在用户显式改选时替换）
        if (TerminalActionIndex != TerminalPreserveIndex && TerminalActionIndex != _origTerminalActionIndex)
        {
            var action = TerminalActionIndex > 0 ? TerminalActions[TerminalActionIndex] : null;
            if (action is null) copy.Terminal.Clear();
            else if (copy.Terminal.Count == 0)
            {
                copy.Terminal.Add(new WorkflowTerminalAction
                {
                    Kind = "terminal.completionAction",
                    Params = new Dictionary<string, JsonElement>
                    { ["action"] = JsonSerializer.SerializeToElement(action) },
                });
            }
            else
            {
                copy.Terminal[0].Kind = "terminal.completionAction";
                copy.Terminal[0].Params ??= new Dictionary<string, JsonElement>();
                copy.Terminal[0].Params["action"] = JsonSerializer.SerializeToElement(action);
            }
        }

        for (var i = 0; i < Nodes.Count && i < copy.Nodes.Count; i++)
            Nodes[i].ApplyToModel(copy.Nodes[i]);
        return copy;
    }

    /// <summary>当前草稿涉及的全部敏感原值与编辑值（状态/错误消息展示前脱敏用，二轮 重要4）。</summary>
    internal IEnumerable<string> CollectSensitiveValues()
    {
        foreach (var node in Nodes)
            foreach (var v in node.CollectSensitiveValues())
                yield return v;
    }
}

/// <summary>节点编辑条目（包装 Draft 内节点引用——修改直达草稿，保留式）。</summary>
public sealed class NodeEditVm : ViewModelBase
{
    private static readonly string[] WeekdayNames = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    // 敏感原值基线（二轮 阻断1/重要4：「留空=不修改」锚定构造时原值——保存失败重试不被前次 Apply 污染）
    private readonly string? _origUid;
    private readonly string? _origBindingCode;
    private readonly string? _origRedeemUid;
    private readonly bool[] _origWeekdays;
    private readonly string _origPriority;
    private string _priorityText;
    public string PriorityText { get => _priorityText; set => SetProperty(ref _priorityText, value); }

    internal NodeEditVm(WorkflowNode model)
    {
        Model = model;
        _origPriority = TaskCenterMechanismPolicy.PriorityOfNode(model).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _priorityText = _origPriority;
        _origUid = AccountStrategy?.GetString("uid");
        _origBindingCode = AccountStrategy?.GetString("bindingCode");
        _origRedeemUid = RedeemStrategy?.GetString("uid");
        // 星期过滤初值（既有 days 数组）
        var days = WeekdaysStrategy?.GetStringArray("days");
        if (days is not null)
            for (var i = 0; i < 7; i++) _weekdays[i] = days.Contains(WeekdayNames[i]);
        _origWeekdays = (bool[])_weekdays.Clone();
    }

    internal WorkflowNode Model { get; }

    private int _index;
    public int Index { get => _index; internal set => SetProperty(ref _index, value); }

    public string KindName => Model.Kind switch
    {
        "resource.oneDragonConfig" => "整龙配置",
        "resource.configGroup" => "配置组",
        "resource.singleTask" => "单项任务",
        var k => k,
    };
    public string ConfigName => Model.Ref?.Config ?? "（未绑定资源）";
    internal void NotifyReferenceChanged() => OnPropertyChanged(nameof(RevisionShort));
    public string RevisionShort => Model.Ref?.Revision is { Length: > 0 } r ? r[..Math.Min(8, r.Length)] : "—";

    private WorkflowStrategy? AccountStrategy => Model.Strategies.FirstOrDefault(s => s.Kind == "prerequisite.account");
    private WorkflowStrategy? RedeemStrategy => Model.Strategies.FirstOrDefault(s => s.Kind == "prerequisite.redeemCode");
    private WorkflowStrategy? WeekdaysStrategy => Model.Strategies.FirstOrDefault(s => s.Kind == "condition.weekdays");

    public bool HasAccount => AccountStrategy is not null;
    public bool HasRedeem => RedeemStrategy is not null;
    public bool HasWeekdays => WeekdaysStrategy is not null;
    public bool HasNoStrategies => !HasAccount && !HasRedeem && !HasWeekdays;

    /// <summary>策略摘要 chips（uid 掩码展示，一轮 I6）。</summary>
    public string StrategySummary
    {
        get
        {
            var chips = new List<string>();
            if (AccountStrategy is not null)
                chips.Add("账号 " + MaskSensitive(AccountStrategy.GetString("uid")));
            if (RedeemStrategy is not null)
                chips.Add("兑换码 " + (RedeemStrategy.GetString("uid") is { } u ? MaskSensitive(u) : "继承账号UID"));
            if (WeekdaysStrategy?.GetStringArray("days") is { } days)
                chips.Add(string.Join("", days.Select(d => d.Length > 1 ? d[1..] : d)));
            return chips.Count > 0 ? string.Join("  ", chips) : "（无策略）";
        }
    }

    // ---- 账号策略编辑（三分：原值驻 Model / 掩码展示 / 编辑框空=不修改） ----
    public string AccountUidMasked => MaskSensitive(AccountStrategy?.GetString("uid"));
    private string _accountUidEdit = "";
    public string AccountUidEdit { get => _accountUidEdit; set => SetProperty(ref _accountUidEdit, value); }
    public string BindingCodeMasked => MaskSensitive(AccountStrategy?.GetString("bindingCode"));
    private string _bindingCodeEdit = "";
    public string BindingCodeEdit { get => _bindingCodeEdit; set => SetProperty(ref _bindingCodeEdit, value); }

    public string RedeemUidMasked => MaskSensitive(RedeemStrategy?.GetString("uid"));
    private string _redeemUidEdit = "";
    public string RedeemUidEdit { get => _redeemUidEdit; set => SetProperty(ref _redeemUidEdit, value); }

    // ---- 星期过滤 ----
    private readonly bool[] _weekdays = new bool[7];
    public bool W1 { get => _weekdays[0]; set { _weekdays[0] = value; OnPropertyChanged(); } }
    public bool W2 { get => _weekdays[1]; set { _weekdays[1] = value; OnPropertyChanged(); } }
    public bool W3 { get => _weekdays[2]; set { _weekdays[2] = value; OnPropertyChanged(); } }
    public bool W4 { get => _weekdays[3]; set { _weekdays[3] = value; OnPropertyChanged(); } }
    public bool W5 { get => _weekdays[4]; set { _weekdays[4] = value; OnPropertyChanged(); } }
    public bool W6 { get => _weekdays[5]; set { _weekdays[5] = value; OnPropertyChanged(); } }
    public bool W7 { get => _weekdays[6]; set { _weekdays[6] = value; OnPropertyChanged(); } }

    // ---- 策略增删（锚点 4：策略即节点修饰；每类至多一个，账号唯一性由 Planner 预检兜底） ----
    public RelayCommand AddAccountCommand => new(_ =>
    {
        if (HasAccount) return;
        Model.Strategies.Add(new WorkflowStrategy { Kind = "prerequisite.account" });
        NotifyStrategyChanged();
    });
    public RelayCommand AddRedeemCommand => new(_ =>
    {
        if (HasRedeem) return;
        Model.Strategies.Add(new WorkflowStrategy { Kind = "prerequisite.redeemCode" });
        NotifyStrategyChanged();
    });
    public RelayCommand AddWeekdaysCommand => new(_ =>
    {
        if (HasWeekdays) return;
        Model.Strategies.Add(new WorkflowStrategy
        {
            Kind = "condition.weekdays",
            Params = new Dictionary<string, JsonElement>
            { ["dayBoundary"] = JsonSerializer.SerializeToElement("localMidnight") },
        });
        NotifyStrategyChanged();
    });
    public RelayCommand RemoveAccountCommand => new(_ => { if (AccountStrategy is { } s) Model.Strategies.Remove(s); NotifyStrategyChanged(); });
    public RelayCommand RemoveRedeemCommand => new(_ => { if (RedeemStrategy is { } s) Model.Strategies.Remove(s); NotifyStrategyChanged(); });
    public RelayCommand RemoveWeekdaysCommand => new(_ => { if (WeekdaysStrategy is { } s) Model.Strategies.Remove(s); NotifyStrategyChanged(); });

    private void NotifyStrategyChanged() => OnPropertyChanged(string.Empty);

    /// <summary>触碰字段回写到指定目标（提交副本节点；编辑框空=回构造基线原值，掩码不落盘；星期未触碰不重建 days）。</summary>
    internal void ApplyToModel(WorkflowNode target)
    {
        if (PriorityText.Trim() != _origPriority)
        {
            if (!int.TryParse(PriorityText.Trim(), out var priority))
                throw new InvalidOperationException("节点优先级须为整数（数值越大越优先，默认0）。");
            var strategy = target.Strategies.LastOrDefault(s => s.Kind == TaskCenterMechanismPolicy.PriorityStrategyKind);
            if (strategy is null)
            {
                strategy = new WorkflowStrategy { Kind = TaskCenterMechanismPolicy.PriorityStrategyKind };
                target.Strategies.Add(strategy);
            }
            strategy.Params ??= new Dictionary<string, JsonElement>();
            strategy.Params["priority"] = JsonSerializer.SerializeToElement(priority);
        }
        if (target.Strategies.FirstOrDefault(s => s.Kind == "prerequisite.account") is { } account)
        {
            account.Params ??= new Dictionary<string, JsonElement>();
            if (!string.IsNullOrWhiteSpace(AccountUidEdit))
                account.Params["uid"] = JsonSerializer.SerializeToElement(AccountUidEdit.Trim());
            else if (_origUid is not null)
                account.Params["uid"] = JsonSerializer.SerializeToElement(_origUid); // 留空=回基线（失败重试不污染）
            if (!string.IsNullOrWhiteSpace(BindingCodeEdit))
                account.Params["bindingCode"] = JsonSerializer.SerializeToElement(BindingCodeEdit.Trim());
            else if (_origBindingCode is not null)
                account.Params["bindingCode"] = JsonSerializer.SerializeToElement(_origBindingCode);
        }
        if (target.Strategies.FirstOrDefault(s => s.Kind == "prerequisite.redeemCode") is { } redeem)
        {
            if (!string.IsNullOrWhiteSpace(RedeemUidEdit))
            {
                redeem.Params ??= new Dictionary<string, JsonElement>();
                redeem.Params["uid"] = JsonSerializer.SerializeToElement(RedeemUidEdit.Trim());
            }
            else if (_origRedeemUid is not null)
            {
                redeem.Params ??= new Dictionary<string, JsonElement>();
                redeem.Params["uid"] = JsonSerializer.SerializeToElement(_origRedeemUid);
            }
        }
        // 星期：相对基线有变化才重建 days（未触碰则原数组原样保留——顺序/未映射值不丢，二轮 阻断1）
        if (target.Strategies.FirstOrDefault(s => s.Kind == "condition.weekdays") is { } weekdays && !_weekdays.SequenceEqual(_origWeekdays))
        {
            weekdays.Params ??= new Dictionary<string, JsonElement>();
            var days = WeekdayNames.Where((d, i) => _weekdays[i]).ToArray();
            weekdays.Params["days"] = JsonSerializer.SerializeToElement(days); // 空选择不改成每天；移除条件另有按钮
        }
    }

    /// <summary>本节点涉及的敏感值（原值基线 + 编辑框当前值；展示脱敏用）。</summary>
    internal IEnumerable<string> CollectSensitiveValues()
    {
        if (!string.IsNullOrEmpty(_origUid)) yield return _origUid!;
        if (!string.IsNullOrEmpty(_origBindingCode)) yield return _origBindingCode!;
        if (!string.IsNullOrEmpty(_origRedeemUid)) yield return _origRedeemUid!;
        if (!string.IsNullOrWhiteSpace(AccountUidEdit)) yield return AccountUidEdit.Trim();
        if (!string.IsNullOrWhiteSpace(BindingCodeEdit)) yield return BindingCodeEdit.Trim();
        if (!string.IsNullOrWhiteSpace(RedeemUidEdit)) yield return RedeemUidEdit.Trim();
    }

    /// <summary>敏感值掩码（一轮 I6：≤5 位全遮盖，否则前 3+***+后 2；空值显示「未设置」）。</summary>
    internal static string MaskSensitive(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "未设置";
        if (value.Length <= 5) return "*****";
        return value[..3] + "***" + value[^2..];
    }
}

/// <summary>追加来源条目（资源目录快照投影；SingleTask 已在填充时排除）。</summary>
public sealed class CatalogSourceVm
{
    private readonly TaskCenterResourceEntry _entry;
    internal CatalogSourceVm(TaskCenterResourceEntry entry) => _entry = entry;
    public string StableId => _entry.StableId;
    public TaskCenterResourceKind Kind => _entry.Kind;
    public string DisplayName => _entry.DisplayName;
    public string? ConfigRevision => _entry.ConfigRevision;
    public string Line => $"{(_entry.Kind == TaskCenterResourceKind.OneDragonConfig ? "整龙" : "配置组")} · {_entry.DisplayName}"
        + (_entry.ConfigRevision is { Length: > 0 } r ? $"（rev {r[..Math.Min(8, r.Length)]}）" : "")
        + (_entry.IsFromCache ? "［缓存］" : "");
    public override string ToString() => Line;
}

/// <summary>只读预览（candidate-ready/隔离/普通流程通用；uid 掩码展示）。</summary>
public sealed class WorkflowPreviewVm
{
    private WorkflowPreviewVm() { }

    public string Title { get; private set; } = "";
    public IReadOnlyList<string> Lines { get; private set; } = [];
    public string? Notice { get; private set; }

    internal static WorkflowPreviewVm Build(WorkflowDocument doc, string revision)
    {
        var lines = new List<string>
        {
            $"修订 {revision[..Math.Min(8, revision.Length)]} · {doc.Nodes.Count} 节点"
            + (doc.Loop is not null ? $" · 循环 {doc.Loop.Mode}" : "")
            + (doc.Triggers.Count > 0 ? $" · 触发器 {doc.Triggers.Count} 个" : "")
            + (doc.Terminal.Count > 0 ? $" · 收尾 {doc.Terminal[0].GetString("action")}" : ""),
        };
        foreach (var node in doc.Nodes)
        {
            var vm = new NodeEditVm(node); // 只读复用摘要/掩码逻辑（不进编辑态，不改模型）
            lines.Add($"{node.NodeId}  {vm.KindName} · {vm.ConfigName}  rev {vm.RevisionShort}  {vm.StrategySummary}");
        }
        var notice = string.Equals(doc.Activation?.Status, "candidate-ready", StringComparison.Ordinal)
            ? "candidate-ready 迁移候选：只读预览；正式激活由 R5 事务迁移完成（D13），此处不提供激活/另存。"
            : null;
        return new WorkflowPreviewVm { Title = $"预览 · {doc.Name}", Lines = lines, Notice = notice };
    }
}