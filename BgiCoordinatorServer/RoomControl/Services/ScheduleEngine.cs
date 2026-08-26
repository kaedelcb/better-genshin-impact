using BgiCoordinatorServer.RoomControl.Persistence;

namespace BgiCoordinatorServer.RoomControl.Services;

/// <summary>
/// 后台调度服务。每分钟扫描所有控制房间的 Schedule，到点后创建 OnlineSession 并通知相关成员。
/// </summary>
public class ScheduleEngine : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ScheduleEngine> _logger;
    private readonly TimeSpan _scanInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _waitingWindow = TimeSpan.FromMinutes(5);

    public ScheduleEngine(IServiceProvider services, ILogger<ScheduleEngine> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduleEngine 启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ScheduleEngine 扫描异常");
            }

            await Task.Delay(_scanInterval, stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IControlRoomRepository>();
        var onlineManager = scope.ServiceProvider.GetRequiredService<IOnlineSessionManager>();
        var notifier = scope.ServiceProvider.GetService<IScheduleNotifier>();

        var now = DateTime.UtcNow;
        // 使用上海时间判断 HH:mm（与原业务语义一致）
        var shanghai = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
        var currentTimeStr = shanghai.ToString("HH:mm");

        var rooms = await repo.GetAllWithScheduleAsync(ct);

        foreach (var room in rooms)
        {
            foreach (var member in room.Members.Where(m => !string.IsNullOrEmpty(m.ScheduledOnlineTime)))
            {
                if (!member.IsOnline) continue;
                if (member.IsScheduleFiredToday(shanghai.ToString("yyyy-MM-dd"))) continue;
                if (!TimeSpan.TryParse(member.ScheduledOnlineTime, out var scheduledTime)) continue;

                var scheduled = shanghai.Date.Add(scheduledTime);
                // 到点或迟到加入：当前时间处于 [schedule, schedule+window) 内即可触发
                if (shanghai >= scheduled && shanghai < scheduled + _waitingWindow)
                {
                    var session = room.TryStartSessionForScheduledMember(member.PlayerUid, shanghai, _waitingWindow);
                    if (session != null)
                    {
                        _logger.LogInformation("房间 {Room} Schedule 触发，member={Member}, session={SessionId}", room.RoomCode, member.PlayerUid, session.Id);
                        await notifier?.TriggerOnlineAsync(room.RoomCode, member.PlayerUid, session.Id, ct)!;
                    }
                }
            }

            // 驱动状态机
            await onlineManager.TickAsync(room.RoomCode, ct);
            await repo.SaveAsync(room, null, ct);
        }
    }
}

public interface IScheduleNotifier
{
    Task TriggerOnlineAsync(string roomCode, string playerUid, long sessionId, CancellationToken ct = default);
    Task AllReadyConfirmAsync(string roomCode, long sessionId, CancellationToken ct = default);
    Task ExecuteOnlineGroupsAsync(string roomCode, long sessionId, CancellationToken ct = default);
}
