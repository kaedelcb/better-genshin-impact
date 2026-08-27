namespace BgiCoordinatorServer.RoomControl.Domain;

/// <summary>
/// 控制房间聚合根。包含房间级期望状态、成员列表、当前 OnlineSession 引用。
/// 所有状态变更必须通过领域方法完成，并产生领域事件写入事件存储。
/// </summary>
public class ControlRoom
{
    public string RoomCode { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public string OwnerUid { get; private set; } = "";
    public List<string> AllowedUids { get; private set; } = [];
    public DateTime CreatedAt { get; private set; }
    public DateTime LastActivityAt { get; private set; }

    private readonly List<ControlRoomMember> _members = [];
    public IReadOnlyList<ControlRoomMember> Members => _members.AsReadOnly();

    private readonly List<OnlineSession> _onlineSessions = [];
    public IReadOnlyList<OnlineSession> OnlineSessions => _onlineSessions.AsReadOnly();

    /// <summary>当前正在进行的 OnlineSession，null 表示没有进行中的调度。</summary>
    public OnlineSession? CurrentSession => _onlineSessions.FirstOrDefault(s => !s.IsTerminal);

    private ControlRoom() { }

    public static ControlRoom Create(string roomCode, string passwordHash, string ownerUid, List<string> allowedUids)
    {
        if (string.IsNullOrWhiteSpace(roomCode)) throw new ArgumentException("房间码不能为空", nameof(roomCode));
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new ArgumentException("密码哈希不能为空", nameof(passwordHash));

        return new ControlRoom
        {
            RoomCode = roomCode,
            PasswordHash = passwordHash,
            OwnerUid = ownerUid,
            AllowedUids = allowedUids?.ToList() ?? [],
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
    }

    public void UpdateActivity() => LastActivityAt = DateTime.UtcNow;

    public ControlRoomMember JoinMember(string playerUid, string playerName, string clientInstanceId, string connectionId)
    {
        if (!AllowedUids.Contains(playerUid) && AllowedUids.Count > 0)
            throw new InvalidOperationException($"UID {playerUid} 不在白名单中");

        var existing = _members.FirstOrDefault(m => m.PlayerUid == playerUid && m.ClientInstanceId == clientInstanceId);
        if (existing != null)
        {
            existing.MarkOnline(connectionId, clientInstanceId, playerName);
            existing.UpdateHeartbeat();
            UpdateActivity();
            return existing;
        }

        var member = ControlRoomMember.Create(this, playerUid, playerName, clientInstanceId, connectionId);
        _members.Add(member);
        UpdateActivity();
        return member;
    }

    public bool LeaveMember(string connectionId)
    {
        var member = _members.FirstOrDefault(m => m.ConnectionId == connectionId);
        if (member == null) return false;

        member.MarkOffline();
        UpdateActivity();
        return true;
    }

    public ControlRoomMember? GetMemberByUid(string playerUid)
        => _members.FirstOrDefault(m => m.PlayerUid == playerUid);

    public IReadOnlyList<ControlRoomMember> GetMembersByUid(string playerUid)
        => _members.Where(m => m.PlayerUid == playerUid).ToList().AsReadOnly();

    public ControlRoomMember? GetMemberByConnectionId(string connectionId)
        => _members.FirstOrDefault(m => m.ConnectionId == connectionId);

    public void SetMemberDesiredState(string playerUid, MemberDesiredState state)
    {
        var members = GetMembersByUid(playerUid);
        if (members.Count == 0)
            throw new InvalidOperationException($"成员 {playerUid} 不存在");

        foreach (var member in members)
        {
            member.UpdateDesiredState(state);
        }
        UpdateActivity();
    }

    /// <summary>
    /// 由 ScheduleEngine 调用：到点后创建新的 OnlineSession。
    /// 若已有进行中的 Session，则忽略（避免重复触发）。
    /// </summary>
    public OnlineSession? StartOnlineSession(DateTime scheduledTime, TimeSpan waitingWindow)
    {
        if (CurrentSession != null && !CurrentSession.IsTerminal)
            return null;

        var threshold = _members
            .Where(m => m.IsOnline)
            .Select(m => Math.Max(1, m.ExpectedHoeingPlayers))
            .DefaultIfEmpty(1)
            .Min();

        var session = OnlineSession.Create(RoomCode, scheduledTime, threshold, waitingWindow);
        _onlineSessions.Add(session);
        UpdateActivity();
        return session;
    }

    /// <summary>
    /// 成员上线或心跳时调用：若该成员有定时上线且今天未触发，且当前时间已过设定时间，
    /// 则为其启动 OnlineSession。解决“9 点迟到加入”和“离线成员到点后才上线”问题。
    /// </summary>
    public OnlineSession? TryStartSessionForScheduledMember(string playerUid, DateTime nowShanghai, TimeSpan waitingWindow)
    {
        var member = GetMemberByUid(playerUid);
        if (member == null || !member.IsOnline) return null;
        if (string.IsNullOrEmpty(member.ScheduledOnlineTime)) return null;

        var todayStr = nowShanghai.ToString("yyyy-MM-dd");
        if (member.IsScheduleFiredToday(todayStr)) return null;

        if (!TimeSpan.TryParse(member.ScheduledOnlineTime, out var scheduledTimeOfDay))
            return null;

        var scheduledToday = nowShanghai.Date.Add(scheduledTimeOfDay);
        // 允许在设定时间之后、等待窗口内触发；已超出窗口视为今天已错过，不再触发
        if (nowShanghai < scheduledToday || nowShanghai >= scheduledToday + waitingWindow)
            return null;

        if (CurrentSession != null && !CurrentSession.IsTerminal)
            return null;

        var session = OnlineSession.Create(RoomCode, scheduledToday, Math.Max(1, member.ExpectedHoeingPlayers), waitingWindow);
        _onlineSessions.Add(session);
        member.MarkScheduledFired(todayStr);
        UpdateActivity();
        return session;
    }

    /// <summary>成员上报已就位，由 OnlineSessionManager 驱动状态流转。</summary>
    public void ReportMemberOnlineEvent(string playerUid, int generation)
    {
        var member = GetMemberByUid(playerUid);
        if (member == null || !member.IsOnline) return;

        member.ReportOnlineEvent(generation);
        CurrentSession?.MemberReady(playerUid);
        UpdateActivity();
    }

    public void ConfirmMemberReady(long sessionId, string playerUid)
    {
        var session = _onlineSessions.FirstOrDefault(s => s.Id == sessionId);
        session?.ConfirmMember(playerUid);
        UpdateActivity();
    }

    public void MarkSessionExecuted(long sessionId)
    {
        var session = _onlineSessions.FirstOrDefault(s => s.Id == sessionId);
        session?.MarkExecuted();
        UpdateActivity();
    }

    public void CancelCurrentSession(string? reason = null)
    {
        CurrentSession?.Cancel(reason);
        UpdateActivity();
    }

    public void CleanupExpiredOnlineEvents(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        foreach (var member in _members.Where(m => m.IsOnline && m.OnlineEventTime < cutoff))
        {
            member.ResetOnlineEvent();
        }
    }

    public bool ClearMemberOnlineHistory(string playerUid)
    {
        var member = GetMemberByUid(playerUid);
        if (member == null) return false;
        member.ClearOnlineHistory();
        UpdateActivity();
        return true;
    }
}
