using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class OriginalNodeSendRoundTests
{
    private static (WorkflowRunRecord Run, WorkflowSubmission Sub, LeaseHandoffSegment Handoff) Seed(bool archived = false)
    {
        var sub = new WorkflowSubmission
        {
            Key = "wire-key", NodeId = "node", Occurrence = 2, LoopIteration = 3, Attempt = 1,
            Epoch = "123:456", SendAttempted = true, Intent = SubmitIntentState.Submitted,
            OriginalRequestEvidence = new(1, new string('A', 64), "ext.task.start", "task", "revision"),
            SendPermit = new(1, Guid.NewGuid().ToString("N"), true, "sub:request:1")
        };
        var run = new WorkflowRunRecord { RunId = "run", WireRunId = "wire-run", CurrentSubmission = sub };
        var op = new OperationRecord
        {
            RequestIdentity = "request", OperationType = OperationType.NodeExecution, RunBinding = "run",
            WireSubmitKey = sub.Key, TargetEpoch = sub.Epoch, SubmissionIdentity = "sub:request:1", LastSendSeq = 1,
            Candidate = new ArbitrationCandidate { NodeId = sub.NodeId, Occurrence = 2, LoopIteration = 3, Attempt = 1 }
        };
        var handoff = new LeaseHandoffSegment();
        if (archived) handoff.ArchivedOperations.Add(new() { Operation = op, ArchivedAtUtc = DateTimeOffset.UtcNow });
        else handoff.Operations.Add(op);
        handoff.PreObservations.Add(new()
        {
            SubmissionIdentity = op.SubmissionIdentity, SendSeq = 1, OperationType = OperationType.NodeExecution,
            TargetEpoch = sub.Epoch, WireSubmitKey = sub.Key, QueryBasis = "wire-submit-key:" + sub.Key,
            OwnerEpoch = "owner", CreatedAtUtc = DateTimeOffset.UtcNow
        });
        return (run, sub, handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalRound_ModernConsumedAnchorResolvesHotOrArchived(bool archived)
    {
        var (run, sub, handoff) = Seed(archived);
        var result = TaskCenterHost.ResolveOriginalNodeSendIdentity(run, sub, handoff);
        Assert.NotNull(result);
        Assert.Equal("sub:request:1", result.SubmissionIdentity);
        Assert.Equal(1, result.SendSeq);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("missing-anchor")]
    [InlineData("nonce")]
    [InlineData("unconsumed")]
    [InlineData("version")]
    [InlineData("accepted-conflict")]
    [InlineData("old-round")]
    [InlineData("no-preobservation")]
    [InlineData("duplicate-preobservation")]
    [InlineData("wrong-preobservation")]
    [InlineData("missing-payload")]
    [InlineData("epoch")]
    public void OriginalRound_MissingOrConflictingAnchorNeverSelectsLatest(string drift)
    {
        var (run, sub, handoff) = Seed();
        switch (drift)
        {
            case "legacy": sub.SendPermit = null; break;
            case "missing-anchor": sub.SendPermit = sub.SendPermit! with { OriginalSendIdentity = null }; break;
            case "nonce": sub.SendPermit = sub.SendPermit! with { Nonce = "invented" }; break;
            case "unconsumed": sub.SendPermit = sub.SendPermit! with { Consumed = false }; break;
            case "version": sub.SendPermit = sub.SendPermit! with { Version = 0 }; break;
            case "accepted-conflict": sub.AcceptedSendIdentity = "sub:request:2"; break;
            case "old-round": handoff.Operations[0].LastSendSeq = 2; handoff.Operations[0].SubmissionIdentity = "sub:request:2"; break;
            case "no-preobservation": handoff.PreObservations.Clear(); break;
            case "duplicate-preobservation": handoff.PreObservations.Add(handoff.PreObservations[0]); break;
            case "wrong-preobservation": handoff.PreObservations[0].WireSubmitKey = "other"; break;
            case "missing-payload": sub.OriginalRequestEvidence = null; break;
            case "epoch": sub.Epoch = null; break;
        }
        Assert.Null(TaskCenterHost.ResolveOriginalNodeSendIdentity(run, sub, handoff));
    }

    [Fact]
    public void OriginalRound_HotArchivedDuplicateIsAmbiguous()
    {
        var (run, sub, handoff) = Seed();
        handoff.ArchivedOperations.Add(new() { Operation = handoff.Operations[0], ArchivedAtUtc = DateTimeOffset.UtcNow });
        Assert.Null(TaskCenterHost.ResolveOriginalNodeSendIdentity(run, sub, handoff));
    }

    [Fact]
    public void OriginalRound_NewRoundWithoutEarlierDischargeIsUnknown()
    {
        var (run, sub, handoff) = Seed();
        sub.SendPermit = sub.SendPermit! with { OriginalSendIdentity = "sub:request:2" };
        handoff.Operations[0].SubmissionIdentity = "sub:request:2";
        handoff.Operations[0].LastSendSeq = 2;
        handoff.PreObservations.Add(new()
        {
            SubmissionIdentity = "sub:request:2", SendSeq = 2, OperationType = OperationType.NodeExecution,
            TargetEpoch = sub.Epoch!, WireSubmitKey = sub.Key, QueryBasis = "wire-submit-key:" + sub.Key,
            OwnerEpoch = "owner", CreatedAtUtc = DateTimeOffset.UtcNow
        });
        Assert.Null(TaskCenterHost.ResolveOriginalNodeSendIdentity(run, sub, handoff));
    }
}
