using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>在尚未提交动作的来源检查处等待恢复，不重跑已经完成的启动节点。</summary>
internal static class StartupSourceRecovery
{
    internal static async Task<WorkflowStopAuthorityRecord?> RefreshAsync(StartupSourceIntent source,
        Func<CancellationToken, Task<WorkflowStopAuthorityRecord?>> refresh, CancellationToken ct,
        Action<string>? report = null, TimeSpan? retryDelay = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, source.LifetimeToken);
        var token = lifetime.Token;
        var waiting = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await refresh(token);
                token.ThrowIfCancellationRequested();
                if (waiting) report?.Invoke("连接已恢复，继续当前启动步骤");
                return result;
            }
            catch (StartupSourceStoppedException)
            {
                source.StopCurrentRun();
                report?.Invoke("当前运行已停止；保留自动启动设置，不重启本次任务");
                throw;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is
                (StartupSourceUnavailableException or IOException or TimeoutException or OperationCanceledException
                    or ObjectDisposedException
                    or BgiNotSentException { EvidenceCode: BgiNotSentException.ChannelNotReady or BgiNotSentException.PipeNotConnected }))
            {
                if (!waiting) report?.Invoke("等待自动恢复：" + ex.Message);
                waiting = true;
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(5), token);
            }
        }
    }
}
