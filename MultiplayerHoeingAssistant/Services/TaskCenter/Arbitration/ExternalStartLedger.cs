using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
}

/// <summary>接管台账文件（修订守卫：一切写入走 revision 守卫的同一配置面）。</summary>
public sealed class ExternalStartLedgerFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
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
    public const int SupportedVersion = 1;

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
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in file.Entries)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.SubmissionIdentity) || e.SendSeq < 1 || !Enum.IsDefined(e.State)
                || string.IsNullOrWhiteSpace(e.CandidateId) || string.IsNullOrWhiteSpace(e.ResourceRef)
                || string.IsNullOrWhiteSpace(e.ActionId) || string.IsNullOrWhiteSpace(e.TargetBgiEpoch)
                || string.IsNullOrWhiteSpace(e.EvidenceSource)
                || (e.State == LedgerEntryState.Terminal && string.IsNullOrWhiteSpace(e.TerminalEvidence))
                || !seenIdentities.Add(e.SubmissionIdentity + "#" + e.SendSeq))
                return new LedgerReadResult { Valid = false, File = null, Detail = "台账记录身份/关联字段/枚举/唯一性/终态证据非法（保守待对账——不完整记录不得充当占用证明）。" };
        }

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

        file.Revision += 1;
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
        if (string.IsNullOrWhiteSpace(entry.SubmissionIdentity) || entry.SendSeq < 1
            || string.IsNullOrWhiteSpace(entry.CandidateId) || string.IsNullOrWhiteSpace(entry.ResourceRef)
            || string.IsNullOrWhiteSpace(entry.ActionId) || string.IsNullOrWhiteSpace(entry.TargetBgiEpoch)
            || string.IsNullOrWhiteSpace(entry.EvidenceSource))
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
                           && string.Equals(existing.RunId, entry.RunId, StringComparison.Ordinal);
                return same ? null : "identity_conflict"; // 重复接管幂等（含 RunId 与既有状态）；同身份不同要素=响亮拒绝
            }

            file.Entries.Add(entry);
            return null;
        });
    }

    /// <summary>
    /// 确认台账记录足以跨重启重建（受理分支第二段：读回校验完整身份在册）。
    /// 完整终态记录同样证明「曾受理」（快速完成 job 在记录后、确认前转 Terminal 不得阻断责任结清）。
    /// </summary>
    public bool ConfirmRebuildable(string submissionIdentity, int sendSeq)
    {
        var read = Read();
        return read.Valid
               && read.File?.Entries.Any(e =>
                   string.Equals(e.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                   && e.SendSeq == sendSeq) == true;
    }

    /// <summary>
    /// 权威终态转移（§4.2a：仅在关联 job 权威终态确认后转 Terminal；Terminal 记录不即时删除，
    /// 按既有历史保留策略清理——本组件不做保留期裁剪，裁剪归 R5.6 迁移/清理统一裁决）。
    /// </summary>
    public LedgerMutateResult MarkTerminal(string submissionIdentity, int sendSeq, string terminalEvidence)
    {
        if (string.IsNullOrWhiteSpace(terminalEvidence))
            return new LedgerMutateResult { Success = false, Reason = "evidence_required", File = null };
        return Mutate(-1, file =>
        {
            var entry = file.Entries.FirstOrDefault(e =>
                string.Equals(e.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                && e.SendSeq == sendSeq);
            if (entry is null) return "entry_not_found";
            if (entry.State == LedgerEntryState.Terminal) return null; // 幂等
            entry.State = LedgerEntryState.Terminal;
            entry.TerminalAtUtc = _utcNow();
            entry.TerminalEvidence = terminalEvidence;
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
}
