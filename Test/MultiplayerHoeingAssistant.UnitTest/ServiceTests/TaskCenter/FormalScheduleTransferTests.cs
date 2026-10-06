using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalScheduleTransferTests
{
    [Fact]
    public void WholeListRoundtrip_PreservesPathsAndUnknownFields_StripsAccounts_AndNeverOverwritesExistingIdentity()
    {
        var root=Path.Combine(Path.GetTempPath(),"schedule-transfer-"+Guid.NewGuid().ToString("N"));
        var store=new WorkflowStore(Path.Combine(root,"flows"));
        var doc=new WorkflowDocument{Name="计划",Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="配置"},Path=new(){Next="$end"},Strategies=[new(){Kind="flow.route"},new(){Kind="prerequisite.account",Params=new(){["uid"]=JsonSerializer.SerializeToElement("private-account"),["bindingCode"]=JsonSerializer.SerializeToElement("private-binding")}}]}],ExtensionData=new(){["custom"]=JsonSerializer.SerializeToElement("keep")}};
        var original=store.Save(doc,null);var originalBytes=File.ReadAllBytes(store.List().Single().FilePath);
        var package=Path.Combine(root,"shared.json");WorkflowScheduleTransfer.Export(store,package);
        var text=File.ReadAllText(package);Assert.DoesNotContain("private-account",text);Assert.DoesNotContain("private-binding",text);
        var imported=Assert.Single(WorkflowScheduleTransfer.Import(store,package));Assert.NotEqual(doc.WorkflowId,imported);
        Assert.Equal(originalBytes,File.ReadAllBytes(store.List().Single(e=>e.WorkflowId==doc.WorkflowId).FilePath));
        Assert.Equal(original,store.LoadSnapshot(doc.WorkflowId!).Revision);
        var copy=store.LoadSnapshot(imported).Document;Assert.Equal("candidate-ready",copy.Activation!.Status);Assert.False(new WorkflowPlan(copy).Preflight(true).Executable);
        Assert.Equal("$end",copy.Nodes[0].Path!.Next);Assert.Equal("keep",copy.ExtensionData!["custom"].GetString());
    }
    [Fact]
    public void InvalidLaterFlow_DoesNotPartiallyImportEarlierFlow()
    {
        var root=Path.Combine(Path.GetTempPath(),"schedule-transfer-invalid-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var store=new WorkflowStore(Path.Combine(root,"flows"));var file=Path.Combine(root,"invalid.json");
        File.WriteAllText(file,"{\"schema\":\"mistletoe.scheduleList\",\"schemaVersion\":1,\"flows\":[{\"schema\":\"mistletoe.workflow\",\"schemaVersion\":1,\"name\":\"first\",\"nodes\":[]},{\"schema\":\"unsupported\"}]}");
        Assert.ThrowsAny<Exception>(()=>WorkflowScheduleTransfer.Import(store,file));Assert.Empty(store.List());Assert.False(Directory.Exists(Path.Combine(root,"flows")));
    }
}
