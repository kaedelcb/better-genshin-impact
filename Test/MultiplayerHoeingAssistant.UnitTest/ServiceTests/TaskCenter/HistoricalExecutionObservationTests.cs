using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class HistoricalExecutionObservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "historical-execution-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private (RunStore Store, WorkflowRunRecord Run, RecoveryAssociationRecord Evidence, OperationRecord Op) Seed(bool acceptedIdentity, bool hasOutcome)
    {
        var store = new RunStore(_root);
        var run = store.CreateRun("wf", "revision");
        var sub = new WorkflowSubmission
        {
            Key = "historical-key", NodeId = "original-node", Occurrence = 2, LoopIteration = 3, Attempt = 1,
            WireRunId = run.WireRunId, Epoch = "123:456", Fingerprint = "local-fingerprint", ExpiresAtUtc = "2030-01-01T00:00:00Z",
            SendAttempted = true, Intent = SubmitIntentState.Accepted, JobId = "original-job",
            AcceptedSendIdentity = acceptedIdentity ? "sub:original-request:1" : null,
            OriginalRequestEvidence = new(1, new string('A', 64), "ext.task.start", "task", "config"),
            SendPermit = new(1, Guid.NewGuid().ToString("N"), true, "sub:original-request:1")
        };
        // An existing historical file with incomplete observations. No production writer manufactures these facts.
        run.State = WorkflowRunState.Unknown; run.StopRequested = true;
        run.SubmissionHistory.Add(sub);
        if (hasOutcome) run.NodeOutcomes.Add(new()
        {
            NodeId = sub.NodeId, Occurrence = sub.Occurrence, LoopIteration = sub.LoopIteration, Attempt = sub.Attempt,
            SubmissionKey = sub.Key, AcceptedSendIdentity = sub.AcceptedSendIdentity, Result = "unknown"
        });
        File.WriteAllText(Path.Combine(_root, run.RunId + ".run.json"), JsonSerializer.Serialize(run));
        var evidence = new RecoveryAssociationRecord
        {
            HistoryIndex = 0, HistoryHash = TerminalReleaseEvidence.Hash(sub), OutcomeIndex = hasOutcome ? 0 : -1,
            OutcomeHash = hasOutcome ? TerminalReleaseEvidence.Hash(run.NodeOutcomes[0]) : null,
            SubmissionKey = sub.Key, SubmissionIdentity = "sub:original-request:1", SendSeq = 1,
            JobId = sub.JobId, Epoch = sub.Epoch, EvidenceSource = "host:historical_original_exit", ObservedAtUtc = DateTimeOffset.UtcNow,
            ObservedExecution = new()
            {
                JobId = sub.JobId, Epoch = new() { ProcessId = 123, StartTicksUtc = 456 },
                IdempotencyKey = sub.Key, WorkflowRunId = run.WireRunId, NodeId = sub.NodeId,
                Occurrence = sub.Occurrence, Iteration = sub.LoopIteration, Attempt = sub.Attempt,
                RequestFingerprintVersion = 1, RequestFingerprint = sub.OriginalRequestEvidence.Fingerprint,
                RequestOperation = "ext.task.start", TaskId = "task", ConfigRevision = "config",
                State = "cancelled", ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited"
            }
        };
        var op = new OperationRecord
        {
            OperationType = OperationType.NodeExecution, RequestIdentity = "original-request", ResourceRef = "node:" + sub.NodeId,
            RunBinding = run.RunId, SubmissionIdentity = evidence.SubmissionIdentity, LastSendSeq = 1,
            TargetEpoch = sub.Epoch!, WireSubmitKey = sub.Key,
            Candidate = new() { NodeId = sub.NodeId, Occurrence = sub.Occurrence, LoopIteration = sub.LoopIteration, Attempt = sub.Attempt }
        };
        return (store, run, evidence, op);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HistoricalObservation_OriginalExitSettlesWithoutRewritingHistoryOrOutcome(bool accepted, bool outcome)
    {
        var (store, run, evidence, op) = Seed(accepted, outcome);
        var originalHistory = JsonSerializer.Serialize(run.SubmissionHistory);
        var originalOutcomes = JsonSerializer.Serialize(run.NodeOutcomes);
        Assert.True(RunStore.HasUnresolvedTerminalResponsibility(run));
        Assert.NotNull(store.TryAppendRecoveryAssociation(run.RunId, evidence));
        var reopened = new RunStore(_root);
        var after = reopened.Load(run.RunId)!;
        Assert.Equal(originalHistory, JsonSerializer.Serialize(after.SubmissionHistory));
        Assert.Equal(originalOutcomes, JsonSerializer.Serialize(after.NodeOutcomes));
        Assert.False(RunStore.HasUnresolvedTerminalResponsibility(after));
        Assert.NotNull(reopened.TrySealTerminalNode(run.RunId, op));
        after = reopened.Load(run.RunId)!;
        after.State = WorkflowRunState.Cancelled; reopened.Update(after);
        Assert.NotNull(reopened.TrySealTerminalRun(run.RunId));
        Assert.True(TerminalReleaseEvidence.ValidRunSeal(reopened.Load(run.RunId)!));
        var sealedBytes = File.ReadAllBytes(Path.Combine(_root, run.RunId + ".run.json"));
        Assert.NotNull(reopened.TryAppendRecoveryAssociation(run.RunId, evidence));
        Assert.Equal(sealedBytes, File.ReadAllBytes(Path.Combine(_root, run.RunId + ".run.json")));
    }

    [Theory]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-identity")]
    [InlineData("result")]
    [InlineData("duplicate-association")]
    [InlineData("conflicting-association")]
    public void HistoricalObservation_DirectBoundSealsRejectConflictingOutcomeSet(string fault)
    {
        var (store, run, evidence, op) = Seed(true, true);
        var sub = run.SubmissionHistory[0];
        sub.ObservedTerminal = "cancelled"; sub.EffectState = "cancelled";
        sub.ExecutionExitConfirmed = true; sub.ExecutionExitDisposition = "execution_exited";
        var outcome = run.NodeOutcomes[0]; outcome.RawTerminal = "cancelled"; outcome.Result = "cancelled";
        run.State = WorkflowRunState.Cancelled;
        if (fault.StartsWith("duplicate-association", StringComparison.Ordinal) || fault == "conflicting-association")
        {
            evidence.HistoryHash = TerminalReleaseEvidence.Hash(sub);
            evidence.OutcomeHash = TerminalReleaseEvidence.Hash(outcome);
            run.RecoveryAssociations.Add(evidence);
            if (fault == "duplicate-association")
                run.RecoveryAssociations.Add(JsonSerializer.Deserialize<RecoveryAssociationRecord>(JsonSerializer.Serialize(evidence))!);
            else evidence.JobId = "foreign-original-job";
        }
        else if (fault == "result") outcome.Result = "succeeded";
        else
        {
            var conflict = JsonSerializer.Deserialize<WorkflowNodeOutcome>(JsonSerializer.Serialize(outcome))!;
            if (fault == "duplicate-key") conflict.AcceptedSendIdentity = "sub:foreign-request:1";
            else conflict.SubmissionKey = "foreign-key";
            run.NodeOutcomes.Add(conflict);
        }
        var path = Path.Combine(_root, run.RunId + ".run.json");
        File.WriteAllText(path, JsonSerializer.Serialize(run));
        var original = File.ReadAllBytes(path);
        Assert.Null(store.TrySealTerminalNode(run.RunId, op));
        Assert.Null(store.TrySealTerminalRun(run.RunId));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void HistoricalObservation_UncertainWordCanBeAugmentedWithoutChangingOriginalOutcome()
    {
        var (store, run, evidence, op) = Seed(false, true);
        run.NodeOutcomes[0].RawTerminal = "result_unknown";
        run.SubmissionHistory[0].ObservedTerminal = "result_unknown";
        File.WriteAllText(Path.Combine(_root, run.RunId + ".run.json"), JsonSerializer.Serialize(run));
        evidence.HistoryHash = TerminalReleaseEvidence.Hash(run.SubmissionHistory[0]);
        evidence.OutcomeHash = TerminalReleaseEvidence.Hash(run.NodeOutcomes[0]);
        Assert.NotNull(store.TryAppendRecoveryAssociation(run.RunId, evidence));
        Assert.NotNull(store.TrySealTerminalNode(run.RunId, op));
        var after = store.Load(run.RunId)!;
        after.State = WorkflowRunState.Cancelled; store.Update(after);
        Assert.NotNull(store.TrySealTerminalRun(run.RunId));
        Assert.Equal("result_unknown", store.Load(run.RunId)!.NodeOutcomes[0].RawTerminal);
        Assert.Equal("result_unknown", store.Load(run.RunId)!.SubmissionHistory[0].ObservedTerminal);
    }

    [Fact]
    public void HistoricalObservation_OrdinaryWriterCannotPublishOrModifyObservedExecution()
    {
        var (store, run, evidence, _) = Seed(false, true);
        run.RecoveryAssociations.Add(evidence);
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
        Assert.Empty(store.Load(run.RunId)!.RecoveryAssociations);
        Assert.NotNull(store.TryAppendRecoveryAssociation(run.RunId, evidence));
        var after = store.Load(run.RunId)!;
        after.RecoveryAssociations[0].ObservedExecution = null;
        Assert.Throws<RunRecordConflictException>(() => store.Update(after));
        Assert.NotNull(store.Load(run.RunId)!.RecoveryAssociations[0].ObservedExecution);
    }

    [Theory]
    [InlineData("JobId", "other-job")]
    [InlineData("IdempotencyKey", "other-key")]
    [InlineData("WorkflowRunId", "other-run")]
    [InlineData("NodeId", "other-node")]
    [InlineData("Occurrence", "4")]
    [InlineData("Iteration", "4")]
    [InlineData("Attempt", "2")]
    [InlineData("RequestFingerprint", "bad-payload")]
    [InlineData("RequestFingerprintVersion", "2")]
    [InlineData("RequestOperation", "other-operation")]
    [InlineData("TaskId", "other-task")]
    [InlineData("ConfigRevision", "other-config")]
    [InlineData("State", "running")]
    [InlineData("ExecutionExitConfirmed", "false")]
    [InlineData("ExecutionExitDisposition", "unknown")]
    [InlineData("Epoch", "epoch")]
    public void HistoricalObservation_WrongOriginalProjectionNeverSettles(string field, string value)
    {
        var (store, run, evidence, _) = Seed(false, true);
        var fields = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(evidence.ObservedExecution))!.AsObject();
        fields[field] = field is "Occurrence" or "Iteration" or "Attempt" or "RequestFingerprintVersion"
            ? System.Text.Json.Nodes.JsonValue.Create(int.Parse(value))
            : field == "ExecutionExitConfirmed" ? System.Text.Json.Nodes.JsonValue.Create(false)
            : field == "Epoch" ? JsonSerializer.SerializeToNode(new BgiEpoch { ProcessId = 123, StartTicksUtc = 999 })
            : System.Text.Json.Nodes.JsonValue.Create(value);
        evidence.ObservedExecution = JsonSerializer.Deserialize<BgiJobInfo>(fields.ToJsonString());
        var original = File.ReadAllBytes(Path.Combine(_root, run.RunId + ".run.json"));
        Assert.Null(store.TryAppendRecoveryAssociation(run.RunId, evidence));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, run.RunId + ".run.json")));
        Assert.True(RunStore.HasUnresolvedTerminalResponsibility(store.Load(run.RunId)!));
    }
}
