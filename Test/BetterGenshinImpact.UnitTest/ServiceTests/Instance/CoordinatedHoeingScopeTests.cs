using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedHoeingScopeTests
{
    [Fact]
    public async Task BatchIdentityAndParticipants_AreFrozenAndFlowWithNestedWork()
    {
        var participants = new List<string> { " 123 ", "456", "123" };
        using (var scope = new CoordinatedHoeingScope(true, "batch-1", participants))
        {
            participants.Clear();
            Assert.Equal(new[] { "123", "456" }, scope.ExpectedParticipantUids);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)scope.ExpectedParticipantUids).Add("789"));
            await Task.Run(() =>
            {
                Assert.Same(scope, CoordinatedHoeingScope.Current);
                Assert.Equal("batch-1", CoordinatedHoeingScope.Current!.BatchId);
            });
        }
        Assert.Null(CoordinatedHoeingScope.Current);
    }

    [Fact]
    public void CoordinatedStart_RequiresFrozenBatchAndParticipantsBeforeAcceptance()
    {
        var legacy = InstanceIpcEnvelope.Request("ext.task.start", new { coordinatedHoeing = "v1", groupName = "组" });
        Assert.Equal("capability_required", InstanceRequestHandler.ValidateCoordinatedHoeingRequest(legacy)!.ErrorCode);
        var valid = InstanceIpcEnvelope.Request("ext.task.start", new
        {
            coordinatedHoeing = "v1", groupName = "组", hoeingBatchId = "batch-1",
            hoeingParticipantUids = new[] { "123", "456" }
        });
        Assert.Null(InstanceRequestHandler.ValidateCoordinatedHoeingRequest(valid));
        valid.Data!["hoeingParticipantUids"] = new Newtonsoft.Json.Linq.JArray("123", "123");
        Assert.Equal("invalid_request", InstanceRequestHandler.ValidateCoordinatedHoeingRequest(valid)!.ErrorCode);
        Assert.Null(InstanceRequestHandler.ValidateCoordinatedHoeingRequest(
            InstanceIpcEnvelope.Request("ext.task.start", new { groupName = "普通组" })));
    }

    [Fact]
    public async Task FailureFlowsFromNestedAsyncWorkAndDoesNotLeakToNextJob()
    {
        Assert.False(CoordinatedHoeingScope.IsActive);
        using (var scope = new CoordinatedHoeingScope(true))
        {
            await Task.Run(async () => { await Task.Yield(); CoordinatedHoeingScope.MarkIncomplete(); });
            Assert.True(scope.Incomplete);
        }
        Assert.False(CoordinatedHoeingScope.IsActive);
        using var next = new CoordinatedHoeingScope(true);
        Assert.False(next.Incomplete);
    }

    [Fact]
    public async Task IndependentJobsCannotMarkEachOther()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var failing = Task.Run(async () =>
        {
            using var scope = new CoordinatedHoeingScope(true);
            entered.Release();
            await release.WaitAsync();
            CoordinatedHoeingScope.MarkIncomplete();
            Assert.True(scope.Incomplete);
        });
        await entered.WaitAsync();
        using (var other = new CoordinatedHoeingScope(false))
        {
            Assert.False(CoordinatedHoeingScope.IsActive);
            release.Release();
            await failing;
            Assert.False(other.Incomplete);
        }
    }
}
