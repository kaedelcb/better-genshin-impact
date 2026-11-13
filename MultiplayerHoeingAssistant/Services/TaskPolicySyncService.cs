using System.Collections.Concurrent;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 上线锄地「完成后动作」远程互改通道（模式复刻 RemoteConfigEditService，服务器零改动）：
/// 发起方发 task_policy.pull → 等 task_policy.data（拉取对方上线锄地持久化配置 policy/specifiedType/specifiedName/specifiedPriority）；
/// 编辑后发 task_policy.push → 等 task_policy.push_result（对方应用：落 AssistConfig 并保存）。
/// 等待回复用 ConcurrentDictionary 按 CommandId 关联，由 MainViewModel.OnRemoteCommand 调 TryComplete 喂入。
/// Params 值一律 string（取值兼容 string/JsonElement，用 GetStringParam 写法）。
/// </summary>
public class TaskPolicySyncService
{
    /// <summary>pull 回复（task_policy.data）超时。</summary>
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(8);
    /// <summary>push 回复（task_policy.push_result）超时。</summary>
    private static readonly TimeSpan PushResultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>按 CommandId 关联的回复等待表（task_policy.data / task_policy.push_result）。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteCommand>> _pendingReplies = new();
    private readonly ConcurrentDictionary<string, bool> _completionStartPeers = new();

    /// <summary>发送 RemoteCommand；返回 false 表示 SignalR 未连接未能发出。</summary>
    private readonly Func<RemoteCommand, Task<bool>> _sendAsync;
    private readonly Func<string> _getSelfUid;
    private readonly Func<string> _getSelfName;

    public TaskPolicySyncService(
        Func<RemoteCommand, Task<bool>> sendAsync,
        Func<string> getSelfUid,
        Func<string> getSelfName)
    {
        _sendAsync = sendAsync;
        _getSelfUid = getSelfUid;
        _getSelfName = getSelfName;
    }

    /// <summary>
    /// 供 MainViewModel.OnRemoteCommand 把 task_policy.data / task_policy.push_result 转进来。
    /// 有等待中的会话且 CommandId 匹配时完成等待并返回 true，否则返回 false。
    /// </summary>
    public bool TryComplete(string commandId, RemoteCommand cmd)
    {
        if (string.IsNullOrEmpty(commandId)) return false;
        if (_pendingReplies.TryRemove(commandId, out var tcs))
        {
            tcs.TrySetResult(cmd);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 拉取对方上线锄地「完成后动作」配置。返回 null = 超时/发送失败/对方应答失败；
    /// 否则为对方回传的键值表（policy/specifiedType/specifiedName）。
    /// </summary>
    public async Task<Dictionary<string, string>?> PullAsync(string targetUid, string? catalogType = null,
        string? catalogName = null, string? catalogWorkflowId = null)
    {
        var parameters = new Dictionary<string, object>();
        if (catalogType != null) parameters["catalogType"] = catalogType;
        if (catalogName != null) parameters["catalogName"] = catalogName;
        if (catalogWorkflowId != null) parameters["catalogWorkflowId"] = catalogWorkflowId;
        var pull = NewCommand("task_policy.pull", targetUid, parameters);
        var (reply, sent) = await SendAndWaitReplyAsync(pull, PullTimeout);
        if (!sent || reply == null || GetStringParam(reply.Params, "ok") != "true")
        {
            _completionStartPeers.TryRemove(targetUid, out _);
            return null;
        }
        _completionStartPeers[targetUid] = GetStringParam(reply.Params, "completionStartVersion") == CompletionTargetCatalogService.Version;
        var result = new Dictionary<string, string>();
        if (reply.Params != null)
        {
            foreach (var (k, v) in reply.Params)
            {
                var s = GetStringParam(reply.Params, k);
                if (s != null) result[k] = s;
            }
        }
        return result;
    }

    /// <summary>
    /// 回传编辑后的任务策略配置给对方应用。返回 null = 超时/发送失败；
    /// 否则为对方应用结果（ok/message）。
    /// </summary>
    public async Task<(bool ok, string message)?> PushAsync(string targetUid, Dictionary<string, object> prms)
    {
        var usesStartFields = GetStringParam(prms, "completionStartVersion") != null
            || GetStringParam(prms, "specifiedType") == "workflow"
            || prms.Keys.Any(k => k is "specifiedStartMode" or "specifiedStartTaskId" or "specifiedStartTaskName" or "specifiedConfigRevision" or "specifiedWorkflowId");
        if (usesStartFields && (GetStringParam(prms, "completionStartVersion") != CompletionTargetCatalogService.Version
            || !_completionStartPeers.TryGetValue(targetUid, out var supported) || !supported))
            return (false, "执行端尚未确认执行起点协议，未发送新设置；请更新执行端并重新读取配置");
        if (usesStartFields)
        {
            // 弹窗打开期间执行端可能重连或被替换；不能凭之前同 UID 的能力缓存写入新字段。
            var current = await PullAsync(targetUid);
            if (current == null || !current.TryGetValue("completionStartVersion", out var version)
                || version != CompletionTargetCatalogService.Version)
                return (false, "执行端当前未确认执行起点协议，未发送新设置；请重新读取配置");
        }
        var push = NewCommand("task_policy.push", targetUid, new Dictionary<string, object>(prms));
        var (reply, sent) = await SendAndWaitReplyAsync(push, PushResultTimeout);
        if (!sent) return (false, "SignalR 未连接，无法发送");
        if (reply == null) return null;
        if (usesStartFields)
        {
            if (GetStringParam(reply.Params, "completionStartVersion") != CompletionTargetCatalogService.Version)
                return (false, "对方未确认起点协议版本，未证明已保存；请更新执行端并重新读取配置");
            if (GetStringParam(reply.Params, "ok") == "true"
                && push.Params!.Any(pair => GetStringParam(reply.Params, pair.Key) != GetStringParam(push.Params, pair.Key)))
                return (false, "对方未完整确认保存值，请重新读取配置核对；未更新本机镜像");
        }
        return (GetStringParam(reply.Params, "ok") == "true", GetStringParam(reply.Params, "message") ?? "");
    }

    private RemoteCommand NewCommand(string cmdName, string targetUid, Dictionary<string, object> prms) => new()
    {
        Cmd = cmdName,
        Sender = _getSelfName(),
        SenderUid = _getSelfUid(),
        Target = [targetUid],
        CommandId = Guid.NewGuid().ToString("N"),
        Params = prms
    };

    /// <summary>注册等待 → 发送 → 等回复。sent=false 表示 SignalR 未连接未能发出。</summary>
    private async Task<(RemoteCommand? reply, bool sent)> SendAndWaitReplyAsync(RemoteCommand cmd, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<RemoteCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReplies[cmd.CommandId] = tcs;
        try
        {
            if (!await _sendAsync(cmd))
            {
                return (null, false);
            }
            return (await tcs.Task.WaitAsync(timeout), true);
        }
        catch (TimeoutException)
        {
            return (null, true);
        }
        finally
        {
            _pendingReplies.TryRemove(cmd.CommandId, out _);
        }
    }

    /// <summary>
    /// 从 Params 字典安全取出字符串值。
    /// SignalR 反序列化后 value 可能是 string 或 JsonElement，需分别处理（同 CommandExecutor.GetStringParam）。
    /// </summary>
    private static string? GetStringParam(Dictionary<string, object>? dict, string key)
    {
        if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return null;
        if (val is string s) return s;
        if (val is System.Text.Json.JsonElement je)
        {
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : je.ToString();
        }
        return val.ToString();
    }
}
