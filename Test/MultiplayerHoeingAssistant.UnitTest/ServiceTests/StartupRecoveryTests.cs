using System.Diagnostics;
using System.IO.Pipes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class StartupRecoveryTests
{
    [Theory]
    [InlineData("io")]
    [InlineData(BgiNotSentException.ChannelNotReady)]
    [InlineData(BgiNotSentException.PipeNotConnected)]
    public async Task ConnectionFailure_ResumesSameSourceAndDoesNotReplayPreviousAction(string failure)
    {
        var source = StartupSourceIntent.Startup();
        var checks = 0;
        var actions = 0;
        var runner = new StartupFlowRunner((_, _) =>
            { actions++; return Task.FromResult(new CommandResult { Status = "success" }); },
            (_, _) => Task.FromResult(StartupHandoffResult.Accepted("run", null)), _ => { },
            (_, _) => Task.FromResult((true, "ok")), _ => { }, () => null, _ => { },
            sourceAuthorityProvider: (intent, _, ct) => StartupSourceRecovery.RefreshAsync(intent, _ =>
            {
                checks++;
                if (checks == 2)
                {
                    if (failure == "io") throw new IOException("暂时断线");
                    throw new BgiNotSentException(failure, "读取前连接断开");
                }
                return Task.FromResult<WorkflowStopAuthorityRecord?>(new("42:99", 0,
                    intent.IntentId, intent.IntentTimestamp, Stopwatch.Frequency));
            }, ct, retryDelay: TimeSpan.FromMilliseconds(10)));
        await source.GetOrCheckAsync((intent, _, _) => Task.FromResult<WorkflowStopAuthorityRecord?>(
            new("42:99", 0, intent.IntentId, intent.IntentTimestamp, Stopwatch.Frequency)), true, default);
        await runner.RunAsync([new() { Kind = StartupStepKinds.StartGroup, TaskName = "first" },
            new() { Kind = StartupStepKinds.StartGroup, TaskName = "second" }], default, sourceIntent: source);
        Assert.Equal(2, actions);
        Assert.Equal(3, checks);
        Assert.False(source.Revoked);
    }

    [Fact]
    public async Task StopDuringRetry_CancelsWaitAndNeverRefreshesTheOldSourceAgain()
    {
        var source = StartupSourceIntent.Startup();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var waiting = source.GetOrCheckAsync((intent, _, ct) => StartupSourceRecovery.RefreshAsync(intent, _ =>
        {
            calls++; attempted.TrySetResult();
            throw new TimeoutException();
        }, ct, retryDelay: TimeSpan.FromSeconds(30)), true, default);
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.StopCurrentRun();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, calls);
        Assert.True(source.StoppedByUser);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetOrCheckAsync((_, _, _) =>
            throw new Exception("旧回调不能再次执行"), true, default));
        Assert.False(StartupSourceIntent.Startup().Revoked);
    }

    [Fact]
    public async Task SameSessionEndpoint_WinsOverRootAndOtherSession()
    {
        var root = "startup-session-" + Guid.NewGuid().ToString("N");
        var session = Process.GetCurrentProcess().SessionId;
        using var own = Server(BgiSessionPipe.Name(root, session));
        using var other = Server(BgiSessionPipe.Name(root, session + 1));
        using var legacy = Server(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = own.WaitForConnectionAsync(timeout.Token);
        using var client = await BgiSessionPipe.ConnectAsync(root, true, 1000, timeout.Token);
        await accepted;
        Assert.True(own.IsConnected);
        Assert.False(other.IsConnected);
        Assert.False(legacy.IsConnected);
    }

    [Fact]
    public async Task OldBgiWithoutSessionEndpoint_CanUseRootCompatibilityConnection()
    {
        var root = "startup-legacy-" + Guid.NewGuid().ToString("N");
        using var legacy = Server(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = legacy.WaitForConnectionAsync(timeout.Token);
        using var client = await BgiSessionPipe.ConnectAsync(root, true, 1000, timeout.Token);
        await accepted;
        Assert.True(legacy.IsConnected);
    }

    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut,
        1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
}
