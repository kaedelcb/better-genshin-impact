using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoOnline;

/// <summary>
/// "联机锄地上线"独立任务。执行时通知助手标记上线，然后立即返回（不阻塞）。
/// 助手通过 IPC task.status 的 onlineGeneration 字段检测到新代序号后，通过 SignalR 上报服务端。
/// 该任务本身不做任何网络操作，纯粹轻量状态标记。
/// 设计符合"助手做决策，BGI 做执行"的架构原则（bgi-implementation-patterns.md §31）。
/// generation 单调递增，作为每次上线事件的唯一标识，用于边沿检测与幂等。
/// </summary>
public class NotifyOnlineTask : ISoloTask
{
    public string Name => "联机锄地上线";

    private static int _nextGeneration = 1;
    private static readonly object _genLock = new();

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
        // 立即返回，不阻塞任务流。
        await Task.CompletedTask;
    }
}