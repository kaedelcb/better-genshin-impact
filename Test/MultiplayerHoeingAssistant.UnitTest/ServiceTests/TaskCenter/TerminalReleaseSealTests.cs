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
    public void OrdinaryWriter_CannotAppendRecoveryAssociation()
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("wf", "rev");
        var revision = run.RecordRevision;
        run.RecoveryAssociations.Add(new()
        {
            SubmissionKey = "key", SubmissionIdentity = "sub:forged:1", SendSeq = 1,
            JobId = "job", Epoch = "123:456", EvidenceSource = "claimed-query"
        });
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
        Assert.Empty(store.Load(run.RunId)!.RecoveryAssociations);
        Assert.Equal(revision, store.Load(run.RunId)!.RecordRevision);
    }

    [Fact]
    public void OrdinaryWriter_CannotCreateNewRecordWithRecoveryAssociation()
    {
        var store = new RunStore(_dir);
        var run = new WorkflowRunRecord { RunId = "forged-run", RecordRevision = 0 };
        run.RecoveryAssociations.Add(new() { SubmissionIdentity = "sub:forged:1" });
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
        Assert.Null(store.Load(run.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyRunSeal_WithoutRecoveryAssociationsProperty_RemainsValidAfterReopen(bool includesEmptyAssociationField)
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("wf", "rev");
        run.State = WorkflowRunState.Cancelled;
        store.Update(run);
        var facts = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(run))!;
        facts.TerminalRelease = null; facts.RecordRevision = 0; facts.UpdatedAt = default; facts.Note = null;
        var legacyFacts = JsonSerializer.Serialize(facts);
        if (!includesEmptyAssociationField) legacyFacts = legacyFacts.Replace(",\"recoveryAssociations\":[]", "", StringComparison.Ordinal);
        var seal = new TerminalReleaseSeal("legacy-seal", "run", run.RunId, run.WireRunId,
            run.RecordRevision, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyFacts))), null);
        run.TerminalRelease = seal;
        var legacyRun = JsonSerializer.Serialize(run);
        if (!includesEmptyAssociationField) legacyRun = legacyRun.Replace(",\"recoveryAssociations\":[]", "", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_dir, run.RunId + ".run.json"), legacyRun);
        Assert.Equal(seal, new RunStore(_dir).TrySealTerminalRun(run.RunId));
        Assert.True(TerminalReleaseEvidence.ValidRunSeal(new RunStore(_dir).Load(run.RunId)!));
    }

    [Fact]
    public void RawTerminalWithoutExit_DoesNotReleaseNode()
    {
        var store = new RunStore(_dir); var r = store.CreateRun("wf", "rev");
        r.CurrentSubmission = Submission(r); r.CurrentSubmission.ExecutionExitConfirmed = false;
        Outcome(r, r.CurrentSubmission); store.Update(r);
        Assert.Null(store.TrySealTerminalNode(r.RunId, Operation(r.CurrentSubmission)));
        Assert.False(TaskCenterHost.NodeOutcomeIsTerminal(store.Load(r.RunId)!, Operation(r.CurrentSubmission)));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("epoch")]
    [InlineData("sequence")]
    [InlineData("identity")]
    [InlineData("historyHash")]
    [InlineData("historyIndex")]
    [InlineData("outcomeHash")]
    public void RecoveryAssociation_MismatchedOriginalFacts_MustNotPublish(string drift)
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("wf", "rev");
        var submission = Submission(run);
        submission.AcceptedSendIdentity = null;
        run.CurrentSubmission = submission; Outcome(run, submission); store.Update(run);
        store.RecordIntent(run, new() { Key = "next", NodeId = "n2" });
        var association = new RecoveryAssociationRecord
        {
            HistoryIndex = 0, HistoryHash = TerminalReleaseEvidence.Hash(run.SubmissionHistory[0]),
            OutcomeIndex = 0, OutcomeHash = TerminalReleaseEvidence.Hash(run.NodeOutcomes[0]),
            SubmissionKey = submission.Key, SubmissionIdentity = "sub:req-n1:1", SendSeq = 1,
            JobId = submission.JobId, Epoch = submission.Epoch, EvidenceSource = "original-query",
            ObservedAtUtc = DateTimeOffset.UtcNow
        };
        switch (drift)
        {
            case "job": association.JobId = "different-job"; break;
            case "epoch": association.Epoch = "123:999"; break;
            case "sequence": association.SendSeq = 0; break;
            case "identity": association.SubmissionIdentity = "sub:req-n1:2"; break;
            case "historyHash": association.HistoryHash = "different-history"; break;
            case "historyIndex": association.HistoryIndex = 1; break;
            case "outcomeHash": association.OutcomeHash = "different-outcome"; break;
        }
        var before = File.ReadAllBytes(Path.Combine(_dir, run.RunId + ".run.json"));
        Assert.Null(store.TryAppendRecoveryAssociation(run.RunId, association));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_dir, run.RunId + ".run.json")));
        Assert.Empty(store.Load(run.RunId)!.RecoveryAssociations);
    }

    [Fact]
    public void RecoveryAssociation_SealsOriginalHistoryWithoutRewritingSubmissionOrOutcome()
    {
        var store = new RunStore(_dir);
        var run = store.CreateRun("wf", "rev");
        var submission = Submission(run); var operation = Operation(submission);
        submission.AcceptedSendIdentity = null;
        run.CurrentSubmission = submission; Outcome(run, submission); store.Update(run);
        store.RecordIntent(run, new() { Key = "next", NodeId = "n2" });
        var historyBefore = JsonSerializer.Serialize(run.SubmissionHistory);
        var outcomesBefore = JsonSerializer.Serialize(run.NodeOutcomes);
        Assert.Null(store.TrySealTerminalNode(run.RunId, operation));
        var association = new RecoveryAssociationRecord
        {
            HistoryIndex = 0, HistoryHash = TerminalReleaseEvidence.Hash(run.SubmissionHistory[0]),
            OutcomeIndex = 0, OutcomeHash = TerminalReleaseEvidence.Hash(run.NodeOutcomes[0]),
            SubmissionKey = submission.Key, SubmissionIdentity = operation.SubmissionIdentity, SendSeq = 1,
            JobId = submission.JobId, Epoch = submission.Epoch, EvidenceSource = "original-query",
            ObservedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.NotNull(store.TryAppendRecoveryAssociation(run.RunId, association));
        Assert.NotNull(store.TrySealTerminalNode(run.RunId, operation));
        var reopened = new RunStore(_dir).Load(run.RunId)!;
        Assert.True(TaskCenterHost.NodeOutcomeIsTerminal(reopened, operation));
        Assert.Equal(historyBefore, JsonSerializer.Serialize(reopened.SubmissionHistory));
        Assert.Equal(outcomesBefore, JsonSerializer.Serialize(reopened.NodeOutcomes));
        Assert.NotNull(store.TryAppendRecoveryAssociation(run.RunId, association));
        reopened.RecoveryAssociations[0].JobId = "other-job";
        Assert.Throws<RunRecordConflictException>(() => store.Update(reopened));
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
