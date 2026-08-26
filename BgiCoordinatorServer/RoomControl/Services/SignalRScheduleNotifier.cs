using BgiCoordinatorServer.RoomControl.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.RoomControl.Services;

/// <summary>
/// 通过 SignalR 向客户端发送 Schedule 触发与执行命令。
/// </summary>
public class SignalRScheduleNotifier : IScheduleNotifier
{
    private readonly IHubContext<ControlRoomHub, IControlRoomClient> _hubContext;

    public SignalRScheduleNotifier(IHubContext<ControlRoomHub, IControlRoomClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task TriggerOnlineAsync(string roomCode, string playerUid, long sessionId, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group(GetGroupName(roomCode))
            .TriggerOnline(sessionId);
    }

    public async Task ExecuteOnlineGroupsAsync(string roomCode, long sessionId, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group(GetGroupName(roomCode))
            .ExecuteOnlineGroups(sessionId);
    }

    public async Task AllReadyConfirmAsync(string roomCode, long sessionId, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group(GetGroupName(roomCode))
            .AllReadyConfirm(sessionId);
    }

    private static string GetGroupName(string roomCode) => $"CTRL_{roomCode}";
}
