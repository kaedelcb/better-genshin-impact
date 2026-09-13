using BetterGenshinImpact.Service.ExternalInterface;

namespace BetterGenshinImpact.UnitTest.ServiceTests.ExternalInterface;

/// <summary>
/// [A5-3] job.progress 事件登记守护：事件名入 All（订阅过滤唯一权威）且 IsKnown 可识别。
/// 发布点（OneDragonFlowViewModel 水位线推进处）UI 依赖重不可单测，payload 为纯匿名对象，
/// 此锚点守"事件名被误改/误删导致订阅方永远收不到"这类回归。
/// </summary>
public class JobProgressEventTests
{
    [Fact]
    public void JobProgress_Registered_InAllAndIsKnown()
    {
        Assert.Equal("job.progress", ExternalInterfaceEventNames.JobProgress);
        Assert.Contains(ExternalInterfaceEventNames.JobProgress, ExternalInterfaceEventNames.All);
        Assert.True(ExternalInterfaceEventNames.IsKnown(ExternalInterfaceEventNames.JobProgress));
    }

    [Fact]
    public void JobProgress_Publish_NeverThrows_OnHub()
    {
        // 发布出口纪律：观察性故障绝不外抛（无订阅者时近零开销空转）
        var ex = Record.Exception(() =>
            ExternalInterfaceEventHub.Instance.PublishJobProgress(Guid.NewGuid(), 1, 5, "自动秘境"));
        Assert.Null(ex);
    }
}
