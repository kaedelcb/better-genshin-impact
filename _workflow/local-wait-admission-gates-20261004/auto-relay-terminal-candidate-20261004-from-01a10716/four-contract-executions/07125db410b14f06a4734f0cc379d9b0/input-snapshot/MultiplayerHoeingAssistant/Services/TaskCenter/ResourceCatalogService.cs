using System.IO;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 资源目录传输面（R4.3 可测试接缝）：隔离 BgiExternalClient 的管道生命周期。
/// 实现：BgiExternalCatalogTransport（生产）/ 测试假实现。
/// </summary>
public interface IResourceCatalogTransport
{
    /// <summary>ext 通道是否就绪。</summary>
    bool IsReady { get; }

    /// <summary>能力查询（DAP 规则：缺省即不支持）。</summary>
    bool HasCapability(string name);

    /// <summary>发送 ext 操作，返回 data 原始 JSON；失败/未就绪返回 null。</summary>
    Task<string?> SendAsync(string operation, object payload, CancellationToken ct);
}

/// <summary>生产传输面：包装 BgiExternalClient。</summary>
public sealed class BgiExternalCatalogTransport : IResourceCatalogTransport
{
    public const string OpConfigList = "ext.config.list";
    public const string OpConfigDescribe = "ext.config.describe";
    public const string CapabilityConfigRevision = "config.revision";

    private readonly BgiExternalClient _client;

    public BgiExternalCatalogTransport(BgiExternalClient client) => _client = client;

    public bool IsReady => _client.State == BgiExternalLinkState.Ready;

    public bool HasCapability(string name) => _client.HasCapability(name);

    public async Task<string?> SendAsync(string operation, object payload, CancellationToken ct)
    {
        try
        {
            var response = await _client.SendCommandAsync(operation, payload, null, ct).ConfigureAwait(false);
            return response.Success ? response.Data : null;
        }
        catch (InvalidOperationException)
        {
            return null; // 通道未就绪/超时由调用方按降级处理
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}

/// <summary>describe 响应的纯解析（无 IPC 依赖，夹具直测）。</summary>
public static class ResourceCatalogParser
{
    /// <summary>解析 ext.config.list 响应：配置组名 + 一条龙配置名。</summary>
    public static (IReadOnlyList<string> Groups, IReadOnlyList<string> OneDragons) ParseList(string dataJson)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;
        return (ReadStringArray(root, "configGroups"), ReadStringArray(root, "oneClickConfigs"));
    }

    /// <summary>
    /// 解析 ext.config.describe 响应为资源条目：配置/组本体 + （一条龙时）单项任务条目。
    /// 单项任务同时绑定所属配置与稳定 taskId（D14）；修订继承所属配置。
    /// </summary>
    public static (TaskCenterResourceEntry ConfigEntry, IReadOnlyList<TaskCenterResourceEntry> Tasks)
        ParseDescribe(string dataJson, TaskCenterResourceKind kind, string name)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;
        var revision = root.TryGetProperty("configRevision", out var rev) && rev.ValueKind == JsonValueKind.String
            ? rev.GetString() : null;
        string? epoch = null;
        if (root.TryGetProperty("bgiEpoch", out var epochEl) && epochEl.ValueKind == JsonValueKind.Object)
        {
            var pid = epochEl.TryGetProperty("processId", out var p) ? p.ToString() : "";
            var ticks = epochEl.TryGetProperty("startTicksUtc", out var t) ? t.ToString() : "";
            epoch = pid + ":" + ticks;
        }

        var prefix = kind == TaskCenterResourceKind.ConfigGroup ? "group:" : "onedragon:";
        var configEntry = new TaskCenterResourceEntry
        {
            Kind = kind,
            StableId = prefix + name,
            DisplayName = name,
            ConfigRevision = revision,
            BgiEpoch = epoch,
        };

        var tasks = new List<TaskCenterResourceEntry>();
        if (kind == TaskCenterResourceKind.OneDragonConfig
            && root.TryGetProperty("tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var taskEl in tasksEl.EnumerateArray())
            {
                var taskId = taskEl.TryGetProperty("taskId", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(taskId)) continue;
                tasks.Add(new TaskCenterResourceEntry
                {
                    Kind = TaskCenterResourceKind.SingleTask,
                    StableId = $"single:{name}:{taskId}",
                    DisplayName = taskEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? taskId : taskId,
                    OwnerConfig = name,
                    ConfigRevision = revision,
                    Enabled = taskEl.TryGetProperty("enabled", out var enEl) &&
                              (enEl.ValueKind == JsonValueKind.True || enEl.ValueKind == JsonValueKind.False)
                        ? enEl.GetBoolean() : null,
                    SingleExecutionSupported = taskEl.TryGetProperty("singleExecutionSupported", out var supEl) &&
                                               supEl.ValueKind == JsonValueKind.True,
                    BgiEpoch = epoch,
                });
            }
        }
        return (configEntry, tasks);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        var list = new List<string>();
        if (root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    list.Add(s);
        }
        return list;
    }
}

/// <summary>
/// 槲寄生 · 任务中心——资源目录服务（R4.3，R4 分解 D5/D14）。
/// 实时拉取执行端资源（ext.config.list + ext.config.describe，capability 门控），
/// 本机缓存仅供 BGI 不可达时展示降级（留痕）；缓存绝不作为执行依据——
/// 执行期解析必须重新实时 describe 并验证能力/epoch/revision（R4.4 Planner 职责）。
/// </summary>
public sealed class ResourceCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<IResourceCatalogTransport?> _transportAccessor;
    private readonly string _cacheFile;

    public ResourceCatalogService(Func<IResourceCatalogTransport?> transportAccessor, string cacheFile)
    {
        _transportAccessor = transportAccessor;
        _cacheFile = cacheFile;
    }

    /// <summary>默认缓存路径（%APPDATA%/NexusBGI/resource-catalog-cache.json）。</summary>
    public static string DefaultCacheFile()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusBGI", "resource-catalog-cache.json");

    /// <summary>当前展示快照（实时或缓存降级；UI 绑定用）。</summary>
    public ResourceCatalogSnapshot Current { get; private set; } = new()
    {
        IsDegraded = true,
        DegradedReason = "尚未拉取",
        CapturedAt = DateTimeOffset.MinValue,
    };

    /// <summary>
    /// 拉取实时资源目录。BGI 不可达/无能力/拉取失败 → 缓存降级（留痕），不硬依赖服务器或 BGI。
    /// </summary>
    public async Task<ResourceCatalogSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        var transport = _transportAccessor();
        if (transport is null || !transport.IsReady)
            return await DegradeAsync("ext 通道未就绪（BGI 离线或未连接）", ct);
        // DAP 能力门控：旧 BGI 无 config.revision → 响亮降级，不降级重发（R2 矩阵）
        if (!transport.HasCapability(BgiExternalCatalogTransport.CapabilityConfigRevision))
            return await DegradeAsync("BGI 缺少 config.revision 能力（旧版本），资源目录不可用（不降级重发）", ct);

        string? listData;
        try
        {
            listData = await transport.SendAsync(BgiExternalCatalogTransport.OpConfigList, new { }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await DegradeAsync("ext.config.list 失败：" + ex.GetType().Name, ct);
        }
        if (listData is null)
            return await DegradeAsync("ext.config.list 无响应或失败", ct);

        IReadOnlyList<string> groups, oneDragons;
        try
        {
            (groups, oneDragons) = ResourceCatalogParser.ParseList(listData);
        }
        catch (JsonException ex)
        {
            return await DegradeAsync("ext.config.list 响应形状异常：" + ex.GetType().Name, ct);
        }

        var entries = new List<TaskCenterResourceEntry>();
        string? epoch = null;
        var describeFailures = new List<string>();
        foreach (var (kind, name) in oneDragons.Select(n => (TaskCenterResourceKind.OneDragonConfig, n))
                     .Concat(groups.Select(n => (TaskCenterResourceKind.ConfigGroup, n))))
        {
            ct.ThrowIfCancellationRequested();
            object payload = kind == TaskCenterResourceKind.OneDragonConfig
                ? new { configName = name } : new { groupName = name };
            var data = await transport.SendAsync(BgiExternalCatalogTransport.OpConfigDescribe, payload, ct)
                .ConfigureAwait(false);
            if (data is null)
            {
                describeFailures.Add(name);
                continue; // 单配置失败不拖垮整目录（留痕）
            }
            try
            {
                var (configEntry, tasks) = ResourceCatalogParser.ParseDescribe(data, kind, name);
                entries.Add(configEntry);
                entries.AddRange(tasks);
                epoch ??= configEntry.BgiEpoch;
            }
            catch (JsonException)
            {
                describeFailures.Add(name);
            }
        }

        var snapshot = new ResourceCatalogSnapshot
        {
            Entries = entries,
            CapturedAt = DateTimeOffset.Now,
            IsDegraded = false,
            BgiEpoch = epoch,
            DegradedReason = describeFailures.Count > 0
                ? "部分配置 describe 失败（已跳过）：" + string.Join("、", describeFailures)
                : null,
        };
        Current = snapshot;
        PersistCache(snapshot);
        return snapshot;
    }

    /// <summary>缓存降级：读取上次成功快照，标记 IsFromCache；无缓存则空目录（留痕）。</summary>
    private Task<ResourceCatalogSnapshot> DegradeAsync(string reason, CancellationToken ct)
    {
        ResourceCatalogSnapshot snapshot;
        try
        {
            if (File.Exists(_cacheFile))
            {
                var cached = JsonSerializer.Deserialize<ResourceCatalogSnapshot>(
                    File.ReadAllText(_cacheFile, Encoding.UTF8), JsonOptions);
                if (cached is not null)
                {
                    foreach (var e in cached.Entries) e.IsFromCache = true;
                    snapshot = cached;
                    snapshot.IsDegraded = true;
                    snapshot.DegradedReason = reason + "（展示为上次缓存，不可作为执行依据）";
                    Current = snapshot;
                    return Task.FromResult(snapshot);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            reason += "；缓存文件损坏（" + ex.GetType().Name + "），按空目录处理";
        }
        snapshot = new ResourceCatalogSnapshot
        {
            IsDegraded = true,
            DegradedReason = reason,
            CapturedAt = DateTimeOffset.Now,
        };
        Current = snapshot;
        return Task.FromResult(snapshot);
    }

    private void PersistCache(ResourceCatalogSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions));
            var tmp = _cacheFile + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, _cacheFile, overwrite: true); }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        catch (IOException)
        {
            // 缓存写入失败不影响实时结果（缓存只是降级展示用）
        }
    }
}
