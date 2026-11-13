using System.Collections.Concurrent;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CompletionPolicySyncTests
{
    [Fact]
    public async Task StartSettingsAreNeverSentBeforeThatPeerAdvertisesVersionOne()
    {
        var sent = new List<RemoteCommand>();
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            sent.Add(command);
            sync.TryComplete(command.CommandId, new RemoteCommand
            {
                Params = command.Target![0] == "new" ? new() { ["ok"] = "true", ["completionStartVersion"] = "1" }
                    : new() { ["ok"] = "true" }
            });
            return Task.FromResult(true);
        }, () => "self", () => "本机");
        var parameters = CompletionPolicyFields.Parameters(Settings());
        Assert.False((await sync.PushAsync("old", parameters))!.Value.ok);
        Assert.Empty(sent);
        await sync.PullAsync("new");
        await sync.PullAsync("old");
        Assert.False((await sync.PushAsync("old", parameters))!.Value.ok);
        Assert.DoesNotContain(sent, command => command.Cmd == "task_policy.push");
    }

    [Theory]
    [InlineData("missing-version")]
    [InlineData("missing-field")]
    [InlineData("changed-node")]
    [InlineData("complete")]
    public async Task OnlyMatchingVersionAndCompleteSavedEchoConfirmSuccess(string response)
    {
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            var data = command.Cmd == "task_policy.push" ? new Dictionary<string, object>(command.Params!)
                : new Dictionary<string, object> { ["completionStartVersion"] = "1" };
            data["ok"] = "true";
            data["message"] = "已持久化";
            if (command.Cmd == "task_policy.push")
            {
                if (response == "missing-version") data.Remove("completionStartVersion");
                if (response == "missing-field") data.Remove("specifiedConfigRevision");
                if (response == "changed-node") data["specifiedStartTaskId"] = "different";
            }
            sync.TryComplete(command.CommandId, new RemoteCommand { Params = data });
            return Task.FromResult(true);
        }, () => "self", () => "本机");
        await sync.PullAsync("executor");
        var result = await sync.PushAsync("executor", CompletionPolicyFields.Parameters(Settings()));
        Assert.NotNull(result);
        Assert.Equal(response == "complete", result.Value.ok);
    }

    [Fact]
    public async Task ReplacedPeerCannotReuseItsEarlierVersionAdvertisementToReceiveNewSettings()
    {
        var commands = new List<string>();
        var reads = 0;
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            commands.Add(command.Cmd);
            var data = new Dictionary<string, object> { ["ok"] = "true" };
            if (command.Cmd == "task_policy.pull" && ++reads == 1) data["completionStartVersion"] = "1";
            sync.TryComplete(command.CommandId, new RemoteCommand { Params = data });
            return Task.FromResult(true);
        }, () => "self", () => "本机");
        await sync.PullAsync("executor");
        var result = await sync.PushAsync("executor", CompletionPolicyFields.Parameters(Settings()));
        Assert.False(result!.Value.ok);
        Assert.Contains("未发送", result.Value.message);
        Assert.Equal(new[] { "task_policy.pull", "task_policy.pull" }, commands);
    }

    [Fact]
    public async Task CatalogQueriesCarryAllTargetSelectorsWithoutChangingSavedSettings()
    {
        RemoteCommand? sent = null;
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            sent = command;
            var data = CompletionPolicyFields.Parameters(Settings());
            data["ok"] = "true";
            data["catalog"] = "{\"workflows\":[],\"target\":null}";
            sync.TryComplete(command.CommandId, new RemoteCommand { Params = data });
            return Task.FromResult(true);
        }, () => "self", () => "本机");
        var result = await sync.PullAsync("executor", "workflow", "计划名", "wf-picked");
        Assert.Equal("workflow", sent!.Params!["catalogType"]);
        Assert.Equal("计划名", sent.Params["catalogName"]);
        Assert.Equal("wf-picked", sent.Params["catalogWorkflowId"]);
        Assert.Equal("wf-original", result!["specifiedWorkflowId"]);
        Assert.Equal("node-b", result["specifiedStartTaskId"]);
    }

    [Theory]
    [InlineData("group", true, "收尾")]
    [InlineData("onedragon", true, "收尾")]
    [InlineData("workflow", false, "收尾")]
    [InlineData("group", true, "")]
    [InlineData("onedragon", true, "")]
    public async Task OnlyExplicitBgiCatalogRefreshCarriesStartupIntentToTheSelectedUid(string type, bool ensure, string name)
    {
        RemoteCommand? sent = null;
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            sent = command;
            sync.TryComplete(command.CommandId, new RemoteCommand { Params = new() { ["ok"] = "true" } });
            return Task.FromResult(true);
        }, () => "controller", () => "遥控端");
        await sync.PullAsync("original-executor", type, name, "wf-1", ensureBgiReady: true);
        Assert.Equal(new[] { "original-executor" }, sent!.Target);
        Assert.Equal("task_policy.pull", sent.Cmd);
        Assert.Equal(type, sent.Params!["catalogType"]);
        Assert.Equal(name, sent.Params["catalogName"]);
        Assert.Equal(ensure, sent.Params.ContainsKey("ensureBgiReady"));
        if (ensure)
        {
            Assert.Equal("true", sent.Params["ensureBgiReady"]);
            Assert.True(sent.ExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(150));
        }
        await sync.PullAsync("original-executor", type, name, "wf-1");
        Assert.False(sent!.Params!.ContainsKey("ensureBgiReady"));
        Assert.Null(sent.ExpiresAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(8), TaskPolicySyncService.GetPullTimeout(false));
        Assert.Equal(TimeSpan.FromSeconds(180), TaskPolicySyncService.GetPullTimeout(true));
    }

    [Fact]
    public async Task CancellingAnInFlightRefreshRevokesThatRequestOnTheSameExecutor()
    {
        var sent = new List<RemoteCommand>();
        using var cancellation = new CancellationTokenSource();
        var sync = new TaskPolicySyncService(command => { sent.Add(command); return Task.FromResult(true); },
            () => "controller", () => "遥控端");
        var pending = sync.PullAsync("original-executor", "group", "收尾", "", cancellation.Token, ensureBgiReady: true);
        var original = Assert.Single(sent);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, sent.Count);
        var cancel = sent[1];
        Assert.Equal(new[] { "original-executor" }, cancel.Target);
        Assert.Equal(original.CommandId, cancel.Params!["cancelCatalogRefresh"]);
        Assert.False(cancel.Params.ContainsKey("ensureBgiReady"));
        Assert.False(sync.TryComplete(original.CommandId, new RemoteCommand { Params = new() { ["ok"] = "true" } }));
    }

    // 真实同步服务的发送回执故意挂起；撤销和本地取消必须在它完成前发生，否则不能交付关闭/换目标行为。
    [Fact]
    public async Task CancellationDuringSendRevokesTheDeliveredRequestBeforeItsSendAcknowledgement()
    {
        var sent = new ConcurrentQueue<RemoteCommand>();
        var delivered = new TaskCompletionSource<RemoteCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoked = new TaskCompletionSource<RemoteCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendAcknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderUid = "original-controller";
        using var cancellation = new CancellationTokenSource();
        var sync = new TaskPolicySyncService(command =>
        {
            sent.Enqueue(command);
            if (command.Params!.ContainsKey("cancelCatalogRefresh"))
            {
                revoked.TrySetResult(command);
                return Task.FromResult(true);
            }
            delivered.TrySetResult(command);
            return sendAcknowledgement.Task;
        }, () => senderUid, () => "遥控端");
        var pending = sync.PullAsync("original-executor", "group", "收尾", "", cancellation.Token, ensureBgiReady: true);
        try
        {
            var original = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(sendAcknowledgement.Task.IsCompleted);
            senderUid = "replacement-controller";
            cancellation.Cancel();
            var cancel = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(sendAcknowledgement.Task.IsCompleted);
            Assert.Equal(original.SenderUid, cancel.SenderUid);
            Assert.Equal(original.Target, cancel.Target);
            Assert.Equal(original.CommandId, cancel.Params!["cancelCatalogRefresh"]);
            Assert.False(cancel.Params.ContainsKey("ensureBgiReady"));
            Assert.False(sync.TryComplete(original.CommandId, new RemoteCommand { Params = new() { ["ok"] = "true" } }));
            sendAcknowledgement.SetResult(true);
            Assert.Equal(2, sent.Count);
        }
        finally
        {
            cancellation.Cancel();
            sendAcknowledgement.TrySetResult(true);
        }
    }

    [Fact]
    public async Task SuccessfulRefreshDoesNotSendRevocationEvenWhenItsCallerLaterCancels()
    {
        var sent = new List<RemoteCommand>();
        using var cancellation = new CancellationTokenSource();
        TaskPolicySyncService sync = null!;
        sync = new TaskPolicySyncService(command =>
        {
            sent.Add(command);
            sync.TryComplete(command.CommandId, new RemoteCommand { Params = new() { ["ok"] = "true", ["catalog"] = "{}" } });
            return Task.FromResult(true);
        }, () => "controller", () => "遥控端");
        var result = await sync.PullAsync("executor", "group", "收尾", "", cancellation.Token, ensureBgiReady: true)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        cancellation.Cancel();
        var original = Assert.Single(sent);
        Assert.Equal("true", original.Params!["ensureBgiReady"]);
        Assert.False(original.Params.ContainsKey("cancelCatalogRefresh"));
    }

    [Fact]
    public async Task CancellationBeforeSendCannotEnqueueStartupOrCancellationMessages()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sync = new TaskPolicySyncService(_ => throw new InvalidOperationException("取消后不能发送"), () => "self", () => "本机");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.PullAsync("executor", "group", "收尾", "",
            cancellation.Token, ensureBgiReady: true));
    }

    private static TaskConflictPolicySettings Settings() => new()
    {
        Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = "workflow", SpecifiedTaskName = "收尾计划",
        SpecifiedTaskPriority = 23, SpecifiedStartMode = "node", SpecifiedStartTaskId = "node-b",
        SpecifiedStartTaskName = "收取", SpecifiedConfigRevision = "rev-7", SpecifiedWorkflowId = "wf-original"
    };
}
