using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// [A4.2] BatchReconcileDecider 纯函数穷尽测试（总计划 §4.5 reconcile 决策）。
/// 锚点用例 = 本次事故形态："锄地继续"式收尾（Complete）严格在全部期望项终态确认之后才允许输出。
/// 测试内 ApplyActions 模拟调用方语义（ConfirmTerminal→TerminalConfirmed，Attach→记录 jobId，
/// Submit/Resubmit→Submitted+SubmitAttempts++，EpochChanged→Submitted 退回 PendingSubmit）。
/// </summary>
public class BatchReconcileDeciderTests
{
    private const int Gen = 42;

    /// <summary>[A6] Decide 的 nowUtc 入参：固定时钟保证用例确定性（重试时间预算判定可复现）。</summary>
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static List<BatchExpectedItem> Items(params string[] names)
        => names.Select(n => new BatchExpectedItem(n, isOneDragon: false)).ToList();

    private static BatchJobObservation Obs(string jobId, string name, string state, bool cancelled = false, string? errorCode = null)
        => new(jobId, name, Gen, state, cancelled, errorCode);

    /// <summary>调用方语义镜像：把动作应用回期望项（生产代码在 MainViewModel reconcile 循环里做同样的事）。</summary>
    private static void ApplyActions(List<BatchExpectedItem> items, IEnumerable<BatchReconcileAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case BatchReconcileAction.Submit s:
                    items[s.Index].State = BatchItemState.Submitted;
                    items[s.Index].SubmitAttempts++;
                    break;
                case BatchReconcileAction.Resubmit r:
                    items[r.Index].SubmitAttempts++;
                    break;
                case BatchReconcileAction.Attach a:
                    items[a.Index].JobId = a.JobId;
                    break;
                case BatchReconcileAction.RetryFromFailure rf:
                    items[rf.Index].State = BatchItemState.PendingSubmit;
                    items[rf.Index].JobId = null;
                    items[rf.Index].RejectionRetries++;
                    items[rf.Index].FirstRejectionUtc ??= Now;
                    break;
                case BatchReconcileAction.ConfirmTerminal c:
                    items[c.Index].State = BatchItemState.TerminalConfirmed;
                    items[c.Index].TerminalWasCancelled = c.Cancelled;
                    items[c.Index].TerminalErrorCode = c.ErrorCode;
                    break;
                case BatchReconcileAction.EpochChanged:
                    foreach (var item in items.Where(i => i.State == BatchItemState.Submitted))
                    {
                        item.State = BatchItemState.PendingSubmit;
                        item.JobId = null;
                    }
                    break;
            }
        }
    }

    private static IReadOnlyList<BatchReconcileAction> Tick(
        List<BatchExpectedItem> items, List<BatchJobObservation> jobs, bool epochMatch = true)
    {
        var actions = BatchReconcileDecider.Decide(items, jobs, epochMatch, Gen, Now);
        ApplyActions(items, actions);
        return actions;
    }

    [Fact]
    public void EmptyBatch_CompletesImmediately()
    {
        var actions = BatchReconcileDecider.Decide([], [], true, Gen, Now);
        Assert.Contains(actions, a => a is BatchReconcileAction.Complete);
    }

    [Fact]
    public void FirstTick_SubmitsFirstItem_OnlyOneInFlight()
    {
        var items = Items("联机队长-传奇", "联机队长-次数盾", "联机队长-精英");
        var actions = BatchReconcileDecider.Decide(items, [], true, Gen, Now);

        var submit = Assert.IsType<BatchReconcileAction.Submit>(Assert.Single(actions));
        Assert.Equal(0, submit.Index);
    }

    [Fact]
    public void RunningJob_Waits_NoPrematureAdvance()
    {
        var items = Items("组A", "组B");
        Tick(items, []); // 提交组A
        items[0].JobId = "job-a";

        var actions = Tick(items, [Obs("job-a", "组A", "running")]);

        Assert.Contains(actions, a => a is BatchReconcileAction.Wait);
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.Submit);
        Assert.Equal(BatchItemState.Submitted, items[0].State);
        Assert.Equal(BatchItemState.PendingSubmit, items[1].State);
    }

    [Fact]
    public void TerminalSucceeded_ConfirmsAndSubmitsNextSameTick()
    {
        var items = Items("组A", "组B");
        Tick(items, []);
        items[0].JobId = "job-a";

        var actions = Tick(items, [Obs("job-a", "组A", "succeeded")]);

        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0 });
        Assert.Contains(actions, a => a is BatchReconcileAction.Submit { Index: 1 });
        Assert.Equal(BatchItemState.TerminalConfirmed, items[0].State);
    }

    /// <summary>锚点：本次事故形态——收尾（Complete）严格在三个组全部终态之后。</summary>
    [Fact]
    public void AccidentReplay_CompleteNeverBeforeAllThreeGroupsTerminal()
    {
        var items = Items("联机队长-传奇", "联机队长-次数盾", "联机队长-精英");

        // 拍1：下发组1（running）
        Tick(items, []);
        items[0].JobId = "j1";
        var a1 = Tick(items, [Obs("j1", "联机队长-传奇", "running")]);
        Assert.DoesNotContain(a1, a => a is BatchReconcileAction.Complete);

        // 拍2：组1完成 → 确认+下发组2；组2 running
        Tick(items, [Obs("j1", "联机队长-传奇", "succeeded")]);
        items[1].JobId = "j2";
        var a2 = Tick(items, [Obs("j2", "联机队长-次数盾", "running")]);
        Assert.DoesNotContain(a2, a => a is BatchReconcileAction.Complete);
        Assert.DoesNotContain(a2, a => a is BatchReconcileAction.Submit);

        // 拍3：组2 failed（记 errorCode 继续），组3 queued → 仍不得 Complete
        Tick(items, [Obs("j2", "联机队长-次数盾", "failed", errorCode: "task_start_failed")]);
        items[2].JobId = "j3";
        var a3 = Tick(items, [Obs("j3", "联机队长-精英", "queued")]);
        Assert.DoesNotContain(a3, a => a is BatchReconcileAction.Complete);

        // 拍4：组3 succeeded → 这一刻才允许 Complete
        var a4 = Tick(items, [Obs("j3", "联机队长-精英", "succeeded")]);
        Assert.Contains(a4, a => a is BatchReconcileAction.Complete);
        Assert.All(items, i => Assert.Equal(BatchItemState.TerminalConfirmed, i.State));
    }

    [Fact]
    public void CancelledJob_ConfirmsThenAborts_NoFurtherSubmit()
    {
        var items = Items("组A", "组B");
        Tick(items, []);
        items[0].JobId = "job-a";

        var actions = Tick(items, [Obs("job-a", "组A", "cancelled", cancelled: true)]);

        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0, Cancelled: true });
        Assert.Contains(actions, a => a is BatchReconcileAction.AbortUserCancelled { Index: 0 });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.Submit);
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.Complete);
    }

    [Fact]
    public void WasCancelledFlag_EvenIfStateNotCancelled_Aborts()
    {
        var items = Items("组A");
        Tick(items, []);
        items[0].JobId = "job-a";

        // BGI 侧 completed(cancelled=true) 口径：state=succeeded 但 wasCancelled=true
        var actions = Tick(items, [Obs("job-a", "组A", "succeeded", cancelled: true)]);
        Assert.Contains(actions, a => a is BatchReconcileAction.AbortUserCancelled);
    }

    [Fact]
    public void EpochMismatch_SubmittedItemsResetToPending()
    {
        var items = Items("组A", "组B");
        Tick(items, []);
        items[0].JobId = "job-a";

        var actions = BatchReconcileDecider.Decide(items, [Obs("job-a", "组A", "running")], epochMatch: false, Gen, Now);
        Assert.Contains(actions, a => a is BatchReconcileAction.EpochChanged);

        ApplyActions(items, actions);
        Assert.All(items, i => Assert.Equal(BatchItemState.PendingSubmit, i.State));
        Assert.Null(items[0].JobId);

        // 新纪元下重新提交组A（幂等键同 generation+name，BGI 注册表去重）
        var next = Tick(items, [], epochMatch: true);
        Assert.Contains(next, a => a is BatchReconcileAction.Submit { Index: 0 });
    }

    [Fact]
    public void AttachByName_RecoversLostSubmitResponse()
    {
        var items = Items("组A");
        Tick(items, []); // 提交，但假设应答帧丢失：JobId 仍为 null
        Assert.Null(items[0].JobId);

        // 快照里按 generation+name 找回（BGI 实际已入队）
        var actions = Tick(items, [Obs("job-a", "组A", "queued")]);
        Assert.Contains(actions, a => a is BatchReconcileAction.Attach { Index: 0, JobId: "job-a" });
        Assert.Equal("job-a", items[0].JobId);
    }

    [Fact]
    public void AttachedJobVanished_SameEpoch_ResubmitsUpToLimit_ThenLostJobTerminal()
    {
        var items = Items("组A");
        Tick(items, []);
        items[0].JobId = "job-a";

        // 作业从快照消失（同纪元 not_found = 句柄淘汰/异常）→ 重提交
        var a1 = Tick(items, []);
        Assert.Contains(a1, a => a is BatchReconcileAction.Resubmit { Index: 0 });

        // 重提交后拿到新句柄又消失…… 到达上限后按 lost_job 终态确认（不永等）
        items[0].SubmitAttempts = BatchReconcileDecider.MaxSubmitAttempts;
        var a2 = Tick(items, []);
        Assert.Contains(a2, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0, ErrorCode: "lost_job" });
        Assert.Equal(BatchItemState.TerminalConfirmed, items[0].State);

        // 全部确认 → Complete（lost_job 也推进批次，与旧循环"失败记日志继续"同语义）
        var a3 = Tick(items, []);
        Assert.Contains(a3, a => a is BatchReconcileAction.Complete);
    }

    [Fact]
    public void SerialInvariant_NeverTwoSubmitsInOneBatch()
    {
        var items = Items("组A", "组B", "组C");
        var jobs = new List<BatchJobObservation>();

        // 模拟 20 拍随机推进：任何一拍不得出现两个 Submit
        var rng = new Random(7);
        for (var tick = 0; tick < 20; tick++)
        {
            var actions = Tick(items, jobs);
            Assert.True(actions.Count(a => a is BatchReconcileAction.Submit) <= 1);

            // 随机让当前在飞作业跑到 succeeded
            var inFlight = items.Select((it, idx) => (it, idx))
                .FirstOrDefault(t => t.it.State == BatchItemState.Submitted && t.it.JobId is not null);
            if (inFlight.it is not null && rng.Next(2) == 0)
            {
                jobs = [Obs(inFlight.it.JobId!, inFlight.it.Name, "succeeded")];
            }

            // 提交后补句柄（模拟应答到达）
            foreach (var it in items.Where(i => i.State == BatchItemState.Submitted && i.JobId is null))
            {
                it.JobId = $"job-{it.Name}";
                jobs = [..jobs, Obs(it.JobId, it.Name, "queued")];
            }
        }
    }
}
