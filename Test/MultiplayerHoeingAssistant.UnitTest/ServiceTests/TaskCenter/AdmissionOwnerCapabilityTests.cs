using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class AdmissionOwnerCapabilityTests
{
    [Fact]
    public void AcquiredCapabilityCannotBeSilentlyAppliedToAnotherStoreRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "admission-owner-root-" + Guid.NewGuid().ToString("N"));
        var original = new ArbitrationLeaseStore(Path.Combine(root, "original"));
        var acquired = original.TryAcquire("original-owner");
        Assert.True(acquired.Success);
        var originalBytes = File.ReadAllBytes(Path.Combine(root, "original", "arbitration-lease.json"));
        Assert.Throws<ArgumentException>(() => new ArbitrationAdmissionService(
            new ArbitrationLeaseStore(Path.Combine(root, "other")), new AdmissionHooks(), ownership: acquired.Ownership));
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(root, "original", "arbitration-lease.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "other")));
    }

    [Fact]
    public async Task LostOwner_CompletionRetainsOriginalPendingIdentityAndObservedCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "admission-owner-pending-" + Guid.NewGuid().ToString("N"));
        var store = new ArbitrationLeaseStore(directory);
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Accepted("test:accepted", "run-original", "job-original")),
            TakeoverPersist = _ => Task.FromResult<string?>(null),
        };
        var facade = new ArbitrationAdmissionService(store, hooks);
        Assert.True(facade.EnsureOwnership("owner-original").Success);
        var request = Request();
        request.OperationType = OperationType.ExternalStart;
        var accepted = await facade.SubmitAsync(request);
        Assert.Equal(AdmissionResultKind.Accepted, accepted.Kind);
        var owner = store.Read().File!;
        Assert.True(store.TryRelease(owner.Lease!.LeaseId, owner.Lease.OwnerEpoch, owner.Revision).Success);
        var successor = new ArbitrationAdmissionService(new ArbitrationLeaseStore(directory), hooks);
        Assert.True(successor.EnsureOwnership("owner-successor").Success);
        var path = Path.Combine(directory, "arbitration-lease.json");
        var before = File.ReadAllBytes(path);
        var cancelled = ExternalStartCompletion.CancelledWith("original-cancel", "test:original-observation", DateTimeOffset.UtcNow);
        var result = await facade.SettleCompletionAsync(request.RequestIdentity, accepted.SubmissionIdentity!, accepted.SendSeq, cancelled);
        Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
        Assert.Equal(accepted.SubmissionIdentity, result.SubmissionIdentity);
        Assert.Equal(accepted.SendSeq, result.SendSeq);
        Assert.Equal(ExecutionDisposition.Cancelled, result.ExecutionDisposition);
        Assert.Equal("original-cancel", result.RawTerminal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SameFacade_ReacquisitionDoesNotUpgradeTerminalAlreadyWaiting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "admission-owner-reacquire-" + Guid.NewGuid().ToString("N"));
        var store = new ArbitrationLeaseStore(directory);
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            Sender = _ => Task.FromResult<SendOutcome>(new SendOutcome.Accepted("test:accepted", "run-original")),
            TakeoverPersist = _ => Task.FromResult<string?>(null),
            TakeoverTerminalConfirmed = (_, _) => true,
        };
        var facade = new ArbitrationAdmissionService(store, hooks);
        Assert.True(facade.EnsureOwnership("original-owner").Success);
        var request = Request();
        Assert.Equal(AdmissionResultKind.Accepted, (await facade.SubmitAsync(request)).Kind);
        var gate = (SemaphoreSlim)typeof(ArbitrationAdmissionService)
            .GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(facade)!;
        await gate.WaitAsync();
        Task<AdmissionResult> waiting;
        byte[] before;
        try
        {
            waiting = facade.MarkOperationTerminalAsync(request.RequestIdentity, "test:terminal");
            Assert.False(waiting.IsCompleted);
            var original = store.Read().File!;
            Assert.True(store.TryRelease(original.Lease!.LeaseId, original.Lease.OwnerEpoch, original.Revision).Success);
            Assert.True(facade.EnsureOwnership("new-owner").Success);
            before = File.ReadAllBytes(Path.Combine(directory, "arbitration-lease.json"));
        }
        finally { gate.Release(); }
        Assert.Equal(AdmissionResultKind.Error, (await waiting).Kind);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "arbitration-lease.json")));
        Assert.Equal(AdmissionResultKind.Accepted,
            (await facade.MarkOperationTerminalAsync(request.RequestIdentity, "test:terminal")).Kind);
    }

    private static AdmissionRequest Request() => new()
    {
        Namespace = "manual", OperationType = OperationType.FlowRegistration,
        Candidate = new ArbitrationCandidate
        {
            Scope = "bgi:inst:ep1", Namespace = "manual", WorkflowId = "owner-wf",
            TriggerOccurrenceId = "owner-test:" + Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "payload", ResourceRef = "owner-wf", Intent = "start", Tier = ArbitrationTier.Plan,
        },
    };

    [Theory]
    [InlineData("new-submit", false)]
    [InlineData("terminal", false)]
    [InlineData("new-submit", true)]
    [InlineData("terminal", true)]
    public async Task OriginalFacade_AfterSuccessorAcquires_CannotBorrowSuccessor(string entry, bool takeover)
    {
        var directory = Path.Combine(Path.GetTempPath(), "admission-owner-capability-" + Guid.NewGuid().ToString("N"));
        var mono = TimeSpan.Zero;
        var storeA = new ArbitrationLeaseStore(directory, monotonic: () => mono);
        var sends = 0;
        var hooks = new AdmissionHooks
        {
            BgiEpochProvider = () => "ep1",
            Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("test:accepted", "run-original")); },
            TakeoverPersist = _ => Task.FromResult<string?>(null),
            TakeoverTerminalConfirmed = (_, _) => true,
        };
        var a = new ArbitrationAdmissionService(storeA, hooks);
        Assert.True(a.EnsureOwnership("owner-A").Success);
        var request = Request();
        if (entry == "terminal") Assert.Equal(AdmissionResultKind.Accepted, (await a.SubmitAsync(request)).Kind);
        var original = storeA.Read().File!;
        if (!takeover) Assert.True(storeA.TryRelease(original.Lease!.LeaseId, original.Lease.OwnerEpoch, original.Revision).Success);
        var storeB = new ArbitrationLeaseStore(directory, monotonic: () => mono);
        var b = new ArbitrationAdmissionService(storeB, hooks);
        LeaseTakeoverEvidence? evidence = null;
        if (takeover)
        {
            var observer = new LeaseTakeoverObserver(() => mono);
            Assert.Null(observer.Observe(storeB.Read()));
            mono += TimeSpan.FromSeconds(20);
            evidence = observer.Observe(storeB.Read());
            Assert.NotNull(evidence);
        }
        Assert.True(b.EnsureOwnership("owner-B", 60, evidence).Success);
        var path = Path.Combine(directory, "arbitration-lease.json");
        var before = File.ReadAllBytes(path);
        var count = sends;
        var result = entry == "new-submit" ? await a.SubmitAsync(Request())
            : await a.MarkOperationTerminalAsync(request.RequestIdentity, "test:terminal");
        Assert.NotEqual(AdmissionResultKind.Accepted, result.Kind);
        Assert.Equal(count, sends);
        Assert.Equal(before, File.ReadAllBytes(path));
        // The successor may lawfully settle the original accepted identity.
        if (entry == "terminal") Assert.Equal(AdmissionResultKind.Accepted,
            (await b.MarkOperationTerminalAsync(request.RequestIdentity, "test:terminal")).Kind);
        else Assert.Equal(AdmissionResultKind.Accepted, (await b.SubmitAsync(Request())).Kind);
    }
}
