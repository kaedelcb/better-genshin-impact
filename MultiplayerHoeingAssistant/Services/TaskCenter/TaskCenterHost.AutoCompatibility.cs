using System.IO;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private string? _lastLegacyCompatibilityStamp;
    private string? _legacyCompatibilityFailedRoot;
    private LegacyCompatibilityResult? _legacyCompatibilityFailure;
    private long _legacyCompatibilityRetryAfter;
    private string? _lastLegacyCompatibilityError;
    private static readonly TimeSpan LegacyCompatibilityRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>接续失败时保留原因；冷却不等于已经成功读取旧计划。</summary>
    public LegacyCompatibilityResult? LegacyCompatibilityFailure
    {
        get { lock (_gate) return _legacyCompatibilityFailure; }
    }

    /// <summary>Read old User and initialize ready-to-edit plans; never creates or resumes a run.</summary>
    public LegacyCompatibilityResult EnsureLegacyCompatibility(string? userRoot)
    {
        if (string.IsNullOrWhiteSpace(userRoot)) return new(0, 0, []);
        LegacyCompatibilityResult result;
        var reportResult = true;
        var recovered = false;
        lock (_gate)
        {
            if (_shutdown) return new(0, 0, ["任务中心正在关闭，未读取旧计划"]);
            if (_legacyCompatibilityFailure is { } failure
                && string.Equals(_legacyCompatibilityFailedRoot, userRoot, StringComparison.OrdinalIgnoreCase)
                && System.Diagnostics.Stopwatch.GetTimestamp() < _legacyCompatibilityRetryAfter)
                return failure;
            try
            {
                var directory = Path.Combine(userRoot, "OneDragon");
                var paths = Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                    : [];
                var schedule = Path.Combine(userRoot, "config.json");
                if (File.Exists(schedule)) paths.Add(schedule);
                var stamp = userRoot + "\n" + string.Join("\n", paths.Select(p => p + ":" + WorkflowStore.HashFileBytes(p)));
                if (_lastLegacyCompatibilityStamp == stamp && _legacyCompatibilityFailure is null) return new(0, 0, []);
                var indexRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_runsDirPath))!, "legacy-compatibility");
                result = LegacyAutoCompatibilityService.Synchronize(userRoot, indexRoot, _workflows,
                    id => !_drives.ContainsKey(id) && !_reservedWorkflows.Contains(id) && !ListActiveRuns().Any(r => r.WorkflowId == id));
                _lastLegacyCompatibilityStamp = stamp;
                recovered = _legacyCompatibilityFailure is not null;
                _legacyCompatibilityFailure = null;
                _legacyCompatibilityFailedRoot = null;
                _lastLegacyCompatibilityError = null;
            }
            catch (Exception ex)
            {
                var message = "旧计划暂时无法接续：" + ex.Message;
                result = new(0, 0, [message]);
                reportResult = message != _lastLegacyCompatibilityError
                    || !string.Equals(_legacyCompatibilityFailedRoot, userRoot, StringComparison.OrdinalIgnoreCase);
                _legacyCompatibilityFailure = result;
                _legacyCompatibilityFailedRoot = userRoot;
                _lastLegacyCompatibilityError = message;
                _legacyCompatibilityRetryAfter = System.Diagnostics.Stopwatch.GetTimestamp()
                    + (long)(LegacyCompatibilityRetryDelay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            }
        }
        // UI 日志回调可能同步等待 Dispatcher，不能占用调度锁。
        if (recovered) _log?.Invoke("[旧配置] 旧计划接续已恢复。");
        if (result.Added + result.Refreshed > 0)
            _log?.Invoke($"[任务中心] 已接续 {result.Added} 个旧计划，更新 {result.Refreshed} 个未修改计划；原配置保留，未启动任务。");
        if (reportResult)
            foreach (var notice in result.Notices) _log?.Invoke("[旧配置] " + notice);
        return result;
    }
}
