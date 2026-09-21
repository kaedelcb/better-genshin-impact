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
        // R4.8 二轮（重要2）：构造零副作用——目录推迟到首次 Persist 才创建
        _runsDir = runsDir;
        _backupDir = Path.Combine(runsDir, "_backup");
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
            if (!Directory.Exists(_runsDir)) return bad; // 二轮：目录未建=无记录
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

    /// <summary>创建运行记录（初始 Planned + 固定幂等键；RecordRevision 从 1 起）。
    /// handoff（R4.9）：移交身份随创建原子落盘——受理提交点即本持久化，崩溃窗无「已受理无身份」记录。</summary>
    public WorkflowRunRecord CreateRun(string workflowId, string workflowRevision, string? note = null, HandoffIdentity? handoff = null)
    {
        var now = DateTimeOffset.Now;
        var rec = new WorkflowRunRecord
        {
            RunId = NewRunId(),
            WireRunId = Guid.NewGuid().ToString("N"), // B1：线协议 workflowRunId 强制 Guid（BGI ReadIdentity 严格解析）
            WorkflowId = workflowId,
            WorkflowRevision = workflowRevision,
            State = WorkflowRunState.Planned,
            IdempotencyKey = NewIdempotencyKey(),
            CreatedAt = now,
            UpdatedAt = now,
            Note = note,
            Handoffs = handoff is null ? [] : [handoff],
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
        if (!Directory.Exists(_runsDir)) return HandoffLedgerQuery.MissInstance;
        WorkflowRunRecord? bestRun = null;
        HandoffIdentity? bestBinding = null;
        var incomplete = false;
        foreach (var file in Directory.EnumerateFiles(_runsDir, "*.run.json"))
        {
            WorkflowRunRecord? rec;
            try
            {
                rec = JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
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
        => rec.CurrentSubmission is { InFlight: true }
           || rec.State == WorkflowRunState.Completing
           || rec.PendingCompletion is not null;

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(applyOwnedFields);
        lock (_gate)
        {
            var file = PathFor(runId);
            if (!File.Exists(file))
            {
                latest = null;
                return false;
            }

            WorkflowRunRecord? current;
            try
            {
                current = JsonSerializer.Deserialize<WorkflowRunRecord>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
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

            Persist(current, current.RecordRevision);   // 以**最新**修订发布（不会自撞冲突）
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
        if (!Directory.Exists(_runsDir)) return list; // 二轮：目录未建=无记录
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
            // R4.8（宿主夹具连带发现）：Unknown 已是保守收敛终点（结果不确定待对账）——再扫描不改动、不追加笔记、
            // 更不降级 Interrupted（否则 ResumeAsync 的 Unknown 守卫被绕过，前置未知记录场景可未经对账恢复）；
            // 仍返回供 Reconciler/宿主对账决策（幂等保持，CrashWindow2 合同不变）。
            // R4.9（ASTRA 二轮 I4）：Interrupted 同样幂等保持——重新 Persist 会刷新 UpdatedAt
            // 重排 resume「最新」候选并重复追加留痕；扫描不得改变业务排序依据。
            if (rec.State is WorkflowRunState.Unknown or WorkflowRunState.Interrupted)
            {
                recovered.Add(rec);
                continue;
            }
            string note;
            if (rec.State == WorkflowRunState.Completing || rec.PendingCompletion is not null)
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

        // ASTRA 二轮 S2：写入未发布时恢复内存对象的未提交修订/时间（避免调用方携带假修订继续推进）
        var previousUpdatedAt = rec.UpdatedAt;
        rec.RecordRevision = expectedRecordRevision + 1;
        rec.UpdatedAt = DateTimeOffset.Now;
        try
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(rec, JsonOptions));
            Directory.CreateDirectory(_runsDir); // 二轮：首次写入才建目录
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
        catch
        {
            rec.RecordRevision = expectedRecordRevision;
            rec.UpdatedAt = previousUpdatedAt;
            throw;
        }
        }
    }

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrEmpty(note) ? addition : note + " | " + addition;

    internal static string NewRunId() => "run-" + Guid.NewGuid().ToString("N")[..12];
    internal static string NewIdempotencyKey() => "idem-" + Guid.NewGuid().ToString("N");
}
