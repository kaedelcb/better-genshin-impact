using MultiplayerHoeingAssistant.Models;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Node time is anchored to the durable run day, so resuming cannot silently move it to tomorrow.</summary>
public static class WorkflowNodeSchedule
{
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
        run.ExtensionData ??= new();
        var timingKey = "nodeTiming:" + key;
        WorkflowTriggerTiming timing;
        if (run.ExtensionData.TryGetValue(timingKey, out var saved))
            timing = saved.Deserialize<WorkflowTriggerTiming>() ?? throw new InvalidOperationException("节点排程持久时刻不可读取");
        else
        {
            var day = occurrence.LoopIteration > 0 ? _opt.Clock() : run.TriggerTiming?.ScheduledAt ?? run.CreatedAt;
            timing = WorkflowNodeSchedule.Resolve(schedule, day, out var error) ?? throw new InvalidOperationException(error);
            run.ExtensionData[timingKey] = JsonSerializer.SerializeToElement(timing);
            _runs.Update(run);
        }
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
