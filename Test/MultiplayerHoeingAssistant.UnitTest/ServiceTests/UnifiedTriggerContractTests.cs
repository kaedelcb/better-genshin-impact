using System.Security.Cryptography;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class UnifiedTriggerContractTests
{
    [Fact]
    public void OldEditorCannotOverwriteOrStripComposition()
    {
        var plain = new WorkflowDocument { Nodes = [new WorkflowNode { Kind = "resource.configGroup", NodeId = "a" }] };
        var composed = new WorkflowDocument { Nodes = [new WorkflowNode { Kind = "control.flow", NodeId = "f" }] };
        var old = new RemoteCenterRequest();
        Assert.True(RemoteCenterProtocol.CanSaveFlow(old, plain, plain));
        Assert.False(RemoteCenterProtocol.CanSaveFlow(old, composed, plain));
        Assert.False(RemoteCenterProtocol.CanSaveFlow(old, plain, composed));
        Assert.True(RemoteCenterProtocol.CanSaveFlow(new RemoteCenterRequest { FlowEditVersion = 1 }, composed, plain));
        Assert.True(RemoteCenterProtocol.CanSaveFlow(new RemoteCenterRequest { FlowEditVersion = 1 }, plain, composed));
    }

    [Fact]
    public void CapabilityIsPartOfNewRequestIdentityWhileOldReceiptsRemainCompatible()
    {
        var command = new RemoteCommand { RoomCode = "room", SenderUid = "uid" };
        var request = new RemoteCenterRequest { Operation = "task.flow.save", TargetInstanceId = "endpoint", PayloadJson = "{}" };
        var oldHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            command.RoomCode, command.SenderUid, request.Version, request.TargetInstanceId,
            request.Operation, request.ExpectedRevision, request.PayloadJson
        })));
        Assert.Equal(oldHash, RemoteCenterProtocol.RequestFingerprint(command, request));
        request.FlowEditVersion = 1;
        Assert.NotEqual(oldHash, RemoteCenterProtocol.RequestFingerprint(command, request));
    }

    [Fact]
    public void UnifiedInfoSeparatesSameRuntimeIdByOwningCenterAndPreservesLegacyPayload()
    {
        var startup = new UnifiedTriggerInfo("same-id", "startup", "timer", "同名");
        var task = startup with { Source = "taskCenter", RunId = "run-b" };
        Assert.NotEqual(startup.Key, task.Key);
        var legacy = JsonSerializer.Deserialize<RemoteStartupStatusSnapshot>(
            """{"Triggers":[{"RuntimeId":"old","Kind":"timer","Text":"旧端定时器"}]}""")!;
        Assert.Null(legacy.UnifiedTriggers);
        Assert.Empty(legacy.TaskPrompts);
        Assert.Equal("old", Assert.Single(legacy.Triggers).RuntimeId);
    }
}
