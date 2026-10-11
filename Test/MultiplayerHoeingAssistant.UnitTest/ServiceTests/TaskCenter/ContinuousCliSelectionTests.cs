using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class ContinuousCliSelectionTests
{
    [Fact]
    public void NamedPlan_SelectsExactName()
    {
        var flows = new[] { Active("wf-a", "日常计划"), Active("wf-b", "夜间计划") };

        var ok = TaskCenterPanelViewModel.TryResolveContinuousPlan(
            flows, "夜间计划", currentWorkflowId: null, out var selected, out var detail);

        Assert.True(ok);
        Assert.Equal("wf-b", selected?.WorkflowId);
        Assert.Empty(detail);
    }

    [Fact]
    public void UnnamedPlan_UsesPersistedCurrentIdentity()
    {
        var flows = new[] { Active("wf-a", "日常计划"), Active("wf-b", "夜间计划") };

        var ok = TaskCenterPanelViewModel.TryResolveContinuousPlan(
            flows, planName: null, currentWorkflowId: "wf-a", out var selected, out _);

        Assert.True(ok);
        Assert.Equal("wf-a", selected?.WorkflowId);
    }

    [Fact]
    public void MissingPlan_IsRejectedWithoutFallback()
    {
        var flows = new[] { Active("wf-a", "日常计划") };

        var ok = TaskCenterPanelViewModel.TryResolveContinuousPlan(
            flows, "不存在", currentWorkflowId: null, out var selected, out var detail);

        Assert.False(ok);
        Assert.Null(selected);
        Assert.Contains("找不到计划", detail);
    }

    [Fact]
    public void AmbiguousPlan_IsRejectedWithoutGuessing()
    {
        var flows = new[] { Active("wf-a", "重复计划"), Active("wf-b", "重复计划") };

        var ok = TaskCenterPanelViewModel.TryResolveContinuousPlan(
            flows, "重复计划", currentWorkflowId: null, out var selected, out var detail);

        Assert.False(ok);
        Assert.Null(selected);
        Assert.Contains("不唯一", detail);
    }

    private static WorkflowListItemVm Active(string id, string name)
        => new(new WorkflowCatalogEntry(id, name, "revision", WorkflowFileStatus.Ready,
            QuarantineReason: null, UnsupportedKinds: [], ActivationStatus: "active", FilePath: id + ".flow.json"));
}
