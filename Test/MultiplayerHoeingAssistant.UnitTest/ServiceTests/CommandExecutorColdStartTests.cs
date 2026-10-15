using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class CommandExecutorColdStartTests : IAsyncLifetime
{
    private readonly BgiInstancePipeDouble _bgi = new();
    private string? _previousPipe;

    public Task InitializeAsync()
    {
        _previousPipe = IpcClient.PipeNameOverrideForTest;
        IpcClient.PipeNameOverrideForTest = _bgi.PipeName;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        IpcClient.PipeNameOverrideForTest = _previousPipe;
        await _bgi.DisposeAsync();
    }

    private static RemoteCommand StartCommand(string command) => new()
    {
        Cmd = command,
        Params = new()
        {
            [command == "start_group" ? "groupName" : "configName"] = "冷启动回归任务",
        },
    };

    [Theory]
    [InlineData("start_group")]
    [InlineData("start_oneclick")]
    public async Task BgiNotRunning_LaunchesBeforeStatusQuery_ThenSendsTaskOnce(string command)
    {
        using var monitor = new BgiProcessMonitor("unused");
        var starts = 0;
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => starts > 0,
            TaskStartBgiLaunchOverride = () =>
            {
                starts++;
                _bgi.Start();
                return true;
            },
        };

        var result = await executor.ExecuteAsync(StartCommand(command)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("success", result.Status);
        Assert.Equal(1, starts);
        Assert.True(_bgi.CountOf("task.status") > 0);
        Assert.Equal(1, _bgi.CountOf("task.start"));
        Assert.Equal(0, _bgi.CountOf("task.stop"));
        Assert.Equal(0, _bgi.CountOf("task.suspend"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task ExistingOrUncertainProcess_UnknownStatusDoesNotLaunchOrSend(bool? present)
    {
        using var monitor = new BgiProcessMonitor("unused");
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => present,
            TaskStartBgiLaunchOverride = () => throw new Xunit.Sdk.XunitException("不得启动或重启现有/身份未知的 BGI"),
            TaskStatusQueryOverride = _ => Task.FromResult<(bool, bool, string?, string?)?>(null),
        };

        var result = await executor.ExecuteAsync(StartCommand("start_oneclick"));

        Assert.Equal("failed", result.Status);
        Assert.Equal("status_unknown", result.ErrorCode);
        Assert.Empty(_bgi.Received);
    }

    [Theory]
    [InlineData("start_group")]
    [InlineData("start_oneclick")]
    public async Task LaunchFails_ReturnsActionableFailureWithoutSending(string command)
    {
        using var monitor = new BgiProcessMonitor("unused");
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => false,
            TaskStartBgiLaunchOverride = () => false,
        };

        var result = await executor.ExecuteAsync(StartCommand(command));

        Assert.Equal("failed", result.Status);
        Assert.Equal("bgi_start_failed", result.ErrorCode);
        Assert.Contains("未发送任务", result.Message);
        Assert.Empty(_bgi.Received);
    }

    [Fact]
    public async Task ColdStartStatusNotReady_WaitsForTrustedStatusBeforeSending()
    {
        using var monitor = new BgiProcessMonitor("unused");
        var statusReads = 0;
        _bgi.BodyForTest = (operation, _) => operation == "task.status" && ++statusReads == 1
            ? (false, "initializing", "BGI 正在初始化", null)
            : null;
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => false,
            TaskStartBgiLaunchOverride = () => { _bgi.Start(); return true; },
        };

        var result = await executor.ExecuteAsync(StartCommand("start_oneclick")).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("success", result.Status);
        Assert.True(statusReads >= 3); // 初始化失败 → 就绪 → 原冲突策略复查。
        var operations = _bgi.Received.Select(request => request.Operation).ToList();
        Assert.True(operations.LastIndexOf("task.status") < operations.IndexOf("task.start"));
        Assert.Equal(1, _bgi.CountOf("task.start"));
    }

    [Fact]
    public async Task ColdStartStatusNeverReady_ReportsTimeoutWithoutSending()
    {
        using var monitor = new BgiProcessMonitor("unused");
        var starts = 0;
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => false,
            TaskStartBgiLaunchOverride = () => { starts++; return true; },
            TaskStatusQueryOverride = _ => Task.FromResult<(bool, bool, string?, string?)?>(null),
            TaskStartReadyTimeoutForTest = TimeSpan.FromMilliseconds(30),
        };

        var result = await executor.ExecuteAsync(StartCommand("start_oneclick"));

        Assert.Equal("bgi_start_timeout", result.ErrorCode);
        Assert.Equal(1, starts);
        Assert.Empty(_bgi.Received);
    }

    [Fact]
    public async Task ColdStartWrongProcessIdentity_NeverSendsTask()
    {
        using var monitor = new BgiProcessMonitor("unused");
        // Ping 身份与实际 testhost 不匹配，真实 IpcClient 必须拒绝采信。
        _bgi.ReportedProcessIdOverride = System.Diagnostics.Process.GetCurrentProcess().Id + 1;
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => false,
            TaskStartBgiLaunchOverride = () => { _bgi.Start(); return true; },
            TaskStartReadyTimeoutForTest = TimeSpan.FromMilliseconds(100),
        };

        var result = await executor.ExecuteAsync(StartCommand("start_oneclick"));

        Assert.Equal("bgi_start_timeout", result.ErrorCode);
        Assert.Equal(0, _bgi.CountOf("task.start"));
    }

    [Fact]
    public async Task RequestExpiresWhileStarting_DoesNotSendTaskAfterReadiness()
    {
        using var monitor = new BgiProcessMonitor("unused");
        var command = StartCommand("start_oneclick");
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => false,
            TaskStartBgiLaunchOverride = () => true,
            TaskStatusQueryOverride = _ =>
            {
                command.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
                return Task.FromResult<(bool, bool, string?, string?)?>((false, false, null, null));
            },
        };

        var result = await executor.ExecuteAsync(command);

        Assert.Equal("request_expired", result.ErrorCode);
        Assert.Empty(_bgi.Received);
    }

    [Fact]
    public async Task ConcurrentColdStarts_ShareOneLaunchAndWaitForReadiness()
    {
        using var monitor = new BgiProcessMonitor("unused");
        var starts = 0;
        var statusReads = 0;
        var firstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new CommandExecutor(monitor, "unused")
        {
            TaskStartBgiPresenceProbeOverride = () => starts > 0,
            TaskStartBgiLaunchOverride = () => { starts++; _bgi.Start(); return true; },
            TaskStatusQueryOverride = async _ =>
            {
                if (Interlocked.Increment(ref statusReads) == 1)
                {
                    firstProbe.SetResult();
                    await ready.Task;
                }
                return (false, false, null, null);
            },
        };

        var first = executor.ExecuteAsync(StartCommand("start_group"));
        await firstProbe.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = executor.ExecuteAsync(StartCommand("start_oneclick"));
        Assert.Equal(1, starts);
        Assert.Equal(0, _bgi.CountOf("task.start"));
        ready.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.Equal("success", result.Status));
        Assert.Equal(1, starts);
        Assert.Equal(2, _bgi.CountOf("task.start"));
    }
}
