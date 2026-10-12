using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class WorkflowResourceEditorTests
{
    [Theory]
    [InlineData("resource.oneDragonConfig", "configName", null)]
    [InlineData("resource.configGroup", "groupName", null)]
    [InlineData("resource.singleTask", "configName", "stable-id")]
    public async Task OpensOriginalReferenceWithRevision(string kind,string nameField,string? task)
    {
        var node=new WorkflowNode {Kind=kind,Ref=new() {Config="原资源",Revision="original",TaskId=task}};
        var transport=new Transport();
        await WorkflowResourceEditor.OpenAsync(node,transport);
        Assert.Equal(WorkflowResourceEditor.Operation,transport.Operation);
        var payload=JsonSerializer.SerializeToElement(transport.Payload);
        Assert.Equal("原资源",payload.GetProperty(nameField).GetString());
        Assert.Equal("original",payload.GetProperty("expectedConfigRevision").GetString());
        Assert.Equal(task,payload.GetProperty("taskId").GetString());
        Assert.Equal("original",node.Ref.Revision);
    }

    [Fact]
    public async Task MissingCapabilityOrUnconfirmedOpenDoesNotClaimSuccess()
    {
        var node=new WorkflowNode {Kind="resource.oneDragonConfig",Ref=new() {Config="原资源",Revision="original"}};
        var offline=new Transport {Ready=false};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkflowResourceEditor.OpenAsync(node,offline));
        Assert.Null(offline.Operation);
        var rejected=new Transport {Reply="{\"status\":\"rejected\"}"};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkflowResourceEditor.OpenAsync(node,rejected));
        Assert.Equal("original",node.Ref.Revision);
    }

    [Fact]
    public async Task RevisionRefreshDoesNotReplaceSingleTaskIdentityOrAcceptMissingTask()
    {
        var node=new WorkflowNode {Kind="resource.singleTask",Ref=new() {Config="原资源",Revision="original",TaskId="stable-id"}};
        var transport=new Transport {Reply="{\"configRevision\":\"new\",\"tasks\":[{\"taskId\":\"other\"}]}"};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>WorkflowResourceEditor.ReadRevisionAsync(node,transport));
        Assert.Equal("original",node.Ref.Revision);Assert.Equal("stable-id",node.Ref.TaskId);
        transport.Reply="{\"configRevision\":\"new\",\"tasks\":[{\"taskId\":\"stable-id\"}]}";
        Assert.Equal("new",await WorkflowResourceEditor.ReadRevisionAsync(node,transport));
        Assert.Equal("original",node.Ref.Revision);
    }

    [Fact]
    public async Task InternalTaskLayoutReadsLiveOrderAndSendsCompleteRevisionBoundEdit()
    {
        var node = new WorkflowNode { Kind = "resource.configGroup", Ref = new() { Config = "配置组", Revision = "plan-old" } };
        var transport = new Transport
        {
            Reply = "{\"configRevision\":\"live-1\",\"startTaskId\":\"b\",\"tasks\":["
                + "{\"taskId\":\"a\",\"name\":\"任务甲\",\"enabled\":true},"
                + "{\"taskId\":\"b\",\"name\":\"任务乙\",\"enabled\":true}]}"
        };
        var current = await WorkflowResourceEditor.ReadLayoutAsync(node, transport);
        Assert.Equal("live-1", current.Revision);
        Assert.Equal("b", current.StartTaskId);
        Assert.Equal(new[] { "a", "b" }, current.Tasks.Select(task => task.TaskId));
        transport.Reply = "{\"status\":\"config_applied\",\"configRevision\":\"live-2\"}";
        var revision = await WorkflowResourceEditor.ApplyLayoutAsync(node, transport, current,
            [current.Tasks[1] with { Enabled = false }, current.Tasks[0]], "a");
        Assert.Equal("live-2", revision);
        Assert.Equal(WorkflowResourceEditor.ApplyLayoutOperation, transport.Operation);
        var sent = JsonSerializer.SerializeToElement(transport.Payload);
        Assert.Equal("配置组", sent.GetProperty("groupName").GetString());
        Assert.Equal("live-1", sent.GetProperty("expectedConfigRevision").GetString());
        Assert.Equal("a", sent.GetProperty("startTaskId").GetString());
        Assert.Equal(new[] { "b", "a" }, sent.GetProperty("tasks").EnumerateArray()
            .Select(task => task.GetProperty("taskId").GetString()));
        Assert.False(sent.GetProperty("tasks")[0].GetProperty("enabled").GetBoolean());
        Assert.Equal("plan-old", node.Ref.Revision);
    }

    private sealed class Transport : IResourceCatalogTransport
    {
        public bool Ready=true;public bool IsReady=>Ready;public bool HasCapability(string name)=>Ready;
        public string? Operation;public object? Payload;public string? Reply="{\"status\":\"editor_opened\"}";
        public Task<string?> SendAsync(string operation,object payload,CancellationToken ct)
        {Operation=operation;Payload=payload;return Task.FromResult(Reply);}
    }
}
