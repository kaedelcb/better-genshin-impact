using System.Collections.Concurrent;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 远程配置组编辑会话状态机（契约见 Docs/远程配置组编辑-实施方案.md §1/§2/§5）。
/// 流程：发 remote_config.pull → 等 remote_config.data → 本机 IPC config.open_remote_editor →
/// 每 2s 轮询 IPC config.remote_editor_result → saved 则发 remote_config.push → 等 remote_config.push_result。
/// 等待回复用 ConcurrentDictionary 按 CommandId 关联，由 MainViewModel.OnRemoteCommand 调 TryComplete 喂入。
/// 同一时刻只允许一个远程编辑流程（单会话锁）。
/// </summary>
public class RemoteConfigEditService
{
    /// <summary>pull 回复（remote_config.data）超时。</summary>
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(20);
    /// <summary>push 回复（remote_config.push_result）超时。</summary>
    private static readonly TimeSpan PushResultTimeout = TimeSpan.FromSeconds(15);
    /// <summary>编辑器结果轮询间隔。</summary>
    private static readonly TimeSpan EditorPollInterval = TimeSpan.FromSeconds(2);
    /// <summary>编辑器结果轮询总时长上限。</summary>
    private static readonly TimeSpan EditorPollMaxDuration = TimeSpan.FromMinutes(10);

    /// <summary>按 CommandId 关联的回复等待表（remote_config.data / remote_config.push_result）。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteCommand>> _pendingReplies = new();

    /// <summary>单会话锁（0=空闲，1=进行中）。</summary>
    private int _sessionActive;

    /// <summary>发送 RemoteCommand；返回 false 表示 SignalR 未连接未能发出。</summary>
    private readonly Func<RemoteCommand, Task<bool>> _sendAsync;
    private readonly Func<string> _getSelfUid;
    private readonly Func<string> _getSelfName;
    /// <summary>进度/结果上报（MainViewModel.AddLog，线程安全）。</summary>
    private readonly Action<string> _report;

    public RemoteConfigEditService(
        Func<RemoteCommand, Task<bool>> sendAsync,
        Func<string> getSelfUid,
        Func<string> getSelfName,
        Action<string> report)
    {
        _sendAsync = sendAsync;
        _getSelfUid = getSelfUid;
        _getSelfName = getSelfName;
        _report = report;
    }

    /// <summary>
    /// 供 MainViewModel.OnRemoteCommand 把 remote_config.data / remote_config.push_result 转进来。
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
    /// 完整远程编辑流程。目标成员信息用基元类型传入，避免 Services 层依赖 ViewModels。
    /// </summary>
    public async Task RunAsync(string targetUid, string targetName, string groupName)
    {
        if (string.IsNullOrEmpty(targetUid))
        {
            _report("远程编辑失败：目标成员 UID 为空");
            return;
        }
        if (Interlocked.CompareExchange(ref _sessionActive, 1, 0) != 0)
        {
            _report("已有进行中的远程编辑会话，请等待其完成后再试");
            return;
        }

        try
        {
            // 1. 发送 remote_config.pull，等待 remote_config.data
            var pull = NewCommand("remote_config.pull", targetUid,
                new Dictionary<string, object> { ["groupName"] = groupName });
            _report($"已向 {targetName} 请求配置组「{groupName}」，等待对方响应（20 秒）...");
            var (dataCmd, pullSent) = await SendAndWaitReplyAsync(pull, PullTimeout);
            if (!pullSent) return; // 发送失败已报日志
            if (dataCmd == null)
            {
                _report($"拉取「{groupName}」超时：对方未响应（可能不在线或版本过旧）");
                return;
            }
            if (GetStringParam(dataCmd.Params, "ok") != "true")
            {
                _report($"对方返回拉取失败：{GetStringParam(dataCmd.Params, "error") ?? "未知原因"}");
                return;
            }
            var packageJson = GetStringParam(dataCmd.Params, "packageJson");
            if (string.IsNullOrEmpty(packageJson))
            {
                _report("对方返回的配置数据为空，流程终止");
                return;
            }
            var baseMd5 = ExtractFileMd5(packageJson);

            // 2. 本机 BGI 弹远程编辑窗口
            var (openResp, openErr) = await SendIpcAsync("config.open_remote_editor",
                JsonSerializer.Serialize(new { targetName, targetUid, groupName, packageJson }));
            if (openResp == null)
            {
                _report($"远程编辑需要本机 BGI 运行中（IPC 连接失败：{openErr}）");
                return;
            }
            if (!openResp.Success)
            {
                _report($"本机 BGI 打开远程编辑窗口失败：{openResp.ErrorMessage ?? "未知原因"}");
                return;
            }
            var openState = GetDataString(openResp.Data, "state");
            if (openState == "rejected")
            {
                _report($"本机 BGI 拒绝了远程编辑：{GetDataString(openResp.Data, "error") ?? "已有进行中的远程编辑会话"}");
                return;
            }
            if (openState != "editing")
            {
                _report($"本机 BGI 返回了未知的编辑状态：{openState ?? "（无 state 字段）"}，流程终止");
                return;
            }
            _report($"已在本机 BGI 打开远程编辑窗口（{targetName} 的「{groupName}」），等待保存（最长 10 分钟）...");

            // 3. 每 2s 轮询编辑结果
            var deadline = DateTime.UtcNow + EditorPollMaxDuration;
            string? scriptGroupConfigJson = null;
            string? soloTaskName = null;
            string? soloTaskSettingsJson = null;
            var saved = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(EditorPollInterval);
                var (pollResp, pollErr) = await SendIpcAsync("config.remote_editor_result", null);
                if (pollResp == null)
                {
                    _report($"轮询编辑结果失败（本机 BGI IPC 中断：{pollErr}），远程编辑流程终止");
                    return;
                }
                if (!pollResp.Success || string.IsNullOrEmpty(pollResp.Data)) continue;

                string? state;
                try
                {
                    using var doc = JsonDocument.Parse(pollResp.Data);
                    state = doc.RootElement.TryGetProperty("state", out var st)
                        && st.ValueKind == JsonValueKind.String
                        ? st.GetString() : null;
                    if (state == "saved")
                    {
                        scriptGroupConfigJson = GetDataString(pollResp.Data, "scriptGroupConfigJson");
                        soloTaskName = GetDataString(pollResp.Data, "soloTaskName");
                        soloTaskSettingsJson = GetDataString(pollResp.Data, "soloTaskSettingsJson");
                    }
                }
                catch (JsonException)
                {
                    continue; // 响应格式异常，继续轮询
                }

                switch (state)
                {
                    case "editing":
                        continue;
                    case "cancelled":
                        _report("远程编辑已取消");
                        return;
                    case "idle":
                        _report("远程编辑会话已结束（无结果）");
                        return;
                    case "saved":
                        saved = true;
                        break;
                    default:
                        continue; // 未知状态，继续轮询
                }
                if (saved) break;
            }
            if (!saved)
            {
                _report("等待远程编辑结果超时（10 分钟），流程终止");
                return;
            }
            if (string.IsNullOrEmpty(scriptGroupConfigJson) && string.IsNullOrEmpty(soloTaskSettingsJson))
            {
                _report("编辑内容为空（未做任何修改），跳过回传");
                return;
            }

            // 4. 发送 remote_config.push，等待 remote_config.push_result
            var pushParams = new Dictionary<string, object>
            {
                ["groupName"] = groupName,
                ["baseMd5"] = baseMd5
            };
            if (!string.IsNullOrEmpty(scriptGroupConfigJson)) pushParams["scriptGroupConfigJson"] = scriptGroupConfigJson;
            if (!string.IsNullOrEmpty(soloTaskName)) pushParams["soloTaskName"] = soloTaskName;
            if (!string.IsNullOrEmpty(soloTaskSettingsJson)) pushParams["soloTaskSettingsJson"] = soloTaskSettingsJson;
            var push = NewCommand("remote_config.push", targetUid, pushParams);
            _report($"编辑已保存，正在回传给 {targetName}...");
            var (resultCmd, pushSent) = await SendAndWaitReplyAsync(push, PushResultTimeout);
            if (!pushSent) return; // 发送失败已报日志
            if (resultCmd == null)
            {
                _report("等待对方应用结果超时（15 秒），对方可能已离线，修改可能未生效");
                return;
            }
            var ok = GetStringParam(resultCmd.Params, "ok") == "true";
            var message = GetStringParam(resultCmd.Params, "message") ?? "";
            _report(ok
                ? $"远程配置已应用：{message}"
                : $"对方应用失败：{message}");
        }
        catch (Exception ex)
        {
            _report($"远程编辑流程异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _sessionActive, 0);
        }
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

    /// <summary>注册等待 → 发送 → 等回复。sent=false 表示 SignalR 未连接未能发出（已报日志）。</summary>
    private async Task<(RemoteCommand? reply, bool sent)> SendAndWaitReplyAsync(RemoteCommand cmd, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<RemoteCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReplies[cmd.CommandId] = tcs;
        try
        {
            if (!await _sendAsync(cmd))
            {
                _report("SignalR 未连接，无法发送远程命令");
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

    /// <summary>独立短连接发一次 IPC（参考 CommandExecutor 里 IpcClient 的 using 用法）。</summary>
    private static async Task<(IpcResponse? response, string? error)> SendIpcAsync(string opCode, string? payload)
    {
        try
        {
            using var ipc = new IpcClient();
            await ipc.ConnectAsync(3000);
            var resp = await ipc.SendCommandAsync(new IpcRequest { OpCode = opCode, Payload = payload });
            return (resp, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>从 IPC 响应 data（JSON 对象字符串）中取字符串字段，取不到返回 null。</summary>
    private static string? GetDataString(string? dataJson, string key)
    {
        if (string.IsNullOrEmpty(dataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            return doc.RootElement.TryGetProperty(key, out var el)
                && el.ValueKind == JsonValueKind.String
                ? el.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 packageJson 中提取 fileMd5（push 时的 baseMd5，乐观并发用）。</summary>
    private static string ExtractFileMd5(string packageJson)
        => GetDataString(packageJson, "fileMd5") ?? "";

    /// <summary>
    /// 从 Params 字典安全取出字符串值。
    /// SignalR 反序列化后 value 可能是 string 或 JsonElement，需分别处理（同 CommandExecutor.GetStringParam）。
    /// </summary>
    private static string? GetStringParam(Dictionary<string, object>? dict, string key)
    {
        if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return null;
        if (val is string s) return s;
        if (val is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        }
        return val.ToString();
    }
}
