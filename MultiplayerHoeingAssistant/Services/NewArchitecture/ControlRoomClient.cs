using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using MultiplayerHoeingAssistant.Dto;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services.NewArchitecture;

/// <summary>
/// 连接新 /control-hub 的 SignalR 客户端。
/// 负责连接、重连、加入控制房间、状态上报、接收服务器事件。
/// </summary>
public class ControlRoomClient : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<List<MemberDto>>? OnPlayersUpdated;
    public event Action<MemberDesiredStateDto>? OnDesiredStateUpdated;
    public event Action<long>? OnTriggerOnline;
    public event Action<long>? OnExecuteOnlineGroups;
    public event Action<long>? OnAllReadyConfirm;
    public event Action<RemoteCommand>? OnRemoteCommand;
    public event Action<string>? OnJoinRejected;

    public async Task ConnectAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, string clientInstanceId)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(serverUrl.TrimEnd('/') + "/"), "control-hub"))
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        _connection.On<List<MemberDto>>("ControlRoomPlayersUpdated", players => OnPlayersUpdated?.Invoke(players));
        _connection.On<MemberDesiredStateDto>("MemberDesiredStateUpdated", state => OnDesiredStateUpdated?.Invoke(state));
        _connection.On<long>("TriggerOnline", sessionId => OnTriggerOnline?.Invoke(sessionId));
        _connection.On<long>("ExecuteOnlineGroups", sessionId => OnExecuteOnlineGroups?.Invoke(sessionId));
        _connection.On<long>("AllReadyConfirm", sessionId => OnAllReadyConfirm?.Invoke(sessionId));
        _connection.On<RemoteCommand>("RemoteCommand", cmd => OnRemoteCommand?.Invoke(cmd));
        _connection.On<string>("JoinRejected", reason => OnJoinRejected?.Invoke(reason));

        // 重连由 WithAutomaticReconnect 策略统一处理，避免 Closed 中递归 ConnectAsync 导致栈溢出。
        await _connection.StartAsync();
        await _connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, clientInstanceId);
    }

    public async Task ReportControlStatusAsync(ControlStatusDto status)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("ReportControlStatus", status);
    }

    public async Task UpdateMemberDesiredStateAsync(string targetUid, MemberDesiredStateDto state)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("UpdateMemberDesiredState", targetUid, state);
    }

    public async Task ReportOnlineEventAsync(int generation)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("ReportOnlineEvent", generation);
    }

    public async Task ConfirmAllReadyAsync(long sessionId)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("ConfirmAllReady", sessionId);
    }

    public async Task SendRemoteCommandAsync(RemoteCommand cmd)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("SendRemoteCommand", cmd);
    }

    public async Task ClearOnlineHistoryAsync(string targetUid)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("ClearOnlineHistory", targetUid);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}
