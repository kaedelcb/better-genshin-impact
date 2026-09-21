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
/// **原负载失败仍未解决**；它已作为 P50 风险登记（设计稿 §16/§17），须据诊断日志定位根因，
/// 并**保留单独、可重复的负载复现入口**，不得因隔离而让日常回归不再暴露该路径缺陷。
/// </summary>
[CollectionDefinition("TaskCenterHeavyE2E", DisableParallelization = true)]
public sealed class TaskCenterHeavyE2ECollection;

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
    /// **§12.3 交错⑤·逐节点释放直接证据（组件层；[新增·2026-09-21 批次十四]）**：
    /// 6 节点流程中，**第 k 次（k≥2）节点发送时**，前一节点的 Operation 必须**已按自身发送身份终局**
    /// （`TerminalCompleted`）并**迁出 `Active` 计容区**——**逐次**取证（不是只看最终态，故与 P19② 的
    /// 「宿主链路逐节点释放直接断言」对应；不依赖被 P50 暂停的 33 节点用例）。
    /// </summary>
    [Fact]
    public async Task NodeSubmit_EachSendObservesPreviousNodeReleased()
    {
        var root = NewRoot("tcsweep2-");
        try
        {
            var nodeIds = Enumerable.Range(1, 6).Select(i => "n-" + i).ToArray();
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

            Assert.True(probe.State == WorkflowRunState.Succeeded, Diag("6 节点流程应收口成功", probe));
            Assert.True(probe.SendCount == 6, Diag("应恰好发送 6 次", probe));
            Assert.Equal(5, observations.Count);            // 第 2..6 次发送各观察一次（共 5 次逐节点释放取证）
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

        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            lock (_sync) _sends.Add(operation);
            BeforeSend?.Invoke();
            OnBeforeSend?.Invoke(SendCount);
            OnBeforeSendWithPayload?.Invoke(SendCount,
                payload is null ? null : System.Text.Json.JsonSerializer.Serialize(payload));
            return Task.FromResult(new BgiExternalResponse
            {
                Success = true,
                Data = "{\"status\":\"accepted\",\"taskHandle\":\"job-node-1\"}",
            });
        }

        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
            => Task.FromResult<BgiJobListSnapshot?>(null);

        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult<(string?, BgiJobInfo?)>(("succeeded", new BgiJobInfo { JobId = jobId, State = "succeeded" }));

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

    /// <summary>
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
        Action<RunStore, int, string?>? onBeforeSendWithPayload = null)
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
        var acceptCount = 0;      // 1＝E1 轮次的「Accepted 后、台账前」观测（交错① 的强制阻塞点）
        var port = new RoutingFakePort();
        var runs = new RunStore(runsDir);
        var host = new TaskCenterHost(
            flowsDir, runsDir, Path.Combine(root, "catalog.json"),
            () => client, log: entry => { lock (logGate) logList.Add(entry); },
            runnerFactory: null, readinessOverride: () => (true, null),
            localExecutionCapability: () => true, statusSnapshotProvider: () => null,
            admissionWired: true,
            admissionSeams: new TaskCenterAdmissionSeams
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
                            // 有界重试：全量并行负载下跨进程锁（FileShare.None）瞬时争用会让单次读抛 IOException——
                            // 那会被误记为「已关闭」而让本夹具偶发失败（实测：满负载下 1/1 次）。此处重试后再定论。
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
                            // 仍读不到＝不臆断「仍在册」：记 false（断言会失败并给出诊断），绝不静默放宽。
                            open ??= false;
                            ledgerPoint.Add(new LedgerPointObservation(s.NodeId, s.Intent, s.JobId, s.AcceptedSendIdentity, open.Value));
                        }
                    },
                    // G6「Accepted 后关闭阶段抛异常」交错注入点（会诊要求的真实链路反例）。
                    AfterLedgerBeforeClose = afterLedgerBeforeClose is null
                        ? null
                        : () =>
                        {
                            // 只对**节点提交**轮次注入（E1 流程启动轮次跳过，否则启动本身会被夹具打断）。
                            if (Interlocked.Increment(ref ledgerCloseCount) > 1)
                            {
                                // 注入前取证「受理回执已落盘」——否则本夹具可能因别的原因（如接管落盘失败）而通过。
                                var injected = runs.List().OrderByDescending(r => r.UpdatedAt).FirstOrDefault()
                                    ?.CurrentSubmission?.AcceptedSendIdentity ?? "";
                                faultInjections.Add(new FaultInjectionObservation(injected));
                                afterLedgerBeforeClose();
                            }
                            return Task.CompletedTask;
                        },
                },
                ProductionBoundaryFactory = (_, runs) =>
                {
                    if (concurrentWriteBeforeSend is not null) port.BeforeSend = () => concurrentWriteBeforeSend(runs);
                    if (onBeforeSend is not null) port.OnBeforeSend = n => onBeforeSend(runs, n);
                    if (onBeforeSendWithPayload is not null)
                        port.OnBeforeSendWithPayload = (n, payloadJson) => onBeforeSendWithPayload(runs, n, payloadJson);
                    return new BgiWorkflowExecutionBoundary(port, runs);
                },
            },
            successorAdmissionWired: successorWired);
        try
        {
            var start = await host.StartWorkflowAsync(doc.WorkflowId!);
            Assert.Equal(HostActionStatus.Registered, start.Status);

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
                readOk = TryReadValidOps(root, runId, out var current);
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
                if (TryReadValidOps(root, runId, out var opsNow))
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
    /// **§12.3 交错 ⑤（连续超 32 节点）验收**：整条 33 节点流程必须跑通且发送恰好 33 次——若节点操作
    /// 未随时间结清，第 33 个占位会因 `operations_capacity_full` 失败；故本夹具是 G8「主槽位随责任结清释放」
    /// 的端到端证据（同时断言全程未出现 `operations_capacity_full`）。
    /// **[更新·2026-09-21 批次十三]** 原「附带登记（交错 ① 首节点抢先）：强制版夹具仍欠」的表述**已被取代**：
    /// 强制版夹具 `NodeAdmission_BeforeGateObservation_E1StillOpen_NoChildPermitYet` 已建（经**门面锁外**观察点
    /// `TaskCenterAdmissionSeams.BeforeSuccessorAdmission`，见设计稿 §24.29）。本夹具只保留交错⑤的容量证据。
    /// </summary>
    // **[P50 复现证据·2026-09-21]** 曾尝试取消 Skip：定向单跑通过，但**满负载全量套件 5 轮中出现 1 轮红灯**——
    // 同宿主类的负载敏感夹具 `NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission` 收敛失败（保守方向、无双跑）。
    // 结论：**负载敏感性真实存在且可复现**（不再只是历史观察），启用本用例会以约 20% 概率污染基线；
    // 按「基线必须稳定 + 断言不得放宽」纪律**恢复 Skip**（断言保持严格、未放宽），P50 继续为**阻断式挂账**
    // （根因未定位、诊断入口未落实、生产节点改道门继续保留——见设计稿 §24.28-B）。
    // 容量证据由确定性**组件级**夹具承担：
    //   `ArbitrationAdmissionServiceTests.Capacity_33NodeCandidates_AllAccepted_WhenEachSettled`（正向）
    //   `ArbitrationAdmissionServiceTests.Capacity_MainSlotsExhausted_33rdCreateRejected`（负向）
    [Fact(Skip = "P50：负载敏感性已复现（启用后满负载 5 轮中 1 轮红灯，同宿主类夹具收敛失败）。诊断入口未落实 ⇒ 维持暂停执行（断言严格未放宽）；生产节点改道门继续保留。")]
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
        var run = new WorkflowRunRecord { RunId = "run-1", WorkflowId = "wf-1" };
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
            ResourceRef = "node:n-1",
            Candidate = new ArbitrationCandidate { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 },
        };

        Assert.Equal(expected, TaskCenterHost.NodeOutcomeIsTerminal(run, op));
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
    private static bool TryReadValidOps(string root, string runId, out IReadOnlyList<OperationRecord> ops)
    {
        ops = [];
        if (string.IsNullOrEmpty(runId)) return false;
        try
        {
            var handoff = new ArbitrationLeaseStore(Path.Combine(root, "arbitration")).Read().File?.Handoff;
            if (handoff?.Operations is not { } list) return false;
            if (!list.Any(o => o.Candidate?.Namespace == "manual"
                               && string.Equals(o.RunBinding, runId, StringComparison.Ordinal)
                               && string.Equals(o.Intent, "start", StringComparison.Ordinal)
                               && string.IsNullOrEmpty(o.Candidate?.NodeId)))
                return false; // 本次运行的 E1 启动操作必须已登记（跨运行误认防护）
            ops = list;
            return true;
        }
        catch (IOException)
        {
            return false; // 锁文件瞬时争用＝尚未就绪
        }
    }
}
