namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// [P1b] AllReady 批次句柄：收编原 OnAllReadyConfirmedInternal 的 fire-and-forget Task.Run 批次，
/// 使其"有主可寻"——记录 generation、取消令牌（并入原共享 bool _isAllReadySequenceCancelled 的语义：
/// 外部"用户手动停止"与批次内 F11 取消统一走 CTS）、后台任务引用与存活状态。
/// 新一轮 AllReady 到达时 MainViewModel 先取消旧批次（Cancel + 短超时等退出）再启动新批次。
/// CTS 不 Dispose：批次任务退出路径仍可能读令牌，Dispose 后访问会抛 ObjectDisposedException，批次量少直接放弃回收。
/// </summary>
public sealed class OnlineHoeingBatch
{
    public OnlineHoeingBatch(int generation)
    {
        Generation = generation;
    }

    /// <summary>本轮 AllReady 的代序号（服务端广播值，兼做策略收尾恰好一次守卫的批次键）。</summary>
    public int Generation { get; }

    /// <summary>批次取消令牌源（原 _isAllReadySequenceCancelled 的承载者）。</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>批次后台任务（Task.Run 发起后由 MainViewModel 同步回填，回填前视为未启动）。</summary>
    public Task? RunTask { get; set; }

    /// <summary>批次是否仍存活（后台任务已发起且未完结）。</summary>
    public bool IsAlive => RunTask is { IsCompleted: false };

    /// <summary>批次是否已被请求取消。</summary>
    public bool IsCancellationRequested => Cts.IsCancellationRequested;

    /// <summary>请求取消批次（幂等；批次循环在下一次迭代检查点退出）。</summary>
    public void Cancel()
    {
        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 防御：理论上不 Dispose 不会命中，保留兜底
        }
    }
}
