using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

// Display-only records. No WPF objects, room credentials, other members' execution results or
// task-dispatch authority cross this boundary. A future web view can reuse the same statuses.
internal sealed record RoomOrchestrationStatus(string Kind, string Text, string Detail = "")
{
    public string Glyph => Kind switch
    {
        "done" => "✓", "running" => "▷", "waiting" => "◷", "pending" => "○",
        "stopped" => "□", "failed" => "△", "unknown" => "?", _ => "—"
    };
    public string Display => Glyph + " " + Text;
}

internal sealed record RoomOrchestrationTaskRow(int Number, string Name, string Type, RoomOrchestrationStatus Status);
internal sealed record RoomOrchestrationEdge(string Relation, bool ContinuesToNext, RoomOrchestrationStatus SwitchStatus,
    string PostAction, RoomOrchestrationStatus? PostStatus, string OnlineResult, string Detail)
{
    public bool HasPostStatus => PostStatus is not null;
    public bool HasOnlineResult => OnlineResult.Length != 0;
    public bool HasDetail => Detail.Length != 0;
}

internal sealed record RoomOrchestrationCard(string Id, string Name, string Identity, string SectionTitle, bool Current,
    bool CanEdit, RoomOrchestrationStatus Status, string UpdatedText, string Detail,
    RoomOrchestrationTaskRow[] Tasks, RoomOrchestrationTaskRow[] NextTasks, string TasksCaption,
    string ExpectedPlayers, string Schedule, string Completion, RoomOrchestrationEdge? Edge, RoomOrchestrationEdge? PreviousEdge,
    bool ProgressUnconfirmed = false)
{
    public bool HasSection => SectionTitle.Length != 0;
    public bool HasDetail => Detail.Length != 0;
    public bool HasUpdatedTime => UpdatedText.Length != 0;
    public bool HasTasks => Tasks.Length != 0;
    public bool HasNextTasks => NextTasks.Length != 0;
    public bool HasTaskCaption => TasksCaption.Length != 0;
    public bool HasTaskWarning => HasNextTasks || TasksCaption.Contains("已变更", StringComparison.Ordinal);
    public bool HasEdge => Edge is not null;
    public bool HasPreviousEdge => PreviousEdge is not null;
    public int CompletedCount => Tasks.Count(t => t.Status.Kind == "done");
    public int TaskCount => Tasks.Length;
    public bool HasConfirmedProgress => HasTaskCaption && !ProgressUnconfirmed;
    public string ProgressText => ProgressUnconfirmed ? "进度待核对" : Tasks.Length == 0 ? "未绑定联机任务"
        : Tasks.All(t => t.Status.Kind == "none") ? $"共 {Tasks.Length} 项绑定"
        : $"{CompletedCount} / {Tasks.Length} 项已完成";
}

internal sealed record RoomOrchestrationPresentation(string LoginText, string LoginContext, RoomOrchestrationStatus CurrentStatus,
    RoomOrchestrationStatus[] Legend, RoomOrchestrationCard[] Cards)
{
    internal static RoomOrchestrationPresentation Empty { get; } = new("当前登录：尚未确认", "正在读取本机房间设置…",
        new("unknown", "待核对"), [], []);

    public string RoomCount => $"{Cards.Length} 个房间";
    public bool IsEmpty => Cards.Length == 0;

    private sealed record ProfileData(AssistantRoomProfile Profile, string Identity, TaskConflictPolicySettings Completion,
        RoomOrchestrationRunSnapshot? Run, RoomOrchestrationTransitionSnapshot? Transition,
        RoomOrchestrationBinding[] Bindings, int People, string Schedule, bool ScheduleRequiresConfirmation, bool ValidEdge);

    internal static RoomOrchestrationPresentation Create(AssistConfig config, string? confirmedId, string? onlineStatus,
        string roomSwitchStatus, bool canEdit, Func<AssistantRoomProfile, RoomOrchestrationRunSnapshot?> readRun,
        Func<AssistantRoomProfile, RoomOrchestrationTransitionSnapshot?> readTransition)
    {
        var profiles = config.RoomProfiles.Where(p => p is not null).ToArray();
        var data = new Dictionary<string, ProfileData>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            // Ambiguous IDs cannot be interpreted as valid editable nodes or routes.
            if (string.IsNullOrWhiteSpace(profile.Id) || profiles.Count(p => p.Id == profile.Id) != 1) continue;
            var active = config.ActiveRoomProfileId == profile.Id;
            var completion = CompletionPolicyFields.SnapshotForProfile(config, profile.Id);
            var hasSameCode = profiles.Count(p => p.RoomCode == profile.RoomCode) > 1;
            var identity = profile.RoomCode + (hasSameCode ? " · " + config.GetRoomServerUrl(profile) : "");
            data.Add(profile.Id, new(profile, identity, completion, readRun(profile), readTransition(profile),
                RoomOrchestrationBinding.From(active ? config.OnlineHoeingGroupNames : profile.OnlineHoeingGroupNames,
                    active ? config.OnlineHoeingGroupTypes : profile.OnlineHoeingGroupTypes),
                active ? config.ExpectedHoeingPlayers : profile.ExpectedHoeingPlayers,
                (active ? config.ScheduledOnlineTime : profile.ScheduledOnlineTime) ?? "",
                active ? config.ScheduledOnlineRequiresConfirmation : profile.ScheduledOnlineRequiresConfirmation,
                completion.RoomSwitch is { } edge && edge.SourceProfileId == profile.Id
                    && CompletionPolicyFields.ValidateRoomSwitch(config, profile.Id, completion) is null));
        }
        var inbound = data.Values.Where(p => p.ValidEdge).Select(p => p.Completion.RoomSwitch!.TargetProfileId).ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cards = new List<RoomOrchestrationCard>();
        // First draw actual directed roots; then unvisited cycles; each room appears exactly once.
        foreach (var profile in profiles)
            if (data.TryGetValue(profile.Id, out var info) && info.Completion.RoomSwitch is not null && !inbound.Contains(profile.Id))
                DrawChain(profile.Id, cards.Count == 0 ? "" : "另一条接力路线");
        foreach (var profile in profiles)
            if (data.TryGetValue(profile.Id, out var info) && (info.Completion.RoomSwitch is not null || inbound.Contains(profile.Id)))
                DrawChain(profile.Id, cards.Count == 0 ? "" : "另一条接力路线");
        var firstIndependent = true;
        foreach (var profile in profiles)
        {
            if (data.ContainsKey(profile.Id) && !visited.Contains(profile.Id))
            {
                DrawChain(profile.Id, firstIndependent ? "独立房间" : "");
                firstIndependent = false;
            }
            else if (!data.ContainsKey(profile.Id))
            {
                cards.Add(new(profile.Id, profile.Name, profile.RoomCode, firstIndependent ? "独立房间" : "", false,
                    false, new("unknown", "档案身份不唯一"), "", "请在管理房间中核对档案，未推断任务状态。", [], [], "",
                    $"{profile.ExpectedHoeingPlayers} 人", profile.ScheduledOnlineTime, "待核对", null, null));
                firstIndependent = false;
            }
        }

        var loggedInCard = cards.FirstOrDefault(c => c.Current);
        var lastConfirmed = profiles.FirstOrDefault(p => p.Id == config.ActiveRoomProfileId);
        var header = loggedInCard is not null ? $"当前登录：{loggedInCard.Name} {loggedInCard.Identity}"
            : lastConfirmed is not null ? $"最后确认房间：{lastConfirmed.Name} {lastConfirmed.RoomCode}（尚未确认连接）" : "当前登录：尚未确认";
        var currentStatus = loggedInCard?.Status ?? new("unknown", "登录待核对");
        var running = loggedInCard?.Tasks.FirstOrDefault(t => t.Status.Kind == "running");
        var context = running is not null ? $"第 {running.Number} / {loggedInCard!.TaskCount} 项 · {running.Name}"
            : loggedInCard is not null && loggedInCard.Detail.Length > 0 ? loggedInCard.Detail : roomSwitchStatus;

        if (loggedInCard is not null)
        {
            // Prefer confirmed current-batch facts over an earlier transition; accepted post jobs
            // are not a new online batch. Historical online receipts are displayed on the edge only.
            var run = data[loggedInCard.Id].Run;
            var activeRound = run is { Settled: false } && currentStatus.Kind is "running" or "waiting" or "pending";
            var outbound = data[loggedInCard.Id].Transition;
            if (outbound is { SwitchState: "waiting" or "switching" }
                && MatchesRun(run, outbound))
            {
                currentStatus = SwitchStatus(outbound.SwitchState);
                context = outbound.Message;
            }
            else if (!activeRound)
            {
                var incoming = data.Values.Select(p => p.Transition)
                    .Where(t => t is not null && t.Target.TargetProfileId == loggedInCard.Id && t.Target.MatchesTarget(config)
                        && t.SwitchState == "switched" && (run is null || t.UpdatedAt >= run.UpdatedAt))
                    .OrderByDescending(t => t!.UpdatedAt).FirstOrDefault();
                if (onlineStatus is not null)
                {
                    currentStatus = new("waiting", onlineStatus);
                    context = incoming?.Message ?? "等待当前房间联机条件。";
                }
                else if (incoming is not null)
                {
                    currentStatus = incoming.Target.RunAfterSwitch ? PostStatus(incoming.PostState, true)
                        : new("waiting", "切房后待命");
                    context = incoming.Target.RunAfterSwitch
                        ? $"切房后任务：{TaskType(incoming.PostTaskType)} · {incoming.PostTaskName}"
                        : "目标房间已就绪；待命不等于已经上线。";
                    // Keep the bound task rows separate from the newly arrived preparation task.
                    var cardIndex = cards.FindIndex(c => c.Id == loggedInCard.Id);
                    if (cardIndex >= 0)
                        cards[cardIndex] = loggedInCard with { Detail = currentStatus.Display + " · " + context
                            + (run is null ? "；联机绑定尚无执行记录" : "；下方为最近联机执行记录") };
                }
                else if (currentStatus.Kind == "none")
                {
                    currentStatus = new("waiting", "已连接");
                    context = "尚无本机联机执行记录。";
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(context)) context = "配置中的活动房间不代表已经登录。";

        var legend = new (string Kind, string Label)[] { ("done", "已完成"), ("running", "进行中"), ("waiting", "等待"),
            ("pending", "未开始"), ("stopped", "已停止"), ("failed", "失败"), ("unknown", "待核对"), ("none", "无记录") }
            .Select(k => new RoomOrchestrationStatus(k.Kind, $"{k.Label} {cards.Count(c => c.Status.Kind == k.Kind)}")).ToArray();
        return new(header, context ?? "", currentStatus, legend, cards.ToArray());

        void DrawChain(string start, string section)
        {
            var id = start;
            while (data.TryGetValue(id, out var entry) && visited.Add(id))
            {
                var targetId = entry.Completion.RoomSwitch?.TargetProfileId;
                var adjacent = entry.ValidEdge && targetId is not null && data.ContainsKey(targetId) && !visited.Contains(targetId);
                cards.Add(BuildCard(entry, config, confirmedId == id, onlineStatus, section,
                    canEdit, adjacent));
                section = "";
                if (!adjacent) break;
                id = targetId!;
            }
        }
    }

    private static bool MatchesRun(RoomOrchestrationRunSnapshot? run, RoomOrchestrationTransitionSnapshot transition)
        => run is null || (string.IsNullOrEmpty(transition.SourceBatchId)
            ? transition.UpdatedAt >= run.UpdatedAt
            : run.BatchId == transition.SourceBatchId && run.Generation == transition.SourceGeneration);

    private static RoomOrchestrationCard BuildCard(ProfileData info, AssistConfig config, bool current,
        string? onlineStatus, string section, bool canEdit, bool adjacent)
    {
        var run = info.Run;
        var live = current && run is { Settled: false } && run.Phase != "unknown";
        var configured = info.Bindings.Select((b, i) => new RoomOrchestrationTaskRow(i + 1, b.Name, TaskType(b.Type),
            new("none", "无执行记录"))).ToArray();
        var taskRows = run is null ? configured : run.Tasks.Select((t, i) => new RoomOrchestrationTaskRow(i + 1,
            t.Name, TaskType(t.Type), TaskStatus(t.State, t.Detail, live, run.Settled))).ToArray();
        var orderChanged = run is not null && !run.Tasks.Select(t => new RoomOrchestrationBinding(t.Name, t.Type)).SequenceEqual(info.Bindings);
        var status = RoomStatus(run, taskRows, live);
        var detail = run?.Message ?? "";
        if (run is { Settled: true } && status.Kind == "done") detail = ""; // already represented by the success badge, timestamp and progress
        if (current && onlineStatus is not null && (run is null || run.Settled))
        {
            // This is the currently confirmed online state, not the previous run's outcome. Its
            // per-task result remains frozen below and the last-run timestamp remains visible.
            status = new("waiting", onlineStatus);
            detail = run is null ? "" : "下方为最近一轮的执行记录。";
        }
        if (info.Transition is { } transition && transition.SwitchState is "waiting" or "switching"
            && MatchesRun(run, transition))
            status = current ? SwitchStatus(transition.SwitchState) : new("unknown", "接续待核对");
        var caption = run is null ? "" : run.Settled ? "最近一轮执行记录" : "本轮冻结任务";
        if (orderChanged) caption += "（当前绑定已变更）";

        var choice = info.Completion.RoomSwitch;
        RoomOrchestrationEdge? edge = null, previousEdge = null;
        if (choice is not null)
        {
            var recorded = info.Transition is { } recent && SameRoute(choice, recent.Target)
                && (!choice.RunAfterSwitch || recent.PostTaskName == info.Completion.SpecifiedTaskName && recent.PostTaskType == info.Completion.SpecifiedTaskType)
                && (run is null || run.Settled && MatchesRun(run, recent))
                ? recent : null;
            edge = MakeEdge(config, info.Profile.Name, choice, info.ValidEdge, adjacent, recorded,
                info.Completion.SpecifiedTaskType, info.Completion.SpecifiedTaskName);
            if (recorded is null && info.Transition is { } previous)
                previousEdge = MakeHistoryEdge(config, info.Profile.Name, previous);
        }
        else if (info.Transition is { } previous) previousEdge = MakeHistoryEdge(config, info.Profile.Name, previous);

        var schedule = string.IsNullOrEmpty(info.Schedule) ? "未启用" : info.Schedule + (info.ScheduleRequiresConfirmation ? "（待确认）" : "");
        var completion = choice is null ? info.Completion.Policy switch
        {
            TaskConflictPolicy.Stop => "停止", TaskConflictPolicy.RunSpecified => $"{TaskType(info.Completion.SpecifiedTaskType)} · {info.Completion.SpecifiedTaskName}", _ => "恢复任务"
        } : "切换至 " + TargetLabel(config, choice) + (info.ValidEdge ? "" : "（关系失效）");
        return new(info.Profile.Id, info.Profile.Name, info.Identity, section, current, canEdit, status,
            run?.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm") ?? "", detail,
            taskRows, orderChanged ? configured : [], caption, $"{info.People} 人", schedule, completion, edge, previousEdge);
    }

    private static RoomOrchestrationStatus RoomStatus(RoomOrchestrationRunSnapshot? run,
        RoomOrchestrationTaskRow[] tasks, bool live)
    {
        if (run is null) return new("none", "无执行记录");
        if (run.Phase is "failed" or "completed_with_errors" or "completed_with_task_errors" || tasks.Any(t => t.Status.Kind == "failed"))
            return new("failed", run.Phase.StartsWith("completed", StringComparison.Ordinal) ? "带错误完成" : "失败", run.Message);
        if (run.Phase is "cancelled" or "stopped" or "aborted") return new("stopped", run.Phase == "cancelled" ? "已取消" : "已停止", run.Message);
        if (run.Phase == "unknown" || !run.Settled && !live) return new("unknown", "待核对", run.Message);
        if (run.Settled)
        {
            if (run.Phase is "succeeded" or "completed" && tasks.Length > 0 && tasks.All(t => t.Status.Kind == "done"))
                return new("done", "已完成");
            return new("unknown", "终态待核对", run.Message);
        }
        if (tasks.Any(t => t.Status.Kind == "running")) return new("running", "联机执行中", run.Message);
        if (run.Phase is "succeeded" or "completed" or "cleanup" or "completion_wait" or "settling" ||
            tasks.Length > 0 && tasks.All(t => t.Status.Kind == "done")) return new("waiting", "等待本轮收尾", run.Message);
        if (tasks.Any(t => t.Status.Kind == "unknown")) return new("unknown", "待核对", run.Message);
        return new("waiting", "等待联机任务", run.Message);
    }

    internal static RoomOrchestrationStatus TaskStatus(string state, string detail, bool live, bool settled) => state switch
    {
        "completed" => new("done", "已完成", detail),
        "failed" or "completed_with_errors" or "completed_with_task_errors" => new("failed", "失败", detail),
        "cancelled" => new("stopped", "已取消", detail), "stopped" or "aborted" => new("stopped", "已停止", detail),
        "pending" when live || settled => new("pending", "未开始", detail),
        "running" when live => new("running", "进行中", detail),
        "queued" when live => new("waiting", "已受理", detail),
        "waiting" when live => new("waiting", "等待", detail),
        _ => new("unknown", "待核对", detail)
    };

    private static bool SameRoute(RoomSwitchCompletion a, RoomSwitchCompletion b)
        => a.SourceProfileId == b.SourceProfileId && a.TargetProfileId == b.TargetProfileId
            && a.SourceRoomCode == b.SourceRoomCode && a.TargetRoomCode == b.TargetRoomCode
            && RoomServerAddress.Same(a.SourceServerUrl, b.SourceServerUrl) && RoomServerAddress.Same(a.TargetServerUrl, b.TargetServerUrl)
            && a.RunAfterSwitch == b.RunAfterSwitch;

    private static string TargetLabel(AssistConfig config, RoomSwitchCompletion room)
    {
        var current = room.MatchesTarget(config) ? config.RoomProfiles.FirstOrDefault(p => p.Id == room.TargetProfileId) : null;
        var name = current?.Name ?? room.TargetName;
        if (string.IsNullOrWhiteSpace(name)) name = "未确认目标房间";
        return string.IsNullOrWhiteSpace(room.TargetRoomCode) ? name : name + " · " + room.TargetRoomCode;
    }

    private static RoomOrchestrationEdge MakeEdge(AssistConfig config, string source, RoomSwitchCompletion edge, bool valid,
        bool adjacent, RoomOrchestrationTransitionSnapshot? record, string taskType, string taskName)
        => new(source + " → " + TargetLabel(config, edge) + (!valid ? "（关系失效）" : adjacent ? "" : "（引用已展示房间）"), adjacent,
            !valid ? new("unknown", "需重新确认") : record is null ? new("pending", "尚无本轮切换记录") : SwitchStatus(record.SwitchState),
            edge.RunAfterSwitch ? $"切换成功后：{TaskType(taskType)} · {taskName}" : "切换成功后：待命",
            record is not null && edge.RunAfterSwitch ? PostStatus(record.PostState, true) : null,
            record?.OnlineConfirmed == true ? "命令上线已受理" : "", record is not null ? TransitionDetail(record) : "");

    private static RoomOrchestrationEdge MakeHistoryEdge(AssistConfig config, string source, RoomOrchestrationTransitionSnapshot previous)
        => new("最近接续：" + source + " → " + TargetLabel(config, previous.Target), false,
            SwitchStatus(previous.SwitchState), previous.Target.RunAfterSwitch
                ? $"切房后任务：{TaskType(previous.PostTaskType)} · {previous.PostTaskName}" : "切房后：待命",
            previous.Target.RunAfterSwitch ? PostStatus(previous.PostState, true) : null,
            previous.OnlineConfirmed ? "命令上线已受理" : "", TransitionDetail(previous));

    private static string TransitionDetail(RoomOrchestrationTransitionSnapshot record)
        => $"{record.UpdatedAt.ToLocalTime():MM-dd HH:mm} · {record.Message}";

    private static RoomOrchestrationStatus SwitchStatus(string state) => state switch
    {
        "switched" => new("done", "切房已确认"), "waiting" => new("waiting", "等待旧批次结清"),
        "switching" => new("waiting", "正在切换"), "failed" => new("failed", "切房失败"),
        "cancelled" => new("stopped", "切房已取消"), _ => new("unknown", "切房待核对")
    };

    private static RoomOrchestrationStatus PostStatus(string state, bool includePrefix) => state switch
    {
        "pending" or "none" => new("waiting", includePrefix ? "后继待提交" : "待提交"),
        "queued" => new("waiting", "任务已受理"), "running" => new("running", "后继任务进行中"),
        "completed" => new("done", "后继任务已完成"), "failed" => new("failed", "后继任务失败"),
        "cancelled" => new("stopped", "后继任务已取消"), _ => new("unknown", "后继任务待核对")
    };

    internal static string TaskType(string? type) => type switch
    {
        "group" => "配置组", "onedragon" => "一条龙", "workflow" => "计划表", _ => "类型待核对"
    };
}
