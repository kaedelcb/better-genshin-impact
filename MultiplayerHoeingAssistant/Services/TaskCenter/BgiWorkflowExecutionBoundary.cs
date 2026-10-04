using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// R4.8 Batch B 生产执行边界（IWorkflowExecutionBoundary 的 ext 实现）。
/// 合同（设计稿 onedragon-r4-8-ui-host-boundary-2026-09-19.md §4.1–4.5）：
/// - 提交三态：Accepted（受理回执 accepted+taskHandle）/ Rejected（仅限可证实未受理：本地校验失败或对端副作用前协议拒绝）/
///   Unknown（传输异常/回执畸形/对账未命中/already_executed——不猜成功也不猜失败，引擎 Unknown 停驻）；
/// - 身份冻结（§4.4）：发送前冻结 Epoch/ExpiresAtUtc/幂等键（引擎派生）/完整载荷指纹，Intent=Submitted + SendAttempted 落盘；
/// - 不确定发送后对账：按幂等键 QueryJobListAsync，唯一命中且身份四元（IdempotencyKey/WorkflowRunId/NodeId/Iteration）一致
///   且 Epoch 未变才绑定 jobId（不重发）；否则 Unknown（查不到 ≠ 未执行证明）；
/// - 终态纯观察（§4.2/4.3）：AwaitTerminalAsync 不发取消；skipped=合法终态（D15，消费 Job 原值重解释）；
///   取消走 RequestCancelAsync（ext.task.cancel ownedOnly=v1，best-effort）；
/// - suppressConfigCompletionAction 按引擎传入（任务中心固定 true，B6/E4'）。
/// I6 无状态：全部事实在 RunStore 记录与客户端能力快照。
/// </summary>
public sealed class BgiWorkflowExecutionBoundary : IWorkflowExecutionBoundary
{
    /// <summary>
    /// 冻结提交（R5.2 B2-γ 边界拆分产物）：第 1 段产出的「身份已冻结、可发送」载荷；
    /// 或可证实未受理的拒绝（此时其余字段为 null）。
    /// </summary>
    /// <summary>
    /// 冻结提交（R5.2 B2-γ 边界拆分产物）：第 1 段产出的「身份已冻结、可发送」载荷；
    /// 或可证实未受理的拒绝（此时其余字段为 null）。
    /// **构造与消费的责任边界（会诊复核修正，勿夸大）**：构造函数私有，但 `Ok`/`No` 是 `internal static`
    /// ——同程序集代码**仍可**调用 `Ok` 另造包装。「拒绝对象不被发送」与「字段残缺对象不被发送」由
    /// 发送段入口承担；「同一**凭据实例**不被重复发送」由消费登记（绑定 `WorkflowSubmission` 对象）承担。
    /// 以上都只是**进程内误用防护**，既不是安全边界，也**不能替代门面的持久化唯一发送许可**；
    /// 同业务身份经复制/重载形成**另一个** submission 实例时，CWT 登记无法覆盖——那一层由门面负责。
    /// </summary>
    internal sealed class PreparedSubmit
    {
        private int _consumed;
        private int _portCallStarted;
        internal PreparedSendPermit? FrozenPermit { get; }
        internal WorkflowStopAuthorityRecord? FrozenStopAuthority => _stopAuthority;
        private readonly string? _runId;
        private readonly string? _expiresAtUtc;
        private readonly WorkflowStopAuthorityRecord? _stopAuthority;

        /// <summary>
        /// **不可变对账身份快照**（会诊回溯复核）：发送载荷用的是准备时冻结的值，对账必须用**同一组值**，
        /// 不得改读仍然可变的 `submission.*`／`run.WireRunId`——否则准备后这些字段被改写会造成
        /// 「按纪元 A 发送、按纪元 B 对账」，合法命中被拒或错误命中被接受。
        /// </summary>
        /// <summary>[P7／§12.2 第 3 项] **`Attempt` 必须纳入冻结身份**：§12.2 第 3 项要求「核对
        /// occurrence/iteration/**attempt**/提交键/游标修订」——attempt 前进＝重试轮次变更，旧命中不得据以绑定受理事实
        /// （游标修订的核对仍在门面层按 `CursorRevision` 执行，不在此重复）。</summary>
        internal sealed record ReconcileIdentity(
            string Epoch, string Key, string WireRunId, string NodeId, int Occurrence, int LoopIteration, int Attempt,
            FrozenOriginalRequestEvidence? RequestEvidence = null);

        private PreparedSubmit(WorkflowRunRecord? run, WorkflowSubmission? submission, object? payload,
            BoundarySubmitResult? rejection, ReconcileIdentity? reconcile)
        {
            Run = run;
            Submission = submission;
            Payload = payload;
            Rejection = rejection;
            Reconcile = reconcile;
            FrozenFingerprint = submission?.Fingerprint;
            FrozenPermit = submission?.SendPermit;
            _runId = run?.RunId;
            _expiresAtUtc = submission?.ExpiresAtUtc;
            _stopAuthority = run?.StopAuthority;
        }

        public WorkflowRunRecord? Run { get; }
        public WorkflowSubmission? Submission { get; }
        public object? Payload { get; }
        public string Operation => BgiExternalClient.ExternalOperations.TaskStart;
        public string? FrozenFingerprint { get; }
        public BoundarySubmitResult? Rejection { get; }

        /// <summary>冻结的对账身份（拒绝分支为 null）。</summary>
        public ReconcileIdentity? Reconcile { get; }

        /// <summary>
        /// 构造可证实未受理的拒绝结果（第 1 段未通过时唯一出口）。
        /// </summary>
        internal static PreparedSubmit No(BoundarySubmitResult rejection)
        {
            ArgumentNullException.ThrowIfNull(rejection);
            // 会诊回溯复核：拒绝槽位承载 Accepted/Unknown 会让发送段把非拒绝当拒绝返回
            // （反例 `No(AcceptedWith(...))` → 零发送却报 Accepted）。工厂必须自守该不变量。
            if (rejection.Accepted || rejection.Uncertain)
                throw new ArgumentException("拒绝分支只接受可证实未受理的结果。", nameof(rejection));
            return new(null, null, null, rejection, null);
        }

        /// <summary>构造可发送的冻结载荷（**内部信任边界**：不校验提交/记录/指纹是否匹配——见类型注释）。</summary>
        internal static PreparedSubmit Ok(WorkflowRunRecord run, WorkflowSubmission submission, object payload)
            => new(run, submission, payload, null,
                new ReconcileIdentity(submission.Epoch ?? "", submission.Key ?? "", run.WireRunId ?? "",
                    submission.NodeId ?? "", submission.Occurrence, submission.LoopIteration, submission.Attempt,
                    submission.OriginalRequestEvidence));

        /// <summary>
        /// 一次性消费护栏（仅防**进程内重复调用**；持久化的发送授权责任仍在门面，绝不由本护栏替代）。
        /// **消费状态绑定到底层冻结凭据（WorkflowSubmission 实例）而非本包装实例**——否则调用方可把同一
        /// 载荷重新包装成新实例（计数从零）而绕过护栏（会诊复核反例）。
        /// **前置条件（本层不保证、须由引擎级夹具证明）**：合法重试必须由引擎签发**新的**
        /// `WorkflowSubmission` 实例；若某条重试路径复用同一实例，会被本护栏一并挡住。
        /// **当前调用链证据（会诊要求，已核实）**：`SubmitAndAwaitAsync` 的生产调用点**只有一处**
        /// （`WorkflowRunner.cs:467`，节点循环内），且其中 `attempt` 为 `const int = 1`、注明「有界重试机制挂账
        /// R4.6+」——即**本层当前不存在透明重试路径**，故不存在「合法重试被本护栏挡住」的情形；每次调用都在
        /// `WorkflowRunner.cs:589` **新建** `WorkflowSubmission`，经 `RecordIntent` 替换 `run.CurrentSubmission`。
        /// 将来实现重试时，必须按 §3.2a/§3.3 以**新 attempt + 新提交身份（新实例）**重新准入，不得复用旧实例。
        /// 另注：登记先于端口调用，故无论前次是 Accepted／协议拒绝／异常／取消，同一实例都不再放行；
        /// **不得**在异常或取消时移除登记（那会重新打开不确定发送后的重复执行窗口）。
        /// </summary>
        public bool TryConsume()
        {
            if (Submission is not { } submission) return false;      // 拒绝对象无凭据：发送段在此之前已返回
            if (!_consumedSubmissions.TryAdd(submission, submission)) return false;
            Interlocked.Exchange(ref _consumed, 1);
            return true;
        }

        internal void MarkPortCallStarted() => Interlocked.Exchange(ref _portCallStarted, 1);

        internal LocalNoSendProof? CreateNoSendProof(string? transportEvidence = null)
        {
            if (Volatile.Read(ref _consumed) != 1
                || (transportEvidence is null ? Volatile.Read(ref _portCallStarted) != 0
                    : Volatile.Read(ref _portCallStarted) != 1 || !BgiNotSentException.IsKnownEvidenceCode(transportEvidence))
                || Reconcile is not { } identity || Payload is null || _runId is null
                || _expiresAtUtc is null || _stopAuthority is null || FrozenFingerprint is null || FrozenPermit is not { Version: 1, Consumed: false }) return null;
            var payloadFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Payload)))[..24].ToLowerInvariant();
            if (payloadFingerprint != FrozenFingerprint) return null;
            return new LocalNoSendProof(transportEvidence is null ? LocalNoSendEvidence.PreparedStop : LocalNoSendEvidence.TransportNotSent, FrozenPermit.Nonce, _runId,
                identity.WireRunId, identity.Epoch, identity.Key, identity.NodeId, identity.Occurrence,
                identity.LoopIteration, identity.Attempt, FrozenFingerprint, _expiresAtUtc, _stopAuthority, transportEvidence);
        }

        /// <summary>进程内「已消费的冻结凭据」登记（弱引用表：不阻止 submission 被回收）。</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorkflowSubmission, WorkflowSubmission> _consumedSubmissions = new();
    }

    private static readonly TimeSpan ExpireWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>终态观察预算上限（节点执行可达数小时；取消/暂停经令牌退出，预算是兜底而非语义）。</summary>
    private static readonly TimeSpan ObserveBudget = TimeSpan.FromHours(24);

    /// <summary>不确定发送后的对账窗口（cleanup 语义，有界）。</summary>
    private static readonly TimeSpan ReconcileBudget = TimeSpan.FromSeconds(5);

    private readonly IBgiExecutionPort _port;
    private readonly RunStore _runs;

    public BgiWorkflowExecutionBoundary(BgiExternalClient client, RunStore runs)
        : this(new BgiExternalClientPort(client ?? throw new ArgumentNullException(nameof(client))), runs)
    {
    }

    /// <summary>端口构造（R5.2 B2-γ：组件级夹具注入可控端口；生产经上面的客户端构造走最薄转发）。</summary>
    internal BgiWorkflowExecutionBoundary(IBgiExecutionPort port, RunStore runs)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    /// <summary>单配置原生执行能力实况（D4；BGI 侧当前恒 false，R4.10 集成验收后评估开放）。</summary>
    public bool RequiresStopAuthority => true;
    public async Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string intentId, long intentTimestamp, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var fence = await WorkflowStopAuthority.QueryAsync(_port, budget.Token).ConfigureAwait(false);
        return fence is null ? null : WorkflowStopAuthority.FromExplicitIntent(fence, intentId, intentTimestamp);
    }

    public async Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
    {
        var fence = await WorkflowStopAuthority.QueryAsync(_port, ct).ConfigureAwait(false);
        if (fence is null || fence.Epoch != authority.Epoch || fence.Frequency != authority.MonotonicFrequency) return null;
        return fence.Version == authority.Version;
    }

    public bool SingleNativeSupported => _port.IsReady
                                         && _port.HasCapability("task.single.native");

    /// <summary>收尾抑制能力实况（B6/E4'；缺能力时 Planner 预检响亮拒绝整龙/配置组流程）。</summary>
    public bool SuppressConfigCompletionSupported => _port.IsReady
        && _port.HasCapability(BgiExternalClient.CapabilitySuppressConfigCompletionAction);

    /// <summary>
    /// 统一提交入口（与非接线路径逐字等价）：本地校验+身份冻结 → 锁外发送 → 三态对账。
    /// R5.2 B2-γ 接线次序（会诊定稿，勿照旧解读为「两段之间插仲裁」）：**门面锁内占位 → Sender 内
    /// 准备（本方法第 1 段）→ 发送（第 2 段）→ 门面三态对账**。即仲裁在准备之前，不在两段之间。
    /// </summary>
    public async Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
    {
        var prepared = PrepareSubmit(request);
        if (prepared.Rejection is { } rejection) return rejection;
        return await SendPreparedAsync(prepared, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 第 1 段：本地校验 + 身份冻结（**发送前，无远端副作用**）。
    /// 通过=返回可发送的冻结载荷；未通过=返回可证实未受理的 Rejected。
    /// </summary>
    internal PreparedSubmit PrepareSubmit(WorkflowSubmitRequest request, string? authorizedEpoch = null, string? originalSendIdentity = null)
    {
        var run = request.Run;
        if (_runs.Load(run.RunId)?.TerminalRelease is not null)
            return PreparedSubmit.No(BoundarySubmitResult.Rejected("运行已终局封印，禁止重新准备。"));
        var occurrence = request.Occurrence;
        var node = request.Node;

        // 1) 意图身份校验（引擎纪律：RecordIntent 先行；不符 = 本地违例，可证实未发送 → Rejected）
        var submission = run.CurrentSubmission;
        var renewal = submission is not null && RunStore.CanRenewPreparedSubmission(run, submission, originalSendIdentity);
        if (submission is null
            || submission.WireRunId is not null && submission.WireRunId != run.WireRunId
            || submission.NodeId != occurrence.NodeId
            || submission.Occurrence != occurrence.Occurrence
            || submission.LoopIteration != occurrence.LoopIteration
            || submission.Intent != SubmitIntentState.IntentRecorded && !renewal)
            return PreparedSubmit.No(BoundarySubmitResult.Rejected("提交意图缺失或身份不符（违反引擎纪律：先落盘意图再提交；未发送）"));

        // 2) 资源映射 + 账号合同（本地校验失败 = 可证实未发送 → Rejected）
        if (TryMapResource(node, out var groupName, out var configName, out var taskId, out var expectedRevision) is { } mapError)
            return PreparedSubmit.No(BoundarySubmitResult.Rejected(mapError));
        if (TryExtractExpectedUid(node, out var expectedUid) is { } uidError)
            return PreparedSubmit.No(BoundarySubmitResult.Rejected(uidError));
        // R4.10 终审复核（重要5）：纪元单次读取——空检查与冻结使用同一局部值（断连清空属性时二次读取会 NRE 于发送 try 之外）
        var epoch = _port.ServerEpoch;
        if (epoch is null)
            return PreparedSubmit.No(BoundarySubmitResult.Rejected("BGI 进程纪元未知（严格合同要求 bgiEpoch；未发送）"));
        // 会诊复核：纪元标量一次性捕获——比较文本、持久化 epoch、线协议载荷必须来自同一组值，
        // 否则「比较通过后纪元对象被原地改写」会让三者不一致（不得依赖纪元对象不可变这一未证前提）。
        var epochProcessId = epoch.ProcessId;
        var epochStartTicks = epoch.StartTicksUtc;
        // 会诊阻断项 2：接线态下本段在门面「锁内占位」之后、锁外发送之前执行。若期间连接切到新纪元，
        // 冻结新纪元会让「服务端执行对象」与「门面授权对象」不一致。故携带本轮授权的固定目标 epoch 比对：
        // 不一致一律可证实未发送地拒绝（绝不把新 epoch 写进旧授权对应的发送身份）。
        var epochText = $"{epochProcessId}:{epochStartTicks}";
        if (authorizedEpoch is not null && !string.Equals(epochText, authorizedEpoch, StringComparison.Ordinal))
            return PreparedSubmit.No(BoundarySubmitResult.Rejected("授权目标纪元与本机当前纪元不一致（stale_epoch；未发送）"));

        if (run.StopRequested || run.StopAuthority is not { } authority || authority.Epoch != epochText
            || !_port.HasCapability(WorkflowStopAuthority.Capability))
            return PreparedSubmit.No(BoundarySubmitResult.Rejected("停止授权缺失/已停止/纪元变化（未发送）"));

        // 3) 冻结：纪元/有效期/指纹 → Intent=Submitted + SendAttempted 落盘（即将发送事实；此后缺 jobId ≠ 未发送）
        // [P7／§12.2 第 3 项「字段合并」] 冻结事实以**盘上最新记录**为基线做选择性更新（自有字段＝冻结字段＋意图），
        // 并发写入者改动的**非自有字段**由此保留；同时**重新核对本次提交身份**（节点/出现次数/轮次/attempt/提交键），
        // 身份已被并发推进＝可证实未发送地拒绝（不得把旧身份发送出去）。
        var frozenEpoch = epochText;
        var frozenExpiresAt = renewal ? submission.ExpiresAtUtc! : DateTimeOffset.UtcNow.Add(ExpireWindow).ToString("O");
        var frozenKey = submission.Key;
        var frozenNodeId = submission.NodeId;
        var frozenOccurrence = submission.Occurrence;
        var frozenLoopIteration = submission.LoopIteration;
        var frozenAttempt = submission.Attempt;
        var payload = new
        {
            executionContractVersion = 1,
            expectedStopVersion = authority.Version,
            takeoverTicket = _port.TakeoverTicket,
            idempotencyKey = submission.Key,
            expiresAtUtc = frozenExpiresAt,
            bgiEpoch = new { processId = epochProcessId, startTicksUtc = epochStartTicks },
            workflowRunId = run.WireRunId,
            nodeId = occurrence.NodeId,
            iteration = occurrence.LoopIteration,
            occurrence = occurrence.Occurrence,
            attempt = submission.Attempt,
            groupName,
            configName,
            taskId,
            expectedConfigRevision = expectedRevision,
            expectedUid,
            suppressConfigCompletionAction = request.SuppressConfigCompletionAction,
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(payload)))[..24].ToLowerInvariant();
        var originalOperation = BgiExternalClient.ExternalOperations.TaskStart;
        var originalEvidence = new FrozenOriginalRequestEvidence(BgiOriginalRequestFingerprint.Version,
            BgiOriginalRequestFingerprint.Compute(originalOperation, JsonSerializer.Serialize(payload)),
            originalOperation, taskId, expectedRevision);
        // **身份与合法前态必须在同一锁内先核对、核对通过后才允许写字段**（会诊阻断处置）：
        // 前置条件不成立 ⇒ `UpdateMergingIf` 返回 false ⇒ **零发布、零修订推进**，调用方内存视图也不被污染。
        var merged = _runs.TryPrepareSubmission(run.RunId!, latest =>
        {
            var live = latest.CurrentSubmission;
            if (latest.StopRequested || latest.StopAuthority != authority) return false;
            if (live is null) return false;
            var renewing = RunStore.CanRenewPreparedSubmission(latest, live, originalSendIdentity);
            if (live.Intent != SubmitIntentState.IntentRecorded && !renewing) return false;
            if (renewing && (live.Epoch != frozenEpoch || live.ExpiresAtUtc != frozenExpiresAt
                || live.Fingerprint != fingerprint || live.OriginalRequestEvidence != originalEvidence)) return false;
            if (!string.Equals(latest.WireRunId, run.WireRunId, StringComparison.Ordinal)
                || !string.Equals(live.NodeId, frozenNodeId, StringComparison.Ordinal)
                || live.Occurrence != frozenOccurrence
                || live.LoopIteration != frozenLoopIteration
                || live.Attempt != frozenAttempt
                || !string.Equals(live.Key, frozenKey, StringComparison.Ordinal))
                return false;                                                    // 身份已被并发推进
            // [G4-residual①·本批处置] 权威游标并入同一冻结 CAS：宿主发送侧检查（`successor_cursor_changed`）与本次
            // `UpdateMergingIf` 之间存在真实窗口——并发写入者可在此期间把游标推进到下一节点而保留提交键/attempt，
            // 使旧节点越过宿主检查进入本冻结。把「盘上最新游标仍指向本次出现身份」纳入本 CAS 的前置：游标已推进
            // ⇒ 零发布、零冻结事实、零发送（可证实未发送地拒绝），不留窗口。只比对游标字段值，不比 RecordRevision（误拒无关更新）。
            if (latest.Cursor is not { } liveCursor
                || !string.Equals(liveCursor.NodeId, occurrence.NodeId, StringComparison.Ordinal)
                || liveCursor.Occurrence != occurrence.Occurrence
                || liveCursor.LoopIteration != occurrence.LoopIteration)
                return false;                                                    // 游标已被并发推进（零发送）
            // 自有字段（冻结事实＋意图）；其余字段（并发写入者改动）不动。
            live.Epoch = frozenEpoch;
            live.ExpiresAtUtc = frozenExpiresAt;
            live.Fingerprint = fingerprint;
            live.OriginalRequestEvidence = originalEvidence;
            live.WireRunId ??= latest.WireRunId;
            live.SendAttempted = true;
            live.Intent = SubmitIntentState.Submitted;
            return true;
        }, out var latestRecord, originalSendIdentity);
        if (!merged || latestRecord is null)
            return PreparedSubmit.No(BoundarySubmitResult.Rejected(
                "运行记录缺失或提交身份已被并发推进（合法前态/身份核对未通过；未发送、未发布冻结事实）"));
        // **旧对象 rebase**：把盘上最新字段整体同步回调用方对象（含并发写入者的改动），
        // 使调用方后续 `Update` 不会把并发改动整对象覆盖（会诊阻断处置：只回写修订号＝洗白旧对象）。
        RunStore.RebaseOnto(run, latestRecord);
        submission = run.CurrentSubmission!;   // rebase 后以盘上实例为准（发送与对账按同一组冻结值）
        submission.Epoch = frozenEpoch;
        submission.ExpiresAtUtc = frozenExpiresAt;
        submission.Fingerprint = fingerprint;
        submission.SendAttempted = true;
        submission.Intent = SubmitIntentState.Submitted;

        return PreparedSubmit.Ok(run, submission, payload);
    }

    /// <summary>
    /// 第 2 段：锁外发送一次 + 三态对账（绝不重发；不确定 → 按幂等键对账）。
    /// 本段不做任何仲裁判定、也不得取门面信号量——它既是直通路径的发送段，也是接线态下仲裁面
    /// Sender 回调的目标（门面 Sender 在锁外调用，回环不得重入 `_gate`）。
    /// </summary>
    internal async Task<BoundarySubmitResult> SendPreparedAsync(PreparedSubmit prepared, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        // 会诊阻断项 1：拒绝对象永不发送；字段残缺=内部违例，响亮未知且不发送；同一对象只允许消费一次。
        if (prepared.Rejection is { } rejected) return rejected;
        if (prepared.Run is not { } run || prepared.Submission is not { } submission || prepared.Payload is not { } payload)
            return BoundarySubmitResult.UnknownWith("冻结载荷不完整（内部违例），未发送、待对账");
        if (!prepared.TryConsume())
            return BoundarySubmitResult.UnknownWith("冻结载荷已被消费（内部违例：禁止重复发送），未重发");
        WorkflowRunRecord? beforeSend;
        try
        {
            if (!_runs.TryConsumePreparedSubmission(prepared, out beforeSend) || beforeSend is null)
                return BoundarySubmitResult.UnknownWith("原发送许可缺失、已消费或权威游标/冻结事实不符；未调用端口，保留责任。");
            RunStore.RebaseOnto(run, beforeSend);
            submission = run.CurrentSubmission!;
            if (beforeSend?.TerminalRelease is not null
                || beforeSend?.NodeReleaseSeals.Any(s => s.SubmissionIdentity == submission.AcceptedSendIdentity) == true)
                return BoundarySubmitResult.UnknownWith("原责任已封印，禁止迟到发送。");
            if (beforeSend is { StopRequested: true })
            {
                if (!_runs.TryPublishPreparedNoSend(prepared, out var noSendRecord) || noSendRecord is null)
                    return BoundarySubmitResult.UnknownWith("本地零调用证明与原身份或既有事实冲突，保留责任。");
                RunStore.RebaseOnto(run, noSendRecord);
                return BoundarySubmitResult.Rejected("停止后同冻结提交未调用本端口，本地零调用证明已耐久发布。");
            }
        }
        catch (Exception ex)
        {
            return BoundarySubmitResult.UnknownWith("发送前记录/零调用证明不可确认：" + ex.GetType().Name);
        }
        if (beforeSend is null || beforeSend.StopAuthority != run.StopAuthority)
            return BoundarySubmitResult.UnknownWith("冻结后停止权威不可确认，未调用发送；原可能发送事实保留待对账");

        // 4) 发送一次（绝不重发；不确定 → 对账）
        BgiExternalResponse response;
        try
        {
            prepared.MarkPortCallStarted();
            response = await _port.SendCommandAsync(
                BgiExternalClient.ExternalOperations.TaskStart, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 发送窗口取消（Stop）：cleanup 令牌对账——命中绑定 jobId 落盘并即发远端取消（引擎 Stop 路径随后再确认），
            // 未命中保留 SendAttempted 事实；随后重抛 OCE（取消纪律不吞）
            await ReconcileAfterUncertainSendAsync(run, submission, prepared.Reconcile!, cancelOnHit: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var reconciled = await ReconcileAfterUncertainSendAsync(run, submission, prepared.Reconcile!, cancelOnHit: false).ConfigureAwait(false);
            if (reconciled is not null) return reconciled;
            // **[P8／§24.62 判据接缝]** 两类失败**严格分离**：
            // ①**可证实未发送**（证据载体 `BgiNotSentException`：本进程在任何字节写入线路之前失败——通道未就绪/
            //   管道未连接/本地请求被拒）⇒ 远端**不可能**存在本笔受理事实 ⇒ **确定拒绝＋开重试窗口**
            //   （§3.2a「无损拒绝类」）；对账仍先执行（一旦命中即证明证据有误，按命中结果回执，绝不硬判未发送）。
            // ②**其余一切异常**（写入后超时/部分写入/未知异常）⇒ 可能已进入线路 ⇒ **保留未决责任**（Unknown 停驻，
            //   禁重发）——现状不变，不得仅凭「异常看起来弱」升级为未发送。
            if (IsProvenNotSent(ex))
            {
                if (!_runs.TryPublishPreparedNoSend(prepared, out var notSentRecord, NotSentEvidence(ex)) || notSentRecord is null)
                    return BoundarySubmitResult.UnknownWith("本地未写入证据无法绑定原消费许可，保留责任。");
                RunStore.RebaseOnto(run, notSentRecord);
                return BoundarySubmitResult.RejectedWithRetryWindow(
                    $"发送前即可证实未发送（证据 {NotSentEvidence(ex)}）：未产生远端受理事实，按无损拒绝开重试窗口");
            }
            return BoundarySubmitResult.UnknownWith(
                $"发送结果不可考（{ex.GetType().Name}），按幂等键对账未命中（不重发，待人工/恢复对账）");
        }

        if (!response.Success)
        {
            if (BgiServerRejectionEvidence.HasTypedDisposition(response.Data))
            {
                if (prepared.Reconcile is not { } frozen || prepared.FrozenFingerprint is not { Length: > 0 } frozenFingerprint)
                    return BoundarySubmitResult.UnknownWith("拒绝观察缺少冻结请求身份，保留原发送责任。");
                var proof = BgiServerRejectionEvidence.Verify(response, prepared.Operation,
                    payload, frozen.Epoch, frozen.Key, frozenFingerprint, _port.ServerEpoch);
                if (proof is null)
                    return BoundarySubmitResult.UnknownWith("类型化建job前拒绝的纪元/载荷不符，保留原发送责任。");
                var saved = _runs.UpdateMergingIf(run.RunId, latest =>
                {
                    var live = latest.CurrentSubmission;
                    if (live is null || latest.WireRunId != frozen.WireRunId || live.Key != frozen.Key || live.Epoch != proof.Epoch
                        || live.Fingerprint != proof.Fingerprint || live.NodeId != frozen.NodeId
                        || live.Occurrence != frozen.Occurrence || live.LoopIteration != frozen.LoopIteration
                        || live.Attempt != frozen.Attempt || live.Intent == SubmitIntentState.Accepted
                        || !string.IsNullOrEmpty(live.JobId) || !string.IsNullOrEmpty(live.AcceptedSendIdentity)) return false;
                    live.ServerRejectionEvidence = proof;
                    live.ObservedTerminal = "rejected";
                    live.ExecutionExitConfirmed = true;
                    live.ExecutionExitDisposition = "never_started";
                    live.EffectState = "rejected";
                    live.Intent = SubmitIntentState.Rejected;
                    return true;
                }, out var latest);
                if (!saved || latest is null)
                    return BoundarySubmitResult.UnknownWith("拒绝证据与已受理身份冲突，保留原责任。");
                RunStore.RebaseOnto(run, latest);
                return BoundarySubmitResult.Rejected("执行端已证明建job前拒绝：" + response.ErrorCode);
            }
            return BoundarySubmitResult.UnknownWith($"非类型化失败（{response.ErrorCode ?? "无错误码"}），保留原发送责任并只读对账");
        }

        var acceptance = BgiJobTerminalPolling.ParseAcceptance(response.Data);
        if (acceptance.AlreadyExecuted)
        {
            // 同键已执行回执正常不可达（引擎不重复提交同 submission）——不猜成功，Unknown 停驻待对账
            return BoundarySubmitResult.UnknownWith("同键已执行回执（already_executed，正常不可达），结果不可考不重发");
        }
        if (!acceptance.Accepted || acceptance.TaskHandle is null)
            return BoundarySubmitResult.UnknownWith("受理回执缺 taskHandle（协议违例），结果不可考");
        return BoundarySubmitResult.AcceptedWith(acceptance.TaskHandle);
    }

    /// <summary>纯观察终态（不发取消）。 skipped=合法终态（D15）：消费轮询返回的 Job 原值重解释（一轮 I3）。</summary>
    public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
    {
        var (outcome, job, reason) = await BgiJobTerminalPolling.PollUntilTerminalAsync(
            _port, jobId, ObserveBudget, PollInterval, ct).ConfigureAwait(false);
        if (job is not null)
        {
            var (word, r) = InterpretNodeJob(job);
            if (word is not null) return BoundaryTerminalResult.Observed(word, r, job.ErrorCode);
        }
        return outcome == "unknown"
            ? BoundaryTerminalResult.UncertainWith(reason ?? "终态不可考")
            : BoundaryTerminalResult.Observed(outcome, reason); // 保底：无 Job 原值时按轮询 Outcome
    }

    /// <summary>请求远端取消（ext.task.cancel，ownedOnly=v1；best-effort——应答不代表清理完成，终态以观察为准）。</summary>
    public async Task<BoundaryTerminalResult> AwaitSubmissionExitAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
    {
        var identity = new BgiJobTerminalPolling.FrozenIdentity(submission.Epoch, submission.Key,
            run.WireRunId, submission.NodeId, submission.LoopIteration, submission.Occurrence, submission.Attempt);
        var jobId = submission.JobId!;
        var fingerprint = submission.Fingerprint;
        var original = submission.OriginalRequestEvidence;
        if (original is null)
            return BoundaryTerminalResult.UncertainWith("缺少原冻结载荷证据，不凭既有 jobId 结清");
        var (outcome, job, reason) = await BgiJobTerminalPolling.PollUntilExitAsync(
            _port, identity, jobId, ObserveBudget, TimeSpan.FromMilliseconds(200), ct,
            observed => BgiWorkflowObservationPersistence.Save(_runs, run, submission, identity, fingerprint, jobId, observed, original),
            observed => OriginalRequestMatches(observed, original)).ConfigureAwait(false);
        return outcome != "unknown" && job is not null
            ? BoundaryTerminalResult.Observed(job.State!, reason, job.ErrorCode, exitConfirmed: true, exitDisposition: job.ExecutionExitDisposition)
            : BoundaryTerminalResult.UncertainWith(reason ?? "同身份退出未确认");
    }

    public Task<BoundarySubmitResult> ReconcileSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
        => ReconcileSubmissionAsync(run, submission, ct, null);

    public async Task<BoundarySubmitResult> ReconcileSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct, string? acceptedSendIdentity)
    {
        var identity = new PreparedSubmit.ReconcileIdentity(submission.Epoch ?? "", submission.Key,
            run.WireRunId, submission.NodeId, submission.Occurrence, submission.LoopIteration, submission.Attempt,
            submission.OriginalRequestEvidence);
        return await ReconcileAfterUncertainSendAsync(run, submission, identity, cancelOnHit: false, acceptedSendIdentity).ConfigureAwait(false)
            ?? BoundarySubmitResult.UnknownWith("原键只读对账无唯一同身份命中；不重发");
    }

    /// <summary>[G7-residual·本批] **历史提交只读对账**（提交已移入 SubmissionHistory，不再是 CurrentSubmission）：
    /// 与 <see cref="ReconcileAfterUncertainSendAsync"/> 同一「三重纪元＋唯一命中＋完整身份四元」判据，但**不写任何记录**
    /// （历史提交不在 CurrentSubmission，不适用该路径的引用核对与 CAS）。唯一命中返回 jobId；否则 null（Unknown，绝不重发）。
    /// 供宿主对「缺完整发送身份的历史提交」取证后调用 <c>TryAppendRecoveryAssociation</c> 追加恢复关联。</summary>
    public Task<string?> ReconcileHistoricalSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
        => ReconcileHistoricalSubmissionAsync(run, submission, ct, submission.SendPermit?.OriginalSendIdentity);

    public async Task<string?> ReconcileHistoricalSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct, string? originalSendIdentity)
    {
        try
        {
            if (run.TerminalRelease is not null || originalSendIdentity is not { Length: > 0 }
                || submission.SendPermit is not { Version: 1, Consumed: true } permit
                || !Guid.TryParseExact(permit.Nonce, "N", out _) || permit.OriginalSendIdentity != originalSendIdentity
                || submission.AcceptedSendIdentity is { Length: > 0 } accepted && accepted != originalSendIdentity
                || run.SubmissionHistory.Count(s => TerminalReleaseEvidence.Hash(s) == TerminalReleaseEvidence.Hash(submission)) != 1
                || !TerminalReleaseEvidence.BodySettled(run, submission) || string.IsNullOrEmpty(submission.JobId)) return null;
            var historicalHash = TerminalReleaseEvidence.Hash(submission);
            var originalPermit = permit;
            var identity = new PreparedSubmit.ReconcileIdentity(submission.Epoch ?? "", submission.Key,
                run.WireRunId, submission.NodeId, submission.Occurrence, submission.LoopIteration, submission.Attempt,
                submission.OriginalRequestEvidence);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ReconcileBudget);
            var epochBefore = _port.ServerEpoch is { } eb ? $"{eb.ProcessId}:{eb.StartTicksUtc}" : null;
            var snapshot = await _port.QueryJobListAsync(budget.Token).ConfigureAwait(false);
            var epochAfter = _port.ServerEpoch is { } e ? $"{e.ProcessId}:{e.StartTicksUtc}" : null;
            var hit = TryMatchReconcileHit(snapshot, epochBefore, epochAfter, identity.Epoch,
                identity.Key, identity.WireRunId, identity.NodeId, identity.LoopIteration, identity.Occurrence, identity.Attempt);
            if (!OriginalRequestMatches(hit, identity.RequestEvidence) || hit!.JobId != submission.JobId
                || !BgiJobTerminalPolling.IsTerminal(hit.State) || hit.State != submission.ObservedTerminal
                || !hit.ExecutionExitConfirmed || hit.ExecutionExitDisposition != submission.ExecutionExitDisposition
                || hit.ExecutionExitDisposition is not ("execution_exited" or "never_started")) return null;
            var readback = _runs.Load(run.RunId!);
            return readback?.TerminalRelease is null && readback?.WireRunId == run.WireRunId
                && readback.SubmissionHistory.Count(s => TerminalReleaseEvidence.Hash(s) == historicalHash && s.SendPermit == originalPermit) == 1
                ? hit.JobId : null;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }   // 通道/纪元/查询瞬态：Unknown 保守，绝不据瞬态判受理
    }

    internal async Task<BgiJobInfo?> ObserveHistoricalExecutionAsync(WorkflowRunRecord run, WorkflowSubmission submission,
        string originalSendIdentity, CancellationToken ct)
    {
        try
        {
            if (run.TerminalRelease is not null || !run.StopRequested
                || submission.SendPermit is not { Version: 1, Consumed: true } permit
                || !Guid.TryParseExact(permit.Nonce, "N", out _) || permit.OriginalSendIdentity != originalSendIdentity
                || submission.AcceptedSendIdentity is { Length: > 0 } accepted && accepted != originalSendIdentity
                || string.IsNullOrEmpty(submission.JobId) || submission.WireRunId != run.WireRunId
                || !submission.SendAttempted || string.IsNullOrEmpty(submission.Fingerprint) || string.IsNullOrEmpty(submission.ExpiresAtUtc)) return null;
            var hash = TerminalReleaseEvidence.Hash(submission);
            if (run.SubmissionHistory.Count(s => TerminalReleaseEvidence.Hash(s) == hash) != 1) return null;
            var identity = new BgiJobTerminalPolling.FrozenIdentity(submission.Epoch, submission.Key, run.WireRunId,
                submission.NodeId, submission.LoopIteration, submission.Occurrence, submission.Attempt);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ReconcileBudget);
            var hit = await BgiJobTerminalPolling.FindOriginalJobAsync(_port, identity, budget.Token).ConfigureAwait(false);
            if (hit?.JobId != submission.JobId || !OriginalRequestMatches(hit, submission.OriginalRequestEvidence)
                || WorkflowStopAuthority.Epoch(hit!.Epoch) != submission.Epoch) return null;
            // An active original may only be cancelled through the full original identity. The RPC is not exit evidence.
            if (!BgiJobTerminalPolling.IsTerminal(hit.State))
            {
                if (!_port.HasCapability("execution.cancel.identity.v1")) return null;
                await _port.CancelOriginalJobAsync(hit.JobId!, identity, budget.Token).ConfigureAwait(false);
            }
            var (outcome, exited, _) = await BgiJobTerminalPolling.PollUntilExitAsync(_port, identity, hit.JobId!,
                ObserveBudget, PollInterval, budget.Token, validateOriginalPayload:
                    job => OriginalRequestMatches(job, submission.OriginalRequestEvidence)).ConfigureAwait(false);
            if (outcome == "unknown" || exited is null
                || BgiJobTerminalPolling.IsTerminal(submission.ObservedTerminal) && exited.State != submission.ObservedTerminal
                || submission.ExecutionExitConfirmed && exited.ExecutionExitDisposition != submission.ExecutionExitDisposition) return null;
            var readback = _runs.Load(run.RunId);
            return readback?.TerminalRelease is null && readback?.WireRunId == run.WireRunId && readback.StopRequested
                && readback.SubmissionHistory.Count(s => TerminalReleaseEvidence.Hash(s) == hash && s.SendPermit == permit) == 1
                ? exited : null;
        }
        catch { return null; }
    }

    public async Task RequestSubmissionCancelAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
    {
        var identity = new BgiJobTerminalPolling.FrozenIdentity(submission.Epoch, submission.Key,
            run.WireRunId, submission.NodeId, submission.LoopIteration, submission.Occurrence, submission.Attempt);
        if (!identity.Complete || WorkflowStopAuthority.Epoch(_port.ServerEpoch) != identity.Epoch
            || !_port.HasCapability("execution.cancel.identity.v1") || string.IsNullOrEmpty(submission.JobId)) return;
        var original = submission.OriginalRequestEvidence;
        if (original is null) return;
        BgiJobInfo? hit;
        try { hit = await BgiJobTerminalPolling.FindOriginalJobAsync(_port, identity, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return; }
        if (hit?.JobId != submission.JobId || !OriginalRequestMatches(hit, original)) return;
        await _port.CancelOriginalJobAsync(submission.JobId, identity, ct).ConfigureAwait(false);
    }

    public async Task RequestCancelAsync(string jobId, CancellationToken ct)
    {
        try
        {
            await _port.CancelOwnedTaskAsync(jobId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; } // 预算/运行取消交调用方分辨
        catch { /* best-effort：取消请求失败不阻断确认观察 */ }
    }

    /// <summary>
    /// 不确定发送后的对账（§4.4）：按幂等键查注册表快照——唯一命中且身份四元一致且 Epoch 未变才绑定 jobId；
    /// 查不到/多命中/身份不符/纪元已变 → null（调用方 Unknown）。绝不重发。
    /// cancelOnHit=true（发送窗口取消场景）：命中即绑定落盘 + 发远端取消（best-effort）。
    /// </summary>
    private async Task<BoundarySubmitResult?> ReconcileAfterUncertainSendAsync(
        WorkflowRunRecord run, WorkflowSubmission submission, PreparedSubmit.ReconcileIdentity identity, bool cancelOnHit, string? acceptedSendIdentity = null)
    {
        try
        {
            var originalPermit = submission.SendPermit;
            acceptedSendIdentity ??= originalPermit?.OriginalSendIdentity;
            if (acceptedSendIdentity is { Length: > 0 }
                && (originalPermit is not { Version: 1, Consumed: true }
                    || !Guid.TryParseExact(originalPermit.Nonce, "N", out _)
                    || originalPermit.OriginalSendIdentity != acceptedSendIdentity
                    || submission.AcceptedSendIdentity is { Length: > 0 } priorIdentity && priorIdentity != acceptedSendIdentity)) return null;
            using var budget = new CancellationTokenSource(ReconcileBudget);
            // 纪元证据采集（判定在 TryMatchReconcileHit 纯函数）：查询前/后连接纪元 + 快照自报纪元（响应载荷内）。
            var epochBefore = _port.ServerEpoch is { } eb ? $"{eb.ProcessId}:{eb.StartTicksUtc}" : null;
            var snapshot = await _port.QueryJobListAsync(budget.Token).ConfigureAwait(false);
            var epochAfter = _port.ServerEpoch is { } e ? $"{e.ProcessId}:{e.StartTicksUtc}" : null;
            // 只消费冻结快照（不得改读可变对象字段——会诊回溯复核）
            var hit = TryMatchReconcileHit(snapshot, epochBefore, epochAfter, identity.Epoch,
                identity.Key, identity.WireRunId, identity.NodeId, identity.LoopIteration, identity.Occurrence, identity.Attempt);
            if (!OriginalRequestMatches(hit, identity.RequestEvidence)) return null;
            // 写回前复核提交身份未被替换**且未被原地改写**（会诊复核：`ReferenceEquals` 只挡得住换实例，
            // 挡不住同一实例的 Key/Epoch/NodeId/LoopIteration 被改）。任一不符=不臆断落盘，保守返回 null
            // （调用方按 Unknown 处置、零重发）——绝不把「身份 A 的受理结果」写进身份已是 B 的提交。
            //
            // 并发写入边界的证据（会诊要求，勿删）：
            // - **本层能证明的**：单个 `TaskCenterHost` 内 `_runs` 是**同一实例**（构造于 `TaskCenterHost.cs:116`，
            //   `CreateRunner` 传同一 `_runs`），其 `Persist` 在同一把 `_gate` 内完成读盘/修订比较/写入——因此
            //   **同一宿主内、持有旧修订的独立副本**提交时必被拒（`RunRecordConflictException`「并发推进未覆盖」），
            //   本次写回不会错误覆盖；该异常由外层 catch 收敛为 null（Unknown、零重发）。
            // - **本层不能证明、不得据此宣称的**：`_gate` 是实例字段，**不提供跨实例/跨进程互斥**；「生产只有一个宿主」
            //   属**部署前提**，不是本层可核实的实现事实；「仲裁租约已覆盖全部运行记录写入」亦未在本层证明
            //   （例如启动恢复扫描会写运行记录、退出释放在有界等待未收敛时仍会释放）。
            //   故跨进程写入排他只能记为**外部合同前提**（租约设计 R5.1 §6.1/§6.3），本层既不重复实现、也不背书其落实。
            // - 上述字段比较覆盖的是**不经 RunStore 的原地内存改写**这一类，其可靠性依赖「同一 run 对象在驱动调用链内
            //   串行变更」这一**对象所有权前提**；字段守卫本身**不构成共享对象的并发保证**。将来若引入共享对象并发
            //   写入或实现真正的重试，必须让所有写入者共用同一同步机制（仅本方法加锁、或仅 `Update` 内部加锁都不够）。
            if (!ReferenceEquals(run.CurrentSubmission, submission)
                || !string.Equals(submission.Epoch, identity.Epoch, StringComparison.Ordinal)
                || !string.Equals(submission.Key, identity.Key, StringComparison.Ordinal)
                || !string.Equals(run.WireRunId, identity.WireRunId, StringComparison.Ordinal)
                || !string.Equals(submission.NodeId, identity.NodeId, StringComparison.Ordinal)
                || submission.LoopIteration != identity.LoopIteration
                || submission.Attempt != identity.Attempt || submission.OriginalRequestEvidence != identity.RequestEvidence)
                return null;
            // 已有受理事实冲突保护（会诊要求）：jobId 为空＝允许绑定；相同＝幂等确认；**不同＝保留原事实并返回
            // null（Unknown）**——不得用本轮命中覆盖既有的另一个 jobId。
            if (submission.JobId is { } existingJob && !string.Equals(existingJob, hit.JobId, StringComparison.Ordinal))
                return null;
            // [G7-residual·本批处置] **完整发送身份冲突保护**：对账命中只回写 Intent/JobId 的旧路径会让
            // 严格 TakeoverPersist 因缺 `AcceptedSendIdentity` 拒绝结清。此处在「保留原发送责任」的同一边界
            // 按本轮发送身份一并落盘；已存在另一身份（不同轮/不同来源）＝冲突保留，不得覆盖。
            if (acceptedSendIdentity is { Length: > 0 }
                && submission.AcceptedSendIdentity is { Length: > 0 } existingIdentity
                && !string.Equals(existingIdentity, acceptedSendIdentity, StringComparison.Ordinal))
                return null;   // 另一发送身份在册：保留原事实与冲突，零落盘（Unknown，绝不重发）
            // 会诊要求：落盘失败不得把「未持久化的受理状态」留在可继续使用的对象上。
            // 先记住原值；仅在字段仍是我们刚写入的值时恢复（**注意：这是在「同一 run 对象于驱动调用链内串行变更」
            // 这一对象所有权前提下的恢复，字段守卫本身不提供共享对象的并发保证**）；异常交外层 catch 收敛为 Unknown。
            var prevIntent = submission.Intent;
            var prevJobId = submission.JobId;
            var prevAcceptedIdentity = submission.AcceptedSendIdentity;
            submission.Intent = SubmitIntentState.Accepted;
            submission.JobId = hit.JobId;
            if (acceptedSendIdentity is { Length: > 0 })
                submission.AcceptedSendIdentity = acceptedSendIdentity;
            try
            {
                // [P7／§12.2 第 3 项「字段合并」] 对账命中的**受理事实**同样按盘上最新记录做选择性更新：
                // 只写自有字段（Intent/JobId），并发写入者改动的其它字段保留；且**重新核验发送身份**（不一致＝
                // 该命中不属于本笔 ⇒ 不落盘、按未命中保守处置）。
                var applied = _runs.UpdateMergingIf(run.RunId!, latest =>
                {
                    var live = latest.CurrentSubmission;
                    if (live is null) return false;
                    if (!string.Equals(live.Epoch, identity.Epoch, StringComparison.Ordinal)
                        || !string.Equals(live.Key, identity.Key, StringComparison.Ordinal)
                        || !string.Equals(latest.WireRunId, identity.WireRunId, StringComparison.Ordinal)
                        || !string.Equals(live.NodeId, identity.NodeId, StringComparison.Ordinal)
                        || live.Occurrence != identity.Occurrence
                        || live.LoopIteration != identity.LoopIteration
                        || live.Attempt != identity.Attempt
                        || live.OriginalRequestEvidence != identity.RequestEvidence
                        || live.SendPermit != originalPermit
                        || live.JobId is { Length: > 0 } existing && !string.Equals(existing, hit.JobId, StringComparison.Ordinal)
                        || acceptedSendIdentity is { Length: > 0 } and var wantedIdentity && live.AcceptedSendIdentity is { Length: > 0 } liveIdentity && !string.Equals(liveIdentity, wantedIdentity, StringComparison.Ordinal))
                        return false;   // 身份/既有句柄不符 ⇒ **零发布**（不产生未持久化状态的假受理、不推进修订）
                    live.Intent = SubmitIntentState.Accepted;
                    live.JobId = hit.JobId;
                    if (acceptedSendIdentity is { Length: > 0 })
                        live.AcceptedSendIdentity = acceptedSendIdentity;
                    return true;
                }, out var mergedRecord);
                if (!applied || mergedRecord?.CurrentSubmission is not { } mergedSubmission
                    || mergedSubmission.Intent != SubmitIntentState.Accepted
                    || !string.Equals(mergedSubmission.JobId, hit.JobId, StringComparison.Ordinal))
                {
                    // **未落盘＝不得留下内存假受理**（会诊阻断处置）：把调用方对象回滚到写回前的原值。
                    submission.Intent = prevIntent;
                    submission.JobId = prevJobId;
                    submission.AcceptedSendIdentity = prevAcceptedIdentity;
                    return null;   // 交外层按 Unknown 保守收敛
                }
                // Confirm the durable original credential and payload after publication before reporting acceptance.
                var readback = _runs.Load(run.RunId!);
                if (readback?.CurrentSubmission is not { } persisted
                    || persisted.Key != identity.Key || persisted.Epoch != identity.Epoch
                    || persisted.NodeId != identity.NodeId || persisted.Occurrence != identity.Occurrence
                    || persisted.LoopIteration != identity.LoopIteration || persisted.Attempt != identity.Attempt
                    || readback.WireRunId != identity.WireRunId || persisted.OriginalRequestEvidence != identity.RequestEvidence
                    || persisted.SendPermit != originalPermit || persisted.Intent != SubmitIntentState.Accepted
                    || persisted.JobId != hit.JobId
                    || acceptedSendIdentity is { Length: > 0 } && persisted.AcceptedSendIdentity != acceptedSendIdentity)
                {
                    submission.Intent = prevIntent;
                    submission.JobId = prevJobId;
                    submission.AcceptedSendIdentity = prevAcceptedIdentity;
                    return null;
                }
                RunStore.RebaseOnto(run, readback);
            }
            catch (Exception) when (submission.Intent == SubmitIntentState.Accepted
                                    && string.Equals(submission.JobId, hit.JobId, StringComparison.Ordinal))
            {
                submission.Intent = prevIntent;
                submission.JobId = prevJobId;
                submission.AcceptedSendIdentity = prevAcceptedIdentity;
                throw;
            }
            if (cancelOnHit)
                await _port.CancelOriginalJobAsync(hit.JobId!, new BgiJobTerminalPolling.FrozenIdentity(
                    identity.Epoch, identity.Key, identity.WireRunId, identity.NodeId,
                    identity.LoopIteration, identity.Occurrence, identity.Attempt), budget.Token).ConfigureAwait(false);
            return BoundarySubmitResult.AcceptedWith(hit.JobId!);
        }
        catch (OperationCanceledException) when (cancelOnHit) { return null; } // 对账预算耗尽：事实保留，OCE 由外层重抛
        catch { return null; }
    }

    /// <summary>
    /// 失败响应分类（纯函数，R4.10 终审复核 重要6）：仅副作用前协议/合同/准入拒绝（白名单：合同校验/纪元/有效期/
    /// 能力/操作不支持/队列满/占用）可证实未受理 → true（Rejected）；其余失败（result_unknown/task_start_failed/无码/
    /// 不可分类）→ false（Unknown 停驻待对账，不猜未受理——T23 已证明协议体系存在执行后失败不可重放的结果）。
    /// </summary>
    internal static bool IsPreSideEffectRejection(string? errorCode)
        => errorCode is "capability_required" or "invalid_request" or "stale_epoch"
            or "request_expired" or "unsupported_operation" or "queue_full" or "task_busy";

    /// <summary>
    /// **[P8／§24.62] 「可证实未发送」判据（纯函数；唯一证据来源＝`BgiNotSentException`）**：
    /// **true**＝失败发生在本进程**任何字节写入线路之前**（通道未就绪／管道未连接／本地请求被拒）
    /// ⇒ 远端不可能存在本笔受理事实；**false**（含一切其它异常、超时、无响应、回执缺失）＝
    /// **不得**据此判「未发送」（必须保留未决责任）。
    /// **纪律**：只认证据载体，**不得**按异常消息文本/类型泛化推断——那会把「可能已发送」误判为未发送（双跑风险）。
    /// </summary>
    /// **[会诊重要项处置·双保险]** 同时要求**证据码在已登记白名单内**（配合构造期校验形成双层防线：
    /// 即使未来有人绕过构造期校验（反射/后续新增 ctor），未登记码也**不会**被判为「可证实未发送」）。
    internal static bool IsProvenNotSent(Exception ex)
        => ex is BgiNotSentException notSent && BgiNotSentException.IsKnownEvidenceCode(notSent.EvidenceCode);

    /// <summary>
    /// **[P8]** 可证实未发送的**证据码**（**不可证实/未登记码时一律返回 `null`**，用于文案/留痕，不参与判定）。
    /// **[第 3 轮会诊重要项处置]** 与 `IsProvenNotSent` **同判据**：不得对「未登记码的证据载体」（判据为 false）
    /// 返回非空证据码——否则后续调用方可能把非空码误当作证明。
    /// </summary>
    internal static string? NotSentEvidence(Exception ex)
        => IsProvenNotSent(ex) ? ((BgiNotSentException)ex).EvidenceCode : null;

    /// <summary>
    /// 对账命中判定（纯函数，R4.10 集成夹具接缝；终审复核 重要4 三重纪元并入）：
    /// 纪元一致性——查询前/后连接纪元一致（查询窗口内连接未更换的证据；同进程重连纪元不变，本校验不否决
    /// 同进程已观察事实：合同目标=进程纪元级一致性，非连接会话级），快照自报纪元（ext.job.list 响应载荷
    /// bgiEpoch——服务端同帧证据，HandleJobList 恒发）=连接纪元=提交冻结纪元（BGI 重启换纪元则旧事实不可沿用）；
    /// 身份——唯一命中（幂等键+运行+节点+迭代四元一致且有 jobId）。
    /// 任一不符/缺失/零命中/多命中 → null（未证实受理或身份歧义，均 Unknown，绝不重发）。
    /// </summary>
    internal static bool OriginalRequestMatches(BgiJobInfo? job, FrozenOriginalRequestEvidence? original)
        => job is not null && original is { Version: 1, Fingerprint.Length: 64 }
           && original.Fingerprint.All(Uri.IsHexDigit)
           && original.Operation == BgiExternalClient.ExternalOperations.TaskStart
           && job.RequestFingerprintVersion == original.Version
           && string.Equals(job.RequestFingerprint, original.Fingerprint, StringComparison.Ordinal)
           && string.Equals(job.RequestOperation, original.Operation, StringComparison.Ordinal)
           && string.Equals(job.TaskId, original.TaskId, StringComparison.Ordinal)
           && string.Equals(job.ConfigRevision, original.ConfigRevision, StringComparison.Ordinal);

    internal static BgiJobInfo? TryMatchReconcileHit(
        BgiJobListSnapshot? snapshot, string? epochBeforeQuery, string? epochAfterQuery, string? frozenEpoch,
        string? idempotencyKey, string? wireRunId, string? nodeId, int? iteration, int? occurrence = null, int? attempt = null)
    {
        if (snapshot is null || epochBeforeQuery is null || epochBeforeQuery != epochAfterQuery)
            return null;
        var snapshotEpoch = snapshot.Epoch is { } se ? $"{se.ProcessId}:{se.StartTicksUtc}" : null;
        if (snapshotEpoch is null || snapshotEpoch != epochAfterQuery || epochAfterQuery != frozenEpoch)
            return null;
        var hits = snapshot.Jobs.Where(j =>
                j.IdempotencyKey == idempotencyKey
                && j.WorkflowRunId == wireRunId
                && j.NodeId == nodeId
                && j.Iteration == iteration
                && (occurrence is null || j.Occurrence == occurrence)
                && (attempt is null || j.Attempt == attempt)
                && j.JobId is not null)
            .ToList();
        return hits.Count == 1 && (occurrence is null && attempt is null
            || snapshot.Jobs.Count(j => j.IdempotencyKey == idempotencyKey) == 1) ? hits[0] : null;
    }

    /// <summary>资源引用 → ext.task.start 寻址字段（纯静态可测；缺失即拒绝=未发送）。</summary>
    internal static string? TryMapResource(WorkflowNode node, out string? groupName, out string? configName,
        out string? taskId, out string? expectedRevision)
    {
        groupName = configName = taskId = expectedRevision = null;
        var r = node.Ref;
        switch (node.Kind)
        {
            case "resource.configGroup":
                if (string.IsNullOrWhiteSpace(r?.Config)) return $"节点 {node.NodeId} 缺配置组名（未发送）";
                groupName = r!.Config;
                break;
            case "resource.oneDragonConfig":
                if (string.IsNullOrWhiteSpace(r?.Config)) return $"节点 {node.NodeId} 缺整龙配置名（未发送）";
                configName = r!.Config;
                break;
            case "resource.singleTask":
                if (string.IsNullOrWhiteSpace(r?.Config) || string.IsNullOrWhiteSpace(r?.TaskId))
                    return $"节点 {node.NodeId} 单项任务缺所属配置名或 taskId（未发送）";
                configName = r!.Config;
                taskId = r!.TaskId;
                break;
            default:
                return $"节点 {node.NodeId} 类型 {node.Kind} 非资源节点（未发送）";
        }
        expectedRevision = r!.Revision;
        if (string.IsNullOrWhiteSpace(expectedRevision))
            return $"节点 {node.NodeId} 缺配置修订号（严格合同要求 expectedConfigRevision，未发送）";
        return null;
    }

    /// <summary>账号合同（§4.5）：至多一个 account；存在即要求完整 uid；expectedUid=该 account 完整原值（掩码不进协议）。</summary>
    internal static string? TryExtractExpectedUid(WorkflowNode node, out string? expectedUid)
    {
        expectedUid = null;
        var accounts = node.Strategies.Where(s => s.Kind == "prerequisite.account").ToList();
        if (accounts.Count > 1) return $"节点 {node.NodeId} 含多个 prerequisite.account（账号歧义，未发送）";
        if (accounts.Count == 0) return null;
        var uid = accounts[0].GetString("uid");
        if (string.IsNullOrWhiteSpace(uid)) return $"节点 {node.NodeId} 账号策略 uid 空白（未发送）";
        expectedUid = uid;
        return null;
    }

    /// <summary>
    /// 节点作业终态解释（一轮 I3：与前置/收尾 InterpretJob 分开——skipped 是合法终态 D15，WasCancelled 优先；
    /// 活动态/未知词返回 null=不猜，调用方 Uncertain）。
    /// </summary>
    internal static (string? Word, string? Reason) InterpretNodeJob(BgiJobInfo job)
    {
        if (job.WasCancelled) return ("cancelled", "远端取消已确认（WasCancelled）");
        return job.State switch
        {
            "succeeded" => ("succeeded", null),
            "failed" => ("failed", job.ErrorCode ?? job.ErrorMessage ?? "failed"),
            "cancelled" => ("cancelled", job.ErrorMessage ?? "远端已取消"),
            "skipped" => ("skipped", job.ErrorMessage ?? "远端正常跳过"),
            "rejected" => ("rejected", job.ErrorCode ?? "rejected"),
            _ => (null, null),
        };
    }
}
