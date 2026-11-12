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

    internal const string PreparingMessage = "BGI 正在更新，等待更新结束后恢复连接（未启动 BGI，未发送任务）";

    internal static bool IsPreparing(string bgiDir) => Read(bgiDir) is { Phase: Preparing } state
        && state.ExpiresUtc > DateTimeOffset.UtcNow;

    internal static bool IsPreparingExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try { return Path.GetDirectoryName(Path.GetFullPath(executable)) is { } directory && IsPreparing(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 无效启动路径仍由原启动入口报告，更新检查不向其外冒泡。
            return false;
        }
    }

    private static void Write(string bgiDir, BgiUpdateCoordinationState state)
    {
        var path = PathFor(bgiDir);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, overwrite: true);
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

        var session = new BgiUpdateCoordinationSession(bgiDir, Guid.NewGuid().ToString("N"));
        Write(bgiDir, new(session.Id, Preparing, DateTimeOffset.UtcNow + PrepareLifetime, null, false));
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
                        try { process.Kill(entireProcessTree: true); } catch { }
                        process.Dispose();
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

/// <summary>一次协作更新。必须以 Complete 或 Fail 结束；两者只生效一次。</summary>
internal sealed class BgiUpdateCoordinationSession(string bgiDir, string id)
{
    private int _finished;
    public string Id { get; } = id;

    /// <summary>更新成功：各助手重新启动原来在运行的 BGI，并在 delay 后重启自己。</summary>
    public void Complete(TimeSpan restartDelay) => Finish(BgiUpdateCoordination.Done, DateTimeOffset.UtcNow + restartDelay, true);

    /// <summary>更新未完成：各助手恢复原来在运行的 BGI，不重启。</summary>
    public void Fail() => Finish(BgiUpdateCoordination.Failed, null, false);

    private void Finish(string phase, DateTimeOffset? restartAfter, bool restartAssistants)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;
        try
        {
            var path = BgiUpdateCoordination.PathFor(bgiDir);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new BgiUpdateCoordinationState(
                Id, phase, DateTimeOffset.UtcNow + BgiUpdateCoordination.PrepareLifetime, restartAfter, restartAssistants)));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写不进去时各助手会在准备期满后自动恢复 BGI。
            Debug.WriteLine("写入更新协作状态失败：" + ex.Message);
        }
    }
}
