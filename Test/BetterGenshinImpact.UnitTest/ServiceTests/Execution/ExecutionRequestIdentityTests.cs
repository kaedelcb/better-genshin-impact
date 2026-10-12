using BetterGenshinImpact.Service.Execution;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

public sealed class ExecutionRequestIdentityTests
{
    [Fact]
    public void WholeGroup_ExplicitNullTaskId_RemainsNullInRegisteredJob()
    {
        var runId = Guid.NewGuid();
        var data = new JObject
        {
            ["workflowRunId"] = runId.ToString("N"),
            ["nodeId"] = "close-stream",
            ["iteration"] = 0,
            ["occurrence"] = 0,
            ["attempt"] = 1,
            ["taskId"] = JValue.CreateNull(),
            ["expectedConfigRevision"] = "group-revision",
        };

        var identity = ExecutionRequestContract.ReadIdentity(data);

        Assert.NotNull(identity);
        Assert.Equal(runId, identity!.WorkflowRunId);
        Assert.Null(identity.TaskId);
        Assert.Equal("group-revision", identity.ConfigRevision);
        var job = new BgiJob(JobKind.Group, "关直播", JobSource.Ext, null, null, null, identity: identity);
        Assert.Null(job.TaskId);
        Assert.Equal("group-revision", job.ConfigRevision);
    }

    [Fact]
    public void SingleTask_IdentityAndRevisionArePreserved()
    {
        var data = new JObject
        {
            ["workflowRunId"] = Guid.NewGuid().ToString("N"),
            ["nodeId"] = "mail",
            ["iteration"] = 0,
            ["taskId"] = "mail-task-id",
            ["expectedConfigRevision"] = "mail-revision",
        };

        var identity = ExecutionRequestContract.ReadIdentity(data)!;

        Assert.Equal("mail-task-id", identity.TaskId);
        Assert.Equal("mail-revision", identity.ConfigRevision);
    }
}
