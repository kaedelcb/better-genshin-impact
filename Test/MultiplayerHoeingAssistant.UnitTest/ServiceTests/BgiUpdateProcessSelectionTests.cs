using System.ComponentModel;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 更新关闭仅能选择会话、路径和存活状态均确认的目标；这些夹具弥补状态文件测试无法观察误杀选择的缺口。
/// 全部探测使用假实例/委托，不枚举、不启动、不结束真实进程；以所选对象及 unknown 拒绝结果作为结束判据。
/// </summary>
public sealed class BgiUpdateProcessSelectionTests
{
    private const string TargetExecutable = @"C:\BGI\BetterGI.exe";
    private sealed record Candidate(int? Session, bool? Exited, string? Executable);

    private static Candidate[] Select(params Candidate[] candidates)
        => BgiProcessMonitor.SelectConfirmedBgiProcessesCore(candidates, 4, TargetExecutable,
            candidate => candidate.Session ?? throw new Win32Exception(5),
            candidate => candidate.Exited ?? throw new InvalidOperationException("存活状态未知"),
            candidate => candidate.Executable);

    [Fact]
    public void WatcherSelection_OnlySelectsLiveCurrentSessionExactExecutable()
    {
        var target = new Candidate(4, false, @"c:\BGI\temp\..\BETTERGI.exe");
        var otherInstallation = new Candidate(4, false, @"C:\BGI-other\BetterGI.exe");
        var otherSession = new Candidate(5, false, TargetExecutable);
        var nestedExecutable = new Candidate(4, false, @"C:\BGI\nested\BetterGI.exe");
        var exited = new Candidate(4, true, TargetExecutable);

        Assert.Same(target, Assert.Single(Select(otherInstallation, otherSession, target, nestedExecutable, exited)));
    }

    [Fact]
    public void WatcherSelection_UnknownOrExitedCannotCountAsPreviouslyRunning()
    {
        Assert.Empty(Select(
            new Candidate(null, false, TargetExecutable),
            new Candidate(4, null, TargetExecutable),
            new Candidate(4, false, null),
            new Candidate(4, false, ""),
            new Candidate(4, false, @"BGI\BetterGI.exe"),
            new Candidate(4, true, TargetExecutable)));
    }

    [Fact]
    public void IdentityProbe_ExitDuringPathReadRejectsPreviouslyLiveTarget()
    {
        var exited = false;
        Assert.False(BgiProcessMonitor.IsConfirmedProcessIdentityCore(TargetExecutable, false,
            () => exited, () => { exited = true; return TargetExecutable; }, () => true));
    }

    [Theory]
    [InlineData("lifetime")]
    [InlineData("session")]
    [InlineData("path")]
    [InlineData("final-lifetime")]
    public void IdentityProbe_AnyUnknownIdentityIsRejected(string failedProbe)
    {
        var lifetimeReads = 0;
        Assert.False(BgiProcessMonitor.IsConfirmedProcessIdentityCore(TargetExecutable, false,
            () =>
            {
                lifetimeReads++;
                if (failedProbe == "lifetime" || (failedProbe == "final-lifetime" && lifetimeReads == 2))
                    throw new InvalidOperationException("存活状态未知");
                return false;
            },
            () => failedProbe == "path" ? throw new Win32Exception(5) : TargetExecutable,
            () => failedProbe == "session" ? throw new Win32Exception(5) : true));
    }

    [Theory]
    [InlineData(@"C:\BGI\BetterGI.exe", @"C:\BGI", true)]
    [InlineData(@"c:\bgi\child\..\BetterGI.exe", @"C:\BGI\", true)]
    [InlineData(@"C:\BGI\child\BetterGI.exe", @"C:\BGI", true)]
    [InlineData(@"C:\BGI-other\BetterGI.exe", @"C:\BGI", false)]
    [InlineData(@"C:\BGI\..\other\BetterGI.exe", @"C:\BGI", false)]
    [InlineData(@"C:\BGI", @"C:\BGI", false)]
    [InlineData(@"C:\BGI\BetterGI.exe", @"C:\", true)]
    [InlineData(null, @"C:\BGI", false)]
    [InlineData("", @"C:\BGI", false)]
    [InlineData(@"BGI\BetterGI.exe", @"C:\BGI", false)]
    [InlineData(@"C:\BGI\BetterGI.exe", "BGI", false)]
    [InlineData(@"C:\BGI\BetterGI.exe", "", false)]
    [InlineData(@"C:\BGI\BetterGI.exe", "C:\\BGI\0", false)]
    public void DirectoryFallback_RequiresNormalizedPathInsideDirectory(string? executable, string directory, bool expected)
    {
        Assert.Equal(expected, BgiProcessMonitor.IsConfirmedProcessIdentityCore(directory, true,
            () => false, () => executable));
    }

    [Fact]
    public void DirectoryFallback_ExitedOrUnreadableProcessIsRejected()
    {
        Assert.False(BgiProcessMonitor.IsConfirmedProcessIdentityCore(@"C:\BGI", true,
            () => true, () => TargetExecutable));
        Assert.False(BgiProcessMonitor.IsConfirmedProcessIdentityCore(@"C:\BGI", true,
            () => false, () => throw new Win32Exception(5)));
    }

    [Fact]
    public void CloseRecheck_DoesNotReuseEarlierConfirmedIdentity()
    {
        var session = 4;
        var executable = TargetExecutable;
        var exited = false;
        bool Confirm() => BgiProcessMonitor.IsConfirmedProcessIdentityCore(TargetExecutable, false,
            () => exited, () => executable, () => session == 4);

        Assert.True(Confirm());
        session = 5;
        Assert.False(Confirm());
        session = 4;
        executable = @"C:\other\BetterGI.exe";
        Assert.False(Confirm());
        executable = TargetExecutable;
        exited = true;
        Assert.False(Confirm());
    }
}
