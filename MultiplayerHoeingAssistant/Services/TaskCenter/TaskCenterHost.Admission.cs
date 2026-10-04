using System;
using System.IO;
using System.Threading;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>R5.2 B2 仲裁接线测试接缝（生产=null 全走实况；夹具注入事实快照/发送结果/时钟）。</summary>
internal sealed class TaskCenterAdmissionSeams
{
    public bool? F11Active { get; set; }
    public bool? Occupied { get; set; }
    public bool? FactsUnknown { get; set; }
    /// <summary>
    /// 夹具接缝：执行占用事实的**时变**来源（生产＝BGI 控制面快照 `TaskRunning`）。用于复现
    /// 「E1 登记时无占用 → 驱动起跑后占用成立」的真实时序；未注入时回落到 <see cref="Occupied"/>。
    /// </summary>
    public Func<bool>? OccupiedProvider { get; set; }
    /// <summary>
    /// 夹具接缝：**本宿主自有在飞驱动** runBinding 集合的供给（§12.3 M1③ 限定豁免的事实输入）。
    /// **生产恒 null**（见 `CurrentArbitrationFacts` 的保守口径说明）；注入时**只**影响「执行占用」这一项的
    /// 有限豁免，不改变其他闸门（外部启动台账占用存在时仍一律不给归属）。
    /// </summary>
    public Func<IReadOnlyCollection<string>>? OwnInFlightRunBindingsProvider { get; set; }
    public string? Epoch { get; set; }
    public Func<SubmissionDispatch, Task<SendOutcome>>? SenderOverride { get; set; }
    public Func<DateTimeOffset>? UtcNow { get; set; }
    public AdmissionBarriers? Barriers { get; set; }
    /// <summary>租约所有权 TTL 秒（生产 null=15s 默认；夹具注小值加速接管观察验证——接管期限取自被观察租约，须两端同值）。</summary>
    public int? OwnershipTtlSeconds { get; set; }
    /// <summary>夹具接缝：进入「§6.3 接管观察」阶段时回调（用于把并发首调者的目标交错固定下来，
    /// 证明后继观察者确实与先行接管者重叠，而不是靠调度运气）。</summary>
    public Action? OnTakeoverObservationEntered { get; set; }
    /// <summary>
    /// 夹具接缝：**生产执行边界**工厂覆盖（null＝真实 `new BgiWorkflowExecutionBoundary(client, runs)`）。
    /// 依据设计稿 §12「实施前置发现」：`BgiExternalClient` 是 sealed 具体类且无线协议注入接缝，宿主级
    /// 「后继节点经仲裁面真实提交 + 断言发送次数/路由」验收在不注入端口时不可满足。仅测试注入；生产
    /// 构造恒不注入（`_admissionSeams` 为 null），故生产组装与行为不变。
    /// </summary>
    public Func<BgiExternalClient, RunStore, BgiWorkflowExecutionBoundary>? ProductionBoundaryFactory { get; set; }

    /// <summary>
    /// **夹具接缝：节点准入入口、取得门面锁（`_gate`）之前**的**只发信号**观察点（§17 P17／§12.3 交错①
    /// 「首节点抢先」的强制版需要锁外观察点——`AdmissionBarriers` 全部在锁内，锁内等待会自死锁）。
    /// **生产恒 null＝空操作**；本回调**不得**改变任何状态（只读快照＋信号）——故签名**不接收任何生产对象**
    /// （不给测试接缝留下修改 `WorkflowSubmitRequest`/`Run`/`Node` 的能力）。
    /// </summary>
    public Func<Task>? BeforeSuccessorAdmission { get; set; }
}

/// <summary>
/// R5.2 B2（E1）：任务中心宿主流线仲裁门面接线（冻结稿 v8 §0/§2.2/§5 落地）。
/// 布线决策登记（冻结稿 §7.3「实现入档」）：
/// - 门面单例懒组装于首个执行入口；租约目录=运行存储同级 arbitration（配置面唯一权威在助手本机，不碰 BGI User 目录）；
///   EnsureOwnership 每次组装执行（TTL 过期即重获）+ RecoverAfterRestart 每进程一次（幂等）。
/// - E1 候选构造按 §2.2 面板 start 列：scope=bgi:local:{epoch}（epoch 首次构造捕获固定，只比较不重写）、
///   namespace=manual、trigger=manual:panel:{requestIdentity}（I3 占位符门面回填）、resourceRef=flow:{workflowId}、
///   intent=start、排序键全缺省（本阶段适配器缺省冻结）；发送前预建 Planned 运行固定 candidateId→runId 绑定
///   （§3.2 绑定不可改写；Planned 记录无执行副作用，仲裁未受理即终态化清理留痕）。
/// - 事实快照来源：执行占用=BGI 控制面快照 TaskRunning；快照缺失=未知（NeedReconcile 保守拒绝，不解释为空闲）。
/// - F11 独立停止闸门事实源：当前助手无 F11 实时信号（F11 为 BGI 侧用户按键），生产恒 false——
///   BGI 侧 F11 取消效果经「执行占用/回执词/WasCancelled」已有通道覆盖；实时闸门源归后续阶段登记。
/// - E1 接管台账=RunStore 运行记录（AdmissionHooks 文档冻结）：受理持久化=运行记录在册可载；
///   终局回写=驱动观察收尾按 runBinding 反查 Operations 调 MarkOperationTerminal（台账交叉确认=运行记录终态）。
/// - B2-β（E2 恢复五路径接线，已落地）：ResumeRunAsync/RegisterResumeHandoff 接线后一律经门面 AdmitRecoveryAsync
///   （恢复专用准入边界：不排序不产候选、恢复意图持久化后发送、保留原票据与 RestorePending 责任）；sender 恢复分支
///   DispatchResumeViaHostAsync 按 runBinding 反查台账驱动 ResumeAsync；恢复拒绝不动原运行记录。
///   确认取消后重跑=走 E1 全边界（已接线）；Unknown 待对账=既有拒绝引导（无副作用）；启动移交登记=台账无执行意图（resume 模式经本边界）。
///   登记：恢复操作不走 RetryAsync（轮次排序违反「恢复不排序」——可重试拒绝由入口新操作重新发起）；A6 票据恢复状态机消费侧归 R5.3。
/// - 节点级后继提交（Runner 经边界）归 B2-γ；本批 sender 遇其他非流程启动/恢复形状=Unknown 保守待对账。
/// 会诊登记（DS FLASH B2-α 轮，建议级归后续批次）：
/// - 建议-1 对账/终局交错：MarkAdmissionTerminalIfAny 与门面「受理→台账→关闭」交错窗下，关闭路径对已终局操作幂等断言归 B4；
/// - 建议-2 接线路经就绪判定在发送侧兜底（CreateRunner launch_error），生产恒传 ensureExecutionReady 前置保留观察，B4 收口复核；
/// - 建议-3 NeedPreemptConfirm 下预建运行已取消的 candidateId→runId 绑定再入/迁移处置归 R5.3 抢占语义登记；
/// - 建议-4 ReadCore 内部 IO 异常折 Corrupt 不经锁争用重试（锁文件不存在直读路径），区分「争用可重试/真损坏留痕」归后续打磨。
/// </summary>
public sealed partial class TaskCenterHost
{
    private readonly bool _admissionWired;

    // Host-scoped fault seams for BO-13 terminal reconciliation tests; production leaves all null.
    internal Func<int, Exception?>? AdmissionTerminalReadFaultForTest { get; set; }
    internal Func<string, int, Exception?>? AdmissionTerminalWriteFaultForTest { get; set; }
    internal Func<string, AdmissionResult?>? AdmissionTerminalResultForTest { get; set; }
    internal Action? AdmissionTerminalReconciliationCompletedForTest { get; set; }
    internal TimeSpan? AdmissionTerminalReconciliationTimeoutForTest { get; set; }

    private enum AdmissionTerminalReconciliationOutcome
    {
        NotRequired,
        NotTerminal,
        NoMapping,
        Completed,
        Pending,
        Failed,
    }
    /// <summary>
    /// B2-γ 第 3 步「路径启用」独立门（§12.3 施工阻断：第 3 步尚不得启用相关路径）。
    /// `_admissionWired` 只表示 E1/E2 入口已接线；**节点后继提交改道必须另开此门**——生产构造恒不传
    /// （＝false，节点提交保持 R4 直通）；仅当 G1/G2/G4/G4a/G5/G6/G7/G8/G9/G10 逐条闭环并通过 §12.3 交错验收后，
    /// 才允许由后续批次在生产构造显式打开。代码就绪 ≠ 路径启用。
    /// </summary>
    private readonly bool _successorAdmissionWired;

    /// <summary>夹具接缝：第 3 步路径门当前是否生效（＝E1/E2 已接线 **且** 第 3 步显式启用）。</summary>
    internal bool SuccessorAdmissionWiredForTest => _admissionWired && _successorAdmissionWired;

    /// <summary>节点责任只消费 RunStore 已耐久发布的节点封印；完整原提交、退出/效果和前置事实
    /// 随封印保护，发送身份/epoch/出现/attempt 必须与该 Operation 相同。整 run 的封印另行发布。</summary>
    internal static bool NodeOutcomeIsTerminal(WorkflowRunRecord run, OperationRecord op)
        => TerminalReleaseEvidence.NodeSeal(run, op) is not null;

    private string? ReadTerminalReleaseEvidence(string submissionIdentity, int sendSeq)
    {
        var read = _admissionStore?.Read();
        var matches = read?.File?.Handoff?.Operations?.Where(o => o.SubmissionIdentity == submissionIdentity
            && o.LastSendSeq == sendSeq && string.IsNullOrEmpty(o.MergedInto)).ToList();
        if (matches is not { Count: 1 } || matches[0].RunBinding is not { } runId) return null;
        var op = matches[0]; var run = _runs.Load(runId);
        if (run is null || run.RunId != runId) return null;
        if (op.OperationType == OperationType.NodeExecution)
            return TerminalReleaseEvidence.NodeSeal(run, op) is { } nodeSeal ? "runstore-seal:" + nodeSeal.Id : null;
        if (op.OperationType is not (OperationType.FlowRegistration or OperationType.Recovery or OperationType.Handoff)) return null;
        return TerminalReleaseEvidence.ValidRunSeal(run) ? "runstore-seal:" + run.TerminalRelease!.Id : null;
    }

    /// <summary>
    /// **节点操作独立终局出口（G8／§12.3）**：把「已可被运行台账证明终局」的节点 Operation 按
    /// 完整发送身份结清（→TerminalPendingTransfer→Tombstone，主槽位随迁移释放），**不等整条 run 终态**。
    /// 触发点＝下一次节点准入之前（此时 Runner 已 await 上一节点终态并落盘 `NodeOutcomes`）。
    /// 只结清能证明终局的；未确认/未知一律不动（保守）。异常留痕不影响准入主流程。
    /// </summary>
    private void SweepTerminalNodeOperations(string runId)
    {
        if (!_admissionWired || _admission is null || _admissionStore is null) return;
        WorkflowRunRecord? run;
        try
        {
            run = _runs.Load(runId);
        }
        catch (Exception)
        {
            return;
        }
        if (run is null) return;

        List<OperationRecord> candidates;
        try
        {
            var leaseRead = _admissionStore.Read();
            if (leaseRead.Status == ArbitrationLeaseStatus.Corrupt)
            {
                // **[P50 复核·批次四十九]** **不可读 ≠ 确无映射**：租约不可确认（争用预算耗尽/损坏）时
                // 不得按「无未决映射」静默通过本轮扫描——留诊断并放弃**本轮**清理（下一轮/心跳再清，保守方向）。
                TryLog("[任务中心] 节点终局扫描：租约不可确认（Corrupt），本轮不清理：" + leaseRead.Detail);
                return;
            }
            candidates = leaseRead.File?.Handoff?.Operations?
                .Where(o => string.Equals(o.RunBinding, runId, StringComparison.Ordinal)
                            && o.Zone == OperationZone.Active
                            && o.RequestState == OperationRequestState.Accepted
                            && o.OperationType == OperationType.NodeExecution)
                .ToList() ?? [];
        }
        catch (IOException)
        {
            return; // 锁文件瞬时争用＝下一轮再清（不阻塞准入）
        }

        foreach (var op in candidates)
        {
            try
            {
                var seal = _runs.TrySealTerminalNode(runId, op);
                if (seal is null) continue;
                var r = _admission.MarkOperationTerminal(op.RequestIdentity, "runstore-seal:" + seal.Id);
                if (r.Kind == AdmissionResultKind.Error)
                    TryLog("[任务中心] 节点操作独立终局被拒（" + r.ReasonCode + "）：" + r.Detail + "——保守留待对账。");
            }
            catch (Exception ex)
            {
                TryLog("[任务中心] 节点操作独立终局回写失败（保守留待对账）：" + ex.GetType().Name);
            }
        }
    }

    /// <summary>诊断留痕（日志委托异常不得穿透执行路径——会诊要求）。</summary>
    private void TryLog(string message)
    {
        try
        {
            _log?.Invoke(message);
        }
        catch (Exception)
        {
            // 诊断失败不影响执行结论。
        }
    }

    /// <summary>
    /// **生产执行边界的唯一组装点**（`CreateRunner` 的 Runner 边界 与 后继发送分派的 Sender 边界共用）。
    /// 会诊复审发现：发送分派若自行 `new BgiWorkflowExecutionBoundary(c, _runs)`，会与 Runner 侧走不同实例；
    /// 端口化夹具下更会导致「预检用注入端口、发送用真实客户端」的分裂。统一走本方法。
    /// 返回的是**生产边界本体**（不含仲裁装饰器），故不会造成重入。
    /// </summary>
    private BgiWorkflowExecutionBoundary CreateProductionBoundary(BgiExternalClient client)
        => _admissionSeams?.ProductionBoundaryFactory?.Invoke(client, _runs)
           ?? new BgiWorkflowExecutionBoundary(client, _runs);
    private readonly string? _arbitrationDir;
    private readonly TaskCenterAdmissionSeams? _admissionSeams;
    private string? _runsDirPath;
    private readonly object _admissionInitGate = new();
    private ArbitrationLeaseStore? _admissionStore;
    private ArbitrationAdmissionService? _admission;
    private string? _admissionLeaseId; // 获取时捕获的所有者身份（心跳只续本进程租约——绝不为他人续命，会诊 建议-1）
    private string? _admissionOwnerEpoch;
    /// <summary>配置面根目录（＝运行目录的父目录）：租约目录与 `external-start-ledger.json` 同根。</summary>
    private string? _admissionRoot;

    /// <summary>
    /// **真实 BGI User 配置根来源（仅演练隔离用）**：由组合根注入（生产＝BGI User 目录）。
    /// **未注入 ⇒ 演练保守拒绝**（无法证明隔离时不得在可能位于 User 下的目录写演练产物）。
    /// </summary>
    internal Func<string?>? UserConfigRootProvider { get; set; }

    /// <summary>
    /// 门面懒组装（所有权+§6.3 失联接管编排+每进程一次重启恢复；失败留 null 允许下次重试）。
    /// 接管编排=宿主职责（门面不自行接管）：TryAcquire held → LeaseTakeoverObserver 单调观察满 TTL 产出证据 →
    /// 证据获取（锁内复核）；TTL 内重启=等满观察期（单调钟，UTC 拨动不影响）——§6.3 唯一接管依据，不抄近道。
    /// 阶段二观察等待在初始化锁外异步进行（不阻塞 UI/调用线程），并发首调者每轮重查 _admission 避免双双等满 TTL。
    /// </summary>
    private async Task EnsureAdmissionFacadeAsync(CancellationToken ct)
    {
        if (_admission is not null) return;

        // 阶段一（锁内零等待）：组装+首次获取尝试。
        ArbitrationAdmissionService facade;
        ArbitrationLeaseStore store;
        LeaseOpResult acq;
        var ttl = _admissionSeams?.OwnershipTtlSeconds ?? 15;
        var ownerKey = "pid:" + Environment.ProcessId;
        // §24.12-3 集合②/③（外部启动观察恢复）需 `await`，不得在 `lock` 内执行——锁内只登记待办，出锁后执行。
        ArbitrationAdmissionService? pendingObservationRecovery = null;
        lock (_admissionInitGate)
        {
            if (_admission is not null) return;
            var root = Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
            _admissionRoot = root;
            var dir = _arbitrationDir ?? Path.Combine(root, "arbitration");
            Directory.CreateDirectory(dir); // 首个执行入口才落目录（构造零副作用原则不破）
            var utcNow = _admissionSeams?.UtcNow;
            store = new ArbitrationLeaseStore(dir, utcNow);
            var hooks = new AdmissionHooks
            {
                F11Active = () => CurrentArbitrationFacts().F11Active,
                FactsProvider = CurrentArbitrationFacts,
                BgiEpochProvider = CurrentBgiEpoch,
                Sender = DispatchViaHostAsync,
                Barriers = _admissionSeams?.Barriers,
                // M2／§12.2 B2：**节点操作不得复用流程级验证**——`ResourceRef` 前缀区分操作层：
                // - `node:`（节点执行操作）：必须验证「**本轮**受理事实已落盘且可重建」——run 记录携带
                //   同身份、同授权纪元的 `Intent=Accepted`＋非空 `JobId`；否则拒绝关闭 Submission。
                // - 其他（流程登记/恢复等非节点操作）：沿用流程级验证（run 记录在册）。
                TakeoverPersist = entry =>
                {
                    if (entry.OperationType == OperationType.ExternalStart)
                    {
                        // ExternalStart 可能在 Operation 已推进后恢复旧轮 AcceptanceClaim；按 entry 自带的
                        // 完整轮次反查请求，并要求该旧轮仍有持久 claim/Submission/终态依据，不能拿当前轮冒充。
                        var separator = entry.SubmissionIdentity.LastIndexOf(':');
                        var requestIdentity = separator > 4
                            && entry.SubmissionIdentity.StartsWith("sub:", StringComparison.Ordinal)
                            && int.TryParse(entry.SubmissionIdentity[(separator + 1)..],
                                System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out var parsedSeq)
                            && parsedSeq == entry.SendSeq
                            ? entry.SubmissionIdentity[4..separator]
                            : null;
                        var file = _admissionStore?.Read().File;
                        var externalOp = requestIdentity is null ? null : file?.Handoff?.Operations?.FirstOrDefault(o =>
                            string.Equals(o.RequestIdentity, requestIdentity, StringComparison.Ordinal));
                        if (externalOp is null || externalOp.OperationType != OperationType.ExternalStart
                            || externalOp.LastSendSeq < entry.SendSeq
                            || !string.Equals(externalOp.CandidateId, entry.CandidateId, StringComparison.Ordinal)
                            || !string.Equals(externalOp.ResourceRef ?? "", entry.ResourceRef, StringComparison.Ordinal)
                            || !string.Equals(externalOp.TargetEpoch, entry.TargetBgiEpoch, StringComparison.Ordinal)
                            || !string.Equals(externalOp.Candidate?.ActionId ?? ArbitrationOrdering.DeriveActionId(externalOp.CandidateId),
                                entry.ActionId, StringComparison.Ordinal))
                            return Task.FromResult<string?>("external_start_receipt_identity_mismatch");
                        var claimMatches = externalOp.AcceptanceClaim is { } claim
                            && string.Equals(claim.SubmissionIdentity, entry.SubmissionIdentity, StringComparison.Ordinal)
                            && claim.SendSeq == entry.SendSeq
                            && string.Equals(claim.EvidenceSource, entry.EvidenceSource, StringComparison.Ordinal)
                            && string.Equals(claim.RunId, entry.RunId, StringComparison.Ordinal)
                            && string.Equals(claim.JobId, entry.JobId, StringComparison.Ordinal);
                        var submissionMatches = file!.Handoff!.Submission is { } submission
                            && string.Equals(submission.SubmissionIdentity, entry.SubmissionIdentity, StringComparison.Ordinal)
                            && submission.SendSeq == entry.SendSeq;
                        var terminalMatches = externalOp.ExecutionResult is { } terminal
                            && string.Equals(terminal.SubmissionIdentity, entry.SubmissionIdentity, StringComparison.Ordinal)
                            && terminal.SendSeq == entry.SendSeq;
                        if (!claimMatches && !submissionMatches && !terminalMatches)
                            return Task.FromResult<string?>("external_start_receipt_basis_missing");
                        return Task.FromResult<string?>(PersistExternalStartReceiptLedger(entry));
                    }
                    // **接管路径必须按「权威操作类型」选择**（会诊阻断）：不得用 `entry.RunId` 是否为空来判定外部启动——
                    // 该字段来自回执/对账方（`SendOutcome.Accepted.RunId`／`ReconcileSettlement.Accepted.RunId`），
                    // 不是持久化的操作类型。节点/流程操作若缺 runId 必须**失败**，绝不能自动转成外部启动台账而跳过
                    // 节点侧的 jobId/提交键/发送身份/纪元验证。
                    var op = _admissionStore?.Read().File?.Handoff?.Operations?
                        .FirstOrDefault(o => string.Equals(o.SubmissionIdentity, entry.SubmissionIdentity, StringComparison.Ordinal));
                    if (op is null) return Task.FromResult<string?>("operation_identity_missing");
                    // **R5.3 §24.17（[落地批次会诊阻断处置]）**：接管载体**只按持久化的 `OperationType` 分派**——
                    // 不得再按 `ResourceRef` 前缀推断操作类别；`Unknown`（旧格式代隔离产物/缺类型）一律 **fail-closed**：
                    // 既不冒充外部启动，也不跳过节点侧 jobId/提交键/发送身份/纪元验证。
                    var isNode = op.OperationType == OperationType.NodeExecution;
                    switch (op.OperationType)
                    {
                        case OperationType.ExternalStart:
                            return Task.FromResult<string?>("external_start_receipt_identity_unresolved");
                        case OperationType.FlowRegistration:
                            // [验证会诊重要项处置] **类型与来源记录不得冲突**（§24.17-3）：流程登记的 resourceRef 必为 `flow:`。
                            if (!(op.ResourceRef ?? "").StartsWith("flow:", StringComparison.Ordinal))
                                return Task.FromResult<string?>("operation_type_source_mismatch");
                            break;
                        case OperationType.NodeExecution:
                            // 节点执行必为 `node:{nodeId}`；下方按前缀切节点身份，前缀不符即响亮拒绝。
                            if (!(op.ResourceRef ?? "").StartsWith("node:", StringComparison.Ordinal))
                                return Task.FromResult<string?>("operation_type_source_mismatch");
                            break;
                        case OperationType.Recovery:
                        case OperationType.Handoff:
                            break; // 继续走下方 run 级/节点级验证
                        default:
                            return Task.FromResult<string?>("legacy_operation_type_unresolved");
                    }
                    if (entry.RunId is not { } boundRunId) return Task.FromResult<string?>("run_id_missing");
                    if (!string.Equals(op.RunBinding, boundRunId, StringComparison.Ordinal))
                        return Task.FromResult<string?>("run_binding_mismatch");
                    var run = _runs.Load(boundRunId);
                    if (run is null) return Task.FromResult<string?>("run_record_missing");
                    if (!isNode) return Task.FromResult<string?>(null); // 流程级（E1/E2）：运行记录在册即可
                    // **完整发送身份关联**（会诊阻断项）：本笔发送的受理事实必须来自**这一笔**租约责任，
                    // 不得用「同一 run 当前恰好是 Accepted」的另一笔提交回执来关闭本 Submission。
                    if (op.Candidate is not { } cand) return Task.FromResult<string?>("candidate_missing");
                    var nodeIdFromRef = entry.ResourceRef!["node:".Length..];
                    if (!string.Equals(cand.NodeId, nodeIdFromRef, StringComparison.Ordinal))
                        return Task.FromResult<string?>("resource_ref_identity_mismatch");
                    if (run.CurrentSubmission is not { } sub) return Task.FromResult<string?>("submission_missing");
                    if (!string.Equals(sub.NodeId, cand.NodeId, StringComparison.Ordinal)
                        || sub.Occurrence != cand.Occurrence
                        || sub.LoopIteration != cand.LoopIteration
                        || sub.Attempt != cand.Attempt)
                        return Task.FromResult<string?>("receipt_identity_mismatch");
                    // 提交键 ↔ 本笔操作线上键（节点操作**必须**有键且一致）：不得让「业务身份相同但提交键不同」的回执冒充本笔。
                    if (string.IsNullOrEmpty(op.WireSubmitKey)
                        || !string.Equals(sub.Key, op.WireSubmitKey, StringComparison.Ordinal))
                        return Task.FromResult<string?>("receipt_key_mismatch");
                    if (sub.Intent != SubmitIntentState.Accepted || string.IsNullOrEmpty(sub.JobId))
                        return Task.FromResult<string?>("accepted_receipt_not_persisted");
                    // **完整发送身份关联（节点操作无条件要求）**：回执必须由**本笔**发送轮次落盘；
                    // 缺失身份一律不放行——「未记录」不等于「属于合法历史路径」，此时应保留对账责任而不是关闭。
                    if (string.IsNullOrEmpty(entry.SubmissionIdentity))
                        return Task.FromResult<string?>("receipt_send_identity_missing");
                    if (!string.Equals(sub.AcceptedSendIdentity, entry.SubmissionIdentity, StringComparison.Ordinal))
                        return Task.FromResult<string?>("receipt_send_identity_mismatch");
                    if (!string.IsNullOrEmpty(entry.TargetBgiEpoch)
                        && !string.Equals(sub.Epoch, entry.TargetBgiEpoch, StringComparison.Ordinal))
                        return Task.FromResult<string?>("receipt_epoch_mismatch");
                    return Task.FromResult<string?>(null);
                },
                LateAcceptanceReceiptPersist = entry => Task.FromResult(
                    entry.OperationType == OperationType.ExternalStart
                        ? PersistExternalStartReceiptLedger(entry)
                        : "late_receipt_operation_type_mismatch"),
                TakeoverTerminalEvidence = ReadTerminalReleaseEvidence,
                TakeoverTerminalConfirmed = (submissionIdentity, sendSeq) =>
                {
                    var read = _admissionStore?.Read();
                    var op = read.File?.Handoff?.Operations?.FirstOrDefault(
                        o => string.Equals(o.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal));
                    // R5.3 §24.1-6（[落地批次会诊阻断处置]）：**按持久化类型分派**，未知类型 fail-closed。
                    // 外部启动的权威终态由 `ExternalStartLedger` 承载：**改查台账终态记录**（§24.1-6：仅同一
                    // `submissionIdentity+sendSeq` 记录为 `Terminal` 才为 true；不得用运行快照或「查询未命中」代替）。
                    if (op is not null && op.OperationType == OperationType.ExternalStart)
                    {
                        var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
                        try
                        {
                            var ledgerRead = new ExternalStartLedger(root).Read();
                            return ledgerRead.Valid && ledgerRead.File?.Entries.Any(e =>
                                string.Equals(e.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                                && e.SendSeq == op.LastSendSeq
                                && e.State == LedgerEntryState.Terminal) == true;
                        }
                        catch (IOException)
                        {
                            return false; // 读取失败/争用＝保守不确认（不推导终态）
                        }
                    }
                    if (op is null || op.OperationType == OperationType.Unknown) return false;
                    if (op.OperationType is not (OperationType.NodeExecution or OperationType.FlowRegistration
                        or OperationType.Recovery or OperationType.Handoff)) return false;
                    return ReadTerminalReleaseEvidence(submissionIdentity, sendSeq) is not null;

                },
                // R5.3 §24.15 完成结算事务的「台账 Terminal」步（[Batch B]）：仅外部启动操作写外部台账；
                // 其余类型不写（由各自载体承载），未知类型 fail-closed。失败原因原样回传门面 ⇒ 保守停驻。
                TakeoverTerminalPersist = (submissionIdentity, sendSeq, terminalEvidence, observedAtUtc, rawTerminal, executionErrorCode, jobId, terminalEvidenceSource, terminalKind) =>
                {
                    var read = _admissionStore?.Read();
                    var op = read?.File?.Handoff?.Operations?.FirstOrDefault(
                        o => string.Equals(o.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal));
                    if (op is null) return "operation_identity_missing";
                    if (op.OperationType != OperationType.ExternalStart) return "operation_type_not_external_start";
                    var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
                    try
                    {
                        var marked = new ExternalStartLedger(root).MarkTerminal(
                            submissionIdentity, sendSeq, terminalEvidence, observedAtUtc,
                            rawTerminal, executionErrorCode, op.OperationType, jobId, terminalEvidenceSource, terminalKind);
                        if (marked.Success) return null;
                        return "ledger_terminal_failed:" + (marked.Reason ?? "unknown");
                    }
                    catch (Exception ex)
                    {
                        return "ledger_terminal_exception:" + ex.GetType().Name;
                    }
                },
                // §24.15 读回验证（[第二轮验证会诊阻断处置]）：按同一发送身份读回台账终态，并**逐字段**核对
                // 原始终态词／错误码／句柄／证据来源／观察时点是否与本次事实等值（仅「记录存在且 Terminal」不算确认）。
                TakeoverTerminalPayloadConfirmed = (submissionIdentity, sendSeq, rawTerminal, executionErrorCode, jobId, evidenceSource, observedAtUtc, terminalKind) =>
                {
                    var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
                    try
                    {
                        var ledgerRead = new ExternalStartLedger(root).Read();
                        if (!ledgerRead.Valid || ledgerRead.File is null) return false;
                        var entry = ledgerRead.File.Entries.FirstOrDefault(e =>
                            string.Equals(e.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal) && e.SendSeq == sendSeq);
                        if (entry is null || entry.State != LedgerEntryState.Terminal) return false;
                        return string.Equals(entry.TerminalEvidence, rawTerminal, StringComparison.Ordinal)
                               // [第四轮验证会诊] **逐字段等值**：不得用期望值补空（缺副本字段即不确认）。
                               && string.Equals(entry.RawTerminal, rawTerminal, StringComparison.Ordinal)
                               && string.Equals(entry.ExecutionErrorCode, executionErrorCode, StringComparison.Ordinal)
                               && string.Equals(entry.JobId, jobId, StringComparison.Ordinal)
                                && string.Equals(entry.TerminalEvidenceSource, evidenceSource, StringComparison.Ordinal)
                                && entry.TerminalObservedAtUtc == observedAtUtc
                                && entry.TerminalKind == terminalKind;
                    }
                    catch (IOException)
                    {
                        return false; // 读取失败/争用＝保守不确认
                    }
                },
                // §24.3-3 句柄合并读回（[第四轮验证会诊]）：门面据此取台账合并后的权威句柄，避免载体与台账分裂。
                // §24.12-3 集合②/③（[Batch B 收尾之五]）：把外部启动台账的**完整发送身份＋是否已终态**交给
                // 门面恢复扫描（终态**载荷**一律以本地 `PendingTerminal` 为准；台账不可读＝不可确认 ⇒ 保守停驻）。
                TakeoverLedgerScan = () =>
                {
                    var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
                    try
                    {
                        var read = new ExternalStartLedger(root).Read();
                        if (!read.Valid) return TakeoverLedgerScan.Unreadable(read.Detail);
                        var facts = (read.File?.Entries ?? [])
                            .Select(e => new TakeoverLedgerFact(
                                e.SubmissionIdentity, e.SendSeq, e.State == LedgerEntryState.Terminal, e.JobId,
                                AcceptedReceipt: true, EvidenceSource: e.EvidenceSource,
                                AcceptedAtUtc: e.AcceptedAtUtc, RunId: e.RunId, OperationType: e.OperationType,
                                TerminalEvidence: e.TerminalEvidence, RawTerminal: e.RawTerminal,
                                ExecutionErrorCode: e.ExecutionErrorCode,
                                TerminalObservedAtUtc: e.TerminalObservedAtUtc,
                                TerminalEvidenceSource: e.TerminalEvidenceSource,
                                TerminalKind: e.TerminalKind))
                            .ToList();
                        return new TakeoverLedgerScan(true, facts);
                    }
                    catch (Exception ex)
                    {
                        return TakeoverLedgerScan.Unreadable("ledger_scan_exception:" + ex.GetType().Name);
                    }
                },
                TakeoverJobIdRead = (submissionIdentity, sendSeq) =>
                {
                    var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
                    try
                    {
                        var read = new ExternalStartLedger(root).Read();
                        // [第八轮验证会诊] 三态：读取失败/损坏/记录不可确认 ⇒ `Unreadable`（门面据此保守停驻），
                        // **不得**返回「无句柄」而让读取故障被当成权威事实。
                        if (!read.Valid) return LedgerHandleProbe.Unreadable();
                        var entry = read.File?.Entries.FirstOrDefault(e =>
                            string.Equals(e.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal) && e.SendSeq == sendSeq);
                        if (entry is null) return LedgerHandleProbe.Absent();
                        return string.IsNullOrEmpty(entry.JobId)
                            ? LedgerHandleProbe.Absent()
                            : LedgerHandleProbe.Present(entry.JobId);
                    }
                    catch (IOException)
                    {
                        return LedgerHandleProbe.Unreadable(); // 读取失败/争用＝不可确认（保守停驻，不臆造）
                    }
                    catch (Exception)
                    {
                        // [第九轮验证会诊] 任何**预期外读取异常**同样折为 `Unreadable`（稳定返回合同码，fail-closed）。
                        return LedgerHandleProbe.Unreadable();
                    }
                },
            };
            facade = new ArbitrationAdmissionService(store, hooks, utcNow);
            acq = facade.EnsureOwnership(ownerKey, ttl);
            if (acq.Success)
            {
                _admissionStore = store; // 会诊重要-1：先于恢复赋值——TakeoverTerminalConfirmed/Dispatch 钩子闭包读字段，晚赋值=恒 null（保守失效）
                _admissionLeaseId = acq.Lease!.LeaseId;
                _admissionOwnerEpoch = ownerKey;
                facade.RecoverAfterRestart(); // 每进程一次（幂等；恢复五路径准入门面侧）
                _admission = facade; // 保持恢复后赋值：并发首调者不得早退复用未完成恢复的实例
                pendingObservationRecovery = facade; // 出锁后执行（见下方）；心跳同样先于该对齐启动
            }
            else if (acq.Reason != "held")
                throw new InvalidOperationException("仲裁租约获取失败：" + acq.Reason);
        }

        if (pendingObservationRecovery is not null)
        {
            await CompleteAdmissionInitAsync(pendingObservationRecovery, ttl, ct).ConfigureAwait(false);
            return;
        }

        // 阶段二（锁外有界等待）：held=旧所有者仍在 TTL 内——单调观察满 TTL 取证接管（§6.3 唯一依据）。
        var observer = new LeaseTakeoverObserver(() => _admissionMonotonic());
        var budget = TimeSpan.FromSeconds(ttl * 2 + 5); // 观察+一轮重试余量（心跳前进重观察另计）
        var started = _admissionMonotonic();
        _admissionSeams?.OnTakeoverObservationEntered?.Invoke(); // 夹具接缝：固定并发首调者的交错（生产 null=空操作）
        while (true)
        {
            ct.ThrowIfCancellationRequested(); // 宿主退出=放弃初始化（未产生任何副作用），OCE 不吞上抛
            LeaseTakeoverEvidence? evidence = null;
            while (evidence is null)
            {
                ct.ThrowIfCancellationRequested();
                // 会诊阻断项处置：并发首调者复用检查必须早于「接管证据成熟」——旧租约仍被持有时两个首调者同时进入
                // 本阶段，先接管者随即开始心跳，后继者观察到的 heartbeatSeq 持续推进=证据永不成形，原实现会空转到
                // 预算耗尽后响亮失败（共享门面其实早已可用）。故每轮等待都先复用已完成的门面。
                lock (_admissionInitGate)
                {
                    if (_admission is not null) return; // 并发首调者已接管完成——直接复用
                }
                if (_admissionMonotonic() - started > budget)
                    throw new InvalidOperationException("仲裁租约接管观察超时（仍被持有且有界预算耗尽，未产生任何副作用）");
                evidence = observer.Observe(store.Read());
                if (evidence is null) await Task.Delay(50, ct).ConfigureAwait(false);
            }

            lock (_admissionInitGate)
            {
                if (_admission is not null) return; // 并发首调者已接管完成——直接复用
                acq = facade.EnsureOwnership(ownerKey, ttl, evidence);
                if (acq.Success)
                {
                    _admissionStore = store; // 会诊重要-1：先于恢复赋值（同阶段一）
                    _admissionLeaseId = acq.Lease!.LeaseId;
                    _admissionOwnerEpoch = ownerKey;
                    facade.RecoverAfterRestart();
                    _admission = facade;
                    pendingObservationRecovery = facade; // 出锁后执行（同阶段一）
                    break;
                }
            }

            // 观察期间心跳前进/被抢先接管：证据自然失效，重新观察（其他拒绝理由响亮抛出）。
            if (acq.Reason is not ("held" or "heartbeat_advanced"))
                throw new InvalidOperationException("仲裁租约接管获取失败：" + acq.Reason);
        }

        if (pendingObservationRecovery is not null)
            await CompleteAdmissionInitAsync(pendingObservationRecovery, ttl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// **准入门面初始化收尾（[Batch B 收尾之五] 新增）**：心跳续期先行（与恢复对齐解耦），随后执行
    /// §24.12-3 集合②/③ 的外部启动观察恢复。该对齐**幂等且经门面串行**：并发首调者即便先复用门面，
    /// 重复结算也只读回既有终局事实（不产生双重终局、不重复释放）。
    /// </summary>
    private async Task CompleteAdmissionInitAsync(ArbitrationAdmissionService facade, int ttl, CancellationToken ct)
    {
        StartAdmissionLeaseHeartbeat(ttl); // 所有者存续期续期（TTL 到期=一切写入被拒）
        // 集合②：未终结台账**保留观察责任**（不可查询时长期保守停驻）；集合③：台账已终态而本地未终局 ⇒
        // 用已持久化 `PendingTerminal` 补终局。
        ct.ThrowIfCancellationRequested();   // [验证会诊重要项] 宿主取消按约定传播（未产生新副作用）
        await RecoverExternalStartObservationsAsync(facade, ct).ConfigureAwait(false);
    }

    /// <summary>本进程单调钟（§6.3：接管观察用单调时间——UTC 前跳不提前撤权、回拨不续命）。</summary>
    private static readonly System.Diagnostics.Stopwatch _admissionStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private static TimeSpan _admissionMonotonic() => _admissionStopwatch.Elapsed;

    /// <summary>
    /// **外部启动准入（B3：E3/E4/E5）**：适配器在**任何启动副作用之前**调用本入口。
    /// 链路＝§2.2 兼容候选构造 → 门面 `SubmitAsync`（锁内占位）→ Sender 内执行适配层既有启动实现（**仅一次**）
    /// → 三态对账；受理接管台账＝`external-start-ledger.json`（§4.2a：E1/E2=RunStore，E3/E4/E5=外接启动台账）。
    /// 纪律：候选的来源字段必须由可信适配器提供；旧客户端不携合同字段＝兼容候选，不得从远程自报字段推断。
    /// </summary>
    internal async Task<AdmissionResult> SubmitExternalStartViaAdmissionAsync(
        ExternalStartAdmissionRequest request, CancellationToken ct)
    {
        using var sendLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        return await SubmitExternalStartViaAdmissionCoreAsync(request, sendLifetime.Token).ConfigureAwait(false);
    }

    private async Task<AdmissionResult> SubmitExternalStartViaAdmissionCoreAsync(
        ExternalStartAdmissionRequest request, CancellationToken sendToken)
    {
        request.HostLifetimeToken = _shutdownCts.Token;
        // §7.1-1：F11 判定先于租约获取。
        if (CurrentArbitrationFacts().F11Active)
            return AdmissionResult.Of(AdmissionResultKind.F11Blocked, "f11_active",
                "F11 独立停止闸门激活（未发生租约副作用；未发送）。", "");
        if (string.IsNullOrEmpty(request.Namespace) || string.IsNullOrEmpty(request.WorkflowId)
            || string.IsNullOrEmpty(request.ResourceRef) || string.IsNullOrEmpty(request.TriggerOccurrenceId))
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request",
                "外部启动准入请求字段缺失（namespace/workflowId/resourceRef/trigger 必填）。", "");
        // 严格合同要求固定 bgiEpoch（§2.2/I-1）：未知纪元不得签发发送许可——否则台账/线协议身份会以空纪元落盘。
        var externalEpoch = CurrentBgiEpoch();
        if (string.IsNullOrEmpty(externalEpoch))
            return AdmissionResult.Of(AdmissionResultKind.Error, "bgi_epoch_unknown",
                "BGI 进程纪元未知（严格合同要求固定 bgiEpoch；未发送、未签发许可）。", "");
        sendToken.ThrowIfCancellationRequested();

        ArbitrationAdmissionService facade;
        try { await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false); facade = _admission!; }
        catch (OperationCanceledException)
        {
            return AdmissionResult.Of(AdmissionResultKind.NeedReconcile, "host_shutdown",
                "宿主退出，仲裁面初始化取消（未发送，待对账）。", "");
        }
        catch (Exception ex)
        {
            return AdmissionResult.Of(AdmissionResultKind.Error, "facade_init_failed",
                "仲裁面初始化失败（未发送）：" + ex.GetType().Name, "");
        }

        // §2.1：非空身份＝**续用既有操作**（同次用户操作的内部重试复用同一身份）；
        // 空＝新建操作（身份由门面在同一次原子发布中分配并回显）。
        var continuing = !string.IsNullOrEmpty(request.RequestIdentity);
        var admission = new AdmissionRequest
        {
            Namespace = request.Namespace,
            Kind = continuing ? AdmissionKind.ContinueUse : AdmissionKind.Create,
            RequestIdentity = continuing ? request.RequestIdentity! : "",
            SourceDetail = string.IsNullOrEmpty(request.SourceDetail) ? "external:start" : request.SourceDetail,
            WireSubmitKey = request.WireSubmitKey,
            CallerToken = sendToken,
            ProcessLocalContext = new ExternalStartContext(request.ExecuteAsync),
            // §24.17（[批次四十四 验证会诊重要项处置]）：**本入口（E3/E4/E5 外部启动）的操作类型恒为
            // `ExternalStart`**——由**调用位置**决定，不采信入参字段：否则受污染调用可把真实外部启动登记成
            // `FlowRegistration`/`NodeExecution`，而 Sender 仍按 `ExternalStartContext` 执行外部副作用，
            // 造成「已经启动、但接管按错误类型分派」的拒绝/不一致面。入参字段仅保留用于兼容（不再透传）。
            OperationType = OperationType.ExternalStart,
            Candidate = new ArbitrationCandidate
            {
                Scope = $"bgi:local:{externalEpoch}",
                Namespace = request.Namespace,
                WorkflowId = request.WorkflowId,
                // §2.2/I3：`{requestIdentity}` 占位符——新建由门面在身份分配后回填（适配器无需预知身份）；
                // **续用**则由本层以既有身份回填，使候选身份与首次登记**逐字一致**（否则换身份＝identity_conflict）。
                TriggerOccurrenceId = continuing
                    ? request.TriggerOccurrenceId.Replace("{requestIdentity}", request.RequestIdentity!, StringComparison.Ordinal)
                    : request.TriggerOccurrenceId,
                ResourceRef = request.ResourceRef,
                Intent = "start",
                Tier = request.Tier,
                Priority = request.Priority,
                ScheduledAt = request.ScheduledAt,
                // §2.2：start 候选 runId/节点身份为空、轮次/attempt=0；tier/priority/scheduledAt 由可信入口冻结提供。
            },
        };

        try
        {
            return await facade.SubmitAsync(admission).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AdmissionResult.Of(AdmissionResultKind.Error, "admission_exception",
                "仲裁面异常（结果待对账）：" + ex.GetType().Name, "");
        }
    }

    /// <summary>
    /// 优雅退出释放租约（会诊 建议-1 处置：缩短重启接管观察窗——释放保留 Handoff/Diag 段，未决事实不清空，§6.2 更替继承）；
    /// 释放失败/所有权已更替=仅留痕（留待 TTL 接管路径兜底，绝不强制）。
    /// </summary>
    private void ReleaseAdmissionLeaseOnShutdown()
    {
        if (_admissionStore is not { } store || _admissionLeaseId is not { } lid || _admissionOwnerEpoch is not { } oe) return;
        try
        {
            var read = store.Read();
            if (read.File?.Lease is not { } lease || !string.Equals(lease.LeaseId, lid, StringComparison.Ordinal)) return; // 已更替=无需释放
            var rel = store.TryRelease(lid, oe, read.File.Revision);
            if (!rel.Success)
                _log?.Invoke("[任务中心] 仲裁租约退出释放被拒（" + rel.Reason + "）——留待 TTL 接管路径。");
        }
        catch (Exception ex)
        {
            _log?.Invoke("[任务中心] 仲裁租约退出释放异常（留待 TTL 接管路径）：" + ex.Message);
        }
    }

    /// <summary>
    /// 租约心跳续期循环（B2-β 实证缺口：所有者存续期间必须续期——TTL 到期后一切变更被锁内 TTL 校验拒 lease_stale_generation，
    /// §6.1 过期不得续期复活=只能重新接管观察等满 TTL，生产不可接受）。TTL/3 节奏（下限 200ms）锁内续期；
    /// 续期被拒=所有权已更替/丢失——后续提交自然响亮拒绝（不借旧身份写入）；宿主退出随 _shutdownCts 取消。
    /// </summary>
    private void StartAdmissionLeaseHeartbeat(int ttlSeconds)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(200, ttlSeconds * 1000 / 3));
        var ct = _shutdownCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    var read = _admissionStore?.Read(); // 锁内读当前文件（修订在文件级）
                    var lease = read?.File?.Lease;
                    if (lease is null || read!.File is null) continue;
                    // 会诊 建议-1 处置：只续「本进程获取时捕获」的租约身份——所有权已更替=停止心跳（绝不为他人续命）；
                    // 后续提交经锁内身份/TTL 校验自然响亮拒绝，重取走 §6.3 接管观察路径。
                    if (!string.Equals(lease.LeaseId, _admissionLeaseId, StringComparison.Ordinal)
                        || !string.Equals(lease.OwnerEpoch, _admissionOwnerEpoch, StringComparison.Ordinal))
                    {
                        _log?.Invoke("[任务中心] 仲裁租约所有权已更替（本进程停止续期；后续提交将响亮拒绝或经接管重取）。");
                        return;
                    }

                    // 用最新修订变体：原「Read() 取修订 → TryRenew(捕获修订)」在同胞写入并发下会因修订漂移被误判
                    // lease_stale_generation；续期误判累积到 TTL 即失去租约（会诊重要项）。
                    var renew = _admissionStore!.TryRenewLatest(_admissionLeaseId!, _admissionOwnerEpoch!);
                    if (!renew.Success)
                        _log?.Invoke("[任务中心] 仲裁租约续期被拒（" + renew.Reason + "）——后续提交将响亮拒绝。");
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { /* 锁争用：下一周期再试（单次操作持锁极短） */ }
                catch (Exception ex)
                {
                    _log?.Invoke("[任务中心] 仲裁租约续期异常（下一周期再试）：" + ex.Message);
                }
            }
        });
    }

    /// <summary>
    /// **一键「迁移演练」（R5.8 §21.4 入口）**：在**助手数据根**下的独立目录（`migration-rehearsal/`）跑
    /// **事务组件模拟演练**并返回报告；**助手数据根位置本身不是隔离证明**——写入前须由真实 User 根来源
    /// 与新建独占演练根做重叠/链接校验，无法证明时**保守拒绝**。
    /// **语义限定**：不代表真实引用更新/激活已接线，也不执行真实 User 目录切换（须 owner 另行下令）。
    /// </summary>
    public MigrationRehearsalReport RunMigrationRehearsal()
    {
        string rehearsalRoot;
        string? userRoot;
        try
        {
            var root = _admissionRoot ?? Directory.GetParent(_runsDirPath ?? "")?.FullName ?? _runsDirPath ?? Path.GetTempPath();
            rehearsalRoot = Path.Combine(root, "migration-rehearsal");
            userRoot = UserConfigRootProvider?.Invoke();
        }
        catch (Exception ex)
        {
            var refusal = new MigrationRehearsalReport
            {
                Success = false,
                RehearsalRoot = "",
                Steps = [new MigrationRehearsalStep("独立根校验", false,
                    "真实 User 配置根来源调用或演练根解析失败（保守拒绝）：" + ex.GetType().Name)],
            };
            TryLog("[任务中心] 迁移演练已拒绝：" + refusal.Summary);
            return refusal;
        }
        if (string.IsNullOrWhiteSpace(userRoot))
        {
            // **保守拒绝**：无法证明与真实 User 目录隔离时，不得在可能位于 User 下的目录写演练产物。
            var refusal = new MigrationRehearsalReport
            {
                Success = false,
                RehearsalRoot = "",
                Steps = [new MigrationRehearsalStep("独立根校验", false, "真实 User 配置根未知（未注入路径来源）——保守拒绝演练")],
            };
            TryLog("[任务中心] 迁移演练已拒绝：" + refusal.Summary);
            return refusal;
        }

        var report = MigrationRehearsal.Run(rehearsalRoot, userConfigRoot: userRoot);
        TryLog("[任务中心] 迁移演练：" + report.Summary + "（演练根：" + report.RehearsalRoot + "）");
        return report;
    }
    /// <summary>仲裁事实快照（接缝优先；生产=BGI 控制面快照——缺失即未知，不解释为空闲）。</summary>
    private ArbitrationFacts CurrentArbitrationFacts()
    {
        // §4.2a 准入读取规则（**部分实现**）：当前合并 BGI 快照 ∪ **external-start-ledger 已受理未终结记录**；
        // queued 不因运行快照为空而失去占用意义；台账损坏/读取失败＝**不推导空闲**（按事实未知保守拒绝）。
        // 该合并对**事实接缝与生产事实面一律生效**（接缝只覆盖 BGI 快照来源，不豁免台账占用）。
        // **如实限定**：本方法**未**读取运行台账，「∪ 运行台账」尚未由本处实现；占用被压成布尔值，不足以区分
        // §6.2 的原生/托管占用与重试资格。副作用：旧未终结记录/损坏文件/锁争用会对**所有生产准入**新增拒绝
        // （合同要求的保守方向，但属可用性影响，且读取原因当前未保留）。
        var (ledgerOccupied, ledgerUnknown) = ReadExternalStartLedgerOccupancy();
        if (_admissionSeams is { } s)
        {
            var occupied = s.OccupiedProvider?.Invoke() ?? s.Occupied ?? false;
            return new ArbitrationFacts
            {
                F11Active = s.F11Active ?? false,
                ExecutionOccupied = occupied || ledgerOccupied,
                // §12.3 M1③（[批次四十四 会诊阻断项处置]）：归属**只认精确来源事实**。BGI 控制面快照当前只给
                // 「是否有任务在跑」这一个布尔值，`_drives` 包含关系**不能证明**该占用确实来自本候选所属 run
                // （原生/外部任务在跑、多个自有驱动并存时都会误判）⇒ **生产不填归属＝一律不豁免（fail-closed）**。
                // 夹具注入的归属**不得覆盖外部启动台账占用**（台账有未终结记录时一律不给归属）。
                OwnInFlightRunBindings = ledgerOccupied ? null : s.OwnInFlightRunBindingsProvider?.Invoke(),
                ExecutionFactsUnknown = (s.FactsUnknown ?? false) || ledgerUnknown,
                // [R5 批次 4／A6] 占用者事实：接缝的占用是**测试事实**（无 BGI 身份）⇒ 如实标注"无身份"，
                // 若接缝声明事实未知则直接未知。绝不把接缝事实伪装成可核验的 BGI 占用者。
                // 接缝态只表达"测试声称的占用/未知"，**不**回读实况快照（避免新旧事实混用与纪元旁路）：
                // 未声明占用即测试意义上的空闲；声明占用恒为"无身份"（不可被抢占）。
                RunningOccupant = (s.FactsUnknown ?? false) || ledgerUnknown
                    ? RunningOccupantFacts.Unknown("facts_unknown")
                    : occupied || ledgerOccupied
                        ? new RunningOccupantFacts
                        {
                            State = OccupantFactsState.Occupied,
                            HasTrustedIdentity = false,
                            Reference = "test_seam_occupied",
                        }
                        : RunningOccupantFacts.Idle(),
            };
        }

        var status = _statusSnapshotProvider?.Invoke();
        var currentEpoch = CurrentBgiEpoch();
        var statusEpochMatches = status is not null
            && !string.IsNullOrWhiteSpace(currentEpoch)
            && string.Equals(status.TaskStatusBgiEpoch, currentEpoch, StringComparison.Ordinal);
        return new ArbitrationFacts
        {
            ExecutionOccupied = status?.TaskRunning == true || ledgerOccupied,
            // §12.3 M1③（[批次四十四 会诊阻断项处置]）**生产恒不填归属**：现有权威事实源（`ControlStatus.TaskRunning`
            // 单一布尔 + 外部启动台账占用）**无法证明**该占用归属本候选所属 run——按「归属不可证明即不豁免」
            // （fail-closed）处置，缺口登记为「控制面需暴露带来源的占用事实（runBinding/jobId/来源）」，
            // 属 R5.8 真实入口层前置；在生产节点改道门验收前本项不影响任何生产路径（门仍关闭）。
            OwnInFlightRunBindings = null,
            // 查询失败/响应缺 running 字段时 MainViewModel 仍会构造用于 UI 的默认 TaskRunning=false；
            // 该展示默认值不是空闲证据。超过 20 秒或 BGI 进程纪元变化也必须待核查。
            ExecutionFactsUnknown = status is null || !status.HasFreshTaskStatus(DateTimeOffset.UtcNow)
                || !statusEpochMatches || ledgerUnknown,
            // [R5 批次 4／A6] 生产占用者事实（单一事实点）：身份/级别/优先级不知道的一律留空；
            // 注意 statusEpochMatches=false（旧纪元快照）⇒ FromStatus 只按新鲜度判定，故此处显式降级为未知。
            RunningOccupant = ResolveOccupantLevels(
                RunningOccupantFacts.FromStatus(status, ledgerOccupied, ledgerUnknown,
                    bgiEpochVerified: statusEpochMatches),
                status),
        };
    }

    /// <summary>
    /// [R5 批次 5／A6 第二步] 占用者**级别事实**解析（唯一路径）：BGI `executionRunId` → 运行台账
    /// （`WireRunId` 唯一命中）→ 该 run 的流程级登记操作（`RunBinding = RunId`）候选快照 → Tier/Priority。
    /// 任一环节缺失、多命中或候选冲突 ⇒ 保持未知；读台账/租约失败也保持未知（**不得**凭空推断归属）。
    /// `HighestClass` 目前**没有受信来源**（自报 key 不作证明）⇒ 恒 null（残余，见 §24.101）。
    /// </summary>
    private RunningOccupantFacts ResolveOccupantLevels(RunningOccupantFacts occupant, ControlStatus? status)
    {
        if (occupant.State != OccupantFactsState.Occupied || !occupant.HasTrustedIdentity) return occupant;
        if (status?.CurrentExecution is not { } execution) return occupant;

        try
        {
            // [ev-1 会诊重要项修复] 未组装判定先于台账读取：避免「门面未组装＋台账读取故障」被误记为
            // 「解析失败」（本分支合同＝原样返回、无解析留痕），也使未组装态不承担台账读取/争用等待成本。
            var read = _admissionStore?.Read();
            if (read is null)
            {
                return occupant; // 仲裁面未组装的接缝态：级别未知
            }
            if (read.Status != ArbitrationLeaseStatus.Valid)
            {
                // 非 Valid（Absent/Expired/Corrupt/Unsupported）不得据其内容推级别；如实留痕并保持未知。
                TryLog($"[任务中心] 占用者级别事实未知：租约读取状态={read.Status}（{read.Detail ?? "无明细"}）");
                return occupant;
            }
            // [ev-1 会诊重要项修复②] 台账读取后移到非 Valid 判定之后：租约已判非 Valid 时，
            // 运行台账故障不得把留痕改写成「解析失败」（与未组装判定前置同族，第 7 轮会诊）。
            // [批次 21／EV1-R1] 台账存在未解析记录（UnknownFiles 非空）⇒ 「WireRunId 唯一命中」只在可解析
            // 子集成立，损坏的同 WireRunId 记录会被静默跳过、把歧义掩盖成唯一命中（非保守方向；
            // owner ev1 裁决选项 a 归占用者级别接线批修复）：整组级别解析按未知收敛＋留痕，
            // 不得据被静默缩减的子集推级别（预填级别事实保留，不覆盖）。
            var runSnapshot = _runs.ListWithIntegrity();
            if (runSnapshot.UnknownFiles.Count > 0)
            {
                TryLog("[任务中心] 占用者级别事实未知：运行台账存在未解析记录（"
                    + string.Join("、", runSnapshot.UnknownFiles) + "），唯一命中判定拒绝在缩减子集上进行");
                return occupant;
            }
            var runs = runSnapshot.Records;
            var operations = read.File?.Handoff?.Operations;
            var level = OccupantLevelResolver.Resolve(execution.RunId.ToString("N"), runs, operations);
            if (!level.Reference.StartsWith("resolved_", StringComparison.Ordinal))
            {
                // 级别解析未命中（缺运行记录/多命中/候选冲突等）：留痕但保持未知。
                TryLog($"[任务中心] 占用者级别事实未知：{level.Reference}（执行运行={execution.RunId:N}）");
            }
            return occupant.WithLevelFacts(level.Tier, level.Priority, level.HighestClass);
        }
        catch (Exception ex)
        {
            // 台账/租约读取失败 ⇒ 级别事实未知（保守），不影响既有占用布尔语义。
            TryLog("[任务中心] 占用者级别事实解析失败（按未知处理）：" + ex.Message);
            return occupant;
        }
    }

    /// <summary>外部启动台账占用判定（§4.2a）：已受理未终结（AcceptedPendingExecution/Running）＝占用；读取失败＝事实未知。</summary>
    private (bool Occupied, bool Unknown) ReadExternalStartLedgerOccupancy()
    {
        try
        {
            var root = _admissionRoot ?? Directory.GetParent(_runsDirPath ?? "")?.FullName;
            if (string.IsNullOrEmpty(root)) return (false, false);
            var read = new ExternalStartLedger(root).Read();
            if (!read.Valid) return (false, true); // 损坏/读取失败：保守按事实未知（不推导空闲）
            var entries = read.File?.Entries;
            if (entries is null) return (false, false);
            return (entries.Any(e => !string.IsNullOrEmpty(e.SubmissionIdentity)
                                     && e.State is LedgerEntryState.AcceptedPendingExecution or LedgerEntryState.Running),
                false);
        }
        catch (Exception)
        {
            return (false, true);
        }
    }

    /// <summary>当前 BGI epoch（只比较不重写；缺失=空串——就绪守卫先行，实践中非空）。</summary>
    private string CurrentBgiEpoch()
    {
        if (_admissionSeams?.Epoch is { } e) return e;
        var se = _clientAccessor()?.ServerEpoch;
        return se is null ? "" : $"{se.ProcessId}:{se.StartTicksUtc}";
    }

    /// <summary>E1 面板启动统一仲裁面入口（冻结稿 §0 链路：无副作用解析→仲裁→锁内校验→落盘→锁外发送→对账→台账→关闭）。</summary>
    private async Task<HostActionResult> SubmitFlowStartViaAdmissionAsync(string workflowId, WorkflowSnapshot snapshot,
        string explicitIntentId, long explicitIntentTimestamp)
    {
        // 同流程互斥前置（既有 R4 合同保留于无副作用解析段；跨流程并发由仲裁面全序裁决）
        lock (_gate)
        {
            var sameFlow = _runs.List().Where(r => r.WorkflowId == workflowId).ToList();
            if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                return HostActionResult.Unavailable("该流程已有活动运行（同流程同时只允许一个运行）");
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HostActionResult.Unavailable("该流程存在结果不确定（Unknown）的运行，需先对账再启动");
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return HostActionResult.Unavailable("该流程运行正在启动/驱动中");
        }

        // 发送前固定 candidateId→runId（§3.2：绑定不可改写）。预建 Planned 记录无执行副作用；未受理即终态化清理留痕。
        WorkflowStopAuthorityRecord? authority;
        try
        {
            authority = await CreateRunner(_clientAccessor()).AcquireExplicitIntentStopAuthorityAsync(
                explicitIntentId, explicitIntentTimestamp, _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { return HostActionResult.Unavailable("停止权威未确认：" + ex.Message); }
        var run = _runs.CreateRun(workflowId, snapshot.Revision, note: "仲裁受理预备（R5.2 E1：未受理即终态化清理）",
            stopAuthority: authority);

        // §7.1-1 冻结合同「F11 判定先于租约获取」：本预检必须早于 EnsureAdmissionFacadeAsync——门面组装会执行
        // EnsureOwnership/RecoverAfterRestart/心跳与重试窗口扫描（均属租约/责任副作用）。预建 Planned 运行属 RunStore
        // 侧、非租约副作用，且 B2-α 已签署「F11 阻断也终态化清理留痕」，故本预检置于其后。
        // 门面内锁内复核保留（双保险，竞态窗口内激活同样阻断占位与发送）。
        if (CurrentArbitrationFacts().F11Active)
        {
            CleanupRejectedFlowRun(run, "F11 独立停止闸门激活（租约零副作用）");
            return HostActionResult.Unavailable("F11 独立停止闸门激活（未发生租约副作用）");
        }

        ArbitrationAdmissionService facade;
        try
        {
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            facade = _admission!;
        }
        catch (OperationCanceledException)
        {
            CleanupRejectedFlowRun(run, "仲裁面初始化已取消（宿主退出，未发送）");
            return HostActionResult.Unavailable("任务中心宿主正在退出，仲裁面初始化已取消（未产生租约副作用）");
        }
        catch (Exception ex)
        {
            CleanupRejectedFlowRun(run, "仲裁面初始化失败（未发送）：" + ex.Message);
            return HostActionResult.Unavailable("仲裁面初始化失败（未产生租约副作用）：" + ex.Message);
        }

        // 会诊 P2 处置：预建 Planned 运行已存在，此后任何异常都必须终态化清理，否则 Planned 僵尸会持续占用
        // 同流程活动态检查、把后续启动一路拒绝到重启恢复为止。
        try
        {
            facade.SweepExpiredRetryWindows();
        }
        catch (Exception ex)
        {
            var cleaned = CleanupRejectedFlowRun(run, "仲裁面维护扫描异常（未发送）：" + ex.Message);
            return HostActionResult.Unavailable(cleaned
                ? "仲裁面维护扫描异常（未产生发送）：" + ex.Message
                : "仲裁面维护扫描异常且预建运行清理失败（未产生发送，该流程在重启对账前不可再启动）：" + ex.Message);
        }

        var request = new AdmissionRequest
        {
            Namespace = "manual",
            Kind = AdmissionKind.Create,
            SourceDetail = "ui:panel:start",
            RunBinding = run.RunId,
            OperationType = OperationType.FlowRegistration, // §24.17：E1 面板启动＝流程登记（可信调用位置）
            Candidate = new ArbitrationCandidate
            {
                Scope = $"bgi:local:{CurrentBgiEpoch()}",
                Namespace = "manual",
                WorkflowId = workflowId,
                TriggerOccurrenceId = "manual:panel:{requestIdentity}", // I3：占位符由门面身份分配后回填
                ResourceRef = $"flow:{workflowId}",
                Intent = "start",
                // R5.4 IP1：结构性层级由根级触发器决定（trigger.timeFixed => Fixed；其余 => Plan）
                Tier = TaskCenterMechanismPolicy.TierOfTrigger(
                    snapshot.Document.Triggers.Count > 0 ? snapshot.Document.Triggers[0].Kind : null),
            },
        };

        AdmissionResult result;
        try
        {
            result = await facade.SubmitAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CleanupRejectedFlowRun(run, "仲裁面异常：" + ex.Message);
            return HostActionResult.Unavailable("仲裁面异常：" + ex.Message);
        }

        if (result.Kind == AdmissionResultKind.Accepted)
            return HostActionResult.Registered($"已受理启动（流程「{snapshot.Document.Name}」，运行 {run.RunId}）");

        // **[批次 14／D1]** WaitLocally＝**确定结论**（零发送、已登记本地持久等待）：既不是「发送已尝试、结果不可考」
        // （不得按 Reconciling 悬置观察），也不是终局未受理（**不得**按下面统一清理把运行终态化成 Cancelled——
        // 那会把「等待中」记录成「已拒绝取消」，属事实改写）。本批该值**无生产方**，分支为显式闭环、不改既有行为。
        if (result.Kind == AdmissionResultKind.WaitLocally)
            return HostActionResult.Unavailable("已登记本地持久等待（未获准入前零发送，不进 BGI 执行队列）：" + result.Detail);

        // Reconciling=发送已尝试但结果不可考：运行记录不臆断取消（驱动或在飞），留待既有收敛/对账；
        // 其余未受理（确定未发送/未获选/拒绝/闸门）一律终态化清理留痕。
        if (result.Kind != AdmissionResultKind.Reconciling)
            CleanupRejectedFlowRun(run, $"仲裁未受理（{result.Kind}/{result.ReasonCode}）");
        return HostActionResult.Unavailable(result.Kind switch
        {
            AdmissionResultKind.F11Blocked => "F11 独立停止闸门激活（未产生任何租约副作用）",
            // **[批次 14／D1 显式闭环]** `WaitLocally` 必须命中显式分支：不得落进 `_ =>` 通用「仲裁拒绝」文案
            // （会把「已登记等待」表述成「被拒绝」，属结论改写）。本批该值无生产方，不改既有行为。
            AdmissionResultKind.WaitLocally => "已登记本地持久等待（未获准入前零发送，不进 BGI 执行队列）：" + result.Detail,
            AdmissionResultKind.Reconciling or AdmissionResultKind.NeedReconcile => "执行事实待对账，已保守拒绝：" + result.Detail,
            AdmissionResultKind.NeedPreemptConfirm => "BGI 执行占用中（需先停止在跑任务；抢占确认归后续阶段）",
            AdmissionResultKind.RetryableRejected => "提交被确定拒绝（窗口内可重试）：" + result.Detail,
            AdmissionResultKind.TerminalRejected => "提交被终局拒绝：" + result.Detail,
            AdmissionResultKind.NotSelected => "并发仲裁未获选：" + result.Detail,
            _ => $"仲裁拒绝（{result.Kind}/{result.ReasonCode}）：{result.Detail}",
        });
    }

    /// <summary>锁外发送分派（门面回调）：E1 流程启动=驱动已受理运行；其他形状=Unknown 保守待对账（归 B2-β/γ）。</summary>
    private async Task<SendOutcome> DispatchViaHostAsync(SubmissionDispatch d)
    {
        if (_admissionSeams?.SenderOverride is { } over) return await over(d).ConfigureAwait(false);
        if (string.Equals(d.Intent, "resume", StringComparison.Ordinal))
            return await DispatchResumeViaHostAsync(d).ConfigureAwait(false); // E2 恢复专用（B2-β）
        // R5.2 B2-γ 第 3 步（§13.10 A2）：**节点执行操作**走后继专用分派（候选带节点出现身份）；
        // 非节点且非流程启动的未知形状仍按 B2-α 口径保守 Unknown。
        if (!string.IsNullOrEmpty(d.Candidate.NodeId))
            return await DispatchSuccessorViaHostAsync(d).ConfigureAwait(false);
        // B3（E3/E4/E5）：外部启动操作——**仅**在获准后执行适配层既有启动实现（进程内上下文缺失＝响亮拒绝，
        // 绝不退回直通启动；那会绕开占位/Pending/固定纪元校验）。
        if (d.ProcessLocalContext is ExternalStartContext ext)
            return await DispatchExternalStartViaHostAsync(ext, d.CallerToken).ConfigureAwait(false);
        if (!d.ResourceRef.StartsWith("flow:", StringComparison.Ordinal))
            return new SendOutcome.Unknown("unsupported_dispatch_shape_b2a");

        // runBinding 反查（消费权威=租约 Operations；不凭内存映射——崩溃窗不丢绑定）
        var read = _admissionStore!.Read();
        var op = read.File?.Handoff?.Operations?.FirstOrDefault(
            o => string.Equals(o.RequestIdentity, d.RequestIdentity, StringComparison.Ordinal));
        if (op?.RunBinding is not { } runId)
            return new SendOutcome.Unknown("run_binding_missing");

        var workflowId = d.Candidate.WorkflowId;
        lock (_gate)
        {
            if (_shutdown) return new SendOutcome.Rejected("host_shutdown", false, "host:shutdown");
            if (CapabilityBlockReason() is { } cap) return new SendOutcome.Rejected("capability_blocked", false, "host:" + cap);
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return new SendOutcome.Rejected("task_running", true, "host:flow_inflight"); // 同流程在飞=可重试拒绝（门面派生窗口）
            _reservedWorkflows.Add(workflowId);
        }

        WorkflowRunner runner;
        try
        {
            runner = CreateRunner(_clientAccessor());
        }
        catch (Exception ex)
        {
            lock (_gate) _reservedWorkflows.Remove(workflowId);
            return new SendOutcome.Rejected("launch_error", false, "host:" + ex.GetType().Name);
        }

        var launch = LaunchDrive(workflowId, runner,
            cts => runner.StartExistingRunAsync(runId, cts.Token),
            $"已受理启动（流程 {workflowId}）", runId);
        // LaunchDrive 返回 Unavailable 的唯一可达路径=关闭竞态册外观察（驱动或在飞——异步入口异常一律入 Task 由观察器收敛）：
        // 受理与否不可考=Unknown 保守待对账（孤儿观察器按既有合同收敛运行记录；不臆断拒绝防双跑）。
        return launch.Status != HostActionStatus.Unavailable
            ? new SendOutcome.Accepted("host:drive_registered", runId)
            : (SendOutcome)new SendOutcome.Unknown("host:orphaned_mid_launch");
    }

    /// <summary>
    /// 锁外发送分派（E2 恢复，B2-β）：runBinding 反查租约 Operations（消费权威）→运行台账核对→LaunchDrive ResumeAsync。
    /// 受理=驱动登记（与 E1 同口径）；LaunchDrive Unavailable=关闭竞态册外观察（驱动或在飞）→Unknown 不臆断拒绝。
    /// </summary>
    private async Task<SendOutcome> DispatchResumeViaHostAsync(SubmissionDispatch d)
    {
        var read = _admissionStore!.Read();
        var op = read.File?.Handoff?.Operations?.FirstOrDefault(
            o => string.Equals(o.RequestIdentity, d.RequestIdentity, StringComparison.Ordinal));
        if (op?.RunBinding is not { } runId)
            return new SendOutcome.Unknown("run_binding_missing");
        var run = _runs.Load(runId);
        if (run is null)
            return new SendOutcome.Rejected("run_record_missing", false, "host:runstore"); // 台账缺失=本地权威确定拒绝
        if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused
                                    or WorkflowRunState.LocalWaitParking)) // [批次 20／Wave3／C11=(a)] 停驻运行可恢复重驱
            return new SendOutcome.Rejected("run_state_changed", false, "host:runstore"); // 准入→发送窗口状态已变（本地权威）

        var workflowId = run.WorkflowId!;
        lock (_gate)
        {
            if (_shutdown) return new SendOutcome.Rejected("host_shutdown", false, "host:shutdown");
            if (CapabilityBlockReason() is { } cap) return new SendOutcome.Rejected("capability_blocked", false, "host:" + cap);
            // 会诊 P1 处置：E2 前置互斥检查在门面登记之前且不预留，与本次预留之间存在窗口——窗口内同流程其他运行
            // 可能已进入活动态（如另一 Interrupted 运行先行恢复后转 Paused/Unknown）。故在预留临界区内对台账原子再复核：
            // 目标仍可恢复 + 同流程无其他活动态/Unknown，任何一项不成立即响亮拒绝、不预留、不驱动。
            var fresh = _runs.Load(runId);
            if (fresh is null) return new SendOutcome.Rejected("run_record_missing", false, "host:runstore");
            if (fresh.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused
                                    or WorkflowRunState.LocalWaitParking)) // [批次 20／Wave3／C11=(a)] 停驻运行可恢复重驱
                return new SendOutcome.Rejected("run_state_changed", false, "host:runstore"); // 准入→发送窗口状态已变（本地权威）
            var siblings = _runs.List().Where(r => r.WorkflowId == workflowId && r.RunId != runId).ToList();
            if (siblings.Any(r => ActiveStates.Contains(r.State)))
                return new SendOutcome.Rejected("same_flow_active_run", false, "host:flow_conflict");
            if (siblings.Any(r => r.State == WorkflowRunState.Unknown))
                return new SendOutcome.Rejected("same_flow_unknown_run", false, "host:flow_conflict");
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return new SendOutcome.Rejected("task_running", true, "host:flow_inflight"); // 同流程在飞=可重试拒绝
            _reservedWorkflows.Add(workflowId);
        }

        WorkflowRunner runner;
        try
        {
            runner = CreateRunner(_clientAccessor());
        }
        catch (Exception ex)
        {
            lock (_gate) _reservedWorkflows.Remove(workflowId);
            return new SendOutcome.Rejected("launch_error", false, "host:" + ex.GetType().Name);
        }

        await Task.CompletedTask.ConfigureAwait(false); // 发送本体为同步登记（与 E1 分支同构，保留异步签名）
        var launch = LaunchDrive(workflowId, runner,
            cts => runner.ResumeAsync(runId, cts.Token),
            $"已受理恢复（运行 {runId}，游标身份重定位）", runId);
        return launch.Status != HostActionStatus.Unavailable
            ? new SendOutcome.Accepted("host:drive_registered", runId)
            : (SendOutcome)new SendOutcome.Unknown("host:orphaned_mid_launch");
    }

    /// <summary>
    /// E2 恢复统一准入入口（冻结稿 §5.1：恢复专用准入边界——不排序不产候选；面板恢复/启动移交 resume 共用）。
    /// 恢复拒绝不动原运行记录（保持 Interrupted/Paused 可再恢复——无预建记录需清理，与 E1 预建清理镜像对称）。
    /// </summary>
    private async Task<HostActionResult> SubmitResumeViaAdmissionAsync(WorkflowRunRecord run, string sourceDetail)
    {
        // §7.1-1 同口径：恢复入口同样先判 F11，再组装门面（理由见启动入口注释）。
        if (CurrentArbitrationFacts().F11Active)
            return HostActionResult.Unavailable("F11 独立停止闸门激活（未发生租约副作用）");

        // §5.1／AMD-1-5（**P28 于 G4a 落地后收口**）：
        // ①**有已登记来源时一律继承该 run 的固定 Scope**——不得用 `CurrentBgiEpoch()` 重新构造：那会让 epoch 变化后的
        //   恢复被绑到**新纪元**（等于换身份），违反「epoch 变化→拒绝或对账，**不重写既有身份**」（I-1）。
        // ②**无已登记来源时一律响亮拒绝**——AMD-1-5 第三条：缺固定来源**不得**读当前 epoch 补造。
        //   **[批次四十五 第三轮验证会诊处置] 顺序纪律**：本解析必须**早于门面初始化**（初始化会建目录、获取/接管
        //   租约、执行恢复扫描并启动心跳＝租约副作用），因此这里用**只读**租约实例（`Read()` 不建目录、不建文件）
        //   或运行台账回落解析来源；命中才继续走后续链路。
        var inheritedScope = TryGetAdmissionScopeForResume(run.RunId!, run.WorkflowId);
        if (inheritedScope is null)
            return HostActionResult.Unavailable(
                "恢复缺少已登记固定来源（无面板流程登记且无移交受理登记 Scope）——按 AMD-1-5 第三条不签发、不发送"
                + "（且未产生任何租约副作用）；历史遗留运行需显式处置（不得读当前 epoch 补造）。");
        var resumeScope = inheritedScope;

        ArbitrationAdmissionService facade;
        try
        {
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            facade = _admission!;
        }
        catch (OperationCanceledException)
        {
            return HostActionResult.Unavailable("任务中心宿主正在退出，仲裁面初始化已取消（未产生任何副作用）");
        }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable("仲裁面初始化失败（未产生任何副作用）：" + ex.Message);
        }

        facade.SweepExpiredRetryWindows();

        // 同流程互斥前置（无副作用解析段；恢复目标自身排除——与旧临界区同口径）
        lock (_gate)
        {
            var sameFlow = _runs.List().Where(r => r.WorkflowId == run.WorkflowId && r.RunId != run.RunId).ToList();
            if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                return HostActionResult.Unavailable("该流程已有其他活动运行（同流程同时只允许一个运行）");
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HostActionResult.Unavailable("该流程存在结果不确定（Unknown）的其他运行，需先对账再恢复");
            if (_reservedWorkflows.Contains(run.WorkflowId!) || _drives.ContainsKey(run.WorkflowId!))
                return HostActionResult.Unavailable("该流程已有运行正在驱动（禁止双驱动）");
        }

        var result = await facade.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = sourceDetail,
            RunId = run.RunId!,
            WorkflowId = run.WorkflowId!,
            RestoreBranch = run.State == WorkflowRunState.Paused ? "paused-continue" : "interrupted-relocate",
            Scope = resumeScope,
        }).ConfigureAwait(false);

        if (result.Kind == AdmissionResultKind.Accepted)
            return HostActionResult.Registered($"已受理恢复（运行 {run.RunId}，游标身份重定位）");
        return HostActionResult.Unavailable(result.Kind switch
        {
            AdmissionResultKind.F11Blocked => "F11 独立停止闸门激活（未产生任何租约副作用）",
            AdmissionResultKind.RetryableRejected when result.ReasonCode == "execution_occupied"
                => "BGI 执行占用中，恢复未发送（占用解除后可重新恢复）",
            AdmissionResultKind.RetryableRejected => "恢复被确定拒绝（窗口内可重试）：" + result.Detail,
            AdmissionResultKind.Reconciling or AdmissionResultKind.NeedReconcile => "执行事实待对账，恢复已保守停驻：" + result.Detail,
            AdmissionResultKind.TerminalRejected => "恢复被终局拒绝：" + result.Detail,
            AdmissionResultKind.WaitLocally => "恢复未发送，已登记本地持久等待（未获准入前零发送，不进 BGI 执行队列）：" + result.Detail,
            // **[批次 14／D1 显式闭环]** 等待＝**确定未发送**（已登记本地持久等待），与「恢复准入拒绝」分开表述；
            // **不得**删除下面的 `_ =>`：`Error`/`NotSelected`/`NeedPreemptConfirm`/`Cancelled`/`ExecutionFailed`
            // 等既有可达值仍需保守文案（删掉会让这些值抛 `SwitchExpressionException`＝当前可达回归）。
            _ => $"恢复准入拒绝（{result.Kind}/{result.ReasonCode}）：{result.Detail}",
        });
    }

    /// <summary>未受理预建运行清理（终态化留痕：历史可见「尝试被拒」，不留 Planned 僵尸）。</summary>
    /// <summary>
    /// 未受理预建运行终态化清理（留痕）。返回是否清理成功——清理失败（磁盘写失败）会留下 Planned 记录并
    /// 持续占用同流程活动态检查，属必须上抛的后果，故不再无条件吞掉（会诊四轮 P2）。
    /// </summary>
    private bool CleanupRejectedFlowRun(WorkflowRunRecord run, string reason)
    {
        try
        {
            run.State = WorkflowRunState.Cancelled;
            run.Note = (run.Note is null ? "" : run.Note + "；") + reason;
            _runs.Update(run);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                _log?.Invoke("[任务中心] 未受理运行清理失败（将阻塞同流程后续启动，需重启对账）：" + ex.Message);
            }
            catch
            {
                // 日志异常不得掩盖清理失败的事实。
            }

            return false;
        }
    }

    /// <summary>
    /// R5.2 B2-γ 第 3 步（§13.10 A）：节点提交的后继结果回传槽。**按完整发送身份**（submissionIdentity）存取，
    /// 不得用业务身份做键（同 attempt 可有多 sendSeq）；装饰器据此返回 Runner 所需的 `BoundarySubmitResult`。
    /// </summary>
    /// <summary>
    /// **节点后继的进程内不可变请求快照**（§13.10 A1/A2）：入队时按 Runner 当时提交的请求**冻结**，
    /// 随获选排队项传到 Sender。**节点经 JSON 往返深拷贝**——不得把可变 `WorkflowNode` 引用当作冻结快照
    /// （排队期间流程定义可能被编辑）。
    /// </summary>
    internal sealed record SuccessorContext(
        WorkflowNode Node, WorkflowNodeOccurrence Occurrence, bool Suppress, int Attempt, string ExpectedSubmissionKey);

    /// <summary>把节点冻结成不可变副本（JSON 往返；失败＝抛错，由调用方保守拒绝，不降级为「用原引用」）。</summary>
    internal static WorkflowNode FreezeNode(WorkflowNode node)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(node);
        return System.Text.Json.JsonSerializer.Deserialize<WorkflowNode>(json)
               ?? throw new InvalidOperationException("节点冻结失败（反序列化为空）。");
    }

    /// <summary>
    /// **准入阶段请求内容指纹**（G5）：对「冻结节点内容 ＋ 出现身份 ＋ 提交选项 ＋ 提交身份」取 SHA256 前 24 位
    /// 十六进制小写，与运行台账 `WorkflowSubmission.Fingerprint`（线上载荷指纹）风格一致但**语义不同**——
    /// 本指纹在**获得发送许可之前**即可计算（不含 epoch/有效期等占位后才冻结的字段），
    /// 供门面「同 candidateId 不同载荷＝整组拒绝」与续用/占位的载荷一致性复核使用。
    /// </summary>
    internal static string SuccessorPayloadFingerprint(
        WorkflowNode frozenNode, WorkflowNodeOccurrence occurrence, bool suppress, int attempt, string submissionKey)
    {
        var content = System.Text.Json.JsonSerializer.Serialize(new
        {
            node = frozenNode,
            occurrence = new { occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration },
            suppressConfigCompletionAction = suppress,
            attempt,
            submissionKey,
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content)))[..24].ToLowerInvariant();
    }

    /// <summary>在共享身份工厂产物上填充发送注解（<c>C3</c>：注解不参与 <c>BuildStableIdentity</c> 身份组成）。</summary>
    private static ArbitrationCandidate WithSuccessorSendAnnotations(
        ArbitrationCandidate candidate, string? payloadFingerprint, string nodeId)
    {
        candidate.PayloadFingerprint = payloadFingerprint;
        candidate.ResourceRef = "node:" + nodeId;
        candidate.Intent = "start";
        return candidate;
    }

    /// <summary>
    /// **[批次 20／C3] successor 候选的「身份字段」构造唯一权威**。namespace/triggerOccurrenceId 口径
    /// 只此一处定义：后继提交路径（本文件）与等待登记翻译（WorkflowRunner.TryRegisterLocalWait）
    /// **共用本工厂**——两处同改的合同由锚定夹具
    /// <c>SuccessorCandidateComposition_AnchoredToSharedFactory</c> 机械锚定（单侧改口径 ⇒ 夹具红）。
    /// 只含 <c>BuildStableIdentity</c> 消费的 9 个身份字段；发送注解（PayloadFingerprint/ResourceRef/Intent）
    /// 由调用方在工厂产物上另行填充，不参与身份组成。
    /// **scope 语义（如实；R9-F1 更正）**：移交来源运行 ⇒ 取值即运行台账 <c>AdmissionSourceScope</c>
    /// （<see cref="ResolveAdmissionParent"/> 回落支返回的正是该字段）；面板来源运行的权威 scope＝
    /// 租约侧 FlowRegistration 反查（本宿主职责）⇒ 等待登记经 <c>localWaitAdmissionScopeProvider</c>
    /// 注入取得（面板来源**并非没有**权威 scope，只是不在台账字段里）；来源缺供 ⇒ 登记点拒绝登记
    /// （[批次 20／Wave1 R7 重要-2]：空段 scope 身份与提交面不同空间，结构性永不可重入）。
    /// </summary>
    internal static ArbitrationCandidate BuildSuccessorIdentityCandidate(
        string? scope, string workflowId, string runId, string nodeId,
        int occurrence, int loopIteration, int attempt)
    {
        // 工作流节点后继沿用可信启动入口的既有固定映射：普通计划层、优先级 0；最高级标记无受信来源。
        // 明确写入候选，避免实际异步准入依赖 ArbitrationCandidate 的缺省值；同步等待预检复用本候选映射。
        return new ArbitrationCandidate
        {
            Scope = scope,
            Namespace = "successor",
            WorkflowId = workflowId,
            TriggerOccurrenceId = "successor:" + runId,
            RunId = runId,
            NodeId = nodeId,
            Occurrence = occurrence,
            LoopIteration = loopIteration,
            Attempt = attempt,
            Tier = ArbitrationTier.Plan,
            Priority = 0,
        };
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BoundarySubmitResult> _successorSendResults = new();

    /// <summary>
    /// 后继提交准入（§5.2／§13.10 A）：门面先占位，Sender 内再准备与发送；本方法只负责**准入**与**结果映射**。
    /// </summary>
    internal async Task<BoundarySubmitResult> SubmitSuccessorViaAdmissionAsync(WorkflowSubmitRequest request, CancellationToken ct)
    {
        var run = request.Run;
        var occ = request.Occurrence;
        var sub = run.CurrentSubmission;
        // 引擎纪律：意图先行（Runner 已落盘）；身份不符＝可证实未发送
        if (sub is null || sub.Intent != SubmitIntentState.IntentRecorded
            || sub.NodeId != occ.NodeId || sub.Occurrence != occ.Occurrence || sub.LoopIteration != occ.LoopIteration)
            return BoundarySubmitResult.Rejected("提交意图缺失或身份不符（未发送）");
        // §7.1-1：F11 判定先于租约获取
        if (CurrentArbitrationFacts().F11Active)
            return BoundarySubmitResult.Rejected("F11 独立停止闸门激活（未发生租约副作用；未发送）");

        // 【顺序纪律】节点冻结与调用方取消必须在**门面初始化之前**——`EnsureAdmissionFacadeAsync` 会创建目录、
        // 获取/接管租约、执行恢复并启动心跳（均属租约副作用）。会诊重要项：原顺序把冻结放在初始化之后，
        // 使「冻结失败」也带着租约副作用，与「可证实未发送」的表述不符。
        // §13.10 A1：节点深拷贝失败＝可证实未发送地拒绝（此前落通用 catch 被报成 Unknown，语义不符）。
        WorkflowNode frozenNode;
        try
        {
            frozenNode = FreezeNode(request.Node);
        }
        catch (Exception ex)
        {
            return BoundarySubmitResult.Rejected("提交前节点冻结失败（未发送，且未发生任何租约副作用）：" + ex.GetType().Name);
        }

        // 调用方已取消：在任何租约副作用之前就返回（发布发送许可之后不再由此令牌中止——见 §13.11 G7）。
        ct.ThrowIfCancellationRequested();

        // §3.2／§13.10 A1（G4 处置）：游标必须存在且与本次提交的出现身份一致——
        // `CursorRef=null` 会让门面 ⑪b「同游标唯一消费」静默失效（防双跑约束凭空消失）；
        // `CursorRevision` 冻结为**出现轮次代次**（`run.Cursor.LoopIteration`：稳定的出现身份代次，入队冻结、占位按同值比对，
        // 失配不得改读最新值继续发送）。[G4-residual②·本批处置] `RecordRevision` 只作运行**记录版本** CAS，不再充当逻辑消费代次——无关记录更新（Note/停止观察）不再推进消费键；同游标仅 Note 改动后的重放、跨迁区/重启重放由 ⑪b 按稳定出现身份继续拦截。
        if (run.Cursor is not { } runCursor)
            return BoundarySubmitResult.Rejected("运行游标缺失（同一游标唯一消费无从判定；未发送）");
        if (!string.Equals(runCursor.NodeId, occ.NodeId, StringComparison.Ordinal)
            || runCursor.Occurrence != occ.Occurrence
            || runCursor.LoopIteration != occ.LoopIteration)
            return BoundarySubmitResult.Rejected("运行游标与本次提交出现身份不一致（未发送）");
        var cursorRef = $"{runCursor.NodeId}#{runCursor.Occurrence}#{runCursor.LoopIteration}";
        var cursorRevision = (long)runCursor.LoopIteration;

        // G8：进入本轮准入之前，先按运行台账已观察到的节点终态**独立结清**此前节点的 Operation
        // （不等整条 run 终态，避免长流程堆满 32 主槽位）。失败只留痕，不影响本次准入。
        SweepTerminalNodeOperations(run.RunId!);
        var afterSweep = _runs.Load(run.RunId!);
        if (afterSweep is null || afterSweep.CurrentSubmission?.Key != sub.Key || afterSweep.StopAuthority != run.StopAuthority)
            return BoundarySubmitResult.UnknownWith("节点封印后当前提交或停止权威改变，未发送。");
        RunStore.RebaseOnto(run, afterSweep);
        sub = run.CurrentSubmission!;
        cursorRevision = (long)(run.Cursor?.LoopIteration ?? 0);

        // G5：准入阶段**请求内容指纹**——门面把它作为候选载荷指纹落盘，用于
        // ①冲突组内「同 candidateId 不同载荷＝整组拒绝」的判别（空串会让不同载荷被当成同载荷），
        // ②续用（ContinueUse）与占位时的「候选载荷一致」复核。覆盖冻结节点内容＋出现身份＋提交选项＋提交身份。
        var payloadFingerprint = SuccessorPayloadFingerprint(frozenNode, occ, request.SuppressConfigCompletionAction,
            sub.Attempt, sub.Key);

        // [§17 P17／§12.3 交错①] **取得门面锁之前**的只发信号观察点（生产 null＝空操作）：
        // 强制版「首节点抢先」夹具在此固定交错（E1 尚未关闭时节点已到达本入口），并在此刻只读取证
        // 「父责任仍在（未关闭）＋子许可/发送为零」。本回调**不得**改变任何状态。
        if (_admissionSeams?.BeforeSuccessorAdmission is { } beforeSuccessorAdmission)
            await beforeSuccessorAdmission().ConfigureAwait(false);

        ArbitrationAdmissionService facade;
        try { await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false); facade = _admission!; }
        catch (OperationCanceledException) { return BoundarySubmitResult.UnknownWith("宿主退出，仲裁面初始化取消（未发送，待对账）"); }
        catch (Exception ex) { return BoundarySubmitResult.UnknownWith("仲裁面初始化失败（未发送，待对账）：" + ex.GetType().Name); }

        // 授权来源：该 runBinding 已登记操作的固定 Scope（I-1：继承，不重读当前 epoch）
        // §12.3 M1⑤：本处只反查**授权来源（固定 Scope）**；父子绑定由门面在同一权威事务内自行反查写入
        // （**不采信调用方自报**，[批次四十四 会诊重要项处置]）。
        var parentRegistration = TryGetAdmissionParent(run.RunId!, run.WorkflowId);
        var scope = parentRegistration is { } parent ? parent.Scope : null;
        if (scope is null || parentRegistration is null)
        {
            // [纠正·2026-09-21] 原「缺 Scope 时退回直通发送」的临时旁路被会诊否决（ASTRA high 阻断项 1）：
            // 那会凭空建立一条**不受仲裁许可约束的发送入口**（绕过占位/Pending/Submission 冲突与固定授权纪元校验），
            // 与 AMD-1-5 第三条「缺固定 Scope/绑定＝不签发、不发送」直接冲突。
            // 正确处置＝响亮拒绝并留痕（可证实未发送）。缺口本体（启动移交来源未登记固定 Scope/绑定）归
            // §13.11 G4a，须先按 AMD-1-5 的四类来源规则在移交受理处落定来源记录，再启用本路径。
            try
            {
                // 会诊复审：日志委托是外部注入的诊断面，其异常不得改变本方法的结构化返回值。
                _log?.Invoke("[任务中心] 后继提交缺少已登记仲裁授权（运行 " + run.RunId + " 无固定 Scope/绑定）——"
                             + "按 AMD-1-5 第三条拒绝签发（未发送）；缺口见设计稿 §13.11 G4a。");
            }
            catch (Exception)
            {
                // 诊断失败不改变结论：仍按 AMD-1-5 第三条拒绝签发。
            }
            return BoundarySubmitResult.Rejected(
                "无已登记仲裁授权（缺固定 Scope/绑定，按 AMD-1-5 第三条不签发不发送）");
        }

        // G2 并发守卫基线：提交前快照（发送段**只允许**改动 CurrentSubmission 的冻结/受理字段与
        // RecordRevision/UpdatedAt；其余任何字段变化都属并发修改，合并必须拒绝）。
        var baseline = NormalizeVolatile(run);

        AdmissionResult result;
        try
        {
            result = await facade.SubmitAsync(new AdmissionRequest
            {
                Namespace = "successor",
                Kind = AdmissionKind.Create,
                SourceDetail = "runner:successor",
                RunBinding = run.RunId,
                OperationType = OperationType.NodeExecution, // §24.17：后继节点提交＝节点执行（可信调用位置）
                // 线上提交键（§6.1 映射表）：无法确定性推导时显式携带——后继提交的键由引擎派生且随 run 落盘，
                // 显式登记使台账侧可自足重建（PID 未传时台账只有空键）。
                WireSubmitKey = sub.Key,
                // §13.10 A1/A2：入队即冻结「Runner 当时提交的请求」（节点深拷贝＋完整出现身份＋提交选项＋预期提交键），
                // 随获选排队项传到 Sender；Sender 不得再从当前流程定义重建。
                ProcessLocalContext = new SuccessorContext(
                    frozenNode, request.Occurrence, request.SuppressConfigCompletionAction,
                    sub.Attempt, sub.Key),
                CursorRef = cursorRef,
                CursorRevision = cursorRevision,
                // §17 P6：调用方取消令牌**入队冻结**并随获选项传到 Sender ⇒ 发送段可中止在飞发送（取消≠关闭依据）
                CallerToken = ct,
                // [批次 20／C3] 身份字段经共享权威工厂构造（与等待登记翻译同口径，夹具锚定）；
                // 发送注解不参与身份组成，在工厂产物上另行填充。
                Candidate = WithSuccessorSendAnnotations(
                    BuildSuccessorIdentityCandidate(scope, run.WorkflowId, run.RunId,
                        occ.NodeId, occ.Occurrence, occ.LoopIteration, sub.Attempt),
                    payloadFingerprint, occ.NodeId),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BoundarySubmitResult.UnknownWith("仲裁面异常（结果待对账）：" + ex.GetType().Name);
        }

        if (result.Kind == AdmissionResultKind.Accepted)
        {
            // 按**完整发送身份**取回 sender 内真实发送结果（§13.10 A2：**非破坏性读取**——合并调用者共享同一结果，
            // 不得因首个读取者 `TryRemove` 而让其余调用者降级为 Unknown）。取不到＝不猜成功，保守待对账。
            if (result.SubmissionIdentity is { } sid && _successorSendResults.TryGetValue(sid, out var sent))
            {
                // G2 处置（§12.1 重要-3 / M3：Sender 与 Runner 走字段合并或版本守卫）。Sender 内的
                // `PrepareSubmit` 改的是**它自己 load 的那份 run**（冻结纪元/有效期/指纹/发送已尝试/意图一并落盘），
                // 而 Runner 仍持提交前的旧副本；不合并＝Runner 随后 `_runs.Update(run)` 以旧修订冲突
                // （宿主级夹具已实证：RunRecordConflictException → 运行被保守收敛 Unknown）。
                MergeBackAuthoritativeSubmission(run, sub, baseline, copyOutcome: true);
                return sent;
            }
            // 已受理但取不到回执：仍须与权威记录对齐（否则 Runner 的后续写回会以旧修订冲突，结果记录丢失）。
            MergeBackAuthoritativeSubmission(run, sub, baseline, copyOutcome: false);
            return BoundarySubmitResult.UnknownWith("仲裁已受理但未取得发送回执（不猜成功，待对账）");
        }

        // 失败/未知路径同样必须对齐权威记录（会诊实证：不对齐会让 Runner 的旧修订写回撞冲突、结果记录丢失）。
        // 意图语义：该路径由 Runner 按自身语义定（Submitted/Rejected）；但若权威记录已是 Accepted+jobId，
        // 合并会**无条件保留该受理事实**（只允许增强不允许降级，见 MergeBackAuthoritativeSubmission）。
        MergeBackAuthoritativeSubmission(run, sub, baseline, copyOutcome: false);
        return MapAdmissionResultToBoundary(result);
    }

    /// <summary>
    /// **按提交身份把权威落盘事实合并回 Runner 持有的实例**（G2：字段合并＋版本对齐，**不做整体覆盖**）。
    /// 前置：身份一致（同 attempt／同提交键／同节点出现身份）——不一致一律不合并，绝不把另一轮身份写进本实例。
    /// 读盘失败＝不合并（Runner 随后的写回按自身修订判定，保守失败而非臆断成功）。
    /// </summary>
    private void MergeBackAuthoritativeSubmission(WorkflowRunRecord runnerRun, WorkflowSubmission runnerSub,
        string baselineNormalized, bool copyOutcome)
    {
        WorkflowRunRecord? fresh;
        try
        {
            fresh = _runs.Load(runnerRun.RunId);
        }
        catch (Exception)
        {
            return; // 读失败：不合并（不臆断）
        }

        if (fresh?.CurrentSubmission is not { } freshSub) return;
        if (freshSub.Attempt != runnerSub.Attempt
            || !string.Equals(freshSub.Key, runnerSub.Key, StringComparison.Ordinal)
            || !string.Equals(freshSub.NodeId, runnerSub.NodeId, StringComparison.Ordinal)
            || freshSub.Occurrence != runnerSub.Occurrence
            || freshSub.LoopIteration != runnerSub.LoopIteration)
            return; // 身份不符＝另一轮提交，禁止合并

        // **并发守卫（会诊阻断处置）**：版本对齐**只允许**在「除发送段冻结/受理字段与 RecordRevision/UpdatedAt 外，
        // 权威记录与本实例提交前基线**逐字段一致**」时进行。若另有写入者推进过记录，凭「业务身份相同」就接受
        // 任意最新修订是错的——那会把未合并的并发事实在 Runner 随后的整体写回中静默覆盖。
        // 拒绝合并＝Runner 的更新按自身修订判定并响亮冲突（保守 Unknown），绝不静默覆盖他人事实。
        if (!string.Equals(NormalizeVolatile(fresh), baselineNormalized, StringComparison.Ordinal))
        {
            try
            {
                _log?.Invoke("[任务中心] 后继提交合并被拒（运行 " + runnerRun.RunId
                             + "）：权威记录存在发送段之外的并发修改，保守不合并（Runner 写回将按修订冲突响亮失败）。");
            }
            catch (Exception)
            {
                // 诊断失败不改变结论。
            }
            return;
        }

        // 只并发送段冻结/受理相关的字段（其余运行事实保持 Runner 侧权威，不整体覆盖）。
        runnerSub.Epoch = freshSub.Epoch;
        runnerSub.ExpiresAtUtc = freshSub.ExpiresAtUtc;
        runnerSub.Fingerprint = freshSub.Fingerprint;
        runnerSub.OriginalRequestEvidence = freshSub.OriginalRequestEvidence;
        runnerSub.SendAttempted = freshSub.SendAttempted;
        runnerSub.AcceptedSendIdentity = freshSub.AcceptedSendIdentity;
        // [static important candidate · real Sender counterexample] typed rejection discharge facts: the send segment (SendPreparedAsync -> UpdateMergingIf) persists 4 self-owned discharge fields on the typed rejection path. They belong to the same "fields the send segment actually writes" family as the whitelist; not copying them back would let Runner's whole-record write silently restore authoritative discharge facts to stale values (see TaskCenterSuccessorPathGateTests.TypedServerRejection_SelfDischarge_Merged_NotUnknown).
        runnerSub.ServerRejectionEvidence = freshSub.ServerRejectionEvidence;
        runnerSub.ExecutionExitConfirmed = freshSub.ExecutionExitConfirmed;
        runnerSub.ExecutionExitDisposition = freshSub.ExecutionExitDisposition;
        runnerSub.EffectState = freshSub.EffectState;
        // **受理回执只允许增强、不允许被 Runner 的后续整体写回降级**（会诊阻断）：权威记录已是
        // `Accepted + jobId` 时（含 G6「已受理但关闭阶段抛异常」场景）必须一并并入 Runner 实例，
        // 否则版本对齐后 Runner 的旧 `Intent/JobId` 会成功覆盖已确认的受理事实，破坏恢复依据。
        if (freshSub.Intent == SubmitIntentState.Accepted && !string.IsNullOrEmpty(freshSub.JobId))
        {
            runnerSub.Intent = SubmitIntentState.Accepted;
            runnerSub.JobId = freshSub.JobId;
        }
        else if (copyOutcome)
        {
            // 仅「已受理」路径把受理结论（意图/jobId）并入 Runner 实例；失败/未知路径由 Runner 按自身语义定。
            runnerSub.Intent = freshSub.Intent;
            runnerSub.JobId = freshSub.JobId;
        }
        if (freshSub.ObservedTerminal is { } terminal) runnerSub.ObservedTerminal = terminal;
        if (!string.IsNullOrEmpty(fresh.WireRunId)) runnerRun.WireRunId = fresh.WireRunId;
        // 版本对齐：Runner 的后续 `_runs.Update(run)` 以**权威修订**为期望值（否则必撞修订冲突）。
        runnerRun.RecordRevision = fresh.RecordRevision;
    }

    /// <summary>
    /// 归一化「发送段允许变更」的可变字段（RecordRevision／UpdatedAt／CurrentSubmission 的冻结与受理字段），
    /// 供「除发送段外无人改动运行记录」的并发守卫做逐字段比较。**不得**把 `wireRunId`、`state`、`note`、
    /// `nodeOutcomes`、`handoffs`、`cursor` 等运行事实排除在比较之外（那些字段一旦变化即属并发修改）。
    /// </summary>
    private static string NormalizeVolatile(WorkflowRunRecord run)
    {
        var node = System.Text.Json.Nodes.JsonNode
            .Parse(System.Text.Json.JsonSerializer.Serialize(run))!.AsObject();
        node["recordRevision"] = -1;
        node["updatedAt"] = "~";
        if (node["currentSubmission"] is System.Text.Json.Nodes.JsonObject sub)
        {
            // This additive field is absent on pre-freeze/legacy records. Removing it
            // avoids a false conflict from differing JSON property insertion order.
            sub.Remove("originalRequestEvidence");
            // 白名单**只含发送段确实会写的字段**（含类型化拒绝路径的清偿事实，见 SendPreparedAsync）。
            // `recordedAt` **不在**白名单内——`PrepareSubmit`/`SendPreparedAsync` 都不改它；被忽略却又不合并回
            // Runner 的字段，会让 Runner 的整体写回静默恢复旧值（会诊阻断/建议项处置）。`observedTerminal` 同为发送段
            // 类型化拒绝路径会写的字段，必须同列白名单；`MergeBackAuthoritativeSubmission` 另按原事实回拷。
            foreach (var key in new[]
                     {
                         "epoch", "expiresAtUtc", "fingerprint", "sendAttempted", "intent", "jobId",
                         "acceptedSendIdentity",
                         "observedTerminal", "serverRejectionEvidence", "executionExitConfirmed", "executionExitDisposition", "effectState",
                     })
                sub[key] = null;
        }
        return node.ToJsonString();
    }

    /// <summary>
    /// §13.10 A3 三态映射（internal static＝可直接夹具断言）。
    /// [纠正·2026-09-21] 会诊阻断（ASTRA high 阻断项 2）：三态必须按「**结果确定性**」映射，不得按「可否重试」
    /// 映射，也不得用 `_ => Rejected` 兜底——否则「sender 已 Accepted、随后关闭/接管抛异常」会被
    /// `ProcessRoundAsync` 收敛成 `Error`，再被本层**反转为确定拒绝**（事实反转）。
    /// 判据：门面给出**确定结论** → Rejected（本层只转发门面结论，**不**自行断言「一定没发送」）；
    /// **事实不可考**（含 Error）→ Unknown（不猜成功不猜失败）。
    /// 原因码与明细原样保留，供对账与诊断。
    /// </summary>
    internal static BoundarySubmitResult MapAdmissionResultToBoundary(AdmissionResult result)
    {
        return result.Kind switch
        {
            // **[会诊重要项处置]** `RetryableRejected`**单独分流**：门面已按 §3.3 结清并把操作落
            // `RetryableRejected`（责任 `Settled`、`Submission` 关闭、重试窗口派生）⇒ 回映射必须**保留可重试性**
            // （否则「窗口内可经 `RetryAsync` 再入场」的事实会在返回给 Runner 的结果维上丢失）。
            AdmissionResultKind.RetryableRejected
                => BoundarySubmitResult.RejectedWithRetryWindow(
                    "仲裁确定未受理·开重试窗口（" + result.Kind + "/" + result.ReasonCode + "）：" + result.Detail),
            AdmissionResultKind.TerminalRejected
                or AdmissionResultKind.NotSelected or AdmissionResultKind.F11Blocked
                or AdmissionResultKind.NeedPreemptConfirm
                => BoundarySubmitResult.Rejected(
                    "仲裁确定未受理（" + result.Kind + "/" + result.ReasonCode + "）：" + result.Detail),
            AdmissionResultKind.NeedReconcile or AdmissionResultKind.Reconciling
                => BoundarySubmitResult.UnknownWith(
                    "仲裁待对账（事实不可考，不猜成功也不猜失败）：" + result.Kind + "/" + result.ReasonCode
                    + "：" + result.Detail),
            // **[批次 14／D1]** `WaitLocally`**单独分流**：门面给出**确定结论**——本笔**未进入发送面、零发送**、
            // 已登记本地持久等待。因此**既不** `Uncertain`（不是「事实不可考」），**也不**开重试窗口
            // （`Retryable` 会给出「可重发」语义 ⇒ 等于绕过「重新走一次完整准入」的要求），`JobId` 必须为空。
            // 语义与「确定未受理」分开表述：本批该值**无生产方**；若把它落进 `_ =>` 未知兜底，等于把
            // 「已确定的零发送等待」改写成「事实不可考」（事实改写），故必须先命中本分支。
            AdmissionResultKind.WaitLocally
                => BoundarySubmitResult.WaitWith(
                    "仲裁本地持久等待（已确定未发送、零发送；等待项就绪后须重新走完整准入；"
                    + result.Kind + "/" + result.ReasonCode + "）：" + result.Detail),
            _ => BoundarySubmitResult.UnknownWith(
                "仲裁面事实不可考（" + result.Kind + "/" + result.ReasonCode + "）：" + result.Detail
                + "（不臆断未发送，待对账）"),
        };
    }

    /// <summary>
    /// 该 runBinding 的**流程级启动操作**（`Intent=="start"` ＋ 候选无节点身份）所固定的 `Scope`
    /// （I-1：继承首次构造时捕获的 bgiEpoch，只比较不重写）。
    /// [纠正·2026-09-21 会诊 G4] ①**不得取「最新」操作**——最新操作可能是本轮后继/恢复操作，其 Scope 不是该 run
    /// 的授权来源；②也**不能只按最早更新时间判定**——`UpdatedAtUtc` 会被关闭/迁移/恢复改写，且排队或被拒的
    /// 恢复操作同样携带 RunBinding。因此按**来源类型**（流程级 start 操作）判定，再取其中最早的：
    /// 并行/后继/恢复操作（`Intent=="resume"` 或带节点身份）一律不作来源。
    /// 注意：首节点可能在该操作仍处 `Granted/Sending` 时抢先到达（设计明确要求的交错）——故**不以请求状态过滤**。
    /// **不得**把「RunBinding 已绑定 ＋ Scope 非空」读作「授权来源已发布」：这些字段在 **Queued 创建登记阶段**
    /// 就已写入，类型过滤能排除后继/恢复，但**不能证明父授权已签发或已成功接管**（会诊纠正）。
    /// **已知残余（登记为路径启用前置）**：精确的「来源引用/父引用」（AMD-1-3）尚未实现，本判别是**临时**的
    /// （此处只取「当前 `UpdatedAtUtc` 最小的同类操作」）；来源记录被**实际删除**（而非仅迁区）后本查询会查不到
    /// → 按无授权拒绝（保守方向），不得改读当前 epoch 补造。
    /// </summary>
    private string? TryGetAdmissionScope(string runId, string? workflowId) => TryGetAdmissionParent(runId, workflowId)?.Scope;

    /// <summary>
    /// **恢复路径的只读来源解析**（[批次四十五 第三轮验证会诊处置]）：与 `TryGetAdmissionScope` 同判据，
    /// 但使用**只读**租约实例（门面尚未初始化时 `_admissionStore` 为 null；直接读盘构造不产生任何副作用——
    /// `ArbitrationLeaseStore.Read()` 既不改写也不新建文件/目录），失败/不可解析 ⇒ null（调用方响亮拒绝）。
    /// </summary>
    private string? TryGetAdmissionScopeForResume(string runId, string? workflowId)
        => string.IsNullOrEmpty(workflowId)
            ? null
            : ResolveAdmissionParent(TryReadAdmissionSources(), _runs.Load(runId), runId, workflowId)?.Scope;

    private IReadOnlyList<OperationRecord>? TryReadAdmissionSources()
    {
        try
        {
            LeaseHandoffSegment? handoff;
            if (_admissionStore is not null)
                handoff = _admissionStore.Read().File?.Handoff;
            else
            {
                // 门面未初始化：同一租约目录只读读取；目录不存在时 Read 返回 Absent，不创建文件。
                var root = _admissionRoot ?? Directory.GetParent(_runsDirPath ?? "")?.FullName;
                var dir = _arbitrationDir ?? (root is null ? null : Path.Combine(root, "arbitration"));
                handoff = dir is null ? null : new ArbitrationLeaseStore(dir).Read().File?.Handoff;
            }
            return handoff is null
                ? null
                : (handoff.Operations ?? []).Concat((handoff.ArchivedOperations ?? [])
                    .Where(a => a?.Operation is not null).Select(a => a.Operation)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// **父登记反查（§12.3 M1⑤ 父子/首节点绑定）**：按同一 `runBinding` 取该 run 的**流程登记父操作**
    /// （`intent=start`、无节点身份、取最早建立者），返回其 `requestIdentity` 与固定 `Scope`。
    /// **顺序按契约**：缺失固定来源（无记录/`Scope` 空/身份空）⇒ 返回 null——调用方一律**响亮拒绝且不签发**，
    /// 不得改读当前 epoch 或按快照补造（AMD-1-5 第三条）。
    /// **[批次四十四 会诊重要项处置]** 父判据改用门面**同一实现**（严格到操作类型与 `flow:` 来源形状）——
    /// 宿主反查与门面锁内判定不得漂移。
    /// **[批次四十五 G4a]** 来源按**类别分权威**：**面板启动**＝租约中的流程登记操作（`IsFlowRegistrationParent`）；
    /// **启动移交**＝**运行台账的受理登记事实**（`WorkflowRunRecord.AdmissionSourceScope`，随受理**同一次落盘**）。
    /// **会诊处置**：不在租约里另造 `Handoff` 来源记录——那会引入「受理-来源」跨存储窗口、空发送身份的终局错配
    /// 与主槽位长期占用，且其形状无法与调用方伪造的普通操作区分。暂停续行／Interrupted 恢复继承其原始来源；
    /// 缺来源仍**不签发、不发送**（AMD-1-5 第三条）。
    /// **[批次四十四 验证会诊重要项处置]** ①**歧义不得任选**：严格判据命中**恰一条**才返回；0 条或多条 ⇒ null
    /// （多条＝同一 runBinding 出现重复/冲突的流程登记 ⇒ 不签发、不绑定，而不是按时间取最早者）。
    /// ②**必须与本运行 workflow 逐字相等**：否则「workflow B ＋ `flow:B`」这种自洽但无关的父记录会被当成
    /// 本运行的固定授权来源（父/子 workflow 关联必须在**授权来源反查与父子绑定建立两处**都强制）。
    /// </summary>
    private (string RequestIdentity, string Scope)? TryGetAdmissionParent(string runId, string? workflowId)
    {
        if (string.IsNullOrEmpty(workflowId)) return null;
        return ResolveAdmissionParent(TryReadAdmissionSources(), _runs.Load(runId), runId, workflowId);
    }

    private LocalWaitDecisionRecord DecideLocalWait(WaitDecisionRequest request)
    {
        var context = new LocalWaitDecisionContext
        {
            RunId = request.RunId,
            WorkflowId = request.WorkflowId,
            WorkflowRevision = request.WorkflowRevision,
            RecordRevision = request.RecordRevision,
            CursorNodeId = request.CursorNodeId,
            CursorOccurrence = request.CursorOccurrence,
            CursorLoopIteration = request.CursorLoopIteration,
            NodeId = request.NodeId,
            SequenceIndex = request.SequenceIndex,
            Occurrence = request.Occurrence,
            LoopIteration = request.LoopIteration,
            Attempt = request.Attempt,
        };
        LocalWaitDecisionRecord Hold(string reason, LocalWaitDecisionContext? observed = null) => new()
        {
            Kind = LocalWaitDecisionKind.Hold,
            Context = observed ?? context,
            Reason = reason,
            NoSendConfirmed = true,
        };
        LocalWaitDecisionRecord Continue(string reason, LocalWaitDecisionContext observed) => new()
        {
            Kind = LocalWaitDecisionKind.ContinueAdmission,
            Context = observed,
            Reason = reason,
            NoSendConfirmed = false,
        };

        // SB21-1 装配不打开双门；门关闭时只保留现有直通提交语义。
        if (!_admissionWired || !_successorAdmissionWired)
            return Continue("节点准入双门仍关闭", context);

        WorkflowRunRecord? run;
        try { run = _runs.Load(request.RunId); }
        catch (Exception ex) { return Hold("运行来源读取失败（" + ex.GetType().Name + "）"); }
        if (run is null
            || run.RecordRevision != request.RecordRevision
            || !string.Equals(run.WorkflowId, request.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(run.WorkflowRevision, request.WorkflowRevision, StringComparison.Ordinal)
            || run.Cursor is not { } cursor
            || !string.Equals(cursor.NodeId, request.CursorNodeId, StringComparison.Ordinal)
            || cursor.Occurrence != request.CursorOccurrence
            || cursor.LoopIteration != request.CursorLoopIteration
            || !string.Equals(cursor.NodeId, request.NodeId, StringComparison.Ordinal)
            || cursor.Occurrence != request.Occurrence
            || cursor.LoopIteration != request.LoopIteration)
            return Hold("等待判定运行修订或游标身份已漂移");

        // 来源证明先于占用比较；面板取唯一流程登记父，启动移交取运行台账固定 Scope。
        var parent = TryGetAdmissionParent(request.RunId, request.WorkflowId);
        if (parent is null || !IsCanonicalAdmissionScope(parent.Value.Scope))
            return Hold("等待判定缺少唯一且规范的授权来源，未创建队列项");
        var handoffIdentity = "run-source:" + request.RunId;
        var sourceKind = string.Equals(parent.Value.RequestIdentity, handoffIdentity, StringComparison.Ordinal)
            ? LocalWaitSourceKind.StartupHandoff
            : LocalWaitSourceKind.PanelFlowRegistration;
        var candidate = BuildSuccessorIdentityCandidate(parent.Value.Scope, request.WorkflowId, request.RunId,
            request.NodeId, request.Occurrence, request.LoopIteration, request.Attempt);
        (string AdmissionIdentity, string CandidateId) translated;
        try { translated = LocalWaitIdentityTranslation.BuildAdmissionIdentity(candidate); }
        catch (Exception ex) { return Hold("等待候选身份无法构造（" + ex.GetType().Name + "）"); }
        var observed = context with
        {
            SourceKind = sourceKind,
            SourceIdentity = sourceKind == LocalWaitSourceKind.StartupHandoff
                ? request.RunId : parent.Value.RequestIdentity,
            Scope = parent.Value.Scope,
            CandidateId = translated.CandidateId,
            AdmissionIdentity = translated.AdmissionIdentity,
            Tier = candidate.Tier,
            Priority = candidate.Priority,
            IsHoeingHighest = false, // ArbitrationCandidate 无可信最高级标记；后继节点不得从父候选继承。
            HasTrustedRankingFacts = true,
        };

        ArbitrationFacts facts;
        try { facts = CurrentArbitrationFacts(); }
        catch (Exception ex) { return Hold("占用事实读取失败（" + ex.GetType().Name + "）", observed); }
        var occupant = facts.RunningOccupant;
        if (facts.ExecutionFactsUnknown || occupant.State == OccupantFactsState.Unknown)
            return Hold("权威占用事实未知，保持停驻，不按空闲处理", observed);
        if (facts.ExecutionOccupied
            && facts.OwnInFlightRunBindings is { Count: 1 } ownBindings
            && string.Equals(ownBindings.First(), request.RunId, StringComparison.Ordinal))
        {
            // 仅在事实快照能把占用唯一归属到当前 run 时跳过本地预停驻；Continue 仍不是发送许可，
            // 异步门面会按持久化父子绑定/未决发送槽再次执行完整豁免校验。生产快照不提供此归属（null）。
            return Continue("占用由当前宿主运行唯一归属；继续完整异步准入并由门面复核", observed);
        }
        if (occupant.State == OccupantFactsState.Idle)
            return Continue("占用事实确认空闲，继续完整异步准入", observed);

        var encounter = RunningOccupancyArbiter.Decide(occupant, new IncomingRequestFacts
        {
            IsHoeingHighest = false,
            Tier = candidate.Tier,
            Priority = candidate.Priority,
            HasTrustedIdentity = observed.HasTrustedRankingFacts,
        });
        return encounter.Verdict switch
        {
            RunningEncounterVerdict.WaitLocally => new LocalWaitDecisionRecord
            {
                Kind = LocalWaitDecisionKind.Wait,
                Context = observed,
                Reason = encounter.Reason,
                NoSendConfirmed = true,
            },
            RunningEncounterVerdict.HoldFactsUnknown or RunningEncounterVerdict.HoldUnknownOccupant => Hold(encounter.Reason, observed),
            _ => Continue(encounter.Reason + "；仍须经过完整异步准入", observed),
        };
    }

    /// <summary>本地等待的稳定身份引用；仅标识该运行游标，不宣称前置已就绪或授予发送权。</summary>
    private static string LocalWaitPrerequisiteReference(WorkflowRunRecord run, WorkflowNodeOccurrence occurrence)
        => "taskcenter-localwait/run/" + Uri.EscapeDataString(run.RunId ?? "")
           + "/workflow/" + Uri.EscapeDataString(run.WorkflowId ?? "")
           + "/node/" + Uri.EscapeDataString(occurrence.NodeId)
           + "/occurrence/" + occurrence.Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + "/loop/" + occurrence.LoopIteration.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// **准入来源解析判据（G4a；纯函数，便于逐支取证）**：
    /// ①租约侧面板来源（`IsFlowRegistrationParent` ＋ workflow 逐字相等）**恰一条** ⇒ 只认它（内容不完整 ⇒ null）；
    /// ②租约侧**多条** ⇒ **来源歧义，直接拒绝**（**禁止**借运行台账字段继续签发）；
    /// ③租约侧零条 ⇒ 回落运行台账：该 run 必须**确有启动移交受理事实**（`Handoffs` 含 start/armTrigger 绑定）、
    ///   `WorkflowId` 逐字相等、且 `AdmissionSourceScope` 为规范形状 `bgi:local:{非空完整 epoch}`
    ///   ⇒ 返回合成来源身份 `run-source:{runId}` 与该固定 Scope；任一不成立 ⇒ null（不签发、不发送）。
    /// </summary>
    internal static (string RequestIdentity, string Scope)? ResolveAdmissionParent(
        IReadOnlyList<OperationRecord>? sources, WorkflowRunRecord? run, string runId, string workflowId)
    {
        var matches = (sources ?? [])
            .Where(o => o is not null
                        && ArbitrationAdmissionService.IsFlowRegistrationParent(o, runId)
                        && string.Equals(o.Candidate?.WorkflowId, workflowId, StringComparison.Ordinal))
            .ToList();
        // 面板来源**唯一**命中 ⇒ 只认它；内容不完整 ⇒ 不签发（不回落到运行台账、不补造）。
        if (matches is { Count: 1 })
        {
            var op = matches[0];
            var scope0 = op.Candidate?.Scope;
            // [批次四十五 第三轮验证会诊处置] 面板来源的 Scope **同样**必须满足规范形状（`bgi:local:{非空完整 epoch}`）：
            // 否则 `garbage`／`bgi:local:`／其它实例前缀都会被当成权威来源。
            return IsCanonicalAdmissionScope(scope0) && !string.IsNullOrEmpty(op.RequestIdentity)
                ? (op.RequestIdentity, scope0!)
                : null;
        }
        // 面板来源**歧义**（同一 runBinding 多条）⇒ **直接拒绝**，**禁止**借运行台账字段继续签发
        // （[批次四十五 验证会诊处置]：否则来源冲突会被运行来源掩盖，形成越权/误判路径）。
        if (matches is { Count: > 1 }) return null;
        // 零条面板来源 ⇒ 按类别回落：启动移交的来源权威＝**运行台账受理登记事实**（与租约相互独立的存储；
        // 租约未初始化/不可读时同样回落——准入本身仍由门面 fail-closed 把关）。
        if (run is null || !string.Equals(run.WorkflowId, workflowId, StringComparison.Ordinal)) return null;
        // 来源类别证明：该 run 必须**确有启动移交受理事实**（start/armTrigger 绑定），且 Scope 为规范形状。
        if (!(run.Handoffs ?? []).Any(h => h is not null
                                          && h.Mode is StartupHandoffModes.Start or StartupHandoffModes.ArmTrigger))
            return null;
        var fixedScope = run.AdmissionSourceScope;
        if (!IsCanonicalAdmissionScope(fixedScope)) return null;
        return ("run-source:" + runId, fixedScope!);
    }

    /// <summary>准入来源 Scope 规范形状：`bgi:local:{非空完整 epoch}`（实例段固定 `local`；epoch 含冒号不成问题）。</summary>
    internal static bool IsCanonicalAdmissionScope(string? scope)
    {
        if (string.IsNullOrEmpty(scope) || !scope.StartsWith("bgi:local:", StringComparison.Ordinal)) return false;
        return !string.IsNullOrEmpty(scope["bgi:local:".Length..].Trim());
    }

    /// <summary>夹具接缝：按运行反查**准入来源**（身份＋固定 Scope）；生产路径内部同源（`TryGetAdmissionParent`）。</summary>
    internal (string RequestIdentity, string Scope)? AdmissionParentForTest(string runId, string workflowId)
        => TryGetAdmissionParent(runId, workflowId);

    /// <summary>
    /// **外部启动受理接管落盘（§4.2a／B3）**：把 `external-start-ledger.json` 写成「已受理待执行」并**读回确认**——
    /// 返回 null＝接管完成（门面才允许关闭 Submission）；返回原因＝接管未完成（保守待对账）。
    /// 台账损坏/读取失败一律视为**未完成**（不推导空闲、不冒充成功）。
    /// **如实限定**：`ConfirmRebuildable` 按本次受理认领的完整载荷读回核验（含证据来源、受理时点、操作类型及已知句柄）；
    /// 句柄尚未知时只确认受理记录本身，不推断远端作业句柄已可重建。
    /// </summary>
    private string? PersistExternalStartLedger(ExternalStartLedgerEntry entry)
    {
        var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
        try
        {
            var ledger = new ExternalStartLedger(root);
            var recorded = ledger.RecordAccepted(entry);
            if (!recorded.Success)
            {
                // 诊断：指明缺哪个必填关联字段（写侧校验口径与台账读侧一致）。
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(entry.SubmissionIdentity)) missing.Add("submissionIdentity");
                if (entry.SendSeq < 1) missing.Add("sendSeq");
                if (string.IsNullOrWhiteSpace(entry.CandidateId)) missing.Add("candidateId");
                if (string.IsNullOrWhiteSpace(entry.ResourceRef)) missing.Add("resourceRef");
                if (string.IsNullOrWhiteSpace(entry.ActionId)) missing.Add("actionId");
                if (string.IsNullOrWhiteSpace(entry.TargetBgiEpoch)) missing.Add("targetBgiEpoch");
                if (string.IsNullOrWhiteSpace(entry.EvidenceSource)) missing.Add("evidenceSource");
                return "ledger_record_failed:" + (recorded.Reason ?? "unknown")
                       + (missing.Count > 0 ? "（缺：" + string.Join(",", missing) + "）" : "");
            }
            return ledger.ConfirmRebuildable(entry)
                ? null
                : "ledger_not_rebuildable";
        }
        catch (Exception ex)
        {
            return "ledger_exception:" + ex.GetType().Name;
        }
    }

    /// <summary>追加式接收 E3/E4/E5 逐轮受理回执；重复回执沿用首次观察元数据并只单调补齐缺失句柄。</summary>
    private string? PersistExternalStartReceiptLedger(ExternalStartLedgerEntry entry)
    {
        var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
        try
        {
            var ledger = new ExternalStartLedger(root);
            var recorded = ledger.RecordLateAcceptedReceipt(entry);
            if (!recorded.Success)
                return "late_receipt_ledger_record_failed:" + (recorded.Reason ?? "unknown");
            var read = ledger.Read();
            if (!read.Valid || read.File is null)
                return "late_receipt_ledger_readback_unavailable";
            var canonical = read.File.Entries.FirstOrDefault(e =>
                string.Equals(e.SubmissionIdentity, entry.SubmissionIdentity, StringComparison.Ordinal)
                && e.SendSeq == entry.SendSeq);
            return canonical is not null && ledger.ConfirmRebuildable(canonical)
                ? null
                : "late_receipt_ledger_readback_mismatch";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "late_receipt_ledger_io:" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// An obsolete sender may finish observing after ownership changed. It can append the terminal fact to the exact
    /// accepted send round, but it cannot settle the Operation or release its slot; the current owner imports it later.
    /// </summary>
    private string? PersistLateExternalStartTerminal(AdmissionResult accepted, ExternalStartCompletion completion)
    {
        if (accepted.ReasonCode != "late_acceptance_receipt_saved"
            || string.IsNullOrWhiteSpace(accepted.RequestIdentity)
            || string.IsNullOrWhiteSpace(accepted.SubmissionIdentity)
            || accepted.SendSeq < 1
            || !string.Equals(accepted.SubmissionIdentity,
                $"sub:{accepted.RequestIdentity}:{accepted.SendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                StringComparison.Ordinal))
            return "late_terminal_send_identity_invalid";

        var terminalKind = completion.Kind switch
        {
            ExternalStartCompletionKind.Succeeded => ExecutionResultKind.Succeeded,
            ExternalStartCompletionKind.ExecutionFailed => ExecutionResultKind.Failed,
            ExternalStartCompletionKind.Cancelled => ExecutionResultKind.Cancelled,
            _ => (ExecutionResultKind?)null,
        };
        if (terminalKind is not { } kind
            || string.IsNullOrWhiteSpace(completion.RawTerminal)
            || string.IsNullOrWhiteSpace(completion.EvidenceSource)
            || completion.ObservedAtUtc is not { } observedAt
            || observedAt == default
            || (kind == ExecutionResultKind.Failed && string.IsNullOrWhiteSpace(completion.ExecutionErrorCode))
            || string.IsNullOrWhiteSpace(accepted.JobId)
            || !string.Equals(accepted.JobId, completion.JobId, StringComparison.Ordinal))
            return "late_terminal_payload_incomplete_or_mismatched";

        var root = _admissionRoot ?? Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
        try
        {
            var ledger = new ExternalStartLedger(root);
            var read = ledger.Read();
            if (!read.Valid || read.File is null) return "late_terminal_ledger_unreadable";
            var entry = read.File.Entries.FirstOrDefault(e =>
                string.Equals(e.SubmissionIdentity, accepted.SubmissionIdentity, StringComparison.Ordinal)
                && e.SendSeq == accepted.SendSeq);
            if (entry is null
                || entry.OperationType != OperationType.ExternalStart
                || string.IsNullOrWhiteSpace(entry.JobId)
                || !string.Equals(entry.JobId, accepted.JobId, StringComparison.Ordinal))
                return "late_terminal_acceptance_receipt_mismatch";

            var marked = ledger.MarkTerminal(accepted.SubmissionIdentity, accepted.SendSeq,
                completion.RawTerminal!, observedAt, completion.RawTerminal, completion.ExecutionErrorCode,
                OperationType.ExternalStart, accepted.JobId, completion.EvidenceSource, kind);
            if (!marked.Success) return "late_terminal_ledger_write_failed:" + (marked.Reason ?? "unknown");

            var readback = ledger.Read();
            if (!readback.Valid || readback.File is null) return "late_terminal_ledger_readback_unavailable";
            var canonical = readback.File.Entries.FirstOrDefault(e =>
                string.Equals(e.SubmissionIdentity, accepted.SubmissionIdentity, StringComparison.Ordinal)
                && e.SendSeq == accepted.SendSeq);
            return canonical is { State: LedgerEntryState.Terminal }
                   && canonical.OperationType == OperationType.ExternalStart
                   && string.Equals(canonical.TerminalEvidence, completion.RawTerminal, StringComparison.Ordinal)
                   && string.Equals(canonical.RawTerminal, completion.RawTerminal, StringComparison.Ordinal)
                   && string.Equals(canonical.ExecutionErrorCode, completion.ExecutionErrorCode, StringComparison.Ordinal)
                   && string.Equals(canonical.JobId, accepted.JobId, StringComparison.Ordinal)
                   && string.Equals(canonical.TerminalEvidenceSource, completion.EvidenceSource, StringComparison.Ordinal)
                   && canonical.TerminalObservedAtUtc == observedAt
                   && canonical.TerminalKind == kind
                ? null
                : "late_terminal_ledger_readback_mismatch";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "late_terminal_ledger_io:" + ex.GetType().Name;
        }
    }
    /// <summary>
    /// **[批次 14／D1 可测接缝] 结果维 → 适配层三态折叠（原为 AdmitExternalStartAsync 内的内联 switch）。**
    /// 纯函数抽取，**不改变**任何既有分支顺序与结论；仅新增 WaitLocally 显式分支。
    /// 纪律：Accepted 之外的未知值一律 NeedReconcile（不得重发、保守待对账）。
    /// </summary>
    internal static ExternalStartAdmissionStatus MapAdmissionResultToExternalStartStatus(AdmissionResult result)
    {
        return result.Kind switch
        {
            AdmissionResultKind.Accepted => ExternalStartAdmissionStatus.Accepted,
            // R5.3 §24.2-2／§24.9：取消与确定执行失败各自成列（**不得**压成 Unknown/Rejected──前者会丢取消信号、后者会诱发重发）。
            AdmissionResultKind.Cancelled => ExternalStartAdmissionStatus.Cancelled,
            AdmissionResultKind.ExecutionFailed => ExternalStartAdmissionStatus.ExecutionFailed,
            AdmissionResultKind.F11Blocked or AdmissionResultKind.NeedPreemptConfirm =>
                ExternalStartAdmissionStatus.Blocked,
            AdmissionResultKind.TerminalRejected or AdmissionResultKind.RetryableRejected
                or AdmissionResultKind.NotSelected => ExternalStartAdmissionStatus.Rejected,
            // **[批次 14／D1]** `WaitLocally` 显式成列：这是**确定结论**（零发送、已登记本地持久等待），
            // 若落入 `_ =>` 会被适配层当成「事实不可考 ⇒ 需对账」（事实改写：把确定事实说成不可考）。
            // 与 `Rejected` 分开：适配层据此按「已在本地排队、稍后重评」回执，而不是走失败回执语义。
            AdmissionResultKind.WaitLocally => ExternalStartAdmissionStatus.WaitLocally,
            _ => ExternalStartAdmissionStatus.NeedReconcile,
        };
    }

    /// <summary>
    /// **适配层可见的外部启动准入（B3）**：把门面内部 `AdmissionResult` 折叠为
    /// <see cref="ExternalStartAdmissionStatus"/> 三态 + 门禁/占用阻断，供 `CommandExecutor` 生成 `CommandResult`。
    /// **不泄漏门面内部类型**；`NeedReconcile` 一律按「不得重发、保守待对账」处置。
    /// </summary>
    public async Task<ExternalStartAdmissionOutcome> AdmitExternalStartAsync(
        ExternalStartAdmissionRequest request, CancellationToken ct = default)
    {
        // Keep the linked send lifetime alive through completion observation. The queue observer is started
        // while the send runs, and must remain linked to host shutdown after the early receipt returns.
        using var sendLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var result = await SubmitExternalStartViaAdmissionCoreAsync(request, sendLifetime.Token).ConfigureAwait(false);
        // [Batch B 收尾之二] §24.10／§24.14 完成观察接线：受理成功后取完成层结果并交完成结算入口——
        // `CompletionProvider` 缺失/返回 null ⇒ 保持普通受理（责任 Pending）；返回 `Unknown` ⇒ 不写终态载体、保守待对账；
        // 返回权威终态 ⇒ 由门面按 §24.15 唯一顺序结算（终态载体→台账 Terminal→关闭→终局）并回传结算事实。
        // [Batch B 收尾之三 验证会诊阻断处置] **不只 `Accepted` 才消费完成观察**（§24.14-4）：
        // 接管/关闭失败时门面返回 `Reconciling`（Submission 未关闭），此时完成层若已取得**权威终态**，
        // 必须由本层暂存结果送结算入口按 §24.15 唯一顺序（终态载体→台账 Terminal→关闭→终局）处理；
        // 否则终态会只留在无人消费的后台观察任务里（观察责任丢失）。判定标准＝**有完整发送身份**，
        // 且**确有完成事实**（观察/提供者返回 null 时下面不结算，保持门面原结论）。
        if (!string.IsNullOrEmpty(result.SubmissionIdentity)
            && (request.CompletionProvider is not null || request.CompletionObserver is not null)
            && _admission is { } facadeForCompletion)
        {
            ExternalStartCompletion? completion;
            try
            {
                // 同步提供者优先（阻塞式协议）；提供者返回 null（例如早期 ack 通道：核心未等待完成）时
                // 再等异步完成观察（§24.10：受理与完成等待分离；等待在门面锁外，§24.14-2）。
                // **观察返回 null ＝本通道不承载完成事实**（未配置/不适用）⇒ 保持普通受理（责任 `Pending`），
                // 不得据此走完成结算（否则 v2 普通受理会被误判成待对账）。
                completion = request.CompletionProvider?.Invoke();
                if (completion is null && request.CompletionObserver is { } observer)
                    completion = await observer(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                completion = ExternalStartCompletion.UnknownWith("completion_provider_exception:" + ex.GetType().Name);
            }
            if (completion is not null)
            {
                try
                {
                    var settled = await facadeForCompletion.SettleCompletionAsync(
                        result.RequestIdentity, result.SubmissionIdentity!, result.SendSeq, completion,
                        result.EvidenceSource,
                        // §24.6-2／D9：**已取得的 JobId 不得在适配器边界断链**——受理时拿到的句柄必须随
                        // 完成结算一路传递（也是「完成层 Unknown／失败但句柄已取得」时对调用方回显的来源）。
                        acceptanceJobId: result.JobId,
                        expectedOwnerLeaseId: result.CapturedLeaseId,
                        expectedOwnerEpoch: result.CapturedOwnerEpoch).ConfigureAwait(false);
                    // [验证会诊阻断处置] 结算入口可能因「操作不在可结算状态」返回**停止结论**（`not_accepted` 等）：
                    // 此时**不得**用停止结论覆盖既有的 Rejected/RetryableRejected 事实；但若本次确有**权威终态**证据，
                    // 属于 §24.2-2″／§24.15「已拒绝后收到冲突证据」——必须原子追加冲突证据并置冲突待决
                    // （责任 Pending、禁止重发；由冲突裁决入口按四分支裁决），**禁止静默丢证据**。
                    if (settled.ReasonCode == "not_accepted")
                    {
                        var conflict = await RegisterObservedTerminalConflictAsync(
                            facadeForCompletion, result, completion).ConfigureAwait(false);
                        if (conflict is not null) result = conflict;
                    }
                    else
                    {
                        var staleLateAcceptedSender = settled.ReasonCode == "lease_stale_generation"
                            && result.ReasonCode == "late_acceptance_receipt_saved";
                        if (staleLateAcceptedSender && completion.Kind != ExternalStartCompletionKind.Unknown)
                        {
                            var terminalFailure = PersistLateExternalStartTerminal(result, completion);
                            result.RawTerminal = completion.RawTerminal;
                            result.ExecutionErrorCode = completion.ExecutionErrorCode;
                            result.EvidenceSource = completion.EvidenceSource;
                            result.ExecutionDisposition = completion.Kind switch
                            {
                                ExternalStartCompletionKind.Cancelled => ExecutionDisposition.Cancelled,
                                ExternalStartCompletionKind.ExecutionFailed => ExecutionDisposition.ExecutionFailed,
                                _ => ExecutionDisposition.None,
                            };
                            result.ResponsibilityState = ResponsibilityState.Pending;
                            result.Detail += terminalFailure is null
                                ? "；旧负责人仅将该发送轮终态追加到共享台账，等待当前负责人恢复结案。"
                                : "；旧负责人未能确认旧发送轮终态入账，当前负责人仍须按原 JobId 对账：" + terminalFailure;
                            if (terminalFailure is not null)
                            {
                                result.Kind = AdmissionResultKind.NeedReconcile;
                                result.ReasonCode = "late_acceptance_terminal_persist_failed";
                            }
                        }
                        else if (!staleLateAcceptedSender)
                            result = settled;
                    }
                }
                catch (Exception ex)
                {
                    // [Batch B 收尾之二 会诊阻断处置] 结算入口抛异常时**不得抹掉已观察的取消/失败事实**（§24.6-2）：
                    // 结果维按完成层事实返回、责任维 `Pending`（未结清），并携带完整发送身份与证据。
                    result = new AdmissionResult
                    {
                        Kind = completion.Kind switch
                        {
                            ExternalStartCompletionKind.Cancelled => AdmissionResultKind.Cancelled,
                            ExternalStartCompletionKind.ExecutionFailed => AdmissionResultKind.ExecutionFailed,
                            ExternalStartCompletionKind.Succeeded => AdmissionResultKind.NeedReconcile,
                            _ => AdmissionResultKind.NeedReconcile,
                        },
                        ReasonCode = "completion_settle_exception:" + ex.GetType().Name,
                        Detail = "完成结算入口异常（结果维保留已观察事实；责任未结清，保守待对账）。",
                        RequestIdentity = result.RequestIdentity,
                        SubmissionIdentity = result.SubmissionIdentity,
                        SendSeq = result.SendSeq,
                        JobId = completion.JobId ?? result.JobId,
                        ExecutionDisposition = completion.Kind switch
                        {
                            ExternalStartCompletionKind.Cancelled => ExecutionDisposition.Cancelled,
                            ExternalStartCompletionKind.ExecutionFailed => ExecutionDisposition.ExecutionFailed,
                            _ => ExecutionDisposition.Unknown,
                        },
                        ResponsibilityState = ResponsibilityState.Pending,
                        RawTerminal = completion.RawTerminal,
                        ExecutionErrorCode = completion.ExecutionErrorCode,
                        EvidenceSource = completion.EvidenceSource,
                    };
                }
            }
        }
        var status = MapAdmissionResultToExternalStartStatus(result);
        // 回显本次操作身份（§2.1）：适配器据此在**同次用户操作**的内部重试上发起 ContinueUse。
        // **不得用 `??`**：`AdmissionResult.RequestIdentity` 默认是空串（门面各前置/异常分支也显式返回空串），
        // `??` 会让「已有身份续用时被前置阻断」回显空串 ⇒ 调用方可能把下一次续用误变成新建操作（会诊重要项）。
        var echoedIdentity = string.IsNullOrEmpty(result.RequestIdentity)
            ? (request.RequestIdentity ?? "")
            : result.RequestIdentity;
        // R5.3 §24.6-2：结果维 × 责任维贯通到适配器边界（不得在宿主侧丢弃 JobId/终态词/证据来源/责任状态）。
        return new ExternalStartAdmissionOutcome(
            status, result.ReasonCode, result.Detail, echoedIdentity,
            result.JobId, result.ExecutionDisposition, result.ResponsibilityState,
            result.RawTerminal, result.ExecutionErrorCode, result.EvidenceSource,
            result.SubmissionIdentity, result.SendSeq);
    }

    /// <summary>
    /// **外部启动观察恢复（R5.3 §24.12-3 集合②/③；[Batch B 收尾之五] 新增）**：进程重启/接管后按**完整发送身份**
    /// （`submissionIdentity＋sendSeq`）把本地租约与外部启动台账对齐：
    /// ①**集合②未终结台账**（`AcceptedPendingExecution`）：**保留观察责任与占用**——不写终态、不释放、不重发
    ///   （重查依据＝台账 `jobId`／线上提交键；本进程无可用查询通道时按「不可查询」保守停驻，§24.16-3）。
    /// ②**集合③台账已终态、本地 Operation 未终局**：用**已持久化的 `PendingTerminal`**（`Kind`／原词／错误码／
    ///   句柄／证据来源／观察时点）驱动 `SettleCompletionAsync` 补终局（§24.15 唯一顺序；`Unknown` 不驱动）。
    /// ③其余不一致（台账终态但缺 `PendingTerminal`／租约侧已终态而台账未终结／台账孤儿记录）**只计数登记**，
    ///   不改变任何责任（禁止静默释放或补造事实）。
    /// 纪律：**绝不使启动失败**（恢复对齐属诊断性动作，异常只记日志、责任保留）；`ct` 取消＝宿主退出，原样上抛。
    /// </summary>
    private async Task RecoverExternalStartObservationsAsync(ArbitrationAdmissionService facade, CancellationToken ct)
    {
        try
        {
            // 分类与补终局语义归**门面**（§24.12-3；`TakeoverLedgerScan` 提供台账事实）：本方法只调用＋记日志。
            var report = await facade.RecoverExternalStartObservationsAsync(ct).ConfigureAwait(false);
            if (report.AnythingReported)
                TryLog("[任务中心] 外部启动观察恢复（§24.12-3）：" + report);
        }
        catch (OperationCanceledException)
        {
            throw;   // 宿主退出＝放弃初始化（未产生新副作用）
        }
        catch (Exception ex)
        {
            // 恢复对齐属诊断性动作：失败只记录，绝不使启动失败、绝不改变既有责任。
            TryLog("[任务中心] 外部启动观察恢复异常（保守停驻，责任保留）：" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// **「已拒绝后收到权威终态」的冲突登记**（§24.2-2″／§24.15 最后一行；[验证会诊阻断处置] 新增）：
    /// 普通完成结算对本笔返回 `not_accepted`（本笔已被判「确定未受理」）时，**不得静默丢弃**同一发送身份上
    /// 新观察到的**权威终态**——必须原子追加冲突证据并置冲突待决（既有拒绝本体不改写，只由审计项表达取代关系），
    /// 由冲突裁决入口按四分支裁决。
    /// 返回 `null`＝证据不构成冲突（`Unknown`／缺观察时点／登记被拒）⇒ 调用方保留门面原结论。
    /// </summary>
    private static async Task<AdmissionResult?> RegisterObservedTerminalConflictAsync(
        ArbitrationAdmissionService facade, AdmissionResult rejectedResult, ExternalStartCompletion completion)
    {
        if (completion.Kind == ExternalStartCompletionKind.Unknown
            || string.IsNullOrEmpty(completion.RawTerminal)
            || completion.ObservedAtUtc is not { } observedAt || observedAt == default
            || string.IsNullOrEmpty(rejectedResult.SubmissionIdentity))
            return null;
        var evidence = new ConflictEvidenceRecord
        {
            // 确定性 evidenceId：同一观察事实重复登记幂等；不同观察时点＝不同证据（§24.2-2″ 追加式）。
            EvidenceId = "host-observed:" + rejectedResult.SubmissionIdentity + ":" + rejectedResult.SendSeq + ":"
                         + completion.RawTerminal + ":" + observedAt.UtcTicks,
            RawTerminal = completion.RawTerminal!,
            ExecutionErrorCode = completion.ExecutionErrorCode,
            EvidenceSource = string.IsNullOrEmpty(completion.EvidenceSource) ? "host:completion" : completion.EvidenceSource!,
            ObservedAtUtc = observedAt,
            SubmissionIdentity = rejectedResult.SubmissionIdentity!,
            SendSeq = rejectedResult.SendSeq,
        };
        try
        {
            var registered = await facade
                .RegisterConflictEvidenceAsync(rejectedResult.RequestIdentity, evidence).ConfigureAwait(false);
            // 登记成功＝责任 Pending＋禁止重发（NeedReconcile）；其余（损坏/身份不符/类型不符）保留门面原结论。
            return registered.Kind is AdmissionResultKind.NeedReconcile or AdmissionResultKind.Accepted
                ? registered
                : null;
        }
        catch (Exception)
        {
            return null;   // 登记异常不得改变既有拒绝结论（保守：证据本轮不登记，责任仍由拒绝结论承担）
        }
    }

    /// <summary>
    /// **外部启动发送分派（B3／§6.1）**：调用适配层既有启动实现一次，并把其结论映射为 `SendOutcome`。
    /// 纪律：适配层异常一律 `Unknown`（**不得**凭异常推断未受理）；「确定未受理」只能由适配层给出关联验证后的结论。
    /// </summary>
    private static async Task<SendOutcome> DispatchExternalStartViaHostAsync(ExternalStartContext ext, CancellationToken callerToken)
    {
        ExternalStartExecution execution;
        try
        {
            execution = await ext.ExecuteAsync(callerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 启动期取消＝结果不可考（适配层可能已发出）——按 Unknown 保守对账，绝不折成确定拒绝。
            return new SendOutcome.Unknown("external_start_cancelled_after_admission");
        }
        catch (Exception ex)
        {
            return new SendOutcome.Unknown("external_adapter_exception:" + ex.GetType().Name);
        }

        // R5.3 §24.2-2（[落地批次会诊阻断处置]）：按**判别式**穷尽分派——**字段级无损**映射到门面 `SendOutcome`：
        // `Accepted(JobId)`／`Rejected(reason, retryable, evidenceSource)`／`Unknown(detail)`；
        // 不得再用旧布尔兼容属性（会让 JobId/Retryable/证据来源在宿主边界丢失）。
        return execution.Kind switch
        {
            ExternalStartExecutionKind.Accepted => new SendOutcome.Accepted(
                string.IsNullOrEmpty(execution.EvidenceSource) ? "external:adapter_accepted" : execution.EvidenceSource!,
                null,
                execution.JobId),
            ExternalStartExecutionKind.Rejected => new SendOutcome.Rejected(
                string.IsNullOrEmpty(execution.Reason) ? "external_rejected" : execution.Reason!,
                execution.Retryable,
                string.IsNullOrEmpty(execution.EvidenceSource) ? "external:adapter" : execution.EvidenceSource!),
            // §24.6-2：Unknown 也必须保留证据来源（字段级无损；不得在宿主边界丢弃）。
            _ => new SendOutcome.Unknown(
                "external_uncertain:" + (string.IsNullOrEmpty(execution.Reason) ? "unknown" : execution.Reason!),
                execution.EvidenceSource),
        };
    }

    /// <summary>
    /// 后继发送分派（门面 Sender 回调，§13.10 A2/A3）：runBinding 反查 run → 由请求重建出现身份与节点 →
    /// 发送段内 `PrepareSubmit + SendPreparedAsync`（准入已在门面完成）→ 结果按完整发送身份暂存并映射三态。
    /// </summary>
    private async Task<SendOutcome> DispatchSuccessorViaHostAsync(SubmissionDispatch d)
    {
        var read = _admissionStore!.Read();
        var op = read.File?.Handoff?.Operations?.FirstOrDefault(
            o => string.Equals(o.RequestIdentity, d.RequestIdentity, StringComparison.Ordinal));
        if (op?.RunBinding is not { } runId) return new SendOutcome.Unknown("run_binding_missing");
        // 会诊重要项：业务身份（runId|node|occ|loop|attempt）相同**不足以**区分同 attempt 的多个 sendSeq，
        // 派遣对象必须与台账中该操作的**本轮完整发送身份**一致；不一致＝不得发送（可证实未发送的拒绝）。
        if (!string.Equals(op.SubmissionIdentity, d.SubmissionIdentity, StringComparison.Ordinal))
            return new SendOutcome.Rejected("successor_submission_identity_mismatch", false, "host:identity");
        var run = _runs.Load(runId);
        if (run is null) return new SendOutcome.Rejected("run_record_missing", false, "host:runstore");
        var c = _clientAccessor();
        if (c is null) return new SendOutcome.Rejected("bgi_client_missing", false, "host:client");

        // §13.10 A1/A2/A2′：**只消费获选排队项携带的不可变请求快照**——
        // 不得 LoadSnapshot 重建节点（排队期间流程定义可变）、不得写死 occurrence 参数、不得丢弃提交选项。
        // 上下文缺失（续用/重试/重启后未携带）= 响亮拒绝，绝不静默重建后发送。
        if (d.ProcessLocalContext is not SuccessorContext ctx)
            return new SendOutcome.Rejected("successor_context_missing", false, "host:context");
        var occ = ctx.Occurrence;
        var node = ctx.Node;
        // 出现身份与候选必须一致（防上下文与候选错配）
        if (!string.Equals(occ.NodeId, d.Candidate.NodeId, StringComparison.Ordinal)
            || occ.Occurrence != d.Candidate.Occurrence || occ.LoopIteration != d.Candidate.LoopIteration)
            return new SendOutcome.Rejected("successor_context_mismatch", false, "host:context");
        // §13.10 A1（[补·2026-09-21] 会诊要求）：**消费**冻结的 attempt/key——排队期间若当前提交已换身份
        // （新 attempt / 新提交键），旧授权不得被准备成新提交，一律响亮拒绝（确定未发送）。
        if (run.CurrentSubmission is not { } liveSub
            || liveSub.Attempt != ctx.Attempt
            || !string.Equals(liveSub.Key, ctx.ExpectedSubmissionKey, StringComparison.Ordinal))
            return new SendOutcome.Rejected("successor_submission_identity_changed", false, "host:context");

        // **G4 残余②（本批 W2）发送侧冻结游标复核**：入队（占位发布）后、准备/发送前，
        // 权威运行游标必须仍与本次提交的出现身份一致；游标已被并发推进（提交键/attempt 未变故
        // 上面的身份检查不拦截）⇒ 旧提交不得进入准备与发送（确定未发送的拒绝，零发送）。
        // 复核**游标字段值**而非 RecordRevision——任何记录更新都推进修订，严格相等会误拒无关更新
        // （§13.11 G4 残余①设计裁决：记录版本校验与逻辑游标消费身份分离；修订已用于 ⑪b 唯一消费键）。
        if (run.Cursor is not { } liveCursor
            || !string.Equals(liveCursor.NodeId, occ.NodeId, StringComparison.Ordinal)
            || liveCursor.Occurrence != occ.Occurrence
            || liveCursor.LoopIteration != occ.LoopIteration)
            return new SendOutcome.Rejected("successor_cursor_changed", false, "host:cursor",
                "入队后运行游标已推进（当前=" + (run.Cursor is null ? "<null>" : run.Cursor.NodeId + "#" + run.Cursor.Occurrence + "#" + run.Cursor.LoopIteration) + "，冻结=" + occ.NodeId + "#" + occ.Occurrence + "#" + occ.LoopIteration + "）；旧提交不得发送");
        // 与 Runner 侧共用同一「生产边界组装点」（会诊复审：不得在此另 new 一份，否则预检/发送分裂）。
        var inner = CreateProductionBoundary(c);
        BgiWorkflowExecutionBoundary.PreparedSubmit prepared;
        try
        {
            prepared = inner.PrepareSubmit(new WorkflowSubmitRequest(run, occ, node, ctx.Suppress),
                authorizedEpoch: d.TargetEpoch); // 授权纪元＝门面本轮授权值（A2：不在 sender 内重读当前 epoch）
        }
        catch (Exception ex)
        {
            // 准备故障保留类型和原文，由门面按原发送身份持久化未知诊断；责任继续待对账。
            TryLog("[任务中心] 后继提交准备阶段异常（" + runId + "）：" + ex.GetType().Name + "：" + ex.Message);
            return new SendOutcome.Unknown("boundary_prepare_exception: " + ex.GetType().Name + ": " + ex.Message, "host:boundary");
        }
        if (prepared.Rejection is { } rej)
        {
            // 原始拒绝原因同时进入诊断日志和结构化结果。
            try
            {
                _log?.Invoke("[任务中心] 后继提交准备阶段拒绝（" + runId + "）：" + rej.RejectReason);
            }
            catch (Exception)
            {
                // 诊断失败不改变结论。
            }
            return rej.Uncertain
                ? new SendOutcome.Unknown("boundary_precheck_uncertain: " + rej.RejectReason, "host:boundary")
                : new SendOutcome.Rejected("boundary_precheck_rejected", false, "host:boundary", rej.RejectReason);
        }

        // §17 P6／§13.11 G7：调用方令牌**透传到发送段**（此前恒 `CancellationToken.None` ⇒ 发送窗口取消无法中止在飞发送）。
        // 取消的语义边界不变：取消是**调用结束方式**，不是关闭依据——发送段取消后仍按不可考/对账路径保留责任，
        // 本行**不**引入任何「取消即结清」的路径。
        var sent = await inner.SendPreparedAsync(prepared, d.CallerToken).ConfigureAwait(false);
        if (d.SubmissionIdentity.Length > 0) _successorSendResults[d.SubmissionIdentity] = sent;
        if (!sent.Accepted)
            return sent.Uncertain
                ? (SendOutcome)new SendOutcome.Unknown("host:successor_uncertain" + (string.IsNullOrEmpty(sent.RejectReason) ? "" : ": " + sent.RejectReason), "host:boundary")
                // **[P8／§24.62]** 确定拒绝的**可重试性来自证据**（`BoundarySubmitResult.Retryable`）：
                // 「可证实未发送」（传输层证据载体）⇒ 开重试窗口（§3.2a 无损拒绝类）；其余确定拒绝保持终局。
                : new SendOutcome.Rejected("host:successor_rejected", sent.Retryable, "host:boundary", sent.RejectReason);

        // **M2／§12.2 B2「先接管、后关闭」**：远端已受理 → **先按完整发送身份把受理事实（jobId）落盘**，
        // 之后才允许门面关闭 Submission（门面 TakeoverPersist 会复核该记录）。
        // 落盘失败＝远端已受理但接管未落盘：**不得**重新解释为确定拒绝（否则会诱发重发），只能报 Unknown，
        // 由门面把 Submission 置 Reconciling 保守待对账。
        if (!TryPersistAcceptedTakeover(runId, ctx, sent.JobId, d.TargetEpoch, d.SubmissionIdentity))
            return new SendOutcome.Unknown("host:takeover_persist_failed");
        return new SendOutcome.Accepted("host:successor_sent", runId);
    }

    /// <summary>
    /// **受理接管落盘（M2／§12.2 B2）**：读最新记录 → 按提交身份与授权纪元复核 → 写 `Intent=Accepted`＋`JobId`
    /// → CAS 落盘。只改本轮提交字段，**不整体覆盖**其他运行事实；修订冲突有界重读重试（读取后立即 CAS）。
    /// 返回 false＝受理事实未落盘（调用方必须报 Unknown：不得报 Accepted、不得重发）。
    /// 纪律：本落盘**不得**复用调用方取消令牌（此处为同步本地写，天然不受用户令牌影响）。
    /// </summary>
    private bool TryPersistAcceptedTakeover(string runId, SuccessorContext ctx, string? jobId, string targetEpoch,
        string submissionIdentity)
    {
        if (string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(submissionIdentity)) return false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            WorkflowRunRecord? run;
            try
            {
                run = _runs.Load(runId);
            }
            catch (Exception)
            {
                return false;
            }

            if (run?.CurrentSubmission is not { } sub) return false;
            // 身份复核：不得把本轮受理结果写进已被替换/改写的另一轮提交。
            if (sub.Attempt != ctx.Attempt
                || !string.Equals(sub.Key, ctx.ExpectedSubmissionKey, StringComparison.Ordinal)
                || !string.Equals(sub.NodeId, ctx.Occurrence.NodeId, StringComparison.Ordinal)
                || sub.Occurrence != ctx.Occurrence.Occurrence
                || sub.LoopIteration != ctx.Occurrence.LoopIteration)
                return false;
            // 授权纪元复核：冻结纪元必须等于本轮门面授权纪元（否则不得认领本次受理）。
            if (!string.Equals(sub.Epoch, targetEpoch, StringComparison.Ordinal)) return false;
            if (string.Equals(sub.JobId, jobId, StringComparison.Ordinal)
                && sub.Intent == SubmitIntentState.Accepted)
            {
                // 幂等（重放安全）：**同回执必须同时确认发送身份**——不得因短路而留下空身份，
                // 也不得把已属另一轮的完整发送身份改写成本轮。
                if (string.Equals(sub.AcceptedSendIdentity, submissionIdentity, StringComparison.Ordinal)) return true;
                if (sub.AcceptedSendIdentity is { Length: > 0 }) return false;
                // 身份缺失但回执一致：补记本轮身份后落盘（一次性补齐，之后钩子即可严格校验）。
                sub.AcceptedSendIdentity = submissionIdentity;
            }
            else
            {
                // **冲突 jobId 不得覆盖**（会诊阻断项）：记录里已有另一受理事实＝他人事实来源，
                // 本方法无权改写（CAS 只防过期版本，不防重读后主动覆盖）。报 false → 调用方报 Unknown。
                if (!string.IsNullOrEmpty(sub.JobId)) return false;
                // 合法转换：IntentRecorded/Submitted → Accepted；不得从已拒绝/已观察终态回退。
                if (sub.Intent is not (SubmitIntentState.IntentRecorded or SubmitIntentState.Submitted)) return false;
                if (sub.ObservedTerminal is not null) return false;
                sub.Intent = SubmitIntentState.Accepted;
                sub.JobId = jobId;
                // **本轮发送身份一并落盘**（会诊阻断处置）：使「回执 ↔ 本笔发送轮次」的关联可跨重启验证，
                // 门面 TakeoverPersist 据此排除「同 run 另一笔同业务身份/同纪元回执关闭本 Submission」。
                sub.AcceptedSendIdentity = submissionIdentity;
            }
            try
            {
                _runs.Update(run);
                return true;
            }
            catch (RunRecordConflictException)
            {
                // 并发推进：重读后按同一身份重试（只改本轮提交字段，故可安全重放）。
            }
            catch (Exception)
            {
                return false;
            }
        }
        return false;
    }


    /// <summary>
    /// [G7-residual·本批处置] 按运行反查租约节点操作的**完整发送身份**（恢复/对账路径用）。
    /// 只返回唯一权威命中：同 run 下按节点/出现/轮次/attempt/提交键/授权纪元匹配节点执行操作；
    /// 零命中/多命中/身份冲突一律 null（保守保留，绝不凭裸 jobId 或当前流程定义重建身份）。
    /// </summary>
    private NodeSendIdentity? TryResolveNodeSendIdentity(WorkflowRunRecord run, WorkflowSubmission sub)
    {
        var read = _admissionStore?.Read();
        var ops = read?.File?.Handoff?.Operations;
        if (ops is null || string.IsNullOrEmpty(run.RunId)) return null;
        var matches = ops.Where(o => o.OperationType == OperationType.NodeExecution
            && string.Equals(o.RunBinding, run.RunId, StringComparison.Ordinal)
            && o.Candidate is { } cand
            && string.Equals(cand.NodeId, sub.NodeId, StringComparison.Ordinal)
            && cand.Occurrence == sub.Occurrence
            && cand.LoopIteration == sub.LoopIteration
            && cand.Attempt == sub.Attempt
            && !string.IsNullOrEmpty(o.WireSubmitKey)
            && string.Equals(o.WireSubmitKey, sub.Key, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(o.SubmissionIdentity)).ToList();
        if (matches.Count != 1) return null;  // 多 sendSeq 无法唯一关联 ⇒ 保守保留（禁止补造/重发）
        var op = matches[0];
        // 授权纪元一致复核：冻结纪元不符＝旧纪元事实，不得据以补写本笔身份。
        if (!string.IsNullOrEmpty(sub.Epoch) && !string.Equals(op.TargetEpoch, sub.Epoch, StringComparison.Ordinal)) return null;
        return new NodeSendIdentity(op.RequestIdentity, op.SubmissionIdentity, op.LastSendSeq, op.TargetEpoch);
    }

    /// <summary>节点操作的完整发送身份（恢复/对账路径取回：原轮次门面结清所需四元）。</summary>
    private sealed record NodeSendIdentity(string RequestIdentity, string SubmissionIdentity, int SendSeq, string TargetEpoch);
    /// <summary>
    /// 运行终态→仲裁操作终局回写。Runner finally 保持异步，避免受理管线同线程等待；显式 Stop 则等待有界结果，
    /// 把未完成回写作为调用者可见的 pending，并允许对已 Cancelled run 再次 Stop 进行同会话/重启后重试。
    /// </summary>
    private void MarkAdmissionTerminalIfAny(string? runId)
    {
        var work = ReconcileAdmissionTerminalAsync(runId);
        _ = work.ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                TryLog("[任务中心] 仲裁操作终局回写异常（保守留待显式重试）:" + task.Exception?.GetBaseException().Message);
                return;
            }
            if (task.Result is AdmissionTerminalReconciliationOutcome.Pending or AdmissionTerminalReconciliationOutcome.Failed)
                TryLog("[任务中心] 仲裁操作终局回写未确认（保守留待显式重试）。");
        }, TaskScheduler.Default);
    }

    private HostActionResult ReconcileAdmissionTerminalForExplicitStop(string runId, string successMessage)
    {
        var work = ReconcileAdmissionTerminalAsync(runId);
        var timeout = (AdmissionTerminalReconciliationTimeoutForTest ?? TimeSpan.FromSeconds(15))
            + TimeSpan.FromSeconds(2);
        AdmissionTerminalReconciliationOutcome outcome;
        try
        {
            outcome = work.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            return HostActionResult.Unavailable("运行已终态化，但受理登记终局回写仍待确认；恢复存储后再次执行 Stop 重试");
        }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable("运行已终态化，但受理登记终局回写失败；恢复存储后再次执行 Stop 重试："
                + ex.GetType().Name + "（" + ex.Message + "）");
        }

        return outcome switch
        {
            AdmissionTerminalReconciliationOutcome.NotRequired or
            AdmissionTerminalReconciliationOutcome.NoMapping or
            AdmissionTerminalReconciliationOutcome.Completed => HostActionResult.Effective(successMessage),
            _ => HostActionResult.Unavailable("运行已终态化，但受理登记终局回写尚未确认；恢复责任后再次执行 Stop 重试"),
        };
    }

    private Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalAsync(string? runId)
        => Task.Run(() => ReconcileAdmissionTerminalCoreAsync(runId));

    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreAsync(string? runId)
    {
        try
        {
            return await ReconcileAdmissionTerminalCoreBodyAsync(runId).ConfigureAwait(false);
        }
        finally
        {
            AdmissionTerminalReconciliationCompletedForTest?.Invoke();
        }
    }

    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync(string? runId)
    {
        if (!_admissionWired) return AdmissionTerminalReconciliationOutcome.NotRequired;
        if (string.IsNullOrWhiteSpace(runId))
            return AdmissionTerminalReconciliationOutcome.Failed;

        try { await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            TryLog("[任务中心] 终局回写无法初始化受理存储（保守留待重试）:" + ex.Message);
            return AdmissionTerminalReconciliationOutcome.Failed;
        }
        if (_admission is null || _admissionStore is null)
            return AdmissionTerminalReconciliationOutcome.Failed;

        WorkflowRunRecord? run;
        try { run = _runs.Load(runId); }
        catch (Exception ex)
        {
            TryLog("[任务中心] 终局回写无法读取运行记录（保守留待重试）:" + ex.Message);
            return AdmissionTerminalReconciliationOutcome.Failed;
        }
        if (run is null || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
            return AdmissionTerminalReconciliationOutcome.Failed;
        if (run.State is not (WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled))
            return AdmissionTerminalReconciliationOutcome.NotTerminal;

        TerminalReleaseSeal? releaseSeal;
        try
        {
            // Node scopes remain separately consumable; seal them before freezing the final run.
            var operations = _admissionStore.Read().File?.Handoff?.Operations ?? [];
            foreach (var op in operations.Where(o => o.RunBinding == runId && o.OperationType == OperationType.NodeExecution
                && o.RequestState == OperationRequestState.Accepted))
                if (_runs.TrySealTerminalNode(runId, op) is null) return AdmissionTerminalReconciliationOutcome.Pending;
            releaseSeal = _runs.TrySealTerminalRun(runId);
        }
        catch (Exception ex)
        {
            TryLog("[任务中心] 终局封印发布失败，保留责任：" + ex.GetType().Name);
            return AdmissionTerminalReconciliationOutcome.Failed;
        }
        if (releaseSeal is null) return AdmissionTerminalReconciliationOutcome.Pending;

        var timeout = AdmissionTerminalReconciliationTimeoutForTest ?? TimeSpan.FromSeconds(15);
        var settleClock = System.Diagnostics.Stopwatch.StartNew();
        var readAttempt = 0;
        List<OperationRecord>? accepted = null;
        List<OperationRecord>? current = null;
        while (settleClock.Elapsed <= timeout)
        {
            try
            {
                var injected = AdmissionTerminalReadFaultForTest?.Invoke(++readAttempt);
                if (injected is not null) throw injected;
                var leaseRead = _admissionStore.Read();
                if (leaseRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)
                {
                    await Task.Delay(20).ConfigureAwait(false);
                    continue;
                }
                current = leaseRead.File?.Handoff?.Operations?
                    .Where(op => string.Equals(op.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
            }
            catch (IOException)
            {
                await Task.Delay(20).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                TryLog("[任务中心] 终局回写读取失败（保守留待重试）:" + ex.Message);
                return AdmissionTerminalReconciliationOutcome.Failed;
            }

            if (current.Count == 0) return AdmissionTerminalReconciliationOutcome.NoMapping;
            if (current.All(IsAdmissionTerminalOrClosed))
                return current.All(op => op.RequestState != OperationRequestState.TerminalCompleted
                    || op.TerminalReleaseEvidence == (op.OperationType == OperationType.NodeExecution
                        ? "runstore-seal:" + TerminalReleaseEvidence.NodeSeal(_runs.Load(runId)!, op)?.Id
                        : "runstore-seal:" + releaseSeal.Id))
                    ? AdmissionTerminalReconciliationOutcome.Completed : AdmissionTerminalReconciliationOutcome.Pending;

            var ready = current.Where(op => op.RequestState == OperationRequestState.Accepted).ToList();
            var transitioning = current.Any(op => op.RequestState is OperationRequestState.Queued
                or OperationRequestState.InRound or OperationRequestState.Granted or OperationRequestState.Sending);
            if (ready.Count > 0 && !transitioning)
            {
                accepted = ready;
                break;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (accepted is null)
        {
            TryLog("[任务中心] 仲裁操作终局回写等待超时/存在未决责任（保守留待显式重试）。");
            return AdmissionTerminalReconciliationOutcome.Pending;
        }

        // A failure on one sibling must not skip writeback attempts for the other Accepted operations.
        foreach (var op in accepted)
        {
            AdmissionResult? result = null;
            Exception? failure = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    failure = AdmissionTerminalWriteFaultForTest?.Invoke(op.RequestIdentity, attempt);
                    if (failure is not null) throw failure;
                    result = AdmissionTerminalResultForTest?.Invoke(op.RequestIdentity)
                        ?? _admission.MarkOperationTerminal(op.RequestIdentity,
                            op.OperationType == OperationType.NodeExecution
                                ? "runstore-seal:" + TerminalReleaseEvidence.NodeSeal(_runs.Load(runId)!, op)!.Id
                                : "runstore-seal:" + releaseSeal.Id);
                    break;
                }
                catch (IOException ex) when (attempt < 5)
                {
                    failure = ex;
                    await Task.Delay(25).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }
            }
            if (failure is not null || result is null || result.Kind == AdmissionResultKind.Error)
                TryLog($"[任务中心] 仲裁操作终局回写被拒（{result?.ReasonCode ?? failure?.GetType().Name ?? "unknown"}）："
                    + (result?.Detail ?? failure?.Message ?? "无结果，保守留待重试"));
        }

        // Confirm every operation bound to this run after independent write attempts.
        try
        {
            var finalReadFault = AdmissionTerminalReadFaultForTest?.Invoke(++readAttempt);
            if (finalReadFault is not null) throw finalReadFault;
            var finalRead = _admissionStore.Read();
            if (finalRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)
                return AdmissionTerminalReconciliationOutcome.Pending;
            current = finalRead.File?.Handoff?.Operations?
                .Where(op => string.Equals(op.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            TryLog("[任务中心] 终局回写后复核失败（保守留待重试）:" + ex.Message);
            return AdmissionTerminalReconciliationOutcome.Pending;
        }

        if (current.Count == 0) return AdmissionTerminalReconciliationOutcome.NoMapping;
        var finalRun = _runs.Load(runId);
        if (finalRun?.TerminalRelease != releaseSeal || !TerminalReleaseEvidence.ValidRunSeal(finalRun))
            return AdmissionTerminalReconciliationOutcome.Pending;
        return current.All(op => IsAdmissionTerminalOrClosed(op)
                && (op.RequestState != OperationRequestState.TerminalCompleted
                    || op.TerminalReleaseEvidence == (op.OperationType == OperationType.NodeExecution
                        ? "runstore-seal:" + TerminalReleaseEvidence.NodeSeal(finalRun, op)?.Id
                        : "runstore-seal:" + releaseSeal.Id)))
            ? AdmissionTerminalReconciliationOutcome.Completed
            : AdmissionTerminalReconciliationOutcome.Pending;
    }

    private static bool IsAdmissionTerminalOrClosed(OperationRecord operation)
        => operation.RequestState is OperationRequestState.TerminalCompleted
            or OperationRequestState.TerminalRejected or OperationRequestState.NotSelected
            or OperationRequestState.RetryableRejected;
}
