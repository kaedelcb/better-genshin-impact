using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using System.Text.Json;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 重型时序夹具的**非并行**收集定义：本组夹具（33 节点全链、并发交错、接管顺序）对 CPU/IO 负载敏感；
/// 与其它测试类并行时会因锁争用/调度延迟而**更频繁地**触发一条已知偶发路径（实测：33 节点夹具在满负载下
/// 偶发第 N 个节点收敛 Reconciling→运行 Unknown，隔离后稳定通过）。**注意（[纠正·2026-09-21 复审]）**：
/// 该现象是**被测运行真的进入了 Unknown**，**不是**单纯的断言/读取假失败——隔离只**降低测试间负载干扰**，
/// **[更正·2026-09-22 批次四十九] 已结题**：负载复现入口（本文件 `P50_LoadRepro_WholeClass_UnderControlledLoad`）
/// 复现并定位根因为 **Windows 文件争用家族**（`UnauthorizedAccessException：Access to the path is denied`）未被
/// 纳入有界重试；修复见 `RunStore`／`ArbitrationLeaseStore`，结题证据见设计稿 §24.61（修复后 10×16 连续两轮全绿；
/// 33 节点用例 `Skip` 已解除、全量连续 3 轮 951/2/953）。**保留**该负载复现入口以便后续回归与实机段复用。
/// </summary>
[CollectionDefinition("TaskCenterHeavyE2E", DisableParallelization = true)]
public sealed class TaskCenterHeavyE2ECollection;

/// <summary>
/// **[C 表 #1／P50][批次四十九] 负载复现入口的专用特性**：xUnit 2.5.3 无「动态 Skip」API，故用自定义
/// <see cref="FactAttribute"/> 在**发现阶段**按环境变量决定 Skip——**不改编源码**即可单独、可重复运行，
/// 且**默认仍是 Skip**（不计为通过、不增加日常负载）。
/// 启用：先设环境变量（值必须为 `1`），再运行。
/// </summary>
public sealed class P50LoadReproFactAttribute : FactAttribute
{
    /// <summary>启用开关（值 `1` 才运行）。</summary>
    public const string EnvVar = "BGI_R5_P50_LOAD_REPRO";

    public P50LoadReproFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1")
            Skip = "P50 负载复现入口：设 " + EnvVar + "=1 显式启用（默认 Skip，不计为通过）";
    }
}

/// <summary>
/// R5.2 B2-γ 第 3 步（节点后继提交改道仲裁面）「一键可跑」验收夹具（owner 0 点击，场景施工方内置）：
/// ①**路径启用门**——`_admissionWired`（E1/E2 入口接线）**不等于**节点改道启用；生产构造恒不启用，只有
///   内部接缝显式 opt-in 两个门才启用（设计稿 §12.3「施工阻断：第 3 步尚不得启用相关路径」/§13.11a）。
/// ②**三态映射**——门面结论 → `BoundarySubmitResult` 必须按「结果确定性」映射：门面确定结论→Rejected，
///   事实不可考（含 `Error`，即 sender 可能已 Accepted、关闭/接管阶段抛异常）→Unknown，绝不反转成确定拒绝。
///   **本组只证明分类，不构成「三态完整链路已验收」**（Accepted 回执读取与「Accepted 后关闭异常」交错归 G6 欠项）。
/// ③**宿主级端到端路由**——门开时节点提交经门面占位/发送且恰好发送一次、运行跑通；门关时不经门面（R4 直通）。
/// ④**并发守卫**——发送期间若另有写入者改动运行记录，G2 合并必须**拒绝**（保守 Unknown），不得静默覆盖。
/// </summary>
[Collection("TaskCenterHeavyE2E")]
public class TaskCenterSuccessorPathGateTests
{
    // ── ① 路径启用门 ─────────────────────────────────────────────────────────────

    private static TaskCenterHost NewHost(string root, bool admissionWired, bool successorAdmissionWired)
        => new(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => null, log: null, runnerFactory: null, readinessOverride: () => (true, null),
            admissionWired: admissionWired, successorAdmissionWired: successorAdmissionWired);

    /// <summary>生产构造（public ctor）必须**不**启用节点改道：E1/E2 已接线，但第 3 步路径门关闭。</summary>
    [Fact]
    public void ProductionCtor_DoesNotEnableSuccessorPathGate()
    {
        var root = NewRoot("tcgate-");
        try
        {
            var host = new TaskCenterHost(
                Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                () => null, () => true, () => null);

            Assert.False(host.SuccessorAdmissionWiredForTest);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SuccessorPathGate_RequiresBothSwitches()
    {
        var root = NewRoot("tcgate-");
        try
        {
            Assert.False(NewHost(root, admissionWired: false, successorAdmissionWired: false).SuccessorAdmissionWiredForTest);
            Assert.False(NewHost(root, admissionWired: true, successorAdmissionWired: false).SuccessorAdmissionWiredForTest);
            Assert.False(NewHost(root, admissionWired: false, successorAdmissionWired: true).SuccessorAdmissionWiredForTest);
            Assert.True(NewHost(root, admissionWired: true, successorAdmissionWired: true).SuccessorAdmissionWiredForTest);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ── ② 三态映射（按结果确定性，不按可否重试） ─────────────────────────────────

    /// <summary>
    /// **§12.3 交错③·发送阶段故障（组件/宿主层）**：节点提交在**已进入可能发送阶段**后失败（端口在记录本次
    /// 发送尝试后抛 `IOException`）⇒ 门面必须按**不可考**处置：**不得判为「确定未受理」**、**不得换通道重发**，
    /// 责任**保留**（Operation `Reconciling`／`Pending`，等待对账），且流程**不得**被标成 `Succeeded`。
    /// 依据：§12.3 M3（阶段边界：不得用笼统 catch 推定未受理）／§12.3 交错③。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_SendStageFailure_StaysReconcilingNoResend()
    {
        var root = NewRoot("tcsendfail-");
        try
        {
            var arbitrationDir = Path.Combine(root, "arbitration");
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                configurePort: port => port.ThrowOnSend = true);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("运行必须收敛（不得悬挂）", probe));
            Assert.True(probe.State != WorkflowRunState.Succeeded, Diag("发送阶段失败后不得假报成功", probe));
            // **责任保留的直接证据**（会诊加固）：运行必须收敛为 **`Unknown`**（不得 Failed/Cancelled 等其它终态），
            // 节点结果必须为 `unknown`，且**未决 Submission 仍在册**、身份与 `LastSendSeq` 未变（不存在更新发送许可）。
            Assert.True(probe.State == WorkflowRunState.Unknown, Diag("发送阶段失败后必须保守停驻为 Unknown", probe));
            Assert.Equal("unknown", probe.FirstNodeResult);
            Assert.True(probe.SendCount == 1, Diag("只允许一次发送尝试（不得重发：实际=" + probe.SendCount + "）", probe));
            var op = probe.Ops.SingleOrDefault(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.NotNull(op);
            // 阶段边界纪律：已进入可能发送阶段 ⇒ **不可考**（Reconciling），不得落成确定拒绝（那会诱发重发）。
            Assert.Equal(OperationRequestState.Reconciling, op!.RequestState);
            Assert.Equal(1, op.LastSendSeq);
            var handoff = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!;
            var live = handoff.Operations.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.Equal(OperationRequestState.Reconciling, live.RequestState);
            Assert.Equal(1, live.LastSendSeq);
            Assert.NotNull(handoff.Submission);                                   // 未决发送责任仍在册（未关闭/未移除）
            Assert.Equal(live.SubmissionIdentity, handoff.Submission!.SubmissionIdentity);
            Assert.Equal(SubmissionState.Reconciling, handoff.Submission.State);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **[P8／§24.62] 「可证实未发送」在宿主链路上的**分离取证**（对照上一支 `NodeSubmit_SendStageFailure_...`）**：
    /// 发送层抛**证据载体** `BgiNotSentException`（通道未就绪／管道未连接／本地请求被拒＝本进程在任何字节写入
    /// 线路之前失败）⇒ 按 §3.2a「无损拒绝类」**确定拒绝 ＋ 开重试窗口**：
    /// ①运行**不得**停在 `Unknown`（受理与否**可判**：远端不可能存在本笔受理事实）；
    /// ②节点结果 `rejected`（不是 `unknown`）；③发送**恰一次**（不重发）；
    /// ④操作状态 `RetryableRejected` ＋ `LastResult.Retryable=true` ＋ 重试窗口**已派生**（§3.3-6 不得重置）
    /// ＋ `LastSendSeq==1`（本轮许可已签发并消费）；⑤`Submission` **已关闭**（无未决发送责任）；
    /// ⑥运行记录内**无受理事实**（`Intent=Rejected`、无 `JobId`）。
    /// **对照**：`NodeSubmit_SendStageFailure_StaysReconcilingNoResend`（同端口在**记录发送尝试之后**抛
    /// `IOException`）⇒ 运行 `Unknown` ＋未决 `Submission` ⇒ **两类失败不得互相代替**（本批核心判据）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_ProvenNotSent_RejectedWithRetryWindow_NotUnknown()
    {
        var root = NewRoot("tcnotsent-");
        try
        {
            var arbitrationDir = Path.Combine(root, "arbitration");
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                configurePort: port => port.SendThrows =
                    new BgiNotSentException(BgiNotSentException.ChannelNotReady, "ext 通道未就绪"));

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("运行必须收敛（不得悬挂）", probe));
            Assert.True(probe.State != WorkflowRunState.Unknown,
                Diag("可证实未发送=受理可判 ⇒ **不得**停在 Unknown", probe));
            Assert.Equal(WorkflowRunState.Failed, probe.State);
            Assert.Equal("rejected", probe.FirstNodeResult);
            Assert.True(probe.SendCount == 1, Diag("只允许一次发送尝试（不重发：实际=" + probe.SendCount + "）", probe));

            var op = probe.Ops.SingleOrDefault(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.NotNull(op);
            Assert.Equal(OperationRequestState.RetryableRejected, op!.RequestState);
            Assert.True(op.LastResult?.Retryable == true);
            Assert.NotNull(op.RetryWindowDeadlineUtc);          // 首次确定拒绝派生（§3.3-6）
            Assert.Equal(1, op.LastSendSeq);

            var handoff = new ArbitrationLeaseStore(arbitrationDir).Read().File!.Handoff!;
            Assert.Null(handoff.Submission);                    // 责任已结清（无未决发送责任）
            var live = handoff.Operations.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.Equal(OperationRequestState.RetryableRejected, live.RequestState);

            var run = new RunStore(Path.Combine(root, "runs")).List().Single(r => r.WorkflowId.Length > 0);
            Assert.Equal(SubmitIntentState.Rejected, run.CurrentSubmission!.Intent);   // 无受理事实
            Assert.True(string.IsNullOrEmpty(run.CurrentSubmission.JobId));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 交错⑤·逐节点释放直接证据（组件层；[新增·2026-09-21 批次十四]）**：
    /// **4 节点**流程（[更正·2026-09-21] 初版 6 节点，批次十七为降低重夹具集合负载降载为 4；实际为
    /// 第 2..4 次发送共 **3 次**观察）中，**第 k 次（k≥2）节点发送时**，前一节点的 Operation 必须**已按自身发送身份终局**
    /// （`TerminalCompleted`）并**迁出 `Active` 计容区**——**逐次**取证（不是只看最终态，故与 P19② 的
    /// 「宿主链路逐节点释放直接断言」对应；不依赖被 P50 暂停的 33 节点用例）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_EachSendObservesPreviousNodeReleased()
    {
        var root = NewRoot("tcsweep2-");
        try
        {
            // **4 节点**（而非 6）：在保持「逐次取证」语义（第 2..4 次发送共 3 次观察）的同时**降低本收集内的负载**——
            // 该收集为 `DisableParallelization` 的宿主级重夹具集合，P50 类负载敏感性对重型夹具敏感（见 §17 P50）。
            var nodeIds = Enumerable.Range(1, 4).Select(i => "n-" + i).ToArray();
            var arbitrationDir = Path.Combine(root, "arbitration");
            var observations = new List<(int Index, string CurrentNodeId, string PrevNodeId, OperationRequestState State, OperationZone Zone)>();
            var violations = new List<string>();
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: nodeIds,
                onBeforeSendWithPayload: (_, index, payloadJson) =>
                {
                    // ①**把本次发送关联到具体节点**（不得按发送序号推断）：从 payload 的 `configName`（＝「配置n-i」）反查。
                    var currentConfig = ParseConfigName(payloadJson);
                    var currentNode = nodeIds.FirstOrDefault(id => "配置" + id == currentConfig);
                    if (currentNode is null)
                    {
                        violations.Add($"第 {index} 次发送的 payload 无法关联到节点（configName={currentConfig ?? "<null>"}）");
                        return;
                    }
                    if (index < 2) return;                       // 首节点没有「前一节点」
                    var currentOrdinal = int.Parse(currentNode.AsSpan(2));
                    var prevNode = "n-" + (currentOrdinal - 1);
                    // 有界重试：满负载下租约文件锁瞬时争用会让单次读抛 IOException（与既有观测点同口径）。
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        try
                        {
                            var ops = new ArbitrationLeaseStore(arbitrationDir).Read().File?.Handoff?.Operations ?? [];
                            // ②前节点必须**唯一命中**（按节点出现身份；两条同 NodeId 记录必须视为取证失败，不得 FirstOrDefault 取一条）。
                            var prevOps = ops.Where(o => o.Candidate?.NodeId == prevNode).ToList();
                            var currentOps = ops.Where(o => o.Candidate?.NodeId == currentNode).ToList();
                            if (prevOps.Count != 1)
                            {
                                violations.Add($"第 {index} 次发送时前节点 {prevNode} 记录数={prevOps.Count}（应唯一）");
                                return;
                            }
                            var prev = prevOps[0];
                            observations.Add((index, currentNode, prevNode, prev.RequestState, prev.Zone));
                            // ③**§16 交错⑤ 等强判据**：前节点终局 + **`Tombstone`（真正迁出计容区）** + 发送身份非空；
                            //    且同一时点**当前节点必须已在 `Active`**（本轮占位）。
                            if (prev.RequestState != OperationRequestState.TerminalCompleted
                                || prev.Zone != OperationZone.Tombstone)
                                violations.Add($"第 {index} 次发送时前节点 {prevNode} 未释放：state={prev.RequestState} zone={prev.Zone}");
                            if (string.IsNullOrEmpty(prev.SubmissionIdentity))
                                violations.Add($"第 {index} 次发送时前节点 {prevNode} 缺少发送身份（不得仅按 NodeId 关联）");
                            if (currentOps.Count != 1 || currentOps[0].Zone != OperationZone.Active)
                                violations.Add($"第 {index} 次发送时当前节点 {currentNode} 未处于 Active（记录数={currentOps.Count}，zone={(currentOps.Count == 1 ? currentOps[0].Zone.ToString() : "<n/a>")}）");
                            return;
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(5);
                        }
                    }
                    violations.Add($"第 {index} 次发送取证失败（租约读取争用）");
                });

            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("4 节点流程应收口成功", probe));
            Assert.True(probe.SendCount == 4, Diag("应恰好发送 4 次", probe));
            Assert.Equal(3, observations.Count);            // 第 2..4 次发送各观察一次（共 3 次逐节点释放取证）
            Assert.Empty(violations);                       // 逐次取证：无一次出现「前节点未终局/仍占主槽位」
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **G1／§13.10 A1 节点冻结语义完整性**：`FreezeNode` 必须是 JSON 往返**深拷贝**——
    /// 语义等价（序列化逐字相同）且与原对象**不共享可变子树**（排队期间的流程编辑不得影响已冻结请求）。
    /// </summary>
    [Fact]
    public void FreezeNode_IsDeepCopy_AndIsolatedFromLaterMutation()
    {
        var node = new WorkflowNode
        {
            NodeId = "n-1",
            Kind = "resource.oneDragonConfig",
            Ref = new WorkflowResourceRef { Config = "配置A", Revision = "rev-1" },
            Strategies =
            [
                new WorkflowStrategy
                {
                    Kind = "strategy.retry",
                    Params = new Dictionary<string, JsonElement> { ["max"] = JsonSerializer.SerializeToElement(3) },
                },
            ],
        };

        var frozen = TaskCenterHost.FreezeNode(node);

        Assert.NotSame(node, frozen);
        Assert.NotSame(node.Ref, frozen.Ref);
        Assert.NotSame(node.Strategies, frozen.Strategies);
        Assert.Equal(JsonSerializer.Serialize(node), JsonSerializer.Serialize(frozen)); // 语义等价

        node.Ref!.Config = "被改写配置";
        node.Strategies![0].Kind = "被改写策略";
        node.NodeId = "被改写节点";

        Assert.Equal("配置A", frozen.Ref!.Config);              // 深拷贝：原对象改写不影响副本
        Assert.Equal("strategy.retry", frozen.Strategies![0].Kind);
        Assert.NotSame(node.Strategies[0].Params, frozen.Strategies[0].Params);
        Assert.Equal("n-1", frozen.NodeId);
    }

    /// <summary>
    /// **G5 准入阶段请求内容指纹**：确定性（同内容同指纹）＋判别性（节点内容/出现身份/提交选项/提交身份
    /// 任一不同 ⇒ 指纹不同——**限于本夹具样本**，不外推为「任意语义等价节点必得同一指纹」）。
    /// 空串或与内容无关的常量会让门面「同 candidateId 不同载荷＝整组拒绝」形同虚设。
    /// </summary>
    [Fact]
    public void SuccessorPayloadFingerprint_IsDeterministicAndDiscriminating()
    {
        var node = new WorkflowNode
        {
            NodeId = "n-1",
            Kind = "resource.oneDragonConfig",
            Ref = new WorkflowResourceRef { Config = "配置A", Revision = "rev-1" },
        };
        var occ = new WorkflowNodeOccurrence("n-1", 0, 0, 0);

        var baseline = TaskCenterHost.SuccessorPayloadFingerprint(node, occ, suppress: true, attempt: 1, submissionKey: "k-1");

        Assert.Equal(24, baseline.Length);
        Assert.Equal(baseline,
            TaskCenterHost.SuccessorPayloadFingerprint(TaskCenterHost.FreezeNode(node), occ, true, 1, "k-1")); // 确定性

        Assert.NotEqual(baseline, TaskCenterHost.SuccessorPayloadFingerprint(
            new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig", Ref = new WorkflowResourceRef { Config = "配置B", Revision = "rev-1" } },
            occ, true, 1, "k-1"));                                                                    // 内容不同
        Assert.NotEqual(baseline, TaskCenterHost.SuccessorPayloadFingerprint(
            node, new WorkflowNodeOccurrence("n-1", 0, 1, 0), true, 1, "k-1"));                        // 出现身份不同
        Assert.NotEqual(baseline, TaskCenterHost.SuccessorPayloadFingerprint(node, occ, false, 1, "k-1")); // 提交选项不同
        Assert.NotEqual(baseline, TaskCenterHost.SuccessorPayloadFingerprint(node, occ, true, 2, "k-1"));  // attempt 不同
        Assert.NotEqual(baseline, TaskCenterHost.SuccessorPayloadFingerprint(node, occ, true, 1, "k-2"));  // 提交身份不同
    }

    [Theory]
    // 门面给出**确定结论** → Rejected（本层不自行断言「一定没发送」）
    [InlineData(AdmissionResultKind.TerminalRejected, false)]
    [InlineData(AdmissionResultKind.RetryableRejected, false)] // 曾错误映射为 Unknown（按「可否重试」而非「结果确定性」）
    [InlineData(AdmissionResultKind.NotSelected, false)]
    [InlineData(AdmissionResultKind.F11Blocked, false)]
    [InlineData(AdmissionResultKind.NeedPreemptConfirm, false)]
    // 事实不可考 → Unknown（不猜成功、也不猜失败；含 Error＝sender 可能已 Accepted、随后关闭/接管抛异常）
    [InlineData(AdmissionResultKind.NeedReconcile, true)]
    [InlineData(AdmissionResultKind.Reconciling, true)]
    [InlineData(AdmissionResultKind.Error, true)] // 曾落入 `_ => Rejected` 兜底＝事实反转
    public void MapAdmissionResultToBoundary_ByResultCertainty(AdmissionResultKind kind, bool expectUncertain)
    {
        var result = new AdmissionResult
        {
            Kind = kind,
            ReasonCode = "rc-1",
            Detail = "detail-1",
        };

        var mapped = TaskCenterHost.MapAdmissionResultToBoundary(result);

        Assert.Equal(expectUncertain, mapped.Uncertain);
        Assert.False(mapped.Accepted);
        Assert.Null(mapped.JobId);
        Assert.Contains("rc-1", mapped.RejectReason);
        Assert.Contains("detail-1", mapped.RejectReason);
    }

    // ── ③④ 宿主级端到端路由 ＋ 并发守卫 ──────────────────────────────────────────

    /// <summary>
    /// **G4 游标纪律（负向）**：游标缺失或与本次提交的出现身份不一致 ⇒ **可证实未发送地拒绝**，
    /// 且在**任何租约副作用之前**（不得因此创建 arbitration 目录/租约）。直接以内部准入方法驱动，
    /// 场景施工方内置、owner 0 点击。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    public async Task SuccessorSubmit_CursorMissingOrMismatched_RejectedWithoutLeaseSideEffect(string mode)
    {
        var root = NewRoot("tccursor-");
        var runsDir = Path.Combine(root, "runs");
        try
        {
            var runs = new RunStore(runsDir);
            var host = new TaskCenterHost(
                Path.Combine(root, "flows"), runsDir, Path.Combine(root, "catalog.json"),
                () => null, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                admissionWired: true, successorAdmissionWired: true);

            var run = runs.CreateRun("wf-x", "r-1");
            run.CurrentSubmission = new WorkflowSubmission
            {
                Key = RunStore.DeriveSubmissionKey(run.RunId, "n-1", 0, 0, 1),
                NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1,
                Intent = SubmitIntentState.IntentRecorded,
            };
            run.Cursor = mode == "missing"
                ? null
                : new WorkflowNodeCursor { NodeId = "n-OTHER", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
            runs.Update(run);

            var result = await host.SubmitSuccessorViaAdmissionAsync(
                new WorkflowSubmitRequest(run, new WorkflowNodeOccurrence("n-1", 0, 0, 0),
                    new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig" }, true),
                default);

            Assert.False(result.Accepted);
            Assert.False(result.Uncertain); // 可证实未发送 ⇒ 确定拒绝（不是「待对账」）
            Assert.Contains("游标", result.RejectReason);
            Assert.False(Directory.Exists(Path.Combine(root, "arbitration")), // 拒绝发生在租约副作用之前
                "游标判定必须早于门面初始化（不得创建 arbitration 目录）");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 交错③·占位前校验拒绝（宿主层；[新增·2026-09-21 批次十九]）**：`SubmitSuccessorViaAdmissionAsync`
    /// 的**本地预检**（提交缺失／意图状态不合法／出现身份不符／节点冻结失败／F11 独立闸门）必须在
    /// **任何租约副作用之前**给出**确定拒绝**——逐支断言 `Accepted=false` 且 `Uncertain=false`
    /// （**不得**报成「待对账」）、**注入的发送端口 `SendCount == 0`**（"零发送"的**直接**证据，不靠
    /// 「目录未创建」间接推断）、`arbitration` 目录未被创建（＝门面未初始化 ⇒ **零租约初始化、零占位、
    /// 零仲裁写入**；事实快照读取本身不计入）、拒绝原因指向该预检项。
    /// 依据：§12.3 M3 阶段边界（尚未进入可能发送阶段＝可证实未发送）／§16 交错③「校验拒绝 ⇒ 零发送」。
    /// **范围**：本夹具只覆盖「校验拒绝」支；「准备阶段 `RunStore` 更新失败」按 §17 P49 归 B4（**未验收**）。
    /// </summary>
    [Theory]
    [InlineData("submission-null")]
    [InlineData("intent-state-invalid")]
    [InlineData("identity-mismatch")]
    [InlineData("freeze-fail")]
    [InlineData("f11")]
    public async Task SuccessorSubmit_PreOccupyRejection_DeterministicNoSendNoLeaseSideEffect(string mode)
    {
        var root = NewRoot("tcprecheck-");
        var runsDir = Path.Combine(root, "runs");
        try
        {
            using var client = new BgiExternalClient();
            var port = new RoutingFakePort();            // 计数发送端口：本用例任何一次实际发送都必须为 0
            var runs = new RunStore(runsDir);
            var host = new TaskCenterHost(
                Path.Combine(root, "flows"), runsDir, Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = RoutingFakePort.Epoch, TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true, successorAdmissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams
                {
                    Epoch = RoutingFakePort.Epoch,
                    F11Active = mode == "f11",
                    ProductionBoundaryFactory = (_, r) => new BgiWorkflowExecutionBoundary(port, r),
                });

            var run = runs.CreateRun("wf-x", "r-1");
            // 「提交缺失」＝`CurrentSubmission == null`；「意图状态不合法」＝存在提交但意图非 `IntentRecorded`
            // （引擎纪律要求意图先行；两者在门面里同属「可证实未发送」的拒绝分支，但构造互不相同）。
            run.CurrentSubmission = mode == "submission-null"
                ? null
                : new WorkflowSubmission
                {
                    Key = RunStore.DeriveSubmissionKey(run.RunId, "n-1", 0, 0, 1),
                    NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1,
                    Intent = mode == "intent-state-invalid" ? SubmitIntentState.Submitted : SubmitIntentState.IntentRecorded,
                };
            run.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
            runs.Update(run);

            // 「身份不符」＝提交请求的出现身份与已落盘意图不一致（可证实未发送）
            var occurrence = mode == "identity-mismatch"
                ? new WorkflowNodeOccurrence("n-OTHER", 0, 0, 0)
                : new WorkflowNodeOccurrence("n-1", 0, 0, 0);
            // 「冻结失败」＝节点无法完成深拷贝冻结（`FreezeNode` 反序列化为空 ⇒ 抛错并被归类为可证实未发送）
            var node = new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig" };

            var result = await host.SubmitSuccessorViaAdmissionAsync(
                new WorkflowSubmitRequest(run, occurrence, mode == "freeze-fail" ? null! : node, true), default);

            Assert.False(result.Accepted, "预检拒绝不得报受理（mode=" + mode + "，reason=" + result.RejectReason + "）");
            Assert.False(result.Uncertain, "可证实未发送 ⇒ 确定拒绝，不得报「待对账」（mode=" + mode + "）");
            Assert.Equal(0, port.SendCount);   // "零发送"的直接证据：发送接缝一次都未被调用
            Assert.False(Directory.Exists(Path.Combine(root, "arbitration")),
                "预检拒绝必须早于门面初始化（不得创建 arbitration 目录 ⇒ 零租约初始化、零占位、零仲裁写入，mode=" + mode + "）");
            var expect = mode switch
            {
                "submission-null" => "提交意图缺失或身份不符",
                "intent-state-invalid" => "提交意图缺失或身份不符",
                "identity-mismatch" => "提交意图缺失或身份不符",
                "freeze-fail" => "冻结失败",
                _ => "F11",
            };
            Assert.Contains(expect, result.RejectReason);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§16 交错⑥·四类入口 Scope 来源矩阵（组件/宿主层；[新增·2026-09-21 批次二十]）·格 A＝面板启动**：
    /// 面板 E1 启动在租约中登记**流程级 start 操作**（无节点身份 ＋ `RunBinding=runId` ＋ 固定 `Scope`），
    /// 后继节点提交必须**继承该固定 Scope**（逐字一致），不得按当前纪元重建。
    /// **证明边界（如实，同 §17 P28 口径）**：本格**不能独立区分**「继承登记值」与「重读当前值」——
    /// 二者在本夹具中取值相同；区分需要「纪元在 E1 与节点提交之间变化」的构造（恢复路径的同型负向夹具见
    /// `TaskCenterHostRecoveryAdmissionTests.ResumeRun_EpochChanged_RejectedNoSilentRebinding`）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_InheritsRegisteredFlowScope()
    {
        var root = NewRoot("tcscopea-");
        try
        {
            // **1 节点**：本格只需「流程级登记 1 条 ＋ 节点操作 ≥1 条」即可比较 Scope（降低重夹具集合内负载，见 §17 P50）。
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"]);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("1 节点流程应收口成功", probe));
            Assert.Equal(1, probe.SendCount);

            // 流程级登记＝无节点身份 ＋ Intent=start ＋ RunBinding=本次运行（`TryGetAdmissionScope` 的唯一识别口径）
            var flowOps = probe.Ops.Where(o => string.IsNullOrEmpty(o.Candidate?.NodeId)
                                               && o.Intent == "start"
                                               && o.RunBinding == probe.RunId).ToList();
            Assert.True(flowOps.Count == 1, Diag("流程级 start 登记必须唯一命中（识别口径：无节点身份＋Intent=start＋RunBinding）", probe));
            var flowScope = flowOps[0].Candidate?.Scope ?? "";
            Assert.False(string.IsNullOrEmpty(flowScope), Diag("流程级登记的固定 Scope 不得为空", probe));
            Assert.StartsWith("bgi:", flowScope);   // `bgi:{实例}:{epoch}` 形状（§2.2）

            // 后继节点操作逐条继承同一固定 Scope（不得各节点自行重建）
            var nodeOps = probe.Ops.Where(o => !string.IsNullOrEmpty(o.Candidate?.NodeId)).ToList();
            Assert.True(nodeOps.Count >= 1, Diag("至少一条节点操作在册（本格用于与流程级登记比较 Scope）", probe));
            Assert.All(nodeOps, o => Assert.Equal(flowScope, o.Candidate!.Scope));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§16 交错④「接管故障后**取消**」组合（[2026-09-21 批次二十五]）**：先制造「远端已受理、接管落盘失败」
    /// （⇒ 不可考、责任保留），**随后取消**（宿主关闭令牌＝取消运行器传给发送段的同一令牌），断言：
    /// **取消不得释放责任、不得新增发送许可、不得把不可考改写成确定结论**——
    /// 取消前后的未决 `Submission` 状态/身份、节点操作状态/`LastSendSeq`/`WireSubmitKey` **全等**；
    /// 发送仍恰一次；盘上仍**无受理事实**；运行仍 `Unknown`。
    /// **层级限定**：取消经**令牌**维度（宿主关闭）；**E4 控制热键／命令执行器**入口的取消映射不在本夹具范围。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_TakeoverPersistFailedThenCancel_KeepsResponsibilityNoResend()
    {
        var root = NewRoot("tctakeover-cancel-");
        TaskCenterHost? host = null;
        RoutingFakePort? port = null;
        var hostShutDown = false;
        var injected = 0;
        try
        {
            using var client = new BgiExternalClient();
            port = new RoutingFakePort();
            var flowsDir = Path.Combine(root, "flows");
            var runsDir = Path.Combine(root, "runs");
            var ws = new WorkflowStore(flowsDir);
            var doc = new WorkflowDocument
            {
                Name = "接管故障后取消流程",
                Activation = new WorkflowActivation { Status = "active" },
                Nodes =
                [
                    new WorkflowNode
                    {
                        NodeId = "n-1", Kind = "resource.oneDragonConfig",
                        Ref = new WorkflowResourceRef { Config = "配置n-1", Revision = "rev-1" },
                    },
                ],
            };
            ws.Save(doc, null);
            var runs = new RunStore(runsDir);
            host = new TaskCenterHost(
                flowsDir, runsDir, Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = RoutingFakePort.Epoch, TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true, successorAdmissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams
                {
                    Epoch = RoutingFakePort.Epoch,
                    ProductionBoundaryFactory = (_, r) =>
                    {
                        // 接管写专属形态注入（与批次二十二同口径）：仅远端受理事实的落盘失败。
                        r.PublishFaultForTest = rec =>
                        {
                            if (rec.CurrentSubmission is not { } s
                                || s.Intent != SubmitIntentState.Accepted
                                || string.IsNullOrEmpty(s.JobId)
                                || string.IsNullOrEmpty(s.AcceptedSendIdentity)
                                || s.ObservedTerminal is not null
                                || (rec.NodeOutcomes?.Count ?? 0) != 0)
                                return null;
                            Interlocked.Increment(ref injected);
                            return new IOException("fixture: takeover persist fault");
                        };
                        return new BgiWorkflowExecutionBoundary(port!, r);
                    },
                });

            var start = await host.StartWorkflowAsync(doc.WorkflowId!);
            Assert.Equal(HostActionStatus.Registered, start.Status);

            // 等接管故障收敛（有界）
            WorkflowRunRecord? converged = null;
            for (var i = 0; i < 600 && converged is null; i++)
            {
                var candidate = runs.List().FirstOrDefault();
                if (candidate?.State is WorkflowRunState.Unknown or WorkflowRunState.Failed
                    or WorkflowRunState.Succeeded or WorkflowRunState.Cancelled) converged = candidate;
                else await Task.Delay(10);
            }
            Assert.NotNull(converged);
            Assert.Equal(WorkflowRunState.Unknown, converged!.State);
            Assert.Equal(1, port.SendCount);
            Assert.Equal(1, Volatile.Read(ref injected));                 // 接管写故障**恰命中一次**（时序真实）
            Assert.Equal("unknown", converged.NodeOutcomes.Last(o => o.NodeId == "n-1").Result);
            Assert.True(port.LastSendToken.CanBeCanceled, "发送段须持有调用方令牌（§17 P6）");
            var before = ReadLeaseFileWithRetry(root)?.Handoff;
            Assert.NotNull(before?.Submission);
            Assert.Equal(SubmissionState.Reconciling, before!.Submission!.State);
            var opBefore = before.Operations!.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            var runBefore = runs.List().Single();
            var keyBefore = runBefore.CurrentSubmission!.Key;
            var intentBefore = runBefore.CurrentSubmission.Intent;
            var nodeResultBefore = runBefore.NodeOutcomes.Last(o => o.NodeId == "n-1").Result;

            // **随后取消**（调用方取消＝宿主关闭；本夹具的取消**不在飞发送**——「取消确发生在发送所用**同一枚令牌**上」
            // 的因果证据由 §24.39 在飞发送版夹具提供（端口侧 `SendCanceledByToken`）。本夹具证明的是
            // 「接管故障**后**的取消**不改变**责任载体与持久化事实」，故不重复主张令牌身份。）
            await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
            hostShutDown = true;

            // 取消**不得**释放责任/新增许可/把不可考改写为确定结论
            Assert.Equal(1, port.SendCount);
            var after = ReadLeaseFileWithRetry(root)?.Handoff;
            Assert.NotNull(after?.Submission);
            var opAfter = after!.Operations!.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.Equal(SubmissionState.Reconciling, after.Submission!.State);
            Assert.Equal(OperationRequestState.Reconciling, opBefore.RequestState);   // 取消前即非终局
            Assert.Equal(opBefore.RequestState, opAfter.RequestState);                // 取消不改变操作状态
            Assert.False(string.IsNullOrEmpty(opBefore.SubmissionIdentity));
            Assert.False(string.IsNullOrEmpty(opAfter.SubmissionIdentity));
            Assert.Equal(opBefore.SubmissionIdentity, before.Submission.SubmissionIdentity);  // 操作↔责任互证（前）
            Assert.Equal(opAfter.SubmissionIdentity, after.Submission.SubmissionIdentity);    // 操作↔责任互证（后）
            Assert.Equal(1, opBefore.LastSendSeq);
            Assert.Equal(opBefore.LastSendSeq, before.Submission.SendSeq);            // 许可水位＝未决发送序号
            Assert.Equal(1, opAfter.LastSendSeq);                                     // 取消**未新增发送许可**
            Assert.Equal(opAfter.LastSendSeq, after.Submission.SendSeq);
            Assert.Equal(before.Submission.SubmissionIdentity, after.Submission.SubmissionIdentity);
            Assert.Equal(before.Submission.SendSeq, after.Submission.SendSeq);
            Assert.Equal(opBefore.SubmissionIdentity, opAfter.SubmissionIdentity);
            Assert.Equal(opBefore.LastSendSeq, opAfter.LastSendSeq);
            Assert.Equal(opBefore.WireSubmitKey, opAfter.WireSubmitKey);

            var run = runs.List().Single();
            Assert.Equal(WorkflowRunState.Unknown, run.State);                  // 仍不可考（未被取消改写）
            Assert.Equal(nodeResultBefore, run.NodeOutcomes.Last(o => o.NodeId == "n-1").Result);  // 节点结果未变
            Assert.Equal("unknown", run.NodeOutcomes.Last(o => o.NodeId == "n-1").Result);
            Assert.Equal(intentBefore, run.CurrentSubmission!.Intent);          // 取消**未改写**持久化事实
            Assert.Equal(keyBefore, run.CurrentSubmission.Key);
            Assert.Equal(SubmitIntentState.Submitted, run.CurrentSubmission.Intent);
            Assert.Null(run.CurrentSubmission.JobId);                           // 盘上仍无受理事实
            Assert.Null(run.CurrentSubmission.AcceptedSendIdentity);
            Assert.Equal(1, run.CurrentSubmission.Attempt);
        }
        finally
        {
            if (!hostShutDown && host is not null)
            {
                try { await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§17 P6／§12.3 M3（[2026-09-21 批次二十四]）宿主级端到端：在飞发送期取消 ⇒ `Unknown`＋责任保持**。
    /// 构造：端口在发送入口**阻塞在取消令牌上**（在飞）；随后触发宿主关闭（＝取消运行器传给发送段的**同一令牌**）。
    /// 断言：发送**恰一次**（无重发）、端口观察到**可取消令牌**、运行收敛 `Unknown`（不得成功、不得确定拒绝）、
    /// 节点结果 `unknown`、节点操作与未决 `Submission` 保持**非终局**（`Reconciling`）且**身份全等**、
    /// 盘上**无受理事实**（`JobId == null`）。⇒ 「取消是调用结束方式、**不是关闭依据**」在宿主链路上被验证。
    /// </summary>
    [Fact]
    public async Task CancelDuringInFlightSend_ConvergesUnknown_ResponsibilityRetained()
    {
        var root = NewRoot("tccancel-");
        TaskCenterHost? host = null;
        RoutingFakePort? port = null;
        var hostShutDown = false;
        try
        {
            using var client = new BgiExternalClient();
            port = new RoutingFakePort { BlockUntilCanceled = true };
            var flowsDir = Path.Combine(root, "flows");
            var runsDir = Path.Combine(root, "runs");
            var ws = new WorkflowStore(flowsDir);
            var doc = new WorkflowDocument
            {
                Name = "取消链流程",
                Activation = new WorkflowActivation { Status = "active" },
                Nodes =
                [
                    new WorkflowNode
                    {
                        NodeId = "n-1", Kind = "resource.oneDragonConfig",
                        Ref = new WorkflowResourceRef { Config = "配置n-1", Revision = "rev-1" },
                    },
                ],
            };
            ws.Save(doc, null);
            var runs = new RunStore(runsDir);
            host = new TaskCenterHost(
                flowsDir, runsDir, Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = RoutingFakePort.Epoch, TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true, successorAdmissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams
                {
                    Epoch = RoutingFakePort.Epoch,
                    ProductionBoundaryFactory = (_, r) => new BgiWorkflowExecutionBoundary(port, r),
                });

            var start = await host.StartWorkflowAsync(doc.WorkflowId!);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await port.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));   // 已进入**在飞**发送
            Assert.Equal(1, port.SendCount);
            Assert.True(port.LastSendToken.CanBeCanceled, "发送段必须持有**调用方令牌**（§17 P6）");
            Assert.False(port.LastSendToken.IsCancellationRequested);

            // 取消「运行器传给发送段的同一令牌」：宿主关闭令牌（生产：Stop/退出路径）。
            // [会诊加固] **因果证据**＝端口侧「在飞发送因该令牌被取消而退出」信号（不得用「ShutdownAsync 返回」代替——
            // 宿主关闭自身可能有界收敛运行，从而在发送仍悬挂时也能返回）。
            var shutdownTask = host.ShutdownAsync();
            await port.SendCanceledByToken.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(port.LastSendToken.IsCancellationRequested, "取消必须真的发生在运行器传给发送段的同一令牌上");
            await shutdownTask.WaitAsync(TimeSpan.FromSeconds(20));
            hostShutDown = true;

            Assert.Equal(1, port.SendCount);                                   // **无重发**
            var run = runs.List().Single();
            Assert.Equal(WorkflowRunState.Unknown, run.State);                 // 不可考（不得成功、不得确定拒绝）
            Assert.Equal("unknown", run.NodeOutcomes.Last(o => o.NodeId == "n-1").Result);
            // 盘上**无受理事实**（与相邻接管故障夹具同口径：三个字段一起断言，避免「部分受理事实」蒙混）
            Assert.Equal(SubmitIntentState.Submitted, run.CurrentSubmission!.Intent);
            Assert.Null(run.CurrentSubmission.JobId);
            Assert.Null(run.CurrentSubmission.AcceptedSendIdentity);
            var sendKey = run.CurrentSubmission.Key;

            var handoff = ReadLeaseFileWithRetry(root)?.Handoff;
            Assert.NotNull(handoff);
            var op = handoff!.Operations!.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.Equal(OperationRequestState.Reconciling, op.RequestState);  // 责任载体非终局
            Assert.False(string.IsNullOrEmpty(op.SubmissionIdentity));
            Assert.Equal(1, op.LastSendSeq);                                   // 取消**未**新增发送许可
            Assert.Equal(sendKey, op.WireSubmitKey);                           // 未换键
            Assert.Equal(1, op.Candidate!.Attempt);
            Assert.NotNull(handoff.Submission);
            Assert.Equal(SubmissionState.Reconciling, handoff.Submission!.State);
            Assert.Equal(op.SubmissionIdentity, handoff.Submission.SubmissionIdentity);
            Assert.Equal(op.LastSendSeq, handoff.Submission.SendSeq);          // 许可水位与未决责任一致
        }
        finally
        {
            // [会诊加固] 清理路径**有界**：先放行被阻塞的在飞发送（**仅清理用**，不被当作取消证据），
            // 再给宿主关闭加超时——避免「首次关闭超时后，finally 里无超时 await」导致测试整体悬挂。
            try { port.ReleaseBlockedSend.TrySetResult(); } catch { }
            if (!hostShutDown && host is not null)
            {
                try { await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
            TryDelete(root);
        }
    }

    /// <summary>
    /// **[C 表 #1／P50][批次四十九] 负载复现入口（覆盖整个重夹具类；可控负载＋可重复；默认 Skip，不计为通过）**：
    /// ①**可控负载**：按 `BGI_R5_P50_LOAD_CPUS`（默认＝`Environment.ProcessorCount`）起 CPU 忙等负载线程；
    /// ②**覆盖整个用例类**：逐轮**直接调用**该类的重夹具（包含受 P50 影响被 `Skip` 的 **33 节点端到端**方法本体，
    /// 直调绕过 Skip 属性、仍按其**严格断言**执行）：`NodeSubmit_33NodeFlow_NoCapacityExhaustion`／
    /// `NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission`／`NodeSubmit_EachSendObservesPreviousNodeReleased`／
    /// `NodeSubmit_GoesThroughAdmissionFace_WhenPathGateOpen`；
    /// ③**轮次**：`BGI_R5_P50_LOAD_ROUNDS`（默认 3）；
    /// ④**根因取证**：任一夹具抛出即收集其**异常消息**（各夹具内部已带 `Diag(...)` 运行态/逐操作快照）与轮次号，
    /// 循环结束后统一失败输出——不吞异常、不放宽断言、不改既有夹具。
    /// **启用**：`$env:BGI_R5_P50_LOAD_REPRO=1` 后运行 `dotnet test --filter P50_LoadRepro`（可重复、可并行叠加外部负载）。
    /// </summary>
    [P50LoadReproFact]
    public async Task P50_LoadRepro_WholeClass_UnderControlledLoad()
    {
        var roundsRaw = Environment.GetEnvironmentVariable("BGI_R5_P50_LOAD_ROUNDS");
        if (!int.TryParse(roundsRaw, out var rounds) || rounds <= 0) rounds = 3;
        var cpusRaw = Environment.GetEnvironmentVariable("BGI_R5_P50_LOAD_CPUS");
        if (!int.TryParse(cpusRaw, out var loadCpus) || loadCpus < 0) loadCpus = Environment.ProcessorCount;
        Assert.True(loadCpus > 0, "负载线程数必须 ≥1（CPUS=0 会让本入口在**零负载**下假绿）。");

        using var loadCts = new CancellationTokenSource();
        using var ready = new CountdownEvent(loadCpus);
        using var start = new ManualResetEventSlim(false);
        var loadErrors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var loadTasks = Enumerable.Range(0, loadCpus).Select(_ => Task.Run(() =>
        {
            try
            {
                ready.Signal();                       // **就绪屏障**：先报就绪
                start.Wait(loadCts.Token);            // 再等统一开跑 ⇒ 保证首个夹具执行时负载已在跑
                var spin = System.Diagnostics.Stopwatch.StartNew();
                while (!loadCts.IsCancellationRequested)
                {
                    // 忙等 + 短让出：制造可预测的 CPU 争用（不睡眠，避免负载失真）
                    if (spin.ElapsedMilliseconds % 50 == 0) Thread.Sleep(1);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常收尾
            }
            catch (Exception ex)
            {
                loadErrors.Enqueue(ex);               // 负载线程异常**不得静默**（清理后统一断言）
            }
        }, loadCts.Token)).ToArray();
        var failures = new List<string>();
        var workersDiedEarly = false;
        try
        {
            // [第二轮会诊重要项处置] **就绪等待与开跑都放进 try**：就绪超时也必须走 finally 取消负载线程，
            // 否则被阻塞在 `start.Wait` 的线程会泄漏到测试之后。
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "负载线程未能在 30s 内就绪。");
            start.Set();
            for (var round = 1; round <= rounds; round++)
            {
                var sut = new TaskCenterSuccessorPathGateTests();
                foreach (var (name, run) in new (string, Func<Task>)[]
                         {
                             ("NodeSubmit_33NodeFlow_NoCapacityExhaustion", sut.NodeSubmit_33NodeFlow_NoCapacityExhaustion),
                             ("NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission", sut.NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission),
                             ("NodeSubmit_EachSendObservesPreviousNodeReleased", sut.NodeSubmit_EachSendObservesPreviousNodeReleased),
                             ("NodeSubmit_GoesThroughAdmissionFace_WhenPathGateOpen", sut.NodeSubmit_GoesThroughAdmissionFace_WhenPathGateOpen),
                         })
                {
                    try
                    {
                        await run().WaitAsync(TimeSpan.FromSeconds(180));
                    }
                    catch (TimeoutException)
                    {
                        // **[首轮会诊重要项处置] 超时不得叠加执行**：底层夹具无法强制中止，故记录后**立即停止**
                        // 本轮与后续轮次（避免超时夹具与下一夹具/下一轮并发，破坏串行前提与临时目录生命周期）。
                        failures.Add($"第 {round} 轮 · {name} ⇒ 超时（180s；底层任务不可强制中止，已停止后续轮次）");
                        workersDiedEarly = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"第 {round} 轮 · {name} ⇒ {ex.GetType().Name}：{ex.Message}");
                    }
                }
                if (workersDiedEarly) break;
            }
        }
        finally
        {
            if (loadTasks.Any(t => t.IsCompleted && t.IsFaulted)) workersDiedEarly = true;
            loadCts.Cancel();
            try { await Task.WhenAll(loadTasks).WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* 清理：负载线程异常不遮蔽真实失败 */ }
        }

        Assert.True(loadErrors.IsEmpty,
            "负载线程自身抛出异常（负载前提被破坏，本轮证据不可用）："
            + string.Join(" ｜ ", loadErrors.Select(e => e.GetType().Name)));
        Assert.True(failures.Count == 0,
            "P50 负载复现命中 " + failures.Count + " 次异常（共 " + rounds + " 轮 × 4 夹具，负载线程 "
            + loadCpus + "）：\n" + string.Join("\n---\n", failures));
    }

    /// <summary>
    /// **P50 诊断探针（[2026-09-21 批次三十六]；**默认 Skip，不计为通过**）**：
    /// **性质（[会诊收窄]）**：本探针只是**隔离 + 重复**的取证入口，**不满足** §17 P50 要求的「**负载下**重复 + 覆盖目标夹具/整个用例类」
    /// ——它既未制造全量并发负载，也未运行实际红灯的 `NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission`。
    /// **启用方式（可单独、可重复）**：临时移除本方法的 `Skip` 参数，并（可选）设 `BGI_R5_P50_REPEAT=<n>`（默认 10 轮）后运行
    /// `dotnet test --filter P50_DiagnosticRepeat`；失败时输出取证快照（运行状态/说明/发送计数/逐操作 状态·许可水位·区域·原因码）。
    /// **纪律**：不修改任何既有夹具的断言；默认 Skip ⇒ 既不增加日常负载，也不会计入「通过」。
    /// </summary>
    [Fact(Skip = "P50 诊断探针：需显式启用（见 §24.47）；默认 Skip，不计为通过")]
    public async Task P50_DiagnosticRepeat_OptIn()
    {
        var raw = Environment.GetEnvironmentVariable("BGI_R5_P50_REPEAT");
        if (!int.TryParse(raw, out var repeats) || repeats <= 0) repeats = 10;   // 启用后默认 10 轮；非法值不再静默空跑

        var nodeIds = Enumerable.Range(1, 4).Select(i => "n-" + i).ToArray();
        var anomalies = new List<string>();
        for (var round = 1; round <= repeats; round++)
        {
            var root = NewRoot("tcp50diag-");
            try
            {
                var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: nodeIds);
                if (probe.State != WorkflowRunState.Succeeded || probe.SendCount != nodeIds.Length)
                {
                    var ops = string.Join("; ", probe.Ops.Select(o =>
                        (o.Candidate?.NodeId ?? "-") + "/" + o.RequestState + "/send=" + o.LastSendSeq + "/zone=" + o.Zone
                        + "/" + (o.LastResult?.ReasonCode ?? "-")));
                    anomalies.Add($"第 {round} 轮：state={probe.State} sends={probe.SendCount} note={probe.Note} ops=[{ops}]");
                }
            }
            finally
            {
                TryDelete(root);
            }
        }

        Assert.True(anomalies.Count == 0,
            "P50 诊断复现到 " + anomalies.Count + "/" + repeats + " 轮异常（取证快照如下）：\n" + string.Join("\n", anomalies));
    }

    /// <summary>
    /// **§16 交错⑥·格 A′＝「继承登记值」与「重读当前值」的判别反例（[2026-09-21 会诊加固]）**：
    /// E1 已按纪元 `E0` 登记固定 Scope；在**节点尚未准入之前**把当前纪元改为另一值（经接缝 `Epoch`）。
    /// 后继提交若**继承登记值**，节点候选的目标纪元仍是 `E0` ⇒ 与当前纪元不符 ⇒ **`stale_epoch` 终局拒绝、
    /// 零发送**；若实现退化为「提交时重读当前纪元」，节点会以新纪元通过校验并**真的发送** ⇒ 本用例变红。
    /// 故本格把格 A 的自陈边界（「不能区分继承/重读」）在本批内**升级为可判别证据**。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_ScopeIsInherited_NotRereadFromCurrentEpoch()
    {
        var root = NewRoot("tcscopea2-");
        try
        {
            const string changedEpoch = "4321:638999999999999998";   // 与 RoutingFakePort.Epoch 不同
            TaskCenterAdmissionSeams? seamsRef = null;
            var mutated = false;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                beforeSuccessorAdmission: () =>
                {
                    // 只改一次：此刻 E1 已完成登记（固定 Scope 携带 E0），节点尚未取门面锁。
                    if (!mutated) { mutated = true; seamsRef!.Epoch = changedEpoch; }
                    return Task.CompletedTask;
                },
                configureSeams: s => seamsRef = s);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.False(probe.State == WorkflowRunState.Succeeded,
                Diag("纪元变化后不得收口成功（必须按 stale_epoch 拒绝，而非按当前纪元重建后发送）", probe));
            Assert.Equal(0, probe.SendCount);   // 零发送：未用「当前纪元」重建并发出

            var flowOps = probe.Ops.Where(o => string.IsNullOrEmpty(o.Candidate?.NodeId)
                                               && o.Intent == "start" && o.RunBinding == probe.RunId).ToList();
            Assert.True(flowOps.Count == 1, Diag("流程级 start 登记必须唯一命中", probe));
            var flowScope = flowOps[0].Candidate?.Scope ?? "";
            Assert.DoesNotContain(changedEpoch, flowScope);   // 登记值仍属 E0

            // 任何节点侧登记都不得携带「当前纪元」重建出来的 Scope/目标纪元
            var nodeOps = probe.Ops.Where(o => !string.IsNullOrEmpty(o.Candidate?.NodeId)).ToList();
            Assert.DoesNotContain(nodeOps, o => (o.Candidate!.Scope ?? "").Contains(changedEpoch, StringComparison.Ordinal)
                                                || o.TargetEpoch.Contains(changedEpoch, StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 交错④·受理接管故障（[新增·2026-09-21 批次二十二]）**：远端**已 Accepted**、但
    /// **接管事实（`Intent=Accepted`＋`JobId`＋本轮发送身份）落盘失败** ⇒ 必须：不得报成功、
    /// **不得反解为确定未受理**、**不得重发**、且**不得在盘上留下受理事实**（旧 Runner 对象也不能覆盖接管事实）。
    /// 注入＝`RunStore.PublishFaultForTest` 条件化在**接管写**（记录已带 `Intent=Accepted`）上。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_TakeoverPersistFailed_UnknownNoResendNoAcceptedFact()
    {
        var root = NewRoot("tctakeover-");
        try
        {
            var injected = 0;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                configureRuns: r => r.PublishFaultForTest = rec =>
                {
                    // **接管写专属形态**（[会诊加固] 仅 `Intent=Accepted` 不足以定位接管写：结果写回/终态写回
                    // 也可能带 Accepted）——要求同时满足：Accepted 意图 ＋ `JobId` 非空 ＋ 本轮受理发送身份非空
                    // ＋ 尚无终态观察 ＋ 尚无节点结果写回（接管发生在这两者之前）。
                    if (rec.CurrentSubmission is not { } s
                        || s.Intent != SubmitIntentState.Accepted
                        || string.IsNullOrEmpty(s.JobId)
                        || string.IsNullOrEmpty(s.AcceptedSendIdentity)
                        || s.ObservedTerminal is not null
                        || (rec.NodeOutcomes?.Count ?? 0) != 0)
                        return null;
                    Interlocked.Increment(ref injected);
                    return new IOException("fixture: takeover persist fault");
                });

            Assert.Equal(1, Volatile.Read(ref injected));      // 注入确实命中接管写
            Assert.True(probe.Converged, Diag("运行必须收敛（不得悬挂）", probe));
            Assert.True(probe.State == WorkflowRunState.Unknown,
                Diag("远端已受理而接管落盘失败 ⇒ 不可考，必须保守停驻 Unknown", probe));
            Assert.Equal("unknown", probe.FirstNodeResult);
            Assert.Equal(1, probe.SendCount);                  // 只发送一次（不得重发）

            var handoff = ReadLeaseFileWithRetry(root)?.Handoff;
            Assert.NotNull(handoff);
            var op = handoff!.Operations.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.Equal(OperationRequestState.Reconciling, op.RequestState);
            Assert.Equal(1, op.LastSendSeq);
            Assert.False(string.IsNullOrEmpty(op.SubmissionIdentity));
            Assert.NotNull(handoff.Submission);
            Assert.Equal(op.SubmissionIdentity, handoff.Submission!.SubmissionIdentity);
            Assert.Equal(SubmissionState.Reconciling, handoff.Submission.State);

            // **盘上不得留下受理事实**（旧 Runner 对象也不得把接管事实写成既成事实）：冻结写已落盘（Submitted）、
            // 但不得出现 JobId／Accepted 意图／本轮受理发送身份。
            var persisted = new RunStore(Path.Combine(root, "runs")).List().Single();
            Assert.Equal(SubmitIntentState.Submitted, persisted.CurrentSubmission!.Intent);
            Assert.Null(persisted.CurrentSubmission.JobId);
            Assert.Null(persisted.CurrentSubmission.AcceptedSendIdentity);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 交错④·接管故障后【宿主对象重建】**（[新增·2026-09-21 批次二十二]；[会诊收窄命名] 本夹具为
    /// **同进程内新建宿主与端口**，**不是**子进程级重启——静态/进程级缓存残留**未排除**，文档与命名一律按
    /// 「host recreation」口径表述）：同一接管落盘故障收敛 `Unknown` 后新建宿主，断言：
    /// ①**责任与完整发送身份跨宿主重建保留**（前后未决 `Submission` 均非空且 `Reconciling`；节点操作
    /// **唯一存在**且 `Reconciling`；`SubmissionIdentity`＋`SendSeq`＋`WireSubmitKey`＋运行记录
    /// `CurrentSubmission.Key`／`Attempt` **前后全等**⇒未新增发送许可、未换键）；
    /// ②**重建后再次驱动＝确定拒绝且零发送**（新端口 `SendCount == 0`；`Accepted=false`、`Uncertain=false`，
    /// 原因码定位**意图预检**「提交意图缺失或身份不符」）；③盘上仍**无受理事实**、无新 attempt。
    /// **如实边界**：该「零发送」由**意图预检**保证——「未决责任本身阻挡再发送」的**门面级**直接证据仍欠，
    /// 归 B4 恢复项（与 §24.36 的范围限定同口径）。
    /// **取消侧**（接管故障后取消）归取消链批次（P6）：其入口在 E4 控制热键/命令执行器，本文件不冒充。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_TakeoverPersistFailed_HostRecreationKeepsResponsibilityNoResend()
    {
        var root = NewRoot("tctakeover2-");
        var runsDir = Path.Combine(root, "runs");
        var flowsDir = Path.Combine(root, "flows");
        TaskCenterHost? first = null;
        TaskCenterHost? second = null;
        var firstShutDown = false;
        var secondShutDown = false;
        try
        {
            // 第一次运行：真实 1 节点流程 + 接管落盘故障 ⇒ 远端已受理（恰一次发送）、盘上无受理事实、收敛 Unknown
            var injected = 0;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                configureRuns: r => r.PublishFaultForTest = rec =>
                {
                    if (rec.CurrentSubmission?.Intent != SubmitIntentState.Accepted) return null;
                    Interlocked.Increment(ref injected);
                    return new IOException("fixture: takeover persist fault");
                });

            Assert.Equal(1, Volatile.Read(ref injected));
            Assert.Equal(1, probe.SendCount);                          // 远端已受理（恰一次发送，不重发）
            Assert.True(probe.State == WorkflowRunState.Unknown, Diag("接管落盘失败 ⇒ 必须保守停驻 Unknown", probe));
            Assert.Equal("unknown", probe.FirstNodeResult);
            var before = ReadLeaseFileWithRetry(root)?.Handoff;        // probe 结束时其宿主已关闭
            Assert.NotNull(before);
            Assert.NotNull(before!.Submission);
            Assert.Equal(SubmissionState.Reconciling, before.Submission!.State);

            // ——重启：同 root 新建宿主与端口（不复用任何进程内对象）——
            using var client = new BgiExternalClient();
            var portTwo = new RoutingFakePort();
            second = new TaskCenterHost(
                flowsDir, runsDir, Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = RoutingFakePort.Epoch, TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true, successorAdmissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams
                {
                    Epoch = RoutingFakePort.Epoch,
                    ProductionBoundaryFactory = (_, r) => new BgiWorkflowExecutionBoundary(portTwo, r),
                });

            var persistedRun = new RunStore(runsDir).List().Single();
            var keyBefore = persistedRun.CurrentSubmission!.Key;
            var resultTwo = await second.SubmitSuccessorViaAdmissionAsync(
                new WorkflowSubmitRequest(persistedRun, new WorkflowNodeOccurrence("n-1", 0, 0, 0),
                    new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig" }, true), default);
            Assert.False(resultTwo.Accepted);
            Assert.False(resultTwo.Uncertain);    // 确定拒绝（不得报「待对账」）
            Assert.Contains("提交意图缺失或身份不符", resultTwo.RejectReason);   // 定位到**意图预检**（见本夹具边界）
            Assert.Equal(0, portTwo.SendCount);   // **重建后再次驱动：零发送**

            // 责任与**完整发送身份**跨宿主重建未变（无新键、无新 attempt、无新增发送许可）
            var after = ReadLeaseFileWithRetry(root)?.Handoff;
            Assert.NotNull(before);
            Assert.NotNull(after);
            var opBefore = before!.Operations!.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            var opAfter = after!.Operations!.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.NotNull(before.Submission);
            Assert.NotNull(after.Submission);
            Assert.Equal(SubmissionState.Reconciling, before.Submission!.State);
            Assert.Equal(SubmissionState.Reconciling, after.Submission!.State);
            Assert.Equal(opBefore.SubmissionIdentity, before.Submission.SubmissionIdentity);   // 操作↔未决责任身份互证
            Assert.Equal(opAfter.SubmissionIdentity, after.Submission.SubmissionIdentity);
            Assert.Equal(opBefore.SubmissionIdentity, opAfter.SubmissionIdentity);             // 身份未变
            Assert.Equal(before.Submission.SendSeq, after.Submission.SendSeq);                 // 发送序号未变
            Assert.Equal(opBefore.LastSendSeq, opAfter.LastSendSeq);                           // 许可序号未变（无新增许可）
            Assert.Equal(opBefore.WireSubmitKey, opAfter.WireSubmitKey);                       // **未换键**
            Assert.Equal(OperationRequestState.Reconciling, opAfter.RequestState);             // 仍非终局
            Assert.Equal(1, opAfter.LastSendSeq);
            var persistedAfter = new RunStore(runsDir).List().Single();
            Assert.Equal(keyBefore, persistedAfter.CurrentSubmission!.Key);                    // 提交键未变
            Assert.Null(persistedAfter.CurrentSubmission!.JobId);                 // 盘上仍无受理事实
            Assert.Null(persistedAfter.CurrentSubmission.AcceptedSendIdentity);
            Assert.Equal(1, persistedAfter.CurrentSubmission.Attempt);            // 未新开 attempt
        }
        finally
        {
            if (!firstShutDown && first is not null) { try { await first.ShutdownAsync(); } catch { } }
            if (!secondShutDown && second is not null) { try { await second.ShutdownAsync(); } catch { } }
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§17 P6（[2026-09-21 批次二十三]）：发送段必须观察到调用方取消令牌**——此前宿主恒传
    /// `CancellationToken.None`，发送窗口内的取消无法中止在飞发送（亦无法触发「命中即取消远端」的对账清理）。
    /// 取证：端口在真实节点发送时观察到**可取消**的令牌（`CanBeCanceled == true`）；
    /// 若透传缺失（None）则 `CanBeCanceled == false` ⇒ 本用例变红。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_SendSegmentSeesCallerToken_NotNone()
    {
        var root = NewRoot("tctoken-");
        try
        {
            RoutingFakePort? captured = null;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                configurePort: p => captured = p);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("1 节点流程应收口成功", probe));
            Assert.NotNull(captured);
            Assert.True(captured!.LastSendToken.CanBeCanceled,
                "发送段必须观察到**调用方令牌**（可取消）；若观察到 `CancellationToken.None` 即 §17 P6 透传缺失");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§17 P49／§16 交错③「准备阶段 `RunStore` 更新（发布）失败」（[新增·2026-09-21 批次二十一]）**：
    /// 经**仅测试**接缝 `RunStore.PublishFaultForTest`（生产恒 `null`）在**原子发布步骤**注入 `IOException`——
    /// 注入条件＝该记录已带**准备段冻结写**标记（`CurrentSubmission.SendAttempted == true`），故发生在
    /// 「占位后、合并回调已修改内存记录、真实发布失败」的窗口（§12.3 M3③ 所指场景），而非「准备段调用之前」。
    /// 阶段边界＝**已占位、尚未调用发送**（§12.3 M3①），故必须：**零发送**、不得假报成功、
    /// 责任**保留**（未决 `Submission` 在册且状态 `Reconciling`、节点操作非终局 `Reconciling`、
    /// `LastSendSeq==1`＝确系已占位、发送身份与提交键仍属原笔），
    /// 且**不得**把「发布失败」反解为「确定未受理」（§12.3 M3③：那会诱发换键重跑）。
    /// **范围限定**：本夹具覆盖**活进程内的一次驱动**；「重启/恢复后仍不换键」不在本节范围（归 B4 恢复项）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_PrepareStageWriteFault_NoSendResponsibilityRetained()
    {
        var root = NewRoot("tcprepwf-");
        try
        {
            var injected = 0;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                configureRuns: r => r.PublishFaultForTest = rec =>
                {
                    // 只对「准备段冻结写」注入（intent 已由 Runner 落盘、冻结字段已写、发布尚未完成）：
                    // 其余写入（意图落盘、结果写回等）不受影响 ⇒ 故障确落在准备段发布窗口。
                    if (rec.CurrentSubmission?.SendAttempted != true) return null;
                    Interlocked.Increment(ref injected);
                    return new IOException("fixture: prepare-stage run-store publish fault");
                });

            Assert.Equal(1, Volatile.Read(ref injected));   // 注入确实命中准备段（且只命中一次）
            Assert.True(probe.Converged, Diag("运行必须收敛（不得悬挂）", probe));
            Assert.True(probe.State != WorkflowRunState.Succeeded, Diag("准备段写失败不得假报成功", probe));
            Assert.Equal(0, probe.SendCount);               // **零发送**（尚未进入可能发送阶段）
            Assert.Equal("unknown", probe.FirstNodeResult); // 不得反解为确定未受理/拒绝
            Assert.True(probe.State == WorkflowRunState.Unknown,
                Diag("发布失败＝不可考 ⇒ 必须保守停驻 Unknown（不得 Failed/Cancelled 等终态）", probe));

            var handoff = ReadLeaseFileWithRetry(root)?.Handoff;
            Assert.NotNull(handoff);
            var op = handoff!.Operations.Single(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
            Assert.Equal(1, op.LastSendSeq);                                    // 已发布发送许可＝确系「已占位」
            Assert.Equal(OperationRequestState.Reconciling, op.RequestState);   // 责任载体非终局（不得记成 TerminalRejected）
            Assert.False(string.IsNullOrEmpty(op.SubmissionIdentity));
            Assert.NotNull(handoff.Submission);                                 // **责任保留**（未决发送责任在册）
            Assert.Equal(op.SubmissionIdentity, handoff.Submission!.SubmissionIdentity);
            Assert.Equal(SubmissionState.Reconciling, handoff.Submission.State);
            // 发送身份仍绑定**原笔提交**：租约操作携带的线上提交键＝运行记录中本笔提交的键（未换键、未新 attempt）
            var persistedRun = new RunStore(Path.Combine(root, "runs")).List().Single();
            Assert.False(string.IsNullOrEmpty(op.WireSubmitKey));
            Assert.Equal(persistedRun.CurrentSubmission!.Key, op.WireSubmitKey);
            Assert.Equal(1, persistedRun.CurrentSubmission.Attempt);
            Assert.Equal("n-1", op.Candidate!.NodeId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§16 交错⑥·格 B＝启动移交／续行／恢复类运行（无已登记固定来源）**：`TryGetAdmissionScope` 查不到
    /// 「同 `RunBinding` ＋ `Intent=start` ＋无节点身份 ＋ Scope 非空」的登记操作 ⇒ 后继节点提交按
    /// **AMD-1-5 第三条「缺固定 Scope/绑定＝不签发、不发送」响亮拒绝**（**不得**退回直通发送、不得临时读
    /// 当前 epoch 补造）——断言：确定拒绝（非「待对账」）＋**端口 `SendCount==0`** ＋ 该运行的**零占位**
    /// （租约中不存在 `RunBinding=runId` 的操作）＋ 运行记录未被推进（意图仍为 `IntentRecorded`）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_NoRegisteredScopeSource_RejectedNoSendNoOccupy()
    {
        var root = NewRoot("tcscopeb-");
        var runsDir = Path.Combine(root, "runs");
        TaskCenterHost? host = null;
        var hostShutDown = false;
        try
        {
            using var client = new BgiExternalClient();
            var port = new RoutingFakePort();
            var runs = new RunStore(runsDir);
            host = new TaskCenterHost(
                Path.Combine(root, "flows"), runsDir, Path.Combine(root, "catalog.json"),
                () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                localExecutionCapability: () => true,
                statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = RoutingFakePort.Epoch, TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                admissionWired: true, successorAdmissionWired: true,
                admissionSeams: new TaskCenterAdmissionSeams
                {
                    Epoch = RoutingFakePort.Epoch,
                    ProductionBoundaryFactory = (_, r) => new BgiWorkflowExecutionBoundary(port, r),
                });

            var run = runs.CreateRun("wf-x", "r-1");
            run.CurrentSubmission = new WorkflowSubmission
            {
                Key = RunStore.DeriveSubmissionKey(run.RunId, "n-1", 0, 0, 1),
                NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1,
                Intent = SubmitIntentState.IntentRecorded,
            };
            run.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
            runs.Update(run);
            var before = runs.List().Single(r => r.RunId == run.RunId);   // 调用前的运行业务快照

            var result = await host.SubmitSuccessorViaAdmissionAsync(
                new WorkflowSubmitRequest(run, new WorkflowNodeOccurrence("n-1", 0, 0, 0),
                    new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig" }, true),
                default);

            Assert.False(result.Accepted, "缺固定来源不得签发（reason=" + result.RejectReason + "）");
            Assert.False(result.Uncertain, "缺固定来源属可证实未发送 ⇒ 确定拒绝，不得报「待对账」");
            Assert.Contains("无已登记仲裁授权", result.RejectReason);
            Assert.Equal(0, port.SendCount);   // 零发送（直接证据）

            // **运行未被推进（逐字段）**：状态/修订/游标/节点结果/提交字段一律不变
            var after = runs.List().Single(r => r.RunId == run.RunId);
            Assert.Equal(before.State, after.State);
            Assert.Equal(before.RecordRevision, after.RecordRevision);
            Assert.Equal(before.Cursor!.NodeId, after.Cursor!.NodeId);
            Assert.Equal(before.Cursor.Occurrence, after.Cursor.Occurrence);
            Assert.Equal(before.Cursor.LoopIteration, after.Cursor.LoopIteration);
            Assert.Equal(before.NodeOutcomes?.Count ?? 0, after.NodeOutcomes?.Count ?? 0);
            Assert.Equal(before.CurrentSubmission!.Key, after.CurrentSubmission!.Key);
            Assert.Equal(before.CurrentSubmission.NodeId, after.CurrentSubmission.NodeId);
            Assert.Equal(SubmitIntentState.IntentRecorded, after.CurrentSubmission.Intent);
            Assert.Null(after.CurrentSubmission.JobId);
            Assert.Null(after.CurrentSubmission.AcceptedSendIdentity);

            // **先可靠关闭宿主**（`EnsureAdmissionFacadeAsync` 已启动租约心跳）：不得在心跳仍在跑时删目录/读快照
            await host.ShutdownAsync();
            hostShutDown = true;

            // **零占位（直接证据）**：先证明「门面确实已初始化」（租约文件在册——说明本条**真的走到了缺来源分支**、
            // 而不是在更早的预检就被拒），再断言无开放 Submission ＋ 无任何归属本运行的仲裁操作。
            var leaseFile = ReadLeaseFileWithRetry(root);
            Assert.NotNull(leaseFile);
            Assert.Null(leaseFile!.Handoff?.Submission);                                // 无未决发送责任
            var ops = leaseFile.Handoff?.Operations ?? [];
            Assert.DoesNotContain(ops, o => o.RunBinding == run.RunId
                                            || (o.Candidate?.RunId ?? "") == run.RunId
                                            || (o.Candidate?.NodeId ?? "") == "n-1"
                                            || !string.IsNullOrEmpty(o.SubmissionIdentity));
        }
        finally
        {
            if (!hostShutDown && host is not null) { try { await host.ShutdownAsync(); } catch { } }
            TryDelete(root);
        }
    }

    /// <summary>宿主关闭后读租约文件（非 Handoff 段）：文件锁瞬时争用时有界重试（与既有观测点同口径）。</summary>
    private static LogicalOwnerLeaseFile? ReadLeaseFileWithRetry(string root)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File;
            }
            catch (IOException)
            {
                Thread.Sleep(5);
            }
        }
        return null;
    }

    /// <summary>
    /// 可控执行端口（§12「实施前置发现」：`BgiExternalClient` 是 sealed 具体类且无线协议注入接缝，
    /// 宿主级「后继节点经仲裁面真实提交 + 断言发送次数/路由」验收在不注入端口时不可满足）。
    /// </summary>
    private sealed class RoutingFakePort : IBgiExecutionPort
    {
        public const long EpochTicks = 638999999999999999;
        public const int EpochProcessId = 4321;
        public static string Epoch => EpochProcessId + ":" + EpochTicks;

        private readonly object _sync = new();
        private readonly List<string> _sends = [];

        public int SendCount { get { lock (_sync) return _sends.Count; } }
        public bool IsReady => true;
        public bool HasCapability(string name) => true; // suppress 能力必须为真，否则流程预检闸门先拒
        public BgiEpoch? ServerEpoch { get; } = new() { ProcessId = EpochProcessId, StartTicksUtc = EpochTicks };

        /// <summary>发送入口注入（夹具用于制造「发送期间另有写入者改动运行记录」的交错）。</summary>
        public Action? BeforeSend { get; set; }

        /// <summary>发送入口注入（带 1 起的发送序号；用于「第二节点发送前上一节点操作是否已独立终局」取证）。</summary>
        public Action<int>? OnBeforeSend { get; set; }

        /// <summary>
        /// 发送入口注入（1 起序号 ＋ **本次发送的实际 payload JSON**）：用于把「第 k 次发送」**关联到具体节点**
        /// （§12.3 交错⑤ 要求「按节点发送身份」而非仅按发送序号推断）。
        /// </summary>
        public Action<int, string?>? OnBeforeSendWithPayload { get; set; }

        /// <summary>
        /// 发送阶段故障注入（§12.3 交错③）：为 true 时**在记录本次发送尝试之后**抛 `IOException`
        /// （模拟「已进入可能发送阶段后失败」⇒ 门面必须按**不可考**处置、不得判为未受理、不得换通道重发）。
        /// </summary>
        public bool ThrowOnSend { get; set; }

        /// <summary>
        /// **[P8／§24.62] 可证实未发送证据注入**：抛出**证据载体** `BgiNotSentException`
        /// （本进程在任何字节写入线路之前失败）。与 `ThrowOnSend`（已进入线路后失败）**严格分离**——
        /// 两者在宿主链路上的处置**不得互相代替**（本类的两支对照夹具即为此设置）。
        /// </summary>
        public Exception? SendThrows { get; set; }

        /// <summary>最近一次发送观察到的取消令牌（§17 P6 透传取证；应为**调用方令牌**而非 `CancellationToken.None`）。</summary>
        public CancellationToken LastSendToken { get; private set; } = new(canceled: false);

        /// <summary>为 true 时在发送入口 `ct.ThrowIfCancellationRequested()`（模拟「发送窗口内取消」）。</summary>
        public bool HonorCancelOnSend { get; set; }

        /// <summary>为 true 时发送**阻塞在取消令牌上**（模拟「在飞发送」；取消令牌 ⇒ 抛 OCE）。</summary>
        public bool BlockUntilCanceled { get; set; }

        /// <summary>
        /// [static important candidate · real Sender counterexample] typed pre-job server rejection injection: when true, after recording this send attempt, return Success=false with a typed rejection response carrying executionDisposition (verifiable by BgiServerRejectionEvidence.Verify: echoes the full request payload, same connection epoch, accepted=false, server_rejected_before_acceptance) — the self-discharge fact a real arbitration Sender may produce (not accepted, rejected before job creation, never_started). Strictly separated from ThrowOnSend (uncertain): this branch is an evidenced deterministic rejection, not "possibly sent".
        /// </summary>
        public bool TypedServerRejection { get; set; }

        /// <summary>已进入在飞发送的信号（§17 P6 宿主级取消夹具用）。</summary>
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>在飞发送**因该令牌被取消而退出**的信号（因果证据；[会诊加固] 不得用「宿主关闭返回」代替）。</summary>
        public TaskCompletionSource SendCanceledByToken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>**仅清理用**逃生放行（夹具 finally 中放行，避免永久悬挂；不得被当作取消成功的证据）。</summary>
        public TaskCompletionSource ReleaseBlockedSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Dictionary<string, System.Text.Json.JsonElement> _acceptedPayloads = new();

        public async Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            if (operation == WorkflowStopAuthority.Operation)
                return new BgiExternalResponse { Success = true, Data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    bgiEpoch = new { processId = EpochProcessId, startTicksUtc = EpochTicks }, stopVersion = 0,
                    lastManualStopTimestamp = (long?)null, monotonicFrequency = System.Diagnostics.Stopwatch.Frequency,
                }) };
            LastSendToken = ct;   // §17 P6 取证：发送段实际观察到的令牌（应为调用方令牌，而非 None）
            if (HonorCancelOnSend) ct.ThrowIfCancellationRequested();   // 模拟「发送窗口取消」
            lock (_sync) _sends.Add(operation);
            BeforeSend?.Invoke();
            OnBeforeSend?.Invoke(SendCount);
            OnBeforeSendWithPayload?.Invoke(SendCount,
                payload is null ? null : System.Text.Json.JsonSerializer.Serialize(payload));
            SendStarted.TrySetResult();
            if (BlockUntilCanceled)
            {
                try
                {
                    // 在飞发送：等待取消（或夹具的**清理用**逃生放行）
                    var canceled = Task.Delay(Timeout.Infinite, ct);
                    await Task.WhenAny(canceled, ReleaseBlockedSend.Task).ConfigureAwait(false);
                    if (ReleaseBlockedSend.Task.IsCompleted && !ct.IsCancellationRequested)
                        return new BgiExternalResponse { Success = true, Data = "{\"status\":\"accepted\",\"taskHandle\":\"job-escape\"}" };
                    await canceled.ConfigureAwait(false);   // 取消路径：抛 OCE
                    throw new InvalidOperationException("fixture: 不可达");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    SendCanceledByToken.TrySetResult();   // **因果证据**：在飞发送确因该令牌被取消而退出
                    throw;
                }
            }
            if (ThrowOnSend) throw new IOException("fixture: 发送阶段故障（已进入可能发送阶段）");
            if (SendThrows is not null) throw SendThrows;   // [P8] 可证实未发送的证据注入（见属性注释）
            if (TypedServerRejection)
                return new BgiExternalResponse
                {
                    Success = false,
                    ErrorCode = "server_rejected_before_acceptance",
                    Data = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        executionDisposition = "server_rejected_before_acceptance",
                        accepted = false,
                        operation,
                        bgiEpoch = new { processId = EpochProcessId, startTicksUtc = EpochTicks },
                        request = payload,
                    }),
                };
            string jobId;
            lock (_sync)
            {
                jobId = "job-node-" + _sends.Count;
                _acceptedPayloads[jobId] = System.Text.Json.JsonSerializer.SerializeToElement(payload);
            }
            return new BgiExternalResponse
            {
                Success = true,
                Data = System.Text.Json.JsonSerializer.Serialize(new { status = "accepted", taskHandle = jobId }),
            };
        }

        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
            => Task.FromResult<BgiJobListSnapshot?>(null);

        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            lock (_sync)
            {
                if (!_acceptedPayloads.TryGetValue(jobId, out var payload))
                    return Task.FromResult<(string?, BgiJobInfo?)>(("not_found", null));
                var epoch = payload.GetProperty("bgiEpoch");
                return Task.FromResult<(string?, BgiJobInfo?)>(("succeeded", new BgiJobInfo
                {
                    JobId = jobId, State = "succeeded", ExecutionExitConfirmed = true,
                    ExecutionExitDisposition = "execution_exited",
                    Epoch = new BgiEpoch { ProcessId = epoch.GetProperty("processId").GetInt32(), StartTicksUtc = epoch.GetProperty("startTicksUtc").GetInt64() },
                    IdempotencyKey = payload.GetProperty("idempotencyKey").GetString(),
                    WorkflowRunId = payload.GetProperty("workflowRunId").GetString(),
                    NodeId = payload.GetProperty("nodeId").GetString(),
                    Iteration = payload.GetProperty("iteration").GetInt32(),
                    Occurrence = payload.GetProperty("occurrence").GetInt32(), Attempt = payload.GetProperty("attempt").GetInt32(),
                }));
            }
        }

        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>「已受理→台账→关闭」观测点事实（绑定节点身份，并记录此刻对应 Submission 是否仍在册）。</summary>
    private sealed record LedgerPointObservation(string NodeId, SubmitIntentState Intent, string? JobId,
        string? AcceptedSendIdentity, bool SubmissionOpen);

    /// <summary>注入点记录：故障注入时**已落盘**的受理发送身份（证明「Sender 已 Accepted 且接管已落盘」先于异常）。</summary>
    private sealed record FaultInjectionObservation(string AcceptedSendIdentity);

    private sealed record RoutingProbe(
        IReadOnlyList<OperationRecord> Ops, bool ReadOk, bool Converged, string RunId, WorkflowRunState? State,
        string Note, string FirstNodeResult, int SendCount, IReadOnlyList<string> Logs,
        IReadOnlyList<LedgerPointObservation> LedgerPointReceipts,
        IReadOnlyList<FaultInjectionObservation> FaultInjections,
        SubmitIntentState? PersistedIntent, string? PersistedJobId, string? PersistedSendIdentity,
        string? OpenSubmissionIdentity)
    {
        public bool IsSettled => State is WorkflowRunState.Succeeded or WorkflowRunState.Failed
            or WorkflowRunState.Cancelled or WorkflowRunState.Interrupted or WorkflowRunState.Unknown;
    }

    private static string Diag(string what, RoutingProbe p)
        => what + "：state=" + (p.State?.ToString() ?? "<none>") + " readOk=" + p.ReadOk + " converged=" + p.Converged + " sends=" + p.SendCount
           + " note=" + p.Note
           + " ops=[" + string.Join("; ", p.Ops.Select(o => o.Candidate?.Namespace + "/" + o.Candidate?.NodeId
               + "/" + o.RequestState + "/" + (o.LastResult?.ReasonCode ?? "") + "/" + (o.LastResult?.EvidenceSource ?? ""))) + "]"
           + " logs=[" + string.Join(" || ", p.Logs) + "]";

    /// <summary>从节点提交 payload 中取 `configName`（把「第 k 次发送」关联到具体节点身份；§12.3 交错⑤）。</summary>
    private static string? ParseConfigName(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("configName", out var el)
                   && el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// 驱动「面板启动 → 节点提交 → 终态观察」全链并回收可断言事实。门关分支节点提交不经门面（无 successor 操作）。
    /// `concurrentWriteBeforeSend`＝在发送入口注入「另有写入者改动运行记录」的交错（仅④使用）。
    /// </summary>
    private static async Task<RoutingProbe> ProbeNodeSubmitRoutingAsync(
        string root, bool successorWired, Action<RunStore>? concurrentWriteBeforeSend = null,
        Action<RunStore, int>? onBeforeSend = null, string[]? nodeIds = null,
        Action? afterLedgerBeforeClose = null,
        // [§17 P17／§12.3 交错①] 强制版交错用（默认 null＝不影响既有夹具）：
        //   `holdFirstAccept`＝首轮（E1）「Accepted 后、台账前」阻塞点；`beforeSuccessorAdmission`＝节点准入入口、
        //   取得门面锁**之前**的只发信号观察点（读取/取证用）。
        TaskCompletionSource? holdFirstAccept = null,
        Func<Task>? beforeSuccessorAdmission = null,
        // [§12.3 交错⑤] 发送入口注入（1 起序号 ＋ payload JSON）：把「第 k 次发送」关联到具体节点身份。
        Action<RunStore, int, string?>? onBeforeSendWithPayload = null,
        // [§12.3 交错③] 端口配置钩子（例如注入发送阶段故障 `ThrowOnSend`）。
        Action<RoutingFakePort>? configurePort = null,
        // [§16 交错⑥ 格 A′] 暴露接缝实例（默认 null＝不影响既有夹具）：用于在「E1 已登记固定 Scope、
        // 节点尚未准入」之间改变当前纪元，从而区分「继承登记值」与「重读当前值」。
        Action<TaskCenterAdmissionSeams>? configureSeams = null,
        // [§17 P49／§16 交错③] 存储侧注入钩子（默认 null＝不影响既有夹具）：例如注入「准备段发布失败」。
        Action<RunStore>? configureRuns = null,
        // [G4a] true＝**经启动移交**受理同一流程（来源权威＝运行记录 `AdmissionSourceScope`，无面板 E1 来源操作）。
        bool startViaHandoff = false,
        // [G4②·本批 W2] 占位发布后、锁外发送前的屏障注入（夹具制造「入队后游标被并发推进」交错）。
        // 参数为宿主实际使用的 RunStore 实例（与 concurrentWriteBeforeSend 同口径）。
        Action<RunStore>? afterOccupyBeforeSend = null)
    {
        using var client = new BgiExternalClient();
        var flowsDir = Path.Combine(root, "flows");
        var runsDir = Path.Combine(root, "runs");
        var ws = new WorkflowStore(flowsDir);
        var doc = new WorkflowDocument
        {
            Name = "路由验收流程",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes =
            [
                .. (nodeIds ?? ["n-1"]).Select(id => new WorkflowNode
                {
                    NodeId = id,
                    Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置" + id, Revision = "rev-1" },
                }),
            ],
        };
        ws.Save(doc, null);

        var arbitrationDir = Path.Combine(root, "arbitration");
        var logGate = new object();
        var logList = new List<string>();
        var ledgerPoint = new List<LedgerPointObservation>();
        var faultInjections = new List<FaultInjectionObservation>();
        var ledgerCloseCount = 0; // 1＝E1 流程启动轮次（夹具不注入故障）；≥2＝节点提交轮次（注入点）
        var occupyBeforeSendCount = 0; // [G4②·W2] 同上：1＝E1 轮次不注入；≥2＝节点提交轮次注入
        var acceptCount = 0;      // 1＝E1 轮次的「Accepted 后、台账前」观测（交错① 的强制阻塞点）
        var port = new RoutingFakePort();
        var runs = new RunStore(runsDir);
        // [§16 交错⑥ 格 A′] 接缝实例先建后传给 host：夹具可在「E1 已登记、节点未准入」之间改当前纪元。
        var seams = new TaskCenterAdmissionSeams
        {
            Epoch = RoutingFakePort.Epoch,
            // [§17 P17／§12.3 交错①] 节点准入入口、取得门面锁**之前**的只发信号观察点（生产 null）。
            BeforeSuccessorAdmission = beforeSuccessorAdmission is null
                ? null
                : beforeSuccessorAdmission,
            // 门面在「Sender 返回 Accepted 之后、持久化接管台账/关闭 Submission 之前」回调——
            // 用它取证「先接管、后关闭」的先后顺序（M2/§12.2 B2）：此刻须已能读到本轮 jobId，
            // 且对应 Submission **仍在册**（尚未关闭）。观测点位于门面同步流水线，不依赖抢时序。
            Barriers = new AdmissionBarriers
            {
                AfterAcceptBeforeLedger = async () =>
                {
                    // [§17 P17／§12.3 交错①] 强制版：**首轮（E1）**在此阻塞——直到节点已到达「取得门面锁之前」
                    // 的观察点（`BeforeSuccessorAdmission`）再放行，从而确定性制造「E1 未关闭时首节点抢先」。
                    if (holdFirstAccept is not null && Interlocked.Increment(ref acceptCount) == 1)
                        await holdFirstAccept.Task.ConfigureAwait(false);
                    var record = runs.List().OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
                    if (record?.CurrentSubmission is { } s)
                    {
                        bool? open = null;
                        for (var readAttempt = 0; readAttempt < 20 && open is null; readAttempt++)
                        {
                            try
                            {
                                open = new ArbitrationLeaseStore(arbitrationDir).Read().File?.Handoff?.Submission is not null;
                            }
                            catch (IOException)
                            {
                                Thread.Sleep(5);
                            }
                        }
                        open ??= false;
                        ledgerPoint.Add(new LedgerPointObservation(s.NodeId, s.Intent, s.JobId, s.AcceptedSendIdentity, open.Value));
                    }
                },
                AfterOccupyBeforeSend = afterOccupyBeforeSend is null
                    ? null
                    : () =>
                    {
                        // 只在节点提交轮次注入（首轮 E1 流程启动不注入，与 afterLedgerBeforeClose 同口径）：
                        // E1 也经 AfterOccupyBeforeSend，过早注入会破坏流程启动本身。
                        if (Interlocked.Increment(ref occupyBeforeSendCount) > 1)
                            afterOccupyBeforeSend(runs);
                        return Task.CompletedTask;
                    },
                AfterLedgerBeforeClose = afterLedgerBeforeClose is null
                    ? null
                    : () =>
                    {
                        if (Interlocked.Increment(ref ledgerCloseCount) > 1)
                        {
                            var injected = runs.List().OrderByDescending(r => r.UpdatedAt).FirstOrDefault()
                                ?.CurrentSubmission?.AcceptedSendIdentity ?? "";
                            faultInjections.Add(new FaultInjectionObservation(injected));
                            afterLedgerBeforeClose();
                        }
                        return Task.CompletedTask;
                    },
            },
            ProductionBoundaryFactory = (_, r) =>
            {
                if (concurrentWriteBeforeSend is not null) port.BeforeSend = () => concurrentWriteBeforeSend(r);
                if (onBeforeSend is not null) port.OnBeforeSend = n => onBeforeSend(r, n);
                if (onBeforeSendWithPayload is not null)
                    port.OnBeforeSendWithPayload = (n, payloadJson) => onBeforeSendWithPayload(r, n, payloadJson);
                configurePort?.Invoke(port);
                configureRuns?.Invoke(r);   // [§17 P49] 宿主实际使用的 RunStore 实例（非本方法局部 `runs`）
                return new BgiWorkflowExecutionBoundary(port, r);
            },
        };
        configureSeams?.Invoke(seams);
        var host = new TaskCenterHost(
            flowsDir, runsDir, Path.Combine(root, "catalog.json"),
            () => client, log: entry => { lock (logGate) logList.Add(entry); },
            runnerFactory: null, readinessOverride: () => (true, null),
            localExecutionCapability: () => true,
            // 启动移交受理带快照预检（生产语义）⇒ 该分支需可用快照；节点路径在接缝下不消费快照。
            statusSnapshotProvider: startViaHandoff ? (() => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = RoutingFakePort.Epoch, TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false }) : (() => null),
            admissionWired: true,
            admissionSeams: seams,
            successorAdmissionWired: successorWired);
        try
        {
            if (startViaHandoff)
            {
                var reg = await host.RegisterHandoffAsync(new StartupHandoffRequest
                {
                    StopAuthority = new WorkflowStopAuthorityRecord(RoutingFakePort.Epoch, 0, "fixture-source-intent", 1, System.Diagnostics.Stopwatch.Frequency),
                    ExecutionId = Guid.NewGuid().ToString("N"),
                    StepId = "step-g4a",
                    IntentKey = "manual:exec-g4a-" + Guid.NewGuid().ToString("N")[..8],
                    WorkflowId = doc.WorkflowId!,
                    Mode = StartupHandoffModes.Start,
                });
                Assert.True(reg.Outcome == HandoffOutcome.Accepted,
                    $"启动移交应受理；实际={reg.Outcome}，reasonCode={reg.ReasonCode}，reason={reg.Reason}");
            }
            else
            {
                var start = await host.StartWorkflowAsync(doc.WorkflowId!);
                Assert.Equal(HostActionStatus.Registered, start.Status);
            }

            // 有界等待**路由收敛**：门开时须同时看到「successor 节点操作已发布」与「运行已离开活动态」——
            // 只在占位瞬间（Granted）就读会误判发送次数（并行负载下夹具实证）。门关时节点操作永不出现，
            // 只需等运行收敛。
            // 台账读取必须**有效**（文件/Handoff 段在册且已含本次运行的 E1 启动操作）——
            // 否则「不存在节点操作」可能只是没读到，`DoesNotContain` 会空过（会诊重要项）。
            IReadOnlyList<OperationRecord> snapshot = [];
            var readOk = false;
            string runId = "";
            for (var i = 0; i < 3000; i++) // 30s 有界预算（并行负载 + 33 节点流程实测需要）
            {
                runId = runs.List().OrderByDescending(r => r.UpdatedAt).FirstOrDefault()?.RunId ?? runId;
                readOk = TryReadValidOps(root, runId, out var current, requirePanelStartOp: !startViaHandoff);
                if (readOk) snapshot = current;

                var settled = runs.List().Any(r => r.State is WorkflowRunState.Succeeded or WorkflowRunState.Failed
                    or WorkflowRunState.Cancelled or WorkflowRunState.Interrupted or WorkflowRunState.Unknown);
                var nodeOpVisible = snapshot.Any(o => o.Candidate?.NodeId == "n-1");
                if (readOk && settled && (nodeOpVisible || !successorWired)) break;
                await Task.Delay(10);
            }

            // **收敛断言（会诊重要项·最小修正）**：收尾读取之前必须先确认目标运行已收敛；否则等待预算耗尽时
            // 「运行仍活动 → 收尾读到仅含 E1 → 节点操作随后登记」的交错会以假通过收场（门关分支尤甚）。
            var converged = false;
            for (var i = 0; i < 800; i++)
            {
                if (runs.List().Any(r => r.RunId == runId && (r.State is WorkflowRunState.Succeeded or WorkflowRunState.Failed
                        or WorkflowRunState.Cancelled or WorkflowRunState.Interrupted or WorkflowRunState.Unknown)))
                {
                    converged = true;
                    break;
                }
                await Task.Delay(10);
            }

            // **无条件**收尾复核（会诊重要项）：最终 `ReadOk`/`Ops` 必须来自运行收敛后的**这一次**新读取——
            // 若受旧 `readOk` 控制而跳过复核，「读到只含 E1 的旧快照 → 随后节点操作才登记」的交错会漏检。
            runId = runs.List().OrderByDescending(r => r.UpdatedAt).FirstOrDefault()?.RunId ?? runId;
            var finalOk = false;
            IReadOnlyList<OperationRecord> finalOps = [];
            for (var i = 0; i < 200; i++)
            {
                if (TryReadValidOps(root, runId, out var opsNow, requirePanelStartOp: !startViaHandoff))
                {
                    finalOk = true;
                    finalOps = opsNow;
                    break;
                }
                await Task.Delay(10);
            }
            snapshot = finalOps;
            readOk = finalOk;

            var run = runs.List().FirstOrDefault(r => r.RunId == runId);
            IReadOnlyList<string> logs;
            lock (logGate) logs = logList.ToList();
            string? openSubmissionIdentity = null;
            try
            {
                openSubmissionIdentity = new ArbitrationLeaseStore(arbitrationDir)
                    .Read().File?.Handoff?.Submission?.SubmissionIdentity;
            }
            catch (IOException)
            {
                // 瞬时争用：保持 null（断言会给出诊断）
            }
            var firstNodeResult = (nodeIds ?? ["n-1"])[0];
            return new RoutingProbe(snapshot, readOk, converged, run?.RunId ?? "", run?.State, run?.Note ?? "",
                run?.NodeOutcomes?.LastOrDefault(o => o.NodeId == firstNodeResult)?.Result ?? "",
                port.SendCount, logs, ledgerPoint.ToList(), faultInjections.ToList(),
                run?.CurrentSubmission?.Intent, run?.CurrentSubmission?.JobId,
                run?.CurrentSubmission?.AcceptedSendIdentity, openSubmissionIdentity);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NodeSubmit_GoesThroughAdmissionFace_WhenPathGateOpen()
    {
        var root = NewRoot("tcroute-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过（否则下面的空集合断言无意义）", probe));
Assert.True(probe.Converged, Diag("运行必须收敛后才允许读取最终台账（防等待耗尽假通过）", probe));
            Assert.True(probe.IsSettled, Diag("运行必须收敛（不能停在活动态）", probe));
            var matches = probe.Ops.Where(o => o.Candidate?.NodeId == "n-1").ToList();
            Assert.True(matches.Count == 1, Diag("路由未成立（应恰有一个该节点操作）", probe));
            var nodeOp = matches[0];
            Assert.Equal("successor", nodeOp.Candidate!.Namespace);                 // §2.2 编码器 namespace 段
            Assert.Equal("start", nodeOp.Intent);
            Assert.Equal(probe.RunId, nodeOp.RunBinding);                           // runBinding 段继承自启动操作
            Assert.False(string.IsNullOrEmpty(nodeOp.SubmissionIdentity));          // 完整发送身份已签发（§13.10 A2）
            Assert.Equal("n-1#0#0", nodeOp.CursorRef);                              // G4：游标引用非空且与提交出现一致
            Assert.NotNull(nodeOp.CursorRevision);                                  // G4：所依据的运行记录修订已冻结
            Assert.False(string.IsNullOrEmpty(nodeOp.Candidate!.PayloadFingerprint)); // G5：准入阶段内容指纹已落盘
            Assert.True(probe.SendCount == 1, Diag("恰好一次发送（经门面，非直通）", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("端到端跑通（含终态观察）", probe));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task NodeSubmit_DoesNotReachAdmissionFace_WhenPathGateClosed()
    {
        var root = NewRoot("tcroute-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: false);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过（否则下面的空集合断言无意义）", probe));
Assert.True(probe.Converged, Diag("运行必须收敛后才允许读取最终台账（防等待耗尽假通过）", probe));
            Assert.True(probe.IsSettled, Diag("运行必须收敛（门关＝R4 直通，仍应跑完）", probe));
            Assert.DoesNotContain(probe.Ops, o => o.Candidate?.NodeId == "n-1"); // 生产默认：节点提交不经仲裁面
            Assert.True(probe.SendCount == 1, Diag("直通同样只发送一次（R4 行为不变）", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("门关＝R4 直通行为不变（应跑通）", probe));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **并发守卫验收（会诊阻断处置）**：发送期间另有写入者改动运行记录 ⇒ G2 合并必须**拒绝**
    /// （不得凭「业务身份相同」接受任意最新修订后把并发事实静默覆盖）——运行按修订冲突保守收敛 `Unknown`，
    /// 且宿主日志留痕「合并被拒」。本夹具即该守卫的可执行反例。
    /// </summary>
    [Fact]
    public async Task ConcurrentRunWriteDuringSend_MergeRefused_ConvergesUnknown()
    {
        var root = NewRoot("tcrace-");
        try
        {
            var injected = false;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                concurrentWriteBeforeSend: runs =>
                {
                    var record = runs.List().FirstOrDefault();
                    if (record is null) return;
                    record.Note = (record.Note ?? "") + " | 并发写入（夹具注入：发送期间由另一写入者推进）";
                    runs.Update(record);
                    injected = true;
                });

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
Assert.True(probe.Converged, Diag("运行必须收敛后才允许读取最终台账（防等待耗尽假通过）", probe));
            Assert.True(injected, Diag("并发写入必须已注入（否则本反例未成立）", probe));
            Assert.True(probe.IsSettled, Diag("运行必须收敛", probe));
            Assert.True(probe.SendCount == 1, Diag("发送仍恰好一次（不因并发改动而重发）", probe));
            Assert.True(probe.State == WorkflowRunState.Unknown, Diag("并发改动下必须保守 Unknown，不得假报成功", probe));
            Assert.Contains(probe.Logs, l => l.Contains("后继提交合并被拒"));
            Assert.Contains("并发写入（夹具注入", probe.Note); // 并发事实必须保留（不得被静默覆盖）
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **G4 残余②（本批 W2）发送侧冻结游标复核（红夹具）**：入队（占位已发布）后、发送前，
    /// 运行游标被并发推进到下一节点 ⇒ 旧提交**不得**进入准备与发送（§13.11 G4 残余②：
    /// 「发送侧未按冻结游标/修订复核权威值（入队后游标变化、提交键与 attempt 未变时
    /// 旧请求仍可能进入准备与发送）」）。断言：零发送（`SendCount==0`）＋运行收敛（不悬挂）
    /// ＋拒绝原因指向游标变化。复核用游标字段值（非 `RecordRevision`——任何记录更新都推进修订，
    /// 严格相等会误拒无关更新；记录修订已用于 ⑪b 唯一消费键）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_CursorAdvancedAfterOccupy_SendSideRejectsBeforeAnySend()
    {
        var root = NewRoot("tccursor-stale-");
        try
        {
            var injected = false;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                afterOccupyBeforeSend: runs =>
                {
                    // 反例注入：占位已发布、发送未始——并发写入者把游标推进到下一节点
                    // （提交键/attempt 未变，故现有 successor_submission_identity_changed 检查不会拦截）。
                    var record = runs.List().OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
                    if (record is null) return;
                    record.Cursor = new WorkflowNodeCursor { NodeId = "n-2", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
                    runs.Update(record);
                    injected = true;
                });

            Assert.True(injected, "游标推进必须已注入（否则本反例未成立）");
            Assert.True(probe.Converged, Diag("运行必须收敛（不悬挂）", probe));
            Assert.True(probe.IsSettled, Diag("运行必须收敛（不能停在活动态）", probe));
            Assert.True(probe.SendCount == 0, Diag("游标已推进 ⇒ 旧提交必须零发送（发送侧复核拒绝）", probe));
            Assert.True(probe.State != WorkflowRunState.Succeeded,
                Diag("零发送的运行不得报成功（保守收敛）", probe));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **[静态重要候选·实质 Sender 反例] 真实仲裁 Sender 的类型化拒绝清偿事实必须能合并回 Runner 持有的实例**。
    /// 背景：发送段的类型化拒绝路径（`BgiWorkflowExecutionBoundary.SendPreparedAsync` → `UpdateMergingIf`）会把
    /// `serverRejectionEvidence / observedTerminal="rejected" / executionExitConfirmed=true /
    /// executionExitDisposition="never_started" / effectState="rejected"` 作为**自有清偿事实**落盘；
    /// 但 `MergeBackAuthoritativeSubmission` 的旧白名单（`NormalizeVolatile`）与回拷只覆盖传统发送字段，
    /// 会让 G2 合并把 Sender 自己刚写的清偿事实误判为「并发修改」而**拒绝合并**（运行保守收敛 Unknown）。
    /// 本测试即该静态候选的实质红例：真实仲裁 Sender 返回类型化拒绝后，运行**不得**停在 Unknown。
    /// </summary>
    [Fact]
    public async Task TypedServerRejection_SelfDischarge_Merged_NotUnknown()
    {
        var root = NewRoot("tcrej-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                configurePort: port => port.TypedServerRejection = true);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("运行必须收敛（不得悬挂）", probe));
            Assert.True(probe.SendCount == 1, Diag("只允许一次发送尝试（不重发）", probe));

            // 读回权威落盘事实：类型化拒绝清偿事实（原身份/退出/效果）必须由同一 Sender 路径写回。
            var run = new RunStore(Path.Combine(root, "runs")).Load(probe.RunId)!;
            var sub = run.CurrentSubmission!;
            Assert.Equal("rejected", sub.ObservedTerminal);
            Assert.True(sub.ExecutionExitConfirmed);
            Assert.Equal("never_started", sub.ExecutionExitDisposition);
            Assert.Equal("rejected", sub.EffectState);
            Assert.NotNull(sub.ServerRejectionEvidence);
            Assert.Equal(SubmitIntentState.Rejected, sub.Intent);
            Assert.Null(sub.JobId);

            // 静态候选的实质反例：合并被拒会让运行保守收敛 Unknown；此处**必须不是** Unknown。
            Assert.True(probe.State != WorkflowRunState.Unknown,
                Diag("类型化拒绝是自有清偿事实，不得因合并守卫保守收敛 Unknown", probe));
            Assert.DoesNotContain(probe.Logs, l => l.Contains("后继提交合并被拒"));
        }
        finally
        {
            TryDelete(root);
        }
    }
    /// <summary>
    /// **G8 节点操作独立终局出口验收（§12.3）**：两节点流程中，**第二节点发送之前**第一节点 Operation
    /// 必须已经独立终局（`TerminalCompleted`）——即按「该节点权威终态结果」结清，而**不是**等整条 run 终态
    /// （后者会让长流程堆满 32 个主槽位）。观测点＝执行端口第二次发送的入口（门面流水线内，不靠抢时序）。
    /// </summary>
    [Fact]
    public async Task NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission()
    {
        var root = NewRoot("tcsweep-");
        try
        {
            OperationRequestState? firstNodeOpAtSecondSend = null;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                nodeIds: ["n-1", "n-2"],
                onBeforeSend: (_, index) =>
                {
                    if (index != 2) return;
                    try
                    {
                        firstNodeOpAtSecondSend = new ArbitrationLeaseStore(Path.Combine(root, "arbitration"))
                            .Read().File?.Handoff?.Operations?
                            .FirstOrDefault(o => o.Candidate?.NodeId == "n-1")?.RequestState;
                    }
                    catch (IOException)
                    {
                        // 瞬时争用＝本轮观测不到，保持 null（断言会失败并给出诊断）
                    }
                });

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("两节点流程必须收敛", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("两节点流程应跑通", probe));
            Assert.True(firstNodeOpAtSecondSend == OperationRequestState.TerminalCompleted,
                Diag("第一节点 Operation 必须在第二节点发送前独立终局（实际=" + (firstNodeOpAtSecondSend?.ToString() ?? "<null>") + "）", probe));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 交错①（强制版）·首节点抢先**：E1 轮次在「Accepted 后、台账前」被阻塞（**尚未关闭**）时，
    /// 首节点已到达**节点准入入口、取得门面锁之前**的观察点——此刻必须：①**父责任仍在**（E1 的 Submission
    /// 未关闭、其操作未终局）；②**子许可/发送为零**（尚无该节点操作，发送仍只有 E1 那一次）；放行后流程仍正常收口
    /// （既不自拒也不重复占位/循环等待）。
    /// 依据 §17 P17：观察点必须位于**取得门面锁之前**（`AdmissionBarriers` 全在锁内，锁内等待会自死锁）。
    /// </summary>
    [Fact]
    public async Task NodeAdmission_BeforeGateObservation_E1StillOpen_NoChildPermitYet()
    {
        var root = NewRoot("tcpreempt-");
        Task<RoutingProbe>? probeTask = null;
        var holdE1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var nodeAtPreGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parentSubmissionOpen = false;
            var parentUnique = false;
            var parentIdentityMatchesOpenSubmission = false;
            var parentState = OperationRequestState.NotSelected;   // 占位：未命中时断言必须失败（不得默认成非终局态）
            var childOpsAtPreGate = -1;
            var sendsAtPreGate = -1;
            var sendsObserved = 0;
            var leaseRead = false;
            probeTask = ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                nodeIds: ["n-1", "n-2"],
                onBeforeSend: (_, index) => sendsObserved = index,
                holdFirstAccept: holdE1,
                beforeSuccessorAdmission: () =>
                {
                    // **锁外只读快照**（不得改变任何状态）：父责任仍在？子许可/发送为零？
                    // 有界重试：全量并行负载下文件锁瞬时争用会让单次读抛 IOException（与既有观测点同口径）。
                    for (var attempt = 0; attempt < 20 && !leaseRead; attempt++)
                    {
                        try
                        {
                            var read = new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read();
                            var ops = read.File?.Handoff?.Operations ?? [];
                            var openSubmission = read.File?.Handoff?.Submission;
                            parentSubmissionOpen = openSubmission is not null;
                            var parents = ops.Where(o =>
                                string.IsNullOrEmpty(o.Candidate?.NodeId) && o.LastSendSeq > 0).ToList();
                            parentUnique = parents.Count == 1;
                            if (parentUnique)
                            {
                                parentState = parents[0].RequestState;
                                parentIdentityMatchesOpenSubmission = openSubmission is not null
                                    && string.Equals(parents[0].SubmissionIdentity, openSubmission.SubmissionIdentity,
                                        StringComparison.Ordinal);
                            }
                            childOpsAtPreGate = ops.Count(o => !string.IsNullOrEmpty(o.Candidate?.NodeId));
                            leaseRead = true;
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(5);
                        }
                    }
                    sendsAtPreGate = sendsObserved;
                    nodeAtPreGate.TrySetResult();
                    return Task.CompletedTask;
                });

            try
            {
                await nodeAtPreGate.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(leaseRead, "交错①：观察点必须读到一致租约快照（有界重试后仍失败=取证不足）");
                Assert.True(parentUnique, "交错①：父（E1）操作必须唯一命中");
                Assert.True(parentSubmissionOpen, "交错①：节点到达准入入口时 E1 必须**尚未关闭**（父责任仍在）");
                Assert.True(parentIdentityMatchesOpenSubmission,
                    "交错①：当前开放的 Submission 必须属于该父操作（按完整发送身份关联）");
                Assert.Contains(parentState, new[]
                {
                    OperationRequestState.Queued, OperationRequestState.InRound, OperationRequestState.Granted,
                    OperationRequestState.Sending, OperationRequestState.Accepted, OperationRequestState.Reconciling,
                });   // **非终局集合**（排除 TerminalCompleted/TerminalRejected 等终局态）
                Assert.Equal(0, childOpsAtPreGate);   // 子许可为零（尚无该节点操作）
                // `RoutingFakePort` 只统计**节点提交**的 BGI 发送（E1 的流程登记发送走宿主 `host:drive_registered`，
                // 不经该端口）⇒ 此刻「子发送为零」＝端口计数 0。
                Assert.Equal(0, sendsAtPreGate);
            }
            finally
            {
                holdE1.TrySetResult();             // **无条件**放行 E1（断言失败/异常时也不悬挂门面轮次）
            }

            var probe = await probeTask.WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("交错①放行后流程应收口成功", probe));
            Assert.True(probe.SendCount == 2, Diag("应共 2 次节点发送（2 节点）", probe));
        }
        finally
        {
            holdE1.TrySetResult();
            if (probeTask is not null)
            {
                try { await probeTask.WaitAsync(TimeSpan.FromSeconds(60)); } catch { /* 清理：吞掉失败以免遮蔽真实断言 */ }
            }
            TryDelete(root);
        }
    }

    /// <summary>
    /// **G4a 端到端（[批次四十五]）**：**经启动移交**受理的 run（**无面板 E1 来源操作**）——后继节点提交必须
    /// 按**运行台账的来源固定 Scope**（受理时捕获的 `bgi:local:{epoch}`）**继承**：节点操作落盘 Scope 与该登记值
    /// **逐字一致**、并整体跑通（发送恰一次）。这是「移交受理处落定固定来源 ＋ 后继提交可查得」的端到端证据。
    /// **范围**：宿主层测试接线态（`_successorAdmissionWired` 显式打开）；生产门仍关闭。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_AfterHandoffStart_InheritsRecordedFixedScope()
    {
        var root = NewRoot("tcg4a-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: ["n-1"],
                startViaHandoff: true);

            Assert.True(probe.Converged, Diag("运行必须收敛后才允许读取最终台账", probe));
            Assert.True(probe.ReadOk, Diag("台账读取必须有效（本 run 已有节点操作在册）", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("移交启动的流程应整体跑通", probe));
            Assert.Equal(1, probe.SendCount);
            var nodeOp = Assert.Single(probe.Ops, o => o.Candidate?.NodeId == "n-1");
            Assert.Equal("bgi:local:" + RoutingFakePort.Epoch, nodeOp.Candidate!.Scope); // **继承运行记录固定 Scope（逐字）**
            Assert.Equal(probe.RunId, nodeOp.RunBinding);
            Assert.False(string.IsNullOrEmpty(nodeOp.SubmissionIdentity));
            // 移交路径**没有**面板流程登记来源操作（来源权威＝运行记录，不在租约）
            Assert.DoesNotContain(probe.Ops, o => o.OperationType == OperationType.FlowRegistration);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 M1①⑤（宿主/端到端·批次四十四）**：E1（流程登记）**完成接管与关闭之后**，该 run 的
    /// **首节点与后继节点各自取得独立且唯一**的发送许可，且节点操作**持久化父子绑定**
    /// （`parentRequestIdentity` ＝该 run 唯一流程登记父操作的身份、`runBinding` ＝该 run）。
    /// 本夹具同时令**自有驱动在飞占用成立**（E1 受理、驱动登记后 BGI 侧即处于运行态 ⇒ `Occupied=true`；
    /// 该时点由「受理后」屏障固定，屏障是测试注入点、不改动任何生产对象）——占用豁免**只**覆盖
    /// 「本宿主自有驱动（`_drives` 推导）＋同一父授权＋父子绑定已持久化＋父已终局关闭」的合法子提交，
    /// 故流程仍必须整体跑通；若豁免失效，本夹具会以「节点 `execution_occupied`／发送数不足／运行未成功」转红。
    /// **范围**：宿主层（**测试接线态**；生产 `_successorAdmissionWired` 仍关闭）；**不证明**真实进程实机路径、
    /// 也不证明 BGI 快照事实源本身（占用事实在此由接缝模拟）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_AfterE1Closed_OwnDriveOccupied_PerNodePermitAndParentBinding()
    {
        var root = NewRoot("tcm1-");
        try
        {
            var occupied = false;   // 生产时序：E1 登记时无占用（否则 E1 自身即被占用拒绝）→ 驱动起跑后占用成立
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                nodeIds: ["n-1", "n-2"],
                configureSeams: seams =>
                {
                    seams.OccupiedProvider = () => Volatile.Read(ref occupied);
                    // 归属事实：本夹具只有这一个 run（该 run 的驱动在飞）⇒ 归属集恰为该 run。
                    // 生产侧**不填归属**（控制面快照只给布尔「有任务在跑」，无法证明归属；见
                    // `CurrentArbitrationFacts` 的保守口径与 §24.54 的 R5.8 前置）。
                    var runsDir = Path.Combine(root, "runs");
                    seams.OwnInFlightRunBindingsProvider = () => new RunStore(runsDir).List()
                        .Select(r => r.RunId)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => id!)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    seams.Barriers = new AdmissionBarriers
                    {
                        // E1 受理（＝宿主驱动已登记）之后置占用成立：固定「自有驱动在飞」这一生产时点。
                        AfterAcceptBeforeLedger = () =>
                        {
                            Volatile.Write(ref occupied, true);
                            return Task.CompletedTask;
                        },
                    };
                });

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded,
                Diag("自有驱动在飞占用下，E1 关闭后首/后继节点仍必须各自取许可并跑通", probe));
            Assert.Equal(2, probe.SendCount);

            // 父登记（流程登记）：无节点身份、intent=start、唯一命中、且**已终局**（＝已接管关闭）
            var parents = probe.Ops
                .Where(o => string.IsNullOrEmpty(o.Candidate?.NodeId)
                            && string.Equals(o.Intent, "start", StringComparison.Ordinal))
                .ToList();
            Assert.True(parents.Count == 1, Diag("父登记（流程登记）必须唯一命中", probe));
            var parentOp = parents[0];
            // M1①：判据是**父登记已完成接管与关闭**——`Accepted`（接管台账已持久化、Submission 已关闭）
            // 即已满足；`TerminalCompleted` 是更后的账龄出口，不是首节点取许可的前置。
            Assert.Contains(parentOp.RequestState,
                new[] { OperationRequestState.Accepted, OperationRequestState.TerminalCompleted });
            Assert.False(string.IsNullOrEmpty(parentOp.SubmissionIdentity));
            Assert.True(parentOp.LastSendSeq > 0, Diag("父登记必须确经受理与发送才谈得上「已关闭」", probe));

            foreach (var nodeId in new[] { "n-1", "n-2" })
            {
                var nodeOps = probe.Ops.Where(o => o.Candidate?.NodeId == nodeId).ToList();
                Assert.True(nodeOps.Count == 1, Diag("节点 " + nodeId + " 应恰有一个操作", probe));
                var op = nodeOps[0];
                // M1⑤ 父子绑定**已持久化**（指向该 run 的流程登记父操作）
                Assert.Equal(parentOp.RequestIdentity, op.ParentRequestIdentity);
                Assert.Equal(probe.RunId, op.RunBinding);
                // M1①② 每个节点**各自**取得独立且唯一的发送许可（本笔 sendSeq=1、身份自证）
                Assert.Equal(1, op.LastSendSeq);
                Assert.Equal("sub:" + op.RequestIdentity + ":1", op.SubmissionIdentity);
            }

            var nodePermits = probe.Ops
                .Where(o => !string.IsNullOrEmpty(o.Candidate?.NodeId))
                .Select(o => o.SubmissionIdentity).ToList();
            Assert.Equal(nodePermits.Count, nodePermits.Distinct(StringComparer.Ordinal).Count()); // 节点许可互不相同
            Assert.DoesNotContain(parentOp.SubmissionIdentity, nodePermits);                        // 与父登记许可不同
            Assert.Null(probe.OpenSubmissionIdentity);                                              // 全部结清（无开放未决发送）
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 M1③（反例·[批次四十四 会诊重要项处置]）**：**外部启动台账占用存在时，夹具注入的自有归属
    /// 也不得触发豁免**——台账已有未终结的外部启动记录 ⇒ 占用归属不可证明为本 run 自身 ⇒ 一律 fail-closed。
    /// 构造：E1 受理（驱动登记）后由屏障在该时点写入一条未终结外部启动台账记录，**同时**让接缝归属供给器
    /// 返回本 run 的 runBinding（取证「归属确被提供」），并把执行占用置真。断言：节点**零发送**、操作停在
    /// `Queued`、运行不得成功——即阻断来自「台账占用优先」规则，而不是「没有归属可用」。
    /// **范围**：宿主层测试接线态；生产不填归属（见 `CurrentArbitrationFacts`）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_OwnDriveOccupied_LedgerOccupationOverridesSeamOwnership_NoExemption()
    {
        var root = NewRoot("tcledger-");
        try
        {
            var occupied = false;
            var providerCalls = 0;
            var callsWhenLedgerPlanted = -1;
            IReadOnlyCollection<string>? lastOwnOffered = null;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                nodeIds: ["n-1"],
                configureSeams: seams =>
                {
                    seams.OccupiedProvider = () => Volatile.Read(ref occupied);
                    var runsDir = Path.Combine(root, "runs");
                    seams.OwnInFlightRunBindingsProvider = () =>
                    {
                        Interlocked.Increment(ref providerCalls);
                        var list = new RunStore(runsDir).List()
                            .Select(r => r.RunId)
                            .Where(id => !string.IsNullOrEmpty(id))
                            .Select(id => id!)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        lastOwnOffered = list;
                        return list;
                    };
                    seams.Barriers = new AdmissionBarriers
                    {
                        AfterAcceptBeforeLedger = () =>
                        {
                            // E1 已受理（驱动登记）后：写入一条**未终结**外部启动台账记录（＝外部占用存在）
                            var ledger = new ExternalStartLedger(root);
                            var recorded = ledger.RecordAccepted(new ExternalStartLedgerEntry
                            {
                                SubmissionIdentity = "sub:fixture-external:1",
                                SendSeq = 1,
                                CandidateId = "cand-fixture-external",
                                ResourceRef = "flow:外部占用占位",
                                ActionId = "act-fixture-external",
                                TargetBgiEpoch = RoutingFakePort.Epoch,
                                AcceptedAtUtc = DateTimeOffset.UtcNow,
                                EvidenceSource = "fixture_ledger_occupation",
                            });
                            Assert.True(recorded.Success, "夹具前置：外部启动台账未终结记录写入失败 " + recorded.Reason);
                            callsWhenLedgerPlanted = Volatile.Read(ref providerCalls);
                            Volatile.Write(ref occupied, true);
                            return Task.CompletedTask;
                        },
                    };
                });

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("运行必须收敛（不得悬挂）", probe));
            Assert.True(providerCalls > 0, "取证前置：归属供给器必须确实被调用过（否则本分支无意义）");
            Assert.Equal(probe.RunId, Assert.Single(lastOwnOffered ?? []));
            Assert.True(callsWhenLedgerPlanted >= 0, "取证前置：台账注入时点必须已记录");
            // **关键证据**：台账占用存在后宿主**不再咨询归属供给器**（`ledgerOccupied ? null : provider()`），
            // 即节点笔的归属恒为 null ⇒ 与「相邻正例夹具（同一供给器形态可生效）应由豁免放行」形成对照，
            // 证明本夹具的阻断来自「台账占用优先」，而不是「没有归属可用」或断言空过。
            Assert.Equal(callsWhenLedgerPlanted, Volatile.Read(ref providerCalls));
            Assert.Equal(0, probe.SendCount);                                   // 零节点发送
            var nodeOps = probe.Ops.Where(o => o.Candidate?.NodeId == "n-1").ToList();
            Assert.True(nodeOps.Count == 1, Diag("节点操作已在册（被登记但未取许可）", probe));
            Assert.Equal(OperationRequestState.Queued, nodeOps[0].RequestState);
            Assert.Equal(0, nodeOps[0].LastSendSeq);
            Assert.True(string.IsNullOrEmpty(nodeOps[0].SubmissionIdentity));
            Assert.NotEqual(WorkflowRunState.Succeeded, probe.State);            // 台账占用优先：不得成功
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **§12.3 交错 ⑤（连续超 32 节点）验收**：整条 33 节点流程必须跑通且发送恰好 33 次——若节点操作
    /// 未随时间结清，第 33 个占位会因 `operations_capacity_full` 失败；故本夹具是 G8「主槽位随责任结清释放」
    /// 的端到端证据（同时断言全程未出现 `operations_capacity_full`）。
    /// **[更新·2026-09-21 批次十三]** 原「附带登记（交错 ① 首节点抢先）：强制版夹具仍欠」的表述**已被取代**：
    /// 强制版夹具 `NodeAdmission_BeforeGateObservation_E1StillOpen_NoChildPermitYet` 已建（经**门面锁外**观察点
    /// `TaskCenterAdmissionSeams.BeforeSuccessorAdmission`，见设计稿 §24.29）。本夹具只保留交错⑤的容量证据。
    /// </summary>
    // **[P50 结题·2026-09-22 批次四十九]** 本用例的 `Skip` 已**解除**——根因不再是「未知的负载敏感」：
    // 负载复现入口 `P50_LoadRepro_WholeClass_UnderControlledLoad`（10 轮 × 16 负载线程 × 本类四个重夹具，
    // 直调本方法本体）在修复前稳定复现（10 轮中 5 轮红灯），取证显示根因＝Windows **文件争用家族**：
    // `UnauthorizedAccessException：Access to the path is denied`（运行记录原子替换/读取与租约锁路径未把该族
    // 异常纳入有界重试）⇒ 运行被收敛为 `Unknown`/`Interrupted`。修复（`RunStore` 与 `ArbitrationLeaseStore`
    // 把 `UnauthorizedAccessException` 与 `IOException` 同列**有界争用重试**，预算耗尽仍原样抛出）之后，
    // 同参数**连续两轮 10×16 全绿**（§24.61）。**断言保持严格、未放宽**；容量证据另由组件级
    // `Capacity_33NodeCandidates_AllAccepted_WhenEachSettled`（正向）／`Capacity_MainSlotsExhausted_33rdCreateRejected`（负向）承担。
    [Fact]
    public async Task NodeSubmit_33NodeFlow_NoCapacityExhaustion()
    {
        var root = NewRoot("tccap-");
        try
        {
            var nodeIds = Enumerable.Range(1, 33).Select(i => "n-" + i).ToArray();
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: nodeIds);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("33 节点流程必须收敛", probe));
            // 逐节点身份证据（会诊要求）：本 run 恰有 33 个**不同**节点的 successor 操作，每笔发送身份非空且唯一，
            // 总计 34 条记录（33 successor + 1 条 E1 流程操作）——比「只看终态快照」更能证明逐节点登记与释放。
            var nodeOps = probe.Ops.Where(o => !string.IsNullOrEmpty(o.Candidate?.NodeId)).ToList();
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("33 节点流程应跑通", probe));
            Assert.True(probe.SendCount == 33, Diag("应恰好发送 33 次（实际=" + probe.SendCount + "）", probe));
            Assert.True(nodeOps.Count == 33, Diag("successor 操作数应为 33（实际=" + nodeOps.Count + "）", probe));
            Assert.Equal(nodeIds.OrderBy(x => x), nodeOps.Select(o => o.Candidate!.NodeId).OrderBy(x => x));
            Assert.Equal(33, nodeOps.Select(o => o.SubmissionIdentity).Distinct().Count());
            Assert.DoesNotContain(nodeOps, o => string.IsNullOrEmpty(o.SubmissionIdentity));
            Assert.True(probe.Ops.Count == 34, Diag("操作总数应为 34（33 successor + 1 E1；实际=" + probe.Ops.Count + "）", probe));
            // 「全程无容量拒绝」的**原因码取证仍欠**（会诊指出：创建容量检查失败发生在新 Operation 登记之前，
            // 原因经 AdmissionResult 返回、不保证出现在最终 Ops.LastResult；真实码形如
            // operations_capacity_full(active=…)）。本夹具以「33 个不同节点操作全部登记成功且身份唯一」间接约束该失败路径。
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **G6 真实链路反例（会诊遗留阻断的端到端验收；范围已按会诊收窄）**：Sender **已返回 Accepted**、
    /// 接管台账已落盘，随后在**关闭调用之前**抛异常（`AdmissionBarriers.AfterLedgerBeforeClose` 注入）⇒
    /// 门面 `ProcessRoundAsync` 收敛为 `Error` ⇒ 宿主必须映射为 **Unknown**（不猜成功、也不猜失败），
    /// Runner 记录节点结果为 `unknown` 并让运行 `Unknown` 停驻——**绝不允许**被 `_ => Rejected` 兜底反转成
    /// 「确定拒绝」（那会诱发重发）。
    /// **证明边界**：本注入点在 `CloseSubmission` **之前**，故等价于「关闭调用前、尚无关闭副作用的异常」；
    /// **不**代表关闭事务内发布失败、关闭已提交后抛错或 `close.Success=false` 等情形（归后续批次）。
    /// 本夹具走真实门面轮次处理链，不是仅分类级断言。
    /// </summary>
    [Fact]
    public async Task SenderAcceptedThenCloseThrows_ConvergesUnknown_NotRejected()
    {
        var root = NewRoot("tcacc-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                afterLedgerBeforeClose: () => throw new InvalidOperationException("close-phase failure"));

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("运行必须收敛（不得停在活动态）", probe));
            Assert.True(probe.SendCount == 1, Diag("发送已发生且不得因后续异常重发", probe));
            Assert.True(probe.State == WorkflowRunState.Unknown,
                Diag("Accepted 后关闭异常必须收敛 Unknown（不得假报成功/确定拒绝）", probe));
            Assert.True(probe.FirstNodeResult == "unknown",
                Diag("节点结果必须是 unknown（实际=" + probe.FirstNodeResult + "）——rejected 即事实反转", probe));
            // 注入命中证明：异常确实发生在「Sender 已 Accepted 且接管已落盘」之后（否则本反例未成立）。
            var injectedIdentity = Assert.Single(probe.FaultInjections).AcceptedSendIdentity;
            var nodeOps = probe.Ops.Where(o => o.Candidate?.NodeId == "n-1").ToList();
            Assert.True(nodeOps.Count == 1, Diag("应恰有一个该节点操作", probe));
            Assert.Equal(nodeOps[0].SubmissionIdentity, injectedIdentity);
            // 责任保留：最终未决 Submission 必须仍是**本笔**发送身份（不是被当作未受理而丢弃）。
            Assert.Equal(nodeOps[0].SubmissionIdentity, probe.OpenSubmissionIdentity);
            // 受理事实不得被后续整体写回降级（会诊阻断）：Intent=Accepted ＋ jobId ＋ 发送身份三项都必须在盘上。
            Assert.Equal(SubmitIntentState.Accepted, probe.PersistedIntent);
            Assert.Equal("job-node-1", probe.PersistedJobId);
            Assert.Equal(nodeOps[0].SubmissionIdentity, probe.PersistedSendIdentity);
            // 未决责任必须**保持**——若宿主把 Error 反转成确定拒绝，门面会走「确定拒绝」分支关闭 Submission
            // （op 变 TerminalRejected）。故断言「未决/在飞状态」，这直接证伪「事实反转」。
            var nodeOpState = probe.Ops.Where(o => o.Candidate?.NodeId == "n-1")
                .Select(o => (OperationRequestState?)o.RequestState).FirstOrDefault();
            Assert.True(nodeOpState is OperationRequestState.Granted or OperationRequestState.Sending
                        or OperationRequestState.Reconciling or OperationRequestState.Queued
                        or OperationRequestState.InRound,
                Diag("未决责任必须保持（实际 op 状态=" + (nodeOpState?.ToString() ?? "<none>")
                     + "）——若为 TerminalRejected 即事实反转", probe));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// **「先接管、后关闭」顺序验收（M2／§12.2 B2 的 G7(c) 部分）**：门面在 `Sender 返回 Accepted 之后、
    /// 持久化接管台账与关闭 Submission 之前`回调（`AdmissionBarriers.AfterAcceptBeforeLedger`）——此刻运行记录
    /// **必须已经**携带本轮的 `Intent=Accepted` ＋ 非空 `JobId`（即接管先落盘），否则关闭就发生在受理事实落盘之前。
    /// </summary>
    [Fact]
    public async Task AcceptedReceipt_PersistedBeforeSubmissionClose()
    {
        var root = NewRoot("tctakeover-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true);

            Assert.True(probe.ReadOk, Diag("租约台账必须成功读取过", probe));
            Assert.True(probe.Converged, Diag("运行必须收敛", probe));
            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("端到端应跑通", probe));
            Assert.True(probe.LedgerPointReceipts.Count > 0,
                Diag("门面必须在「已受理→台账→关闭」之间触发观测点", probe));
            // 绑定节点身份、预期 jobId、**本轮完整发送身份**，并断言此刻对应 Submission **仍在册**（即尚未关闭）——
            // 「先接管、后关闭」的完整顺序＋身份关联证据，而不是任意一次观测到非空 jobId。
            var nodeOps = probe.Ops.Where(o => o.Candidate?.NodeId == "n-1").ToList();
            Assert.True(nodeOps.Count == 1, Diag("应恰有一个该节点操作（用于比对发送身份）", probe));
            Assert.Contains(probe.LedgerPointReceipts,
                r => r.NodeId == "n-1" && r.Intent == SubmitIntentState.Accepted
                     && r.JobId == "job-node-1" && r.SubmissionOpen
                     && r.AcceptedSendIdentity == nodeOps[0].SubmissionIdentity);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ── 工具 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **G8 结清判据的反例组（会诊阻断处置）**：节点 Operation 只有在其结果**能证明属于本笔发送**时才可独立结清——
    /// 逐项证明「错提交键 / 错 attempt / 无原始终态词 / 未确认结果 / 非终态结果」一律**不结清**（保守保留责任）。
    /// </summary>
    [Theory]
    [InlineData("ok", true)]                 // 键+attempt+原始终态词+业务终态 ⇒ 可结清
    [InlineData("key", false)]               // 错提交键（另一笔发送的结果）⇒ 不得结清
    [InlineData("attempt", false)]           // 错 attempt ⇒ 不得结清
    [InlineData("noRawTerminal", false)]     // 无原始终态词（本地拒绝/闸门/跳过）⇒ 不得结清
    [InlineData("unknown", false)]           // 未确认结果 ⇒ 不得结清
    [InlineData("runningRawWord", false)]    // 原始终态词仍是活动态词（running）⇒ 不得结清
    [InlineData("sameKeyOtherSendIdentity", false)] // **同键同 attempt 的另一笔发送** ⇒ 不得结清
    public void NodeOutcomeIsTerminal_RequiresSendLinkedObservedTerminal(string mode, bool expected)
    {
        using var fixture = new TerminalFixtureDirectory();
        var store = new RunStore(fixture.Path);
        var run = store.CreateRun("wf-1", "rev-1");
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = "key-1", NodeId = "n-1", Attempt = 1, Epoch = "123:456", WireRunId = run.WireRunId, Fingerprint = "payload",
            ExpiresAtUtc = "2030-01-01T00:00:00Z", SendAttempted = true, JobId = "job-1",
            Intent = SubmitIntentState.Accepted, ObservedTerminal = "succeeded", ExecutionExitConfirmed = true,
            ExecutionExitDisposition = "execution_exited", EffectState = "succeeded", AcceptedSendIdentity = "sub:req-1:1"
        };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = "n-1",
            Occurrence = 0,
            LoopIteration = 0,
            Result = mode == "unknown" ? "unknown" : "succeeded",
            RawTerminal = mode == "noRawTerminal" ? null : mode == "runningRawWord" ? "running" : "succeeded",
            SubmissionKey = mode == "key" ? "other-key" : "key-1",
            Attempt = mode == "attempt" ? 9 : 1,
            AcceptedSendIdentity = mode == "sameKeyOtherSendIdentity" ? "sub:req-9:2" : "sub:req-1:1",
        });
        var op = new OperationRecord
        {
            RequestIdentity = "req-1",
            SubmissionIdentity = "sub:req-1:1",
            WireSubmitKey = "key-1",
            LastSendSeq = 1,
            TargetEpoch = "123:456",
            ResourceRef = "node:n-1",
            Candidate = new ArbitrationCandidate { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 },
        };

        store.Update(run);
        store.TrySealTerminalNode(run.RunId, op);
        Assert.Equal(expected, TaskCenterHost.NodeOutcomeIsTerminal(store.Load(run.RunId)!, op));
    }

    private sealed class TerminalFixtureDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "node-seal-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private static string NewRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>有效台账读取：文件＋Handoff 段在册，且已含**本次运行**的 E1 启动操作
    /// （manual／`RunBinding==runId`／`Intent=="start"`／无节点身份）。</summary>
    /// <summary>
    /// 台账读取有效性：默认要求**本 run 的面板流程登记来源操作**在册（跨运行误认防护）；**启动移交**受理的 run
    /// （G4a：来源权威＝运行记录，租约内**无**面板来源操作）改用「本 run 已有节点操作在册」为有效性判据。
    /// </summary>
    private static bool TryReadValidOps(string root, string runId, out IReadOnlyList<OperationRecord> ops,
        bool requirePanelStartOp = true)
    {
        ops = [];
        if (string.IsNullOrEmpty(runId)) return false;
        try
        {
            var handoff = new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File?.Handoff;
            if (handoff?.Operations is not { } list) return false;
            var valid = requirePanelStartOp
                ? list.Any(o => o.Candidate?.Namespace == "manual"
                                && string.Equals(o.RunBinding, runId, StringComparison.Ordinal)
                                && string.Equals(o.Intent, "start", StringComparison.Ordinal)
                                && string.IsNullOrEmpty(o.Candidate?.NodeId))
                : list.Any(o => string.Equals(o.RunBinding, runId, StringComparison.Ordinal)
                                && !string.IsNullOrEmpty(o.Candidate?.NodeId));
            if (!valid) return false;
            ops = list;
            return true;
        }
        catch (IOException)
        {
            return false; // 锁文件瞬时争用＝尚未就绪
        }
    }
}
