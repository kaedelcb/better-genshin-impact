using System.IO;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private string? _lastLegacyCompatibilityStamp;

    /// <summary>Read old User and initialize ready-to-edit plans; never creates or resumes a run.</summary>
    public LegacyCompatibilityResult EnsureLegacyCompatibility(string? userRoot)
    {
        if (string.IsNullOrWhiteSpace(userRoot)) return new(0, 0, []);
        lock (_gate)
        {
            if (_shutdown) return new(0, 0, ["任务中心正在关闭，未读取旧计划"]);
            try
            {
                var directory = Path.Combine(userRoot, "OneDragon");
                if (!Directory.Exists(directory)) return new(0, 0, []);
                var paths = Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                var schedule = Path.Combine(userRoot, "config.json");
                if (File.Exists(schedule)) paths.Add(schedule);
                var stamp = userRoot + "\n" + string.Join("\n", paths.Select(p => p + ":" + WorkflowStore.HashFileBytes(p)));
                if (_lastLegacyCompatibilityStamp == stamp) return new(0, 0, []);
                var indexRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_runsDirPath))!, "legacy-compatibility");
                var result = LegacyAutoCompatibilityService.Synchronize(userRoot, indexRoot, _workflows,
                    id => !_drives.ContainsKey(id) && !_reservedWorkflows.Contains(id) && !ListActiveRuns().Any(r => r.WorkflowId == id));
                _lastLegacyCompatibilityStamp = stamp;
                if (result.Added + result.Refreshed > 0)
                    _log?.Invoke($"[任务中心] 已接续 {result.Added} 个旧计划，更新 {result.Refreshed} 个未修改计划；原配置保留，未启动任务。");
                foreach (var notice in result.Notices) _log?.Invoke("[旧配置] " + notice);
                return result;
            }
            catch (Exception ex)
            {
                var message = "旧计划暂时无法接续：" + ex.Message;
                _log?.Invoke("[任务中心] " + message);
                return new(0, 0, [message]);
            }
        }
    }
}
