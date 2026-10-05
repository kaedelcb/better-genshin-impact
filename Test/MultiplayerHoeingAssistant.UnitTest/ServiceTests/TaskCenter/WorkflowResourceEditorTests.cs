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

    private sealed class Transport : IResourceCatalogTransport
    {
        public bool Ready=true;public bool IsReady=>Ready;public bool HasCapability(string name)=>Ready;
        public string? Operation;public object? Payload;public string? Reply="{\"status\":\"editor_opened\"}";
        public Task<string?> SendAsync(string operation,object payload,CancellationToken ct)
        {Operation=operation;Payload=payload;return Task.FromResult(Reply);}
    }
}
