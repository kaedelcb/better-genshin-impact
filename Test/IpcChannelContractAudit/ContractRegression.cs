using System.Reflection;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using MultiplayerHoeingAssistant.Services;
using Newtonsoft.Json.Linq;

internal static class ContractRegression
{
    private static void Must(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Key() => Guid.NewGuid().ToString("N");
    private static InstanceIpcEnvelope Request(object? data = null) => InstanceIpcEnvelope.Request("ext.config.setTaskEnabled", data);
    private static async Task Throws(Func<Task> action, string expected)
    {
        try { await action(); } catch (Exception ex) when (ex.Message.Contains(expected)) { return; }
        throw new Exception("Expected rejection: " + expected);
    }
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("T22 cancellation abandons waiter, not accepted operation", async () => {
            var registry = new JobRegistry(false); using var cancelled = new CancellationTokenSource();
            var release = new TaskCompletionSource<InstanceIpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var r = Request(); var calls = 0;
            Task<InstanceIpcEnvelope> Execute() { calls++; return release.Task; }
            var first = registry.ExecuteRequestOnceAsync("key", "fp", r, Execute, cancelled.Token);
            var second = registry.ExecuteRequestOnceAsync("key", "fp", r, Execute, default);
            cancelled.Cancel();
            try { await first; throw new Exception("waiter did not cancel"); } catch (OperationCanceledException) { }
            release.SetResult(InstanceIpcEnvelope.Response(r));
            Must((await second).Success == true && calls == 1, "duplicate cancellation killed owner");
        });
        await check("T23 escaped outcome and execution failure cannot be replayed", async () => {
            var registry = new JobRegistry(false); var r = Request(); var calls = 0;
            Task<InstanceIpcEnvelope> Execute() { calls++; throw new IOException("lost result"); }
            var first = await registry.ExecuteRequestOnceAsync("unknown", "fp", r, Execute, default);
            var second = await registry.ExecuteRequestOnceAsync("unknown", "fp", r, Execute, default);
            Must(first.ErrorCode == "result_unknown" && second.ErrorCode == "result_unknown" && calls == 1, "unknown executed twice");
            calls = 0;
            Task<InstanceIpcEnvelope> Failed() { calls++; return Task.FromResult(InstanceIpcEnvelope.Failure(r, "task_start_failed", "partial work")); }
            await registry.ExecuteRequestOnceAsync("failed", "fp", r, Failed, default);
            await registry.ExecuteRequestOnceAsync("failed", "fp", r, Failed, default);
            Must(calls == 1, "failed execution replayed");
        });
        await check("T24 known queue rejection permits retry", async () => {
            var registry = new JobRegistry(false); var r = Request(); var calls = 0;
            Task<InstanceIpcEnvelope> Execute() => Task.FromResult(++calls == 1
                ? InstanceIpcEnvelope.Failure(r, "queue_full", "full") : InstanceIpcEnvelope.Response(r));
            await registry.ExecuteRequestOnceAsync("key", "fp", r, Execute, default);
            Must((await registry.ExecuteRequestOnceAsync("key", "fp", r, Execute, default)).Success == true && calls == 2, "rejection became permanent");
        });
        await check("T25 full receipt ledger rejects new writes but retains receipts and stop", async () => {
            var registry = new JobRegistry(false); var r = Request(); var calls = 0;
            Task<InstanceIpcEnvelope> Execute() { calls++; return Task.FromResult(InstanceIpcEnvelope.Response(r)); }
            for (var i = 0; i < JobRegistry.RequestReceiptCapacity; i++)
                await registry.ExecuteRequestOnceAsync(i.ToString(), "fp", r, Execute, default);
            var rejected = await registry.ExecuteRequestOnceAsync("overflow", "fp", r, Execute, default);
            Must(rejected.ErrorCode == "idempotency_capacity" && calls == JobRegistry.RequestReceiptCapacity, "overflow evicted receipt or executed");
            await registry.ExecuteRequestOnceAsync("0", "fp", r, Execute, default);
            Must(calls == JobRegistry.RequestReceiptCapacity, "first receipt lost");
            var stop = InstanceIpcEnvelope.Request("ext.task.stop");
            Must((await registry.ExecuteRequestOnceAsync("stop", "stop", stop, Execute, default)).Success == true, "safety stop blocked by capacity");
        });
        await check("T26 fingerprint ignores property order but binds operation and content", () => {
            var a = Request(new { enabled = true, taskIndex = 1, idempotencyKey = "a" });
            var b = Request(new { idempotencyKey = "b", taskIndex = 1, enabled = true });
            Must(ExecutionRequestContract.Fingerprint(a) == ExecutionRequestContract.Fingerprint(b), "property order changed identity");
            b.Data!["enabled"] = false;
            Must(ExecutionRequestContract.Fingerprint(a) != ExecutionRequestContract.Fingerprint(b), "content unbound");
            return Task.CompletedTask;
        });
        await check("T27 expired request rejected without reaching write boundary", async () => {
            var handler = new InstanceRequestHandler { Write = _ => throw new Exception("expired request executed") };
            var response = await ExternalInterfaceSession.GetOrCreate(new InstanceConnection()).RouteAsync(handler,
                Request(new { expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), idempotencyKey = Key() }), default);
            Must(response.ErrorCode == "request_expired", "expired request admitted");
        });
        await check("T28 epoch accepts lossless string ticks, rejects malformed values", () => {
            var r = Request(new { bgiEpoch = new { processId = Environment.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc.ToString() } });
            Must(ExecutionRequestContract.Validate(r) == null, "string ticks rejected");
            r.Data!["bgiEpoch"]!["startTicksUtc"] = "not-a-tick";
            Must(ExecutionRequestContract.Validate(r)?.ErrorCode == "invalid_request", "malformed epoch unhandled");
            return Task.CompletedTask;
        });
        await check("T29 explicit workflow contract rejects missing identity or revision", () => {
            var r = InstanceIpcEnvelope.Request("ext.task.start", new { executionContractVersion = 1, idempotencyKey = Key(),
                expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), bgiEpoch = new { processId = Environment.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc },
                groupName = "fixture" });
            Must(ExecutionRequestContract.Validate(r)?.ErrorCode == "invalid_request", "incomplete workflow accepted");
            r.Data!["workflowRunId"] = Guid.NewGuid().ToString(); r.Data["nodeId"] = "node"; r.Data["iteration"] = 0;
            r.Data["expectedConfigRevision"] = "revision";
            Must(ExecutionRequestContract.Validate(r) == null, "complete contract rejected");
            r.Data["iteration"] = -1;
            Must(ExecutionRequestContract.Validate(r)?.ErrorCode == "invalid_request", "invalid iteration accepted");
            return Task.CompletedTask;
        });
        await check("T30 queued job query retains workflow node and attempt identities", async () => {
            using var queue = new BgiTaskCoordinator(isSlotFree: () => false, publish: (_, _) => { });
            var run = Guid.NewGuid(); var identity = new JobExecutionIdentity(run, "node-a", 7, "legacy:2", "rev");
            var item = queue.Submit(new(0, Key(), null, 0, (_, _) => Task.FromResult(false)) { Identity = identity, IdempotencyKey = Key() });
            var response = await ExternalInterfaceSession.GetOrCreate(new InstanceConnection()).RouteAsync(new(),
                InstanceIpcEnvelope.Request("ext.job.status", new { jobId = item.TaskHandle }), default);
            var job = response.Data!["job"]!;
            Must(Guid.Parse(job["workflowRunId"]!.ToString()) == run && job["nodeId"]!.ToString() == "node-a"
                && job["iteration"]!.Value<int>() == 7 && Guid.Parse(job["attemptId"]!.ToString()) == item.TaskHandle
                && job["taskId"]!.ToString() == "legacy:2" && job["configRevision"]!.ToString() == "rev", "identity lost at query");
            queue.ClearQueue();
        });
        await check("T31 internal child inherits plan identity without reusing attempt", () => {
            var registry = new JobRegistry(false); var identity = new JobExecutionIdentity(Guid.NewGuid(), "node", 1, "task", "rev");
            var parent = registry.Submit(JobKind.OneDragon, "parent", JobSource.Ext, identity: identity).Job;
            var child = registry.Submit(JobKind.Group, "child", JobSource.OneDragonInternal, parentJobId: parent.JobId).Job;
            Must(child.WorkflowRunId == parent.WorkflowRunId && child.NodeId == parent.NodeId && child.Iteration == 1 && child.JobId != parent.JobId, "child identity lost");
            return Task.CompletedTask;
        });

        using var fixture = new Fixture(); var store = new TaskConfigurationContract(fixture.Root);
        await check("T32 configuration describe distinguishes duplicate legacy names", async () => {
            var r = await ExternalInterfaceConfigurationPlane.DispatchAsync(InstanceIpcEnvelope.Request("ext.config.describe", new { configName = "dragon" }), store);
            Must(r.Success == true && r.Data!["tasks"]!.Count() == 2, "describe failed");
            var ids = r.Data!["tasks"]!.Select(t => t["taskId"]!.ToString()).ToArray();
            Must(ids.SequenceEqual(new[] { "legacy:1", "legacy:2" }), "names used as identities");
        });
        await check("T33 config applied receipt carries actual persisted revision", async () => {
            var before = await store.ReadAsync("dragon", true);
            var r = await ExternalInterfaceConfigurationPlane.DispatchAsync(InstanceIpcEnvelope.Request("ext.config.applyTaskState",
                new { configName = "dragon", taskId = "legacy:2", enabled = true, expectedConfigRevision = before.Revision }), store);
            var after = await store.ReadAsync("dragon", true);
            Must(r.Success == true && r.Data!["status"]!.ToString() == "config_applied" && r.Data["configRevision"]!.ToString() == after.Revision
                && after.Revision != before.Revision && after.Tasks.All(t => t.Enabled), "receipt not bound to persistence");
        });
        await check("T34 stale configuration update cannot overwrite newer edit", async () => {
            var before = await store.ReadAsync("dragon", true);
            await Throws(() => store.ApplyEnabledAsync("dragon", true, "legacy:1", null, false, "stale", null), "configuration_changed");
            Must((await store.ReadAsync("dragon", true)).Revision == before.Revision, "stale update changed file");
        });
        await check("T35 concurrent compare-and-set permits exactly one winner", async () => {
            var before = await store.ReadAsync("dragon", true);
            async Task<bool> Attempt(string task)
            {
                try { await store.ApplyEnabledAsync("dragon", true, task, null, false, before.Revision, null); return true; }
                catch (InvalidOperationException ex) when (ex.Message == "configuration_changed") { return false; }
            }
            var results = await Task.WhenAll(Attempt("legacy:1"), Attempt("legacy:2"));
            Must(results.Count(v => v) == 1, "both writers overwrote each other");
        });
        await check("T36 stale start rejected, valid single-task selection frozen", async () => {
            var s = await store.ReadAsync("dragon", true); var selected = s.Tasks.First(t => t.Enabled);
            var r = InstanceIpcEnvelope.Request("ext.task.start", new { configName = "dragon", taskId = selected.TaskId, expectedConfigRevision = s.Revision });
            var prepared = await ExternalInterfaceConfigurationPlane.PrepareExecutionAsync(r, store);
            Must(prepared.SingleIndex == selected.LegacyIndex && prepared.Snapshot!.Revision == s.Revision, "selection did not resolve exact ID");
            r.Data!["expectedConfigRevision"] = "stale";
            await Throws(() => ExternalInterfaceConfigurationPlane.PrepareExecutionAsync(r, store), "configuration_changed");
        });
        await check("T37 disabled or unversioned single-task request rejected", async () => {
            var s = await store.ReadAsync("dragon", true); var disabled = s.Tasks.First(t => !t.Enabled);
            var r = InstanceIpcEnvelope.Request("ext.task.start", new { configName = "dragon", taskId = disabled.TaskId, expectedConfigRevision = s.Revision });
            await Throws(() => ExternalInterfaceConfigurationPlane.PrepareExecutionAsync(r, store), "task_disabled");
            r.Data!.Remove("expectedConfigRevision");
            await Throws(() => ExternalInterfaceConfigurationPlane.PrepareExecutionAsync(r, store), "configuration_revision_required");
        });
        await check("T38 group task IDs survive toggles and identify duplicate occurrences", async () => {
            var before = await store.ReadAsync("group", false);
            Must(before.Tasks.Select(t => t.TaskId).Distinct().Count() == 3, "duplicates collide");
            var after = await store.ApplyEnabledAsync("group", false, before.Tasks[1].TaskId, null, false, before.Revision, null);
            Must(after.Tasks.Select(t => t.TaskId).SequenceEqual(before.Tasks.Select(t => t.TaskId)) && !after.Tasks[1].Enabled
                && after.Tasks[0].Enabled && after.Tasks[2].Enabled, "toggle changed IDs or wrong item");
        });
        await check("T39 native bool schema remains writable and explicitly lacks single executor", async () => {
            var s = await store.ReadAsync("native", true);
            var after = await store.ApplyEnabledAsync("native", true, s.Tasks[0].TaskId, null, true, s.Revision, null);
            Must(after.Tasks[0].Enabled && after.Tasks[0].LegacyIndex == null, "native boolean schema damaged");
            await Throws(() => ExternalInterfaceConfigurationPlane.PrepareExecutionAsync(InstanceIpcEnvelope.Request("ext.task.start",
                new { configName = "native", taskId = s.Tasks[0].TaskId, expectedConfigRevision = after.Revision }), store), "native_single_execution_not_supported");
        });
        await check("T40 foreign takeover ticket cannot modify configuration", async () => {
            var s = await store.ReadAsync("dragon", true); PreemptionGate.Arm("owner");
            await Throws(() => store.ApplyEnabledAsync("dragon", true, "legacy:1", null, true, s.Revision, "foreign"), "takeover_conflict");
            Must((await store.ReadAsync("dragon", true)).Revision == s.Revision, "foreign write persisted");
        });
        await check("T41 traversal and malformed arrays rejected without temporary leaks", async () => {
            await Throws(() => store.ReadAsync("../dragon", true), "invalid_configuration_name");
            await Throws(() => Task.Run(() => TaskConfigurationContract.Parse(Encoding.UTF8.GetBytes("{\"projects\":[null]}"), false)), "invalid_project");
            Must(!Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories).Any(), "temporary file leaked");
        });
        await check("T42 UTF8 BOM and unknown configuration fields retained", async () => {
            var s = await store.ReadAsync("bom", true);
            await store.ApplyEnabledAsync("bom", true, "legacy:1", null, false, s.Revision, null);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "OneDragon", "bom.json"));
            Must(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }) && (await store.ReadAsync("bom", true)).Document["unknown"]!.Value<int>() == 42, "BOM or unrelated field lost");
        });
        await check("T43 real assistant decoder consumes real BGI frame in fragments", async () => {
            using var bytes = new MemoryStream(); var request = Request();
            await InstanceIpcProtocol.WriteJsonAsync(bytes, InstanceIpcEnvelope.Response(request, new { text = "槲寄生" }), default);
            using var stream = new FragmentedStream(bytes.ToArray());
            var response = await Decode(stream);
            Must(response != null && Guid.Parse((string)response.GetType().GetProperty("RequestId")!.GetValue(response)!) == request.RequestId, "SDK correlation lost");
        });
        foreach (var (name, json, type) in new[] {
            ("T44 SDK rejects unknown frame type", "{\"version\":2}", (byte)255),
            ("T45 SDK rejects unsupported version", "{\"version\":3}", (byte)1),
            ("T46 SDK rejects string version", "{\"version\":\"2\"}", (byte)1),
            ("T47 SDK rejects non-object envelope", "[]", (byte)1) })
            await check(name, async () => {
                var body = Encoding.UTF8.GetBytes(json); using var stream = new MemoryStream();
                stream.Write(BitConverter.GetBytes(body.Length)); stream.WriteByte(type); stream.Write(body); stream.Position = 0;
                try { await Decode(stream); } catch (InvalidDataException) { return; }
                throw new Exception("invalid SDK frame accepted");
            });
        await check("T48 production pipe ACL and SDK bidirectional frames use only private fixture", async () => {
            var name = "Codex.IpcChannelAudit." + Key();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var server = InstancePipeFactory.CreateServer(name, true);
            var acl = server.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            Must(acl.AreAccessRulesProtected && Equals(acl.GetOwner(typeof(SecurityIdentifier)), identity.User), "pipe owner/ACL changed");
            var rules = acl.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            Must(rules.Any(r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null))), "network deny missing");
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
            // Constructing SDK only computes its name. Never call StartAsync/SendCommandAsync;
            // pass the already-connected fixture stream to the actual private frame writer.
            using var sdk = new BgiExternalClient(); var rid = Guid.NewGuid();
            var write = typeof(BgiExternalClient).GetMethod("WriteEnvelopeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)write.Invoke(sdk, new object[] { client, "ext.fixture", rid.ToString("N"), new { text = "独立管道" }, timeout.Token })!;
            var decoded = InstanceIpcProtocol.ReadJson((await InstanceIpcProtocol.ReadFrameAsync(server, timeout.Token))!.Value);
            Must(decoded.RequestId == rid && decoded.Data!["text"]!.ToString() == "独立管道", "SDK to BGI frame mismatch");
            await InstanceIpcProtocol.WriteJsonAsync(server, InstanceIpcEnvelope.Response(decoded, new { fixture = true }), timeout.Token);
            var reply = await Decode(client).WaitAsync(timeout.Token);
            Must(Guid.Parse((string)reply!.GetType().GetProperty("RequestId")!.GetValue(reply)!) == rid, "BGI to SDK frame mismatch");
        });
        await check("T49 timed-out items can be cleared and capacity reused repeatedly", async () => {
            using var queue = new BgiTaskCoordinator(isSlotFree: () => false, publish: (_, _) => { },
                slotPollInterval: TimeSpan.FromMilliseconds(1), slotWaitTimeout: TimeSpan.FromMilliseconds(3));
            for (var i = 0; i < 40; i++)
            {
                var item = queue.Submit(new(0, Key(), null, 0, (_, _) => throw new Exception("must not run")));
                Must(item.Status == BgiTaskCoordinator.SubmitStatus.Queued, "capacity leaked");
                if (i % 2 == 0) queue.CancelByHandle(item.TaskHandle);
                else
                {
                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    while (queue.QueryItemStatus(item.TaskHandle).Status != "failed" && DateTime.UtcNow < deadline) await Task.Delay(1);
                    Must(queue.QueryItemStatus(item.TaskHandle).Status == "failed", $"timeout never settled: round={i} status={queue.QueryItemStatus(item.TaskHandle).Status} depth={queue.QueueDepth}");
                }
                queue.ClearQueue(); Must(queue.QueueDepth == 0, "terminal item remained pending");
            }
        });
        await check("T50 expired duplicate replays actual receipt instead of falsely saying unexecuted", async () => {
            var calls = 0; var request = Request(new { idempotencyKey = Key(), expiresAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(250) });
            var handler = new InstanceRequestHandler { Write = r => { calls++; return Task.FromResult(InstanceIpcEnvelope.Response(r)); } };
            var session = ExternalInterfaceSession.GetOrCreate(new InstanceConnection());
            Must((await session.RouteAsync(handler, request, default)).Success == true, "fixture failed to execute");
            await Task.Delay(300);
            Must((await session.RouteAsync(handler, request, default)).Success == true && calls == 1, "expiry erased accepted outcome or repeated execution");
        });
        await check("T51 config deadline is rechecked after waiting for another writer", async () => {
            var snapshot = await store.ReadAsync("dragon", true);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = store.ExecuteLockedAsync("dragon", true, async () => { entered.SetResult(); await release.Task; return 0; });
            await entered.Task;
            var request = InstanceIpcEnvelope.Request("ext.config.applyTaskState", new {
                configName = "dragon", taskId = "legacy:1", enabled = true, expectedConfigRevision = snapshot.Revision,
                expiresAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(250) });
            var pending = ExternalInterfaceConfigurationPlane.DispatchAsync(request, store);
            await Task.Delay(300); release.SetResult(); await held;
            var response = await pending;
            Must(response.ErrorCode == "request_expired" && (await store.ReadAsync("dragon", true)).Revision == snapshot.Revision,
                "expired lock waiter changed configuration");
        });
        await check("T52 strict execution contract cannot silently downgrade to legacy entry", () => {
            var r = InstanceIpcEnvelope.Request("task.start", new { executionContractVersion = 1, groupName = "fixture" });
            Must(ExecutionRequestContract.Validate(r)?.ErrorCode == "capability_required", "v2 entry accepted unfulfilled strict contract");
            return Task.CompletedTask;
        });
    }
    private static async Task<object?> Decode(Stream stream)
    {
        var method = typeof(BgiExternalClient).GetMethod("ReadEnvelopeAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(null, new object[] { stream, CancellationToken.None })!;
        await task; return task.GetType().GetProperty("Result")!.GetValue(task);
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bgi-channel-contract-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "OneDragon")); Directory.CreateDirectory(Path.Combine(Root, "ScriptGroup"));
            var dragon = "{\"taskEnabledList\":{\"1\":{\"Item1\":true,\"Item2\":\"同名\"},\"2\":{\"Item1\":false,\"Item2\":\"同名\"}},\"unknown\":42}";
            File.WriteAllText(Path.Combine(Root, "OneDragon", "dragon.json"), dragon);
            File.WriteAllText(Path.Combine(Root, "OneDragon", "bom.json"), dragon, new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(Root, "OneDragon", "native.json"), "{\"taskEnabledList\":{\"guid-task\":false}}");
            File.WriteAllText(Path.Combine(Root, "ScriptGroup", "group.json"), "{\"projects\":[{\"name\":\"同名\",\"type\":\"Javascript\",\"folderName\":\"a\"},{\"name\":\"同名\",\"type\":\"Javascript\",\"folderName\":\"a\"},{\"name\":\"不同\",\"folderName\":\"b\"}]}");
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(path).StartsWith("bgi-channel-contract-", StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(path, true);
        }
    }
}
