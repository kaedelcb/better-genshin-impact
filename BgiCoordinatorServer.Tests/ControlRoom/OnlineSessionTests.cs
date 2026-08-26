using BgiCoordinatorServer.RoomControl.Domain;
using Xunit;

namespace BgiCoordinatorServer.Tests.ControlRoom;

public class OnlineSessionTests
{
    [Fact]
    public void Create_StartsInWaitingState()
    {
        var session = OnlineSession.Create("ROOM1", DateTime.UtcNow, 4, TimeSpan.FromMinutes(5));

        Assert.Equal(OnlineSessionState.Waiting, session.State);
        Assert.Equal(4, session.Threshold);
    }

    [Fact]
    public void MemberReady_BelowThreshold_StaysWaiting()
    {
        var session = OnlineSession.Create("ROOM1", DateTime.UtcNow, 4, TimeSpan.FromMinutes(5));

        session.MemberReady("uid1");
        session.MemberReady("uid2");

        Assert.Equal(OnlineSessionState.Waiting, session.State);
    }

    [Fact]
    public void MemberReady_ReachesThreshold_BecomesReady()
    {
        var session = OnlineSession.Create("ROOM1", DateTime.UtcNow, 2, TimeSpan.FromMinutes(5));

        session.MemberReady("uid1");
        session.MemberReady("uid2");

        Assert.Equal(OnlineSessionState.Ready, session.State);
    }

    [Fact]
    public void BeginConfirming_FromReady_Transitions()
    {
        var session = OnlineSession.Create("ROOM1", DateTime.UtcNow, 1, TimeSpan.FromMinutes(5));
        session.MemberReady("uid1");
        Assert.Equal(OnlineSessionState.Ready, session.State);

        session.BeginConfirming();

        Assert.Equal(OnlineSessionState.Confirming, session.State);
    }

    [Fact]
    public void AllConfirmed_BecomesExecuting()
    {
        var session = OnlineSession.Create("ROOM1", DateTime.UtcNow, 2, TimeSpan.FromMinutes(5));
        session.MemberReady("uid1");
        session.MemberReady("uid2");
        session.BeginConfirming();

        session.ConfirmMember("uid1");
        session.ConfirmMember("uid2");

        Assert.Equal(OnlineSessionState.Confirming, session.State);
        session.BeginExecuting();
        Assert.Equal(OnlineSessionState.Executing, session.State);
    }

    [Fact]
    public void MarkExecuted_BecomesDone()
    {
        var session = OnlineSession.Create("ROOM1", DateTime.UtcNow, 1, TimeSpan.FromMinutes(5));
        session.MemberReady("uid1");
        session.BeginConfirming();
        session.BeginExecuting();

        session.MarkExecuted();

        Assert.Equal(OnlineSessionState.Done, session.State);
        Assert.True(session.IsTerminal);
    }

    [Fact]
    public void CheckWaitingTimeout_BelowThreshold_BecomesMissed()
    {
        var session = OnlineSession.Create("ROOM1", DateTime.UtcNow.AddMinutes(-10), 2, TimeSpan.FromMinutes(5));
        session.MemberReady("uid1");

        var timeout = session.CheckWaitingTimeout(DateTime.UtcNow);

        Assert.True(timeout);
        Assert.Equal(OnlineSessionState.Missed, session.State);
    }
}
