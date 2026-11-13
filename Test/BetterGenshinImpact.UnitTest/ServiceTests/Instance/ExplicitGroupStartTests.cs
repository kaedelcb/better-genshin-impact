using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>显式单次起点覆盖本次入口，同时保留已保存的配置与其他启动语义。</summary>
public sealed class ExplicitGroupStartTests
{
    private static ScriptGroup Group() => ScriptGroup.FromJson("""
        {"name":"收尾","taskCenterStartTaskId":"saved-tail","projects":[
          {"name":"领取","folderName":"a","type":"Pathing","status":"Enabled","index":1,"taskCenterTaskId":"head"},
          {"name":"回城","folderName":"b","type":"Pathing","status":"Enabled","index":2,"taskCenterTaskId":"middle"},
          {"name":"退出","folderName":"c","type":"Pathing","status":"Enabled","index":3,"taskCenterTaskId":"saved-tail"}
        ]}
        """);

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(3, true, true, false)]
    public void ExplicitSelectionRunsTheChosenTailWithoutChangingTheSavedStart(
        int start, bool firstSkipped, bool secondSkipped, bool thirdSkipped)
    {
        var group = Group();
        foreach (var item in group.Projects) { item.SkipFlag = true; item.NextFlag = true; }
        var projects = InstanceRequestHandler.SelectExplicitGroupStart(group, start);

        Assert.Equal(new[] { firstSkipped, secondSkipped, thirdSkipped }, projects.Select(item => item.SkipFlag == true));
        Assert.All(projects, item => Assert.False(item.NextFlag));
        Assert.Equal("saved-tail", group.TaskCenterStartTaskId);
        Assert.Equal(new[] { "领取", "回城", "退出" }, projects.Select(item => item.Name));
        Assert.Equal(group.Projects, projects); // 保留原始位置，进度与重试仍能使用完整列表。
    }

    [Fact]
    public void ExplicitBeginningDoesNotConsumeOrValidateAnUnrelatedSavedStart()
    {
        var group = Group();
        group.TaskCenterStartTaskId = "previously-deleted-task";
        var projects = InstanceRequestHandler.SelectExplicitGroupStart(group, 1);
        Assert.All(projects, item => Assert.False(item.SkipFlag));
        Assert.Equal("previously-deleted-task", group.TaskCenterStartTaskId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public void MissingStartRejectsWithoutChangingExecutionFlags(int start)
    {
        var group = Group();
        group.Projects[2].NextFlag = true;
        var before = group.ToJson();
        Assert.Throws<InvalidOperationException>(() => InstanceRequestHandler.SelectExplicitGroupStart(group, start));
        Assert.Equal(before, group.ToJson());
        Assert.All(group.Projects, item => Assert.False(item.SkipFlag));
    }

    [Fact]
    public void DisabledOrAmbiguousStartRejectsBeforeSelectingAnyTail()
    {
        var disabled = Group();
        disabled.Projects[1].Status = "Disabled";
        Assert.Throws<InvalidOperationException>(() => InstanceRequestHandler.SelectExplicitGroupStart(disabled, 2));
        Assert.All(disabled.Projects, item => Assert.False(item.SkipFlag));

        var ambiguous = Group();
        ambiguous.Projects[2].Index = 2;
        Assert.Throws<InvalidOperationException>(() => InstanceRequestHandler.SelectExplicitGroupStart(ambiguous, 2));
        Assert.All(ambiguous.Projects, item => Assert.False(item.SkipFlag));
    }

    [Fact]
    public void ResumingBetweenTasksCanSkipADisabledCursorAndContinueTheRemainingTail()
    {
        var group = Group();
        group.Projects[1].Status = "Disabled";
        // A 完成后的空隙水位为位置 2，即使 B 禁用也必须继续到 C。
        var projects = InstanceRequestHandler.SelectExplicitGroupStart(group, 2, isResume: true);
        Assert.Equal(new[] { true, false, false }, projects.Select(item => item.SkipFlag == true));
        Assert.Equal("Disabled", projects[1].Status);
        Assert.Equal("Enabled", projects[2].Status);
        Assert.Equal("saved-tail", group.TaskCenterStartTaskId);
    }

    [Fact]
    public void HelloAdvertisesOneTimeGroupSelectionSoOldExecutorsCannotSilentlyUseSavedStarts()
    {
        var hello = ExternalInterfaceProtocol.BuildHelloData(Guid.NewGuid(), null, 123);
        Assert.True(hello["capabilities"]!["task.start.explicitGroupStart.v1"]!.Value<bool>());
    }
}
