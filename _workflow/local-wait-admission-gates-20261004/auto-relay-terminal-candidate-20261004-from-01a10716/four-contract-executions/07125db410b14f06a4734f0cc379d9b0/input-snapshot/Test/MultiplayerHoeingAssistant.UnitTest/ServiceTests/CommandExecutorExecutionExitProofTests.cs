using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// **R5 批次 3：助手侧退出判定接线**（组件级夹具；生产构造不注入接缝，不触达真实 IPC/BGI 进程）。
/// 语义：助手端"原任务已退出/已停止"只能由 BGI 的**按身份退出凭证**证明；
/// "空闲（executionIdle）／running=false"是弱观察，**不得**单独作为退出结论；
/// 身份或纪元形状不合法、回答对象身份不是请求的那一颗 ⇒ 一律保守未确认。
/// </summary>
public sealed class CommandExecutorExecutionExitProofTests
{
    private const int Pid = 4242;
    private const long Ticks = 638_000_000_000_000_000L;

    private static string StatusJson(Guid id, int pid = Pid, long ticks = Ticks, string extra = "")
        => $"{{\"executionInstanceId\":\"{id:N}\",\"bgiEpoch\":{{\"processId\":{pid},\"startTicksUtc\":{ticks}}}{extra}}}";

    private static CommandExecutor.ExecutionExitProof Proof(Guid id, bool confirmed, string reason)
        => new(id, confirmed, reason, null, null, null, null, null, null);

    /// <summary>完整"已确认"凭证响应；<paramref name="withActiveRoot"/>＝是否同时带上当前活动根身份。</summary>
    private static string ConfirmedJson(Guid id, bool withActiveRoot = true, string reason = "confirmed",
        bool includeAt = true, bool includeOrder = true)
    {
        var head = withActiveRoot ? $"{{\"executionInstanceId\":\"{id:N}\"," : "{\"executionInstanceId\":null,";
        return head +
               $"\"bgiEpoch\":{{\"processId\":{Pid},\"startTicksUtc\":{Ticks}}}," +
               $"\"executionExitConfirmed\":true,\"executionExitReason\":\"{reason}\"," +
               $"\"executionExitQueryInstanceId\":\"{id:N}\"" +
               (includeAt ? ",\"executionExitAtUtc\":\"2026-09-24T00:00:00Z\"" : "") +
               (includeOrder ? ",\"executionExitOrder\":7" : "") + "}";
    }

    [Fact]
    public void ParseExecutionIdentity_RequiresStrictShape()
    {
        var id = Guid.NewGuid();
        var parsed = CommandExecutor.ParseExecutionIdentity(StatusJson(id));
        Assert.Equal(new CommandExecutor.ExecutionIdentity(id, Pid, Ticks), parsed);

        Assert.Null(CommandExecutor.ParseExecutionIdentity(null));
        Assert.Null(CommandExecutor.ParseExecutionIdentity("{}"));
        // 身份非字符串（对象）不得被文本化后接受
        Assert.Null(CommandExecutor.ParseExecutionIdentity(
            $"{{\"executionInstanceId\":{{\"value\":\"{id:N}\"}},\"bgiEpoch\":{{\"processId\":{Pid},\"startTicksUtc\":{Ticks}}}}}"));
        // 纪元缺失 / 子字段为字符串
        Assert.Null(CommandExecutor.ParseExecutionIdentity($"{{\"executionInstanceId\":\"{id:N}\"}}"));
        Assert.Null(CommandExecutor.ParseExecutionIdentity(
            $"{{\"executionInstanceId\":\"{id:N}\",\"bgiEpoch\":{{\"processId\":\"{Pid}\",\"startTicksUtc\":{Ticks}}}}}"));
    }

    [Fact]
    public void ParseExecutionExitProof_RequiresIdentityEchoAndStrictShape()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var target = new CommandExecutor.ExecutionIdentity(id, Pid, Ticks);
        var confirmedJson = StatusJson(id, extra:
            $",\"executionExitConfirmed\":true,\"executionExitReason\":\"confirmed\"," +
            $"\"executionExitQueryInstanceId\":\"{id:N}\",\"executionExitAtUtc\":\"2026-09-24T00:00:00Z\"," +
            "\"executionExitObservedOutcome\":true,\"executionExitResult\":\"Preempted\"," +
            "\"executionExitStopRequested\":true,\"executionExitStopSource\":\"directional_stop_requested\",\"executionExitOrder\":7");

        var proof = CommandExecutor.ParseExecutionExitProof(confirmedJson, target);
        Assert.True(proof!.Confirmed);
        Assert.Equal("confirmed", proof.Reason);
        Assert.Equal("Preempted", proof.Result);
        Assert.Equal("directional_stop_requested", proof.StopSource);
        Assert.Equal(7, proof.Order);
        Assert.True(proof.ObservedOutcome);

        // 回答对象是**别的**执行根 ⇒ 不得把 A 的退出当成 B 的
        var wrongSubject = CommandExecutor.ParseExecutionExitProof(StatusJson(id, extra:
            $",\"executionExitConfirmed\":true,\"executionExitReason\":\"confirmed\",\"executionExitQueryInstanceId\":\"{other:N}\""), target);
        Assert.False(wrongSubject!.Confirmed);
        Assert.Equal("identity_mismatch", wrongSubject.Reason);

        // 声明 confirmed 却没回显回答对象 ⇒ 保守拒绝
        var noEcho = CommandExecutor.ParseExecutionExitProof(StatusJson(id, extra:
            ",\"executionExitConfirmed\":true,\"executionExitReason\":\"confirmed\""), target);
        Assert.False(noEcho!.Confirmed);
        Assert.Equal("identity_unproven", noEcho.Reason);

        // 纪元不同 ⇒ 未知（不得据旧代回答）
        Assert.Null(CommandExecutor.ParseExecutionExitProof(
            StatusJson(id, ticks: Ticks + 1, extra: ",\"executionExitConfirmed\":true,\"executionExitReason\":\"confirmed\""), target));

        // 形状非法 ⇒ null
        Assert.Null(CommandExecutor.ParseExecutionExitProof(StatusJson(id, extra: ",\"executionExitReason\":\"confirmed\""), target));
        Assert.Null(CommandExecutor.ParseExecutionExitProof(StatusJson(id, extra: ",\"executionExitConfirmed\":\"true\",\"executionExitReason\":\"confirmed\""), target));
        Assert.Null(CommandExecutor.ParseExecutionExitProof("{not json", target));
    }

    [Fact]
    public void ParseExecutionExitProof_RejectsContradictoryOrIncompleteConfirmation()
    {
        var id = Guid.NewGuid();
        var target = new CommandExecutor.ExecutionIdentity(id, Pid, Ticks);

        // confirmed=true 却带其他 reason ⇒ 自相矛盾，按未确认处理
        var conflict = CommandExecutor.ParseExecutionExitProof(ConfirmedJson(id, reason: "not_exited"), target);
        Assert.False(conflict!.Confirmed);
        Assert.Equal("reason_conflict", conflict.Reason);

        // confirmed=true 但缺时刻／顺序号 ⇒ 形状不完整，按未确认处理
        var noAt = CommandExecutor.ParseExecutionExitProof(ConfirmedJson(id, includeAt: false), target);
        Assert.False(noAt!.Confirmed);
        Assert.Equal("shape_incomplete", noAt.Reason);
        var noOrder = CommandExecutor.ParseExecutionExitProof(ConfirmedJson(id, includeOrder: false), target);
        Assert.False(noOrder!.Confirmed);
        Assert.Equal("shape_incomplete", noOrder.Reason);
    }

    [Fact]
    public void ParseExecutionExitProof_AcceptsProofWhenNoActiveRootRemains()
    {
        // 目标退出后 BGI 正当返回 executionInstanceId=null：不得因此拒收该执行根的退出凭证。
        var id = Guid.NewGuid();
        var target = new CommandExecutor.ExecutionIdentity(id, Pid, Ticks);

        var proof = CommandExecutor.ParseExecutionExitProof(ConfirmedJson(id, withActiveRoot: false), target);

        Assert.True(proof!.Confirmed);
        Assert.Equal("confirmed", proof.Reason);
        Assert.Equal(id, proof.QueryInstanceId);
        // 反例证据（旧规则镜像）：旧实现用"顶层执行根身份"校验纪元，对这份合法凭证会得到 null ⇒ 会被判定为无证据。
        // 该镜像断言让"修复前的红"在夹具里留痕，而不是只靠会诊口述。
        Assert.Null(CommandExecutor.ParseExecutionIdentity(ConfirmedJson(id, withActiveRoot: false)));
    }

    [Fact]
    public void SupportsExecutionExitProof_DistinguishesOldPeerShape()
    {
        var id = Guid.NewGuid();
        Assert.True(CommandExecutor.SupportsExecutionExitProof(StatusJson(id, extra: ",\"executionExitReason\":\"not_exited\"")));
        Assert.False(CommandExecutor.SupportsExecutionExitProof(StatusJson(id))); // 旧版 BGI：无凭证字段
        Assert.False(CommandExecutor.SupportsExecutionExitProof("{}"));
        Assert.False(CommandExecutor.SupportsExecutionExitProof("{not json"));
    }

    [Fact]
    public void IsExecutionIdle_RequiresStrictBoolean()
    {
        Assert.True(CommandExecutor.IsExecutionIdle("{\"executionIdle\":true}"));
        Assert.False(CommandExecutor.IsExecutionIdle("{\"executionIdle\":false}"));
        Assert.False(CommandExecutor.IsExecutionIdle("{\"executionIdle\":\"true\"}"));
        Assert.False(CommandExecutor.IsExecutionIdle("{}"));
        Assert.False(CommandExecutor.IsExecutionIdle(null));
    }

    [Fact]
    public async Task WaitTaskSlotSettled_WithIdentity_RequiresReceipt_NotIdle()
    {
        var id = Guid.NewGuid();
        var logs = new List<string>();
        var executor = new CommandExecutor(null!, "unused") { TaskSettlePollBudgetForTest = 1 };
        // BGI 报告"空闲"，但从未产生退出凭证
        executor.TaskStatusQueryOverride = _ =>
            Task.FromResult<(bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)?>((false, false, null, null));
        executor.TaskExecutionExitQueryOverride = (_, _, _) =>
            Task.FromResult<CommandExecutor.ExecutionExitProof?>(Proof(id, confirmed: false, reason: "not_exited"));

        var settled = await executor.WaitTaskSlotSettledAsync("[夹具]", logs.Add,
            new CommandExecutor.ExecutionIdentity(id, Pid, Ticks));

        Assert.False(settled); // 空闲不是退出凭证
        Assert.Contains(logs, line => line.Contains("退出凭证"));
    }

    [Fact]
    public async Task WaitTaskSlotSettled_WithIdentity_SucceedsOnConfirmedReceipt()
    {
        var id = Guid.NewGuid();
        var logs = new List<string>();
        var executor = new CommandExecutor(null!, "unused") { TaskSettlePollBudgetForTest = 2 };
        executor.TaskExecutionExitQueryOverride = (_, _, _) =>
            Task.FromResult<CommandExecutor.ExecutionExitProof?>(Proof(id, confirmed: true, reason: "confirmed"));
        executor.ExecutionIdleQueryOverride = () => Task.FromResult(true);

        var settled = await executor.WaitTaskSlotSettledAsync("[夹具]", logs.Add,
            new CommandExecutor.ExecutionIdentity(id, Pid, Ticks));

        Assert.True(settled);
        Assert.Contains(logs, line => line.Contains("已取得原执行根退出凭证"));
    }

    [Fact]
    public async Task WaitTaskSlotSettled_WithIdentity_RequiresIdleConjunct_SoSuccessorIsNotPassedThrough()
    {
        var id = Guid.NewGuid();
        var logs = new List<string>();
        var executor = new CommandExecutor(null!, "unused") { TaskSettlePollBudgetForTest = 1 };
        executor.TaskExecutionExitQueryOverride = (_, _, _) =>
            Task.FromResult<CommandExecutor.ExecutionExitProof?>(Proof(id, confirmed: true, reason: "confirmed"));
        // A 已退出，但槽位被继任根占用：A 的凭证不得放行
        executor.ExecutionIdleQueryOverride = () => Task.FromResult(false);

        var settled = await executor.WaitTaskSlotSettledAsync("[夹具]", logs.Add,
            new CommandExecutor.ExecutionIdentity(id, Pid, Ticks));

        Assert.False(settled);
        Assert.Contains(logs, line => line.Contains("当前空闲"));
    }

    [Fact]
    public void MergeExitContractSupport_NeverDowngradesUnknownToUnsupported()
    {
        Assert.Equal(CommandExecutor.ExitContractSupport.Supported,
            CommandExecutor.MergeExitContractSupport(CommandExecutor.ExitContractSupport.Unsupported, CommandExecutor.ExitContractSupport.Supported));
        Assert.Equal(CommandExecutor.ExitContractSupport.Unknown,
            CommandExecutor.MergeExitContractSupport(CommandExecutor.ExitContractSupport.Unsupported, CommandExecutor.ExitContractSupport.Unknown));
        Assert.Equal(CommandExecutor.ExitContractSupport.Unsupported,
            CommandExecutor.MergeExitContractSupport(CommandExecutor.ExitContractSupport.Unsupported, CommandExecutor.ExitContractSupport.Unsupported));
    }

    [Fact]
    public void MergeExitContractSupport_FirstRealSampleInitialisesAccumulator()
    {
        // 尚未采样 ⇒ 首次实际探测决定初值（否则旧版永远无法确认 Unsupported，合法收尾不可达）
        Assert.Equal(CommandExecutor.ExitContractSupport.Unsupported,
            CommandExecutor.MergeExitContractSupport(null, CommandExecutor.ExitContractSupport.Unsupported));
        Assert.Equal(CommandExecutor.ExitContractSupport.Supported,
            CommandExecutor.MergeExitContractSupport(null, CommandExecutor.ExitContractSupport.Supported));
        // 连续 Unsupported 保持 Unsupported
        Assert.Equal(CommandExecutor.ExitContractSupport.Unsupported,
            CommandExecutor.MergeExitContractSupport(CommandExecutor.ExitContractSupport.Unsupported, CommandExecutor.ExitContractSupport.Unsupported));
        // Unknown→Unsupported 仍为 Unknown（探测失败过就不降级）；Unsupported→Unknown 变 Unknown（保守）
        Assert.Equal(CommandExecutor.ExitContractSupport.Unknown,
            CommandExecutor.MergeExitContractSupport(CommandExecutor.ExitContractSupport.Unknown, CommandExecutor.ExitContractSupport.Unsupported));
        Assert.Equal(CommandExecutor.ExitContractSupport.Unknown,
            CommandExecutor.MergeExitContractSupport(CommandExecutor.ExitContractSupport.Unsupported, CommandExecutor.ExitContractSupport.Unknown));
    }

    [Fact]
    public void IsRunningTrue_RequiresStrictBoolean()
    {
        Assert.True(CommandExecutor.IsRunningTrue("{\"running\":true}"));
        Assert.False(CommandExecutor.IsRunningTrue("{\"running\":false}"));
        Assert.False(CommandExecutor.IsRunningTrue("{\"running\":\"true\"}"));
        Assert.False(CommandExecutor.IsRunningTrue("{}"));
    }

    [Fact]
    public void ClassifyStatus_NeverTreatsMissingOrMalformedRunningAsNoActiveRoot()
    {
        var id = Guid.NewGuid();
        var supportedEpoch = $"\"bgiEpoch\":{{\"processId\":{Pid},\"startTicksUtc\":{Ticks}}},\"executionExitReason\":\"not_exited\"";

        // running 缺失 / 类型错误 ⇒ Unknown（不得变成"没有活动根"）
        Assert.Equal(CommandExecutor.ExecutionRootState.Unknown,
            CommandExecutor.ClassifyStatus($"{{\"executionInstanceId\":\"{id:N}\",{supportedEpoch}}}").State);
        Assert.Equal(CommandExecutor.ExecutionRootState.Unknown,
            CommandExecutor.ClassifyStatus($"{{\"running\":\"false\",\"executionInstanceId\":\"{id:N}\",{supportedEpoch}}}").State);
        Assert.Equal(CommandExecutor.ExitContractSupport.Unknown,
            CommandExecutor.ClassifyStatus($"{{\"running\":\"false\",\"executionInstanceId\":\"{id:N}\",{supportedEpoch}}}").Support);

        // 身份畸形但 running=true ⇒ 有根但身份不可用（保守），不得当作没有根
        Assert.Equal(CommandExecutor.ExecutionRootState.IdentityUnavailable,
            CommandExecutor.ClassifyStatus($"{{\"running\":true,\"executionInstanceId\":{{\"value\":\"{id:N}\"}},{supportedEpoch}}}").State);

        // 显式 running=false + 支持契约字段 ⇒ 有证据地"没有活动根"
        var idle = CommandExecutor.ClassifyStatus($"{{\"running\":false,\"executionInstanceId\":null,{supportedEpoch}}}");
        Assert.Equal(CommandExecutor.ExecutionRootState.NoActiveRoot, idle.State);
        Assert.Equal(CommandExecutor.ExitContractSupport.Supported, idle.Support);

        // 旧版形状（无凭证字段但 running 合法）⇒ Unsupported，仍可走空闲弱证据
        var legacy = CommandExecutor.ClassifyStatus($"{{\"running\":false,\"executionInstanceId\":null,\"bgiEpoch\":{{\"processId\":{Pid},\"startTicksUtc\":{Ticks}}}}}");
        Assert.Equal(CommandExecutor.ExecutionRootState.NoActiveRoot, legacy.State);
        Assert.Equal(CommandExecutor.ExitContractSupport.Unsupported, legacy.Support);
    }

    [Fact]
    public async Task WaitTaskSlotSettled_WithoutIdentity_SupportedPeerRequiresEvidenceOfNoActiveRoot()
    {
        // 支持契约 + 有活动根但拿不到身份 ⇒ 空闲不足以放行（保守）
        var logs = new List<string>();
        var executor = new CommandExecutor(null!, "unused") { TaskSettlePollBudgetForTest = 1 };
        executor.ExecutionStateProbeOverride = () => Task.FromResult(new CommandExecutor.ExecutionStateProbe(
            CommandExecutor.ExecutionRootState.IdentityUnavailable, null, CommandExecutor.ExitContractSupport.Supported));
        Assert.False(await executor.WaitTaskSlotSettledAsync("[夹具]", logs.Add));
        Assert.Contains(logs, line => line.Contains("身份不可用"));

        // 支持契约 + 有证据地"没有活动执行根" ⇒ 没有可等的退出，可继续
        var logs2 = new List<string>();
        executor.ExecutionStateProbeOverride = () => Task.FromResult(new CommandExecutor.ExecutionStateProbe(
            CommandExecutor.ExecutionRootState.NoActiveRoot, null, CommandExecutor.ExitContractSupport.Supported));
        Assert.True(await executor.WaitTaskSlotSettledAsync("[夹具]", logs2.Add));
        Assert.Contains(logs2, line => line.Contains("没有活动执行根"));
    }

    [Fact]
    public async Task WaitTaskSlotSettled_WithoutIdentity_OldPeerKeepsIdleObservation()
    {
        var logs = new List<string>();
        var executor = new CommandExecutor(null!, "unused") { TaskSettlePollBudgetForTest = 1 };
        executor.ExecutionStateProbeOverride = () => Task.FromResult(new CommandExecutor.ExecutionStateProbe(
            CommandExecutor.ExecutionRootState.ActiveRoot, null, CommandExecutor.ExitContractSupport.Unsupported));
        executor.ExecutionIdleQueryOverride = () => Task.FromResult(true);

        Assert.True(await executor.WaitTaskSlotSettledAsync("[夹具]", logs.Add));
        Assert.Contains(logs, line => line.Contains("弱证据"));
    }

    [Fact]
    public async Task WaitExecutionExit_ConfirmsOnlyWhenReceiptAppears()
    {
        var id = Guid.NewGuid();
        var calls = 0;
        var executor = new CommandExecutor(null!, "unused");
        executor.TaskExecutionExitQueryOverride = (_, _, _) =>
        {
            calls++;
            return Task.FromResult<CommandExecutor.ExecutionExitProof?>(
                calls >= 2 ? Proof(id, confirmed: true, reason: "confirmed") : Proof(id, confirmed: false, reason: "not_exited"));
        };

        Assert.True(await executor.WaitExecutionExitAsync(
            new CommandExecutor.ExecutionIdentity(id, Pid, Ticks), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20)));
        Assert.True(calls >= 2);

        executor.TaskExecutionExitQueryOverride = (_, _, _) =>
            Task.FromResult<CommandExecutor.ExecutionExitProof?>(Proof(id, confirmed: false, reason: "unknown_instance"));
        Assert.False(await executor.WaitExecutionExitAsync(
            new CommandExecutor.ExecutionIdentity(id, Pid, Ticks), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(20)));
    }
}
