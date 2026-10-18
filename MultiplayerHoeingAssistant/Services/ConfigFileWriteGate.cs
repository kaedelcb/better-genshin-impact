using System.IO;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>All cooperating writers share a physical directory sidecar, including cross-profile shared rules.
/// Contention fails immediately: never wait for this permit while holding a service's memory lock.</summary>
internal static class ConfigFileWriteGate
{
    internal const string LockFileName = ".nexus-config-write.lock";
    [ThreadStatic] private static Dictionary<string, Entry>? _held;
    private sealed class Entry(FileStream stream) { internal readonly FileStream Stream = stream; internal int Depth = 1; }

    internal static IDisposable Acquire(string target) => AcquireMany([target]);

    internal static IDisposable AcquireMany(IEnumerable<string> targets)
    {
        var directories = targets.Select(path => Path.GetDirectoryName(Path.GetFullPath(path))!)
            .Select(directory =>
            {
                ConfigTransferTransaction.CheckPath(directory);
                Directory.CreateDirectory(directory);
                return ConfigTransferFileIdentity.CanonicalDirectory(directory);
            }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var acquired = new List<string>();
        var held = _held ??= new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in directories)
            {
                if (held.TryGetValue(directory, out var entry)) entry.Depth++;
                else
                {
                    var path = Path.Combine(directory, LockFileName);
                    ConfigTransferTransaction.CheckPath(path);
                    try { held.Add(directory, new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))); }
                    catch (IOException ex) { throw new IOException("此目录的配置正在由其他操作保存或迁移，原文件未覆盖，请稍后重试：" + directory, ex); }
                }
                acquired.Add(directory);
            }
            return new Scope(acquired);
        }
        catch { Release(acquired); throw; }
    }

    private static void Release(List<string> directories)
    {
        foreach (var directory in directories.AsEnumerable().Reverse())
            if (--_held![directory].Depth == 0)
            {
                _held[directory].Stream.Dispose();
                _held.Remove(directory);
            }
    }
    private sealed class Scope(List<string> directories) : IDisposable
    {
        private List<string>? _directories = directories;
        public void Dispose()
        {
            if (_directories is not { } current) return;
            _directories = null;
            Release(current);
        }
    }
}
