using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class WorkflowRunner
{
    /// <summary>等待记录种类：时段「等待到点」。</summary>
    public const string TimeWindowStartWaitKind = "timeWindow.start";

    /// <summary>
    /// 时段「等待到点」：时段限制开启、该行勾选了等待、当天开始时间还没到，并且日期、启用等其他条件都放行时，
    /// 等到开始时间再继续（可先关闭游戏）。返回 true 表示进入过等待，调用方需重新核对暂停、截止和修订。
    /// 当天开始时间已过、或其他条件本来就会跳过时不等待，沿用原规则。
    /// </summary>
    private async Task<bool> AwaitTimeWindowStartAsync(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNode node,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        if (!plan.Document.NodeTimeWindowEnabled || node.TimeWindow is not { WaitForStart: true } window) return false;
        if (node.Kind is "control.condition" or "control.end") return false;
        var now = _opt.Clock();
        if (WorkflowTimeWindowPolicy.StartWaitTarget(true, window, now) is not { } target) return false;
        var other = plan.EvaluateNode(occurrence, now, _boundary.SingleNativeSupported,
            ignorePlanParticipation: IsSingleNodeRun(run), ignoreTimeWindow: true);
        if (other.Action != NodeGateAction.Proceed) return false;

        // 循环截止/本轮结束早于开始时间时只等到截止，醒来后由调用方按原规则结算。
        var cutoff = NodeScheduleCutoff(run, plan, occurrence);
        var until = cutoff is { } end && end < target ? end : target;
        if (until <= now) return false;

        string? gameNote = null;
        var gameClosed = false;
        if (window.CloseGameWhileWaiting)
        {
            if (_opt.CloseGameForTimeWindowWaitAsync is not { } close)
                gameNote = "当前执行端不支持关闭游戏，保持游戏运行等待";
            else
            {
                try { (gameClosed, gameNote) = await close(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { gameNote = "关闭游戏失败，保持游戏运行等待：" + Sanitize(ex.Message); }
            }
        }
        Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 还没到执行时段（{WorkflowTimeWindowPolicy.Display(window)}），"
                 + $"等待到 {until:HH:mm} 再执行" + (gameNote is null ? "。" : "；" + gameNote + "。"));
        await WaitAsync(run, TimeWindowStartWaitKind, until, control, ct, wait =>
        {
            wait.ExtensionData ??= new();
            wait.ExtensionData["nodeId"] = JsonSerializer.SerializeToElement(occurrence.NodeId);
            wait.ExtensionData["windowStart"] = JsonSerializer.SerializeToElement(window.Start);
            wait.ExtensionData["gameClosed"] = JsonSerializer.SerializeToElement(gameClosed);
            if (gameNote is not null) wait.ExtensionData["note"] = JsonSerializer.SerializeToElement(gameNote);
        }).ConfigureAwait(false);
        return true;
    }
}

public static class WorkflowTimeWindowWaitDisplay
{
    /// <summary>运行正在「等待到点」时给出简短说明（展示用）；不是这种等待返回 null。</summary>
    public static string? Describe(WaitStateRecord? wait)
    {
        if (wait is not { Kind: WorkflowRunner.TimeWindowStartWaitKind }) return null;
        var closed = wait.ExtensionData?.TryGetValue("gameClosed", out var c) == true && c.ValueKind == JsonValueKind.True;
        return $"等待时段开始（{wait.NextTriggerAt:HH:mm} 执行）" + (closed ? " · 已关闭游戏" : "");
    }
}
