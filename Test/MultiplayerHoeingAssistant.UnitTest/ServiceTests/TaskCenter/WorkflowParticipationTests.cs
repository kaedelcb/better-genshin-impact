using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class WorkflowParticipationTests
{
    private static WorkflowEditVm Edit(bool legacy)
    {
        var root=Path.Combine(Path.GetTempPath(),"participation-"+Guid.NewGuid().ToString("N"));
        var node=new WorkflowNode {NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="A"},
            ExtensionData=new(){["custom"]=JsonSerializer.SerializeToElement("保留")}};
        if(legacy)node.ExtensionData["legacyFiltered"]=JsonSerializer.SerializeToElement(true);
        var doc=new WorkflowDocument {Nodes=[node]};
        return new WorkflowEditVm(doc,"revision",new ResourceCatalogService(()=>null,Path.Combine(root,"catalog.json")));
    }

    [Fact]
    public void OpeningPreservesOriginalSkip_ExplicitIncludeBecomesExecutable()
    {
        var edit=Edit(true);var node=edit.Nodes.Single();
        Assert.False(node.IncludedInPlan);Assert.NotEmpty(node.ParticipationNote);
        Assert.True(edit.BuildSubmissionCopy().Nodes[0].ExtensionData!["legacyFiltered"].GetBoolean());
        node.IncludedInPlan=true;var saved=edit.BuildSubmissionCopy();
        Assert.False(saved.Nodes[0].ExtensionData!["legacyFiltered"].GetBoolean());
        Assert.False(saved.Nodes[0].ExtensionData!["planDisabled"].GetBoolean());
        Assert.Equal("保留",saved.Nodes[0].ExtensionData!["custom"].GetString());
        Assert.NotEqual(NodeGateAction.Skip,new WorkflowPlan(saved).EvaluateNode(new("a",0,0,0),DateTimeOffset.Now,true).Action);
        Assert.True(edit.UndoSchedule());Assert.False(node.IncludedInPlan);
    }

    [Fact]
    public void DisablingOrdinaryTaskSkipsItWithoutChangingItsResource()
    {
        var edit=Edit(false);edit.Nodes[0].IncludedInPlan=false;var saved=edit.BuildSubmissionCopy();
        var gate=new WorkflowPlan(saved).EvaluateNode(new("a",0,0,0),DateTimeOffset.Now,true);
        Assert.Equal(NodeGateAction.Skip,gate.Action);Assert.Contains("停用",gate.Reason);
        Assert.Equal("A",saved.Nodes[0].Ref!.Config);
        Assert.False(saved.Nodes[0].ExtensionData!.ContainsKey("legacyFiltered"));
    }
}
