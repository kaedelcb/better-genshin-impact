using System.Globalization;
using BetterGenshinImpact.Shared.CooperativeRerun;
using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    public RerunSnapshot ApplyRerun(GatewayHandlerContext ctx, RerunRequest request, Func<string, bool> hasCapability)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null)
            throw new RerunProtocolException("rerun_not_participant", "Connection is not a current room participant.");

        lock (room)
        {
            var player = room.Players.SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player == null || string.IsNullOrWhiteSpace(player.PlayerUid))
                throw new RerunProtocolException("rerun_not_participant", "Connection has no current player identity.");

            if (!hasCapability(ctx.ConnectionId))
                throw new RerunProtocolException("rerun_capability_required", "The caller must advertise hoeing.rerun.v1.");

            var scope = request.Scope;
            if (string.IsNullOrWhiteSpace(scope))
                throw new RerunProtocolException("rerun_scope", "Task scope is required.");

            // 终态会话清理：同一世界轮里第二次启动锄地（用户手动重开 / 守护重开 / 异常退出后重开）
            // 会带新 token 发起 Enroll。旧会话若已 Completed/Aborted，就不承载任何待完成语义，
            // 必须允许被新一轮替换；否则新 token 会被 rerun_token 拒绝，把整轮锄地直接拖停。
            // 另外也回收**孤儿会话**（非终态，但全体成员都早已停止续租）：典型场景是起过一轮后相关
            // 进程都没了、房间却还活着——它同样会以 rerun_token 永久挡住新一轮。
            // 注意活跃会话一律保留：进行中的幂等重试与迟到消息必须打到原会话上。
            if (request.Operation == RerunProtocol.Enroll
                && room.RerunExecution is { } existing
                && (existing.IsTerminal || existing.IsOrphaned(DateTime.UtcNow)))
            {
                existing.Abort(existing.IsTerminal ? "replaced_after_terminal" : "reclaimed_orphaned");
                room.RerunExecution = null;
            }

            // Scope is the client task-round label. WorldRound is retained only as server lifecycle metadata;
            // world reset/room close invalidate the execution explicitly instead of comparing counters here.
            if (room.RerunExecution != null && room.RerunExecution.Scope != scope)
                throw new RerunProtocolException("rerun_scope_mismatch", "Request scope differs from active task.");
            if (room.ExpCapBroadcasted)
                room.RerunExecution?.Abort("exp_cap_reached");

            if (room.RerunExecution == null)
            {
                if (request.Operation != RerunProtocol.Enroll)
                    throw new RerunProtocolException("rerun_not_enrolled", "Enroll before requesting rerun state.");
                if (room.Players.Any(p => string.IsNullOrWhiteSpace(p.PlayerUid)) ||
                    room.Players.Select(p => p.PlayerUid).Distinct(StringComparer.Ordinal).Count() != room.Players.Count)
                    throw new RerunProtocolException("rerun_invalid_roster", "Every participant must have a unique nonempty UID.");
                if (room.Players.Any(p => !hasCapability(p.ConnectionId)))
                    throw new RerunProtocolException("rerun_capability_required", "Every participant must advertise hoeing.rerun.v1.");
                if (room.ExpCapBroadcasted)
                    throw new RerunProtocolException("rerun_exp_cap_reached", "The room has reached the experience cap.");

                var state = new RerunExecutionState(scope, room.CurrentWorldRound,
                    room.Players.ToDictionary(p => p.PlayerUid, p => p.ConnectionId, StringComparer.Ordinal), DateTime.UtcNow);
                var snapshot = state.Apply(player.PlayerUid, ctx.ConnectionId, request, DateTime.UtcNow);
                // Publish only after a valid first enrollment; malformed requests cannot reserve the session.
                room.RerunExecution = state;
                return snapshot;
            }

            return room.RerunExecution.Apply(player.PlayerUid, ctx.ConnectionId, request, DateTime.UtcNow);
        }
    }
}
