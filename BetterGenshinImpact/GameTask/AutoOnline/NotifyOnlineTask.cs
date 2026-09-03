using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoOnline;

/// <summary>
/// "联机锄地上线"独立任务。执行时通知助手标记上线，然后立即返回（不阻塞）。
/// 助手通过 IPC task.status 的 onlineGeneration 字段检测到新代序号后，通过 SignalR 上报服务端。
/// 该任务本身不做任何网络操作，纯粹轻量状态标记。
/// 设计符合"助手做决策，BGI 做执行"的架构原则（bgi-implementation-patterns.md §31）。
/// generation 单调递增，作为每次上线事件的唯一标识，用于边沿检测与幂等。
/// generation 持久化到 User/online_generation.txt，保证 BGI 重启后不复位（止血 P0-B）：
/// 服务端 RoomManager 会丢弃 generation ≤ 历史值的事件，BGI 重启后若从 1 重新开始会被永久丢弃。
/// </summary>
public class NotifyOnlineTask : ISoloTask
{
    public string Name => "联机锄地上线";

    private static int _nextGeneration = LoadPersistedGeneration();
    private static readonly object _genLock = new();

    private static string GenerationFilePath => Global.Absolute("User/online_generation.txt");

    /// <summary>当前上线事件的代序号（单调递增）。0 = 从未触发。</summary>
    public static int CurrentGeneration { get; private set; }

    /// <summary>最近一次执行的触发时间戳（UTC）。MinValue = 从未触发。</summary>
    public static DateTime LastTriggeredAt { get; private set; } = DateTime.MinValue;

    public async Task Start(CancellationToken ct)
    {
        // 自增 generation，作为本次上线事件的唯一标识
        lock (_genLock)
        {
            CurrentGeneration = _nextGeneration++;
        }
        LastTriggeredAt = DateTime.UtcNow;
        // 自增后立即写盘，保证跨 BGI 重启单调递增。写盘失败仅记日志，不影响主流程。
        PersistGeneration(_nextGeneration);
        // 立即返回，不阻塞任务流。
        await Task.CompletedTask;
    }

    /// <summary>从持久化文件读取下一个 generation。文件不存在/解析失败则从 1 开始（单机用户无感知）。</summary>
    private static int LoadPersistedGeneration()
    {
        try
        {
            var path = GenerationFilePath;
            if (File.Exists(path)
                && int.TryParse(File.ReadAllText(path).Trim(), out var saved)
                && saved >= 1)
            {
                return saved;
            }
        }
        catch (Exception ex)
        {
            TaskControl.Logger.LogWarning(ex, "[联机锄地上线] 读取 online_generation.txt 失败，generation 从 1 开始");
        }
        return 1;
    }

    /// <summary>自增后写盘。失败仅记日志，不影响本次上线事件。</summary>
    private static void PersistGeneration(int nextGeneration)
    {
        try
        {
            File.WriteAllText(GenerationFilePath, nextGeneration.ToString());
        }
        catch (Exception ex)
        {
            TaskControl.Logger.LogWarning(ex, "[联机锄地上线] 写入 online_generation.txt 失败，本次 generation 未持久化");
        }
    }
}