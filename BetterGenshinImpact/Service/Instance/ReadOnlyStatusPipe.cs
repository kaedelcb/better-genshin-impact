using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System;

using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.Instance;

/// <summary>每个 BGI 进程独立的只读状态管道。生命周期由 InstanceService 管理；不提供控制操作。</summary>
internal sealed class ReadOnlyStatusPipe : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<object> _snapshot;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    internal ReadOnlyStatusPipe(string pipeName, Func<object> snapshot)
    {
        _pipeName = pipeName;
        _snapshot = snapshot;
    }

    internal void Start() => _loop = AcceptLoopAsync(_stop.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServer();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var expected = Encoding.UTF8.GetBytes("GET_STATUS\n");
                var buffer = new byte[expected.Length];
                await pipe.ReadExactlyAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (!buffer.AsSpan().SequenceEqual(expected)) continue;
                var json = JsonSerializer.Serialize(_snapshot());
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                await pipe.WriteAsync(bytes.AsMemory(), deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("只读状态管道 {0}: {1}", _pipeName, ex.Message);
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }


    private NamedPipeServerStream CreateServer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("无法取得当前用户 SID");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _stop.Dispose();
    }
}
