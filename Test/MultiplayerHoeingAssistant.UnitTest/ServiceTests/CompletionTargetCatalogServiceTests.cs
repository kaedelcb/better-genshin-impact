using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CompletionTargetCatalogServiceTests
{
    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    public async Task LiveDescriptionValidatesStableNodeRevisionAndEnabledState(string type)
    {
        var transport = new Transport();
        var service = new CompletionTargetCatalogService(() => [], _ => throw new InvalidOperationException(), transport);
        var catalog = await service.LoadAsync(type, "收尾");
        Assert.Equal(BgiExternalCatalogTransport.OpConfigDescribe, transport.Operation);
        using var payload = JsonDocument.Parse(transport.Payload!);
        Assert.Equal("收尾", payload.RootElement.GetProperty(type == "group" ? "groupName" : "configName").GetString());
        Assert.Equal("rev-1", catalog.Target!.Revision);
        var settings = Settings(type);
        Assert.Null(await service.ValidateAsync(settings));
        settings.SpecifiedStartTaskId = "missing";
        Assert.Contains("删除或禁用", (await service.ValidateAsync(settings))!);
        settings.SpecifiedStartTaskId = "disabled";
        Assert.Contains("删除或禁用", (await service.ValidateAsync(settings))!);
        settings.SpecifiedStartTaskId = "task-b";
        settings.SpecifiedConfigRevision = "older";
        Assert.Contains("已修改", (await service.ValidateAsync(settings))!);
        settings.SpecifiedStartMode = "beginning";
        settings.SpecifiedStartTaskId = settings.SpecifiedStartTaskName = "";
        Assert.NotNull(await service.ValidateAsync(settings));
        settings.SpecifiedConfigRevision = "rev-1";
        Assert.Null(await service.ValidateAsync(settings));
    }

    [Fact]
    public async Task WorkflowCatalogUsesSavedIdsAndExcludesCandidatesUnsupportedOrUnstableNodes()
    {
        var documents = new Dictionary<string, WorkflowSnapshot>
        {
            ["wf-ready"] = Snapshot("wf-ready"),
            ["wf-candidate"] = Snapshot("wf-candidate", activation: "candidate-ready"),
            ["wf-unknown"] = Snapshot("wf-unknown", kind: "unknown.kind"),
            ["wf-unstable"] = Snapshot("wf-unstable", nodeId: "")
        };
        var entries = documents.Select(pair => new WorkflowCatalogEntry(pair.Key, pair.Key, pair.Value.Revision,
            WorkflowFileStatus.Ready, null, [], null, pair.Key + ".flow.json")).ToArray();
        var service = new CompletionTargetCatalogService(() => entries, id => documents[id], null);
        var catalog = await service.LoadAsync("workflow", "旧显示名", "wf-ready");
        var ready = Assert.Single(catalog.Workflows);
        Assert.Equal("wf-ready", ready.WorkflowId);
        Assert.Equal("实际计划名", catalog.Target!.Name);
        Assert.Equal("node-b", catalog.Target.Nodes[0].TaskId);
        var settings = Settings("workflow");
        settings.SpecifiedTaskName = "实际计划名";
        settings.SpecifiedWorkflowId = "wf-ready";
        settings.SpecifiedStartTaskId = "node-b";
        Assert.Null(await service.ValidateAsync(settings));
        settings.SpecifiedWorkflowId = "wf-candidate";
        Assert.NotNull(await service.ValidateAsync(settings));
        settings.SpecifiedWorkflowId = "wf-ready";
        documents["wf-ready"] = Snapshot("wf-ready", revision: "rev-2");
        Assert.Contains("已修改", (await service.ValidateAsync(settings))!);
    }

    [Fact]
    public async Task ReadingAbsentStoreDoesNotCreateFilesOrMigrateData()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var path = Path.Combine(AssistantDataDirectory.Root, "completion-readonly-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(path));
        var store = new WorkflowStore(path);
        var service = new CompletionTargetCatalogService(store.List, store.LoadSnapshot, null);
        var catalog = await service.LoadAsync("workflow", "", "");
        Assert.Empty(catalog.Workflows);
        Assert.Null(catalog.Target);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void DuplicateOrMissingStableTaskIdsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CompletionTargetCatalogService.ParseDescription("group", "收尾",
            """{"configRevision":"r","tasks":[{"taskId":"same","name":"a","enabled":true},{"taskId":"same","name":"b","enabled":true}]}"""));
        Assert.Throws<InvalidOperationException>(() => CompletionTargetCatalogService.ParseDescription("onedragon", "收尾",
            """{"configRevision":"r","tasks":[{"taskId":"","name":"a","enabled":true}]}"""));
        var legacy = Settings("group");
        legacy.SpecifiedStartMode = "default";
        Assert.NotNull(CompletionTargetCatalogService.ValidateShape(legacy));
        legacy.SpecifiedStartTaskId = legacy.SpecifiedStartTaskName = legacy.SpecifiedConfigRevision = "";
        Assert.Null(CompletionTargetCatalogService.ValidateShape(legacy));
    }

    private static TaskConflictPolicySettings Settings(string type) => new()
    {
        Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = type, SpecifiedTaskName = "收尾",
        SpecifiedStartMode = "node", SpecifiedStartTaskId = "task-b", SpecifiedStartTaskName = "收取", SpecifiedConfigRevision = "rev-1"
    };

    private static WorkflowSnapshot Snapshot(string id, string kind = "resource.configGroup", string nodeId = "node-b",
        string? activation = null, string revision = "rev-1") => new(new WorkflowDocument
    {
        WorkflowId = id, Name = "实际计划名", Nodes = [new() { NodeId = nodeId, Kind = kind }],
        Activation = activation == null ? null : new() { Status = activation }
    }, revision);

    private sealed class Transport : IResourceCatalogTransport
    {
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        internal string? Operation { get; private set; }
        internal string? Payload { get; private set; }
        public Task<string?> SendAsync(string operation, object payload, CancellationToken ct)
        {
            Operation = operation;
            Payload = JsonSerializer.Serialize(payload);
            return Task.FromResult<string?>("""{"configRevision":"rev-1","tasks":[{"taskId":"task-b","name":"收取","enabled":true},{"taskId":"disabled","name":"禁用","enabled":false}]}""");
        }
    }
}
