using MultiplayerHoeingAssistant.Models;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Node time is anchored to the durable run day, so resuming cannot silently move it to tomorrow.</summary>
public static class WorkflowNodeSchedule
{
    internal static string TimingKey(string nodeId, int occurrence, int loopIteration)
        => $"nodeTiming:schedule.time:{nodeId}:{occurrence}:{loopIteration}";

    internal static WorkflowTriggerTiming? Read(WorkflowRunRecord run, string nodeId, int occurrence, int loopIteration)
    {
        if (run.ExtensionData?.TryGetValue(TimingKey(nodeId, occurrence, loopIteration), out var saved) == true)
            return saved.Deserialize<WorkflowTriggerTiming>() ?? throw new InvalidOperationException("节点排程持久时刻不可读取");
        // 旧候选的键不含出现序号，只能在无歧义的首次出现复用。
        if (occurrence == 0 && run.ExtensionData?.TryGetValue($"nodeTiming:schedule.time:{nodeId}:{loopIteration}", out saved) == true)
            return saved.Deserialize<WorkflowTriggerTiming>() ?? throw new InvalidOperationException("旧节点排程持久时刻不可读取");
        return null;
    }

    internal static WorkflowTriggerTiming? Effective(WorkflowRunRecord run, WorkflowNode node, WorkflowNodeOccurrence occurrence)
    {
        var schedule = node.Strategies.LastOrDefault(s => s.Kind == "schedule.time");
        if (schedule is null) return run.TriggerTiming;
        var timing = Read(run, occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration)
            ?? throw new InvalidOperationException("本次节点尚未绑定持久排程时刻，禁止自报排序");
        var expected = Resolve(schedule, timing.ScheduledAt, out var error)
            ?? throw new InvalidOperationException(error);
        if (expected.Kind != timing.Kind || expected.ScheduledAt != timing.ScheduledAt || expected.WindowEndsAt != timing.WindowEndsAt)
            throw new InvalidOperationException("节点排程与本次定义不匹配，保留原时刻并禁止提交");
        return timing;
    }

    internal static WorkflowTriggerTiming Bind(WorkflowRunRecord run, WorkflowNode node, WorkflowNodeOccurrence occurrence, DateTimeOffset now)
    {
        if (Read(run, occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration) is not null)
            return Effective(run, node, occurrence)!;
        var schedule = node.Strategies.Last(s => s.Kind == "schedule.time");
        var previous = run.NodeOutcomes.AsEnumerable().Reverse()
            .Select(o => Read(run, o.NodeId, o.Occurrence, o.LoopIteration)).FirstOrDefault(t => t is not null);
        var prior = run.NodeOutcomes.AsEnumerable().Reverse().Where(o=>o.NodeId==occurrence.NodeId && o.Occurrence==occurrence.Occurrence)
            .Select(o=>Read(run,o.NodeId,o.Occurrence,o.LoopIteration)).FirstOrDefault(t=>t is not null);
        var repeated = prior is not null;
        var day = previous?.ScheduledAt ?? run.TriggerTiming?.ScheduledAt ?? run.CreatedAt;
        if(repeated && schedule.GetString("mode")=="sequence")
        {
            // 顺序型是本轮开始下限；合法再次到达不附加“每日一次”门槛。
            day=prior!.ScheduledAt;
            if(run.ExtensionData?.TryGetValue("loopRoundStart:"+occurrence.LoopIteration,out var roundStart)==true)
                day=roundStart.Deserialize<DateTimeOffset>();
        }
        var timing = Resolve(schedule, day, out var error) ?? throw new InvalidOperationException(error);
        // 首次到达的前向跨午夜沿路径选日；重复在原下限/有效窗口内继续执行。冷恢复只读既存值。
        if (!repeated && previous is not null && timing.ScheduledAt < previous.ScheduledAt)
            timing = ShiftDay(timing);
        if (occurrence.LoopIteration > 0 && timing.Kind != "trigger.time")
        {
            while (timing.Kind=="trigger.timeFixed" ? now>=timing.ScheduledAt.AddMinutes(1) : now>=timing.WindowEndsAt)
                timing = ShiftDay(timing);
        }
        run.ExtensionData ??= new();
        run.ExtensionData[TimingKey(occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration)] = JsonSerializer.SerializeToElement(timing);
        return timing;
    }

    private static WorkflowTriggerTiming ShiftDay(WorkflowTriggerTiming timing)
        => timing with { ScheduledAt = timing.ScheduledAt.AddDays(1), WindowEndsAt = timing.WindowEndsAt?.AddDays(1) };

    internal static bool FixedDeclared(WorkflowRunRecord run, DateTimeOffset now)
    {
        if (run.State != WorkflowRunState.Waiting) return false;
        var cursor = run.Cursor;
        var timing = cursor is null ? null : Read(run, cursor.NodeId, cursor.Occurrence, cursor.LoopIteration);
        timing ??= run.TriggerTiming;
        return timing is { Kind: "trigger.timeFixed" } && timing.ScheduledAt <= now && now < timing.ScheduledAt.AddMinutes(1);
    }

    public static WorkflowTriggerTiming? Resolve(WorkflowStrategy schedule, DateTimeOffset runDay, out string? error)
    {
        error = null;
        var mode = schedule.GetString("mode");
        if (mode is not ("sequence" or "fixed" or "flexible")) { error = "节点执行方式无效"; return null; }
        if (!TimeOnly.TryParseExact(schedule.GetString("time"), "HH:mm", out var time)) { error = "节点时间须为HH:mm"; return null; }
        var at = new DateTimeOffset(runDay.Year, runDay.Month, runDay.Day, time.Hour, time.Minute, 0, runDay.Offset);
        DateTimeOffset? until = null;
        if (mode == "flexible")
        {
            if (!TimeOnly.TryParseExact(schedule.GetString("until"), "HH:mm", out var end)) { error = "节点窗口结束须为HH:mm"; return null; }
            until = new DateTimeOffset(at.Year, at.Month, at.Day, end.Hour, end.Minute, 0, at.Offset);
            if (until <= at) until = until.Value.AddDays(1);
        }
        return new WorkflowTriggerTiming(mode == "flexible" ? "trigger.timeFlexible" : mode == "fixed" ? "trigger.timeFixed" : "trigger.time", at, until) { MissPolicy = "skip" };
    }
}

public sealed partial class WorkflowRunner
{
    private async Task<bool> AwaitNodeScheduleAsync(WorkflowRunRecord run, WorkflowNode node,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        var schedule = node.Strategies.LastOrDefault(s => s.Kind == "schedule.time");
        if (schedule is null) return true;
        var key = $"schedule.time:{occurrence.NodeId}:{occurrence.LoopIteration}";
        var timing = WorkflowNodeSchedule.Bind(run, node, occurrence, _opt.Clock());
        _runs.Update(run);
        var at = timing.ScheduledAt;
        if (timing.Kind == "trigger.timeFixed" && _opt.Clock() >= at.AddMinutes(1))
        { run.Wait = null; run.State = WorkflowRunState.Running; _runs.Update(run); return false; }
        if (_opt.Clock() < at)
        {
            await WaitAsync(run, key, at, control, ct).ConfigureAwait(false);
            if (control.PauseRequested) return false;
        }
        if (timing.Kind == "trigger.timeFixed") return _opt.Clock() < at.AddMinutes(1);
        if (timing.Kind != "trigger.timeFlexible") return true;
        if (_opt.FlexibleFactsProvider is null) throw new InvalidOperationException("节点灵活窗口缺少实际空闲事实来源");
        while (_opt.Clock() < timing.WindowEndsAt)
        {
            await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
            if (TaskCenterMechanismPolicy.IsFlexiblyIdle(_opt.FlexibleFactsProvider(), out _)) return true;
            var next = _opt.Clock().AddSeconds(2);
            if (next > timing.WindowEndsAt) next = timing.WindowEndsAt!.Value;
            await WaitAsync(run, key, next, control, ct).ConfigureAwait(false);
            if (control.PauseRequested) return false;
        }
        return false;
    }
}
