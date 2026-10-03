using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

public sealed class OriginalJobCancellationIdentityTests
{
    [Theory]
    [InlineData("epoch")][InlineData("idempotencyKey")][InlineData("workflowRunId")]
    [InlineData("nodeId")][InlineData("iteration")][InlineData("occurrence")][InlineData("attempt")]
    public void CancelRequiresOriginalEpochAndWholeAppearance(string fault)
    {
        var run = Guid.NewGuid();
        var job = new BgiJob(JobKind.Prerequisite, "fixture", JobSource.Ui, null, "original-key", null,
            identity: new(run, "node", 3, Occurrence: 2, Attempt: 1));
        var epoch = JobRegistry.CurrentEpoch;
        var identity = JObject.FromObject(new { epoch = $"{epoch.ProcessId}:{epoch.StartTicksUtc}", idempotencyKey = "original-key",
            workflowRunId = run.ToString("N"), nodeId = "node", iteration = 3, occurrence = 2, attempt = 1 });
        Assert.True(ExternalInterfaceCommandPlane.MatchesCancelIdentity(identity, job));
        var bad = (JObject)identity.DeepClone();
        bad[fault] = fault is "iteration" or "occurrence" or "attempt" ? new JValue(99) : new JValue("wrong");
        Assert.False(ExternalInterfaceCommandPlane.MatchesCancelIdentity(bad, job));
        bad = (JObject)identity.DeepClone(); bad.Remove(fault);
        Assert.False(ExternalInterfaceCommandPlane.MatchesCancelIdentity(bad, job));
        Assert.False(ExternalInterfaceCommandPlane.MatchesCancelIdentity(identity, null));
    }
}
