using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>通过私有命名管道核对真实 SDK 发出的启动 JSON，不访问用户 BGI 或 User。</summary>
[Collection("TaskCenterPipeTransport")]
public sealed class BgiTaskStartPayloadTests : IAsyncLifetime
{
    private readonly BgiInstancePipeDouble _pipe = new();
    private string? _previousPipe;
    private BgiExternalClient _client = null!;

    public async Task InitializeAsync()
    {
        _previousPipe = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = _pipe.PipeName;
        _pipe.Start();
        _client = new BgiExternalClient();
        Assert.Equal(BgiExternalLinkState.Ready, await _client.StartAsync());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _pipe.DisposeAsync();
        BgiExternalClient.PipeNameOverrideForTest = _previousPipe;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryStart_OmitsCoordinatedFields_AndPreservesStartIdentity(bool preempt)
    {
        var expires = new DateTimeOffset(2026, 10, 16, 1, 0, 0, TimeSpan.Zero);
        var result = await _client.SubmitTaskStartAsync(null, "日常一条龙", 2, 7,
            batchGroupNames: "组甲,组乙", preempt: preempt, idempotencyKey: "ordinary-start",
            expectedConfigRevision: "revision-1", bgiEpoch: new { processId = 123, startTicksUtc = 456L },
            expiresAtUtc: expires, startFromTaskId: "task-3");

        Assert.True(result.Success);
        var sent = SentStart();
        Assert.False(sent.TryGetProperty("coordinatedHoeing", out _));
        Assert.False(sent.TryGetProperty("hoeingBatchId", out _));
        Assert.False(sent.TryGetProperty("hoeingParticipantUids", out _));
        Assert.Equal("日常一条龙", sent.GetProperty("configName").GetString());
        Assert.Equal(2, sent.GetProperty("startFromIndex").GetInt32());
        Assert.Equal("task-3", sent.GetProperty("startFromTaskId").GetString());
        Assert.Equal(7, sent.GetProperty("generation").GetInt32());
        Assert.Equal("组甲,组乙", sent.GetProperty("batchGroupNames").GetString());
        Assert.Equal("ordinary-start", sent.GetProperty("idempotencyKey").GetString());
        Assert.Equal("revision-1", sent.GetProperty("expectedConfigRevision").GetString());
        Assert.Equal(123, sent.GetProperty("bgiEpoch").GetProperty("processId").GetInt32());
        Assert.Equal(expires, sent.GetProperty("expiresAtUtc").GetDateTimeOffset());
        Assert.Equal(preempt, sent.TryGetProperty("preempt", out var flag));
        if (preempt) Assert.True(flag.GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedStart_PreservesCompleteBatchIdentity(bool preempt)
    {
        var result = await _client.SubmitTaskStartAsync("联机线路", null, 0, 9,
            preempt: preempt, coordinatedHoeing: true, hoeingBatchId: "batch-9",
            hoeingParticipantUids: ["100000001", "100000002"]);

        Assert.True(result.Success);
        var sent = SentStart();
        Assert.Equal("v1", sent.GetProperty("coordinatedHoeing").GetString());
        Assert.Equal("batch-9", sent.GetProperty("hoeingBatchId").GetString());
        Assert.Equal(new[] { "100000001", "100000002" },
            sent.GetProperty("hoeingParticipantUids").EnumerateArray().Select(p => p.GetString()).ToArray());
        Assert.Equal("联机线路", sent.GetProperty("groupName").GetString());
        Assert.Equal(preempt, sent.TryGetProperty("preempt", out var flag));
        if (preempt) Assert.True(flag.GetBoolean());
    }

    [Fact]
    public async Task BatchIdentityWithoutContract_IsStillSentForExplicitProtocolRejection()
    {
        _pipe.BodyForTest = (operation, _) => operation == "ext.task.start"
            ? (false, "invalid_request", "协调批次身份必须与 coordinatedHoeing=v1 一起提供", null)
            : null;

        var result = await _client.SubmitTaskStartAsync("线路", null, 0, 1,
            hoeingBatchId: "unexpected-batch", hoeingParticipantUids: ["100000001"]);

        Assert.False(result.Success);
        Assert.Equal("invalid_request", result.ErrorCode);
        var sent = SentStart();
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("coordinatedHoeing").ValueKind);
        Assert.Equal("unexpected-batch", sent.GetProperty("hoeingBatchId").GetString());
    }

    private JsonElement SentStart()
        => Assert.Single(_pipe.Received.Where(r => r.Operation == "ext.task.start")).Data!.Value;
}
