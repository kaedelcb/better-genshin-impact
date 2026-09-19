using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// R4.9 Batch B 启动移交 Runner/节点侧夹具——设计稿 §5/§6/§7/§8 矩阵 Runner 侧：
/// ConfigMissing 链继续（旧配置兼容+FailureNote 单处回报）、未知 mode 响亮拒绝终止链、
/// Accepted/AlreadyAccepted 终止含嵌套分支回溯（矩阵 8）、Rejected 终止 StopBgi 不执行（矩阵 9）、
/// MixedUsage 三态（成功/结果不确定/带任务参数 StartBgi；裸 start 不算）、armTrigger 绕过混用、
/// IntentKey 双重身份格式（手动=manual:{ExecutionId}；触发器=种类:实例:日程日）、序列化往返新字段不丢（矩阵 13）。
/// 假 BGI 执行器 + 假移交委托，无真实 IPC/宿主。
/// </summary>
public class StartupFlowRunnerHandoffTests
{
    private readonly List<string> _logs = new();
    private readonly List<(string StepId, NodeRunState State, string? Note)> _reports = new();
    private readonly List<string> _bgiCommands = new();
    private readonly List<StartupHandoffRequest> _handoffRequests = new();

    private StartupHandoffResult _handoffResult = StartupHandoffResult.Accepted("run-1", null);

    private StartupFlowRunner MakeRunner(Func<string, Dictionary<string, object>?, Task<CommandResult>>? bgi = null)
    {
        var runner = new StartupFlowRunner(
            bgi ?? ((cmd, _) =>
            {
                _bgiCommands.Add(cmd);
                return Task.FromResult(new CommandResult { Status = "success", Message = "ok" });
            }),
            (req, _) =>
            {
                _handoffRequests.Add(req);
                return Task.FromResult(_handoffResult);
            },
            _ => { },
            (_, _) => Task.FromResult((true, "测试确认")),
            _logs.Add,
            () => null,
            _ => { },
            armLogTrigger: null);
        runner.NodeStateSink = (step, state, note) => _reports.Add((step.Id, state, note));
        return runner;
    }

    private static StartupStep Handoff(string flowId = "wf-1", string mode = StartupHandoffModes.Start)
        => new() { Kind = StartupStepKinds.EnterTaskCenter, TaskCenterFlowId = flowId, TaskCenterHandoffMode = mode };

    private static StartupStep Wait0() => new() { Kind = StartupStepKinds.Wait, WaitSeconds = 0 };

    private (NodeRunState State, string? Note)? LastReport(StartupStep step)
        => _reports.Where(r => r.StepId == step.Id).Select(r => ((NodeRunState, string?)?)(r.State, r.Note)).LastOrDefault();

    private bool EverRan(StartupStep step) => _reports.Any(r => r.StepId == step.Id && r.State == NodeRunState.Running);

    [Fact]
    public async Task ConfigMissing_ChainContinues_NodeFailedWithReason()
    {
        var handoff = Handoff(flowId: "");
        var wait = Wait0();
        var runner = MakeRunner();

        await runner.RunAsync([handoff, wait], CancellationToken.None);

        Assert.Empty(_handoffRequests); // 空目标在 Runner 侧短路，不进委托
        var rep = LastReport(handoff);
        Assert.NotNull(rep);
        Assert.Equal(NodeRunState.Failed, rep!.Value.State);
        Assert.Contains(HandoffReasonCodes.ConfigMissing, rep.Value.Note);
        Assert.True(EverRan(wait)); // 链继续（旧配置兼容语义）
        Assert.Equal(NodeRunState.Success, LastReport(wait)?.State);
    }

    [Fact]
    public async Task UnknownMode_Rejected_TerminatesChain()
    {
        var handoff = Handoff(mode: "bogus-mode");
        var wait = Wait0();
        var runner = MakeRunner();

        await runner.RunAsync([handoff, wait], CancellationToken.None);

        Assert.Empty(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Equal(NodeRunState.Failed, rep!.Value.State);
        Assert.Contains(HandoffReasonCodes.UnsupportedMode, rep.Value.Note);
        Assert.False(EverRan(wait)); // 未知 mode 终止链，不静默回落
    }

    [Fact]
    public async Task Accepted_TerminatesChain_WithNestedBranchBacktrack()
    {
        // 矩阵 8：多层条件分支内移交成功 → 子链剩余 + 全部外层后继不执行
        var handoff = Handoff();
        var innerWait = Wait0();
        var outerWait = Wait0();
        // 条件用人工确认（测试注入恒 true 确认器），不依赖运行时钟（三轮 S5）
        var innerCondition = new StartupStep
        {
            Kind = StartupStepKinds.ManualConfirm, NodeType = "condition",
            TrueSteps = [handoff, innerWait],
        };
        var outerCondition = new StartupStep
        {
            Kind = StartupStepKinds.ManualConfirm, NodeType = "condition",
            TrueSteps = [innerCondition, Wait0()], // 中间层后继（矩阵 8 补全：中间层剩余同样不执行）
        };
        _handoffResult = StartupHandoffResult.Accepted("run-9", null);
        var runner = MakeRunner();

        await runner.RunAsync([outerCondition, outerWait], CancellationToken.None);

        Assert.Single(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Equal(NodeRunState.Success, rep!.Value.State);
        Assert.Contains("run-9", rep.Value.Note);
        Assert.Contains("执行", rep.Value.Note); // 三轮 重要7：节点回报附执行身份
        Assert.False(EverRan(innerWait)); // 最内层子链剩余不执行
        Assert.False(EverRan(outerCondition.TrueSteps[1])); // 中间层后继不执行
        Assert.False(EverRan(outerWait)); // 主链外层后继不回溯执行
        Assert.Contains(_logs, l => l.Contains("终止"));
    }

    [Fact]
    public async Task AlreadyAccepted_TerminatesChain_WithSuccessReport()
    {
        var handoff = Handoff();
        var wait = Wait0();
        _handoffResult = StartupHandoffResult.AlreadyAccepted("run-old", "此前已受理同一计划出现");
        var runner = MakeRunner();

        await runner.RunAsync([handoff, wait], CancellationToken.None);

        Assert.Single(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Equal(NodeRunState.Success, rep!.Value.State);
        Assert.Contains("run-old", rep.Value.Note);
        Assert.False(EverRan(wait));
    }

    [Fact]
    public async Task Rejected_TerminatesChain_StopBgiNotExecuted()
    {
        // 矩阵 9：移交被拒（终止类原因码）→ 后续 StopBgi 不执行，不破坏宿主管理的在册运行
        var handoff = Handoff();
        var stop = new StartupStep { Kind = StartupStepKinds.StopBgi };
        _handoffResult = StartupHandoffResult.Rejected(HandoffReasonCodes.NotReady, "BGI 离线");
        var runner = MakeRunner();

        await runner.RunAsync([handoff, stop], CancellationToken.None);

        Assert.Single(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Equal(NodeRunState.Failed, rep!.Value.State);
        Assert.Contains(HandoffReasonCodes.NotReady, rep.Value.Note);
        Assert.False(EverRan(stop));
        Assert.DoesNotContain("kill_bgi", _bgiCommands);
    }

    [Fact]
    public async Task HostConfigMissing_ChainContinues_SingleReport_NoNoteLeak()
    {
        // 委托回 ConfigMissing（TerminateChain=false）→ 链继续；三轮 重要3：Failed 单处回报（恰 1 次）；
        // FailureNote 单次消费——紧随的普通失败节点不继承移交原因
        var handoff = Handoff();
        var badCmd = new StartupStep { Kind = StartupStepKinds.RunCmd, Arguments = "" }; // 空命令=普通失败（无 note）
        _handoffResult = StartupHandoffResult.Rejected(HandoffReasonCodes.ConfigMissing, "未配置目标流程");
        var runner = MakeRunner();

        await runner.RunAsync([handoff, badCmd], CancellationToken.None);

        Assert.Single(_handoffRequests);
        var faileds = _reports.Where(r => r.StepId == handoff.Id && r.State == NodeRunState.Failed).ToList();
        Assert.Single(faileds); // 单处回报
        Assert.Contains(HandoffReasonCodes.ConfigMissing, faileds[0].Note);
        Assert.Contains("（执行", faileds[0].Note); // 四轮 建议1：链继续路径回报附执行短码
        Assert.True(EverRan(badCmd)); // 链继续
        var badRep = LastReport(badCmd);
        Assert.Equal(NodeRunState.Failed, badRep!.Value.State);
        Assert.Null(badRep.Value.Note); // 不继承移交节点的原因
    }

    [Fact]
    public async Task EmptyFlowAndUnknownMode_UnsupportedModeWins_TerminatesChain()
    {
        // 三轮 重要5：两项同时无效时显式未知 mode 优先（与宿主校验顺序一致），不允许空目标短路把它放成链继续
        var handoff = Handoff(flowId: "", mode: "bogus-mode");
        var wait = Wait0();
        var runner = MakeRunner();

        await runner.RunAsync([handoff, wait], CancellationToken.None);

        Assert.Empty(_handoffRequests);
        Assert.Contains(HandoffReasonCodes.UnsupportedMode, LastReport(handoff)!.Value.Note);
        Assert.False(EverRan(wait));
    }

    [Fact]
    public async Task MixedUsage_SuccessCommit_RejectsBeforeDelegate()
    {
        // 矩阵 11：本链 start_group 成功提交 + start 移交 → MixedUsage，不进委托
        var group = new StartupStep { Kind = StartupStepKinds.StartGroup, TaskName = "组A" };
        var handoff = Handoff();
        var runner = MakeRunner();

        await runner.RunAsync([group, handoff], CancellationToken.None);

        Assert.Contains("start_group", _bgiCommands);
        Assert.Empty(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Equal(NodeRunState.Failed, rep!.Value.State);
        Assert.Contains(HandoffReasonCodes.MixedUsage, rep.Value.Note);
    }

    [Fact]
    public async Task MixedUsage_UncertainCommit_CountsAsFact()
    {
        // 结果不确定（IPC 异常）也计提交事实——保守口径
        var group = new StartupStep { Kind = StartupStepKinds.StartGroup, TaskName = "组A" };
        var handoff = Handoff();
        var runner = MakeRunner((cmd, _) => throw new InvalidOperationException("IPC 超时"));

        await runner.RunAsync([group, handoff], CancellationToken.None);

        Assert.Empty(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Equal(NodeRunState.Failed, rep!.Value.State);
        Assert.Contains(HandoffReasonCodes.MixedUsage, rep.Value.Note);
        Assert.Contains("结果不确定", rep.Value.Note);
    }

    [Fact]
    public async Task MixedUsage_StartBgiWithTaskArgs_Counts_BareStart_DoesNot()
    {
        // 带任务参数 StartBgi（startOneDragon）→ MixedUsage
        var startBgi = new StartupStep { Kind = StartupStepKinds.StartBgi, Arguments = "startOneDragon 配置A" };
        var handoff = Handoff();
        var runner = MakeRunner();

        await runner.RunAsync([startBgi, handoff], CancellationToken.None);

        Assert.Empty(_handoffRequests);
        Assert.Contains(HandoffReasonCodes.MixedUsage, LastReport(handoff)!.Value.Note);

        // 裸 start（仅启截图器）不算提交事实 → 移交正常进委托
        _reports.Clear();
        var bareStart = new StartupStep { Kind = StartupStepKinds.StartBgi, Arguments = "start" };
        var handoff2 = Handoff();
        var runner2 = MakeRunner();

        await runner2.RunAsync([bareStart, handoff2], CancellationToken.None);

        Assert.Single(_handoffRequests);
        Assert.Equal(NodeRunState.Success, LastReport(handoff2)!.Value.State);
    }

    [Fact]
    public async Task MixedUsage_ReturnedFailureWithoutErrorCode_CountsAsUncertain()
    {
        // 三轮 重要4：无业务信封的失败返回=结果不确定（at-least-once，超时≠未执行）→ 计入提交事实
        var group = new StartupStep { Kind = StartupStepKinds.StartOneClick, TaskName = "龙A" };
        var handoff = Handoff();
        var runner = MakeRunner((cmd, _) =>
            Task.FromResult(new CommandResult { Status = "failed", Message = "IPC 仍不可达" }));

        await runner.RunAsync([group, handoff], CancellationToken.None);

        Assert.Empty(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Contains(HandoffReasonCodes.MixedUsage, rep!.Value.Note);
        Assert.Contains("结果不确定", rep.Value.Note);
    }

    [Theory]
    [InlineData("")]   // 空串
    [InlineData(" ")]  // 纯空格
    [InlineData("	")] // 制表符
    public async Task MixedUsage_BlankErrorCode_CountsAsUncertain(string errorCode)
    {
        // 六轮 建议6 + 七轮 建议3：空白（空串/纯空格/制表符）ErrorCode 不构成权威业务信封——与 null 同口径按结果不确定计入提交事实
        var group = new StartupStep { Kind = StartupStepKinds.StartOneClick, TaskName = "龙A" };
        var handoff = Handoff();
        var runner = MakeRunner((cmd, _) =>
            Task.FromResult(new CommandResult { Status = "failed", ErrorCode = errorCode, Message = "传输失败" }));

        await runner.RunAsync([group, handoff], CancellationToken.None);

        Assert.Empty(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Contains(HandoffReasonCodes.MixedUsage, rep!.Value.Note);
        Assert.Contains("结果不确定", rep.Value.Note);
    }

    [Fact]
    public async Task BusinessRejection_WithErrorCode_NotCountedAsCommit()
    {
        // 三轮 重要4 + R4.10 终审复核 重要4：failed + 白名单码（task_busy=副作用前准入拒绝，确定未提交）→ 不计提交事实，移交正常进委托
        var group = new StartupStep { Kind = StartupStepKinds.StartGroup, TaskName = "组A" };
        var handoff = Handoff();
        var runner = MakeRunner((cmd, _) =>
            Task.FromResult(new CommandResult { Status = "failed", ErrorCode = "task_busy", Message = "任务忙" }));

        await runner.RunAsync([group, handoff], CancellationToken.None);

        Assert.Single(_handoffRequests);
        Assert.Equal(NodeRunState.Success, LastReport(handoff)!.Value.State);
    }

    [Theory]
    [InlineData("result_unknown")]    // 执行后结果不可考（CommandExecutor 真实可达：656/737/804/1486 行）
    [InlineData("quiesce_timeout")]   // 静默等待超时（命令可能已送达）
    [InlineData("task_start_failed")] // 执行后失败不可重放（审计 T23 证明体系内存在）
    [InlineData("some_future_code")]  // 未知码
    public async Task MixedUsage_UnprovenErrorCode_CountsAsUncertain(string errorCode)
    {
        // R4.10 终审复核（重要4）：非白名单错误码 ≠ 确定未提交——保守计入提交事实，后续移交被混用守卫阻断
        var group = new StartupStep { Kind = StartupStepKinds.StartGroup, TaskName = "组A" };
        var handoff = Handoff();
        var runner = MakeRunner((cmd, _) =>
            Task.FromResult(new CommandResult { Status = "failed", ErrorCode = errorCode, Message = "结果不可考" }));

        await runner.RunAsync([group, handoff], CancellationToken.None);

        Assert.Empty(_handoffRequests);
        var rep = LastReport(handoff);
        Assert.Contains(HandoffReasonCodes.MixedUsage, rep!.Value.Note);
        Assert.Contains("结果不确定", rep.Value.Note);
    }

    [Fact]
    public void IsProvenNotCommitted_WhitelistOnly()
    {
        // R4.10 终审复核（重要4）：仅副作用前准入/合同拒绝（白名单）不计入；其余一律保守计入
        foreach (var code in new[] { "request_expired", "capability_required", "legacy_start_index_not_supported", "batch_busy", "task_busy", "task_already_running" })
            Assert.True(StartupFlowRunner.IsProvenNotCommitted(code), code);
        foreach (var code in new[] { "result_unknown", "quiesce_timeout", "task_start_failed", "epoch_unknown", "", " " })
            Assert.False(StartupFlowRunner.IsProvenNotCommitted(code), code);
        Assert.False(StartupFlowRunner.IsProvenNotCommitted(null), "<null>");
    }

    [Fact]
    public async Task ArmTrigger_BypassesMixedUsage()
    {
        // armTrigger 挂载等待不占槽位，不受混用限制
        var group = new StartupStep { Kind = StartupStepKinds.StartGroup, TaskName = "组A" };
        var handoff = Handoff(mode: StartupHandoffModes.ArmTrigger);
        var runner = MakeRunner();

        await runner.RunAsync([group, handoff], CancellationToken.None);

        Assert.Single(_handoffRequests);
        Assert.Equal(StartupHandoffModes.ArmTrigger, _handoffRequests[0].Mode);
        Assert.Equal(NodeRunState.Success, LastReport(handoff)!.Value.State);
    }

    [Fact]
    public async Task IntentKey_Manual_PerExecutionNewIntent()
    {
        var handoff = Handoff();
        var runner = MakeRunner();

        await runner.RunAsync([handoff], CancellationToken.None);
        await runner.RunAsync([handoff], CancellationToken.None);

        Assert.Equal(2, _handoffRequests.Count);
        var (r1, r2) = (_handoffRequests[0], _handoffRequests[1]);
        Assert.StartsWith("manual:", r1.IntentKey);
        Assert.StartsWith("manual:", r2.IntentKey);
        Assert.NotEqual(r1.IntentKey, r2.IntentKey); // 手动每次执行=新意图，不拦有意重复
        Assert.Equal(r1.IntentKey, "manual:" + r1.ExecutionId);
        Assert.Null(r1.TriggerKind);
        Assert.Equal(handoff.Id, r1.StepId);
        Assert.Equal(32, r1.ExecutionId.Length);
        Assert.NotEqual(r1.ExecutionId, r2.ExecutionId);
    }

    [Fact]
    public async Task IntentKey_Trigger_SharedOccurrenceIdentity()
    {
        var handoff = Handoff();
        var runner = MakeRunner();
        var trigger = new StartupTriggerInfo(StartupStepKinds.TimerTrigger, "trig-1", "2026-09-19");

        // 同一触发器同一日程出现，两个执行（不同 ExecutionId）共享 IntentKey
        await runner.RunAsync([handoff], CancellationToken.None, trigger);
        await runner.RunAsync([handoff], CancellationToken.None, trigger);

        Assert.Equal(2, _handoffRequests.Count);
        Assert.Equal("timerTrigger:trig-1:2026-09-19", _handoffRequests[0].IntentKey);
        Assert.Equal(_handoffRequests[0].IntentKey, _handoffRequests[1].IntentKey);
        Assert.NotEqual(_handoffRequests[0].ExecutionId, _handoffRequests[1].ExecutionId);
        Assert.Equal("timerTrigger", _handoffRequests[0].TriggerKind);
        Assert.Equal("trig-1", _handoffRequests[0].TriggerInstanceId);
        Assert.Equal("2026-09-19", _handoffRequests[0].FireDate);
    }

    [Fact]
    public void SerializationRoundTrip_PreservesHandoffFields()
    {
        // 矩阵 13：方案保存/导入导出往返，新字段不丢
        var step = new StartupStep
        {
            Kind = StartupStepKinds.EnterTaskCenter,
            TaskCenterFlowId = "wf-x",
            TaskCenterHandoffMode = StartupHandoffModes.Resume,
        };
        var json = JsonSerializer.Serialize(step);
        Assert.Contains("\"taskCenterFlowId\"", json);
        Assert.Contains("\"taskCenterHandoffMode\"", json);
        var back = JsonSerializer.Deserialize<StartupStep>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("wf-x", back.TaskCenterFlowId);
        Assert.Equal(StartupHandoffModes.Resume, back.TaskCenterHandoffMode);

        // 旧配置（无新字段）→ 默认值零破坏
        var legacy = JsonSerializer.Deserialize<StartupStep>("{\"kind\":\"enterTaskCenter\"}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("", legacy.TaskCenterFlowId);
        Assert.Equal(StartupHandoffModes.Start, legacy.TaskCenterHandoffMode);
    }

    [Fact]
    public void HasBgiTaskArgs_Classification()
    {
        // 与 BGI 命令行解析对齐：只看第一个参数 token（三轮 S1）
        Assert.True(StartupFlowRunner.HasBgiTaskArgs("startOneDragon 配置A"));
        Assert.True(StartupFlowRunner.HasBgiTaskArgs("--startGroups 组A 组B"));
        Assert.True(StartupFlowRunner.HasBgiTaskArgs("--TaskProgress"));
        Assert.True(StartupFlowRunner.HasBgiTaskArgs("  --startGroups 组A")); // 前导空白不影响首 token
        Assert.False(StartupFlowRunner.HasBgiTaskArgs("start"));
        Assert.False(StartupFlowRunner.HasBgiTaskArgs("foo --startGroups")); // 非首 token 不算
        Assert.False(StartupFlowRunner.HasBgiTaskArgs("--TaskProgressX")); // 精确匹配，前缀不算
        Assert.False(StartupFlowRunner.HasBgiTaskArgs(""));
        Assert.False(StartupFlowRunner.HasBgiTaskArgs(null));
    }

    [Fact]
    public async Task LogTriggerHit_MergedHits_ConsumeLastEvent_NoPhantomIdentity()
    {
        // 五轮 重1：命中与通知同一通道传输——信号/记录不脱节；合并取最后一次事件；
        // 消费后无残留信号 → 不会凭空产生无真实事件对应的兜底身份（跨日漂移窗口结构性消除）
        var trig = new ArmedLogTriggerViewModel(
            new StartupStep { Kind = StartupStepKinds.LogTrigger, LogKeyword = "锄地" }, null!);
        var t1 = new DateTime(2026, 9, 19, 23, 59, 57);
        var t2 = new DateTime(2026, 9, 19, 23, 59, 58);
        var t3 = new DateTime(2026, 9, 19, 23, 59, 59);

        trig.OnLogEntry(new LogEntry(t1, "INF", null, "test", "开始锄地A", null, 0, "f"));
        trig.OnLogEntry(new LogEntry(t2, "INF", null, "test", "开始锄地B", null, 0, "f"));
        trig.OnLogEntry(new LogEntry(t3, "INF", null, "test", "开始锄地C", null, 0, "f"));

        var hit = await trig.Hits.ReadAsync(CancellationToken.None);
        Assert.Contains("C", hit.Line); // 合并取最后一次事件
        Assert.Equal(t3, hit.OccurredAt); // 事件时刻同源（非消费时刻）
        Assert.False(trig.Hits.TryRead(out _)); // 无残留 → 不存在「有信号无记录」的兜底消费
    }

    [Fact]
    public async Task LogTriggerHit_PendingRead_Cancellable()
    {
        // 六轮 建议5：无命中时等待中的读取可被撤下取消——ReadAsync 响应令牌，不悬挂、取消不产生幻影记录
        var trig = new ArmedLogTriggerViewModel(
            new StartupStep { Kind = StartupStepKinds.LogTrigger, LogKeyword = "锄地" }, null!);

        var pending = trig.Hits.ReadAsync(trig.Cts.Token).AsTask();
        trig.Cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(trig.Hits.TryRead(out _));
    }

    // ===== VM 静态助手（三轮处置回归） =====

    [Fact]
    public void OccurrenceDate_InvariantFormat()
    {
        // 三轮 重要6：固定文化 yyyy-MM-dd，不受系统日历区域影响
        Assert.Equal("2026-09-19", MistletoeViewModel.OccurrenceDate(new DateTime(2026, 9, 19, 23, 59, 59)));
        Assert.Equal("2027-01-02", MistletoeViewModel.OccurrenceDate(new DateTime(2027, 1, 2, 0, 0, 1)));
    }

    [Fact]
    public void BuildFlowChoiceList_ReadyOnly_MissingTargetPreservedAsUnavailable()
    {
        // 三轮 阻断2/S3：仅 Ready 条目入选；缺失/隔离目标保留原 ID 追加「不可用」项；空目标不追加
        var entries = new List<(string, string, bool)>
        {
            ("wf-a", "日常流程", true),
            ("wf-q", "隔离流程", false),
        };
        var list = MistletoeViewModel.BuildFlowChoiceList(entries, "wf-gone");
        Assert.Equal(2, list.Count);
        Assert.Equal("wf-a", list[0].WorkflowId);
        Assert.Equal("wf-gone", list[1].WorkflowId);
        Assert.Contains("不可用", list[1].Display);
        Assert.Contains("wf-gone", list[1].Display);

        // 当前目标在 Ready 列表中 → 不追加保留项
        var hit = MistletoeViewModel.BuildFlowChoiceList(entries, "wf-a");
        Assert.Single(hit);
        // 空目标 → 不追加
        var empty = MistletoeViewModel.BuildFlowChoiceList(entries, "");
        Assert.Single(empty);
        // 候选项闭合态显示=Display（三轮 重要8）
        Assert.Equal("日常流程", hit[0].ToString());
    }

    // ===== VM 层快照辅助判定（R4.9 §6.1：矩阵 12；internal static 纯函数直测；三轮 B1 起实现位于宿主） =====

    [Fact]
    public void SnapshotPrecheck_NoSnapshot_StartResumeRejected_ArmPasses()
    {
        // 快照不可考 + start/resume → StatusUncertain（授权判断必须可考，锚点 3）
        var start = TaskCenterHost.SnapshotPrecheck(null, StartupHandoffModes.Start);
        Assert.NotNull(start);
        Assert.Equal(HandoffOutcome.Rejected, start!.Outcome);
        Assert.Equal(HandoffReasonCodes.StatusUncertain, start.ReasonCode);

        var resume = TaskCenterHost.SnapshotPrecheck(null, StartupHandoffModes.Resume);
        Assert.Equal(HandoffReasonCodes.StatusUncertain, resume!.ReasonCode);

        // armTrigger 不受快照限制（挂载等待不占槽位）
        Assert.Null(TaskCenterHost.SnapshotPrecheck(null, StartupHandoffModes.ArmTrigger));
    }

    [Fact]
    public void SnapshotPrecheck_TaskRunning_BgiBusyWithTaskName()
    {
        var busy = new ControlStatus { TaskRunning = true, CurrentTaskName = "锄地", CurrentTaskGroupName = "日常" };
        var r = TaskCenterHost.SnapshotPrecheck(busy, StartupHandoffModes.Start);
        Assert.NotNull(r);
        Assert.Equal(HandoffReasonCodes.BgiBusy, r!.ReasonCode);
        Assert.Contains("锄地", r.Reason);

        var idle = new ControlStatus { TaskRunning = false };
        Assert.Null(TaskCenterHost.SnapshotPrecheck(idle, StartupHandoffModes.Start));
        Assert.Null(TaskCenterHost.SnapshotPrecheck(idle, StartupHandoffModes.Resume));
        Assert.Null(TaskCenterHost.SnapshotPrecheck(idle, StartupHandoffModes.ArmTrigger));
    }
}