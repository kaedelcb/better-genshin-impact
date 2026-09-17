// These replace ONLY external boundaries. Assertions exercise linked production
// framing/session/command plane/query plane/queue/registry/gate, not these handlers.
// No disk configuration, real pipes, UI, input, process control or network APIs.
using BetterGenshinImpact.Service.Instance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace MultiplayerHoeingAssistant.Helpers
{
    internal static class RuntimeLog { public static void WriteLine(string text) { } }
}

namespace BetterGenshinImpact
{
    public static class App
    {
        public static IServiceProvider ServiceProvider { get; } = new ServiceCollection()
            .AddSingleton<Service.Interface.IScriptService, Service.Interface.InertScriptService>().BuildServiceProvider();
        public static T? GetService<T>() => ServiceProvider.GetService<T>();
        public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
    }
}
namespace BetterGenshinImpact.Core.Config
{
    public static class Global { public const string Version = "isolated-contract-audit"; }
}
namespace BetterGenshinImpact.GameTask.Common
{
    public static class TaskControl { public static SemaphoreSlim TaskSemaphore { get; } = new(1, 1); }
}
namespace BetterGenshinImpact.Service.Execution
{
    // Ownership behavior is covered separately by IpcIncidentAudit's real ExecutionScope.
    public static class ExecutionScope { public static bool HasActive => false; }
}
namespace BetterGenshinImpact.Service.Interface
{
    public interface IScriptService;
    public sealed class InertScriptService : IScriptService;
}
namespace BetterGenshinImpact.Service.Instance
{
    public sealed class InstanceConnection
    {
        public Task WriteJsonAsync(InstanceIpcEnvelope envelope, CancellationToken token)
            => throw new InvalidOperationException("Live event transport is forbidden in this audit");
    }
}
namespace BetterGenshinImpact.Service.ExternalInterface
{
    public sealed class ExternalInterfaceEventHub
    {
        public static ExternalInterfaceEventHub Instance { get; } = new();
        public long CurrentRevision => 0;
        public void Publish(string name, object? payload) { }
        public void PublishJobTransition(Execution.BgiJob job) { }
        public void PublishJobHeartbeat(Execution.BgiJob job) { }
        public (IReadOnlyList<JObject>, bool) GetReplayEvents(long revision, Func<string, bool> filter) => ([], true);
        public void Subscribe(Guid id, InstanceConnection connection, List<string> events) { }
        public void Unsubscribe(Guid id, List<string> events) { }
    }
}
namespace BetterGenshinImpact.Service.Instance.MessageHandlers
{
    public sealed class InstanceRequestHandler
    {
        public Func<InstanceIpcEnvelope, Task<InstanceIpcEnvelope>> Write { get; set; }
            = r => Task.FromResult(InstanceIpcEnvelope.Response(r, new { status = "inert_write" }));
        public int Executions;
        public static InstanceIpcEnvelope? CheckManualStopCooldown(InstanceIpcEnvelope r, string source) => null;
        public static string[]? ParseBatchGroupNames(string? json) => null;
        public Task<bool> ExecuteTaskStartCoreAsync(Interface.IScriptService service, string? group, string? config,
            int index, string[]? batch, int generation, Guid handle, bool preempt, string? ticket, CancellationToken token,
            Guid? workflowRunId = null, Execution.JobExecutionIdentity? executionIdentity = null, InstanceIpcEnvelope? executionRequest = null)
        { Interlocked.Increment(ref Executions); return Task.FromResult(false); }
        public Task<InstanceIpcEnvelope> HandleTaskStart(InstanceConnection c, InstanceIpcEnvelope r)
            => throw new InvalidOperationException("Fallback to product handler is forbidden");
        public Task<InstanceIpcEnvelope> HandleSetTaskEnabled(InstanceConnection c, InstanceIpcEnvelope r) => Write(r);
        public Task<InstanceIpcEnvelope> HandleTaskSuspend(InstanceConnection c, InstanceIpcEnvelope r) => Write(r);
        public Task<InstanceIpcEnvelope> HandleTaskResume(InstanceConnection c, InstanceIpcEnvelope r) => Write(r);
        public Task<InstanceIpcEnvelope> HandleConfigApplyGroup(InstanceConnection c, InstanceIpcEnvelope r) => Write(r);
        public Task<InstanceIpcEnvelope> HandleExecuteHotkey(InstanceConnection c, InstanceIpcEnvelope r) => Write(r);
        public InstanceIpcEnvelope HandleTaskStop(InstanceConnection c, InstanceIpcEnvelope r)
            => InstanceIpcEnvelope.Response(r, new { status = "inert_stop" });
        public InstanceIpcEnvelope HandleCloseGame(InstanceConnection c, InstanceIpcEnvelope r)
            => throw new InvalidOperationException("Game operations are forbidden");
        public InstanceIpcEnvelope HandleConfigPullGroup(InstanceConnection c, InstanceIpcEnvelope r)
            => throw new InvalidOperationException("Configuration file access is forbidden");
        public InstanceIpcEnvelope HandleConfigOpenRemoteEditor(InstanceConnection c, InstanceIpcEnvelope r)
            => throw new InvalidOperationException("UI is forbidden");
        public InstanceIpcEnvelope HandleConfigRemoteEditorResult(InstanceConnection c, InstanceIpcEnvelope r)
            => throw new InvalidOperationException("UI is forbidden");
        public InstanceIpcEnvelope HandleTaskStatus(InstanceConnection c, InstanceIpcEnvelope r)
            => InstanceIpcEnvelope.Response(r, new { running = false });
        public InstanceIpcEnvelope HandleConfigList(InstanceConnection c, InstanceIpcEnvelope r)
            => throw new InvalidOperationException("Configuration file access is forbidden");
    }
}
