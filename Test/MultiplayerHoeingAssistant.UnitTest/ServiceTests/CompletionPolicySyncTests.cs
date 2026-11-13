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

    private static TaskConflictPolicySettings Settings() => new()
    {
        Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = "workflow", SpecifiedTaskName = "收尾计划",
        SpecifiedTaskPriority = 23, SpecifiedStartMode = "node", SpecifiedStartTaskId = "node-b",
        SpecifiedStartTaskName = "收取", SpecifiedConfigRevision = "rev-7", SpecifiedWorkflowId = "wf-original"
    };
}
