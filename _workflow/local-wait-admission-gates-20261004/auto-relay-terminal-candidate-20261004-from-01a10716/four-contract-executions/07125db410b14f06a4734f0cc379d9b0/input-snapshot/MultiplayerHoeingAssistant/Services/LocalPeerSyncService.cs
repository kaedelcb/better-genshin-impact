using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 同机双端（执行端 + 监控端）本地对等同步服务：不走服务器，靠 exe 目录 shared/ 下的文件互相同步。
/// - 信标 executor_beacon.json：执行端（非监控模式）启动即写并每 30 秒刷新心跳（UTC），
///   监控端据此判断"本机是否有一个活着的同安装目录执行端"（freshness 阈值 90 秒，
///   且 installDir 与本机 exe 目录规范化后 OrdinalIgnoreCase 相等才算——跨安装目录的信标不认）；
/// - 共享规则 watch_rules.json：双端原子写（先写本进程独有的 .tmp 再覆盖移动，防对端读到半文件），
///   配合 KeywordWatchService 的 FileSystemWatcher 做规则互同步。
/// 运行期切模式由 observerModeProvider 动态生效：切成执行 → 当拍开始写信标；切成监控 → 当拍删掉自己写的信标。
/// 所有文件 IO 容错，失败仅 Debug.WriteLine，不抛。
/// </summary>
public sealed class LocalPeerSyncService : IDisposable
{
    /// <summary>执行端信标心跳刷新间隔。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    /// <summary>信标新鲜度阈值：心跳超过该时长未刷新视为执行端已不在。</summary>
    private static readonly TimeSpan FreshnessThreshold = TimeSpan.FromSeconds(90);

    private readonly Func<bool>? _observerModeProvider;
    private readonly Func<string?>? _uidProvider;
    /// <summary>本进程会话标识（每次启动新生成，供诊断区分信标来源进程）。</summary>
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly Timer _timer;
    private volatile bool _disposed;
    /// <summary>本进程当前是否写着自己的信标（切模式/Dispose 时决定要不要删）。</summary>
    private bool _beaconWritten;

    /// <summary>助手 exe 目录（与 LogFileBrowser.AssistantLogDir 同一取法）。</summary>
    public static string ExeDir => Path.GetDirectoryName(Environment.ProcessPath) ?? ".";

    /// <summary>同机共享目录（exe 目录 shared/）。</summary>
    public static string SharedDir => Path.Combine(ExeDir, "shared");

    /// <summary>执行端信标文件路径。</summary>
    public static string BeaconPath => Path.Combine(SharedDir, "executor_beacon.json");

    /// <summary>共享规则文件路径（双端同步的监控规则）。</summary>
    public static string SharedRulesPath => Path.Combine(SharedDir, "watch_rules.json");

    /// <param name="observerModeProvider">监控模式判定（true=监控端，不写信标）；可为 null=恒执行端。</param>
    /// <param name="uidProvider">玩家 uid 提供者（信标诊断字段）；可为 null。</param>
    public LocalPeerSyncService(Func<bool>? observerModeProvider = null, Func<string?>? uidProvider = null)
    {
        _observerModeProvider = observerModeProvider;
        _uidProvider = uidProvider;
        // 启动即按当前模式处理一次（执行端立刻有信标；监控端清掉本进程可能残留的信标），之后每 30 秒刷新
        RefreshBeacon();
        _timer = new Timer(_ => RefreshBeacon(), null, HeartbeatInterval, HeartbeatInterval);
    }

    private bool IsObserver() => _observerModeProvider?.Invoke() == true;

    /// <summary>本机是否有活着的同安装目录执行端：信标文件存在 + 心跳 fresh（90 秒内）
    /// + installDir 与本机 exe 目录一致（规范化后 OrdinalIgnoreCase）。读失败/过期/跨目录一律 false。</summary>
    public bool IsLocalExecutorAlive()
    {
        try
        {
            if (!File.Exists(BeaconPath)) return false;
            var beacon = JsonSerializer.Deserialize<ExecutorBeacon>(File.ReadAllText(BeaconPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (beacon == null) return false;
            if (beacon.HeartbeatUtc.Kind != DateTimeKind.Utc) return false;
            if (DateTime.UtcNow - beacon.HeartbeatUtc > FreshnessThreshold) return false;
            return string.Equals(NormalizeDir(beacon.InstallDir), NormalizeDir(ExeDir),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { /* 信标读取失败一律按无活执行端处理 */ }
        return false;
    }

    /// <summary>目录路径规范化：去掉尾部分隔符后比较（不同来源的路径写法差一个 '\' 不该判不同目录）。</summary>
    private static string NormalizeDir(string? dir) =>
        (dir ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>心跳刷新（构造时 + Timer 每拍）：执行端写/更新信标；监控端停写并删掉本进程之前写的信标。</summary>
    private void RefreshBeacon()
    {
        if (_disposed) return;
        try
        {
            if (IsObserver())
            {
                if (_beaconWritten)
                {
                    TryDeleteBeacon();
                    _beaconWritten = false;
                }
                return;
            }
            var beacon = new ExecutorBeacon
            {
                Uid = _uidProvider?.Invoke() ?? "",
                MachineName = Environment.MachineName,
                InstallDir = ExeDir,
                SessionId = _sessionId,
                HeartbeatUtc = DateTime.UtcNow
            };
            WriteFileAtomic(BeaconPath, JsonSerializer.Serialize(beacon));
            _beaconWritten = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalPeerSync] 信标刷新失败: {ex.Message}");
        }
    }

    /// <summary>尽力删信标（进程退出/切成监控时；删不掉不致命——freshness 过期兜底）。</summary>
    private static void TryDeleteBeacon()
    {
        try
        {
            if (File.Exists(BeaconPath)) File.Delete(BeaconPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalPeerSync] 信标删除失败: {ex.Message}");
        }
    }

    /// <summary>原子写文本文件（共享文件统一入口）：先写本进程独有的 .tmp 再覆盖移动，
    /// 对端 FileSystemWatcher/读取方不会看到写了一半的内容；双端同时写时各自 tmp 互不冲突（最后移动者胜出，下一拍心跳/保存收敛）。
    /// 返回是否写入成功（失败已在内部 Debug.WriteLine，调用方需要留痕时可据此补运行日志）。</summary>
    public static bool WriteFileAtomic(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalPeerSync] 原子写失败 {path}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        // 退出时尝试删信标（删不掉靠 90 秒 freshness 过期兜底）
        if (_beaconWritten) TryDeleteBeacon();
    }

    /// <summary>执行端信标结构（shared/executor_beacon.json）。</summary>
    private sealed class ExecutorBeacon
    {
        [JsonPropertyName("uid")] public string Uid { get; set; } = "";
        [JsonPropertyName("machineName")] public string MachineName { get; set; } = "";
        [JsonPropertyName("installDir")] public string InstallDir { get; set; } = "";
        [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
        [JsonPropertyName("heartbeatUtc")] public DateTime HeartbeatUtc { get; set; }
    }
}
