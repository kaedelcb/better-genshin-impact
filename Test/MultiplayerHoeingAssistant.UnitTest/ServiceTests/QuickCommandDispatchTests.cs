using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class QuickCommandDispatchTests
{
    // 四人快捷启动必须在本机任务仍运行时发给全部队员，与本机在列表中的位置无关。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RunningLocalTaskDoesNotHoldBackOtherMembers(int localIndex)
    {
        var members = CreateMembers();
        var localFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();
        var completion = MainViewModel.DispatchQuickCommandMembersAsync(members, member =>
        {
            started.Add(member.PlayerUid);
            return member == members[localIndex] ? localFinished.Task : Task.CompletedTask;
        });

        try
        {
            Assert.Equal(members.Select(m => m.PlayerUid), started);
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            localFinished.SetResult();
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FailedMemberDoesNotPreventOtherDispatchesAndFailureIsObserved()
    {
        var members = CreateMembers();
        var started = new List<string>();
        var failure = new InvalidOperationException("本机启动失败");
        var completion = MainViewModel.DispatchQuickCommandMembersAsync(members, member =>
        {
            started.Add(member.PlayerUid);
            if (member == members[1]) throw failure;
            return Task.CompletedTask;
        });

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => completion));
        Assert.Equal(members.Select(m => m.PlayerUid), started);
    }

    [Fact]
    public async Task OnlySelectedOnlineMembersAreDispatchedFromInitialSelection()
    {
        var members = CreateMembers();
        members[1].Online = false;
        members[2].IsSelected = false;
        var started = new List<string>();
        await MainViewModel.DispatchQuickCommandMembersAsync(members, member =>
        {
            started.Add(member.PlayerUid);
            // 模拟异步发送期间的成员状态更新；不能改变这次点击已经确定的目标。
            members[1].Online = true;
            members[2].IsSelected = true;
            members[3].IsSelected = false;
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "player-0", "player-3" }, started);
    }

    private static MemberViewModel[] CreateMembers() => Enumerable.Range(0, 4)
        .Select(i => new MemberViewModel { PlayerUid = $"player-{i}", Online = true, IsSelected = true })
        .ToArray();
}
