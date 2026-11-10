using System.IO;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>验证同机更新协作文件：发起后为“准备”，成功写“完成”并带重启时间，失败写“失败”且不重启助手，结果只写一次。</summary>
public class BgiUpdateCoordinationTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "coordination-test-" + Guid.NewGuid().ToString("N"));

    public BgiUpdateCoordinationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Begin_WritesPreparing_ThenComplete_WritesDoneWithRestart()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        var preparing = BgiUpdateCoordination.Read(_dir);
        Assert.Equal(BgiUpdateCoordination.Preparing, preparing?.Phase);
        Assert.Equal(session.Id, preparing?.Id);

        session.Complete(TimeSpan.FromSeconds(6));
        var done = BgiUpdateCoordination.Read(_dir);
        Assert.Equal(BgiUpdateCoordination.Done, done?.Phase);
        Assert.True(done!.RestartAssistants);
        Assert.NotNull(done.RestartAfterUtc);
    }

    [Fact]
    public async Task Fail_WritesFailedWithoutRestart_AndResultIsWrittenOnce()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        session.Fail();
        session.Complete(TimeSpan.Zero); // 已结束，不得改写为“完成”
        var state = BgiUpdateCoordination.Read(_dir);
        Assert.Equal(BgiUpdateCoordination.Failed, state?.Phase);
        Assert.False(state!.RestartAssistants);
    }

    [Fact]
    public void Read_IgnoresMissingOrCorruptFile()
    {
        Assert.Null(BgiUpdateCoordination.Read(_dir));
        File.WriteAllText(BgiUpdateCoordination.PathFor(_dir), "{not json");
        Assert.Null(BgiUpdateCoordination.Read(_dir));
    }
}
