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
    /// **封闭构造**：只能经 <see cref="Ok"/> / <see cref="No"/> 产生，杜绝「拒绝对象被送去发送」
    /// 或「字段残缺的成功对象」这两类误用（会诊阻断项 1）。
    /// </summary>
    internal sealed class PreparedSubmit
    {
        private int _consumed;

        private PreparedSubmit(WorkflowRunRecord? run, WorkflowSubmission? submission, object? payload, BoundarySubmitResult? rejection)
        {
            Run = run;
            Submission = submission;
            Payload = payload;
            Rejection = rejection;
        }

        public WorkflowRunRecord? Run { get; }
        public WorkflowSubmission? Submission { get; }
        public object? Payload { get; }
        public BoundarySubmitResult? Rejection { get; }

        /// <summary>构造可证实未受理的拒绝结果（第 1 段未通过时唯一出口）。</summary>
        public static PreparedSubmit No(BoundarySubmitResult rejection) => new(null, null, null, rejection);

        /// <summary>构造可发送的冻结载荷（仅第 1 段全部校验通过时可达）。</summary>
        public static PreparedSubmit Ok(WorkflowRunRecord run, WorkflowSubmission submission, object payload)
            => new(run, submission, payload, null);

        /// <summary>
        /// 一次性消费护栏（仅防**进程内重复调用**；持久化的发送授权责任仍在门面，绝不由本护栏替代）。
        /// </summary>
        public bool TryConsume() => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
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
    public bool SingleNativeSupported => _port.IsReady
                                         && _port.HasCapability("task.single.native");

    /// <summary>收尾抑制能力实况（B6/E4'；缺能力时 Planner 预检响亮拒绝整龙/配置组流程）。</summary>
    public bool SuppressConfigCompletionSupported => _port.IsReady
        && _port.HasCapability(BgiExternalClient.CapabilitySuppressConfigCompletionAction);

    /// <summary>
    /// 统一提交入口（与非接线路径逐字等价）：本地校验+身份冻结 → 锁外发送 → 三态对账。
    /// R5.2 B2-γ：接线态由 <see cref="ArbitrationWorkflowExecutionBoundary"/> 在这两段之间插入仲裁面。
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
    internal PreparedSubmit PrepareSubmit(WorkflowSubmitRequest request, string? authorizedEpoch = null)
    {
        var run = request.Run;
        var occurrence = request.Occurrence;
        var node = request.Node;

        // 1) 意图身份校验（引擎纪律：RecordIntent 先行；不符 = 本地违例，可证实未发送 → Rejected）
        var submission = run.CurrentSubmission;
        if (submission is null
            || submission.NodeId != occurrence.NodeId
            || submission.Occurrence != occurrence.Occurrence
            || submission.LoopIteration != occurrence.LoopIteration
            || submission.Intent != SubmitIntentState.IntentRecorded)
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
        // 会诊阻断项 2：接线态下本段在门面「锁内占位」之后、锁外发送之前执行。若期间连接切到新纪元，
        // 冻结新纪元会让「服务端执行对象」与「门面授权对象」不一致。故携带本轮授权的固定目标 epoch 比对：
        // 不一致一律可证实未发送地拒绝（绝不把新 epoch 写进旧授权对应的发送身份）。
        var epochText = $"{epoch.ProcessId}:{epoch.StartTicksUtc}";
        if (authorizedEpoch is not null && !string.Equals(epochText, authorizedEpoch, StringComparison.Ordinal))
            return PreparedSubmit.No(BoundarySubmitResult.Rejected("授权目标纪元与本机当前纪元不一致（stale_epoch；未发送）"));

        // 3) 冻结：纪元/有效期/指纹 → Intent=Submitted + SendAttempted 落盘（即将发送事实；此后缺 jobId ≠ 未发送）
        submission.Epoch = $"{epoch.ProcessId}:{epoch.StartTicksUtc}";
        submission.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ExpireWindow).ToString("O");
        var payload = new
        {
            executionContractVersion = 1,
            idempotencyKey = submission.Key,
            expiresAtUtc = submission.ExpiresAtUtc,
            bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
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
        submission.Fingerprint = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(payload)))[..24].ToLowerInvariant();
        submission.SendAttempted = true;
        submission.Intent = SubmitIntentState.Submitted;
        _runs.Update(run);

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

        // 4) 发送一次（绝不重发；不确定 → 对账）
        BgiExternalResponse response;
        try
        {
            response = await _port.SendCommandAsync(
                BgiExternalClient.ExternalOperations.TaskStart, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 发送窗口取消（Stop）：cleanup 令牌对账——命中绑定 jobId 落盘并即发远端取消（引擎 Stop 路径随后再确认），
            // 未命中保留 SendAttempted 事实；随后重抛 OCE（取消纪律不吞）
            await ReconcileAfterUncertainSendAsync(run, submission, cancelOnHit: true).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var reconciled = await ReconcileAfterUncertainSendAsync(run, submission, cancelOnHit: false).ConfigureAwait(false);
            return reconciled ?? BoundarySubmitResult.UnknownWith(
                $"发送结果不可考（{ex.GetType().Name}），按幂等键对账未命中（不重发，待人工/恢复对账）");
        }

        if (!response.Success)
        {
            // R4.10 终审复核（重要6）：失败分类纯函数——白名单外一律 Unknown 停驻待对账
            return IsPreSideEffectRejection(response.ErrorCode)
                ? BoundarySubmitResult.Rejected($"受理被副作用前协议拒绝：{response.ErrorCode}")
                : BoundarySubmitResult.UnknownWith($"受理结果不可考（{response.ErrorCode ?? "无错误码"}），不猜未受理，待对账");
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
        WorkflowRunRecord run, WorkflowSubmission submission, bool cancelOnHit)
    {
        try
        {
            using var budget = new CancellationTokenSource(ReconcileBudget);
            // 纪元证据采集（判定在 TryMatchReconcileHit 纯函数）：查询前/后连接纪元 + 快照自报纪元（响应载荷内）。
            var epochBefore = _port.ServerEpoch is { } eb ? $"{eb.ProcessId}:{eb.StartTicksUtc}" : null;
            var snapshot = await _port.QueryJobListAsync(budget.Token).ConfigureAwait(false);
            var epochAfter = _port.ServerEpoch is { } e ? $"{e.ProcessId}:{e.StartTicksUtc}" : null;
            var hit = TryMatchReconcileHit(snapshot, epochBefore, epochAfter, submission.Epoch,
                submission.Key, run.WireRunId, submission.NodeId, submission.LoopIteration);
            if (hit is null) return null; // 通道瞬态/纪元不一致（查询窗口/快照自报/冻结）/零命中/多命中：Unknown
            submission.Intent = SubmitIntentState.Accepted;
            submission.JobId = hit.JobId;
            _runs.Update(run); // 对账命中即受理事实落盘
            if (cancelOnHit)
                await RequestCancelAsync(hit.JobId!, budget.Token).ConfigureAwait(false);
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
    /// 对账命中判定（纯函数，R4.10 集成夹具接缝；终审复核 重要4 三重纪元并入）：
    /// 纪元一致性——查询前/后连接纪元一致（查询窗口内连接未更换的证据；同进程重连纪元不变，本校验不否决
    /// 同进程已观察事实：合同目标=进程纪元级一致性，非连接会话级），快照自报纪元（ext.job.list 响应载荷
    /// bgiEpoch——服务端同帧证据，HandleJobList 恒发）=连接纪元=提交冻结纪元（BGI 重启换纪元则旧事实不可沿用）；
    /// 身份——唯一命中（幂等键+运行+节点+迭代四元一致且有 jobId）。
    /// 任一不符/缺失/零命中/多命中 → null（未证实受理或身份歧义，均 Unknown，绝不重发）。
    /// </summary>
    internal static BgiJobInfo? TryMatchReconcileHit(
        BgiJobListSnapshot? snapshot, string? epochBeforeQuery, string? epochAfterQuery, string? frozenEpoch,
        string? idempotencyKey, string? wireRunId, string? nodeId, int? iteration)
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
                && j.JobId is not null)
            .ToList();
        return hits.Count == 1 ? hits[0] : null;
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
