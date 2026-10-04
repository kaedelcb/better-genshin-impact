using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiplayerHoeingAssistant.Models; // R5.3 §24：OperationType 等仲裁面模型类型

namespace MultiplayerHoeingAssistant.Services;

/// <summary>台账记录状态（§4.2a：queued/AcceptedPendingExecution 不因运行快照为空而失去占用意义）。</summary>
public enum LedgerEntryState
{
    /// <summary>已受理待执行（BGI 回执 queued 等——占用成立）。</summary>
    AcceptedPendingExecution,
    /// <summary>运行中（权威快照/注册表可见）。</summary>
    Running,
    /// <summary>权威终态确认（BGI 快照/job 注册表）；按既有历史保留策略清理（不即时删除）。</summary>
    Terminal,
}

/// <summary>
/// 接管台账记录（§4.2a 必要字段：完整发送关联身份 submissionIdentity+sendSeq 与 Submission/Operations/回执处理/对账同一规则；
/// targetBgiEpoch 原目标实例不可改写；evidenceSource=原始回执词或对账结论+产生端，不伪造远端回执词）。
/// </summary>
public sealed class ExternalStartLedgerEntry
{
    [JsonPropertyName("submissionIdentity")] public string SubmissionIdentity { get; set; } = "";
    [JsonPropertyName("sendSeq")] public int SendSeq { get; set; }
    [JsonPropertyName("candidateId")] public string CandidateId { get; set; } = "";
    [JsonPropertyName("resourceRef")] public string ResourceRef { get; set; } = "";
    [JsonPropertyName("actionId")] public string ActionId { get; set; } = "";
    [JsonPropertyName("targetBgiEpoch")] public string TargetBgiEpoch { get; set; } = "";
    [JsonPropertyName("acceptedAtUtc")] public DateTimeOffset AcceptedAtUtc { get; set; }
    [JsonPropertyName("evidenceSource")] public string EvidenceSource { get; set; } = "";
    [JsonPropertyName("state")] public LedgerEntryState State { get; set; } = LedgerEntryState.AcceptedPendingExecution;
    /// <summary>关联 runId（E1/E2 托管流程；外部启动为空）。</summary>
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("terminalAtUtc")] public DateTimeOffset? TerminalAtUtc { get; set; }
    [JsonPropertyName("terminalEvidence")] public string? TerminalEvidence { get; set; }
    /// <summary>
    /// **远端作业句柄**（R5.3 §24.3-1；加法字段）：ext 队列 `taskHandle`／协议句柄；无则 null（**不得臆造**）。
    /// 重复接管按 §24.3-3 合并：两侧皆空=幂等；一侧空=补齐；两侧非空且不同=**拒绝并保守待对账**。
    /// </summary>
    [JsonPropertyName("jobId")] public string? JobId { get; set; }
    /// <summary>
    /// **观察时点副本**（R5.3 §24.2-2″／§24.20-D4）：权威终态证据首次被可信观察层接收的时点；
    /// 与「台账落盘时点」<see cref="TerminalAtUtc"/> **分离**，不得复用（首写保存、幂等重试严格比对）。
    /// </summary>
    [JsonPropertyName("terminalObservedAtUtc")] public DateTimeOffset? TerminalObservedAtUtc { get; set; }
    /// <summary>终态副本：原始结果词（不伪造）。</summary>
    [JsonPropertyName("rawTerminal")] public string? RawTerminal { get; set; }
    /// <summary>终态副本：完成层执行错误码（与信封 errorCode 语义分离）。</summary>
    [JsonPropertyName("executionErrorCode")] public string? ExecutionErrorCode { get; set; }
    /// <summary>终态副本：**完成观察证据来源**（[第三轮验证会诊] 新增，供 §24.15 读回逐字段比对）。</summary>
    [JsonPropertyName("terminalEvidenceSource")] public string? TerminalEvidenceSource { get; set; }
    /// <summary>终态类别（历史轮观察恢复使用；缺失的旧记录不得从原始终态词反推）。</summary>
    [JsonPropertyName("terminalKind")] public ExecutionResultKind? TerminalKind { get; set; }
    /// <summary>终态副本：持久化操作类型（§24.17；类型相关判定 fail-closed 的依据）。</summary>
    [JsonPropertyName("operationType")] public OperationType OperationType { get; set; } = OperationType.Unknown;
}

/// <summary>接管台账文件（修订守卫：一切写入走 revision 守卫的同一配置面）。</summary>
public sealed class ExternalStartLedgerFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("entries")] public List<ExternalStartLedgerEntry> Entries { get; set; } = [];
}

/// <summary>台账读取结果（损坏/读取失败/记录交叉不一致=保守待对账，不推导空闲）。</summary>
public sealed class LedgerReadResult
{
    public bool Valid { get; set; }
    public string? Detail { get; set; }
    public ExternalStartLedgerFile? File { get; set; }
}

/// <summary>台账变更结果。</summary>
public sealed class LedgerMutateResult
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public ExternalStartLedgerFile? File { get; set; }
}

/// <summary>
/// 外部启动接管台账（R5.2 冻结稿 §4.2a——E3/E4/E5 受理事实的权威接管记录；E1/E2=RunStore 运行记录）。
/// 文件 external-start-ledger.json + 固定锁 external-start-ledger.lock（同租约存取纪律：锁对象永不替换/删除；
/// 原子发布=临时文件同目录原子替换；构造零副作用，首次写入才建目录）。
/// 准入读取规则：资格快照的「执行占用」= BGI 快照 ∪ 运行台账 ∪ 本台账已受理未终结记录；
/// 关闭 Submission 前必须「接管台账已持久化并可跨重启重建」（B-2：两记录不得同时缺失）。
/// </summary>
public sealed class ExternalStartLedger
{
    /// <summary>
    /// 当前支持/写入的台账格式代（R5.3 §24.20-A：v2 起承载 `jobId` 与责任终态副本；
    /// **旧 v1 消费者**遇 2＝版本过高 → 保守待对账（响亮拒绝）；**新代码**读 v1＝兼容（缺字段视为「未取得」，**不等于**「无责任」），写入一律升 2）。
    /// </summary>
    public const int SupportedVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _configDir;
    private readonly string _ledgerPath;
    private readonly string _lockPath;
    private readonly Func<DateTimeOffset> _utcNow;

    public ExternalStartLedger(string configDir, Func<DateTimeOffset>? utcNow = null)
    {
        _configDir = configDir;
        _ledgerPath = Path.Combine(configDir, "external-start-ledger.json");
        _lockPath = Path.Combine(configDir, "external-start-ledger.lock");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>只读盘读台账（Absent=无正式文件 Valid 文件；Corrupt=解析失败/版本过高——保守待对账，不推导空闲）。</summary>
    public LedgerReadResult Read()
    {
        if (!Directory.Exists(_configDir))
            return new LedgerReadResult { Valid = true, File = null, Detail = null };
        if (!File.Exists(_lockPath))
            return ReadCore();
        using var lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return ReadCore();
    }

    private LedgerReadResult ReadCore()
    {
        if (!File.Exists(_ledgerPath))
            return new LedgerReadResult { Valid = true, File = null, Detail = null };
        string text;
        try
        {
            text = File.ReadAllText(_ledgerPath, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            return new LedgerReadResult { Valid = false, File = null, Detail = "台账读取失败（保守待对账）：" + ex.Message };
        }

        // 原始 JSON 预检（区分「字段缺失」与合法空值——缺 version/entries=非法，不得默认为合法空台账）。
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("version", out var versionEl) || versionEl.ValueKind != JsonValueKind.Number
                || !doc.RootElement.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
                return new LedgerReadResult { Valid = false, File = null, Detail = "台账 version/entries 字段缺失（保守待对账——缺字段不得默认为合法空台账）。" };
        }
        catch (JsonException ex)
        {
            return new LedgerReadResult { Valid = false, File = null, Detail = "台账解析失败（保守待对账）：" + ex.Message };
        }

        ExternalStartLedgerFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExternalStartLedgerFile>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new LedgerReadResult { Valid = false, File = null, Detail = "台账解析失败（保守待对账）：" + ex.Message };
        }
        if (file is null)
            return new LedgerReadResult { Valid = false, File = null, Detail = "台账反序列化为空（保守待对账）。" };
        if (file.Version > SupportedVersion)
            return new LedgerReadResult { Valid = false, File = null, Detail = $"台账 version={file.Version} 高于支持版本 {SupportedVersion}（保守待对账）。" };
        if (file.Version < 1)
            return new LedgerReadResult { Valid = false, File = null, Detail = $"台账 version={file.Version} 非法（缺版本不得默认为合法空台账——保守待对账）。" };
        if (file.Entries is null)
            return new LedgerReadResult { Valid = false, File = null, Detail = "台账 entries 缺失（保守待对账）。" };

        // §24.20-A 兼容读（[验证会诊阻断处置]）：**旧 v1 记录的终态副本观察时点缺失时做一次性投影**——
        // 以落盘时点（`TerminalAtUtc`，缺失则受理时点）作为观察时点的**兼容投影**，避免「v1 升版后立即自判损坏」的死路；
        // 投影后写入即持久化该值，责任判定不因此被放宽（旧记录本就没有冲突裁决审计可比对）。
        if (file.Version < 2)
        {
            foreach (var e in file.Entries.Where(x => x is not null && x.State == LedgerEntryState.Terminal
                                                      && x.TerminalObservedAtUtc is null))
                e.TerminalObservedAtUtc = e.TerminalAtUtc ?? e.AcceptedAtUtc;
        }
        if (!ValidateEntries(file, out var validationDetail))
            return new LedgerReadResult { Valid = false, File = null, Detail = validationDetail };

        return new LedgerReadResult { Valid = true, File = file, Detail = null };
    }

    /// <summary>
    /// 锁内原子变更（唯一写入口；修订守卫：变更函数返回 null=提交，非 null=拒绝原因文件不变）。
    /// expectedRevision&lt;0=不校验修订（首写/查询后写入场景由变更函数自行判等幂）。
    /// </summary>
    public LedgerMutateResult Mutate(long expectedRevision, Func<ExternalStartLedgerFile, string?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        Directory.CreateDirectory(_configDir); // 构造零副作用：首次写入才建目录
        using var lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var read = ReadCore();
        if (!read.Valid) return new LedgerMutateResult { Success = false, Reason = "ledger_unreadable", File = null };
        var file = read.File ?? new ExternalStartLedgerFile();
        if (expectedRevision >= 0 && file.Revision != expectedRevision)
            return new LedgerMutateResult { Success = false, Reason = "ledger_stale_revision", File = null };

        var reason = mutate(file);
        if (reason is not null) return new LedgerMutateResult { Success = false, Reason = reason, File = null };

        file.Version = SupportedVersion;
        file.Revision += 1;
        if (!ValidateEntries(file, out _))
            return new LedgerMutateResult { Success = false, Reason = "invalid_mutation_state", File = null };
        Publish(file);
        return new LedgerMutateResult { Success = true, Reason = null, File = file };
    }

    /// <summary>
    /// 记录受理事实（受理分支第一段：先持久化接管台账）。重复接管幂等——按完整发送关联身份
    /// （submissionIdentity+sendSeq）合并：同身份同要素=成功不改写；同身份不同要素=响亮拒绝 identity_conflict
    /// （§4.2b「台账已发布、Submission 未关闭」交错：按完整身份合并，不得产第二份记录）。
    /// </summary>
    public LedgerMutateResult RecordAccepted(ExternalStartLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // 写侧同口径校验（N6：与 ReadCore 严格读一致——缺关联字段的记录不得落盘，杜绝「自造损坏台账」）。
        if (entry.State != LedgerEntryState.AcceptedPendingExecution
            || entry.TerminalEvidence is not null || entry.TerminalAtUtc is not null
            || entry.TerminalObservedAtUtc is not null
            || string.IsNullOrWhiteSpace(entry.SubmissionIdentity) || entry.SendSeq < 1
            || string.IsNullOrWhiteSpace(entry.CandidateId) || string.IsNullOrWhiteSpace(entry.ResourceRef)
            || string.IsNullOrWhiteSpace(entry.ActionId) || string.IsNullOrWhiteSpace(entry.TargetBgiEpoch)
            || string.IsNullOrWhiteSpace(entry.EvidenceSource) || entry.AcceptedAtUtc == default
            || !Enum.IsDefined(entry.OperationType))
            return new LedgerMutateResult { Success = false, Reason = "invalid_request", File = null };
        return Mutate(-1, file =>
        {
            var existing = file.Entries.FirstOrDefault(e =>
                string.Equals(e.SubmissionIdentity, entry.SubmissionIdentity, StringComparison.Ordinal)
                && e.SendSeq == entry.SendSeq);
            if (existing is not null)
            {
                var same = string.Equals(existing.CandidateId, entry.CandidateId, StringComparison.Ordinal)
                           && string.Equals(existing.ResourceRef, entry.ResourceRef, StringComparison.Ordinal)
                           && string.Equals(existing.ActionId, entry.ActionId, StringComparison.Ordinal)
                           && string.Equals(existing.TargetBgiEpoch, entry.TargetBgiEpoch, StringComparison.Ordinal)
                           && string.Equals(existing.RunId, entry.RunId, StringComparison.Ordinal)
                           && string.Equals(existing.EvidenceSource, entry.EvidenceSource, StringComparison.Ordinal)
                           && existing.AcceptedAtUtc == entry.AcceptedAtUtc
                           && existing.OperationType == entry.OperationType;
                if (!same) return "identity_conflict"; // 重复接管幂等（含 RunId 与既有状态）；同身份不同要素=响亮拒绝
                // 终态不得被降级回未终结（R5.3 §24.7-1：迟到活动态不得降级 Terminal）——
                // [第五轮验证会诊] 本函数**从不改写既有 State/终态副本**，故「不降级」已由「不写入」保证；
                // 直接拒绝会让「终态已成、句柄后补」的对账重试永久失败，故此处放行并仅做下方句柄合并。
                // JobId 合并规则（R5.3 §24.3-3）：两空=幂等；一空一非空=补齐；两侧非空且不同=拒绝并保守待对账。
                var existingJob = existing.JobId ?? "";
                var incomingJob = entry.JobId ?? "";
                if (existingJob.Length == 0 && incomingJob.Length > 0) existing.JobId = incomingJob;
                else if (existingJob.Length > 0 && incomingJob.Length > 0
                         && !string.Equals(existingJob, incomingJob, StringComparison.Ordinal))
                    return "job_id_conflict";
                return null;
            }

            file.Entries.Add(entry);
            return null;
        });
    }

    /// <summary>
    /// Persist an additional receipt for an already identified external-start round. The round identity is
    /// canonical; later observations may have a different receive time/source, so preserve the first receipt
    /// metadata and only enrich a missing RunId/JobId. Conflicting non-empty handles remain fail-closed.
    /// </summary>
    public LedgerMutateResult RecordLateAcceptedReceipt(ExternalStartLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != LedgerEntryState.AcceptedPendingExecution
            || entry.TerminalEvidence is not null || entry.TerminalAtUtc is not null
            || entry.TerminalObservedAtUtc is not null
            || string.IsNullOrWhiteSpace(entry.SubmissionIdentity) || entry.SendSeq < 1
            || string.IsNullOrWhiteSpace(entry.CandidateId) || string.IsNullOrWhiteSpace(entry.ResourceRef)
            || string.IsNullOrWhiteSpace(entry.ActionId) || string.IsNullOrWhiteSpace(entry.TargetBgiEpoch)
            || string.IsNullOrWhiteSpace(entry.EvidenceSource) || entry.AcceptedAtUtc == default
            || entry.OperationType != OperationType.ExternalStart)
            return new LedgerMutateResult { Success = false, Reason = "invalid_request", File = null };

        return Mutate(-1, file =>
        {
            var existing = file.Entries.FirstOrDefault(e =>
                string.Equals(e.SubmissionIdentity, entry.SubmissionIdentity, StringComparison.Ordinal)
                && e.SendSeq == entry.SendSeq);
            if (existing is null)
            {
                file.Entries.Add(entry);
                return null;
            }
            if (existing.OperationType != OperationType.ExternalStart
                || !string.Equals(existing.CandidateId, entry.CandidateId, StringComparison.Ordinal)
                || !string.Equals(existing.ResourceRef, entry.ResourceRef, StringComparison.Ordinal)
                || !string.Equals(existing.ActionId, entry.ActionId, StringComparison.Ordinal)
                || !string.Equals(existing.TargetBgiEpoch, entry.TargetBgiEpoch, StringComparison.Ordinal)
                || (existing.RunId is not null && entry.RunId is not null
                    && !string.Equals(existing.RunId, entry.RunId, StringComparison.Ordinal)))
                return "identity_conflict";
            if (existing.RunId is null && entry.RunId is not null) existing.RunId = entry.RunId;
            var oldJob = existing.JobId ?? "";
            var newJob = entry.JobId ?? "";
            if (oldJob.Length == 0 && newJob.Length > 0) existing.JobId = newJob;
            else if (oldJob.Length > 0 && newJob.Length > 0
                     && !string.Equals(oldJob, newJob, StringComparison.Ordinal))
                return "job_id_conflict";
            return null;
        });
    }

    /// <summary>
    /// Confirm that the exact claimed acceptance payload is durably readable. A ledger-side JobId discovered
    /// after a claim with no JobId is a monotonic enrichment; all other claim fields must match exactly.
    /// A complete terminal entry still proves acceptance (a fast job may finish before the confirmation read).
    /// </summary>
    public bool ConfirmRebuildable(ExternalStartLedgerEntry expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var read = Read();
        return read.Valid
               && read.File?.Entries.Any(actual =>
                   string.Equals(actual.SubmissionIdentity, expected.SubmissionIdentity, StringComparison.Ordinal)
                   && actual.SendSeq == expected.SendSeq
                   && string.Equals(actual.CandidateId, expected.CandidateId, StringComparison.Ordinal)
                   && string.Equals(actual.ResourceRef, expected.ResourceRef, StringComparison.Ordinal)
                   && string.Equals(actual.ActionId, expected.ActionId, StringComparison.Ordinal)
                   && string.Equals(actual.TargetBgiEpoch, expected.TargetBgiEpoch, StringComparison.Ordinal)
                   && actual.AcceptedAtUtc == expected.AcceptedAtUtc
                   && string.Equals(actual.EvidenceSource, expected.EvidenceSource, StringComparison.Ordinal)
                   && actual.OperationType == expected.OperationType
                   && string.Equals(actual.RunId, expected.RunId, StringComparison.Ordinal)
                   && (string.IsNullOrEmpty(expected.JobId)
                       || string.Equals(actual.JobId, expected.JobId, StringComparison.Ordinal))) == true;
    }

    /// <summary>
    /// 权威终态转移（§4.2a：仅在关联 job 权威终态确认后转 Terminal；Terminal 记录不即时删除，
    /// 按既有历史保留策略清理——本组件不做保留期裁剪，裁剪归 R5.6 迁移/清理统一裁决）。
    /// **观察时点口径（R5.3 §24.2-2″／§24.20-D4）**：`observedAtUtc`＝权威证据首次被可信观察层接收的时点，
    /// 由调用方传入并**首写保存**；幂等重试必须**严格比对**既有值（不一致＝损坏/冲突，fail-closed）。
    /// `TerminalAtUtc` 仍＝**台账落盘时点**（本组件自行取时钟），二者不得复用。
    /// </summary>
    public LedgerMutateResult MarkTerminal(
        string submissionIdentity, int sendSeq, string terminalEvidence, DateTimeOffset observedAtUtc,
        string? rawTerminal = null, string? executionErrorCode = null,
        OperationType operationType = OperationType.Unknown, string? jobId = null,
        string? terminalEvidenceSource = null, ExecutionResultKind? terminalKind = null)
    {
        if (string.IsNullOrWhiteSpace(terminalEvidence))
            return new LedgerMutateResult { Success = false, Reason = "evidence_required", File = null };
        if (observedAtUtc == default)
            return new LedgerMutateResult { Success = false, Reason = "observed_at_required", File = null };
        if (terminalKind is { } kind
            && (!Enum.IsDefined(kind)
                || kind is not (ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled)))
            return new LedgerMutateResult { Success = false, Reason = "terminal_kind_invalid", File = null };
        if (terminalKind == ExecutionResultKind.Failed && string.IsNullOrWhiteSpace(executionErrorCode))
            return new LedgerMutateResult { Success = false, Reason = "terminal_error_code_required", File = null };
        return Mutate(-1, file =>
        {
            var entry = file.Entries.FirstOrDefault(e =>
                string.Equals(e.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                && e.SendSeq == sendSeq);
            if (entry is null) return "entry_not_found";
            if (entry.State == LedgerEntryState.Terminal)
            {
                // 幂等：既有观察时点必须与本次传入**全等**（重试不得改写观察事实）。
                if (entry.TerminalObservedAtUtc is { } existingObserved && existingObserved != observedAtUtc)
                    return "terminal_observed_conflict";
                // §24.2-2″／§24.7-1（[落地批次会诊重要项处置]）：**先做句柄合并与终态载荷核对，再判幂等**——
                // 否则「相同观察时点、不同终态载荷」或「既有句柄为空需补齐」都会被静默当作幂等吞掉。
                var existingJobId = entry.JobId ?? "";
                var incomingJobId = jobId ?? "";
                if (existingJobId.Length > 0 && incomingJobId.Length > 0
                    && !string.Equals(existingJobId, incomingJobId, StringComparison.Ordinal))
                    return "job_id_conflict";
                if (existingJobId.Length == 0 && incomingJobId.Length > 0) entry.JobId = incomingJobId;
                // [验证会诊重要项处置] 终态载荷**逐步强校验**：既有记录已带值的字段，重试必须**原值重放**——
                // 传空/Unknown 视为「载荷缺失」而拒绝（不得用「调用方省略」跳过一致性核对）。
                if (entry.RawTerminal is { Length: > 0 } recordedRaw)
                {
                    if (string.IsNullOrEmpty(rawTerminal)) return "terminal_payload_required";
                    if (!string.Equals(recordedRaw, rawTerminal, StringComparison.Ordinal)) return "terminal_payload_conflict";
                }
                else if (rawTerminal is { Length: > 0 }) entry.RawTerminal = rawTerminal;

                if (entry.ExecutionErrorCode is { Length: > 0 } recordedError)
                {
                    if (string.IsNullOrEmpty(executionErrorCode)) return "terminal_payload_required";
                    if (!string.Equals(recordedError, executionErrorCode, StringComparison.Ordinal)) return "terminal_payload_conflict";
                }
                else if (executionErrorCode is { Length: > 0 }) entry.ExecutionErrorCode = executionErrorCode;

                // 完成观察证据来源同样按「有值必须原值重放」核对（[第三轮验证会诊]：读回逐字段比对需要该副本）。
                if (entry.TerminalEvidenceSource is { Length: > 0 } recordedSource)
                {
                    if (string.IsNullOrEmpty(terminalEvidenceSource)) return "terminal_payload_required";
                    if (!string.Equals(recordedSource, terminalEvidenceSource, StringComparison.Ordinal))
                        return "terminal_payload_conflict";
                }
                else if (terminalEvidenceSource is { Length: > 0 }) entry.TerminalEvidenceSource = terminalEvidenceSource;

                if (entry.TerminalKind is { } recordedKind)
                {
                    if (terminalKind is null) return "terminal_payload_required";
                    if (recordedKind != terminalKind.Value) return "terminal_payload_conflict";
                }
                else if (terminalKind is { } incomingKind) entry.TerminalKind = incomingKind;

                if (entry.OperationType is not OperationType.Unknown)
                {
                    if (operationType == OperationType.Unknown) return "terminal_payload_required";
                    if (entry.OperationType != operationType) return "terminal_payload_conflict";
                }
                else if (operationType != OperationType.Unknown) entry.OperationType = operationType;
                return null;
            }
            entry.State = LedgerEntryState.Terminal;
            entry.TerminalAtUtc = _utcNow();          // 台账落盘时点（诊断/保留策略用）
            entry.TerminalObservedAtUtc = observedAtUtc; // 观察时点副本（审计比对用；与上一行分离）
            entry.TerminalEvidence = terminalEvidence;
            entry.RawTerminal ??= rawTerminal;
            entry.ExecutionErrorCode ??= executionErrorCode;
            entry.TerminalEvidenceSource ??= terminalEvidenceSource;
            entry.TerminalKind ??= terminalKind;
            if (operationType != OperationType.Unknown) entry.OperationType = operationType;
            // 句柄补齐（一空一非空=补齐；两侧非空且不同=拒绝并保守待对账，§24.3-3）。
            var existingJob = entry.JobId ?? "";
            var incomingJob = jobId ?? "";
            if (existingJob.Length == 0 && incomingJob.Length > 0) entry.JobId = incomingJob;
            else if (existingJob.Length > 0 && incomingJob.Length > 0
                     && !string.Equals(existingJob, incomingJob, StringComparison.Ordinal))
                return "job_id_conflict";
            return null;
        });
    }

    /// <summary>
    /// 准入读取（§4.2a）：已受理未终结记录（占用语义——queued 不因运行快照为空而失去占用意义）。
    /// Unknown=true=台账损坏/读取失败（保守待对账，绝不推导空闲——与「无记录」严格区分）。
    /// </summary>
    public (bool Unknown, IReadOnlyList<ExternalStartLedgerEntry> Entries) GetOccupancy()
    {
        var read = Read();
        if (!read.Valid) return (true, []);
        if (read.File is null) return (false, []);
        return (false, read.File.Entries.Where(e => e.State != LedgerEntryState.Terminal).ToList());
    }

    private void Publish(ExternalStartLedgerFile file)
    {
        // §24.20-A（[落地批次会诊阻断处置]）：**任何写入一律升到当前格式代**——
        // 若读 v1 后按原版本写回，新增的责任副本字段会以旧版本落盘，旧消费者会静默忽略（fail-open）。
        file.Version = SupportedVersion;
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(file, JsonOptions));
        var tmp = Path.Combine(_configDir, ".ledger-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _ledgerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static bool ValidateEntries(ExternalStartLedgerFile file, out string detail)
    {
        detail = "台账记录身份/关联字段/枚举/唯一性/终态证据/作业句柄非法（保守待对账——不完整记录不得充当占用证明）。";
        if (file.Version < 1 || file.Entries is null) return false;
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var seenJobIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in file.Entries)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.SubmissionIdentity) || e.SendSeq < 1 || !Enum.IsDefined(e.State)
                || string.IsNullOrWhiteSpace(e.CandidateId) || string.IsNullOrWhiteSpace(e.ResourceRef)
                || string.IsNullOrWhiteSpace(e.ActionId) || string.IsNullOrWhiteSpace(e.TargetBgiEpoch)
                || string.IsNullOrWhiteSpace(e.EvidenceSource) || e.AcceptedAtUtc == default
                || !Enum.IsDefined(e.OperationType)
                || (e.State == LedgerEntryState.Terminal
                    && (string.IsNullOrWhiteSpace(e.TerminalEvidence)
                        || (file.Version >= 2 && e.TerminalObservedAtUtc is null)))
                || (e.TerminalKind is { } terminalKind
                    && (!Enum.IsDefined(terminalKind)
                        || terminalKind is not (ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled)
                        || e.State != LedgerEntryState.Terminal))
                || (!string.IsNullOrWhiteSpace(e.JobId) && !seenJobIds.Add(e.JobId!))
                || !seenIdentities.Add(e.SubmissionIdentity + "#" + e.SendSeq))
                return false;
        }
        return true;
    }
}
