using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class NativeSingleAuthoringTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"single-ui-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
    [Fact]
    public async Task LiveSupportedSingleAppendsExactOwnerTaskAndRevision()
    {
        var transport=new LiveTransport();
        var catalog=new ResourceCatalogService(()=>transport,Path.Combine(_root,"cache.json"));
        await catalog.RefreshAsync();
        var draft=new WorkflowDocument{Name="mixed",Nodes=[]};
        var edit=new WorkflowEditVm(draft,"rev",catalog);
        var source=Assert.Single(edit.AppendSources.Where(s=>s.Kind==TaskCenterResourceKind.SingleTask));
        Assert.Contains("配置:原生 / 重复领取邮件",source.Line);
        edit.AppendSourceIndex=edit.AppendSources.IndexOf(source);
        edit.AppendNodeCommand.Execute(null);
        var node=Assert.Single(edit.BuildSubmissionCopy().Nodes);
        Assert.Equal("resource.singleTask",node.Kind);
        Assert.Equal("配置:原生",node.Ref!.Config);
        Assert.Equal("task-duplicate-2",node.Ref.TaskId);
        Assert.Equal("revision-current",node.Ref.Revision);
        Assert.Empty(draft.Nodes.Single().Strategies);
        transport.Ready=false;await catalog.RefreshAsync();edit.RefreshCatalog(catalog);
        Assert.DoesNotContain(edit.AppendSources,s=>s.Kind==TaskCenterResourceKind.SingleTask);
    }
    private sealed class LiveTransport:IResourceCatalogTransport
    {
        public bool Ready=true;
        public bool IsReady=>Ready;
        public bool HasCapability(string name)=>true;
        public Task<string?> SendAsync(string operation,object payload,CancellationToken ct)=>Task.FromResult<string?>(operation==BgiExternalCatalogTransport.OpConfigList
            ? "{\"configGroups\":[],\"oneClickConfigs\":[\"配置:原生\"]}"
            : JsonSerializer.Serialize(new{configRevision="revision-current",tasks=new[]{
                new{taskId="task-duplicate-2",name="重复领取邮件",enabled=true,singleExecutionSupported=true},
                new{taskId="disabled",name="disabled",enabled=false,singleExecutionSupported=true},
                new{taskId="unsupported",name="unsupported",enabled=true,singleExecutionSupported=false}}}));
    }
}
