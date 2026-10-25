using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using BetterGenshinImpact.Core.Monitor;
using BetterGenshinImpact.Service.Instance;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public sealed class SessionControlPipeTests
{
    [Fact]
    public async Task ChildSession_ServesItsOwnControlHandshakeWhileRootIsUnavailable()
    {
        // 隔离命名空间，不连接真实 BGI；仍使用正式 InstanceService/协议/端点校验。
        var context = new InstanceContext(BetterGiInstanceType.ChildSession,
            "bgi-session-test-" + Guid.NewGuid().ToString("N"), -1);
        using var bootstrap = (InstanceBootstrap)Activator.CreateInstance(typeof(InstanceBootstrap),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [context, null, null], null)!;
        await using var service = new InstanceService(bootstrap, new EmptyServices(),
            new RawInputMonitor(NullLogger<RawInputMonitor>.Instance), null!, NullLogger<InstanceService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await service.StartAsync(deadline.Token);
        using var client = new NamedPipeClientStream(".", context.SessionControlPipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, deadline.Token);
        foreach (var operation in new[] { "ping", "ext.hello" })
        {
            var request = InstanceIpcEnvelope.Request(operation, null);
            await InstanceIpcProtocol.WriteJsonAsync(client, request, deadline.Token);
            var frame = await InstanceIpcProtocol.ReadFrameAsync(client, deadline.Token);
            Assert.NotNull(frame);
            var response = InstanceIpcProtocol.ReadJson(frame!.Value);
            Assert.True(response.Success, response.ErrorMessage);
            Assert.Equal(Environment.ProcessId, response.Data!["processId"]!.Value<int>());
            Assert.Equal(Process.GetCurrentProcess().SessionId, response.Data["windowsSessionId"]!.Value<int>());
        }
        await service.StopAsync(deadline.Token);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
