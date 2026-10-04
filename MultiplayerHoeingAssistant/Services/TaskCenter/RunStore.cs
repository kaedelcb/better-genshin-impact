using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>运行记录修订冲突（记录级乐观并发；防止并发推进互相覆盖水位）。</summary>
public sealed class RunRecordConflictException : Exception
{
    public RunRecordConflictException(string message) : base(message) { }
}

internal sealed record RunStoreListSnapshot(
    IReadOnlyList<WorkflowRunRecord> Records,
    IReadOnlyList<string> UnknownFiles);

/// <summary>
/// 槲寄生 · 任务中心——RunStore（运行水位持久化，R4.2）。
/// 路径：默认 %APPDATA%/NexusBGI/runs/（按 Windows 用户隔离，不走 SignalR 同步）；
/// 构造函数可注入独立配置根（D2）。
///
/// 纪律（R4 分解 D11 + B3 会诊）：
/// - 提交意图先行：RecordIntent 必须在向 BGI 提交前落盘（IntentRecorded + 固定幂等键）；
/// - 一运行一文件，原子写（临时文件 + 同目录替换）+ 写前备份；
/// - 记录级单调修订防并发覆盖；
/// - 坏记录文件隔离：原件保留、列入 UnknownFiles、不参与恢复决策；
/// - RecoverOnStart 只标 Interrupted/Unknown，绝不自动补跑、绝不换幂等键重跑；
///   恢复后是否继续由 Reconciler 显式决策（§7.2：失联/重启不自动补发未知任务）。
/// </summary>
public sealed partial class RunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _runsDir;
    private readonly string _backupDir;

    /// <summary>写入串行化闸门（ASTRA 二轮重要项①：乐观并发只防覆盖不防交错，读-检-写全程互斥）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PathGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private readonly bool _requireOwnership;
    private LeaseOwnerCapability? _owner;
    private const string OwnerPolicy = "managed-runstore-owner-fence-v1";
    private string OwnerPolicyPath => Path.Combine(_runsDir, ".owner-fence");

    internal void VerifyBoundOwner()
    {
        lock (_gate)
        {
            if (_owner is null) throw new RunRecordConflictException("运行写者未取得原所有者能力。");
            using var fence = _owner.Store.AcquireOwnerFence(_owner);
        }
    }

    internal void BindOwner(LeaseOwnerCapability owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            if (_owner is not null && !_owner.Matches(owner.LeaseId, owner.OwnerEpoch))
                throw new RunRecordConflictException("同一运行存储实例不可升级旧责任到新所有者。");
            using var fence = owner.Store.AcquireOwnerFence(owner);
            using var publication = AcquirePublicationLock();
            var policy = TryReadAllTextOrNull(OwnerPolicyPath, "read-owner-policy");
            if (policy is not null && policy != OwnerPolicy)
                throw new RunRecordConflictException("运行所有者策略不可验证，原件保持。");
            if (policy is null)
            {
                var temporary = OwnerPolicyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, OwnerPolicy, Utf8NoBom);
                    owner.Store.VerifyOwnerFence(owner);
                    File.Move(temporary, OwnerPolicyPath, overwrite: true);
                }
                finally { try { File.Delete(temporary); } catch (IOException) { } }
            }
            _owner = owner;
        }
    }

    /// <summary>
    /// **仅测试接缝**（生产恒 `null`）：按记录判定是否在**原子发布步骤**注入故障（返回 `null`＝不注入）。
    /// 依据：[§17 P49／§16 交错③] 需要可确定地制造「准备阶段 `RunStore` 更新（发布）失败」，
    /// 该窗口内**内存记录已被合并回调修改**、磁盘发布结果不明——正是 §12.3 M3③ 所指的
    /// 「禁止继续用该对象发送、禁止无条件回滚、禁止立即再次 PrepareSubmit」的判定场景。
    /// </summary>
    internal Func<WorkflowRunRecord, Exception?>? PublishFaultForTest { get; set; }

    /// <summary>测试专用的逐次 Load 前探针，用于固定 Host 两次读取之间的 run 文件身份/损坏交错；生产恒 null。</summary>
    internal Action<string>? BeforeLoadForTest { get; set; }

    public RunStore(string runsDir, bool requireOwnership = false)
    {
        // R4.8 二轮（重要2）：构造零副作用——目录推迟到首次 Persist 才创建
        _runsDir = Path.GetFullPath(runsDir);
        _requireOwnership = requireOwnership;
        _gate = PathGates.GetOrAdd(Path.TrimEndingDirectorySeparator(_runsDir), _ => new object());
        _backupDir = Path.Combine(_runsDir, "_backup");
    }

    /// <summary>默认运行目录（%APPDATA%/NexusBGI/runs）。</summary>
    public static string DefaultRunsDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI", "runs");

    private string PathFor(string runId) => Path.Combine(_runsDir, runId + ".run.json");

    /// <summary>无法解析的记录文件（隔离展示用；原件保留，绝不自动删改）。</summary>
    public IReadOnlyList<string> UnknownFiles
    {
        get => ListWithIntegrity("read-unknown").UnknownFiles;
    }

    /// <summary>创建运行记录（初始 Planned + 固定幂等键；RecordRevision 从 1 起）。
    /// handoff（R4.9）：移交身份随创建原子落盘——受理提交点即本持久化，崩溃窗无「已受理无身份」记录。</summary>
    public WorkflowRunRecord CreateRun(string workflowId, string workflowRevision, string? note = null,
        HandoffIdentity? handoff = null, string? admissionSourceScope = null, WorkflowStopAuthorityRecord? stopAuthority = null)
    {
        var now = DateTimeOffset.Now;
        var rec = new WorkflowRunRecord
        {
            RunId = NewRunId(),
            WireRunId = Guid.NewGuid().ToString("N"), // B1：线协议 workflowRunId 强制 Guid（BGI ReadIdentity 严格解析）
            WorkflowId = workflowId,
            WorkflowRevision = workflowRevision,
            State = WorkflowRunState.Planned,
            StopAuthority = stopAuthority,
            IdempotencyKey = NewIdempotencyKey(),
            CreatedAt = now,
            UpdatedAt = now,
            Note = note,
            Handoffs = handoff is null ? [] : [handoff],
            // G4a：**启动移交**的来源固定 Scope 随受理同一次落盘（只比较、不重写；空=无固定来源）。
            // 形状校验：仅接受规范 `bgi:local:{非空完整 epoch}`（畸形值=编程/接线错误 ⇒ 响亮抛出，不落权威字段）。
            AdmissionSourceScope = NormalizeAdmissionSourceScope(admissionSourceScope),
        };
        if (handoff is { Mode: StartupHandoffModes.Start or StartupHandoffModes.ArmTrigger }
            && rec.AdmissionSourceScope is not null)
            rec.AdmissionParentSource = AdmissionParentSource.Handoff(rec, handoff);
        Persist(rec, expectedRecordRevision: 0, authorizedParent: rec.AdmissionParentSource);
        return rec;
    }

    /// <summary>F11拒绝诊断一次发布为Cancelled；不给执行/恢复/普通写者任何资格。</summary>
    internal WorkflowRunRecord CreateNonExecutingDiagnostic(string workflowId, string workflowRevision, string note)
    {
        var now = DateTimeOffset.Now;
        var rec = new WorkflowRunRecord
        {
            RunId = NewRunId(), WireRunId = Guid.NewGuid().ToString("N"),
            WorkflowId = workflowId, WorkflowRevision = workflowRevision,
            State = WorkflowRunState.Cancelled, NonExecutingDiagnostic = true,
            IdempotencyKey = NewIdempotencyKey(), CreatedAt = now, UpdatedAt = now, Note = note,
        };
        Persist(rec, 0, authorizedDiagnostic: true);
        return rec;
    }

    /// <summary>
    /// 准入来源 Scope 规范化（G4a）：空 ⇒ null（无固定来源）；非空必须是 `bgi:local:{非空完整 epoch}`，
    /// 否则**响亮抛出**（不得把畸形值写入权威字段；调用方为宿主内部接线点，属编程错误）。
    /// </summary>
    private static string? NormalizeAdmissionSourceScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        if (!scope.StartsWith("bgi:local:", StringComparison.Ordinal)
            || string.IsNullOrEmpty(scope["bgi:local:".Length..].Trim()))
            throw new InvalidOperationException("准入来源 Scope 形状非法（应为 bgi:local:{非空完整 epoch}）：" + scope);
        return scope;
    }

    /// <summary>
    /// 记录提交意图（必须在向 BGI 提交前调用；D11/B3：崩溃后按意图 + 账本/job 查询对账，不重跑）。
    /// B2：一提交一身份——submission 键须经 DeriveSubmissionKey 按出现身份确定性派生；
    /// 前一提交终态未确认（InFlight）时拒绝重叠提交（防 jobId/终态跨节点残留误判）。
    /// </summary>
    public void RecordIntent(WorkflowRunRecord rec, WorkflowSubmission submission)
        => RecordIntentCore(rec, submission, null);

    internal void RecordIntentForBoundary(WorkflowRunRecord rec, WorkflowSubmission submission, bool nodeAdmissionRequired)
        => RecordIntentCore(rec, submission, nodeAdmissionRequired);

    private void RecordIntentCore(WorkflowRunRecord rec, WorkflowSubmission submission, bool? nodeAdmissionRequired)
    {
        if (submission.NodeAdmissionRequired is not null)
            throw new RunRecordConflictException("发送路由只能由意图边界同次固定，不接受调用者补造。");
        if (nodeAdmissionRequired is { } requestedRoute && rec.CurrentSubmission is { } original
            && original.Key == submission.Key && original.NodeAdmissionRequired is { } originalRoute
            && originalRoute != requestedRoute)
            throw new RunRecordConflictException("同一提交身份的原发送路由不能随恢复边界改变。");
        submission.NodeAdmissionRequired = rec.CurrentSubmission?.Key == submission.Key
            ? rec.CurrentSubmission.NodeAdmissionRequired : nodeAdmissionRequired;
        if (string.IsNullOrWhiteSpace(submission.Key))
            throw new InvalidOperationException("提交意图要求确定性派生幂等键已存在。");
        if (string.IsNullOrWhiteSpace(submission.NodeId))
            throw new InvalidOperationException("提交意图要求绑定节点出现身份。");
        if (rec.CurrentSubmission is { } prev && (prev.InFlight
            || (prev.SendAttempted || !string.IsNullOrEmpty(prev.JobId) || !string.IsNullOrEmpty(prev.AcceptedSendIdentity))
               && (!prev.ExecutionExitConfirmed || !BgiJobTerminalPolling.IsTerminal(prev.ObservedTerminal))))
            throw new InvalidOperationException(
                $"前一提交 {prev.Key}（节点 {prev.NodeId}）终态未确认，拒绝重叠提交（一提交一身份）。");
        submission.WireRunId = rec.WireRunId;
        submission.Intent = SubmitIntentState.IntentRecorded;
        submission.RecordedAt = DateTimeOffset.Now;
        if (rec.CurrentSubmission is { } previous && previous.Key != submission.Key)
            rec.SubmissionHistory.Add(JsonSerializer.Deserialize<WorkflowSubmission>(JsonSerializer.Serialize(previous))!);
        rec.CurrentSubmission = submission;
        Persist(rec, rec.RecordRevision, authorizedRouting: nodeAdmissionRequired);
    }

    /// <summary>
    /// 确定性派生单次提交幂等键（B2）：同一 runId+节点出现+attempt 重算同键（重复投递复用），
    /// 不同节点/轮次/尝试绝不复用；崩溃恢复不产生第二次执行。
    /// </summary>
    public static string DeriveSubmissionKey(string runId, string nodeId, int occurrence, int loopIteration, int attempt)
    {
        var material = $"{runId}|{nodeId}|{occurrence}|{loopIteration}|{attempt}";
        return "idem-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..24].ToLowerInvariant();
    }

    /// <summary>
    /// 账号标识派生（I3 + 四轮阻断 3）：SHA256 截断哈希——稳定可比对、碰撞隔离、非可逆；
    /// 空 UID 返回 null（严格合同另行拒绝，不参与键材料）。
    /// </summary>
    public static string? DeriveAccountKey(string? uid)
        => string.IsNullOrWhiteSpace(uid) ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid)))[..16].ToLowerInvariant();

    /// <summary>
    /// 权威台账查询（R4.9 §3 + ASTRA 二轮 I5 三态：命中/确定未命中/查询不完整）。
    /// 与展示型 List 容错不同：存在无法解析的记录文件时返回 Incomplete（坏文件可能藏着受理事实），调用方必须拒绝新受理。
    /// 命中时返回运行+具体绑定（内容核对按该绑定的 Mode）；同一运行的多条绑定按创建次序取最新（追加式，后受理者优先）。
    /// </summary>
    public HandoffLedgerQuery QueryHandoffLedger(string intentKey)
    {
        if (string.IsNullOrWhiteSpace(intentKey)) return HandoffLedgerQuery.MissInstance; // 空键由移交入口先行拒绝
        // [第三轮会诊重要项处置] 目录枚举争用**不得逸出**：枚举预算耗尽 ⇒ 对账**不可确认**（`Incomplete`，
        // 不得当未命中）；目录未建仍由枚举助手返回空集。
        IReadOnlyList<string> handoffFiles;
        try
        {
            handoffFiles = EnumerateRunFilesOrEmpty();
        }
        catch (Exception ex) when (IsFileContention(ex))
        {
            return HandoffLedgerQuery.IncompleteInstance;
        }
        WorkflowRunRecord? bestRun = null;
        HandoffIdentity? bestBinding = null;
        var incomplete = false;
        foreach (var file in handoffFiles)
        {
            WorkflowRunRecord? rec;
            try
            {
                var text = TryReadAllTextOrNull(file, "read-handoff");
                if (text is null) continue;               // 读取前已被移除＝已确认不存在（非未命中）
                rec = JsonSerializer.Deserialize<WorkflowRunRecord>(text, JsonOptions);
            }
            catch (Exception ex) when (IsFileContention(ex))
            {
                // [第一／二轮会诊重要项处置] 争用家族预算耗尽 ⇒ **不可确认**（保守：不得当未命中，也不得当坏记录）
                incomplete = true;   // 由既有 `Incomplete` 语义承接
                continue;
            }
            catch (JsonException)
            {
                incomplete = true; // 坏文件可能正是该键的受理记录——不得当未命中
                continue;
            }
            if (rec is null || string.IsNullOrWhiteSpace(rec.RunId)) { incomplete = true; continue; }
            foreach (var binding in rec.Handoffs)
            {
                if (!string.Equals(binding.IntentKey, intentKey, StringComparison.Ordinal)) continue;
                if (bestRun is null || rec.CreatedAt > bestRun.CreatedAt
                    || (rec.CreatedAt == bestRun.CreatedAt && rec.UpdatedAt >= bestRun.UpdatedAt))
                {
                    bestRun = rec;
                    bestBinding = binding;
                }
            }
        }
        if (bestRun is not null && bestBinding is not null)
            return HandoffLedgerQuery.Hit(bestRun, bestBinding);
        return incomplete ? HandoffLedgerQuery.IncompleteInstance : HandoffLedgerQuery.MissInstance;
    }

    /// <summary>
    /// 是否存在未决外部事实（ASTRA 二轮 I4：恢复扫描与驱动异常收敛统一判定）——主体提交在飞 / 收尾在意或执行中。
    /// 注意（R4.9 二轮处置回退）：前置动作在飞【不计入】——R4.6 已验收合同是「前置在飞 → Interrupted，
    /// 恢复时经 ReconcileAsync 对账」，标 Unknown 会绕过该合同（RecoverOnStart_PrerequisiteInFlight 回归证明）。
    /// </summary>
    public static bool HasUnresolvedExternalFact(WorkflowRunRecord rec)
        => rec.State == WorkflowRunState.Completing
           || rec.PendingCompletion is { } pending && !TerminalReleaseEvidence.CompletionSettled(rec, pending)
           || rec.CurrentSubmission is { } sub && !LocalNoSendEvidence.IsDischarged(rec, sub)
              && (sub.InFlight || sub.Intent == SubmitIntentState.Accepted || sub.SendAttempted
                  || !string.IsNullOrEmpty(sub.JobId) || !string.IsNullOrEmpty(sub.AcceptedSendIdentity))
              && (!sub.ExecutionExitConfirmed || !BgiJobTerminalPolling.IsTerminal(sub.ObservedTerminal));

    /// <summary>停止/终局资格覆盖主体、前置和收尾；保留非停止前置恢复的既有Interrupted合同。</summary>
    public static bool HasUnresolvedTerminalResponsibility(WorkflowRunRecord rec)
        => rec.SubmissionHistory.Any(s => !TerminalReleaseEvidence.BodySettled(rec, s))
           || rec.CompletionHistory.Any(c => !TerminalReleaseEvidence.CompletionSettled(rec, c))
           || HasUnresolvedExternalFact(rec)
           || rec.CurrentSubmission is { } sub && !LocalNoSendEvidence.IsDischarged(rec, sub)
              && (sub.SendAttempted || !string.IsNullOrEmpty(sub.JobId) || !string.IsNullOrEmpty(sub.AcceptedSendIdentity))
              && (!sub.ExecutionExitConfirmed || sub.ObservedTerminal is not ("succeeded" or "failed" or "cancelled" or "rejected" or "skipped"))
           || rec.PrerequisiteActions.Any(a => a.State == PrerequisiteActionState.Unknown
               || (a.SendAttempted || !string.IsNullOrEmpty(a.JobId))
                  && (!a.ExecutionExitConfirmed || !BgiJobTerminalPolling.IsTerminal(a.ObservedTerminal)));

    internal bool TryPublishPreparedNoSend(BgiWorkflowExecutionBoundary.PreparedSubmit prepared, out WorkflowRunRecord? latest, string? transportEvidence = null)
    {
        lock (_gate)
        {
            latest = null;
            var proof = prepared.CreateNoSendProof(transportEvidence);
            if (proof is null) return false;
            var current = Load(proof.RunId);
            if (current?.CurrentSubmission is not { } sub || sub.SendPermit is not { Version: 1, Consumed: true } permit
                || permit.Nonce != proof.ConsumptionId || !LocalNoSendEvidence.Matches(current, sub, proof)
                || sub.LocalNoSendProof is not null) return false;
            sub.LocalNoSendProof = proof;
            sub.Intent = SubmitIntentState.Rejected;
            Persist(current, current.RecordRevision, proof);
            var readback = Load(proof.RunId);
            if (readback?.RecordRevision != current.RecordRevision || readback.CurrentSubmission?.LocalNoSendProof != proof)
                return false;
            latest = readback;
            return true;
        }
    }

    /// <summary>推进记录（提交受理/终态/水位/等待/收尾状态更新；记录修订单调递增）。</summary>
    public void Update(WorkflowRunRecord rec) => Persist(rec, rec.RecordRevision);

    /// <summary>
    /// **[P7／§12.2 第 3 项「字段合并」] 按身份字段的选择性更新**：在存储闸门内**重新加载盘上最新记录**，把调用方
    /// 经 <paramref name="applyOwnedFields"/> 声明的**自有字段**应用到**最新记录**上，再以最新修订原子发布。
    /// 与 <see cref="Update"/> 的区别：并发写入者改动的**非自有字段**由此**保留**，不再因修订漂移整笔失败
    /// （也不再允许调用方携带的旧对象整对象覆盖他人改动）。
    /// 返回 `false`＝记录不存在（无副作用）；盘上记录损坏时抛 <see cref="RunRecordConflictException"/>（原件保留、拒绝覆盖）。
    /// **自有字段范围**由调用方声明；本方法不做字段语义校验（身份校验由调用方在其回调内完成）。
    /// </summary>
    /// <param name="applyOwnedFields">
    /// 回调返回 **`false`＝前置条件不成立 ⇒ 不发布、不推进修订**（调用方须据此保守处置并自行回滚内存视图）。
    /// 返回 `true`＝已按**自有字段**更新并原子发布。
    /// </param>
    public bool UpdateMergingIf(string runId, Func<WorkflowRunRecord, bool> applyOwnedFields, out WorkflowRunRecord? latest)
        => UpdateMergingCore(runId, applyOwnedFields, out latest);

    internal bool BindOriginalAdmissionMapping(string runId, OperationRecord op)
    {
        if (op.RunBinding != runId || op.Candidate is null || string.IsNullOrWhiteSpace(op.RequestIdentity)
            || op.LastSendSeq < 1 || op.SubmissionIdentity != $"sub:{op.RequestIdentity}:{op.LastSendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            || op.OperationType is not (OperationType.FlowRegistration or OperationType.Recovery or OperationType.Handoff))
            return false;
        var mapping = new RunAdmissionMapping(1, op.RequestIdentity, op.SubmissionIdentity, op.LastSendSeq, op.OperationType);
        UpdateMergingCore(runId, run =>
        {
            if (run.WorkflowId != op.Candidate.WorkflowId) return false;
            if (run.AdmissionMappings?.Contains(mapping) == true) return false;
            (run.AdmissionMappings ??= []).Add(mapping);
            return true;
        }, out var latest, authorizedMapping: mapping);
        return latest?.AdmissionMappings?.Contains(mapping) == true;
    }

    private bool UpdateMergingCore(string runId, Func<WorkflowRunRecord, bool> applyOwnedFields,
        out WorkflowRunRecord? latest, PreparedSendPermit? authorizedPermit = null, RunAdmissionMapping? authorizedMapping = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(applyOwnedFields);
        lock (_gate)
        {
            var file = PathFor(runId);
            var currentText = TryReadAllTextOrNull(file, "read-merging");   // 「不存在」与「拒绝访问」在此分流
            if (currentText is null)
            {
                latest = null;                        // 已确认不存在（拒绝访问会在此抛出，不落此分支）
                return false;
            }

            WorkflowRunRecord? current;
            try
            {
                current = JsonSerializer.Deserialize<WorkflowRunRecord>(currentText, JsonOptions);
            }
            catch (JsonException)
            {
                throw new RunRecordConflictException($"运行 {runId} 盘上记录已损坏，拒绝覆盖写入（原件保留）。");
            }

            // 坏记录一律响亮冲突（不得当作「不存在」或落到别的目标路径）：JSON null／缺 RunId／RunId 与请求不一致。
            if (current is null || string.IsNullOrWhiteSpace(current.RunId)
                || !string.Equals(current.RunId, runId, StringComparison.Ordinal))
                throw new RunRecordConflictException($"运行 {runId} 盘上记录不可确认（RunId 缺失或与请求不一致），拒绝覆盖写入（原件保留）。");

            if (!applyOwnedFields(current))     // 前置条件不成立：**零发布、零修订推进**
            {
                latest = current;
                return false;
            }

            Persist(current, current.RecordRevision, authorizedPermit: authorizedPermit, authorizedMapping: authorizedMapping);   // 以最新修订发布
            latest = current;
            return true;
        }
    }

    /// <summary>
    /// **[P7／§12.2 第 3 项] 旧对象 rebase**：把**盘上最新记录**的字段整体同步到调用方持有的旧对象上
    /// （含嵌套对象），使调用方后续再用 <see cref="Update"/> 写回时**不会把并发写入者的改动整对象覆盖**。
    /// 语义说明：这是「以最新盘上状态为准」的**全量同步**（嵌套引用会被替换为新实例）；仅用于合并写成功之后。
    /// </summary>
    public static void RebaseOnto(WorkflowRunRecord target, WorkflowRunRecord latest)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(latest);
        // 逐字段复制（含嵌套引用，均指向「盘上最新」实例）；本工程所用 STJ 版本无 `JsonSerializer.Populate`，
        // 故用反射完成同类型浅复制——只用于合并写成功后的**全量 rebase**，不做深拷贝（文档已声明该语义）。
        foreach (var property in typeof(WorkflowRunRecord)
                     .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite) property.SetValue(target, property.GetValue(latest));
        }
    }

    /// <summary>读取运行记录（不存在返回 null；解析失败抛 JsonException——调用方按隔离处理，不回空）。</summary>
    internal bool IsStartupSourceRevoked(WorkflowStopAuthorityRecord authority)
    {
        lock (_gate)
        {
            var snapshot = ListWithIntegrity("read-startup-source");
            if (snapshot.UnknownFiles.Count != 0)
                throw new InvalidDataException("来源停止记录不可完全核对，禁止猜测原挂载仍有效。");
            return snapshot.Records.Any(r => r.StopRequested && r.StopAuthority == authority);
        }
    }

    public WorkflowRunRecord? Load(string runId)
    {
        BeforeLoadForTest?.Invoke(runId);
        var file = PathFor(runId);
        // [第三轮会诊阻断项处置] **不得用 `File.Exists` 探测**（它会把「拒绝访问」静默折成 false ⇒ 误判为不存在）：
        // 直接读——NotFound ⇒ null（合法「无记录」）；争用 ⇒ 有界重试后原样抛出。
        var text = TryReadAllTextOrNull(file, "read-load");
        return text is null ? null : JsonSerializer.Deserialize<WorkflowRunRecord>(text, JsonOptions);
    }

    /// <summary>
    /// 同一次目录枚举中读取可解析运行和损坏文件清单（记录按创建时间排序）。
    /// 锁覆盖本 RunStore 实例的写入与读取；不承诺跨不同 RunStore 实例/进程的全局快照。
    /// </summary>
    internal RunStoreListSnapshot ListWithIntegrity(string readOperation = "read-list")
    {
        lock (_gate)
        {
            var list = new List<WorkflowRunRecord>();
            var bad = new List<string>();
            foreach (var file in EnumerateRunFilesOrEmpty())   // 目录未建=空集；拒绝访问⇒响亮上抛（见枚举助手）
            {
                try
                {
                    var text = TryReadAllTextOrNull(file, readOperation);
                    if (text is null) continue;               // 读取前已被移除＝已确认不存在
                    var rec = JsonSerializer.Deserialize<WorkflowRunRecord>(text, JsonOptions);
                    if (rec is null || string.IsNullOrWhiteSpace(rec.RunId))
                    {
                        bad.Add(file);
                        continue;
                    }
                    list.Add(rec);
                }
                catch (Exception ex) when (IsFileContention(ex))
                {
                    // [第一／二轮会诊重要项处置] **争用家族预算耗尽后必须响亮**：不得当作「内容损坏」静默隐藏。
                    throw;
                }
                catch (JsonException)
                {
                    // 隔离：坏文件不参与列表，但与本次记录集合一并返回完整性信息；原件保留。
                    bad.Add(file);
                }
            }

            list.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
            bad.Sort(StringComparer.Ordinal);
            return new RunStoreListSnapshot(list.AsReadOnly(), bad.AsReadOnly());
        }
    }

    /// <summary>列出全部可解析记录（按创建时间排序）；需同时核验完整性时使用 <see cref="ListWithIntegrity"/>。</summary>
    public IReadOnlyList<WorkflowRunRecord> List() => ListWithIntegrity().Records;

    /// <summary>
    /// 启动恢复扫描：非终态记录 → 显式标 Interrupted/Unknown（§7.2：重启换纪元后未证实终态不标成功）。
    /// - Running/Waiting/Paused/Planned → Interrupted（本地推进被中断，可恢复候选）；
    /// - 提交在飞（IntentRecorded/Submitted/Accepted 且无观察终态）→ Unknown（结果不确定，禁止自动重跑）；
    /// - 终态记录不动；幂等键/RunId 绝不变更（不产生第二次执行）。
    /// 返回被标记的记录（供 Reconciler 对账决策）。
    /// </summary>
    public IReadOnlyList<WorkflowRunRecord> RecoverOnStart()
    {
        var recovered = new List<WorkflowRunRecord>();
        foreach (var rec in List())
        {
            if (rec.IsTerminal)
            {
                if (!HasUnresolvedTerminalResponsibility(rec)) continue;
                rec.State = WorkflowRunState.Unknown;
                rec.Note = AppendNote(rec.Note, "重启核对：终态标签含未确认外部责任，保留原身份并标Unknown，禁止自动重跑。");
                Persist(rec, rec.RecordRevision);
                recovered.Add(rec);
                continue;
            }
            var hasUnresolvedExternalFact = HasUnresolvedExternalFact(rec);
            if (rec.StopRequested)
            {
                var stoppedState = HasUnresolvedTerminalResponsibility(rec) ? WorkflowRunState.Unknown : WorkflowRunState.Cancelled;
                if (rec.State == stoppedState)
                {
                    recovered.Add(rec);
                    continue;
                }
                rec.State = stoppedState;
                rec.Note = AppendNote(rec.Note, "重启核对耐久停止意图；旧运行不恢复执行。");
                Persist(rec, rec.RecordRevision);
                recovered.Add(rec);
                continue;
            }
            // R4.8（宿主夹具连带发现）：Unknown 已是保守收敛终点（结果不确定待对账）——再扫描不改动、不追加笔记、
            // 更不降级 Interrupted（否则 ResumeAsync 的 Unknown 守卫被绕过，前置未知记录场景可未经对账恢复）；
            // 仍返回供 Reconciler/宿主对账决策（幂等保持，CrashWindow2 合同不变）。
            if (rec.State == WorkflowRunState.Unknown)
            {
                recovered.Add(rec);
                continue;
            }
            // Interrupted 通常幂等保持，但必须先检查未决外部事实；旧/冲突记录不得借 Interrupted 绕过对账。
            if (rec.State == WorkflowRunState.Interrupted && !hasUnresolvedExternalFact)
            {
                recovered.Add(rec);
                continue;
            }
            string note;
            // 先核验任何尚未闭合的外部事实；即使 State/LocalWaitDecision 声称本地等待，只要发送/收尾事实
            // 有矛盾或含混，仍必须 Unknown，不能让停驻标签遮住对账义务。
            if (hasUnresolvedExternalFact && (rec.State == WorkflowRunState.Completing || rec.PendingCompletion is not null))
            {
                // B5：收尾意图已落盘但执行结果未知——结果不确定，禁止自动补发收尾
                rec.State = WorkflowRunState.Unknown;
                note = "助手重启：收尾动作在飞（执行结果未证实），标 Unknown，需人工对账，禁止自动补发收尾。";
            }
            else if (hasUnresolvedExternalFact && rec.CurrentSubmission is { } sub)
            {
                rec.State = WorkflowRunState.Unknown;
                note = $"助手重启：提交存在未闭合发送事实（{sub.Key}，节点 {sub.NodeId}），标 Unknown，需按幂等键+job 查询对账，禁止自动重跑。";
            }
            else if (rec.State == WorkflowRunState.LocalWaitParking)
            {
                // [批次 20／Wave3／C11=(a)] 本地等待仅在上方已排除收尾/发送事实后收敛为 Interrupted。
                rec.State = WorkflowRunState.Interrupted;
                note = "助手重启：本地等待停驻运行，标 Interrupted（等待项绑定可由显式恢复重建）。";
            }
            else
            {
                rec.State = WorkflowRunState.Interrupted;
                note = "助手重启：运行被中断，标 Interrupted，恢复需显式决策。";
            }
            rec.Note = AppendNote(rec.Note, note);
            Persist(rec, rec.RecordRevision);
            recovered.Add(rec);
        }
        return recovered;
    }

    private FileStream AcquirePublicationLock()
    {
        WithContentionRetry(() =>
        {
            ThrowIfFileFaultInjected("create-runs-dir");
            Directory.CreateDirectory(_runsDir);
        });
        // This fixed lock name is never replaced or removed. A process-local monitor alone
        // cannot make revision validation and the subsequent atomic replacement one transaction.
        return WithContentionRetry(() =>
        {
            try
            {
                ThrowIfFileFaultInjected("acquire-run-lock");
                return new FileStream(Path.Combine(_runsDir, ".runstore.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (IsFileContention(ex))
            {
                ThrowIfFileFaultInjected("run-lock-contention");
                throw;
            }
        });
    }
    private void Persist(WorkflowRunRecord rec, int expectedRecordRevision, LocalNoSendProof? authorizedNoSend = null, TerminalReleaseSeal? authorizedSeal = null, RecoveryAssociationRecord? authorizedRecoveryAssociation = null, PreparedSendPermit? authorizedPermit = null, AdmissionParentSource? authorizedParent = null, RunAdmissionMapping? authorizedMapping = null, bool authorizedDiagnostic = false, bool? authorizedRouting = null)
    {
        lock (_gate)
        {
        if (authorizedDiagnostic && (expectedRecordRevision != 0 || !rec.NonExecutingDiagnostic
            || rec.State != WorkflowRunState.Cancelled))
            throw new RunRecordConflictException("非执行诊断只能一次创建为Cancelled。");
        using var ownerFence = authorizedDiagnostic ? null : _owner?.Store.AcquireOwnerFence(_owner);
        using var publicationLock = AcquirePublicationLock();
        var ownerPolicy = TryReadAllTextOrNull(OwnerPolicyPath, "read-owner-policy");
        if (!authorizedDiagnostic && (_requireOwnership || ownerPolicy is not null) && _owner is null
            || ownerPolicy is not null && ownerPolicy != OwnerPolicy)
            throw new RunRecordConflictException("运行目录要求原所有者能力，拒绝无资格的直接发布或恢复。");
        if (expectedRecordRevision == 0 && rec.StopAuthority is { } creatingAuthority && IsStartupSourceRevoked(creatingAuthority))
            throw new InvalidOperationException("原来源已经耐久停止，禁止借新 runId 再次运行；需要新的明确用户意图。");
        if (rec.LocalWaitDecision is { } waitDecision)
            rec.LocalWaitDecision = WorkflowRunner.SanitizeWaitDecision(waitDecision);
        // [P50 根因修复·批次四十九] Windows 文件争用家族：目标文件被其他句柄占用（并发读/原子替换窗口）时，
        // 既可能抛 `IOException`，也可能抛 `UnauthorizedAccessException`（"Access to the path is denied"）。
        // 两者都按**有界重试**处理；预算耗尽后**原样抛出**（响亮失败不静默，仍走既有 Unknown/冲突归类）。
        // **仅测试接缝**（生产恒 `null`）：在**原子发布步骤之前**（尚未写临时文件/替换目标）注入故障——
        // 用于 §17 P49／§16 交错③「准备阶段 `RunStore` 更新失败」的**真实存储写入路径**（此时内存记录
        // 已被合并回调修改、发布结果不明，正是 §12.3 M3③ 所指的窗口）。异常**原样抛出**（不包装），
        // 与真实磁盘故障走同一归类路径。回调按记录判定，便于只对「准备段冻结写」（`SendAttempted` 已置真）注入。
        if (PublishFaultForTest is { } faultForTest && faultForTest(rec) is { } injectedFault)
            throw injectedFault;
        if (string.IsNullOrWhiteSpace(rec.RunId))
            throw new ArgumentException("RunId 不能为空", nameof(rec));
        if (rec.RecordRevision != expectedRecordRevision)
            throw new RunRecordConflictException(
                $"运行 {rec.RunId} 记录修订冲突：期望 {expectedRecordRevision}，对象携带 {rec.RecordRevision}。");

        var file = PathFor(rec.RunId);
        // [第二轮会诊阻断项处置] 用**读取**取代 `File.Exists` 探测：不存在 ⇒ 无盘上记录（跳过核对与备份）；
        // 拒绝访问/争用 ⇒ 有界重试后**原样抛出**（不得被静默当作「不存在」而跳过修订核对与备份）。
        var currentText = TryReadAllTextOrNull(file, "read-persist-check");
        if (rec.NonExecutingDiagnostic && !authorizedDiagnostic)
            throw new RunRecordConflictException("非执行诊断不可由普通写者创建或升级。");
        if (authorizedDiagnostic && currentText is not null)
            throw new RunRecordConflictException("非执行诊断不能覆盖既有运行。");
        if (currentText is null && (rec.CurrentSubmission?.SendPermit is not null || rec.CurrentSubmission?.LocalNoSendProof is not null || rec.CurrentSubmission?.PreviousSendRounds is not null
            || rec.SubmissionHistory.Any(s => s.SendPermit is not null || s.LocalNoSendProof is not null || s.PreviousSendRounds is not null)))
            throw new RunRecordConflictException("新记录不能补造发送许可。");
        if (currentText is null && rec.AdmissionParentSource is not null && rec.AdmissionParentSource != authorizedParent)
            throw new RunRecordConflictException("新记录不能补造受理父来源。");
        if (currentText is null && (rec.CurrentSubmission?.NodeAdmissionRequired is not null
            || rec.SubmissionHistory.Any(s => s.NodeAdmissionRequired is not null)))
            throw new RunRecordConflictException("新记录不能补造历史发送路由。");
        if (currentText is null && rec.AdmissionMappings is not null)
            throw new RunRecordConflictException("新记录不能补造原准入映射。");
        if (currentText is null && rec.RecoveryAssociations.Count != 0)
            throw new RunRecordConflictException("新运行记录不得自造历史恢复关联。");
        if (currentText is not null)
        {
            WorkflowRunRecord? current;
            try
            {
                current = JsonSerializer.Deserialize<WorkflowRunRecord>(currentText, JsonOptions);
            }
            catch (JsonException)
            {
                // 盘上是坏文件：不静默覆盖，拒绝写入（原件保留，由人处置）
                throw new RunRecordConflictException($"运行 {rec.RunId} 盘上记录已损坏，拒绝覆盖写入（原件保留）。");
            }
            if (current?.NonExecutingDiagnostic == true)
                throw new RunRecordConflictException("非执行诊断永久不可变，不能恢复执行或增加责任。");
            if (current?.CurrentSubmission is { } previous && rec.CurrentSubmission?.Key != previous.Key
                && !rec.SubmissionHistory.Any(s => JsonSerializer.Serialize(s) == JsonSerializer.Serialize(previous))
                && TerminalReleaseEvidence.BodySettled(current, previous))
                rec.SubmissionHistory.Add(JsonSerializer.Deserialize<WorkflowSubmission>(JsonSerializer.Serialize(previous))!);
            if (current is not null) RunStoreEvidenceGuard.Validate(current, rec, authorizedNoSend, authorizedSeal, authorizedRecoveryAssociation, authorizedPermit, authorizedMapping, authorizedRouting);
            if (current?.StopRequested == true) rec.StopRequested = true;
            if (current is not null && current.StopAuthority != rec.StopAuthority)
                throw new RunRecordConflictException("停止授权创建即固定，禁止恢复/旧对象刷新或移除基线。");
            if (current is not null && current.RecordRevision != expectedRecordRevision)
                throw new RunRecordConflictException(
                    $"运行 {rec.RunId} 记录修订冲突：盘上 {current.RecordRevision}，期望 {expectedRecordRevision}（并发推进未覆盖）。");

            WithContentionRetry(() =>
            {
                ThrowIfFileFaultInjected("create-backup-dir");   // [第三轮会诊重要项] 建目录同样纳入覆盖取证
                Directory.CreateDirectory(_backupDir);
            });
            WithContentionRetry(() =>
            {
                ThrowIfFileFaultInjected("backup");
                // 备份源可能已被并发移除（合法）⇒ 仅忽略「不存在」，争用仍走重试/响亮
                try
                {
                    File.Copy(file, Path.Combine(_backupDir, $"{rec.RunId}.{current?.RecordRevision ?? 0}.run.json"), overwrite: true);
                }
                catch (FileNotFoundException) { }
            });
        }

        // ASTRA 二轮 S2：写入未发布时恢复内存对象的未提交修订/时间（避免调用方携带假修订继续推进）
        var previousUpdatedAt = rec.UpdatedAt;
        rec.RecordRevision = expectedRecordRevision + 1;
        rec.UpdatedAt = DateTimeOffset.Now;
        try
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(rec, JsonOptions));
            WithContentionRetry(() =>
            {
                ThrowIfFileFaultInjected("create-runs-dir");     // [第三轮会诊重要项] 建目录同样纳入覆盖取证
                Directory.CreateDirectory(_runsDir);             // 二轮：首次写入才建目录（纳入争用重试）
            });
            var tmp = Path.Combine(_runsDir, $".{rec.RunId}.{Guid.NewGuid():N}.tmp");
            // 临时文件写入同样纳入争用重试（[首轮会诊阻断项] 覆盖全部文件访问点）
            WithContentionRetry(() =>
            {
                ThrowIfFileFaultInjected("write-tmp");
                File.WriteAllBytes(tmp, bytes);
            });
            try
            {
                // 原子替换同样受争用影响（目标被并发读者/发布者占用）⇒ 有界重试；耗尽后抛出
                WithContentionRetry(() =>
                {
                    ThrowIfFileFaultInjected("publish");
                    if (!authorizedDiagnostic) _owner?.Store.VerifyOwnerFence(_owner);
                    File.Move(tmp, file, overwrite: true);
                });
            }
            finally
            {
                // 临时文件清理**尽力而为**：不给主流程结果添乱（残留 `.tmp` 不被 `List`/`UnknownFiles` 读取——
                // 二者只匹配 `*.run.json`），故此处吞掉清理异常（含争用耗尽）并留注释说明。
                try
                {
                    WithContentionRetry(() =>
                    {
                        ThrowIfFileFaultInjected("cleanup-tmp"); // [第三轮会诊重要项] 清残件同样纳入覆盖取证
                        if (File.Exists(tmp)) File.Delete(tmp);
                    });
                }
                catch (Exception)
                {
                    // best-effort：残留临时文件不影响权威读取路径
                }
            }
        }
        catch
        {
            rec.RecordRevision = expectedRecordRevision;
            rec.UpdatedAt = previousUpdatedAt;
            throw;
        }
        }
    }

    /// <summary>
    /// **[P50 根因修复] 文件争用家族判定**：`IOException`（共享冲突）与 `UnauthorizedAccessException`
    /// （Windows 上「路径被占用/拒绝访问」，例如目标正被另一句柄 `FileShare.None` 打开或正被原子替换）
    /// 在本工程中属**同一类瞬时争用**，一律按有界重试处理。
    /// </summary>
    internal static bool IsFileContention(Exception ex)
        // [第二轮会诊处置·收窄] **「不存在」不是争用**：`FileNotFoundException`／`DirectoryNotFoundException`
        // 都是 `IOException` 子类，但**等多久也不会出现**——把它们排除出重试族，避免在「路径已删除/从未创建」
        // 的病态路径上白烧整段预算（实测：夹具收尾后台写在已删除临时根上，单夹具白烧 237 次重试 ≈6s）。
        // 保留真正的争用族：共享冲突类 `IOException` 与 Windows「拒绝访问」（`UnauthorizedAccessException`）。
        => ex is UnauthorizedAccessException
           || (ex is IOException and not (FileNotFoundException or DirectoryNotFoundException));

    /// <summary>**有界争用重试**（默认 80×15ms ≈ 1.2s，与租约存储同口径）；预算耗尽后原样抛出。</summary>
    internal static T WithContentionRetry<T>(Func<T> action, int attempts = 80, int delayMs = 15)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (IsFileContention(ex) && i < attempts - 1)
            {
                // [第二轮会诊建议·诊断计数] 争用重试可观测：供负载诊断入口/回归排查（**只读观测，不参与判定**）。
                Interlocked.Increment(ref _contentionRetryAttempts);
                Volatile.Write(ref _lastContentionException, ex);
                Thread.Sleep(delayMs);
            }
            catch (Exception ex) when (IsFileContention(ex))
            {
                Interlocked.Increment(ref _contentionExhausted);
                Volatile.Write(ref _lastContentionException, ex);
                throw;
            }
        }
    }

    private static long _contentionRetryAttempts;
    private static long _contentionExhausted;
    private static Exception? _lastContentionException;
    /// <summary>**争用重试诊断计数**（[第二轮会诊建议] **只读观测**，不参与任何判定）：累计重试次数。</summary>
    internal static long ContentionRetryAttempts => Interlocked.Read(ref _contentionRetryAttempts);
    /// <summary>**争用重试诊断计数**：预算耗尽次数（耗尽即原样抛出）。</summary>
    internal static long ContentionExhausted => Interlocked.Read(ref _contentionExhausted);
    /// <summary>最近一次争用异常（**只读快照**，仅供诊断；不参与判定）。</summary>
    internal static Exception? LastContentionException => Volatile.Read(ref _lastContentionException);

    /// <summary>**有界争用重试**（无返回值重载）。</summary>
    internal static void WithContentionRetry(Action action, int attempts = 80, int delayMs = 15)
        => WithContentionRetry<object?>(() => { action(); return null; }, attempts, delayMs);

    /// <summary>
    /// **仅测试接缝**（生产恒 `null`）：按**操作标签**（`read-load`／`read-list`／`read-unknown`／`read-handoff`／
    /// `read-merging`／`read-persist-check`／`backup`／`write-tmp`／`publish`／`enumerate`／`create-runs-dir`／
    /// `create-backup-dir`／`cleanup-tmp`）注入文件访问故障——
    /// 用于**确定性**验证「争用家族（`IOException`／`UnauthorizedAccessException`）有界重试」与
    /// 「预算耗尽后的响亮失败」两类语义；回调每次返回非 null 即抛出该异常（可只抛前 N 次以模拟瞬时争用）。
    /// </summary>
    internal Func<string, Exception?>? FileOperationFaultForTest { get; set; }

    private string ReadAllTextWithFaultHook(string file, string operation)
    {
        ThrowIfFileFaultInjected(operation);
        return File.ReadAllText(file, Encoding.UTF8);
    }

    /// <summary>
    /// **[P50 结题·第二轮会诊阻断项处置] 读取文件，且**区分「不存在」与「被拒/争用」**：
    /// 不存在（`FileNotFoundException`／`DirectoryNotFoundException`）⇒ 返回 `null`（合法「无记录」）；
    /// 争用家族（`IOException`／`UnauthorizedAccessException`）⇒ **有界重试**，耗尽后**原样抛出**（响亮）；
    /// 其余原样抛出。**不得**再用 `File.Exists` 探测——那会把「拒绝访问」静默当作「不存在」。
    /// </summary>
    private string? TryReadAllTextOrNull(string file, string operation)
    {
        try
        {
            return WithContentionRetry(() => ReadAllTextWithFaultHook(file, operation));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;   // 已确认不存在（不含「拒绝访问」）
        }
    }

    /// <summary>**[第二轮会诊阻断项处置]** 枚举目录中的运行记录：目录不存在 ⇒ 返回空集；
    /// 争用家族 ⇒ 有界重试、耗尽后**原样抛出**（**不得**把不可访问目录误判为空目录）。</summary>
    private IReadOnlyList<string> EnumerateRunFilesOrEmpty()
    {
        try
        {
            return WithContentionRetry(() =>
            {
                ThrowIfFileFaultInjected("enumerate");
                return Directory.EnumerateFiles(_runsDir, "*.run.json").ToList();
            });
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private void ThrowIfFileFaultInjected(string operation)
    {
        if (FileOperationFaultForTest?.Invoke(operation) is { } injected) throw injected;
    }

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrEmpty(note) ? addition : note + " | " + addition;

    internal static string NewRunId() => "run-" + Guid.NewGuid().ToString("N")[..12];
    internal static string NewIdempotencyKey() => "idem-" + Guid.NewGuid().ToString("N");
}
