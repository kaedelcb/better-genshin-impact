using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FullProductUiTests
{
    [Fact]
    public void DragReorder_PreservesOccurrenceObjectsAndSubmissionOrder()
    {
        var a = new WorkflowNode { NodeId = "a", Kind = "resource.oneDragonConfig", Ref = new() { Config = "same", Revision = "r1" } };
        var b = new WorkflowNode { NodeId = "b", Kind = "resource.oneDragonConfig", Ref = new() { Config = "same", Revision = "r2" } };
        var c = new WorkflowNode { NodeId = "c", Kind = "resource.oneDragonConfig" };
        var doc = new WorkflowDocument { Nodes = [a, b, c] };
        var editor = new WorkflowEditVm(doc, "revision", new ResourceCatalogService(() => null,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "catalog.json")));
        var first = editor.Nodes[0];
        editor.MoveNodeTo(first, editor.Nodes[2], true);
        Assert.Equal(new[] { "b", "c", "a" }, doc.Nodes.Select(n => n.NodeId));
        Assert.Same(a, doc.Nodes[2]);
        Assert.Same(first, editor.Nodes[2]);
        Assert.Equal(new[] { 1, 2, 3 }, editor.Nodes.Select(n => n.Index));
        Assert.Equal(new[] { "b", "c", "a" }, editor.BuildSubmissionCopy().Nodes.Select(n => n.NodeId));
        editor.MoveNodeTo(first, editor.Nodes[0], false);
        Assert.Equal(new[] { "a", "b", "c" }, doc.Nodes.Select(n => n.NodeId));
        Assert.Equal("r1", doc.Nodes[0].Ref!.Revision);
        Assert.Equal("r2", doc.Nodes[1].Ref!.Revision);
        editor.MoveNodeTo(new NodeEditVm(new WorkflowNode { NodeId = "foreign" }), editor.Nodes[0], false);
        Assert.Equal(new[] { "a", "b", "c" }, doc.Nodes.Select(n => n.NodeId));
    }

    [Fact]
    public void ParkedRun_OffersStopAndExplicitResumeWithoutSkipOrPause()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-product-ui-" + Guid.NewGuid().ToString("N"));
        var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, null, null, () => (true, null));
        var card = new ActiveRunVm();
        card.Update(new WorkflowRunRecord { RunId = "run-parked", WorkflowId = "missing", State = WorkflowRunState.LocalWaitParking }, host);
        Assert.True(card.CanStop);
        Assert.True(card.CanResume);
        Assert.False(card.CanSkip);
        Assert.False(card.CanPause);
        Assert.Contains("等待", card.StateText);
    }
}
