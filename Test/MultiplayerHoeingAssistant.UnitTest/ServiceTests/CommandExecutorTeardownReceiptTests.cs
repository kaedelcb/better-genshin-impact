using System.Diagnostics;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>收尾回执必须代表恢复/释放已确认；真实私有 IPC 帧验证 stale 之后的同纪元撤销证明。</summary>
[Collection("TaskCenterPipeTransport")]
public sealed class CommandExecutorTeardownReceiptTests : IAsyncLifetime
{
    private readonly BgiInstancePipeDouble _pipe = new();
    private string? _previousPipe;

    public Task InitializeAsync()
    {
        _previousPipe = IpcClient.PipeNameOverrideForTest;
        IpcClient.PipeNameOverrideForTest = _pipe.PipeName;
        _pipe.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        IpcClient.PipeNameOverrideForTest = _previousPipe;
        await _pipe.DisposeAsync();
    }

    private static BgiEpoch Epoch()
    {
        using var process = Process.GetCurrentProcess();
        return new BgiEpoch { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
    }

    private static object Status(BgiEpoch epoch, bool revoked) => new
    {
        running = false, taskRunning = false, executionIdle = true,
        slotOccupied = false, executionSlotOccupied = false,
        takeoverActive = !revoked, hasSuspendedTaskContext = false,
        bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
    };

    private static CommandExecutor Seed(string ticket, BgiEpoch epoch,
        Func<ExternalStartAdmissionRequest, CancellationToken, Task<ExternalStartAdmissionOutcome>>? admission = null)
    {
        // 此测试不枚举或启动真实 BGI，进程事实仅保持“不能证明 BGI 已退出”的保守分支。
        var executor = new CommandExecutor(null!, "unused", externalStartAdmission: admission)
        { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest(ticket, epoch);
        return executor;
    }

    [Theory]
    [InlineData("stale_ticket", false)]
    [InlineData("stale_context", false)]
    [InlineData("stale_ticket", true)]
    [InlineData("stale_context", true)]
    public async Task StaleResume_IsSuccessfulOnlyAfterSameEpochRevocationProof(string staleCode, bool revoked)
    {
        var epoch = Epoch();
        var ticket = Guid.NewGuid().ToString("N");
        _pipe.BodyForTest = (operation, _) => operation switch
        {
            "task.resume" => (false, staleCode, "隔离替身：原票据已失效", null),
            "task.status" => (true, null, null, Status(epoch, revoked)),
            _ => null,
        };
        var executor = Seed(ticket, epoch);

        var result = await executor.ApplyPolicyTeardownAsync(new TaskConflictPolicySettings
        { Policy = TaskConflictPolicy.Stop }, "联机锄地", userCancelled: false, expectedTicket: ticket)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(revoked ? "success" : "failed", result.Status);
        Assert.Equal(revoked ? "already_revoked" : staleCode, result.ErrorCode);
        Assert.Equal(revoked ? null : ticket, executor.TakeoverTicketSnapshot);
        Assert.Equal(1, _pipe.CountOf("task.resume"));
        Assert.Equal(1, _pipe.CountOf("task.status"));
        var resume = Assert.Single(_pipe.Received.Where(r => r.Operation == "task.resume")).Data!.Value;
        Assert.Equal(ticket, resume.GetProperty("takeoverTicket").GetString());
        Assert.True(resume.GetProperty("cancel").GetBoolean());
        var operations = _pipe.Received.Select(request => request.Operation).ToList();
        Assert.True(operations.IndexOf("task.resume") < operations.IndexOf("task.status"));
        Assert.Equal(0, _pipe.CountOf("task.start"));
        Assert.Equal(0, _pipe.CountOf("ext.task.start"));
    }

    [Fact]
    public async Task ResumeWhenStatusIsUnreadable_FailsAndPreservesResponsibilityWithoutSendingResume()
    {
        var epoch = Epoch();
        var ticket = Guid.NewGuid().ToString("N");
        _pipe.BodyForTest = (operation, _) => operation == "task.status"
            ? (false, "state_unavailable", "隔离替身：状态不可读", null) : null;
        var executor = Seed(ticket, epoch);

        var result = await executor.ApplyPolicyTeardownAsync(new TaskConflictPolicySettings
        { Policy = TaskConflictPolicy.Resume }, "联机锄地", userCancelled: false, expectedTicket: ticket)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("failed", result.Status);
        Assert.Equal("result_unknown", result.ErrorCode);
        Assert.Equal(ticket, executor.TakeoverTicketSnapshot);
        Assert.Equal(1, _pipe.CountOf("task.status"));
        Assert.Equal(0, _pipe.CountOf("task.resume"));
    }

    [Fact]
    public async Task StopWhenReleaseIsRejected_PropagatesFailureAndRetainsTicket()
    {
        var ticket = Guid.NewGuid().ToString("N");
        _pipe.BodyForTest = (operation, _) => operation == "task.resume"
            ? (false, "takeover_conflict", "隔离替身：释放未获确认", null) : null;
        var executor = Seed(ticket, Epoch());

        var result = await executor.ApplyPolicyTeardownAsync(new TaskConflictPolicySettings
        { Policy = TaskConflictPolicy.Stop }, "联机锄地", userCancelled: false, expectedTicket: ticket)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("failed", result.Status);
        Assert.Equal("takeover_conflict", result.ErrorCode);
        Assert.Equal(ticket, executor.TakeoverTicketSnapshot);
        Assert.Equal(1, _pipe.CountOf("task.resume"));
        Assert.Equal(0, _pipe.CountOf("task.start"));
    }

    [Theory]
    [InlineData("group", "group:隔离收尾任务")]
    [InlineData("onedragon", "onedragon:隔离收尾任务")]
    public async Task RunSpecified_UsesPerCallAdmissionAndPropagatesRefusalAfterConfirmedRelease(string type, string resource)
    {
        var ticket = Guid.NewGuid().ToString("N");
        var beforeAdmission = false;
        ExternalStartAdmissionRequest? received = null;
        _pipe.BodyForTest = (operation, _) => operation switch
        {
            "task.resume" => (true, null, null, new { status = "cleared_not_resumed" }),
            "config.list" => (true, null, null, new { configGroups = new[] { "隔离收尾任务" },
                oneClickConfigs = new[] { "隔离收尾任务" } }),
            _ => null,
        };
        var executor = Seed(ticket, Epoch(), (_, _) => throw new InvalidOperationException("不得借用普通启动的全局准入委托"));

        var result = await executor.ApplyPolicyTeardownAsync(new TaskConflictPolicySettings
        {
            Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskName = "隔离收尾任务",
            SpecifiedTaskType = type, SpecifiedTaskPriority = 17,
        }, "联机锄地", userCancelled: false, expectedTicket: ticket,
            specifiedStartAdmission: (request, _) =>
            {
                Assert.True(beforeAdmission);
                Assert.Null(executor.TakeoverTicketSnapshot);
                received = request;
                return Task.FromResult(new ExternalStartAdmissionOutcome(ExternalStartAdmissionStatus.Blocked,
                    "isolated_admission_refused", "隔离替身：新任务未获准"));
            }, beforeSpecifiedStart: () => { beforeAdmission = true; return Task.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(received);
        Assert.Equal(resource, received!.ResourceRef);
        Assert.Equal(17, received.Priority);
        Assert.Equal("failed", result.Status);
        Assert.Equal("isolated_admission_refused", result.ErrorCode);
        Assert.Null(executor.TakeoverTicketSnapshot);
        Assert.Equal(1, _pipe.CountOf("task.resume"));
        Assert.Equal(1, _pipe.CountOf("config.list"));
        Assert.Equal(0, _pipe.CountOf("task.start"));
        Assert.Equal(0, _pipe.CountOf("ext.task.start"));
    }
}
