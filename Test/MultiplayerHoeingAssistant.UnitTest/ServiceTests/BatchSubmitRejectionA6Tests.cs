using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// [A6] ADR-2026-09-16 第 4 条：瞬态拒绝分类重试的纯函数测试。
/// 可重试瞬态 {queue_full, task_busy, preempt_timeout}：单项 2 分钟预算 + 次数硬帽退避重试；
/// manual_stop_cooldown：终态 + 响亮通知（D1：F11 是用户最终权威，绝不自动重发）；
/// 其余永久性拒绝：维持既有终态跳过。作业终态失败（job.failed 带可重试码）同规则退回重发。
/// </summary>
public class BatchSubmitRejectionA6Tests
{
    private const int Gen = 42;
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static BatchExpectedItem Item(string name) => new(name, isOneDragon: false);

    /// <summary>已提交在飞项（jobId 已附着）。</summary>
    private static BatchExpectedItem SubmittedItem(string name, string jobId) => new(name, isOneDragon: false)
    {
        State = BatchItemState.Submitted,
        JobId = jobId,
        SubmitAttempts = 1,
    };

    private static BatchJobObservation Obs(string jobId, string name, string state, string? errorCode = null, bool cancelled = false)
        => new(jobId, name, Gen, state, cancelled, errorCode);

    // ---------- ClassifySubmitRejection ----------

    [Theory]
    [InlineData("queue_full")]
    [InlineData("task_busy")]
    [InlineData("preempt_timeout")]
    public void Classify_RetryableTransientCodes(string errorCode)
        => Assert.Equal(BatchSubmitRejectionKind.RetryableTransient, BatchReconcileDecider.ClassifySubmitRejection(errorCode));

    [Fact]
    public void Classify_ManualStopCooldown_IsTerminalNoRetry()
        => Assert.Equal(BatchSubmitRejectionKind.ManualStopCooldown, BatchReconcileDecider.ClassifySubmitRejection("manual_stop_cooldown"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rejected")]
    [InlineData("lost_job")]
    [InlineData("preempted")] // 让位终态：观察面新值，按非成功处理、不重试不崩
    [InlineData("some_future_code")] // 未知新码：按永久性处理，不崩
    public void Classify_UnknownAndOtherCodes_ArePermanent(string? errorCode)
        => Assert.Equal(BatchSubmitRejectionKind.Permanent, BatchReconcileDecider.ClassifySubmitRejection(errorCode));

    // ---------- CanRetryRejection ----------

    [Fact]
    public void CanRetry_WithinBudget_True()
        => Assert.True(BatchReconcileDecider.CanRetryRejection("preempt_timeout", 0, Now, Now.AddSeconds(30)));

    [Fact]
    public void CanRetry_CountCapReached_False()
        => Assert.False(BatchReconcileDecider.CanRetryRejection(
            "queue_full", BatchReconcileDecider.MaxRejectionRetries, Now, Now.AddSeconds(30)));

    [Fact]
    public void CanRetry_TimeBudgetExceeded_False()
        => Assert.False(BatchReconcileDecider.CanRetryRejection(
            "task_busy", 1, Now, Now.AddMinutes(2).AddTicks(1)));

    [Fact]
    public void CanRetry_TimeBudgetEdge_True()
        => Assert.True(BatchReconcileDecider.CanRetryRejection(
            "task_busy", 1, Now, Now.AddMinutes(2).AddTicks(-1)));

    [Theory]
    [InlineData("manual_stop_cooldown")]
    [InlineData("rejected")]
    [InlineData(null)]
    public void CanRetry_NonRetryableCodes_AlwaysFalse(string? errorCode)
        => Assert.False(BatchReconcileDecider.CanRetryRejection(errorCode, 0, Now, Now));

    // ---------- Decide：作业终态失败的分类重试 ----------

    [Fact]
    public void FailedJob_PreemptTimeout_WithinBudget_RetriesFromFailure()
    {
        var item = SubmittedItem("组A", "job-a");

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "failed", "preempt_timeout")], true, Gen, Now);

        // 不确认终态、不完成批次：退回重发（本拍仍视为在飞 → 附带 Wait）
        Assert.Contains(actions, a => a is BatchReconcileAction.RetryFromFailure { Index: 0, ErrorCode: "preempt_timeout" });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.ConfirmTerminal);
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.Complete);
    }

    [Fact]
    public void FailedJob_PreemptTimeout_CountCapExhausted_TerminalConfirmed()
    {
        var item = SubmittedItem("组A", "job-a");
        item.RejectionRetries = BatchReconcileDecider.MaxRejectionRetries;
        item.FirstRejectionUtc = Now.AddSeconds(-30);

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "failed", "preempt_timeout")], true, Gen, Now);

        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0, Cancelled: false, ErrorCode: "preempt_timeout" });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.RetryFromFailure);
        Assert.Contains(actions, a => a is BatchReconcileAction.Complete); // 全部终态 → 批次完成
    }

    [Fact]
    public void FailedJob_QueueFull_TimeBudgetExhausted_TerminalConfirmed()
    {
        var item = SubmittedItem("组A", "job-a");
        item.RejectionRetries = 2;
        item.FirstRejectionUtc = Now.AddMinutes(-3); // 超过 2 分钟预算

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "failed", "queue_full")], true, Gen, Now);

        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0, ErrorCode: "queue_full" });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.RetryFromFailure);
    }

    [Fact]
    public void FailedJob_ManualStopCooldown_TerminalNoRetry()
    {
        var item = SubmittedItem("组A", "job-a");

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "failed", "manual_stop_cooldown")], true, Gen, Now);

        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0, ErrorCode: "manual_stop_cooldown" });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.RetryFromFailure);
    }

    [Fact]
    public void FailedJob_UnknownCode_TerminalNoRetry()
    {
        var item = SubmittedItem("组A", "job-a");

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "failed", "task_start_failed")], true, Gen, Now);

        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0, ErrorCode: "task_start_failed" });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.RetryFromFailure);
    }

    [Fact]
    public void RejectedJob_QueueFull_WithinBudget_RetriesFromFailure()
    {
        var item = SubmittedItem("组A", "job-a");

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "rejected", "queue_full")], true, Gen, Now);

        Assert.Contains(actions, a => a is BatchReconcileAction.RetryFromFailure { Index: 0, ErrorCode: "queue_full" });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.ConfirmTerminal);
    }

    [Fact]
    public void SucceededJob_NeverRetried_EvenWithRetryableErrorCode()
    {
        var item = SubmittedItem("组A", "job-a");

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "succeeded", "preempt_timeout")], true, Gen, Now);

        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0 });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.RetryFromFailure);
    }

    [Fact]
    public void PreemptedJob_IsNotUserCancellation_AndKeepsFailureReason()
    {
        // Preemption is an engine interruption, not a user pressing stop.
        var item = SubmittedItem("组A", "job-a");

        var actions = BatchReconcileDecider.Decide(
            [item], [Obs("job-a", "组A", "cancelled", "preempted", cancelled: true)], true, Gen, Now);

        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.AbortUserCancelled);
        Assert.Contains(actions, a => a is BatchReconcileAction.ConfirmTerminal { ErrorCode: "preempted" });
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.RetryFromFailure);
    }

    /// <summary>锚点：组间缝隙事故形态——preempt_timeout 失败后退回重发，下一拍重新提交，最终跑完才 Complete。</summary>
    [Fact]
    public void AccidentReplay_PreemptTimeout_RetriedThenCompletesOnlyAfterSuccess()
    {
        var items = new List<BatchExpectedItem> { Item("联机队长-精英"), Item("联机队长-传奇") };

        // 拍1：提交第一项
        var a1 = BatchReconcileDecider.Decide(items, [], true, Gen, Now);
        var submit = Assert.IsType<BatchReconcileAction.Submit>(Assert.Single(a1));
        items[submit.Index].State = BatchItemState.Submitted;
        items[submit.Index].SubmitAttempts++;
        items[submit.Index].JobId = "j1";

        // 拍2：BGI 抢占超时（preempt_timeout 终态失败）→ 退回重发，绝不 ConfirmTerminal/Complete
        var a2 = BatchReconcileDecider.Decide(items, [Obs("j1", "联机队长-精英", "failed", "preempt_timeout")], true, Gen, Now);
        var retry = Assert.IsType<BatchReconcileAction.RetryFromFailure>(Assert.Single(a2, a => a is BatchReconcileAction.RetryFromFailure));
        Assert.DoesNotContain(a2, a => a is BatchReconcileAction.ConfirmTerminal);
        Assert.DoesNotContain(a2, a => a is BatchReconcileAction.Complete);
        items[retry.Index].State = BatchItemState.PendingSubmit;
        items[retry.Index].JobId = null;
        items[retry.Index].RejectionRetries++;
        items[retry.Index].FirstRejectionUtc ??= Now;

        // 拍3：重新提交（串行不变量：只发第一项）
        var a3 = BatchReconcileDecider.Decide(items, [], true, Gen, Now);
        var resubmit = Assert.IsType<BatchReconcileAction.Submit>(Assert.Single(a3));
        Assert.Equal(0, resubmit.Index);
        items[0].State = BatchItemState.Submitted;
        items[0].SubmitAttempts++;
        items[0].JobId = "j1b";

        // 拍4：第一项成功 → 确认 + 提交第二项
        var a4 = BatchReconcileDecider.Decide(items, [Obs("j1b", "联机队长-精英", "succeeded")], true, Gen, Now);
        Assert.Contains(a4, a => a is BatchReconcileAction.ConfirmTerminal { Index: 0 });
        Assert.Contains(a4, a => a is BatchReconcileAction.Submit { Index: 1 });
        Assert.DoesNotContain(a4, a => a is BatchReconcileAction.Complete);
    }
}
