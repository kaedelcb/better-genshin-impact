using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.RoomControl.Domain;
using BgiCoordinatorServer.RoomControl.Dto;
using BgiCoordinatorServer.RoomControl.Services;
using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.RoomControl.Hubs;

public class ControlRoomHub : Hub<IControlRoomClient>
{
    private readonly IControlRoomManager _controlRoomManager;
    private readonly IOnlineSessionManager _onlineSessionManager;
    private readonly ILogger<ControlRoomHub> _logger;

    public ControlRoomHub(IControlRoomManager controlRoomManager, IOnlineSessionManager onlineSessionManager, ILogger<ControlRoomHub> logger)
    {
        _controlRoomManager = controlRoomManager;
        _onlineSessionManager = onlineSessionManager;
        _logger = logger;
    }

    public async Task JoinControlRoom(string roomCode, string password, string playerUid, string playerName, string clientInstanceId)
    {
        try
        {
            var member = await _controlRoomManager.JoinAsync(roomCode, password, playerUid, playerName, clientInstanceId, Context.ConnectionId);
            if (member == null)
            {
                await Clients.Caller.JoinRejected("密码错误或UID不在白名单中");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupName(roomCode));
            Context.Items["PlayerUid"] = member.PlayerUid;
            await BroadcastMembersAsync(roomCode);

            // 推送该成员的期望状态
            await Clients.Caller.MemberDesiredStateUpdated(new MemberDesiredStateDto
            {
                PlayerUid = member.PlayerUid,
                ScheduledOnlineTime = member.ScheduledOnlineTime,
                OnlineHoeingGroupNames = member.OnlineHoeingGroupNames,
                OnlineHoeingGroupTypes = member.OnlineHoeingGroupTypes,
                ExpectedHoeingPlayers = member.ExpectedHoeingPlayers,
                QuickCommands = member.QuickCommands
            });

            // 成员上线时立即检查定时上线：解决“迟到加入”与“离线到点后才上线”
            var room = await _controlRoomManager.GetAsync(roomCode);
            if (room != null)
            {
                var shanghai = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
                var session = room.TryStartSessionForScheduledMember(member.PlayerUid, shanghai, TimeSpan.FromMinutes(5));
                if (session != null)
                {
                    _logger.LogInformation("Join 触发 Schedule，room={Room}, member={Member}, session={SessionId}", roomCode, member.PlayerUid, session.Id);
                    await Clients.Caller.TriggerOnline(session.Id);
                    // 立即驱动一次状态机
                    await _onlineSessionManager.TickAsync(roomCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JoinControlRoom 失败");
            await Clients.Caller.JoinRejected($"加入失败: {ex.Message}");
        }
    }

    public async Task ReportControlStatus(ControlStatusDto status)
    {
        var ok = await _controlRoomManager.UpdateReportedStateAsync(Context.ConnectionId, new MemberReportedState(
            status.BgiStatus,
            status.TaskRunning,
            status.CurrentTaskName,
            status.CurrentTaskGroupName,
            status.CurrentRouteDisplay,
            status.AutoHoeingRunning,
            status.AutoHoeingProgress,
            status.ConfigGroups,
            status.OneClickConfigs,
            status.ConfigGroupTasks,
            status.OneClickTasks,
            status.Hotkeys), default);

        if (ok)
        {
            var room = await _controlRoomManager.GetByConnectionIdAsync(Context.ConnectionId);
            if (room != null)
            {
                await BroadcastMembersAsync(room.RoomCode);

                // 每次状态上报（心跳）都检查定时上线：覆盖“在线时设定 schedule 但错过整点扫描”
                var playerUid = GetActorUidFromContext();
                var shanghai = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
                var session = room.TryStartSessionForScheduledMember(playerUid, shanghai, TimeSpan.FromMinutes(5));
                if (session != null)
                {
                    _logger.LogInformation("ReportControlStatus 触发 Schedule，room={Room}, member={Member}, session={SessionId}", room.RoomCode, playerUid, session.Id);
                    await Clients.Caller.TriggerOnline(session.Id);
                    await _onlineSessionManager.TickAsync(room.RoomCode);
                }
            }
        }
    }

    public async Task UpdateMemberDesiredState(string targetUid, MemberDesiredStateDto state)
    {
        // TODO: 从 Context 解析当前 actorUid 并做权限校验
        var actorUid = GetActorUidFromContext();
        var ok = await _controlRoomManager.UpdateDesiredStateAsync(actorUid, targetUid, new MemberDesiredState(
            state.ScheduledOnlineTime,
            state.OnlineHoeingGroupNames,
            state.OnlineHoeingGroupTypes,
            state.ExpectedHoeingPlayers,
            state.QuickCommands), default);

        if (ok)
        {
            var room = await _controlRoomManager.GetByPlayerUidAsync(targetUid);
            if (room != null)
            {
                await BroadcastMembersAsync(room.RoomCode);
                await Clients.Group(GetGroupName(room.RoomCode)).MemberDesiredStateUpdated(new MemberDesiredStateDto
                {
                    PlayerUid = targetUid,
                    ScheduledOnlineTime = state.ScheduledOnlineTime,
                    OnlineHoeingGroupNames = state.OnlineHoeingGroupNames,
                    OnlineHoeingGroupTypes = state.OnlineHoeingGroupTypes,
                    ExpectedHoeingPlayers = state.ExpectedHoeingPlayers,
                    QuickCommands = state.QuickCommands
                });
            }
        }
    }

    public async Task ReportOnlineEvent(int generation)
    {
        await _controlRoomManager.ReportOnlineEventAsync(Context.ConnectionId, generation);
        var room = await _controlRoomManager.GetByConnectionIdAsync(Context.ConnectionId);
        if (room != null)
        {
            await BroadcastMembersAsync(room.RoomCode);
            // 驱动 OnlineSession 状态机
            await _onlineSessionManager.TickAsync(room.RoomCode);
        }
    }

    public async Task ConfirmAllReady(long sessionId)
    {
        var room = await _controlRoomManager.GetByConnectionIdAsync(Context.ConnectionId);
        if (room == null) return;

        var member = room.GetMemberByConnectionId(Context.ConnectionId);
        if (member == null) return;

        await _onlineSessionManager.ConfirmMemberAsync(room.RoomCode, sessionId, member.PlayerUid);
        await _onlineSessionManager.TickAsync(room.RoomCode);
    }

    public async Task ClearOnlineHistory(string targetUid)
    {
        var actorUid = GetActorUidFromContext();
        var ok = await _controlRoomManager.ClearOnlineHistoryAsync(actorUid, targetUid);
        if (ok)
        {
            var room = await _controlRoomManager.GetByPlayerUidAsync(targetUid);
            if (room != null)
                await BroadcastMembersAsync(room.RoomCode);
        }
    }

    /// <summary>
    /// 转发远程命令给目标成员。Web 控制端/房主通过新 Hub 下发，助手端通过新 Hub 接收。
    /// 过滤掉 Web 控制端自身（clientInstanceId 以 web_ 开头）。
    /// </summary>
    public async Task SendRemoteCommand(RemoteCommand command)
    {
        var room = await _controlRoomManager.GetByConnectionIdAsync(Context.ConnectionId);
        if (room == null) return;

        var onlineMembers = room.Members
            .Where(m => m.IsOnline && !m.ClientInstanceId.StartsWith("web_"))
            .ToList();

        var targetConnIds = new List<string>();
        if (command.Target.Contains("*"))
        {
            targetConnIds.AddRange(onlineMembers.Select(m => m.ConnectionId).OfType<string>());
        }
        else
        {
            foreach (var uid in command.Target)
            {
                var m = onlineMembers.FirstOrDefault(x => x.PlayerUid == uid);
                if (m?.ConnectionId != null)
                    targetConnIds.Add(m.ConnectionId);
            }
        }

        foreach (var connId in targetConnIds.Distinct())
        {
            try
            {
                await Clients.Client(connId).RemoteCommand(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转发 RemoteCommand 到 {ConnId} 失败", connId);
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var room = await _controlRoomManager.GetByConnectionIdAsync(Context.ConnectionId);
        if (room != null)
        {
            var roomCode = room.RoomCode;
            await _controlRoomManager.LeaveAsync(Context.ConnectionId);
            await BroadcastMembersAsync(roomCode);
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastMembersAsync(string roomCode)
    {
        var room = await _controlRoomManager.GetAsync(roomCode);
        if (room == null) return;

        var dtos = room.Members.Select(MemberDto.FromDomain).ToList();
        await Clients.Group(GetGroupName(roomCode)).ControlRoomPlayersUpdated(dtos);
    }

    private static string GetGroupName(string roomCode) => $"CTRL_{roomCode}";

    private string GetActorUidFromContext()
    {
        return Context.Items.TryGetValue("PlayerUid", out var value) && value is string uid ? uid : "";
    }
}

public interface IControlRoomClient
{
    Task JoinRejected(string reason);
    Task ControlRoomPlayersUpdated(List<MemberDto> players);
    Task MemberDesiredStateUpdated(MemberDesiredStateDto state);
    Task TriggerOnline(long sessionId);
    Task ExecuteOnlineGroups(long sessionId);
    Task AllReadyConfirm(long sessionId);
    Task RemoteCommand(RemoteCommand command);
}
