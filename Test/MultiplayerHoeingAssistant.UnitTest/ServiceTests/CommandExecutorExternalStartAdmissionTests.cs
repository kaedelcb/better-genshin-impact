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

    // ── §24.2-5 v2 取消等价与「发送成功非终态」（[Batch B 收尾之二]）──────────────

    /// <summary>
    /// 核心结果 → 完成层结果的映射纪律：**明确取消事实**＝终态 `Cancelled`（线路词表不改）；
    /// **非终态（普通发送成功／仅业务失败）＝`null`**（＝本层无完成事实，§24.3-4 第一分支 ⇒ 保持普通受理）。
    /// **[Batch B 收尾之三 验证会诊阻断处置]** 原先返回 `Unknown` 会让 §24.4-4 明文要求的
    /// 「v2 发送成功：入口 success」被改判成 `NeedReconcile`（对外 `result_unknown`）——已纠正。
    /// </summary>
    [Fact]
    public void ToCompletion_CancelFact_IsTerminalCancelled_ButNonTerminalIsNullNotUnknown()
    {
        var cancelled = CommandExecutor.ToCompletion(new CommandResult
        {
            Status = "failed", ErrorCode = "cancelled", RawTerminal = "cancelled",
            IsTerminal = true, ExecutionDisposition = ExecutionDisposition.Cancelled, JobId = "job-c",
        });
        Assert.NotNull(cancelled);
        Assert.Equal(ExternalStartCompletionKind.Cancelled, cancelled!.Kind);
        Assert.Equal("cancelled", cancelled.RawTerminal);
        Assert.Equal("job-c", cancelled.JobId);

        // v2「发送成功」＝非终态（§24.2-1／§24.2-5）。
        var success = CommandExecutor.ToCompletion(new CommandResult { Status = "success", Message = "已启动" });
        Assert.Null(success);   // 无完成事实 ⇒ 普通受理（不得写成 Unknown）

        // 明确失败但**未观察到终态** ⇒ 同样无完成事实（不得据信封错误码断言执行终态）。
        var failedNoTerminal = CommandExecutor.ToCompletion(new CommandResult { Status = "failed", ErrorCode = "task_busy" });
        Assert.Null(failedNoTerminal);

        // 权威失败终态（IsTerminal）⇒ `ExecutionFailed`（携带执行错误码）。
        var failedTerminal = CommandExecutor.ToCompletion(new CommandResult
        {
            Status = "failed", IsTerminal = true, ExecutionDisposition = ExecutionDisposition.ExecutionFailed,
            RawTerminal = "failed", ExecutionErrorCode = "E_TASK", ErrorCode = "envelope",
        });
        Assert.Equal(ExternalStartCompletionKind.ExecutionFailed, failedTerminal!.Kind);
        Assert.Equal("E_TASK", failedTerminal.ExecutionErrorCode);
    }

    /// <summary>
    /// **v2 明确取消的端到端（适配器侧）**：核心返回取消事实时，发送层必须表达「**已受理**」（否则宿主不会取完成观察），
    /// 完成层携带 `Cancelled`（`IsTerminal`）——两段合起来才构成 §24.2-5 的分阶段链。
    /// 同时校验收据：**信封 `ErrorCode=cancelled` 但无终态/取消事实时不得升格为取消**。
    /// </summary>
    [Fact]
    public void V2CancelFact_SendLayerAccepted_CompletionCancelled_EnvelopeErrorCodeAloneIsNotCancel()
    {
        var cancelCore = new CommandResult
        {
            Status = "failed", RawTerminal = "cancelled", IsTerminal = true,
            ExecutionDisposition = ExecutionDisposition.Cancelled, JobId = "job-v2",
        };
        var sendLayer = CommandExecutor.ToExecution(cancelCore);
        Assert.Equal(ExternalStartExecutionKind.Accepted, sendLayer.Kind);   // 曾受理（否则宿主不取完成观察）
        Assert.Equal("job-v2", sendLayer.JobId);
        var completion = CommandExecutor.ToCompletion(cancelCore);
        Assert.Equal(ExternalStartCompletionKind.Cancelled, completion!.Kind);
        Assert.Equal("job-v2", completion.JobId);

        // **真实最小形状**：`new CommandResult { Status = "cancelled" }`（无 IsTerminal/无 RawTerminal）——
        // 发送层同样必须表达「已受理」，否则真实 v2 取消永远进不了完成结算链。
        var realShape = new CommandResult { Status = "cancelled" };
        Assert.Equal(ExternalStartExecutionKind.Accepted, CommandExecutor.ToExecution(realShape).Kind);
        Assert.Equal(ExternalStartCompletionKind.Cancelled, CommandExecutor.ToCompletion(realShape)!.Kind);

        // 反例：仅信封错误码为 cancelled（无终态、无取消事实）⇒ 不得当取消（也不得据此释放占用）。
        var envelopeOnly = new CommandResult { Status = "failed", ErrorCode = "cancelled" };
        Assert.False(CommandExecutor.IsObservedCancel(envelopeOnly));
        Assert.Null(CommandExecutor.ToCompletion(envelopeOnly));   // 无终态/无取消事实 ⇒ 无完成事实
    }

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
        // [批次三十八] 入参形状完整性：准入回调必须**自带执行委托与完成事实提供者**（适配器不得交出空壳请求）
        Assert.NotNull(request.ExecuteAsync);
        Assert.NotNull(request.CompletionProvider);
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
        // [批次三十八] 入参形状完整性（与 start_group 同口径）：三类委托均不得为空壳
        Assert.NotNull(request.ExecuteAsync);
        Assert.NotNull(request.CompletionProvider);
        Assert.NotNull(request.CompletionObserver);
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
    /// **E4 热键接线（默认未启用）**：候选按 §2.2 映射（`hotkey:{配置名}`；本地=`manual:hotkey:{requestIdentity}`、
    /// 远程=`v2:hotkey:{requestIdentity}`）；被阻断时**未发键**。
    /// 控制热键（CancelTask/BgiEnabled/Suspend）**不走准入**（保持动作分类）。
    /// </summary>
    [Fact]
    public async Task Hotkey_RoutesThroughAdmission_BlockedDoesNotPress()
    {
        var calls = new List<ExternalStartAdmissionRequest>();
        CommandExecutor NewExecutor() => new(null!, "unused",
            externalStartAdmission: (request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(new ExternalStartAdmissionOutcome(
                    ExternalStartAdmissionStatus.Blocked, "f11_active", "F11 激活"));
            })
        {
            // 空闲（无任务在跑）＝决策 Idle，故直接进入准入（不触碰 IPC）
            TaskStatusQueryOverride = _ => Task.FromResult(
                ((bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)?)(false, false, null, null)),
        };

        // 本地来源（`local_` 前缀 CommandId）⇒ manual；远程来源 ⇒ v2（**临时判别**，见设计稿 §14）
        await NewExecutor().ExecuteAsync(new RemoteCommand
        {
            Cmd = "hotkey_execute", CommandId = "local_1",
            Params = new() { ["hotkeyConfigName"] = "测试热键" },
        });
        var localResult = await NewExecutor().ExecuteAsync(new RemoteCommand
        {
            Cmd = "hotkey_execute", CommandId = "remote-1",
            Params = new() { ["hotkeyConfigName"] = "测试热键" },
        });

        Assert.Equal(2, calls.Count);
        var request = calls[0];
        Assert.Equal("manual", request.Namespace);                            // 本地按钮来源
        Assert.Equal("manual:hotkey_execute", request.SourceDetail);
        Assert.Equal("hotkey:测试热键", request.WorkflowId);
        Assert.Equal("hotkey:测试热键", request.ResourceRef);
        Assert.Equal("manual:hotkey:{requestIdentity}", request.TriggerOccurrenceId); // 占位符由门面回填
        Assert.Equal("v2", calls[1].Namespace);                               // 远程命令来源
        Assert.Equal("v2:hotkey:{requestIdentity}", calls[1].TriggerOccurrenceId);
        // [批次三十八] 双来源**完整字段矩阵 + 三类委托非空**（此前 v2 侧仅断言两项）
        Assert.Equal("hotkey:测试热键", calls[1].WorkflowId);
        Assert.Equal("hotkey:测试热键", calls[1].ResourceRef);
        Assert.Equal("v2:hotkey_execute", calls[1].SourceDetail);
        Assert.NotNull(request.ExecuteAsync);
        Assert.NotNull(request.CompletionProvider);
        Assert.NotNull(request.CompletionObserver);
        Assert.NotNull(calls[1].ExecuteAsync);
        Assert.NotNull(calls[1].CompletionProvider);
        Assert.NotNull(calls[1].CompletionObserver);
        Assert.Equal("failed", localResult.Status);
        Assert.Equal("f11_active", localResult.ErrorCode);
    }

    /// <summary>
    /// **E4 无损拒绝不进入准入**：本机批次在跑 + BGI 有中断上下文 ⇒ 决策 `RefuseContextHeld`，
    /// 入口直接按既有文案拒绝（**不产候选、不签发许可、不发键**）。
    /// 注意：包装层顶部的批次守卫与决策读取**同一组标志**，故该分支只在「守卫通过后、判定前批次开始」的竞态窗可达——
    /// 本夹具用计数委托精确模拟该竞态（第一次读=false 放行，后续读=true 触发拒绝）。
    /// 控制热键（如 `SuspendHotkey`）保持直通，**不经准入**。
    /// </summary>
    [Fact]
    public async Task Hotkey_ContextHeldRefusalSkipsAdmission_AndControlHotkeyBypasses()
    {
        var admitted = 0;
        var busyReads = 0;
        var executor = new CommandExecutor(null!, "unused",
            isBatchInFlight: () => System.Threading.Interlocked.Increment(ref busyReads) > 1, // 竞态：守卫通过后批次开始
            externalStartAdmission: (_, _) =>
            {
                admitted++;
                return Task.FromResult(new ExternalStartAdmissionOutcome(
                    ExternalStartAdmissionStatus.Accepted, "accepted", "ok"));
            })
        {
            TaskStatusQueryOverride = _ => Task.FromResult(
                ((bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)?)(true, true, "group", "批次")),
        };

        var refused = await executor.ExecuteAsync(new RemoteCommand
        {
            Cmd = "hotkey_execute",
            Params = new() { ["hotkeyConfigName"] = "测试热键" },
        });

        Assert.Equal("failed", refused.Status);
        Assert.Contains("无损拒绝", refused.Message);
        Assert.Equal(0, admitted); // 未进入准入

        // 控制热键（CancelTask/BgiEnabled/Suspend）**本夹具不执行**：其直通分支会真实调用 ExecuteHotkeyAsync →
        // 真实 IPC（可能真的向运行中的 BGI 发键）。该分支「不经准入」以源码审查为据，**测试注入发送接缝**归启用前置
        // （见设计稿 §14）——禁止在单测中触达真实系统。
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

    // ── ext 队列通道「早期受理 ＋ 完成观察」拆分（R5.3 §24.10／§24.14，[Batch B 收尾之三／P38]）──

    /// <summary>
    /// **早期受理段分类（§24.14-1）**：一次 `ext.task.start` 回执只产出一个早期结论；副作用前拒绝＝
    /// **确定未受理（可重试）**；`already_executed`＝**等同 executed**（完成层给权威终态、不得凭空生成句柄）；
    /// 缺句柄＝协议违例（不可考）；queued/adopted＋句柄＝已受理（句柄随早期受理全链传递，§24.5-1）。
    /// </summary>
    [Fact]
    public void QueueEarly_ClassifySubmit_SingleConclusionPerReceipt()
    {
        var observedAt = DateTimeOffset.UtcNow;

        var rejected = CommandExecutor.ClassifyQueueSubmitEarly(
            new BgiTaskSubmitResult { Success = false, ErrorCode = "queue_full", ErrorMessage = "队列已满" },
            "配置组「A」", 3, observedAt);
        Assert.Equal(CommandExecutor.QueueStartEarlyKind.Rejected, rejected.Kind);
        var rejectedMapped = CommandExecutor.MapQueueEarlyToAdmission(rejected);
        Assert.Equal(ExternalStartExecutionKind.Rejected, rejectedMapped.Early.Kind);
        Assert.True(rejectedMapped.Early.Retryable);          // 可重试窗口（旧词表「请稍后重试」同义）
        Assert.Equal("ext:task.queue", rejectedMapped.Early.EvidenceSource);
        Assert.Null(rejectedMapped.Observer);                  // 未受理 ⇒ 无完成观察

        var already = CommandExecutor.ClassifyQueueSubmitEarly(
            new BgiTaskSubmitResult { Success = true, Status = "already_executed" }, "配置组「A」", 3, observedAt);
        Assert.Equal(CommandExecutor.QueueStartEarlyKind.AlreadyExecuted, already.Kind);
        var alreadyMapped = CommandExecutor.MapQueueEarlyToAdmission(already);
        Assert.Equal(ExternalStartExecutionKind.Accepted, alreadyMapped.Early.Kind);
        Assert.Null(alreadyMapped.Early.JobId);                // 无句柄 ⇒ 不得凭空生成
        var alreadyCompletion = alreadyMapped.Observer!(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(ExternalStartCompletionKind.Succeeded, alreadyCompletion!.Kind);  // R4 既有口径：等同 executed
        Assert.Equal("already_executed", alreadyCompletion.RawTerminal);
        Assert.Equal("ext:idempotency", alreadyCompletion.EvidenceSource);
        Assert.Equal(observedAt, alreadyCompletion.ObservedAtUtc);                     // 观察时点＝回执接收时点

        var missing = CommandExecutor.ClassifyQueueSubmitEarly(
            new BgiTaskSubmitResult { Success = true, Status = "queued" }, "配置组「A」", 3, observedAt);
        Assert.Equal(CommandExecutor.QueueStartEarlyKind.MissingHandle, missing.Kind);
        Assert.Equal(ExternalStartExecutionKind.Unknown,
            CommandExecutor.MapQueueEarlyToAdmission(missing).Early.Kind);             // 协议违例 ⇒ 不可考
        Assert.Null(CommandExecutor.MapQueueEarlyToAdmission(missing).Observer);

        var faulted = new CommandExecutor.QueueStartEarly(
            CommandExecutor.QueueStartEarlyKind.Unknown, Detail: "pipe broken");
        Assert.Equal(ExternalStartExecutionKind.Unknown, CommandExecutor.MapQueueEarlyToAdmission(faulted).Early.Kind);
        Assert.Null(CommandExecutor.MapQueueEarlyToAdmission(faulted).Observer);

        // 已受理：句柄与完成观察分离（§24.10-1 两层各有独立类型，不得互相代替）
        var accepted = CommandExecutor.ClassifyQueueSubmitEarly(
            new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = "h-1" },
            "配置组「A」", 3, observedAt);
        Assert.Equal(CommandExecutor.QueueStartEarlyKind.Accepted, accepted.Kind);
        Assert.Equal("h-1", accepted.TaskHandle);
        Assert.Null(accepted.Observation);                     // 观察任务由早期段入口创建（纯分类不建连接）
        var observation = Task.FromResult(new CommandExecutor.QueueTerminalObservation(
            CommandExecutor.QueueTerminalSource.Event, "completed", false, null, null, "h-1", false, null,
            observedAt));
        var acceptedWithReply = accepted with
        {
            Observation = observation,
            Reply = new ExternalStartReply.EarlyAccepted("h-1", CommandExecutor.MapObservationTaskAsync(observation)),
        };
        var acceptedMapped = CommandExecutor.MapQueueEarlyToAdmission(acceptedWithReply);
        Assert.Equal(ExternalStartExecutionKind.Accepted, acceptedMapped.Early.Kind);
        Assert.Equal("h-1", acceptedMapped.Early.JobId);       // §24.5-1：句柄不得在准入层丢弃
        Assert.NotNull(acceptedMapped.Observer);
        var completion = acceptedMapped.Observer!(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(ExternalStartCompletionKind.Succeeded, completion!.Kind);
        Assert.Equal("h-1", completion.JobId);
        Assert.Equal("ext:task.event", completion.EvidenceSource);
    }

    /// <summary>
    /// **完成观察 → 完成层结果（§24.2-2／§24.7-3）**：只有事件／轮询取得的**权威终态**才构造终态载体；
    /// `not_found`／超预算／观察中断一律 `Unknown`（不写 `PendingTerminal`、禁止借超时或未命中转终态）。
    /// </summary>
    [Fact]
    public void QueueObservation_TerminalFactsMapToCompletion_NonTerminalStaysUnknown()
    {
        // 观察结论必须携带**接收时点**（§24.2-2″）：映射阶段不得重取，本夹具用固定时点验证等值传递。
        var receivedAt = DateTimeOffset.UtcNow.AddSeconds(-7);
        static ExternalStartCompletion Map(
            CommandExecutor.QueueTerminalSource source, string? raw, bool cancelled = false,
            string? code = null, string? detail = null, DateTimeOffset? observedAt = null)
            => CommandExecutor.MapQueueObservationToCompletion(new CommandExecutor.QueueTerminalObservation(
                source, raw, cancelled, code, null, "h-9", false, detail, observedAt));

        var succeeded = Map(CommandExecutor.QueueTerminalSource.Event, "completed", observedAt: receivedAt);
        Assert.Equal(ExternalStartCompletionKind.Succeeded, succeeded.Kind);
        Assert.Equal("h-9", succeeded.JobId);
        Assert.Equal(receivedAt, succeeded.ObservedAtUtc);        // 接收时点原样传递（不重取）

        // ext 的取消＝`completed` ＋ `Cancelled=true`：**原词不得被改写**（§24.2-2″），取消事实由 Kind 承载。
        var cancelledEvent = Map(CommandExecutor.QueueTerminalSource.Event, "completed", cancelled: true,
            observedAt: receivedAt);
        Assert.Equal(ExternalStartCompletionKind.Cancelled, cancelledEvent.Kind);
        Assert.Equal("completed", cancelledEvent.RawTerminal);
        Assert.Equal(receivedAt, cancelledEvent.ObservedAtUtc);
        Assert.Equal(ExternalStartCompletionKind.Cancelled,
            Map(CommandExecutor.QueueTerminalSource.Poll, "completed", cancelled: true, observedAt: receivedAt).Kind);
        Assert.Equal(ExternalStartCompletionKind.Cancelled,
            Map(CommandExecutor.QueueTerminalSource.Poll, "queueCancelled", cancelled: true, observedAt: receivedAt).Kind);

        var failed = Map(CommandExecutor.QueueTerminalSource.Poll, "failed", code: "task_failed", observedAt: receivedAt);
        Assert.Equal(ExternalStartCompletionKind.ExecutionFailed, failed.Kind);
        Assert.Equal("task_failed", failed.ExecutionErrorCode);   // 完成层执行错误码（不得复用信封 ErrorCode）
        Assert.Equal("ext:task.queueStatus", failed.EvidenceSource);

        // §24.2-2″（验证会诊阻断处置）：**权威终态缺观察时点 ⇒ fail-closed**（不得以映射时刻冒充接收时点）。
        var missingObservedAt = Map(CommandExecutor.QueueTerminalSource.Event, "completed");
        Assert.Equal(ExternalStartCompletionKind.Unknown, missingObservedAt.Kind);
        Assert.Null(missingObservedAt.ObservedAtUtc);

        var notFound = Map(CommandExecutor.QueueTerminalSource.Poll, "not_found");
        Assert.Equal(ExternalStartCompletionKind.Unknown, notFound.Kind);
        Assert.Null(notFound.ObservedAtUtc);                      // 非权威终态 ⇒ 无观察时点（不得生成 PendingTerminal）
        Assert.DoesNotContain("completed", notFound.RawTerminal!); // 不得把「未命中」写成终态词

        var timeout = Map(CommandExecutor.QueueTerminalSource.Timeout, null);
        Assert.Equal(ExternalStartCompletionKind.Unknown, timeout.Kind);
        Assert.Null(timeout.ObservedAtUtc);                       // 无权威终态 ⇒ 无观察时点

        var faulted = Map(CommandExecutor.QueueTerminalSource.Faulted, null, detail: "pipe broken");
        Assert.Equal(ExternalStartCompletionKind.Unknown, faulted.Kind);
        Assert.Contains("pipe broken", faulted.RawTerminal);
    }

    /// <summary>
    /// **可重试白名单（§24.2-2″／§24.11 第 3′ 行）**：只有**无损拒绝类**才开重试窗口；
    /// 未列入的错误码一律**终局拒绝**（保守方向，防止「可能已产生副作用」的失败进入可重试分类）。
    /// </summary>
    [Fact]
    public void QueueEarly_RejectionRetryability_IsClosedWhitelist()
    {
        static ExternalStartExecution MapRejected(string? code)
            => CommandExecutor.MapQueueEarlyToAdmission(CommandExecutor.ClassifyQueueSubmitEarly(
                new BgiTaskSubmitResult { Success = false, ErrorCode = code, ErrorMessage = "拒绝" },
                "配置组「A」", 1, DateTimeOffset.UtcNow)).Early;

        Assert.True(CommandExecutor.IsRetryableQueueRejection("queue_full"));
        Assert.True(CommandExecutor.IsRetryableQueueRejection("task_already_running"));
        Assert.False(CommandExecutor.IsRetryableQueueRejection("contract_violation"));  // 未列入 ⇒ 终局拒绝
        Assert.False(CommandExecutor.IsRetryableQueueRejection(null));

        Assert.True(MapRejected("queue_full").Retryable);
        Assert.Equal(ExternalStartExecutionKind.Rejected, MapRejected("queue_full").Kind);
        var terminalReject = MapRejected("unknown_side_effect_code");
        Assert.Equal(ExternalStartExecutionKind.Rejected, terminalReject.Kind);
        Assert.False(terminalReject.Retryable);
        Assert.Equal("unknown_side_effect_code", terminalReject.Reason);
    }

    /// <summary>
    /// **未接线直启路径的旧词表投影逐字保留**（本批只改内部结构：对外文案与错误码不得回归）。
    /// </summary>
    [Fact]
    public async Task QueueLegacyProjection_PreservesExistingVocabulary()
    {
        static CommandExecutor.QueueStartEarly Observed(
            CommandExecutor.QueueTerminalSource source, string? raw, bool cancelled = false,
            string? code = null, string? message = null, string? detail = null)
            => new(CommandExecutor.QueueStartEarlyKind.Accepted, TaskHandle: "h-7",
                Observation: Task.FromResult(new CommandExecutor.QueueTerminalObservation(
                    source, raw, cancelled, code, message, "h-7", false, detail, DateTimeOffset.UtcNow)));

        var eventCompleted = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Event, "completed"), "配置组「A」", 1);
        Assert.Equal("success", eventCompleted.Status);
        Assert.Contains("已启动并执行完成（队列通道）", eventCompleted.Message);

        var pollCompleted = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Poll, "completed"), "配置组「A」", 1);
        Assert.Contains("已启动并执行完成（队列通道，轮询校准）", pollCompleted.Message);

        Assert.Equal("cancelled", (await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Event, "completed", cancelled: true), "配置组「A」", 1)).Status);
        Assert.Equal("cancelled", (await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Poll, "queueCancelled", cancelled: true), "配置组「A」", 1)).Status);
        Assert.Contains("排队中被取消", (await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Poll, "queueCancelled", cancelled: true), "配置组「A」", 1)).Message);

        var pollFailed = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Poll, "failed", code: "task_failed", message: "任务失败"),
            "配置组「A」", 1);
        Assert.Contains("执行失败（task_failed）：任务失败", pollFailed.Message);

        Assert.Contains("任务句柄在 BGI 侧不存在", (await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Poll, "not_found"), "配置组「A」", 1)).Message);
        Assert.Contains("等待执行结果超时", (await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Timeout, null), "配置组「A」", 1)).Message);

        var faulted = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            Observed(CommandExecutor.QueueTerminalSource.Faulted, null, detail: "pipe broken"), "配置组「A」", 1);
        Assert.Equal("result_unknown", faulted.ErrorCode);
        Assert.Contains("执行结果未知，未重新下发：pipe broken", faulted.Message);

        var rejected = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            new CommandExecutor.QueueStartEarly(CommandExecutor.QueueStartEarlyKind.Rejected,
                ReasonCode: "queue_full", Detail: "队列已满"), "配置组「A」", 4);
        Assert.Equal("failed", rejected.Status);
        Assert.Contains("BGI 任务队列拒绝启动配置组「A」（queue_full）：队列已满", rejected.Message);

        var already = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            new CommandExecutor.QueueStartEarly(CommandExecutor.QueueStartEarlyKind.AlreadyExecuted),
            "配置组「A」", 4);
        Assert.Equal("success", already.Status);
        Assert.Contains("已执行过（generation=4，幂等跳过）", already.Message);

        var missing = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            new CommandExecutor.QueueStartEarly(CommandExecutor.QueueStartEarlyKind.MissingHandle, Detail: "queued"),
            "配置组「A」", 4);
        Assert.Equal("result_unknown", missing.ErrorCode);
        Assert.Contains("已提交但未获得有效句柄，禁止换通道重发", missing.Message);

        var unknown = await CommandExecutor.MapQueueEarlyToLegacyResultAsync(
            new CommandExecutor.QueueStartEarly(CommandExecutor.QueueStartEarlyKind.Unknown, Detail: "pipe broken"),
            "配置组「A」", 4);
        Assert.Equal("result_unknown", unknown.ErrorCode);
        Assert.Contains("执行结果未知，未重新下发：pipe broken", unknown.Message);
    }

    /// <summary>
    /// **无早期 ack 通道时观察委托必须返回 `null`（＝本通道不承载完成事实）**：
    /// 不得伪造 `Unknown` —— 否则「普通受理」会被结算成待对账（对外 `result_unknown`），
    /// 既有 v2 发送成功语义（§24.6-5「普通受理已关闭、完成层尚未报终态」＝责任 `Pending`）随之回归。
    /// </summary>
    [Fact]
    public async Task StartGroup_AdmissionAcceptedWithoutEarlyAck_ObserverYieldsNullNotUnknown()
    {
        ExternalStartAdmissionRequest? captured = null;
        var executor = new CommandExecutor(null!, "unused",
            externalStartAdmission: (request, _) =>
            {
                captured = request;
                // 模拟门面「已受理」（发送段未被执行 ⇒ `coreResult` 保持 null ⇒ 回执走 MapAdmissionOutcome）
                return Task.FromResult(new ExternalStartAdmissionOutcome(
                    ExternalStartAdmissionStatus.Accepted, "accepted", "已受理",
                    JobId: "job-x", ExecutionDisposition: ExecutionDisposition.None,
                    ResponsibilityState: ResponsibilityState.Pending,
                    SubmissionIdentity: "sub-1", SendSeq: 1));
            });

        var result = await executor.ExecuteAsync(StartGroupCommand());

        Assert.Equal("success", result.Status);                    // 受理≠终态：既有词表仍为 success
        Assert.False(result.IsTerminal);
        Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
        Assert.NotNull(captured!.CompletionObserver);
        Assert.Null(await captured.CompletionObserver!(CancellationToken.None));  // 不承载完成事实
        Assert.Null(captured.CompletionProvider?.Invoke());
    }
}
