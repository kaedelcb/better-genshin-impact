using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using System.Text.Json;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

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

        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            lock (_sync) _sends.Add(operation);
            BeforeSend?.Invoke();
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

    private sealed record RoutingProbe(
        IReadOnlyList<OperationRecord> Ops, bool ReadOk, bool Converged, string RunId, WorkflowRunState? State,
        string Note, int SendCount, IReadOnlyList<string> Logs, IReadOnlyList<LedgerPointObservation> LedgerPointReceipts)
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
    /// 驱动「面板启动 → 节点提交 → 终态观察」全链并回收可断言事实。门关分支节点提交不经门面（无 successor 操作）。
    /// `concurrentWriteBeforeSend`＝在发送入口注入「另有写入者改动运行记录」的交错（仅④使用）。
    /// </summary>
    private static async Task<RoutingProbe> ProbeNodeSubmitRoutingAsync(
        string root, bool successorWired, Action<RunStore>? concurrentWriteBeforeSend = null)
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
                new WorkflowNode
                {
                    NodeId = "n-1",
                    Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", Revision = "rev-1" },
                },
            ],
        };
        ws.Save(doc, null);

        var arbitrationDir = Path.Combine(root, "arbitration");
        var logGate = new object();
        var logList = new List<string>();
        var ledgerPoint = new List<LedgerPointObservation>();
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
                // 门面在「Sender 返回 Accepted 之后、持久化接管台账/关闭 Submission 之前」回调——
                // 用它取证「先接管、后关闭」的先后顺序（M2/§12.2 B2）：此刻须已能读到本轮 jobId，
                // 且对应 Submission **仍在册**（尚未关闭）。观测点位于门面同步流水线，不依赖抢时序。
                Barriers = new AdmissionBarriers
                {
                    AfterAcceptBeforeLedger = () =>
                    {
                        var record = runs.List().OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
                        if (record?.CurrentSubmission is { } s)
                        {
                            bool open;
                            try
                            {
                                open = new ArbitrationLeaseStore(arbitrationDir).Read().File?.Handoff?.Submission is not null;
                            }
                            catch (IOException)
                            {
                                open = false; // 读数失败＝不臆断「仍在册」
                            }
                            ledgerPoint.Add(new LedgerPointObservation(s.NodeId, s.Intent, s.JobId, s.AcceptedSendIdentity, open));
                        }
                        return Task.CompletedTask;
                    },
                },
                ProductionBoundaryFactory = (_, runs) =>
                {
                    if (concurrentWriteBeforeSend is not null) port.BeforeSend = () => concurrentWriteBeforeSend(runs);
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
            for (var i = 0; i < 800; i++)
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
            for (var i = 0; i < 200; i++)
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
            for (var i = 0; i < 50; i++)
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
            return new RoutingProbe(snapshot, readOk, converged, run?.RunId ?? "", run?.State, run?.Note ?? "",
                port.SendCount, logs, ledgerPoint.ToList());
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
