using Mistletoe.Shared;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class TerminalEffectJournalTests
{
    private static TerminalEffectProgress Progress(string action = "closeSoftware") => new(new string('a', 64),
        "123:456", "job", "key", "run", action, new string('B', 64), Environment.MachineName,
        DateTimeOffset.UtcNow.AddMinutes(-2), action == "shutdown" ? "shutdown_requested" : "software_exit_requested", true);

    [Fact]
    public void OriginalEffect_RequiresFullBindingAndCompletedSoftwareRequest()
    {
        var p = Progress();
        bool Match(TerminalEffectProgress v) => TerminalEffectJournal.Matches(v, p.Token, p.Epoch, p.JobId, p.Key, p.WireRunId, p.Action, p.RequestFingerprint);
        Assert.True(Match(p));
        Assert.False(Match(p with { Action = "closeGameAndSoftware" }));
        Assert.False(Match(p with { JobId = "other" }));
        Assert.False(Match(p with { Epoch = "123:457" }));
        Assert.False(Match(p with { RequestFingerprint = "wrong" }));
        Assert.False(Match(p with { Stage = "prepared" }));
        Assert.False(Match(p with { Machine = "other-machine" }));
        Assert.False(Match(p with { Key = "other" }));
    }

    [Fact]
    public void CombinedAction_NeedsIndependentGameExit()
    {
        var p = Progress("closeGameAndSoftware");
        Assert.True(TerminalEffectJournal.Matches(p, p.Token, p.Epoch, p.JobId, p.Key, p.WireRunId, p.Action, p.RequestFingerprint));
        Assert.False(TerminalEffectJournal.Matches(p with { GameExitConfirmed = false }, p.Token, p.Epoch, p.JobId, p.Key, p.WireRunId, p.Action, p.RequestFingerprint));
    }

    [Fact]
    public void Shutdown_RequestAloneIsUnknown_NeedsMatchingGracefulShutdownAndNextOsBoot()
    {
        var p = Progress("shutdown"); var at = p.StartedAtUtc;
        var request = new TerminalSystemEvent(1074, "User32", 10, at.AddSeconds(1), TerminalEffectJournal.ShutdownComment(p.Token));
        var close = new TerminalSystemEvent(6006, "EventLog", 11, at.AddSeconds(70));
        var boot = new TerminalSystemEvent(12, "Microsoft-Windows-Kernel-General", 12, at.AddSeconds(120));
        Assert.Equal(new long[] { 10, 11, 12 }, TerminalEffectJournal.ConfirmShutdown(p, [request, close, boot]));
        Assert.Null(TerminalEffectJournal.ConfirmShutdown(p, [request]));
        Assert.Null(TerminalEffectJournal.ConfirmShutdown(p, [request, close]));
        Assert.Null(TerminalEffectJournal.ConfirmShutdown(p, [request with { Comment = "another request" }, close, boot]));
        Assert.Null(TerminalEffectJournal.ConfirmShutdown(p, [request, new(1074, "User32", 15, at.AddSeconds(40), "other"), close, boot]));
        Assert.Null(TerminalEffectJournal.ConfirmShutdown(p, [request, close, new(41, "Microsoft-Windows-Kernel-Power", 16, at.AddSeconds(119)), boot]));
        Assert.Null(TerminalEffectJournal.ConfirmShutdown(p, [request, close with { AtUtc = at.AddHours(3) }, boot with { AtUtc = at.AddHours(4) }]));
    }
}
