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
public sealed class RunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _runsDir;
    private readonly string _backupDir;

    /// <summary>写入串行化闸门（ASTRA 二轮重要项①：乐观并发只防覆盖不防交错，读-检-写全程互斥）。</summary>
    private readonly object _gate = new();

    public RunStore(string runsDir)
    {
        _runsDir = runsDir;
        _backupDir = Path.Combine(runsDir, "_backup");
        Directory.CreateDirectory(_runsDir);
    }

    /// <summary>默认运行目录（%APPDATA%/NexusBGI/runs）。</summary>
    public static string DefaultRunsDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI", "runs");

    private string PathFor(string runId) => Path.Combine(_runsDir, runId + ".run.json");

    /// <summary>无法解析的记录文件（隔离展示用；原件保留，绝不自动删改）。</summary>
    public IReadOnlyList<string> UnknownFiles
    {
        get
        {
            var bad = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_runsDir, "*.run.json"))
            {
                try
                {
                    var rec = JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                    if (rec is null || string.IsNullOrWhiteSpace(rec.RunId)) bad.Add(file);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    bad.Add(file);
                }
            }
            bad.Sort(StringComparer.Ordinal);
            return bad;
        }
    }

    /// <summary>创建运行记录（初始 Planned + 固定幂等键；RecordRevision 从 1 起）。</summary>
    public WorkflowRunRecord CreateRun(string workflowId, string workflowRevision, string? note = null)
    {
        var now = DateTimeOffset.Now;
        var rec = new WorkflowRunRecord
        {
            RunId = NewRunId(),
            WorkflowId = workflowId,
            WorkflowRevision = workflowRevision,
            State = WorkflowRunState.Planned,
            IdempotencyKey = NewIdempotencyKey(),
            CreatedAt = now,
            UpdatedAt = now,
            Note = note,
        };
        Persist(rec, expectedRecordRevision: 0);
        return rec;
    }

    /// <summary>
    /// 记录提交意图（必须在向 BGI 提交前调用；D11/B3：崩溃后按意图 + 账本/job 查询对账，不重跑）。
    /// B2：一提交一身份——submission 键须经 DeriveSubmissionKey 按出现身份确定性派生；
    /// 前一提交终态未确认（InFlight）时拒绝重叠提交（防 jobId/终态跨节点残留误判）。
    /// </summary>
    public void RecordIntent(WorkflowRunRecord rec, WorkflowSubmission submission)
    {
        if (string.IsNullOrWhiteSpace(submission.Key))
            throw new InvalidOperationException("提交意图要求确定性派生幂等键已存在。");
        if (string.IsNullOrWhiteSpace(submission.NodeId))
            throw new InvalidOperationException("提交意图要求绑定节点出现身份。");
        if (rec.CurrentSubmission is { } prev && prev.InFlight)
            throw new InvalidOperationException(
                $"前一提交 {prev.Key}（节点 {prev.NodeId}）终态未确认，拒绝重叠提交（一提交一身份）。");
        submission.Intent = SubmitIntentState.IntentRecorded;
        submission.RecordedAt = DateTimeOffset.Now;
        rec.CurrentSubmission = submission;
        Persist(rec, rec.RecordRevision);
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

    /// <summary>推进记录（提交受理/终态/水位/等待/收尾状态更新；记录修订单调递增）。</summary>
    public void Update(WorkflowRunRecord rec) => Persist(rec, rec.RecordRevision);

    /// <summary>读取运行记录（不存在返回 null；解析失败抛 JsonException——调用方按隔离处理，不回空）。</summary>
    public WorkflowRunRecord? Load(string runId)
    {
        var file = PathFor(runId);
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
    }

    /// <summary>列出全部可解析记录（按创建时间排序）。</summary>
    public IReadOnlyList<WorkflowRunRecord> List()
    {
        var list = new List<WorkflowRunRecord>();
        foreach (var file in Directory.EnumerateFiles(_runsDir, "*.run.json"))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                if (rec is not null && !string.IsNullOrWhiteSpace(rec.RunId)) list.Add(rec);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 隔离：坏文件不参与列表（UnknownFiles 另行展示），原件保留
            }
        }
        list.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return list;
    }

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
            if (rec.IsTerminal) continue;
            string note;
            if (rec.State == WorkflowRunState.Completing || rec.PendingCompletionAction is not null)
            {
                // B5：收尾意图已落盘但执行结果未知——结果不确定，禁止自动补发收尾
                rec.State = WorkflowRunState.Unknown;
                note = "助手重启：收尾动作在飞（执行结果未证实），标 Unknown，需人工对账，禁止自动补发收尾。";
            }
            else if (rec.CurrentSubmission is { } sub && sub.InFlight)
            {
                rec.State = WorkflowRunState.Unknown;
                note = $"助手重启：提交在飞且终态未证实（{sub.Key}，节点 {sub.NodeId}），标 Unknown，需按幂等键+job 查询对账，禁止自动重跑。";
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

    private void Persist(WorkflowRunRecord rec, int expectedRecordRevision)
    {
        lock (_gate)
        {
        if (string.IsNullOrWhiteSpace(rec.RunId))
            throw new ArgumentException("RunId 不能为空", nameof(rec));
        if (rec.RecordRevision != expectedRecordRevision)
            throw new RunRecordConflictException(
                $"运行 {rec.RunId} 记录修订冲突：期望 {expectedRecordRevision}，对象携带 {rec.RecordRevision}。");

        var file = PathFor(rec.RunId);
        if (File.Exists(file))
        {
            WorkflowRunRecord? current;
            try
            {
                current = JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
            }
            catch (JsonException)
            {
                // 盘上是坏文件：不静默覆盖，拒绝写入（原件保留，由人处置）
                throw new RunRecordConflictException($"运行 {rec.RunId} 盘上记录已损坏，拒绝覆盖写入（原件保留）。");
            }
            if (current is not null && current.RecordRevision != expectedRecordRevision)
                throw new RunRecordConflictException(
                    $"运行 {rec.RunId} 记录修订冲突：盘上 {current.RecordRevision}，期望 {expectedRecordRevision}（并发推进未覆盖）。");

            Directory.CreateDirectory(_backupDir);
            File.Copy(file, Path.Combine(_backupDir, $"{rec.RunId}.{current?.RecordRevision ?? 0}.run.json"), overwrite: true);
        }

        rec.RecordRevision = expectedRecordRevision + 1;
        rec.UpdatedAt = DateTimeOffset.Now;
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(rec, JsonOptions));
        var tmp = Path.Combine(_runsDir, $".{rec.RunId}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        }
    }

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrEmpty(note) ? addition : note + " | " + addition;

    internal static string NewRunId() => "run-" + Guid.NewGuid().ToString("N")[..12];
    internal static string NewIdempotencyKey() => "idem-" + Guid.NewGuid().ToString("N");
}
