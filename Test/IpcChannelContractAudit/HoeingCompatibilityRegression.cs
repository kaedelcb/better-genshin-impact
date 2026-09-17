using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using MultiplayerHoeingAssistant.Services;

internal static class HoeingCompatibilityRegression
{
    private static void Must(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        foreach (var owned in new[] { false, true })
            await check(owned ? "T54 owned cancel bypasses global stop and confirms actual terminal"
                             : "T53 ordinary cancel retains existing handler and response", async () =>
            {
                var queue = BgiTaskCoordinator.Instance;
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken executionToken = default;
                var item = queue.Submit(new(0, Guid.NewGuid().ToString("N"), null, 0, async (_, ct) =>
                {
                    executionToken = ct;
                    started.SetResult();
                    await release.Task;
                    ct.ThrowIfCancellationRequested();
                    return false;
                }));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    var request = InstanceIpcEnvelope.Request("ext.task.cancel", new { taskHandle = item.TaskHandle.ToString("N") });
                    if (owned) request.Data!["ownedOnly"] = "v1";
                    var response = await ExternalInterfaceCommandPlane.DispatchAsync(new InstanceRequestHandler(), new InstanceConnection(), request, default);
                    Must(response.Data?["status"]?.ToString() == (owned ? "stop_requested" : "inert_stop"), "cancel response or handler changed");
                    Must(executionToken.IsCancellationRequested == owned, "ordinary cancellation silently entered owned-only path");
                    Must(queue.QueryItemStatus(item.TaskHandle).Status == "running", "acknowledgement became terminal before cleanup");
                }
                finally { release.TrySetResult(); }
                await Until(() => queue.QueryItemStatus(item.TaskHandle).Status is "completed" or "failed");
                var terminal = queue.QueryItemStatus(item.TaskHandle);
                Must(owned ? terminal.ErrorCode == "task_cancelled" : terminal.Status == "completed", "wrong terminal contract");
            });

        await check("T55 ordinary execution failures retain generic error code", async () =>
        {
            using var queue = new BgiTaskCoordinator(() => true, (_, _) => { });
            var item = queue.Submit(new(0, "ordinary", null, 0, (_, _) => Task.FromException<bool>(new InvalidOperationException("fixture"))));
            await Until(() => queue.QueryItemStatus(item.TaskHandle).Status == "failed");
            Must(queue.QueryItemStatus(item.TaskHandle).ErrorCode == "task_start_failed", "hoeing error leaked into ordinary job");
        });
        await check("T56 ordinary SDK submissions do not opt into coordinated hoeing", () =>
        {
            var flag = typeof(BgiExternalClient).GetMethod(nameof(BgiExternalClient.SubmitTaskStartAsync))!
                .GetParameters().Single(p => p.Name == "coordinatedHoeing");
            Must(flag.HasDefaultValue && Equals(flag.DefaultValue, false), "ordinary callers opt into hoeing by default");
            using var scope = new CoordinatedHoeingScope(false);
            CoordinatedHoeingScope.MarkIncomplete();
            Must(!CoordinatedHoeingScope.IsActive && !scope.Incomplete, "inactive scope changes ordinary job");
            return Task.CompletedTask;
        });
    }
}
