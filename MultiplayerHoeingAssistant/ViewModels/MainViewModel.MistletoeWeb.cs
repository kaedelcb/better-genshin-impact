using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 网页槲寄生入口。两条传输（本机直连、服务器转发）都换成现有远程中心请求，
/// 交给 <see cref="HandleRemoteCenterRequestAsync"/> 执行，因此请求身份认领、指纹核对、
/// 持久回执、重复启动防护和停止语义与 PC 监控端完全相同。
/// 网页额外只做一件事：确认来者是本人（本机配对会话或浏览器凭据）；操作范围与 PC 监控端远程中心相同。
/// </summary>
public partial class MainViewModel
{
    private WebOwnerAuthorizationStore? _webOwners;
    internal WebOwnerAuthorizationStore WebOwners => _webOwners ??= new WebOwnerAuthorizationStore();
    private long _mistletoeWebEpoch;

    /// <summary>网页需要的额外只读操作（不经 PC 中心协议）。</summary>
    private static readonly HashSet<string> WebExtraOperations = new(StringComparer.Ordinal)
    {
        "web.identity", "web.authorize"
    };

    /// <summary>由 WireSignalRClient 对每个新连接调用一次；断线后旧请求按纪元作废，不会迟到执行。</summary>
    private void WireMistletoeWeb(SignalRClient client)
    {
        client.OnConnectionStateChanged += connected => { if (!connected) Interlocked.Increment(ref _mistletoeWebEpoch); };
        client.OnMistletoeRequested += (requestId, request, deadline, requester, roomCode)
            => OnMistletoeWebRequested(client, requestId, request, deadline, requester, roomCode);
    }

    private void OnMistletoeWebRequested(SignalRClient client, string requestId, JsonObject request, DateTimeOffset deadline,
        string requesterConnectionId, string roomCode)
    {
        var epoch = Interlocked.Read(ref _mistletoeWebEpoch);
        _ = Task.Run(async () =>
        {
            object reply;
            try
            {
                if (DateTimeOffset.UtcNow >= deadline || !client.IsControlRoomReady || client.CurrentRoomCode != roomCode
                    || !ReferenceEquals(client, _signalRClient)
                    || epoch != Interlocked.Read(ref _mistletoeWebEpoch) || string.IsNullOrEmpty(requesterConnectionId))
                    reply = WebReply("rejected", "请求已过期或连接已变化，未执行");
                else
                    reply = await Application.Current.Dispatcher.InvokeAsync(
                        () => ExecuteMistletoeWebAsync(request, requestId, local: false)).Task.Unwrap();
            }
            catch (Exception ex) { reply = WebReply("unknown", "执行端处理异常，结果未确认：" + ex.Message); }
            try { await client.ReportMistletoeAsync(requestId, reply); }
            catch { /* 回复丢失时网页按超时显示结果未确认，不会重发 */ }
        });
    }
    /// <summary>本机配对网页入口：本机会话本身就是本人凭据。</summary>
    internal Task<object> ExecuteMistletoeLocalAsync(JsonObject request, string requestId)
        => Application.Current.Dispatcher.InvokeAsync(
            () => ExecuteMistletoeWebAsync(request, requestId, local: true)).Task.Unwrap();

    /// <summary>网页自带的操作身份优先：本机桥会为每次 HTTP 请求另起编号，核对终态必须用网页记住的那一个。</summary>
    private static string OperationIdentity(JsonObject request, string fallback)
    {
        var id = request["requestId"]?.GetValue<string>();
        return id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit) ? id : fallback;
    }

    private static object WebReply(string status, string message, object? data = null, string? revision = null,
        string? operationId = null, string? instanceId = null)
        => new { status, message, data, revision, operationId, targetInstanceId = instanceId };

    private async Task<object> ExecuteMistletoeWebAsync(JsonObject request, string requestId, bool local)
        => await ExecuteMistletoeWebCoreAsync(request, requestId, local,
            command => HandleRemoteCenterRequestAsync(command, allowLocalObserver: !IsExecutorMode));

    /// <summary>
    /// 网页请求门禁：实例核对 → 本人核对 → 监控端拒绝任务中心 → 转成 PC 远程中心请求。
    /// handler 是 PC 远程中心处理流程；测试注入替身以验证未授权请求根本到不了这里。
    /// </summary>
    internal async Task<object> ExecuteMistletoeWebCoreAsync(JsonObject request, string requestId, bool local,
        Func<RemoteCommand, Task<RemoteCommand?>> handler)
    {
        var config = _config;
        if (config is null) return WebReply("rejected", "执行端助手尚未就绪");
        var operation = request["operation"]?.GetValue<string>() ?? "";
        var instance = config.ClientInstanceId;
        var expectedInstance = request["targetInstanceId"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(operation) || operation.Length > 64)
            return WebReply("rejected", "操作无效", instanceId: instance);
        if (operation != "web.identity" && expectedInstance != instance)
            return WebReply("rejected", "执行端实例已变化，请重新选择执行端；本次未执行", instanceId: instance);

        // 本人核对：本机会话免授权；远程必须出示本执行端签发的浏览器凭据。
        var owner = local || WebOwners.Verify(request["browserId"]?.GetValue<string>(), request["browserSecret"]?.GetValue<string>());
        if (operation == "web.identity")
            return WebReply("ok", "", new
            {
                uid = config.PlayerUid, name = config.PlayerName, instanceId = instance,
                machineId = HostMachineIdentity.Current, isExecutor = IsExecutorMode, owner,
                capabilities = IsExecutorMode ? new[] { "startup", "taskCenter" } : new[] { "startup" }
            }, instanceId: instance);
        if (operation == "web.authorize")
        {
            if (local) return WebReply("ok", "本机直连无需授权", new { local = true }, instanceId: instance);
            try
            {
                var (browserId, secret) = WebOwners.Redeem(request["code"]?.GetValue<string>() ?? "",
                    request["label"]?.GetValue<string>() ?? "");
                AddLog("[网页授权] 已授权一个新的网页浏览器，可在设置页撤销");
                return WebReply("ok", "已授权此浏览器", new { browserId, secret }, instanceId: instance);
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
            { return WebReply("rejected", ex.Message, instanceId: instance); }
        }
        if (!owner)
            return WebReply("denied", "此浏览器尚未获得本人授权：请在执行端助手「设置 → 网页授权」生成授权码后输入", instanceId: instance);
        if (!IsExecutorMode && operation.StartsWith("task.", StringComparison.Ordinal))
            return WebReply("rejected", "该助手是监控端，任务中心只在执行端可用", instanceId: instance);

        // 交给 PC 远程中心的同一处理流程；SenderUid 固定为本人，执行端身份核对保持原样。
        var payload = request["payload"];
        var centerRequest = new RemoteCenterRequest
        {
            Operation = operation,
            TargetInstanceId = instance,
            ExpectedRevision = request["expectedRevision"]?.GetValue<string>(),
            PayloadJson = payload is null ? null : payload.ToJsonString()
        };
        if (centerRequest.PayloadJson is { Length: > 2_000_000 })
            return WebReply("rejected", "请求内容超过 2 MB 上限", instanceId: instance);
        var command = RemoteCenterProtocol.Request(RoomCode, config.PlayerUid, config.PlayerName,
            config.PlayerUid, centerRequest, "web-" + OperationIdentity(request, requestId));
        var replyCommand = await handler(command);
        if (replyCommand is null) return WebReply("rejected", "执行端拒绝了这次请求（模式或身份不符）", instanceId: instance);
        var reply = ParseCenterReply(replyCommand);
        JsonNode? data = null;
        if (!string.IsNullOrEmpty(reply.PayloadJson))
        {
            try { data = JsonNode.Parse(reply.PayloadJson); }
            catch (JsonException) { return WebReply("unknown", "执行端回复无法解析，结果未确认", instanceId: instance); }
        }
        // 受理身份原样返回（"web-" 前缀），网页查询终态时用它调用 operation.get。
        return WebReply(reply.Status, reply.Message, data, reply.Revision, reply.OperationId, reply.TargetInstanceId);
    }
}
