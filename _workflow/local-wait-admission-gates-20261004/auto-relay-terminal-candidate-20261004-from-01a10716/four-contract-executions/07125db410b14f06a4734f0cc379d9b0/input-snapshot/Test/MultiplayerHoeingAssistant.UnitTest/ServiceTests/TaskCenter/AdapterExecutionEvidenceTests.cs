using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

// Real SDK and production adapters, private transport only. No game/real User access.
[Collection("TaskCenterPipeTransport")]
public sealed class AdapterExecutionEvidenceTests : IAsyncLifetime
{
    private readonly BgiInstancePipeDouble _pipe = new();
    private string? _previous;
    private BgiExternalClient _client = null!;
    private RunStore _store = null!;
    private WorkflowRunRecord _run = null!;
    private JsonElement _sent;
    private string _reply = "accepted";
    private string _fault = "";
    private string _raw = "succeeded";
    private int _queries;
    private object Epoch => new { processId = Environment.ProcessId, startTicksUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks };

    public async Task InitializeAsync()
    {
        _previous = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = _pipe.PipeName;
        _pipe.BodyForTest = Respond;
        _pipe.Start();
        _client = new BgiExternalClient();
        await _client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BgiExternalLinkState.Ready, _client.State);
        _client.TakeoverTicket = "frozen-ticket";
        _store = new RunStore(Path.Combine(Path.GetTempPath(), "adapter-evidence-" + Guid.NewGuid().ToString("N")));
        _run = _store.CreateRun("wf", "rev", stopAuthority: new WorkflowStopAuthorityRecord(
            WorkflowStopAuthority.Epoch(_client.ServerEpoch)!, 7, Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), Stopwatch.Frequency));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _pipe.DisposeAsync();
        BgiExternalClient.PipeNameOverrideForTest = _previous;
    }

    private (bool, string?, string?, object?)? Respond(string op, JsonElement? data)
    {
        if (op == "ext.hello") return (true, null, null, new {
            bgiVersion = "evidence-fixture", sessionId = "fixture", windowsSessionId = Process.GetCurrentProcess().SessionId,
            processId = Environment.ProcessId, bgiEpoch = Epoch,
            capabilities = new Dictionary<string, bool> { ["execution.contract.v1"] = true,
                [WorkflowStopAuthority.Capability] = true, ["execution.exit.confirmed.v1"] = true, ["execution.cancel.identity.v1"] = true,
                [BgiExternalClient.CapabilityPrerequisiteAccount] = true, [BgiExternalClient.CapabilityTerminalCompletionAction] = true } });
        if (op is "ext.prerequisite.account" or "ext.terminal.completionAction")
        {
            _sent = data!.Value.Clone();
            if (_reply == "untyped") return (false, "queue_full", "untyped", new { });
            if (_reply == "typed") return (false, "expired", "typed", new {
                executionDisposition = "server_rejected_before_acceptance", accepted = false, operation = op,
                bgiEpoch = _fault == "epoch" ? new { processId = -1, startTicksUtc = -1L } : Epoch, request = _sent });
            if (_reply == "already_executed") return (true, null, null, new { status = _reply });
            return (true, null, null, new { status = "queued", taskHandle = "job-1" });
        }
        if (op == "ext.task.cancel") return (false, "cancel_failed", "RPC failed", new { });
        if (op == "ext.job.status")
        {
            _queries++;
            var job = JsonNode.Parse(_sent.GetRawText())!.AsObject();
            job["jobId"] = "job-1";
            job["state"] = _raw;
            job["exitConfirmed"] = _fault != "delayed-exit" || _queries >= 2;
            job["exitDisposition"] = "execution_exited";
            job["wasCancelled"] = _fault == "wasCancelled";
            if (_fault is "jobId" or "idempotencyKey" or "workflowRunId" or "nodeId") job[_fault] = "wrong";
            if (_fault is "occurrence" or "iteration" or "attempt") job[_fault] = 99;
            return (true, null, null, new { status = "found", bgiEpoch = _fault == "epoch" ? new { processId = -1, startTicksUtc = -1L } : Epoch, job });
        }
        if (op == "ext.job.list")
        {
            var job = JsonNode.Parse(_sent.GetRawText())!.AsObject(); job["jobId"] = "job-1";
            return (true, null, null, new { bgiEpoch = Epoch, jobs = new[] { job } });
        }
        return null;
    }

    private WorkflowStrategy Strategy => new() { Kind = "prerequisite.account", Params = new() { ["uid"] = JsonSerializer.SerializeToElement("123") } };
    private async Task<PrerequisiteResult> Prerequisite()
    {
        _run.PrerequisiteActions.Add(new() { NodeId = "node", Occurrence = 2, LoopIteration = 3, Attempt = 1,
            Kind = Strategy.Kind, AccountKey = RunStore.DeriveAccountKey("123") });
        _store.Update(_run);
        return await new BgiWorkflowPrerequisiteAdapter(_client, _store).ExecuteAsync(Strategy, _run, new("node", 0, 2, 3), CancellationToken.None);
    }
    private async Task<TerminalExecutionResult> Terminal()
    {
        _run.PendingCompletion = new() { Kind = "terminal.completionAction", Action = "closeGame" };
        _store.Update(_run);
        return await new BgiWorkflowTerminalExecutor(_client, _store).ExecuteAsync(new() { Kind = "terminal.completionAction",
            Params = new() { ["action"] = JsonSerializer.SerializeToElement("closeGame") } }, _run, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, "untyped")]
    [InlineData(true, "untyped")]
    [InlineData(false, "already_executed")]
    [InlineData(true, "already_executed")]
    public async Task UnprovedReceipt_NeverBecomesRejectedOrSuccess(bool terminal, string reply)
    {
        _reply = reply;
        if (terminal) { Assert.Equal("unknown", (await Terminal()).State); Assert.Equal("dispatching", _store.Load(_run.RunId)!.PendingCompletion!.State); }
        else { Assert.Equal(PrerequisiteStatus.Unknown, (await Prerequisite()).Status); Assert.True(_store.Load(_run.RunId)!.PrerequisiteActions.Single().SendAttempted); }
    }

    [Theory]
    [InlineData(false, "jobId")][InlineData(true, "jobId")]
    [InlineData(false, "idempotencyKey")][InlineData(true, "idempotencyKey")]
    [InlineData(false, "workflowRunId")][InlineData(true, "workflowRunId")]
    [InlineData(false, "nodeId")][InlineData(true, "nodeId")]
    [InlineData(false, "occurrence")][InlineData(true, "occurrence")]
    [InlineData(false, "iteration")][InlineData(true, "iteration")]
    [InlineData(false, "attempt")][InlineData(true, "attempt")]
    [InlineData(false, "epoch")][InlineData(true, "epoch")]
    public async Task WrongFrozenIdentity_CannotClearExecutionResponsibility(bool terminal, string fault)
    {
        _fault = fault;
        if (terminal) Assert.Equal("unknown", (await Terminal()).State);
        else Assert.Equal(PrerequisiteStatus.Unknown, (await Prerequisite()).Status);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task BusinessSuccessBeforeExecutorExit_MustWaitForExit(bool terminal)
    {
        _fault = "delayed-exit";
        if (terminal) Assert.Equal("executed", (await Terminal()).State);
        else Assert.Equal(PrerequisiteStatus.Proceed, (await Prerequisite()).Status);
        Assert.True(_queries >= 2, "business terminal before cleanup must not discharge responsibility");
    }

    [Fact]
    public async Task SucceededWithWasCancelled_PreservesActualRawTerminal()
    {
        _fault = "wasCancelled";
        Assert.Equal(PrerequisiteStatus.Proceed, (await Prerequisite()).Status);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task TypedRejection_MustPersistSeparateZeroExecutionProofAndFrozenTicket(bool terminal)
    {
        _reply = "typed";
        if (terminal) Assert.Equal("rejected", (await Terminal()).State);
        else Assert.Equal(PrerequisiteStatus.Rejected, (await Prerequisite()).Status);
        Assert.Equal("frozen-ticket", _sent.GetProperty("takeoverTicket").GetString());
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(_store.Load(_run.RunId)));
        var rec = terminal ? saved.RootElement.GetProperty("pendingCompletion") : saved.RootElement.GetProperty("prerequisiteActions")[0];
        Assert.Equal("rejected", rec.GetProperty("observedTerminal").GetString());
        Assert.True(rec.GetProperty("executionExitConfirmed").GetBoolean());
        Assert.Equal("frozen-ticket", rec.GetProperty("takeoverTicket").GetString());
        Assert.Equal(WorkflowStopAuthority.Epoch(_client.ServerEpoch), rec.GetProperty("serverRejectionEvidence").GetProperty("Epoch").GetString());
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task TypedRejectionFromWrongEpoch_RemainsUnknown(bool terminal)
    {
        _reply = "typed"; _fault = "epoch";
        if (terminal) Assert.Equal("unknown", (await Terminal()).State);
        else Assert.Equal(PrerequisiteStatus.Unknown, (await Prerequisite()).Status);
    }

    [Fact]
    public void KnownBusinessTerminalWithoutExit_CannotBeOverwrittenByNextIntent()
    {
        _run.CurrentSubmission = new() { Key = "previous", NodeId = "node", Intent = SubmitIntentState.Accepted,
            SendAttempted = true, JobId = "job-1", ObservedTerminal = "succeeded", ExecutionExitConfirmed = false };
        _store.Update(_run);
        Assert.Throws<InvalidOperationException>(() => _store.RecordIntent(_run, new() { Key = "next", NodeId = "next-node" }));
        Assert.Equal("previous", _store.Load(_run.RunId)!.CurrentSubmission!.Key);
    }
}
