using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// **R5.2 B3 第 2 步：CommandExecutor（E3 `start_group`）适配器接线验收**（施工方内置、owner 0 点击）。
/// 接线态＝注入准入委托；**未注入＝既有直启路径逐字不变**（故这些夹具只覆盖「接线态的准入边界」，
/// 不进入核心启动体——核心体涉及真实 IPC/BGI 进程，禁止在单测中触达）。
/// </summary>
public sealed class CommandExecutorExternalStartAdmissionTests
{
    private static RemoteCommand StartGroupCommand()
        => new() { Cmd = "start_group", Params = new() { ["groupName"] = "测试组" } };

    /// <summary>
    /// 接线态下 `start_group` 必须**先经准入**：候选按 §2.2 兼容映射（namespace=v2、
    /// workflowId/resourceRef=`group:{组名}`、触发出现身份含门面回填占位符）；被阻断＝按门禁结论回执且**未启动**。
    /// </summary>
    [Fact]
    public async Task StartGroup_RoutesThroughAdmission_BlockedMapsToFailure()
    {
        var calls = new List<ExternalStartAdmissionRequest>();
        var executor = new CommandExecutor(null!, "unused",
            externalStartAdmission: (request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(new ExternalStartAdmissionOutcome(
                    ExternalStartAdmissionStatus.Blocked, "f11_active", "F11 独立停止闸门激活（未发生租约副作用）"));
            });

        var result = await executor.ExecuteAsync(StartGroupCommand());

        var request = Assert.Single(calls);
        Assert.Equal("v2", request.Namespace);                                  // §2.1 可信来源：远程命令=v2
        Assert.Equal("group:测试组", request.WorkflowId);                        // §2.2 workflowId 段
        Assert.Equal("group:测试组", request.ResourceRef);
        Assert.Equal("v2:remote:{requestIdentity}", request.TriggerOccurrenceId); // 占位符由门面回填
        Assert.Equal("v2:start_group", request.SourceDetail);
        Assert.Equal("failed", result.Status);
        Assert.Equal("f11_active", result.ErrorCode);
    }

    /// <summary>
    /// **顺序纪律**：既有「副作用前段」（批次占用检查）必须**先于**准入——批次忙时不发起准入、更不启动。
    /// </summary>
    [Fact]
    public async Task StartGroup_BatchBusyGuard_PrecedesAdmission()
    {
        var admitted = 0;
        var executor = new CommandExecutor(null!, "unused", isBatchInFlight: () => true,
            externalStartAdmission: (_, _) =>
            {
                admitted++;
                return Task.FromResult(new ExternalStartAdmissionOutcome(
                    ExternalStartAdmissionStatus.Accepted, "accepted", "ok"));
            });

        var result = await executor.ExecuteAsync(StartGroupCommand());

        Assert.Equal("failed", result.Status);
        Assert.Equal("batch_busy", result.ErrorCode);
        Assert.Equal(0, admitted); // 准入未被调用（更没有启动）
    }

    /// <summary>
    /// 准入结论三态映射（**不改线协议**：`Status` 仍只用既有 `success/failed`）：
    /// 未获准/不可考一律 `failed` ＋ 明确错误码，其中不可考用既有 `result_unknown` 口径且**禁止重发**。
    /// </summary>
    [Fact]
    public async Task StartGroup_AdmissionOutcomeMapping_UsesExistingWireVocabulary()
    {
        async Task<CommandResult> Run(ExternalStartAdmissionOutcome outcome)
        {
            var executor = new CommandExecutor(null!, "unused",
                externalStartAdmission: (_, _) => Task.FromResult(outcome));
            return await executor.ExecuteAsync(StartGroupCommand());
        }

        var rejected = await Run(new ExternalStartAdmissionOutcome(
            ExternalStartAdmissionStatus.Rejected, "capability_blocked", "缺少能力"));
        Assert.Equal("failed", rejected.Status);
        Assert.Equal("capability_blocked", rejected.ErrorCode);

        var reconcile = await Run(new ExternalStartAdmissionOutcome(
            ExternalStartAdmissionStatus.NeedReconcile, "takeover_persist_failed", "接管未落盘"));
        Assert.Equal("failed", reconcile.Status);
        Assert.Equal("result_unknown", reconcile.ErrorCode);   // 既有口径：不得重发
        Assert.Contains("不得重发", reconcile.Message);
    }

    /// <summary>
    /// **会诊重要项**：准入调用异常**不得**落成可重试的普通失败（异常可能发生在核心已执行之后）——
    /// 一律按既有 `result_unknown` 口径回执且明确禁止重发。
    /// </summary>
    [Fact]
    public async Task StartGroup_AdmissionThrows_MapsToResultUnknownNeverRetryable()
    {
        var executor = new CommandExecutor(null!, "unused",
            externalStartAdmission: (_, _) => throw new InvalidOperationException("admission exploded"));

        var result = await executor.ExecuteAsync(StartGroupCommand());

        Assert.Equal("failed", result.Status);
        Assert.Equal("result_unknown", result.ErrorCode);
        Assert.Contains("禁止重发", result.Message);
    }

    /// <summary>
    /// **E3 `start_oneclick` 同源接线**：候选按 §2.2 映射为 `onedragon:{配置名}`（不得与 `group:` 混用），
    /// 且被阻断时**未启动**。与 `start_group` 共用同一 `StartViaAdmissionAsync` 接线助手（路径一致）。
    /// </summary>
    [Fact]
    public async Task StartOneClick_RoutesThroughAdmission_OneDragonCandidate()
    {
        var calls = new List<ExternalStartAdmissionRequest>();
        var executor = new CommandExecutor(null!, "unused",
            externalStartAdmission: (request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(new ExternalStartAdmissionOutcome(
                    ExternalStartAdmissionStatus.Blocked, "need_preempt_confirm", "占用中"));
            });

        var result = await executor.ExecuteAsync(new RemoteCommand
        {
            Cmd = "start_oneclick",
            Params = new() { ["configName"] = "测试一条龙", ["startFromTaskId"] = "task-1" },
        });

        var request = Assert.Single(calls);
        Assert.Equal("v2", request.Namespace);
        Assert.Equal("onedragon:测试一条龙", request.WorkflowId);   // §2.2：start_oneclick 段
        Assert.Equal("onedragon:测试一条龙", request.ResourceRef);
        Assert.Equal("v2:remote:{requestIdentity}", request.TriggerOccurrenceId);
        Assert.Equal("v2:start_oneclick", request.SourceDetail);
        Assert.Equal("failed", result.Status);
        Assert.Equal("need_preempt_confirm", result.ErrorCode);
    }

    /// <summary>
    /// **B3 第 3 步（接线态的冲突纪律）**：既有路径对 BGI「task_already_running」无损拒绝做 1s×6 重试；
    /// **接线态（获准后）一律 0 次**——每次重新发送都需要新的发送许可（§3.2a），盲目重发即「未准入即重发」。
    /// **冲突后只停止本轮发送**：适配层把非 success 一律映射为 Unknown（待对账），
    /// 故不得表述为「确定未受理／可直接重新准入」（责任结清证据与合法重新准入闭环仍待补）。
    /// </summary>
    [Fact]
    public void ConflictRetryLimit_WiredPathHasZeroBlindRetries()
    {
        Assert.Equal(6, CommandExecutor.ConflictRetryLimit(allowLegacyRetry: true));  // 未接线＝既有语义
        Assert.Equal(0, CommandExecutor.ConflictRetryLimit(allowLegacyRetry: false)); // 接线＝零盲目重发
    }

    /// <summary>
    /// **B3 第 3 步：无副作用冲突解析决策表**（只读状态 + 本机批次/重试窗口标志 ⇒ 决策）：
    /// 空闲/状态未知=Idle；任务已结束但上下文未消费且本机无批次=IdleAfterClearingEndedContext（孤儿）；
    /// 在跑无上下文=PreemptRunning；在跑带上下文且本机无批次=PreemptAfterClearingContext；
    /// 在跑带上下文且批次在跑/恢复重试窗口=RefuseContextHeld（无损拒绝）。
    /// **解析本身不产生副作用**（清上下文/恢复取消/suspend 均在决策之后的动作阶段执行）。
    /// </summary>
    [Theory]
    [InlineData("unknown", false, "Idle")]                          // 状态未知（查询失败）
    [InlineData("idleNoContext", false, "Idle")]                    // 空闲无上下文
    [InlineData("idleWithContext", false, "IdleAfterClearingEndedContext")] // 已结束但上下文未消费（孤儿）
    [InlineData("idleWithContext", true, "Idle")]                   // 已结束带上下文且批次在跑＝正常间隙态（不清）
    [InlineData("runningNoContext", false, "PreemptRunning")]       // 在跑无上下文
    [InlineData("runningNoContext", true, "PreemptRunning")]        // 在跑无上下文 + 批次在跑：仍不因此改判拒绝
    [InlineData("runningWithContext", false, "PreemptAfterClearingContext")] // 在跑带上下文、本机无批次
    [InlineData("runningWithContext", true, "RefuseContextHeld")]   // 在跑带上下文、批次在跑＝无损拒绝
    [InlineData("idleWithContext", false, "Idle", true)]            // 同「无批次」但**恢复重试窗口**在飞＝正常间隙态
    [InlineData("runningWithContext", false, "RefuseContextHeld", true)] // 恢复重试窗口在飞＝无损拒绝
    public async Task ResolveStartConflict_DecisionTable(string scenario, bool batchInFlight, string expected,
        bool resumeRetryInFlight = false)
    {
        (bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)? status = scenario switch
        {
            "unknown" => null,
            "idleNoContext" => (false, false, null, null),
            "idleWithContext" => (false, true, null, null),
            "runningNoContext" => (true, false, null, null),
            "runningWithContext" => (true, true, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var executor = new CommandExecutor(null!, "unused", isBatchInFlight: () => batchInFlight)
        {
            TaskStatusQueryOverride = _ => Task.FromResult(status),
        };
        if (resumeRetryInFlight) executor.ResumeRetryInFlightForTest = true;

        var decision = await executor.ResolveStartConflictAsync();

        Assert.Equal(expected, decision.ToString());
    }
}
