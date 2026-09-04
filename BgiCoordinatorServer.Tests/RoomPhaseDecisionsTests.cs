using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>RoomPhaseDecisions.Derive 纯函数测试（《审核方案》§7.1.1 转换矩阵）。</summary>
public class RoomPhaseDecisionsTests
{
    [Fact]
    public void Derive_NoPlayers_ReturnsIdle()
    {
        Assert.Equal(RoomPhase.Idle,
            RoomPhaseDecisions.Derive(false, false, false, false, false, false, 0));
    }

    [Fact]
    public void Derive_PlayersNoActivity_ReturnsWaitingForPlayers()
    {
        Assert.Equal(RoomPhase.WaitingForPlayers,
            RoomPhaseDecisions.Derive(false, false, false, false, false, false, 3));
    }

    [Fact]
    public void Derive_RouteReportActivity_ReturnsRouteVerifying()
    {
        Assert.Equal(RoomPhase.RouteVerifying,
            RoomPhaseDecisions.Derive(false, false, false, false, false, true, 2));
    }

    [Fact]
    public void Derive_HostReadyUploadedAllDone_ReturnsReadyToStart()
    {
        Assert.Equal(RoomPhase.ReadyToStart,
            RoomPhaseDecisions.Derive(false, false, true, true, true, true, 4));
    }

    [Fact]
    public void Derive_Started_ReturnsRunning()
    {
        Assert.Equal(RoomPhase.Running,
            RoomPhaseDecisions.Derive(true, false, false, false, false, false, 4));
    }

    [Fact]
    public void Derive_StartedAndExpCapBroadcasted_ReturnsEnded()
    {
        Assert.Equal(RoomPhase.Ended,
            RoomPhaseDecisions.Derive(true, true, true, true, true, false, 4));
    }

    [Fact]
    public void Derive_StartedTakesPriorityOverHostReadyCombination()
    {
        // isStarted 优先于 hostReady∧uploaded∧allDone 组合（Running 而非 ReadyToStart）
        Assert.Equal(RoomPhase.Running,
            RoomPhaseDecisions.Derive(true, false, true, true, true, true, 4));
    }

    [Fact]
    public void Derive_ExpCapBroadcastedOnlyEffectiveWhenStarted()
    {
        // expCapBroadcasted 只在 isStarted 时生效：未开锄时应落到其它分支而非 Ended
        Assert.Equal(RoomPhase.WaitingForPlayers,
            RoomPhaseDecisions.Derive(false, true, false, false, false, false, 2));
        Assert.Equal(RoomPhase.ReadyToStart,
            RoomPhaseDecisions.Derive(false, true, true, true, true, false, 2));
        Assert.Equal(RoomPhase.Idle,
            RoomPhaseDecisions.Derive(false, true, false, false, false, false, 0));
    }

    [Fact]
    public void Derive_HostReadyCombinationIncomplete_FallsThrough()
    {
        // 三要素缺一不算 ReadyToStart
        Assert.Equal(RoomPhase.WaitingForPlayers,
            RoomPhaseDecisions.Derive(false, false, true, true, false, false, 2));
        Assert.Equal(RoomPhase.RouteVerifying,
            RoomPhaseDecisions.Derive(false, false, true, true, false, true, 2));
    }
}
