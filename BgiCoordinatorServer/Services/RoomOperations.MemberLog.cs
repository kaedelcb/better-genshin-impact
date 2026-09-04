using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 日志三件套 + 截图汇聚族（自 CoordinatorHub 逐字搬迁：ReportMemberScreenshot/
/// ReportMemberLogBatch/SubscribeMemberLog/UnsubscribeMemberLog/RequestMemberLogFiles/
/// ReportMemberLogFiles/RequestMemberLogDownload/ReportMemberLogChunk，
/// 及 NotifyLogSubscriberCount/RegisterLogTransferRequest/PassRateLimit 私有辅助
/// 与 ScreenshotRateLimit/LogRateLimit/LogFileNameRegex/LogChunkRateLimit/LogBusyRateLimit/
/// LogTransferRequests/LogTransferRequestTtl 静态表和相关常量）。
/// 截图方法同属 CTRL_ 控制房间命名空间、无独立成族价值，按约定一并放本文件。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// 控制房间 Group 名为 "CTRL_{roomCode}"，与锄地房间不同命名空间——
/// RoomPhase 观测（只针对锄地房间）本族一律不加。
/// </summary>
public sealed partial class RoomOperations
{
    // 成员截图汇聚限流表（嘟嘟可 P5）："CTRL_{roomCode}:{uid}" → 最近一次转发时间（UTC）
    private static readonly ConcurrentDictionary<string, DateTime> ScreenshotRateLimit = new();

    /// <summary>
    /// 成员截图汇聚（嘟嘟可 P5 / 远程成员巡检墙）：成员端助手每 10s 上报一帧 JPEG 缩略图（base64），
    /// 服务端校验连接确实在对应 CTRL_ 控制房间后纯转发给房间内所有成员（不做服务端存储）。
    /// 限流：同 uid 10 秒内只转发一帧，超出丢弃。
    /// </summary>
    public async Task ReportMemberScreenshotAsync(GatewayHandlerContext ctx, string roomCode, string uid, string jpegBase64, int width, int height, DateTime capturedAt)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            // 校验与 SendRemoteCommand 同款：PC 端在 _controlRooms 里；遥控端登记连接也放行（可只读观看）
            if (!_roomManager.IsInControlRoom(group, ctx.ConnectionId)
                && !_roomManager.IsRemoteConnection(group, ctx.ConnectionId))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发成员截图（uid={Uid}）", roomCode, uid);
                return;
            }

            // 负载上限（审查中危5）：base64 超 512KB（≈384KB JPEG，远超 480px 缩略图正常体积 ~30KB）直接丢弃，
            // 防止异常/恶意端用大图打爆房间广播带宽
            const int MaxJpegBase64Length = 512 * 1024;
            if (string.IsNullOrEmpty(jpegBase64) || jpegBase64.Length > MaxJpegBase64Length)
            {
                _logger.LogWarning("成员截图负载超限或为空（uid={Uid}, {Len} 字符），丢弃", uid, jpegBase64?.Length ?? 0);
                return;
            }

            // 简单限流：同 uid 10 秒内只转发一帧（截图流量大，防止异常端打爆房间广播）
            // 注：TryGetValue + 写回不是原子操作，并发下同 uid 可能偶尔放过多一帧——
            // 限流只是防滥用兜底而非精确配额，可接受，不为它引入锁。
            var key = $"{group}:{uid}";
            var now = DateTime.UtcNow;
            if (ScreenshotRateLimit.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(10))
                return;
            ScreenshotRateLimit[key] = now;
            // 顺带清理过期键（房间/uid 规模小，概率性清理即可，防长期运行字典膨胀）
            if (ScreenshotRateLimit.Count > 200)
            {
                foreach (var kv in ScreenshotRateLimit)
                    if (now - kv.Value > TimeSpan.FromHours(1))
                        ScreenshotRateLimit.TryRemove(kv.Key, out _);
            }

            // 纯转发（发送者自己也在 Group 内会收到自己的帧，客户端按 uid 自行忽略即可）
            var payload = new
            {
                uid,
                jpegBase64,
                width,
                height,
                capturedAt
            };
            await _broadcaster.BroadcastGroupAsync(group, "MemberScreenshot", payload, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberScreenshot 失败");
        }
    }

    // 成员日志汇聚限流表（房间实时日志汇聚）："CTRL_{roomCode}:{uid}" → 最近 1 秒窗口内的批次数（窗口起点, 计数）
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> LogRateLimit = new();

    /// <summary>
    /// 房间实时日志汇聚：成员端助手每 500ms 合批上报本机 BGI 实时日志行（已渲染为文本行），
    /// 服务端校验连接确实在对应 CTRL_ 控制房间后纯转发给房间内所有成员（不做服务端存储/不过滤内容）。
    /// 限流：同 uid 每秒最多 4 批（正常节奏 500ms 一批，留一倍余量）；负载上限：单批 500 行 / 256KB 字符。
    /// infoOnly：发送端开启省流（仅 INF+）的标志，透传给观看端做状态提示。
    /// </summary>
    public async Task ReportMemberLogBatchAsync(GatewayHandlerContext ctx, string roomCode, string uid, string senderName, List<string> lines, bool infoOnly)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            // 校验与 ReportMemberScreenshot 同款：PC 端在 _controlRooms 里；遥控端登记连接也放行
            if (!_roomManager.IsInControlRoom(group, ctx.ConnectionId)
                && !_roomManager.IsRemoteConnection(group, ctx.ConnectionId))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发成员日志（uid={Uid}）", roomCode, uid);
                return;
            }

            // 负载上限：单批 500 行 / 总 256KB 字符，超限丢弃（正常批远低于此，超限多半是异常端）
            const int MaxLines = 500;
            const int MaxTotalChars = 256 * 1024;
            if (lines == null || lines.Count == 0) return;
            if (lines.Count > MaxLines || lines.Sum(l => l?.Length ?? 0) > MaxTotalChars)
            {
                _logger.LogWarning("成员日志批负载超限（uid={Uid}, {Count} 行 / {Chars} 字符），丢弃",
                    uid, lines.Count, lines.Sum(l => l?.Length ?? 0));
                return;
            }

            // 兜底门：没有任何订阅者时直接丢弃。目标端按订阅数>0 才上报，但订阅可能刚断开、
            // 或目标端是旧版（不看订阅数照常上报），这里挡住避免白转发/白限流。
            if (_roomManager.GetLogSubscriberCount(group, uid) == 0)
            {
                _logger.LogDebug("成员 {Uid} 的日志批无订阅者，丢弃", uid);
                return;
            }

            // 简单限流：同 uid 每秒最多 4 批。与截图限流同款非原子读写，偶尔多放一批可接受（防滥用兜底而非精确配额）
            var key = $"{group}:{uid}";
            var now = DateTime.UtcNow;
            var state = LogRateLimit.GetOrAdd(key, _ => (now, 0));
            if (now - state.WindowStart > TimeSpan.FromSeconds(1))
                state = (now, 0);
            if (state.Count >= 4) return;
            LogRateLimit[key] = (state.WindowStart, state.Count + 1);
            // 顺带清理过期键（房间/uid 规模小，概率性清理即可）
            if (LogRateLimit.Count > 200)
            {
                foreach (var kv in LogRateLimit)
                    if (now - kv.Value.WindowStart > TimeSpan.FromHours(1))
                        LogRateLimit.TryRemove(kv.Key, out _);
            }

            // 纯转发（发送者自己也在 Group 内会收到自己的批，客户端按 uid 自滤）
            var payload = new
            {
                uid,
                senderName,
                lines,
                infoOnly,
                serverTime = now
            };
            await _broadcaster.BroadcastGroupAsync(group, "MemberLogBatch", payload, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberLogBatch 失败");
        }
    }

    /// <summary>
    /// 订阅某成员的实时日志流（房间日志汇聚·观众驱动：没人订阅时目标端零上报零流量）。
    /// 目标端收到 MemberLogSubscribersChanged(count)，只需要知道"有几个人在看"。幂等。
    /// </summary>
    public async Task SubscribeMemberLogAsync(GatewayHandlerContext ctx, string roomCode, string targetUid)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!_roomManager.IsInControlRoom(group, ctx.ConnectionId)
                && !_roomManager.IsRemoteConnection(group, ctx.ConnectionId))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝日志订阅（target={Uid}）", roomCode, targetUid);
                return;
            }

            var count = _roomManager.SubscribeMemberLog(group, targetUid, ctx.ConnectionId);
            if (count < 0)
            {
                _logger.LogWarning("成员 {Uid} 的日志订阅者已达上限，拒绝新订阅", targetUid);
                return;
            }
            await NotifyLogSubscriberCountAsync(group, targetUid, count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubscribeMemberLog 失败");
        }
    }

    /// <summary>退订某成员的实时日志流。未订阅过时静默。</summary>
    public async Task UnsubscribeMemberLogAsync(GatewayHandlerContext ctx, string roomCode, string targetUid)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            var count = _roomManager.UnsubscribeMemberLog(group, targetUid, ctx.ConnectionId);
            if (count == null) return; // 未订阅过
            await NotifyLogSubscriberCountAsync(group, targetUid, count.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UnsubscribeMemberLog 失败");
        }
    }

    /// <summary>把最新订阅数转发给目标成员在房间里的连接（目标端据此启停上报）。
    /// public：旧 Hub 的 OnDisconnectedAsync 断线清理也要调用。</summary>
    private async Task NotifyLogSubscriberCountAsync(string group, string targetUid, int count)
    {
        var targetConn = _roomManager.GetConnectionIdByUid(group, targetUid);
        if (string.IsNullOrEmpty(targetConn)) return; // 目标不在线：订阅留着，上线后由观看端重订阅流程兜底
        await _broadcaster.SendToConnectionAsync(targetConn, "MemberLogSubscribersChanged", new { count }, count);
    }

    // ========== 远程成员完整日志下载（按需请求-应答，纯转发不存储） ==========

    /// <summary>日志文件名白名单（含中文）：拒绝路径分隔符/相对路径，防目录穿越。服务端与助手端同款。</summary>
    private static readonly Regex LogFileNameRegex = new(
        @"^[\w\-.一-鿿]{1,120}\.log$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>requestId 长度上限（Guid.N 为 32，留余量防滥用）。</summary>
    private const int MaxRequestIdLength = 64;
    /// <summary>文件列表项数上限（超出截断）。</summary>
    private const int MaxLogFileListItems = 200;
    /// <summary>单块 base64 字符数上限（≈192KB 原始数据）。</summary>
    private const int MaxChunkBase64Chars = 256 * 1024;
    /// <summary>单次下载总块数上限（≈压缩后 75MB 内；超出直接丢）。</summary>
    private const int MaxLogFileChunks = 400;
    /// <summary>分块上行限流：同 uid 每秒最多 30 块。</summary>
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> LogChunkRateLimit = new();
    /// <summary>忙标记块限流：同 uid 每秒最多 5 个（防洪泛：忙标记不计入数据块限流，独立宽松阈值）。</summary>
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> LogBusyRateLimit = new();

    /// <summary>
    /// 日志传输请求映射：requestId → (请求方 connectionId, 创建时间 UTC)。
    /// RequestMemberLogFiles / RequestMemberLogDownload 时建立；对应应答（列表 / done=true 块）转发后删除；
    /// 兜底：插入新映射时顺带清理 10 分钟前的过期项（如下载中途双方断线，无 done 块可触发删除）。
    /// 转发方式：应答一律按映射<b>单播</b>请求方，不广播（块数据量大，广播会让无关成员白收流量）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string RequesterConnectionId, DateTime CreatedAtUtc)>
        LogTransferRequests = new();
    private static readonly TimeSpan LogTransferRequestTtl = TimeSpan.FromMinutes(10);

    /// <summary>登记请求映射并顺带概率性清理过期项。</summary>
    private static void RegisterLogTransferRequest(string requestId, string requesterConnectionId)
    {
        var now = DateTime.UtcNow;
        LogTransferRequests[requestId] = (requesterConnectionId, now);
        if (LogTransferRequests.Count > 64)
        {
            foreach (var kv in LogTransferRequests)
                if (now - kv.Value.CreatedAtUtc > LogTransferRequestTtl)
                    LogTransferRequests.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>同款非原子窗口计数限流（防滥用兜底而非精确配额）。返回 true=放行。</summary>
    private static bool PassRateLimit(ConcurrentDictionary<string, (DateTime WindowStart, int Count)> table,
        string key, int perSecond)
    {
        var now = DateTime.UtcNow;
        var state = table.GetOrAdd(key, _ => (now, 0));
        if (now - state.WindowStart > TimeSpan.FromSeconds(1))
            state = (now, 0);
        if (state.Count >= perSecond) return false;
        table[key] = (state.WindowStart, state.Count + 1);
        // 顺带清理过期键（概率性清理即可）
        if (table.Count > 200)
        {
            foreach (var kv in table)
                if (now - kv.Value.WindowStart > TimeSpan.FromHours(1))
                    table.TryRemove(kv.Key, out _);
        }
        return true;
    }

    /// <summary>
    /// 观众端请求目标成员的日志文件列表 → 单播目标端 MemberLogFilesRequested(requesterUid, requestId)。
    /// 目标不在线时静默丢弃（观众端超时兜底提示）。
    /// </summary>
    public async Task RequestMemberLogFilesAsync(GatewayHandlerContext ctx, string roomCode, string targetUid, string requestId)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(ctx, group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝日志文件列表请求（target={Uid}）", roomCode, targetUid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;

            var targetConn = _roomManager.GetConnectionIdByUid(group, targetUid);
            if (string.IsNullOrEmpty(targetConn)) return; // 目标不在线
            // 登记 requestId → 请求方连接，应答按映射单播回来
            RegisterLogTransferRequest(requestId, ctx.ConnectionId);
            var requesterUid = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId) ?? "";
            await _broadcaster.SendToConnectionAsync(targetConn, "MemberLogFilesRequested", new { requesterUid, requestId }, requesterUid, requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestMemberLogFiles 失败");
        }
    }

    /// <summary>
    /// 目标端应答日志文件列表 → 按 requestId 映射<b>单播</b>请求方（转发后即删映射）。
    /// 项数超上限截断；文件名不合白名单的项剔除（双保险，助手端已校验）。映射不存在（过期/伪造）直接丢弃。
    /// </summary>
    public async Task ReportMemberLogFilesAsync(GatewayHandlerContext ctx, string roomCode, string uid, string requestId, List<MemberLogFileDescriptor> files)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(ctx, group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发日志文件列表（uid={Uid}）", roomCode, uid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;
            // 认领并删除映射（一次性应答）；无映射 = 过期或伪造，丢弃
            if (!LogTransferRequests.TryRemove(requestId, out var req)) return;

            files ??= [];
            var cleaned = files
                .Where(f => f != null && LogFileNameRegex.IsMatch(f.Name ?? ""))
                .Take(MaxLogFileListItems)
                .ToList();
            var payload = new
            {
                uid,
                requestId,
                files = cleaned
            };
            await _broadcaster.SendToConnectionAsync(req.RequesterConnectionId, "MemberLogFileList", payload, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberLogFiles 失败");
        }
    }

    /// <summary>
    /// 观众端请求下载目标成员的某个日志文件 → 单播目标端 MemberLogDownloadRequested(requesterUid, requestId, fileName)。
    /// fileName 必须过白名单（防目录穿越），否则直接拒绝。
    /// </summary>
    public async Task RequestMemberLogDownloadAsync(GatewayHandlerContext ctx, string roomCode, string targetUid, string requestId, string fileName)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(ctx, group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝日志下载请求（target={Uid}）", roomCode, targetUid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;
            if (string.IsNullOrEmpty(fileName) || !LogFileNameRegex.IsMatch(fileName))
            {
                _logger.LogWarning("日志下载文件名不合白名单（target={Uid}, name={FileName}），拒绝", targetUid, fileName);
                return;
            }

            var targetConn = _roomManager.GetConnectionIdByUid(group, targetUid);
            if (string.IsNullOrEmpty(targetConn)) return; // 目标不在线
            // 登记 requestId → 请求方连接，分块按映射单播回来（done 块转发后删除映射）
            RegisterLogTransferRequest(requestId, ctx.ConnectionId);
            var requesterUid = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId) ?? "";
            await _broadcaster.SendToConnectionAsync(targetConn, "MemberLogDownloadRequested", new { requesterUid, requestId, fileName }, requesterUid, requestId, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestMemberLogDownload 失败");
        }
    }

    /// <summary>
    /// 目标端分块上行日志文件（gzip+base64）→ 按 requestId 映射<b>单播</b>请求方（映射不存在=过期/伪造，丢弃）。
    /// 限流：数据块同 uid 每秒 ≤30；忙标记块（totalChunks=0 且 done=true）独立阈值同 uid 每秒 ≤5，防洪泛。
    /// 总块数 ≤400。done=true 块转发后删除映射（下载结束清理）。
    /// </summary>
    public async Task ReportMemberLogChunkAsync(GatewayHandlerContext ctx, string roomCode, string uid, string requestId,
        int seq, int totalChunks, string chunkBase64, string fileName, bool done)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(ctx, group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发日志块（uid={Uid}）", roomCode, uid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;
            if (string.IsNullOrEmpty(fileName) || !LogFileNameRegex.IsMatch(fileName)) return;

            // 认领映射（下载期间保留，done 块转发后才删）；无映射 = 过期或伪造，丢弃
            if (!LogTransferRequests.TryGetValue(requestId, out var req)) return;

            var rateKey = $"{group}:{uid}";
            var isBusyMarker = done && totalChunks == 0;
            if (isBusyMarker)
            {
                // 忙标记也纳入限流（独立宽松阈值 5 个/秒），防止异常端刷忙标记泛洪请求方
                if (!PassRateLimit(LogBusyRateLimit, rateKey, 5)) return;
            }
            else
            {
                if (totalChunks <= 0 || totalChunks > MaxLogFileChunks) return;
                if (seq < 0 || seq >= totalChunks) return;
                if (!string.IsNullOrEmpty(chunkBase64) && chunkBase64.Length > MaxChunkBase64Chars)
                {
                    _logger.LogWarning("成员日志块超限（uid={Uid}, {Chars} 字符），丢弃", uid, chunkBase64.Length);
                    return;
                }

                // 简单限流：同 uid 每秒最多 30 块（正常节奏 ~6 块/秒，留数倍余量；防滥用兜底而非精确配额）
                if (!PassRateLimit(LogChunkRateLimit, rateKey, 30)) return;
            }

            var payload = new
            {
                uid,
                requestId,
                seq,
                totalChunks,
                chunkBase64 = chunkBase64 ?? "",
                fileName,
                done
            };
            await _broadcaster.SendToConnectionAsync(req.RequesterConnectionId, "MemberLogFileChunk", payload, payload);
            // 下载结束（含忙标记）：删除映射，请求生命周期闭环
            if (done) LogTransferRequests.TryRemove(requestId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberLogChunk 失败");
        }
    }
}
