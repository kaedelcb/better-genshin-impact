using System.Text.Json;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>监控端与执行端共用的中心请求。两种传输只负责送达，执行端负责全部读写和运行。</summary>
public sealed class RemoteCenterRequest
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string Operation { get; set; } = "";
    public string TargetInstanceId { get; set; } = "";
    public string? PayloadJson { get; set; }
    public string? ExpectedRevision { get; set; }
}

public sealed class RemoteCenterReply
{
    public int Version { get; set; } = RemoteCenterRequest.CurrentVersion;
    public string TargetInstanceId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string? PayloadJson { get; set; }
    public string? Revision { get; set; }
    public string? OperationId { get; set; }

    public static RemoteCenterReply Ok(object? data = null, string? revision = null, string message = "") => new()
    {
        Status = "ok", Message = message,
        PayloadJson = data is null ? null : JsonSerializer.Serialize(data), Revision = revision
    };

    public static RemoteCenterReply Rejected(string message) => new() { Status = "rejected", Message = message };
    public static RemoteCenterReply Unknown(string operationId, string message) => new()
    { Status = "unknown", OperationId = operationId, Message = message };
}

public static class RemoteCenterProtocol
{
    public const string RequestCommand = "center.request";
    public const string ReplyCommand = "center.data";
    public const string PayloadKey = "centerPayload";

    public static string? PayloadOf(RemoteCommand command)
        => command.Params?.GetValueOrDefault(PayloadKey)?.ToString();

    public static RemoteCommand Request(string roomCode, string senderUid, string senderName,
        string targetUid, RemoteCenterRequest request, string? commandId = null) => new()
    {
        Cmd = RequestCommand, RoomCode = roomCode, SenderUid = senderUid, Sender = senderName,
        Target = [targetUid], TargetInstanceId = request.TargetInstanceId,
        CommandId = commandId ?? Guid.NewGuid().ToString("N"),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20),
        Params = new Dictionary<string, object> { [PayloadKey] = JsonSerializer.Serialize(request) }
    };

    public static RemoteCommand Reply(RemoteCommand source, string executorUid, string executorName,
        RemoteCenterReply reply) => new()
    {
        Cmd = ReplyCommand, RoomCode = source.RoomCode, SenderUid = executorUid, Sender = executorName,
        Target = [source.SenderUid], CommandId = source.CommandId,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20),
        Params = new Dictionary<string, object> { [PayloadKey] = JsonSerializer.Serialize(reply) }
    };
}

public sealed class RemoteCenterTarget
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string ClientInstanceId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public bool Online { get; set; }
    public bool ForceLocal { get; set; }
    public string Label => $"{PlayerName} · {PlayerUid} · {ClientInstanceId[..Math.Min(8, ClientInstanceId.Length)]}";
    public override string ToString() => Label;
}

public sealed class RemoteStartupSnapshot
{
    public StartupFlowConfig Config { get; set; } = new();
    public string? Revision { get; set; }
    public List<StartupFlowScheme> Schemes { get; set; } = [];
    public string? SchemesRevision { get; set; }
    public bool IsRunning { get; set; }
    public string StatusText { get; set; } = "";
    public bool AutoLaunchOnBoot { get; set; }
    public int AutoLaunchOnBootModeIndex { get; set; }
    public string? AutoLaunchRevision { get; set; }
    public List<string> Timers { get; set; } = [];
    public List<string> Watchdogs { get; set; } = [];
    public List<string> LogTriggers { get; set; } = [];
    public List<RemoteTriggerInfo> Triggers { get; set; } = [];
    public List<RemotePromptInfo> Prompts { get; set; } = [];
    public List<RemoteNodeRunState> NodeStates { get; set; } = [];
}

public sealed record RemoteTriggerInfo(string RuntimeId, string Kind, string Text,
    StartupStep? Step = null, string? Note = null);
public sealed record RemoteNodeRunState(string StepId, NodeRunState State, string Note);
public sealed record RemotePromptInfo(string RuntimeId, string Message,
    string YesPreview, string NoPreview, int RemainingSeconds);

public sealed class RemoteStartupStatusSnapshot
{
    public bool IsRunning { get; set; }
    public string StatusText { get; set; } = "";
    public List<RemoteTriggerInfo> Triggers { get; set; } = [];
    public List<RemotePromptInfo> Prompts { get; set; } = [];
    public List<RemoteNodeRunState>? NodeStates { get; set; }
}

public sealed class RemoteTaskListSnapshot
{
    public List<WorkflowCatalogEntry> Flows { get; set; } = [];
    public List<WorkflowRunRecord> Active { get; set; } = [];
    public List<WorkflowRunRecord> History { get; set; } = [];
    public ResourceCatalogSnapshot Catalog { get; set; } = new();
    public List<TaskCenterResourceEntry> StoredResourceEntries { get; set; } = [];
    public List<WorkflowDeletionReceipt> Deleted { get; set; } = [];
    public Dictionary<string, WorkflowSnapshot> ActiveFlowSnapshots { get; set; } = new(StringComparer.Ordinal);
    public List<string> DrivingWorkflowIds { get; set; } = [];
}
