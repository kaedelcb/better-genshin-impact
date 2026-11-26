using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RoomOrchestrationCompactDisplayTests
{
    private const string Job = "6e97a488393f41cbbff3597b64288fad";
    private const string Note = "绑定完成动作尚未确认，保留原请求：计划表原运行尚未退出，保留同一完成动作";

    [Fact]
    public void ExactConfirmedJobCompactsOnlyItsOwnSuccessMessage()
    {
        var success = new RoomOrchestrationStatus("done", "已完成", $"本机作业 {Job} 已确认成功");
        var task = new RoomOrchestrationTaskRow(1, "联机锄地", "配置组", success);
        Assert.Equal(Job, RoomOrchestrationDisplayConverter.ConfirmedJob(success));
        Assert.Empty(RoomOrchestrationDisplayConverter.InlineTaskDetail(success));
        Assert.Equal("作业 6e97a488…", RoomOrchestrationDisplayConverter.ShortJob(task, 560));
        Assert.Empty(RoomOrchestrationDisplayConverter.ShortJob(task, 380));
        Assert.Empty(RoomOrchestrationDisplayConverter.ShortJob(task with { Name = new string('任', 44) }, 560));
        var failure = success with { Kind = "failed", Detail = "本机作业失败，结果待核对" };
        Assert.Null(RoomOrchestrationDisplayConverter.ConfirmedJob(failure));
        Assert.Equal(failure.Detail, RoomOrchestrationDisplayConverter.InlineTaskDetail(failure));
        Assert.Null(RoomOrchestrationDisplayConverter.ConfirmedJob(success with { Detail = success.Detail + "；后续任务尚未确认" }));
    }

    [Fact]
    public void HeaderSeparatesConfirmedLoginIdentityAndCancellationCleanup()
    {
        const string cleanup = "联机锄地已取消，正在确认清理";
        var target = new MainViewModel.OrchestrationMonitorTarget("fixture-uid", "instance-full-123456789", "machine-full-987654321", "执行端 A");
        var status = new RoomOrchestrationStatus("stopped", "已取消", cleanup);
        var card = new RoomOrchestrationCard("room", "示例 房间", "123456 · https://fixture.example.test", "独立房间", true, true,
            status, "", cleanup, [], [], "", "4 人", "未启用", "恢复任务", null, null);
        var original = new RoomOrchestrationPresentation("当前登录：示例 房间 123456 · https://fixture.example.test",
            "执行端：" + target.Label + " · " + cleanup, status, [], [card]);
        var compact = RoomOrchestrationHeaderDisplay.Create(original, true, target, null);
        Assert.Equal("登录", compact.LoginLabel);
        Assert.Equal("示例 房间", compact.RoomName);
        Assert.Equal("123456", compact.RoomCode);
        Assert.Equal(target.Name, compact.ExecutorName);
        Assert.Equal("已取消", compact.Status.Text);
        Assert.Equal("清理确认中", compact.Reason);
        Assert.Contains(cleanup, compact.Details);
        Assert.Contains(target.InstanceId, compact.Details);
        Assert.Contains(target.MachineId, compact.Details);
        var unconfirmed = original with { LoginText = "最后确认房间：示例 房间 123456（尚未确认连接）",
            Cards = [card with { Current = false }], CurrentStatus = new("unknown", "登录待核对") };
        var stale = RoomOrchestrationHeaderDisplay.Create(unconfirmed, true, target, null);
        Assert.Equal("最后确认", stale.LoginLabel);
        Assert.Equal("unknown", stale.Status.Kind);
        Assert.Contains("尚未确认连接", stale.Details);
        Assert.Equal(target.Name, new MonitorTargetOption(target, false).Label);
        Assert.Contains(target.InstanceId[..8], new MonitorTargetOption(target, true).Label);
        Assert.Contains(target.MachineId, new MonitorTargetOption(target, true).FullLabel);
    }

    [Fact]
    public void RoomSummaryDoesNotDiscardOriginalEvidenceOrInventStatus()
    {
        var status = new RoomOrchestrationStatus("waiting", "等待本轮收尾", Note);
        var card = new RoomOrchestrationCard("room", "示例", "123456", "", true, true, status,
            "11-26 12:36", Note, [new(1, "任务甲", "配置组", new("done", "已完成", $"本机作业 {Job} 已确认成功"))],
            [], "", "4 人", "未启用", "停止", null, null);
        Assert.Equal("计划表尚未退出", RoomOrchestrationDisplayConverter.Summary(card));
        var details = RoomOrchestrationDisplayConverter.FullDetails(card);
        Assert.Contains(Note, details);
        Assert.Contains(Job, details);
        Assert.Contains("任务甲", details);
        Assert.Equal("状态待核对", RoomOrchestrationDisplayConverter.Summary(card with { ProgressUnconfirmed = true }));
        var plain = card with { Detail = "", Status = new("done", "已完成") };
        Assert.Equal("任务执行详情", RoomOrchestrationDisplayConverter.Summary(plain));
        Assert.Empty(RoomOrchestrationDisplayConverter.Summary(plain with { Tasks = [] }));
        Assert.Equal("19:30", RoomOrchestrationDisplayConverter.ScheduleValue("19:30（待确认）"));
        Assert.True(RoomOrchestrationDisplayConverter.ScheduleNeedsConfirmation("19:30（待确认）"));
    }
}
