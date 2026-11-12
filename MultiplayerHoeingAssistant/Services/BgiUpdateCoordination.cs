using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>同机更新协作状态：写在 BGI 目录下，本机所有使用该目录的助手（执行端、监控端、任何 Windows 会话）都会读取。</summary>
internal sealed record BgiUpdateCoordinationState(
    string Id,
    string Phase,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? RestartAfterUtc,
    bool RestartAssistants);

/// <summary>
/// 更新 BGI 的同机协作：发起方写入“准备更新”，各助手关闭自己会话的 BGI 并暂停守护；
/// 发起方确认所有 BGI 退出后覆盖安装，再写入“完成”或“失败”。
/// 完成：各助手重新启动原来在运行的 BGI，并在约定时间后重启自己；失败：只恢复 BGI，不重启助手。
/// 不需要跨账户结束进程的权限，每个助手只处理自己会话的 BGI。
/// </summary>
internal static class BgiUpdateCoordination
{
    public const string FileName = "_update_coordination.json";
    public const string Preparing = "preparing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Blocked = "blocked";

    /// <summary>准备阶段的最长有效期：发起方异常退出时，各助手到期后自动恢复。</summary>
    public static readonly TimeSpan PrepareLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(45);

    public static string PathFor(string bgiDir) => Path.Combine(bgiDir, FileName);

    public static BgiUpdateCoordinationState? Read(string bgiDir)
    {
        try
        {
            var path = PathFor(bgiDir);
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4096) return null;
            var state = JsonSerializer.Deserialize<BgiUpdateCoordinationState>(stream);
            return state is { Id.Length: > 0, Phase.Length: > 0 } ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal const string PreparingMessage = "BGI 正在更新或等待启动文件修复，暂停自动启动（未启动 BGI，未发送任务）";

    internal static bool IsLaunchBlocked(string bgiDir) => Read(bgiDir) is { } state
        && (state.Phase == Blocked || (state.Phase == Preparing && state.ExpiresUtc > DateTimeOffset.UtcNow));

    internal static bool IsPreparing(string bgiDir) => Read(bgiDir) is { Phase: Preparing } state
        && state.ExpiresUtc > DateTimeOffset.UtcNow;

    internal static bool IsLaunchBlockedExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try { return Path.GetDirectoryName(Path.GetFullPath(executable)) is { } directory && IsLaunchBlocked(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 无效启动路径仍由原启动入口报告，更新检查不向其外冒泡。
            return false;
        }
    }

    internal static void Write(string bgiDir, BgiUpdateCoordinationState state)
    {
        var path = PathFor(bgiDir);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, overwrite: true);
    }

    private static readonly object RestoreGate = new();

    internal static bool RestoreBgiIfNeeded(string executable, Func<bool> launch, Action? alreadyRunning = null)
        => RestoreBgiIfNeededCore(() => ProbeCurrentSessionExecutable(executable), launch, alreadyRunning);

    internal static bool RestoreBgiIfNeededCore(Func<bool?> probe, Func<bool> launch, Action? alreadyRunning = null)
    {
        lock (RestoreGate)
        {
            // 完成通知后的延时内，其他启动入口可能已恢复 BGI；未知身份也不能据此再拉一个实例。
            var running = probe();
            if (running == true) { alreadyRunning?.Invoke(); return true; }
            return running == false && launch();
        }
    }

    private static bool? ProbeCurrentSessionExecutable(string executable)
    {
        if (!BgiProcessMonitor.TryGetCurrentSessionBgiProcessesStrict(out var processes, out _)) return null;
        var uncertain = false;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path)) { uncertain = true; continue; }
                    if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    try { if (process.HasExited) continue; } catch { }
                    uncertain = true;
                }
            }
            return uncertain ? null : false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static bool AnyBgiRunning(string bgiDir)
    {
        var processes = BgiPackageUpdateService.FindBgiProcessesUnder(bgiDir);
        try { return processes.Count > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    /// <summary>通知本机所有助手关闭 BGI，并等待全部退出。失败时已写入“失败”让各助手恢复，然后抛出原因。</summary>
    public static async Task<BgiUpdateCoordinationSession> BeginAsync(string bgiDir, bool closeBgi, Action<string>? report)
    {
        if (!closeBgi && AnyBgiRunning(bgiDir))
            throw new IOException("BGI 尚未退出。请先退出 BGI，或勾选“更新前自动关闭 BGI”。");

        var previous = Read(bgiDir);
        var wasBlocked = previous?.Phase == Blocked
            || previous is { Phase: Preparing, ExpiresUtc: var expiry } && expiry == DateTimeOffset.MaxValue;
        var session = new BgiUpdateCoordinationSession(bgiDir, Guid.NewGuid().ToString("N"), wasBlocked);
        Write(bgiDir, new(session.Id, Preparing,
            wasBlocked ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + PrepareLifetime, null, false));
        report?.Invoke("正在通知本机所有助手关闭 BGI…");

        var deadline = DateTime.UtcNow + CloseTimeout;
        while (AnyBgiRunning(bgiDir))
        {
            if (DateTime.UtcNow >= deadline)
            {
                // 兜底：旧版助手不会响应协作，尝试直接结束（其他账户的进程可能没有权限）。
                if (closeBgi)
                {
                    foreach (var process in BgiPackageUpdateService.FindBgiProcessesUnder(bgiDir))
                    {
                        try
                        {
                            if (BgiProcessMonitor.IsConfirmedProcessUnderDirectory(process, bgiDir))
                                process.Kill(entireProcessTree: true);
                        }
                        catch { }
                        finally { process.Dispose(); }
                    }
                    await Task.Delay(3000);
                }
                if (AnyBgiRunning(bgiDir))
                {
                    session.Fail();
                    throw new IOException("仍有 BGI 未退出（可能是未升级的旧版助手所在账户，或没有权限关闭）。已恢复原来运行的 BGI，未更新。");
                }
                break;
            }
            await Task.Delay(500);
        }
        report?.Invoke("本机所有 BGI 已退出，开始更新…");
        return session;
    }
}

/// <summary>一次协作更新。以 Complete、Fail 或 BlockRestart 结束，终态只生效一次。</summary>
internal sealed class BgiUpdateCoordinationSession(string bgiDir, string id, bool wasBlocked = false)
{
    private readonly object _gate = new();
    private bool _finished;
    private bool _installationStarted;
    public string Id { get; } = id;
    public bool MustKeepRestartBlocked { get { lock (_gate) return wasBlocked || _installationStarted; } }

    /// <summary>在第一次截断产品文件前持久化保护。解压及电脑端等待确认期间不再因十分钟超时放行。</summary>
    public void BeginInstallation()
    {
        lock (_gate)
        {
            if (_finished) throw new InvalidOperationException("更新会话已经结束，未覆盖文件。");
            var state = BgiUpdateCoordination.Read(bgiDir);
            if (state?.Id != Id || state.Phase != BgiUpdateCoordination.Preparing || state.ExpiresUtc <= DateTimeOffset.UtcNow)
                throw new IOException("更新协作已经变化或过期，请重新发起更新（未覆盖文件）。");
            BgiUpdateCoordination.Write(bgiDir, new(Id, BgiUpdateCoordination.Preparing, DateTimeOffset.MaxValue, null, false));
            _installationStarted = true;
            if (BgiUpdateCoordination.AnyBgiRunning(bgiDir))
                throw new IOException("覆盖前仍发现 BGI 进程，已保持暂停，未覆盖文件。");
        }
    }

    /// <summary>电脑端完成提示存续期间保持保护；即使包内文件全部被排除，也不能因准备期到期提前恢复。</summary>
    public void HoldUntilConfirmation()
    {
        lock (_gate)
        {
            if (_finished) throw new InvalidOperationException("更新会话已经结束。");
            var state = BgiUpdateCoordination.Read(bgiDir);
            if (state?.Id != Id || state.Phase != BgiUpdateCoordination.Preparing || state.ExpiresUtc <= DateTimeOffset.UtcNow)
                throw new IOException("更新协作已经变化或过期，不能安排自动重启。");
            BgiUpdateCoordination.Write(bgiDir, new(Id, BgiUpdateCoordination.Preparing, DateTimeOffset.MaxValue, null, false));
        }
    }

    /// <summary>只在覆盖和检查完成、以及电脑端确认后调用；网页端无需本机确认。</summary>
    public void Complete(TimeSpan restartDelay)
    {
        lock (_gate)
        {
            if (_finished) return;
            if (wasBlocked && !_installationStarted)
                throw new IOException("先前更新尚未修复，不能只凭重新发起更新解除暂停。");
            if (wasBlocked) WindowsLauncherIntegrity.EnsureExistingLaunchersSafe(bgiDir);
            Finish(BgiUpdateCoordination.Done, DateTimeOffset.UtcNow + restartDelay, true);
        }
    }

    /// <summary>只有覆盖前的普通失败可恢复；已进入覆盖或承接旧故障时持续阻止启动。</summary>
    public void Fail()
    {
        lock (_gate)
            Finish(wasBlocked || _installationStarted ? BgiUpdateCoordination.Blocked : BgiUpdateCoordination.Failed, null, false);
    }

    public void BlockRestart()
    {
        lock (_gate) Finish(BgiUpdateCoordination.Blocked, null, false);
    }

    private void Finish(string phase, DateTimeOffset? restartAfter, bool restartAssistants)
    {
        if (_finished) return;
        // 必须先写成功才结束会话；若写失败，安装前已落盘的无限期 preparing 仍保护所有助手。
        BgiUpdateCoordination.Write(bgiDir, new(Id, phase,
            phase == BgiUpdateCoordination.Blocked ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + BgiUpdateCoordination.PrepareLifetime,
            restartAfter, restartAssistants));
        _finished = true;
    }
}
