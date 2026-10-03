using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TerminalReleaseSealTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "terminal-seal-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static WorkflowSubmission Submission(WorkflowRunRecord r, string node = "n1") => new()
    {
        Key = RunStore.DeriveSubmissionKey(r.RunId, node, 1, 0, 1), NodeId = node, Occurrence = 1, Attempt = 1,
        Epoch = "123:456", WireRunId = r.WireRunId, Fingerprint = "payload", ExpiresAtUtc = "2030-01-01T00:00:00Z",
        SendAttempted = true, Intent = SubmitIntentState.Accepted, JobId = "job-" + node,
        ObservedTerminal = "succeeded", ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
        EffectState = "succeeded", AcceptedSendIdentity = "sub:req-" + node + ":1"
    };

    private static OperationRecord Operation(WorkflowSubmission s) => new()
    {
        ResourceRef = "node:" + s.NodeId, SubmissionIdentity = s.AcceptedSendIdentity,
        RequestIdentity = "req-" + s.NodeId, LastSendSeq = 1,
        WireSubmitKey = s.Key, TargetEpoch = s.Epoch!,
        Candidate = new() { NodeId = s.NodeId, Occurrence = s.Occurrence, LoopIteration = s.LoopIteration, Attempt = s.Attempt }
    };

    private static void Outcome(WorkflowRunRecord r, WorkflowSubmission s) => r.NodeOutcomes.Add(new()
    {
        NodeId = s.NodeId, Occurrence = s.Occurrence, LoopIteration = s.LoopIteration,
        Attempt = s.Attempt, SubmissionKey = s.Key, AcceptedSendIdentity = s.AcceptedSendIdentity,
        RawTerminal = "succeeded", Result = "succeeded"
    });

    [Fact]
    public void RawTerminalWithoutExit_DoesNotReleaseNode()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); r.CurrentSubmission.ExecutionExitConfirmed = false;
        Outcome(r, r.CurrentSubmission); store.Update(r);
        Assert.Null(store.TrySealTerminalNode(r.RunId, Operation(r.CurrentSubmission)));
        Assert.False(TaskCenterHost.NodeOutcomeIsTerminal(store.Load(r.RunId)!, Operation(r.CurrentSubmission)));
    }

    [Fact]
    public void RawTerminalWithoutOriginalEpoch_DoesNotReleaseNode()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); r.CurrentSubmission.Epoch = null;
        Outcome(r, r.CurrentSubmission); store.Update(r);
        Assert.Null(store.TrySealTerminalNode(r.RunId, Operation(r.CurrentSubmission)));
        Assert.False(TaskCenterHost.NodeOutcomeIsTerminal(store.Load(r.RunId)!, Operation(r.CurrentSubmission)));
    }

    [Fact]
    public void CompletedHistoryWithUnknownEffect_RemainsUnresolved()
    {
        var r = new WorkflowRunRecord { State = WorkflowRunState.Succeeded };
        r.CompletionHistory.Add(new() { SendAttempted = true, ObservedTerminal = "succeeded", ExecutionExitConfirmed = true, EffectState = "unknown" });
        Assert.True(RunStore.HasUnresolvedTerminalResponsibility(r));
    }

    [Fact]
    public void ReplacingExitedSubmission_RetainsCompleteOriginalRecord()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); Outcome(r, r.CurrentSubmission); store.Update(r);
        store.RecordIntent(r, new() { Key = "next", NodeId = "n2" });
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, r.RunId + ".run.json")));
        Assert.True(json.RootElement.TryGetProperty("submissionHistory", out var history));
        Assert.Contains(history.EnumerateArray(), item => item.GetRawText().Contains("sub:req-n1:1"));
    }

    [Fact]
    public void EarlyNodeSeal_SurvivesReplacementAndReopen_AllowsSuccessor()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.State = WorkflowRunState.Running; r.CurrentSubmission = Submission(r);
        Outcome(r, r.CurrentSubmission); store.Update(r); var op = Operation(r.CurrentSubmission);
        var seal = store.TrySealTerminalNode(r.RunId, op); Assert.NotNull(seal);
        Assert.Null(store.TrySealTerminalRun(r.RunId));
        r = store.Load(r.RunId)!;
        store.RecordIntent(r, new() { Key = "next", NodeId = "n2" });
        var reopened = new RunStore(_dir).Load(r.RunId)!;
        Assert.True(TaskCenterHost.NodeOutcomeIsTerminal(reopened, op));
        Assert.Equal(seal, Assert.Single(reopened.NodeReleaseSeals));
        Assert.Equal("next", reopened.CurrentSubmission!.Key);
        Assert.Equal("job-n1", Assert.Single(reopened.SubmissionHistory).JobId);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("submission")]
    [InlineData("history")]
    [InlineData("completion")]
    [InlineData("seal")]
    [InlineData("stop")]
    public void RunSeal_RejectsFactChangesAndRemainsSameAfterReopen(string mutation)
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); Outcome(r, r.CurrentSubmission);
        r.State = WorkflowRunState.Succeeded; store.Update(r);
        var seal = store.TrySealTerminalRun(r.RunId); Assert.NotNull(seal);
        r = store.Load(r.RunId)!;
        switch (mutation)
        {
            case "state": r.State = WorkflowRunState.Running; break;
            case "submission": r.CurrentSubmission!.ExecutionExitConfirmed = false; break;
            case "history": r.NodeOutcomes.Clear(); break;
            case "completion": r.CompletionHistory.Add(new()); break;
            case "seal": r.TerminalRelease = null; break;
            case "stop": r.StopRequested = true; break;
        }
        Assert.Throws<RunRecordConflictException>(() => store.Update(r));
        Assert.Equal(seal, new RunStore(_dir).TrySealTerminalRun(r.RunId));
        Assert.Throws<RunRecordConflictException>(() => store.UpdateMergingIf(r.RunId, latest =>
        { latest.CurrentSubmission = new() { Key = "late", NodeId = "n2" }; return true; }, out _));
        var read = store.Load(r.RunId)!; read.Note = "writeback retry diagnostic"; store.Update(read);
        Assert.Equal(seal, store.TrySealTerminalRun(r.RunId));
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("effect")]
    [InlineData("epoch")]
    [InlineData("wire")]
    [InlineData("prerequisite")]
    [InlineData("completion")]
    public void UnresolvedOriginalFacts_PreventSeal(string missing)
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); Outcome(r, r.CurrentSubmission); r.State = WorkflowRunState.Succeeded;
        switch (missing)
        {
            case "exit": r.CurrentSubmission.ExecutionExitDisposition = null; break;
            case "effect": r.CurrentSubmission.EffectState = "unknown"; break;
            case "epoch": r.CurrentSubmission.Epoch = null; break;
            case "wire": r.WireRunId = ""; break;
            case "prerequisite": r.PrerequisiteActions.Add(new() { NodeId = "n1", Occurrence = 1, Attempt = 1, State = PrerequisiteActionState.Unknown }); break;
            case "completion": r.CompletionHistory.Add(new() { State = "executed", SendAttempted = true, EffectState = "unknown" }); break;
        }
        store.Update(r); Assert.Null(store.TrySealTerminalRun(r.RunId));
        if (missing == "completion") Assert.NotNull(store.TrySealTerminalNode(r.RunId, Operation(r.CurrentSubmission)));
        else Assert.Null(store.TrySealTerminalNode(r.RunId, Operation(r.CurrentSubmission)));
        Assert.Null(store.Load(r.RunId)!.TerminalRelease);
    }

    [Fact]
    public void SealPublishFailure_DoesNotExposeReleaseOrAdvanceRevision()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.State = WorkflowRunState.Cancelled; store.Update(r); var revision = r.RecordRevision;
        store.PublishFaultForTest = rec => rec.TerminalRelease is null ? null : new IOException("seal publish failed");
        Assert.Throws<IOException>(() => store.TrySealTerminalRun(r.RunId));
        Assert.Null(store.Load(r.RunId)!.TerminalRelease);
        Assert.Equal(revision, store.Load(r.RunId)!.RecordRevision);
        store.PublishFaultForTest = null; Assert.NotNull(store.TrySealTerminalRun(r.RunId));
    }

    [Fact]
    public void OrdinaryWriter_CannotForgeSealOrModifyReleasedNodeHistory()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.State = WorkflowRunState.Running; r.CurrentSubmission = Submission(r); Outcome(r, r.CurrentSubmission); store.Update(r);
        var op = Operation(r.CurrentSubmission); Assert.NotNull(store.TrySealTerminalNode(r.RunId, op));
        r = store.Load(r.RunId)!; r.CurrentSubmission!.Fingerprint = "other";
        Assert.Throws<RunRecordConflictException>(() => store.Update(r));
        r = store.Load(r.RunId)!; r.NodeReleaseSeals.Clear(); Assert.Throws<RunRecordConflictException>(() => store.Update(r));
        r = store.Load(r.RunId)!; r.State = WorkflowRunState.Succeeded;
        r.TerminalRelease = new("forged", "run", r.RunId, r.WireRunId, r.RecordRevision + 1, TerminalReleaseEvidence.RunHash(r), null);
        Assert.Throws<RunRecordConflictException>(() => store.Update(r));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("attempt")]
    [InlineData("sendSeq")]
    [InlineData("node")]
    public void NodeSeal_CannotReleaseDifferentOriginalOperation(string drift)
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); Outcome(r, r.CurrentSubmission); store.Update(r);
        var op = Operation(r.CurrentSubmission); Assert.NotNull(store.TrySealTerminalNode(r.RunId, op));
        switch (drift)
        {
            case "epoch": op.TargetEpoch = "123:999"; break;
            case "attempt": op.Candidate!.Attempt++; break;
            case "sendSeq": op.LastSendSeq++; break;
            case "node": op.Candidate!.NodeId = "n2"; break;
        }
        Assert.False(TaskCenterHost.NodeOutcomeIsTerminal(store.Load(r.RunId)!, op));
        Assert.Null(store.TrySealTerminalNode(r.RunId, op));
    }

    [Fact]
    public void ArchivedOriginalWireCannotChangeBeforeSuccessorPreparation()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); Outcome(r, r.CurrentSubmission); store.Update(r);
        var wire = r.WireRunId;
        store.RecordIntent(r, new() { Key = "next", NodeId = "n2" });
        Assert.Equal(wire, Assert.Single(r.SubmissionHistory).WireRunId);
        r.WireRunId = Guid.NewGuid().ToString("N");
        Assert.Throws<RunRecordConflictException>(() => store.Update(r));
        Assert.Equal(wire, store.Load(r.RunId)!.WireRunId);
    }

    [Fact]
    public void FileAndRecordIdentityMismatch_CannotSealOrCreateOtherRun()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        var original = r.RunId; r.RunId = "other-run"; r.State = WorkflowRunState.Cancelled;
        File.WriteAllText(Path.Combine(_dir, original + ".run.json"), JsonSerializer.Serialize(r));
        Assert.Null(store.TrySealTerminalRun(original));
        Assert.Null(store.TrySealTerminalNode(original, Operation(Submission(r))));
        Assert.False(File.Exists(Path.Combine(_dir, "other-run.run.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HistoricalWaitIsDischargedOnlyByLaterSameOccurrenceWithExit(bool exit)
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.NodeOutcomes.Add(new() { NodeId = "n1", Occurrence = 1, Result = WorkflowRunner.LocalWaitResultWord });
        r.CurrentSubmission = Submission(r); r.CurrentSubmission.ExecutionExitConfirmed = exit;
        Outcome(r, r.CurrentSubmission); r.State = WorkflowRunState.Succeeded; store.Update(r);
        var seal = store.TrySealTerminalRun(r.RunId);
        if (exit) Assert.NotNull(seal); else Assert.Null(seal);
        Assert.Equal(WorkflowRunner.LocalWaitResultWord, store.Load(r.RunId)!.NodeOutcomes[0].Result);
    }

    [Fact]
    public void CompletionHistoryWithoutObservedEffect_DoesNotSealRun()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.State = WorkflowRunState.Succeeded; r.CompletionHistory.Add(new() { State = "executed" }); store.Update(r);
        Assert.Null(store.TrySealTerminalRun(r.RunId));
    }

    [Theory]
    [InlineData("prerequisite")]
    [InlineData("pending")]
    [InlineData("history")]
    public void TypedRejectionDischargesOriginalResponsibilityWithoutInventingJob(string stage)
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev"); r.State = WorkflowRunState.Failed;
        var operation = stage == "prerequisite" ? BgiExternalClient.ExternalOperations.PrerequisiteAccount
            : BgiExternalClient.ExternalOperations.TerminalCompletionAction;
        var proof = new ServerRejectionEvidence("123:456", "typed-key", "typed-payload", operation);
        if (stage == "prerequisite") r.PrerequisiteActions.Add(new()
        {
            NodeId = "n1", Occurrence = 1, Attempt = 1, Kind = "prerequisite.account", Epoch = proof.Epoch,
            WireRunId = r.WireRunId, TakeoverTicket = "ticket", IdempotencyKey = proof.Key, Fingerprint = proof.Fingerprint,
            ExpiresAtUtc = "2030-01-01T00:00:00Z", SendAttempted = true, ObservedTerminal = "rejected",
            ExecutionExitConfirmed = true, ExecutionExitDisposition = "server_rejected_before_acceptance",
            EffectState = "not_executed", ServerRejectionEvidence = proof, State = PrerequisiteActionState.Failed
        });
        else
        {
            var c = new PendingCompletionRecord
            {
                ActionId = "$flow#0", Kind = "terminal.completionAction", Epoch = proof.Epoch, WireRunId = r.WireRunId,
                TakeoverTicket = "ticket", IdempotencyKey = proof.Key, Fingerprint = proof.Fingerprint,
                ExpiresAtUtc = "2030-01-01T00:00:00Z", SendAttempted = true, ObservedTerminal = "rejected",
                ExecutionExitConfirmed = true, ExecutionExitDisposition = "server_rejected_before_acceptance",
                EffectState = "not_executed", ServerRejectionEvidence = proof, State = "rejected"
            };
            if (stage == "pending") r.PendingCompletion = c; else r.CompletionHistory.Add(c);
        }
        store.Update(r); Assert.NotNull(store.TrySealTerminalRun(r.RunId));
        var reopened = new RunStore(_dir);
        Assert.Empty(reopened.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Failed, reopened.Load(r.RunId)!.State);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("fingerprint")]
    [InlineData("wire")]
    [InlineData("epoch")]
    [InlineData("effect")]
    public void TypedRejectionMismatchKeepsOriginalResponsibility(string mismatch)
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev"); r.State = WorkflowRunState.Failed;
        var c = new PendingCompletionRecord
        {
            ActionId = "$flow#0", Kind = "terminal.completionAction", Epoch = "123:456", WireRunId = r.WireRunId,
            IdempotencyKey = "key", Fingerprint = "fp", ExpiresAtUtc = "2030-01-01T00:00:00Z", SendAttempted = true,
            ObservedTerminal = "rejected", ExecutionExitConfirmed = true, ExecutionExitDisposition = "server_rejected_before_acceptance",
            EffectState = "not_executed", State = "rejected",
            ServerRejectionEvidence = new("123:456", "key", "fp", BgiExternalClient.ExternalOperations.TerminalCompletionAction)
        };
        switch (mismatch)
        {
            case "operation": c.ServerRejectionEvidence = c.ServerRejectionEvidence with { Operation = BgiExternalClient.ExternalOperations.TaskStart }; break;
            case "fingerprint": c.Fingerprint = "other"; break;
            case "wire": c.WireRunId = "other"; break;
            case "epoch": c.Epoch = "123:999"; break;
            case "effect": c.EffectState = "unknown"; break;
        }
        r.CompletionHistory.Add(c); store.Update(r); Assert.Null(store.TrySealTerminalRun(r.RunId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoppedNodeWithoutOrdinaryOutcome_RequiresOriginalExit(bool exit)
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); r.CurrentSubmission.ExecutionExitConfirmed = exit;
        r.State = WorkflowRunState.Cancelled; r.StopRequested = true; store.Update(r);
        var seal = store.TrySealTerminalNode(r.RunId, Operation(r.CurrentSubmission));
        if (exit) Assert.NotNull(seal); else Assert.Null(seal);
        Assert.Empty(store.Load(r.RunId)!.NodeOutcomes);
    }

    [Fact]
    public void StoppedUnknownOutcomeWithOriginalExit_PreservesWordsAndReleases()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); r.State = WorkflowRunState.Cancelled; r.StopRequested = true;
        r.NodeOutcomes.Add(new() { NodeId = "n1", Occurrence = 1, Result = "unknown" }); store.Update(r);
        Assert.NotNull(store.TrySealTerminalNode(r.RunId, Operation(r.CurrentSubmission)));
        Assert.NotNull(store.TrySealTerminalRun(r.RunId));
        Assert.Equal("unknown", store.Load(r.RunId)!.NodeOutcomes.Single().Result);
        Assert.Equal("succeeded", store.Load(r.RunId)!.CurrentSubmission!.ObservedTerminal);
    }

    [Fact]
    public void StoppedUnknownOutcomeWithoutCorrespondingSubmission_RemainsUnsealed()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.State = WorkflowRunState.Cancelled; r.StopRequested = true;
        r.NodeOutcomes.Add(new() { NodeId = "n1", Occurrence = 1, Result = "unknown" }); store.Update(r);
        Assert.Null(store.TrySealTerminalRun(r.RunId));
    }
}
