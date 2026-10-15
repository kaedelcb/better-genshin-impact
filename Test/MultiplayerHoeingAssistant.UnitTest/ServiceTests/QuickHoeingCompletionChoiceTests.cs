using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("UnifiedHoeingStartLifecycle")]
public sealed class QuickHoeingCompletionChoiceTests
{
    internal static (MainViewModel Vm, AssistConfig Config) Configured(string policy = "runSpecified")
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var vm = new MainViewModel();
        var config = new AssistConfig { PlayerUid = "self", OnlineHoeingCompletionPolicy = policy,
            OnlineHoeingSpecifiedTaskName = "绑定中的任务", OnlineHoeingSpecifiedTaskType = "onedragon", OnlineHoeingSpecifiedTaskPriority = 12 };
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, config);
        return (vm, config);
    }

    internal static CoordinatedBatchSnapshot Start(bool use, string source = "manual") => new("choice", 2, 0, 0, 1, "registering", "")
    {
        Protocol = "hoeing.start.v2", Source = source, UseCompletionActions = use,
        ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
        { ["self"] = [new("锄地", "group")] }
    };

    [Theory]
    [InlineData(false, "manual", TaskConflictPolicy.Stop)]
    [InlineData(true, "manual", TaskConflictPolicy.RunSpecified)]
    [InlineData(false, "online", TaskConflictPolicy.RunSpecified)]
    public void SelectionReusesTheExistingBindingAndKeepsItsFourFieldsFrozen(bool use, string source, TaskConflictPolicy expected)
    {
        var (vm, config) = Configured();
        var start = Start(use, source);
        var batch = new OnlineHoeingBatch(start, "self", vm.SnapshotBatchCompletionPolicy(start));
        Assert.Equal(expected, batch.Policy!.Policy);
        if (expected == TaskConflictPolicy.RunSpecified)
        {
            Assert.Equal(config.OnlineHoeingSpecifiedTaskName, batch.Policy.SpecifiedTaskName);
            Assert.Equal("onedragon", batch.Policy.SpecifiedTaskType);
            Assert.Equal(12, batch.Policy.SpecifiedTaskPriority);
        }
        config.OnlineHoeingCompletionPolicy = "stop";
        config.OnlineHoeingSpecifiedTaskName = "后改的任务";
        Assert.Equal(expected, batch.Policy.Policy);
        if (expected == TaskConflictPolicy.RunSpecified) Assert.Equal("绑定中的任务", batch.Policy.SpecifiedTaskName);
    }

    [Theory]
    [InlineData("resume", "group", "", "恢复原任务")]
    [InlineData("stop", "group", "", "锄完停止")]
    [InlineData("runSpecified", "group", "收尾", "执行配置组「收尾」")]
    [InlineData("runSpecified", "onedragon", "收尾", "执行一条龙「收尾」")]
    public void MemberDescriptionShowsExistingBindingAction(string policy, string type, string name, string text)
        => Assert.Equal(text, new CoordinatedCompletionAction(policy, type, name, 0).Description);
}
