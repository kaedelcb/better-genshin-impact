using System.Collections.Concurrent;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;
using Xunit.Abstractions;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[Collection("TaskCenterHeavyE2E")]
public sealed class WorkflowTimeWindowHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HostAdmissionAndSendGate_CrossingWindowSkipsOriginalTaskAndCompletesFollowingTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "time-window-host-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-10-25T09:59:00+08:00");
        var sendGate = new TaskCenterOnlineSendGate();
        var port = new RecordingPort();
        var logs = new ConcurrentQueue<string>();
        var succeeded = false;
        var phase = "创建计划";
        using var client = new BgiExternalClient();
        TaskCenterHost? host = null;
        var seams = new TaskCenterAdmissionSeams
        {
            Epoch = RecordingPort.Epoch,
            ProductionBoundaryFactory = (_, runs) => new BgiWorkflowExecutionBoundary(port, runs, sendGate),
        };
        host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
            () => client, logs.Enqueue,
            (_, flows, runs) =>
            {
                var original = new BgiWorkflowExecutionBoundary(port, runs, sendGate);
                var boundary = new ArbitrationWorkflowExecutionBoundary(original,
                    (request, token) => host!.SubmitSuccessorViaAdmissionAsync(request, token));
                return new WorkflowRunner(flows, runs, boundary, new NoActions(), new NoActions(), new() { Clock = () => now });
            },
            () => (true, null), localExecutionCapability: () => true, statusSnapshotProvider: () => null,
            admissionWired: true, admissionSeams: seams, successorAdmissionWired: true);
        try
        {
            var document = new WorkflowDocument
            {
                Name = "时段跨界后继续计划", NodeTimeWindowEnabled = true,
                Nodes =
                [
                    new() { NodeId = "windowed", Kind = "resource.oneDragonConfig", Ref = new() { Config = "受限任务", Revision = "r1" },
                        TimeWindow = new() { Start = "09:00", End = "10:00" } },
                    new() { NodeId = "following", Kind = "resource.oneDragonConfig", Ref = new() { Config = "后续任务", Revision = "r2" } },
                ],
            };
            host.SaveFlow(document, null);
            sendGate.CloseForPriority();
            phase = "启动计划";
            var start = await host.StartWorkflowAsync(document.WorkflowId!);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            phase = "等待受限任务冻结且尚未调用端口";
            await WaitUntil(() => host.Runs.List().Any(run => run.CurrentSubmission is
                { NodeId: "windowed", Intent: SubmitIntentState.Submitted, SendPermit: not null }),
                () => Diagnostic(phase, root, host, port, logs));
            Assert.Empty(port.SentNodes);
            now = DateTimeOffset.Parse("2026-10-25T10:00:00+08:00");
            await sendGate.OpenAsync();
            phase = "跨过时段结束并放开发送门，等待后续任务完成";
            await WaitUntil(() => host.Runs.List().Any(run => run.IsTerminal || run.State is
                    WorkflowRunState.Unknown or WorkflowRunState.Interrupted or WorkflowRunState.LocalWaitParking)
                && !host.IsDriving(document.WorkflowId!), () => Diagnostic(phase, root, host, port, logs));
            var completed = Assert.Single(host.Runs.List());
            Assert.True(completed.State == WorkflowRunState.Succeeded,
                Diagnostic(phase, root, host, port, logs));
            Assert.Equal(new[] { "following" }, port.SentNodes);
            Assert.Equal(new[] { "skippedFilter", "succeeded" }, completed.NodeOutcomes.Select(outcome => outcome.Result));
            var skipped = Assert.Single(completed.SubmissionHistory.Where(submission => submission.NodeId == "windowed"));
            Assert.Equal(LocalNoSendEvidence.PreparedTimeWindow, skipped.LocalNoSendProof!.Kind);
            Assert.True(TerminalReleaseEvidence.BodySettled(completed, skipped));
            Assert.Null(skipped.JobId);
            Assert.False(host.IsDriving(document.WorkflowId!));
            succeeded = true;
        }
        catch (Exception ex)
        {
            // 在关闭宿主改变状态之前保留原失败；失败目录不由夹具清理。
            var diagnostic = Diagnostic(phase, root, host, port, logs);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "failure-diagnostic.json"), diagnostic);
            File.WriteAllText(Path.Combine(root, "failure-exception.txt"), ex.ToString());
            output.WriteLine("保留失败现场：" + root);
            output.WriteLine(diagnostic);
            throw;
        }
        finally
        {
            await sendGate.OpenAsync();
            await host.ShutdownAsync();
            if (succeeded && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Diagnostic(string phase, string root, TaskCenterHost host, RecordingPort port, ConcurrentQueue<string> logs)
    {
        static object Read(Func<object> read)
        {
            try { return read(); }
            catch (Exception ex) { return new { ReadError = ex.ToString() }; }
        }
        return JsonSerializer.Serialize(new
        {
            Phase = phase, Root = root,
            Runs = Read(() => host.Runs.List()),
            Driving = Read(() => host.ListFlows().Select(flow => new { flow.WorkflowId, IsDriving = host.IsDriving(flow.WorkflowId) }).ToArray()),
            Lease = Read(() => File.ReadAllText(Path.Combine(root, "arbitration", "arbitration-lease.json"))),
            SentNodes = port.SentNodes.ToArray(), Logs = logs.ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task WaitUntil(Func<bool> predicate, Func<string> diagnostic)
    {
        var budget = System.Diagnostics.Stopwatch.StartNew();
        while (budget.Elapsed < TimeSpan.FromSeconds(15))
        {
            try { if (predicate()) return; }
            catch (IOException) { }
            await Task.Delay(10);
        }
        Assert.True(predicate(), "任务中心未在有界等待内达到预期状态。\n" + diagnostic());
    }

    private sealed class NoActions : IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => throw new InvalidOperationException("本计划不包含前置。");
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => throw new InvalidOperationException("本计划不包含收尾。");
    }

    private sealed class RecordingPort : IBgiExecutionPort
    {
        internal const string Epoch = "4321:638999999999999999";
        private readonly ConcurrentDictionary<string, JsonElement> _accepted = new();
        internal ConcurrentQueue<string> SentNodes { get; } = new();
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch? ServerEpoch => new() { ProcessId = 4321, StartTicksUtc = 638999999999999999 };
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (operation == WorkflowStopAuthority.Operation)
                return Task.FromResult(new BgiExternalResponse { Success = true, Data = JsonSerializer.Serialize(new
                {
                    bgiEpoch = new { processId = 4321, startTicksUtc = 638999999999999999 }, stopVersion = 0,
                    lastManualStopTimestamp = (long?)null, monotonicFrequency = System.Diagnostics.Stopwatch.Frequency,
                }) });
            var json = JsonSerializer.SerializeToElement(payload);
            SentNodes.Enqueue(json.GetProperty("nodeId").GetString()!);
            var job = "job-" + SentNodes.Count;
            _accepted[job] = json;
            return Task.FromResult(new BgiExternalResponse { Success = true,
                Data = JsonSerializer.Serialize(new { status = "accepted", taskHandle = job }) });
        }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct) => Task.FromResult<BgiJobListSnapshot?>(null);
        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            var payload = _accepted[jobId];
            var job = new BgiJobInfo
            {
                JobId = jobId, State = "succeeded", ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
                Epoch = ServerEpoch, IdempotencyKey = payload.GetProperty("idempotencyKey").GetString(),
                WorkflowRunId = payload.GetProperty("workflowRunId").GetString(), NodeId = payload.GetProperty("nodeId").GetString(),
                Iteration = payload.GetProperty("iteration").GetInt32(), Occurrence = payload.GetProperty("occurrence").GetInt32(),
                Attempt = payload.GetProperty("attempt").GetInt32(), ConfigRevision = payload.GetProperty("expectedConfigRevision").GetString(),
                RequestFingerprintVersion = 1, RequestOperation = BgiExternalClient.ExternalOperations.TaskStart,
                RequestFingerprint = BgiOriginalRequestFingerprint.Compute(BgiExternalClient.ExternalOperations.TaskStart, payload.GetRawText()),
            };
            return Task.FromResult<(string?, BgiJobInfo?)>(("succeeded", job));
        }
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }
}
