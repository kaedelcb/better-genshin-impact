using System.IO.Pipes;

namespace MultiplayerHoeingAssistant.Services;

internal static class BgiSessionPipe
{
    internal static string Name(string root, int sessionId) => root + ".session-" + sessionId;

    internal static async Task<NamedPipeClientStream> ConnectAsync(string root, bool preferSession,
        int timeoutMs, CancellationToken ct)
    {
        if (preferSession)
        {
            var session = new NamedPipeClientStream(".", Name(root, System.Diagnostics.Process.GetCurrentProcess().SessionId),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await session.ConnectAsync(Math.Min(300, timeoutMs), ct).ConfigureAwait(false);
                return session;
            }
            catch (TimeoutException) { session.Dispose(); }
            catch { session.Dispose(); throw; }
        }
        // 兼容尚未提供会话端点的旧 BGI；现有握手仍核对会话、进程和启动时间。
        var legacy = new NamedPipeClientStream(".", root, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await legacy.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return legacy;
        }
        catch { legacy.Dispose(); throw; }
    }
}
