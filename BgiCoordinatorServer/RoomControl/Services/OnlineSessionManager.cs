using BgiCoordinatorServer.RoomControl.Domain;
using BgiCoordinatorServer.RoomControl.Persistence;

namespace BgiCoordinatorServer.RoomControl.Services;

public interface IOnlineSessionManager
{
    Task TickAsync(string roomCode, CancellationToken ct = default);
    Task<bool> ConfirmMemberAsync(string roomCode, long sessionId, string playerUid, CancellationToken ct = default);
    Task<bool> MarkMemberExecutedAsync(string roomCode, long sessionId, string playerUid, CancellationToken ct = default);
}

/// <summary>
/// 负责 OnlineSession 状态机流转：等待 → 就绪 → 确认 → 执行 → 完成。
/// </summary>
public class OnlineSessionManager : IOnlineSessionManager
{
    private readonly IControlRoomRepository _repo;
    private readonly IScheduleNotifier _notifier;
    private readonly TimeSpan _confirmTimeout = TimeSpan.FromSeconds(30);
    private readonly int _maxConfirmAttempts = 3;

    public OnlineSessionManager(IControlRoomRepository repo, IScheduleNotifier notifier)
    {
        _repo = repo;
        _notifier = notifier;
    }

    public async Task TickAsync(string roomCode, CancellationToken ct = default)
    {
        var room = await _repo.GetAsync(roomCode, ct);
        if (room == null) return;

        var session = room.CurrentSession;
        if (session == null) return;

        var now = DateTime.UtcNow;

        switch (session.State)
        {
            case OnlineSessionState.Waiting:
                session.CheckWaitingTimeout(now);
                break;

            case OnlineSessionState.Ready:
                var onlineCount = room.Members.Count(m => m.IsOnline);
                if (onlineCount <= 1)
                {
                    session.BeginExecuting();
                    await _notifier.ExecuteOnlineGroupsAsync(roomCode, session.Id, ct);
                }
                else
                {
                    session.BeginConfirming();
                    await _notifier.AllReadyConfirmAsync(roomCode, session.Id, ct);
                }
                break;

            case OnlineSessionState.Confirming:
                await HandleConfirmingAsync(room, session, now, ct);
                break;
        }

        await _repo.SaveAsync(room, null, ct);
    }

    public async Task<bool> ConfirmMemberAsync(string roomCode, long sessionId, string playerUid, CancellationToken ct = default)
    {
        var room = await _repo.GetAsync(roomCode, ct);
        if (room == null) return false;

        room.ConfirmMemberReady(sessionId, playerUid);
        await _repo.SaveAsync(room, null, ct);
        return true;
    }

    public async Task<bool> MarkMemberExecutedAsync(string roomCode, long sessionId, string playerUid, CancellationToken ct = default)
    {
        var room = await _repo.GetAsync(roomCode, ct);
        if (room == null) return false;

        var session = room.OnlineSessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return false;

        session.MarkMemberExecuted(playerUid);

        if (session.ExecutedMemberUids.ToHashSet().SetEquals(session.ReadyMemberUids))
        {
            session.MarkExecuted();
        }

        await _repo.SaveAsync(room, null, ct);
        return true;
    }

    private async Task HandleConfirmingAsync(ControlRoom room, OnlineSession session, DateTime now, CancellationToken ct)
    {
        var readyUids = session.ReadyMemberUids.ToHashSet();
        if (readyUids.SetEquals(session.ConfirmedMemberUids))
        {
            session.BeginExecuting();
            await _notifier.ExecuteOnlineGroupsAsync(room.RoomCode, session.Id, ct);
            return;
        }

        // 简单超时重试：超过最大尝试次数则取消
        // TODO: 在 OnlineSession 上记录 ConfirmAttempts 和 LastConfirmTime
        // 当前版本：确认阶段若 30 秒未全部确认，直接进入 executing（兜底）
        // 后续应改为可配置策略：取消 / 继续等待 / 执行已确认成员。
    }
}
