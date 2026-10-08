using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.OneDragon;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

[Collection("TaskTakeoverIncident")]
public sealed class HoeingMissingResourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hoeing-missing-resource-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingScriptRootAndPathingAreFailures()
    {
        Assert.Throws<HoeingMissingResourceException>(() => HoeingMissingResourceBoundary.RequireScriptDirectory(_root, CancellationToken.None));
        Directory.CreateDirectory(_root);
        Assert.Throws<HoeingMissingResourceException>(() => HoeingMissingResourceBoundary.RequireScriptDirectory(_root, CancellationToken.None));
    }

    [Fact]
    public void PresentNormalPathingMayBeEmptyWithoutReclassifyingFilteredSchedules()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pathing"));
        HoeingMissingResourceBoundary.RequireScriptDirectory(_root, CancellationToken.None);
    }

    [Fact]
    public void NoFixedRouteInputIsFailureAndExistingFixedInputIsAccepted()
    {
        Assert.Throws<HoeingMissingResourceException>(() => HoeingMissingResourceBoundary.RequireFixedRoutes(0, CancellationToken.None));
        HoeingMissingResourceBoundary.RequireFixedRoutes(1, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UserCancellationPrecedesMissingResourceAndOwnerChecks(bool script)
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var ownerChecked = false;
        Action owner = () => ownerChecked = true;
        Assert.Throws<OperationCanceledException>(() =>
        {
            if (script) HoeingMissingResourceBoundary.RequireScriptDirectory(_root, cts.Token, owner);
            else HoeingMissingResourceBoundary.RequireFixedRoutes(0, cts.Token, owner);
        });
        Assert.False(ownerChecked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LostOwnerPrecedesMissingResourceFailure(bool script)
    {
        Action owner = () => throw new OperationCanceledException("execution owner was stopped");
        Assert.Throws<OperationCanceledException>(() =>
        {
            if (script) HoeingMissingResourceBoundary.RequireScriptDirectory(_root, CancellationToken.None, owner);
            else HoeingMissingResourceBoundary.RequireFixedRoutes(0, CancellationToken.None, owner);
        });
    }

    [Fact]
    public void SpecificMissingResourceFailureEscapesCleanupWithItsOriginalIdentity()
    {
        var original = Assert.Throws<HoeingMissingResourceException>(() =>
            HoeingMissingResourceBoundary.RequireFixedRoutes(0, CancellationToken.None));
        var propagated = Assert.Throws<HoeingMissingResourceException>(() =>
            HoeingMissingResourceBoundary.RethrowAfterCleanup(original, CancellationToken.None));
        Assert.Same(original, propagated);
    }

    [Fact]
    public void CancellationAlsoPrecedesRethrowingAfterCleanup()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => HoeingMissingResourceBoundary.RethrowAfterCleanup(
            new HoeingMissingResourceException("missing"), cts.Token));
    }

    [Fact]
    public void OtherNativeFailureAndNormalReturnPoliciesAreUnchanged()
    {
        HoeingMissingResourceBoundary.RethrowAfterCleanup(null, CancellationToken.None);
        HoeingMissingResourceBoundary.RethrowAfterCleanup(new IOException("unrelated existing native error"), CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EscapedMissingResourceProducesActualFailedRegistryJobAfterCleanup(bool script)
    {
        // Reuse the coordinator's real registry/owned-root fixture, with no TaskRunner.Init or game capture.
        using var coordinator = new BgiTaskCoordinator(isSlotFree: static () => true,
            publish: static (_, _) => { }, logger: NullLogger.Instance, slotPollInterval: TimeSpan.FromMilliseconds(5));
        var identity = new JobExecutionIdentity(Guid.NewGuid(), "missing-resource", 0, Occurrence: 0, Attempt: 1);
        var submitted = coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(0, null, null, 0, (handle, token) =>
        {
            using var scope = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "missing hoeing resource", JobSource.Ext,
                JobId: handle, WorkflowRunId: identity.WorkflowRunId, NodeId: identity.NodeId, Iteration: identity.Iteration,
                Occurrence: identity.Occurrence, Attempt: identity.Attempt));
            Exception? failure = null;
            try
            {
                if (script) HoeingMissingResourceBoundary.RequireScriptDirectory(_root, token, scope.ThrowIfStopped);
                else HoeingMissingResourceBoundary.RequireFixedRoutes(0, token, scope.ThrowIfStopped);
            }
            catch (HoeingMissingResourceException ex)
            {
                failure = ex;
                scope.Observe(TaskRunResult.Failed); // Same failed-result observation as the production TaskRunner catch.
            }
            // This is the same specialization used after native cleanup; returning normally here would mark success.
            HoeingMissingResourceBoundary.RethrowAfterCleanup(failure, token, scope.ThrowIfStopped);
            return Task.FromResult(false);
        })
        {
            RegistryKind = JobKind.Solo, RegistryName = "missing hoeing resource",
            IdempotencyKey = Guid.NewGuid().ToString("N"), PayloadFingerprint = "missing-hoeing-resource-test",
            Identity = identity,
        });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (JobRegistry.Instance.Query(submitted.TaskHandle)?.ExitConfirmed != true && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        var job = JobRegistry.Instance.Query(submitted.TaskHandle)!;
        Assert.Equal(JobState.Failed, job.State);
        Assert.NotEqual(JobState.Succeeded, job.State);
        Assert.Equal(JobErrorCodes.TaskStartFailed, job.ErrorCode);
        Assert.True(job.ExitConfirmed);
        Assert.Equal("execution_exited", job.ExitDisposition);
        Assert.Equal("failed", coordinator.QueryItemStatus(submitted.TaskHandle).Status);
    }

    [Fact]
    public void RealOwnerCancellationWinsMissingResourceAndOneDragonCannotRunItsSuccessTail()
    {
        using var scope = ExecutionScope.Start(new JobDescriptor(JobKind.OneDragon, "cancelled hoeing resource", JobSource.OneDragonInternal));
        var failure = Assert.Throws<HoeingMissingResourceException>(() =>
            HoeingMissingResourceBoundary.RequireFixedRoutes(0, CancellationToken.None, scope.ThrowIfStopped));
        ExecutionScope.StopActive(manual: true);
        Assert.Throws<OperationCanceledException>(() =>
            HoeingMissingResourceBoundary.RethrowAfterCleanup(failure, scope.Token, scope.ThrowIfStopped));
        Assert.Equal(TaskRunResult.Cancelled, OneDragonRootResultRule.DecideRootResult(scope.Result, finishMark: true));
        Assert.False(OneDragonRootResultRule.ShouldRunTail(scope.Result));
        Assert.Equal(0, scope.FailureCount);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
