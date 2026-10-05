using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>流程文件状态：Ready=判型通过可用；Quarantined=判型失败隔离（原件保留，禁止执行/写回）。</summary>
public enum WorkflowFileStatus
{
    Ready,
    Quarantined,
}

/// <summary>流程目录条目（List 结果；含隔离原因与未支持类型，供 UI 预览/报告）。</summary>
public sealed record WorkflowCatalogEntry(
    string WorkflowId,
    string Name,
    string Revision,
    WorkflowFileStatus Status,
    string? QuarantineReason,
    IReadOnlyList<string> UnsupportedKinds,
    string? ActivationStatus,
    string FilePath);

/// <summary>修订冲突：期望修订与当前盘上字节哈希不一致（防并发/外部修改覆盖）。</summary>
public sealed class WorkflowRevisionConflictException : Exception
{
    public WorkflowRevisionConflictException(string message) : base(message) { }
}

/// <summary>流程文件被隔离：拒绝加载为可执行文档（原件保留，不自动重写）。</summary>
public sealed class WorkflowQuarantinedException : Exception
{
    public WorkflowQuarantinedException(string message) : base(message) { }
}

/// <summary>
/// 槲寄生 · 任务中心——WorkflowStore（流程定义持久化，R4.1）。
/// 路径：默认 %APPDATA%/NexusBGI/flows/（按 Windows 用户隔离，不走 SignalR 同步）；
/// 构造函数可注入独立配置根（D2，开发验证不碰真实 User 目录）。
///
/// 纪律（总计划 §3.4 持久化合同 + R4 分解 D3/D11）：
/// - 原子写：临时文件 + 同目录替换；写前把既有版本复制到 _backup/；
/// - 修订号 = 写入字节的 SHA256（读时实时重算，不信任缓存，防外部替换）；
/// - 覆盖保存必须携带期望修订（不携带 = 仅允许新建），防静默覆盖外部/并发改动；
/// - 坏文件隔离：判型失败原件保留、字节不动、列入目录标 Quarantined，绝不读失败回空后自动保存；
/// - 隔离文件可被显式覆盖的唯一方式：携带其当前字节哈希作为期望修订（明确的人为决定）。
/// </summary>
public sealed class WorkflowStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 与 R1 产物一致：非 ASCII 转义由默认 encoder 处理，形状语义不受影响
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _flowsDir;
    private readonly string _backupDir;

    /// <summary>写入串行化闸门（R4.8 一轮 I7：检查-备份-写入全程互斥，防同进程两次保存通过同一期望修订；
    /// 与 RunStore 同模式——乐观并发只防覆盖不防交错）。</summary>
    private readonly object _gate = new();

    public WorkflowStore(string flowsDir)
    {
        // R4.8 二轮（重要2）：构造零副作用——目录推迟到首次写入才创建，
        // 宿主/面板惰性创建或监控端绑定求值不再产生任何文件系统痕迹
        _flowsDir = flowsDir;
        _backupDir = Path.Combine(flowsDir, "_backup");
    }

    /// <summary>默认流程目录（%APPDATA%/NexusBGI/flows）。</summary>
    public static string DefaultFlowsDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI", "flows");

    /// <summary>流程身份 → 路径（R4.8 一轮 I7：身份即文件名成分，拒绝空白/路径分隔/上级跳转/非法文件名字符）。</summary>
    private string PathFor(string workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId)
            || workflowId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || workflowId.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"流程身份非法（不得含路径成分）：{workflowId}", nameof(workflowId));
        return Path.Combine(_flowsDir, workflowId + ".flow.json");
    }

    internal string MigrationRootFor(string workflowId)
    {
        _ = PathFor(workflowId);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_flowsDir))!, "workflow-migrations", workflowId);
    }

    internal MigrationSwitchTransaction OpenMigration(string workflowId)
        => new(_flowsDir, MigrationRootFor(workflowId), quiesce: AcquireMigrationWindow,
            effectService: new WorkflowFileMigrationEffectService());

    internal IDisposable AcquireMigrationWindow()
    {
        Monitor.Enter(_gate);
        return new MigrationWriteWindow(_gate);
    }

    private sealed class MigrationWriteWindow(object gate) : IDisposable
    {
        private object? _gate = gate;
        public void Dispose() { if (_gate is { } value) { _gate = null; Monitor.Exit(value); } }
    }

    internal T WithMigrationWindow<T>(Func<T> action)
    {
        lock (_gate) return action();
    }

    /// <summary>列出流程目录（含隔离文件；每次实时重算哈希，不信任缓存）。</summary>
    public IReadOnlyList<WorkflowCatalogEntry> List()
    {
        var entries = new List<WorkflowCatalogEntry>();
        if (!Directory.Exists(_flowsDir)) return entries; // 二轮：目录未建=空目录（构造不建目录）
        foreach (var file in Directory.EnumerateFiles(_flowsDir, "*.flow.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(InspectFile(file));
        }
        return entries;
    }

    /// <summary>加载流程文档。隔离文件抛 <see cref="WorkflowQuarantinedException"/>（绝不回空对象）。</summary>
    public WorkflowDocument Load(string workflowId) => LoadSnapshot(workflowId).Document;

    /// <summary>
    /// 一致性快照加载（B1 会诊：文档与修订号必须来自同一批字节，防"读到新文档配旧修订"错位）。
    /// 单次读盘、单次哈希、单次判型；隔离文件抛 <see cref="WorkflowQuarantinedException"/>。
    /// </summary>
    public WorkflowSnapshot LoadSnapshot(string workflowId)
    {
        var file = PathFor(workflowId);
        if (!File.Exists(file))
            throw new FileNotFoundException($"流程不存在: {workflowId}", file);
        var bytes = File.ReadAllBytes(file);
        var entry = InspectBytes(bytes, file);
        if (entry.Status == WorkflowFileStatus.Quarantined)
            throw new WorkflowQuarantinedException($"流程 {workflowId} 已隔离（{entry.QuarantineReason}），原件保留，禁止执行。");
        var doc = JsonSerializer.Deserialize<WorkflowDocument>(Encoding.UTF8.GetString(bytes), JsonOptions)
               ?? throw new WorkflowQuarantinedException($"流程 {workflowId} 反序列化为空，已按隔离处理。");
        if (doc.ExtensionData?.TryGetValue(WorkflowMigrationConsumer.TransactionField, out var transaction) == true)
        {
            using var migration = OpenMigration(workflowId);
            var manifest = migration.LoadValidated();
            if (transaction.ValueKind != JsonValueKind.String || manifest is null ||
                manifest.TransactionId != transaction.GetString() || manifest.Stage != MigrationStage.Committed ||
                manifest.CommitMarker != manifest.TransactionId || !string.IsNullOrEmpty(manifest.BlockedReason) ||
                manifest.ConfigRoot != Path.GetFullPath(_flowsDir) ||
                manifest.ActivationRecord?.Path != workflowId + ".flow.json")
                throw new WorkflowQuarantinedException("迁移提交未确认，禁止启动或编辑；请使用迁移回退恢复原候选。");
        }
        return new WorkflowSnapshot(doc, entry.Revision);
    }

    /// <summary>Only recovery identity/resource metadata; never an executable or editable snapshot.</summary>
    internal WorkflowMigrationRecoveryDescriptor LoadMigrationRecovery(string workflowId)
    {
        lock (_gate)
        {
            var file = PathFor(workflowId);
            if (MigrationSwitchTransaction.HasReparsePoint(file))
                throw new WorkflowQuarantinedException("Recovery file contains a link; original retained.");
            var bytes = File.ReadAllBytes(file);
            var entry = InspectBytes(bytes, file);
            if (entry.Status == WorkflowFileStatus.Quarantined)
                throw new WorkflowQuarantinedException("Recovery metadata is quarantined: " + entry.QuarantineReason);
            var current = JsonSerializer.Deserialize<WorkflowDocument>(Encoding.UTF8.GetString(bytes), JsonOptions)!;
            var hasTransaction = current.ExtensionData?.TryGetValue(WorkflowMigrationConsumer.TransactionField, out _) == true;
            if (!Directory.Exists(MigrationRootFor(workflowId)))
            {
                if (hasTransaction) throw new WorkflowQuarantinedException("Original recovery transaction is missing.");
                return DescribeRecovery(workflowId, current);
            }
            using var tx = OpenMigration(workflowId);
            var manifest = tx.LoadValidated() ?? throw new WorkflowQuarantinedException("Original recovery manifest is missing or invalid.");
            var relative = workflowId + ".flow.json";
            if (manifest.ConfigRoot != Path.GetFullPath(_flowsDir) ||
                manifest.ActivationRecord is { } activation && activation.Path != relative ||
                manifest.ChangedFiles.Any(c => c.Path != relative || c.Kind != ChangeKind.Modified))
                throw new WorkflowQuarantinedException("Recovery transaction root or target identity mismatch.");
            if (hasTransaction && (current.ExtensionData![WorkflowMigrationConsumer.TransactionField].ValueKind != JsonValueKind.String ||
                current.ExtensionData[WorkflowMigrationConsumer.TransactionField].GetString() != manifest.TransactionId))
                throw new WorkflowQuarantinedException("Recovery transaction identity mismatch.");
            if (manifest.ChangedFiles.Count == 0)
            {
                if (hasTransaction || current.Activation?.Status != "candidate-ready" ||
                    manifest.Stage is not (MigrationStage.None or MigrationStage.Snapshotting or MigrationStage.SnapshotReady))
                    throw new WorkflowQuarantinedException("Recovery target has not been declared.");
                return DescribeRecovery(workflowId, current);
            }
            if (!manifest.FileHashes.TryGetValue(relative, out var expected))
                throw new WorkflowQuarantinedException("Original workflow baseline is missing.");
            if (!hasTransaction && !HashBytes(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowQuarantinedException("Unbound recovery document differs from the original baseline.");
            var baselineFile = Path.Combine(manifest.SnapshotPath, relative);
            if (MigrationSwitchTransaction.HasReparsePoint(baselineFile))
                throw new WorkflowQuarantinedException("Recovery baseline contains a link.");
            var baselineBytes = File.ReadAllBytes(baselineFile);
            if (!HashBytes(baselineBytes).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                InspectBytes(baselineBytes, file).Status == WorkflowFileStatus.Quarantined)
                throw new WorkflowQuarantinedException("Original workflow baseline cannot be verified.");
            var baseline = JsonSerializer.Deserialize<WorkflowDocument>(Encoding.UTF8.GetString(baselineBytes), JsonOptions)!;
            if (baseline.WorkflowId != workflowId || baseline.Activation?.Status != "candidate-ready" ||
                baseline.ExtensionData?.ContainsKey(WorkflowMigrationConsumer.TransactionField) == true)
                throw new WorkflowQuarantinedException("Original candidate identity does not match recovery.");
            return DescribeRecovery(workflowId, baseline);
        }
    }

    private static WorkflowMigrationRecoveryDescriptor DescribeRecovery(string workflowId, WorkflowDocument document)
        => new(workflowId, document.Activation?.Status, document.Nodes.Select(n =>
            new WorkflowMigrationResourceReference(n.Kind, n.Ref?.Config, n.Ref?.ConfigKey, n.Ref?.Revision)).ToArray());

    /// <summary>
    /// 保存流程定义，返回新修订号。
    /// expectedRevision：覆盖既有文件时必填（= 保存前盘上字节哈希）；新建时必须为 null。
    /// doc.WorkflowId 为空时指派稳定身份（"wf-" + 8 位十六进制）。
    /// </summary>
    public string Save(WorkflowDocument doc, string? expectedRevision)
    {
        doc.WorkflowId ??= NewWorkflowId();
        if (string.IsNullOrWhiteSpace(doc.Name))
            throw new ArgumentException("流程名不能为空", nameof(doc));

        // R4.8 一轮 I7：检查-备份-写入全程互斥（同进程两次保存不得通过同一期望修订；外部非合作写方仍不承诺 CAS）
        lock (_gate)
        {
            var file = PathFor(doc.WorkflowId);
            var exists = File.Exists(file);
            if (!exists && expectedRevision is not null)
                throw new WorkflowRevisionConflictException($"流程 {doc.WorkflowId} 不存在，但携带了期望修订（可能已被外部删除）。");
            if (exists)
            {
                var currentHash = HashFileBytes(file);
                if (expectedRevision is null)
                    throw new WorkflowRevisionConflictException($"流程 {doc.WorkflowId} 已存在，覆盖保存必须携带期望修订。");
                if (!string.Equals(currentHash, expectedRevision, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowRevisionConflictException(
                        $"流程 {doc.WorkflowId} 修订冲突：期望 {expectedRevision[..Math.Min(8, expectedRevision.Length)]}…，当前 {currentHash[..8]}…（文件已被外部或并发修改，未覆盖）。");
            }

            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(doc, JsonOptions));
            Directory.CreateDirectory(_flowsDir); // 二轮：首次写入才建目录

            // 备份既有版本（保留可回滚副本，含隔离文件的显式覆盖场景）
            if (exists)
            {
                Directory.CreateDirectory(_backupDir);
                var priorHash = HashFileBytes(file);
                File.Copy(file, Path.Combine(_backupDir, $"{doc.WorkflowId}.{priorHash[..8]}.flow.json"), overwrite: true);
            }

            // 原子写：临时文件 + 同目录替换
            var tmp = Path.Combine(_flowsDir, $".{doc.WorkflowId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                File.Move(tmp, file, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            return HashBytes(bytes);
        }
    }

    /// <summary>
    /// 导入外部流程文件（如 R1 dry-run 候选的显式导入；预览不走此路径）。
    /// 判型失败抛 <see cref="WorkflowQuarantinedException"/>，源文件不动。
    /// </summary>
    public string Import(string sourceFile, string? expectedRevisionForOverwrite = null)
    {
        var probe = InspectFile(sourceFile);
        if (probe.Status == WorkflowFileStatus.Quarantined)
            throw new WorkflowQuarantinedException($"导入源判型失败（{probe.QuarantineReason}），未导入。");
        var doc = JsonSerializer.Deserialize<WorkflowDocument>(File.ReadAllText(sourceFile, Encoding.UTF8), JsonOptions)!;
        return Save(doc, expectedRevisionForOverwrite);
    }

    /// <summary>单文件判型（读取 + 判型分离：InspectBytes 保证快照/目录共用同一批字节）。</summary>
    private WorkflowCatalogEntry InspectFile(string file)
    {
        var fallbackId = Path.GetFileNameWithoutExtension(file).Replace(".flow", "");
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex)
        {
            return new WorkflowCatalogEntry(fallbackId, fallbackId, "", WorkflowFileStatus.Quarantined,
                "readFailure:" + ex.GetType().Name, [], null, file);
        }
        return InspectBytes(bytes, file);
    }

    /// <summary>单批字节判型（JSON DOM，不反序列化为产品模型前先做形状预检；同 R3.0 判型纪律）。</summary>
    private WorkflowCatalogEntry InspectBytes(byte[] bytes, string file)
    {
        var fallbackId = Path.GetFileNameWithoutExtension(file).Replace(".flow", "");
        var hash = HashBytes(bytes);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (JsonException)
        {
            return Quarantined(fallbackId, hash, "jsonInvalid", file);
        }
        if (root is not JsonObject obj)
            return Quarantined(fallbackId, hash, "jsonShapeInvalid", file);

        // R4.8 一轮 I7：schema 非字符串形状（数字/对象）时 GetValue<string> 会抛——逐形状判型，错误形状隔离不炸列表
        string? schema = null;
        if (obj["schema"] is JsonValue schemaVal)
            schemaVal.TryGetValue<string>(out schema);
        if (!string.Equals(schema, WorkflowDocumentSchema.Name, StringComparison.Ordinal))
            return Quarantined(fallbackId, hash, "schemaMismatch", file);
        if (obj["schemaVersion"] is not JsonValue ver || !ver.TryGetValue<int>(out var v) || v != WorkflowDocumentSchema.Version)
            return Quarantined(fallbackId, hash, "schemaVersionUnsupported", file);

        WorkflowDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<WorkflowDocument>(obj.ToJsonString(), JsonOptions)!;
        }
        catch (JsonException)
        {
            return Quarantined(fallbackId, hash, "modelShapeInvalid", file);
        }
        // R4.8 一轮 I7：可反序列化但字段形状异常（nodes:null 等）按隔离处理，FindUnsupportedKinds 不容忍 null 集合
        if (doc.Nodes is null || doc.Triggers is null || doc.Terminal is null
            || doc.Nodes.Any(n => n is null || n.Strategies is null))
            return Quarantined(fallbackId, hash, "modelShapeInvalid", file);
        var id = !string.IsNullOrWhiteSpace(doc.WorkflowId) ? doc.WorkflowId! : fallbackId;
        // R4.8 一轮 I7：本店目录内文件身份与文件名不一致 → 按身份寻址会加载错文件，响亮隔离
        // （仅本店目录强制；Import 探针的外部源文件名合法不同，R1 候选无 workflowId 走文件名 fallback 均不受影响）
        if (!string.IsNullOrWhiteSpace(doc.WorkflowId)
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(_flowsDir), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(doc.WorkflowId, fallbackId, StringComparison.OrdinalIgnoreCase))
            return Quarantined(fallbackId, hash, "idFilenameMismatch", file);
        return new WorkflowCatalogEntry(id, doc.Name, hash, WorkflowFileStatus.Ready, null,
            WorkflowKindCatalog.FindUnsupportedKinds(doc),
            string.IsNullOrWhiteSpace(doc.Activation?.Status) ? null : doc.Activation!.Status, file);
    }

    private static WorkflowCatalogEntry Quarantined(string id, string hash, string reason, string file)
        => new(id, Path.GetFileNameWithoutExtension(file).Replace(".flow", ""), hash,
            WorkflowFileStatus.Quarantined, reason, [], null, file);

    internal static string NewWorkflowId() => "wf-" + Guid.NewGuid().ToString("N")[..8];

    internal static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string HashFileBytes(string file) => HashBytes(File.ReadAllBytes(file));
}

/// <summary>流程一致性快照（B1：文档与修订号同源——同一批字节的解析结果与哈希）。</summary>
public sealed record WorkflowSnapshot(WorkflowDocument Document, string Revision);

// This descriptor deliberately has no WorkflowDocument and cannot be passed to a Runner.
internal sealed record WorkflowMigrationResourceReference(string Kind, string? Config, string? ConfigKey, string? Revision);
internal sealed record WorkflowMigrationRecoveryDescriptor(string WorkflowId, string? ActivationStatus,
    IReadOnlyList<WorkflowMigrationResourceReference> Resources);
