using System.Buffers.Binary;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Newtonsoft.Json.Linq;

var passed = 0; var failed = 0;
async Task Check(string name, Func<Task> test)
{
    PreemptionGate.Disarm();
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static string Key() => Guid.NewGuid().ToString("N");
static ExternalInterfaceSession Session() => ExternalInterfaceSession.GetOrCreate(new InstanceConnection());
static async Task<InstanceIpcEnvelope> Route(ExternalInterfaceSession session, InstanceRequestHandler handler,
    string op, object? data = null) => await session.RouteAsync(handler, InstanceIpcEnvelope.Request(op, data), default);
static async Task WaitUntil(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
    Assert(condition(), "bounded wait expired");
}
static async Task RejectFrame(byte[] bytes)
{
    try { await InstanceIpcProtocol.ReadFrameAsync(new MemoryStream(bytes), default); }
    catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { return; }
    throw new Exception("invalid/truncated frame accepted");
}

await Check("T01 UTF8 envelope roundtrip and response correlation", async () => {
    var request = InstanceIpcEnvelope.Request("ext.task.start", new { configName = "槲寄生联机", idempotencyKey = Key() });
    using var stream = new MemoryStream();
    await InstanceIpcProtocol.WriteJsonAsync(stream, request, default);
    var bytes = stream.ToArray();
    Assert(BinaryPrimitives.ReadUInt32LittleEndian(bytes) == bytes.Length - 5 && bytes[4] == 1, "wrong framing");
    stream.Position = 0;
    var decoded = InstanceIpcProtocol.ReadJson((await InstanceIpcProtocol.ReadFrameAsync(stream, default))!.Value);
    Assert(decoded.RequestId == request.RequestId && decoded.Data!["configName"]!.ToString() == "槲寄生联机", "roundtrip lost data");
    Assert(InstanceIpcEnvelope.Response(decoded).RequestId == request.RequestId, "response correlation lost");
});
await Check("T02 fragmented adjacent frames do not merge", async () => {
    using var encoded = new MemoryStream();
    var a = InstanceIpcEnvelope.Request("ping"); var b = InstanceIpcEnvelope.Request("ext.job.list");
    await InstanceIpcProtocol.WriteJsonAsync(encoded, a, default);
    await InstanceIpcProtocol.WriteJsonAsync(encoded, b, default);
    using var stream = new FragmentedStream(encoded.ToArray());
    Assert(InstanceIpcProtocol.ReadJson((await InstanceIpcProtocol.ReadFrameAsync(stream, default))!.Value).RequestId == a.RequestId, "first frame lost");
    Assert(InstanceIpcProtocol.ReadJson((await InstanceIpcProtocol.ReadFrameAsync(stream, default))!.Value).RequestId == b.RequestId, "second frame lost");
    Assert(await InstanceIpcProtocol.ReadFrameAsync(stream, default) == null, "EOF not detected");
});
await Check("T03 oversized frame rejected", () => {
    var bytes = new byte[5]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, InstanceIpcProtocol.MaxPayloadLength + 1); bytes[4] = 1;
    return RejectFrame(bytes);
});
await Check("T04 truncated header rejected", () => RejectFrame([1, 0]));
await Check("T05 truncated payload rejected", () => RejectFrame([3, 0, 0, 0, 1, 123]));
await Check("T06 unknown payload type rejected", () => RejectFrame([0, 0, 0, 0, 255]));
await Check("T07 hello retains v2 and reports epoch", async () => {
    var response = await Route(Session(), new(), "ext.hello");
    Assert(response.Success == true && response.Data!["protocolVersion"]!.Value<int>() == 2, "version changed");
    Assert(response.Data!["bgiEpoch"]!["processId"]!.Value<int>() == Environment.ProcessId, "wrong epoch");
    Assert(response.Data["capabilities"]!["job.registry"]!.Value<bool>(), "missing registry capability");
});
await Check("T08 completed write replay survives session change", async () => {
    var calls = 0; var key = Key();
    var handler = new InstanceRequestHandler { Write = r => {
        Interlocked.Increment(ref calls); return Task.FromResult(InstanceIpcEnvelope.Response(r, new { status = "applied" })); } };
    var first = await Route(Session(), handler, "ext.config.setTaskEnabled", new { idempotencyKey = key, taskIndex = 1 });
    var second = await Route(Session(), handler, "ext.config.setTaskEnabled", new { idempotencyKey = key, taskIndex = 1 });
    Assert(first.Success == true && second.Success == true && calls == 1, "sequential replay repeated write");
    Assert(first.RequestId != second.RequestId, "replay returned old correlation id");
});
await Check("T09 concurrent duplicate writes must execute once", async () => {
    var calls = 0; var key = Key();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new InstanceRequestHandler { Write = async r => {
        Interlocked.Increment(ref calls); entered.TrySetResult();
        await release.Task.WaitAsync(TimeSpan.FromSeconds(3));
        return InstanceIpcEnvelope.Response(r, new { status = "applied" }); } };
    var first = Route(Session(), handler, "ext.config.setTaskEnabled", new { idempotencyKey = key, taskIndex = 1 });
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var second = Route(Session(), handler, "ext.config.setTaskEnabled", new { idempotencyKey = key, taskIndex = 1 });
    release.TrySetResult(); await Task.WhenAll(first, second);
    Assert(calls == 1, $"same key invoked write boundary {calls} times");
});
await Check("T10 reused key with changed payload must not return old success", async () => {
    var key = Key(); var handler = new InstanceRequestHandler(); var session = Session();
    await Route(session, handler, "ext.config.setTaskEnabled", new { idempotencyKey = key, taskIndex = 1, enabled = true });
    var response = await Route(session, handler, "ext.config.setTaskEnabled", new { idempotencyKey = key, taskIndex = 2, enabled = false });
    Assert(response.Success == false, "different payload silently replayed previous success");
});
await Check("T11 reused key across operations must not replay unrelated response", async () => {
    var key = Key(); var handler = new InstanceRequestHandler(); var session = Session();
    await Route(session, handler, "ext.config.setTaskEnabled", new { idempotencyKey = key });
    var response = await Route(session, handler, "ext.task.stop", new { idempotencyKey = key });
    Assert(response.Success == false || response.Data?["status"]?.ToString() == "inert_stop", "stop received cached config-write success without reaching stop boundary");
});
await Check("T12 known pre-admission rejection is not cached as success", async () => {
    var calls = 0; var key = Key(); var session = Session();
    var handler = new InstanceRequestHandler { Write = r => Task.FromResult(++calls == 1
        ? InstanceIpcEnvelope.Failure(r, "queue_full", "rejected before write") : InstanceIpcEnvelope.Response(r)) };
    Assert((await Route(session, handler, "ext.config.setTaskEnabled", new { idempotencyKey = key })).Success == false, "failure swallowed");
    Assert((await Route(session, handler, "ext.config.setTaskEnabled", new { idempotencyKey = key })).Success == true && calls == 2, "retry blocked");
});
await Check("T13 dropped events do not prevent terminal pull", async () => {
    var job = JobRegistry.Instance.Submit(JobKind.Group, Key(), JobSource.Ext).Job;
    JobRegistry.Instance.TryMarkRunning(job.JobId);
    JobRegistry.Instance.TryMarkTerminal(job.JobId, JobState.Failed, "simulated_failure");
    var response = await Route(Session(), new(), "ext.job.status", new { jobId = job.JobId });
    Assert(response.Data!["status"]!.ToString() == "failed" && response.Data["job"]!["errorCode"]!.ToString() == "simulated_failure", "terminal unavailable without event delivery");
});
await Check("T14 unknown handle is not succeeded", async () => {
    var response = await Route(Session(), new(), "ext.job.status", new { jobId = Guid.NewGuid() });
    Assert(response.Data!["status"]!.ToString() == "not_found" && response.Data["bgiEpoch"] != null, "unknown treated as completion");
});
await Check("T15 queue backpressure and pending cancellation", async () => {
    using var queue = new BgiTaskCoordinator(isSlotFree: () => false, publish: (_, _) => { }, slotPollInterval: TimeSpan.FromMilliseconds(5));
    var calls = 0; var handles = new List<Guid>();
    for (var i = 0; i < BgiTaskCoordinator.QueueCapacity; i++)
        handles.Add(queue.Submit(new(0, Key(), null, 0, (_, _) => { calls++; return Task.FromResult(false); })).TaskHandle);
    Assert(queue.Submit(new(0, Key(), null, 0, (_, _) => Task.FromResult(false))).Status == BgiTaskCoordinator.SubmitStatus.QueueFull, "queue not bounded");
    Assert(queue.ClearQueue() == handles.Count && queue.QueueDepth == 0, "queued cancellation leaked");
    await Task.Delay(20); Assert(calls == 0, "cancelled queued task executed");
});
await Check("T16 timed-out queue item must release queue capacity", async () => {
    using var queue = new BgiTaskCoordinator(isSlotFree: () => false, publish: (_, _) => { },
        slotPollInterval: TimeSpan.FromMilliseconds(5), slotWaitTimeout: TimeSpan.FromMilliseconds(25));
    var item = queue.Submit(new(0, Key(), null, 0, (_, _) => throw new Exception("must not execute")));
    await WaitUntil(() => queue.QueryItemStatus(item.TaskHandle).Status == "failed");
    Assert(queue.QueueDepth == 0, $"terminal item still consumes queue capacity: {queue.QueueDepth}");
});
await Check("T17 queue adopts duplicate in-flight request", async () => {
    using var queue = new BgiTaskCoordinator(isSlotFree: () => false, publish: (_, _) => { }, slotPollInterval: TimeSpan.FromMilliseconds(5));
    var request = new BgiTaskCoordinator.TaskSubmission(0, Key(), null, 0, (_, _) => Task.FromResult(false)) { IdempotencyKey = Key() };
    var a = queue.Submit(request); var b = queue.Submit(request);
    Assert(a.TaskHandle == b.TaskHandle && b.Status == BgiTaskCoordinator.SubmitStatus.Adopted, "duplicate queue entry");
    queue.ClearQueue(); await Task.CompletedTask;
});
await Check("T18 declared stale epoch must be rejected before enqueue", async () => {
    var handler = new InstanceRequestHandler();
    var response = await Route(Session(), handler, "ext.task.start", new {
        groupName = Key(), idempotencyKey = Key(), bgiEpoch = new { processId = Environment.ProcessId, startTicksUtc = -1L } });
    if (response.Data?["taskHandle"] is { } handle)
        await WaitUntil(() => BgiTaskCoordinator.Instance.QueryItemStatus(Guid.Parse(handle.ToString())).Status == "completed");
    Assert(response.Success == false && handler.Executions == 0, $"stale epoch accepted; inert executions={handler.Executions}");
});
await Check("T19 workflow node identity must be represented by job contract", () => {
    var missing = new[] { "WorkflowRunId", "NodeId", "Iteration" }.Where(n => typeof(BgiJob).GetProperty(n) == null).ToArray();
    Assert(missing.Length == 0, "job model lacks " + string.Join(", ", missing)); return Task.CompletedTask;
});
await Check("T20 queue snapshot and registry preserve explicit request key", async () => {
    using var queue = new BgiTaskCoordinator(isSlotFree: () => false, publish: (_, _) => { }, slotPollInterval: TimeSpan.FromMilliseconds(5));
    var key = Key(); var item = queue.Submit(new(0, Key(), null, 0, (_, _) => Task.FromResult(false)) { IdempotencyKey = key });
    var response = await Route(Session(), new(), "ext.job.status", new { jobId = item.TaskHandle });
    Assert(response.Data!["job"]!["idempotencyKey"]!.ToString() == key && response.Data["status"]!.ToString() == "queued", "request identity lost");
    queue.ClearQueue();
});

await Check("T21 cache pressure must not replay completed task within retry window", async () => {
    var handler = new InstanceRequestHandler(); var session = Session(); var key = Key(); var name = Key();
    var payload = new { groupName = name, idempotencyKey = key };
    var first = await Route(session, handler, "ext.task.start", payload);
    Assert(first.Success == true, "setup submission failed");
    var firstHandle = Guid.Parse(first.Data!["taskHandle"]!.ToString());
    await WaitUntil(() => BgiTaskCoordinator.Instance.QueryItemStatus(firstHandle).Status == "completed"
        && BgiTaskCoordinator.Instance.CurrentTaskHandle == null);
    // The documented time window is 30 minutes, but the response cache has a 128-entry cap.
    // All filler writes are inert in-memory handler calls; no configuration is read or saved.
    for (var i = 0; i < 130; i++)
        await Route(session, handler, "ext.config.setTaskEnabled", new { idempotencyKey = Key(), taskIndex = i });
    var retry = await Route(Session(), handler, "ext.task.start", payload);
    if (retry.Data?["taskHandle"] is { } token)
        await WaitUntil(() => BgiTaskCoordinator.Instance.QueryItemStatus(Guid.Parse(token.ToString())).Status == "completed");
    Assert(handler.Executions == 1, $"completed request replayed after cache eviction; inert executions={handler.Executions}");
});

await ContractRegression.Run(Check);
await HoeingCompatibilityRegression.Run(Check);
await R2BridgeRegression.Run(Check);
if (BgiTaskCoordinator.IsCreated) BgiTaskCoordinator.Instance.Dispose();
Console.WriteLine($"Channel contract audit: passed={passed}; failed={failed}; total={passed + failed}. Private pipe/temp fixtures only; no product UI/game/IPC/User access.");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
}
