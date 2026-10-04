using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class LegacyHistoricalSealIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-history-seal-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private (RunStore Store, WorkflowRunRecord Run, RecoveryAssociationRecord Link) Seed(bool outcome)
    {
        var store = new RunStore(_root);
        var run = store.CreateRun("wf", "revision");
        run.State = WorkflowRunState.Cancelled; run.StopRequested = true;
        var sub = new WorkflowSubmission
        {
            Key = "original-key", NodeId = "original-node", Occurrence = 2, LoopIteration = 3, Attempt = 1,
            WireRunId = run.WireRunId, Epoch = "123:456", Fingerprint = "original-payload", ExpiresAtUtc = "2030-01-01T00:00:00Z",
            SendAttempted = true, Intent = SubmitIntentState.Accepted, JobId = "original-job",
            ObservedTerminal = "cancelled", ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited", EffectState = "cancelled"
        };
        run.SubmissionHistory.Add(sub);
        if (outcome) run.NodeOutcomes.Add(new()
        {
            NodeId = sub.NodeId, Occurrence = sub.Occurrence, LoopIteration = sub.LoopIteration, Attempt = sub.Attempt,
            SubmissionKey = sub.Key, RawTerminal = "cancelled", Result = "cancelled"
        });
        var link = new RecoveryAssociationRecord
        {
            HistoryIndex = 0, HistoryHash = TerminalReleaseEvidence.Hash(sub), OutcomeIndex = outcome ? 0 : -1,
            OutcomeHash = outcome ? TerminalReleaseEvidence.Hash(run.NodeOutcomes[0]) : null,
            SubmissionKey = sub.Key, SubmissionIdentity = "sub:original-request:1", SendSeq = 1,
            JobId = sub.JobId, Epoch = sub.Epoch, EvidenceSource = "original-query", ObservedAtUtc = DateTimeOffset.UtcNow
        };
        return (store, run, link);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyOriginalTerminal_WithOrWithoutAssociation_PreservesOriginalsAndSeals(bool outcome, bool association)
    {
        var (store, run, link) = Seed(outcome);
        var path = Path.Combine(_root, run.RunId + ".run.json");
        File.WriteAllText(path, JsonSerializer.Serialize(run));
        var history = JsonSerializer.Serialize(run.SubmissionHistory);
        var outcomes = JsonSerializer.Serialize(run.NodeOutcomes);
        if (association) Assert.NotNull(store.TryAppendRecoveryAssociation(run.RunId, link));
        Assert.NotNull(new RunStore(_root).TrySealTerminalRun(run.RunId));
        var after = new RunStore(_root).Load(run.RunId)!;
        Assert.True(TerminalReleaseEvidence.ValidRunSeal(after));
        Assert.Equal(history, JsonSerializer.Serialize(after.SubmissionHistory));
        Assert.Equal(outcomes, JsonSerializer.Serialize(after.NodeOutcomes));
        var sealedBytes = File.ReadAllBytes(path);
        Assert.NotNull(new RunStore(_root).TrySealTerminalRun(run.RunId));
        Assert.Equal(sealedBytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("duplicate-link")]
    [InlineData("job")]
    [InlineData("epoch")]
    [InlineData("history-index")]
    [InlineData("history-hash")]
    [InlineData("outcome-index")]
    [InlineData("outcome-hash")]
    [InlineData("sequence")]
    [InlineData("orphan")]
    [InlineData("duplicate-outcome")]
    [InlineData("outcome-result")]
    [InlineData("outcome-terminal")]
    public void LegacyOriginalTerminal_ConflictingFactsCannotCreateRunSeal(string fault)
    {
        var (store, run, link) = Seed(true);
        run.RecoveryAssociations.Add(link);
        switch (fault)
        {
            case "duplicate-link": run.RecoveryAssociations.Add(JsonSerializer.Deserialize<RecoveryAssociationRecord>(JsonSerializer.Serialize(link))!); break;
            case "job": link.JobId = "foreign-job"; break;
            case "epoch": link.Epoch = "123:999"; break;
            case "history-index": link.HistoryIndex = 1; break;
            case "history-hash": link.HistoryHash = "foreign-history"; break;
            case "outcome-index": link.OutcomeIndex = 1; break;
            case "outcome-hash": link.OutcomeHash = "foreign-outcome"; break;
            case "sequence": link.SendSeq = 2; break;
            case "orphan": link.SubmissionKey = "foreign-key"; break;
            case "duplicate-outcome": run.RecoveryAssociations.Clear(); run.NodeOutcomes.Add(JsonSerializer.Deserialize<WorkflowNodeOutcome>(JsonSerializer.Serialize(run.NodeOutcomes[0]))!); break;
            case "outcome-result": run.RecoveryAssociations.Clear(); run.NodeOutcomes[0].Result = "succeeded"; break;
            case "outcome-terminal": run.RecoveryAssociations.Clear(); run.NodeOutcomes[0].RawTerminal = "succeeded"; run.NodeOutcomes[0].Result = "succeeded"; break;
        }
        var path = Path.Combine(_root, run.RunId + ".run.json");
        File.WriteAllText(path, JsonSerializer.Serialize(run));
        var original = File.ReadAllBytes(path);
        Assert.Null(new RunStore(_root).TrySealTerminalRun(run.RunId));
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
