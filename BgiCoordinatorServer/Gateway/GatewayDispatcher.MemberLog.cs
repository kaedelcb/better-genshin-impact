namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：日志三件套 + 截图汇聚族（log.* 7 条 + screenshot.report，与旧方法一一对应）。
/// MemberScreenshot/MemberLogBatch/MemberLogSubscribersChanged/MemberLogFilesRequested/
/// MemberLogFileList/MemberLogDownloadRequested/MemberLogFileChunk 走事件（evt 双发），响应一律 ack。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterMemberLog()
    {
        _commands[GatewayProtocol.Names.LogReportBatch] = async (env, ctx) =>
        {
            await _ops.ReportMemberLogBatchAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "uid"),
                GetString(env, "senderName"),
                GetStringList(env, "lines")!,
                GetBool(env, "infoOnly"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.LogSubscribe] = async (env, ctx) =>
        {
            await _ops.SubscribeMemberLogAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "targetUid"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.LogUnsubscribe] = async (env, ctx) =>
        {
            await _ops.UnsubscribeMemberLogAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "targetUid"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.LogRequestFiles] = async (env, ctx) =>
        {
            await _ops.RequestMemberLogFilesAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "targetUid"),
                GetString(env, "requestId"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.LogReportFiles] = async (env, ctx) =>
        {
            await _ops.ReportMemberLogFilesAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "uid"),
                GetString(env, "requestId"),
                Get<List<Models.MemberLogFileDescriptor>>(env, "files")!);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.LogRequestDownload] = async (env, ctx) =>
        {
            await _ops.RequestMemberLogDownloadAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "targetUid"),
                GetString(env, "requestId"),
                GetString(env, "fileName"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.LogReportChunk] = async (env, ctx) =>
        {
            await _ops.ReportMemberLogChunkAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "uid"),
                GetString(env, "requestId"),
                GetInt(env, "seq"),
                GetInt(env, "totalChunks"),
                GetString(env, "chunkBase64"),
                GetString(env, "fileName"),
                GetBool(env, "done"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ScreenshotReport] = async (env, ctx) =>
        {
            await _ops.ReportMemberScreenshotAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "uid"),
                GetString(env, "jpegBase64"),
                GetInt(env, "width"),
                GetInt(env, "height"),
                GetDateTime(env, "capturedAt"));
            return new { ack = true };
        };
    }
}
