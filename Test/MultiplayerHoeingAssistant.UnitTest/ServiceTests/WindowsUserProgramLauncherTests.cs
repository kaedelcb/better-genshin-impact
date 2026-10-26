using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class WindowsUserProgramLauncherTests
{
    private const string TargetSid = "S-1-5-21-123-456-789-1001";
    private const string OtherSid = "S-1-5-21-123-456-789-1002";

    [Fact]
    public void SessionSelection_UsesExactAccountSidAndExcludesServiceSession()
    {
        var selected = WindowsUserProgramLauncher.SelectSession(TargetSid, "PC\\YS",
        [new(0, TargetSid, "PC\\YS"), new(1, OtherSid, "PC\\current"), new(2, TargetSid, "PC\\YS")]);
        Assert.Equal(2, selected.SessionId);
        Assert.Equal(TargetSid, selected.UserSid);
    }

    [Fact]
    public void SessionSelection_LoggedOutUserDoesNotFallBackToCurrentSession()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsUserProgramLauncher.SelectSession(TargetSid, "PC\\YS", [new(1, OtherSid, "PC\\current")]));
        Assert.Contains("尚未登录", error.Message);
        Assert.Contains("不会改在当前用户", error.Message);
    }

    [Fact]
    public void SessionSelection_RejectsAmbiguousDesktopsOfSameUser()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            WindowsUserProgramLauncher.SelectSession(TargetSid, "PC\\YS",
                [new(2, TargetSid, "PC\\YS"), new(3, TargetSid, "PC\\YS")]));
        Assert.Contains("多个登录会话", error.Message);
    }

    [Theory]
    [InlineData("YS", "YS")]
    [InlineData("  .\\YS  ", "YS")]
    public void BareAndDotAccountNamesResolveToThisComputer(string input, string user)
        => Assert.Equal(Environment.MachineName + "\\" + user, WindowsUserProgramLauncher.NormalizeAccountName(input));

    [Theory]
    [InlineData("DOMAIN\\YS")]
    [InlineData("user@example.com")]
    public void QualifiedAccountNamesArePreserved(string name)
        => Assert.Equal(name, WindowsUserProgramLauncher.NormalizeAccountName(name));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("YS\nother")]
    public void InvalidAccountNameIsRejected(string name)
        => Assert.Throws<InvalidOperationException>(() => WindowsUserProgramLauncher.NormalizeAccountName(name));

    [Fact]
    public void UsernameSurvivesNestedConfigAndSavedSchemeSerialization()
    {
        var scheme = new StartupFlowScheme
        {
            Config = new StartupFlowConfig
            {
                Steps = [new() { TrueSteps = [new() { Kind = StartupStepKinds.StartProgram, WindowsUserName = "PC\\YS" }] }]
            }
        };
        var restored = JsonSerializer.Deserialize<StartupFlowScheme>(JsonSerializer.Serialize(scheme))!;
        Assert.Equal("PC\\YS", restored.Config.Steps[0].TrueSteps[0].WindowsUserName);
        Assert.Equal("", JsonSerializer.Deserialize<StartupStep>("{\"kind\":\"startProgram\"}")!.WindowsUserName);
    }

    [Fact]
    public void ProgramSummaryShowsUserWhileGameSummaryKeepsExistingScope()
    {
        var program = new StartupStep { Kind = StartupStepKinds.StartProgram, Path = "app.exe", WindowsUserName = "YS" };
        Assert.Contains("Windows 用户：YS", StartupStepViewModel.BuildSummary(program));
        program.Kind = StartupStepKinds.StartGame;
        Assert.Equal("app.exe", StartupStepViewModel.BuildSummary(program));
        program.Kind = StartupStepKinds.StartProgram;
        program.WindowsUserName = " ";
        Assert.Equal("app.exe", StartupStepViewModel.BuildSummary(program));
    }

    [Fact]
    public async Task RunnerPassesUsernamePathAndLiteralArgumentsToUserLauncher()
    {
        var args = "--name \"hello world\" & echo untouched";
        (string Path, string Args, string User)? launch = null;
        var (runner, states) = MakeRunner((path, arguments, user, _) =>
        {
            launch = (path, arguments, user);
            return Task.FromResult("已在目标用户启动");
        });
        var step = new StartupStep { Kind = StartupStepKinds.StartProgram, Path = @"D:\Program Files\App.exe", Arguments = args, WindowsUserName = " YS " };
        await runner.RunAsync([step], CancellationToken.None);
        Assert.Equal((step.Path, args, "YS"), launch!.Value);
        Assert.Equal(NodeRunState.Success, states[^1].State);
    }

    [Fact]
    public async Task RunnerShowsCrossUserFailureAndDoesNotTryCurrentUserLaunch()
    {
        var calls = 0;
        var (runner, states) = MakeRunner((_, _, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("目标用户尚未登录");
        });
        // 路径故意不存在：如果回退旧 StartProcess，将丢失目标账号失败原因。
        await runner.RunAsync([new() { Kind = StartupStepKinds.StartProgram, Path = "nonexistent.exe", WindowsUserName = "YS" }], CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(NodeRunState.Failed, states[^1].State);
        Assert.Equal("目标用户尚未登录", states[^1].Note);
    }

    [Theory]
    [InlineData(StartupStepKinds.StartProgram, " ")]
    [InlineData(StartupStepKinds.StartGame, "YS")]
    public async Task ExistingCurrentUserAndGameRouteDoNotInvokeCrossUserLauncher(string kind, string user)
    {
        var called = false;
        var (runner, states) = MakeRunner((_, _, _, _) =>
        {
            called = true;
            return Task.FromResult("should not run");
        });
        await runner.RunAsync([new() { Kind = kind, WindowsUserName = user }], CancellationToken.None);
        Assert.False(called);
        Assert.Equal(NodeRunState.Failed, states[^1].State); // 原路径校验仍拒绝空路径。
    }

    [Fact]
    public async Task CancelledFlowDoesNotDispatchProgramLaunch()
    {
        var called = false;
        var (runner, _) = MakeRunner((_, _, _, _) => { called = true; return Task.FromResult("unexpected"); });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            [new() { Kind = StartupStepKinds.StartProgram, WindowsUserName = "YS" }], cts.Token));
        Assert.False(called);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsUserProgramLauncher.StartAsync("", "", "YS", cts.Token));
    }

    [Fact]
    public async Task MalformedHelperInvocationIsHandledWithoutNormalAssistantStartup()
    {
        Assert.True(WindowsUserProgramLauncher.IsHelperInvocation(["bad", WindowsUserProgramLauncher.HelperArgument]));
        Assert.False(WindowsUserProgramLauncher.IsHelperInvocation(["--minimized"]));
        Assert.Equal(2, await WindowsUserProgramLauncher.RunHelperAsync([WindowsUserProgramLauncher.HelperArgument]));
    }

    private static (StartupFlowRunner Runner, List<(NodeRunState State, string? Note)> States) MakeRunner(
        Func<string, string, string, CancellationToken, Task<string>> launcher)
    {
        var states = new List<(NodeRunState, string?)>();
        var runner = new StartupFlowRunner(
            (_, _) => Task.FromResult(new CommandResult { Status = "success" }),
            (_, _) => Task.FromResult(StartupHandoffResult.Accepted("test", null)),
            _ => { }, (_, _) => Task.FromResult((true, "test")), _ => { }, () => null, _ => { },
            userProgramLauncher: launcher);
        runner.NodeStateSink = (_, state, note) => states.Add((state, note));
        return (runner, states);
    }
}
