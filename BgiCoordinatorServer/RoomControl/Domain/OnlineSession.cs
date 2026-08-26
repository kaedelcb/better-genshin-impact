namespace BgiCoordinatorServer.RoomControl.Domain;

/// <summary>
/// 一次定时上线/全员就绪的完整状态机实例。
/// </summary>
public class OnlineSession
{
    public long Id { get; private set; }
    public string RoomCode { get; private set; } = "";
    public DateTime ScheduledTime { get; private set; }
    public OnlineSessionState State { get; private set; } = OnlineSessionState.Scheduled;
    public int Generation { get; private set; }
    public int Threshold { get; private set; }
    public DateTime WaitingDeadline { get; private set; }

    private readonly List<string> _readyMemberUids = [];
    public IReadOnlyList<string> ReadyMemberUids => _readyMemberUids.AsReadOnly();

    private readonly List<string> _confirmedMemberUids = [];
    public IReadOnlyList<string> ConfirmedMemberUids => _confirmedMemberUids.AsReadOnly();

    private readonly List<string> _executedMemberUids = [];
    public IReadOnlyList<string> ExecutedMemberUids => _executedMemberUids.AsReadOnly();

    public string? FailureReason { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    public bool IsTerminal => State is OnlineSessionState.Done
                                          or OnlineSessionState.Missed
                                          or OnlineSessionState.Cancelled;

    private OnlineSession() { }

    public static OnlineSession Create(string roomCode, DateTime scheduledTime, int threshold, TimeSpan waitingWindow)
    {
        return new OnlineSession
        {
            RoomCode = roomCode,
            ScheduledTime = scheduledTime,
            State = OnlineSessionState.Waiting,
            Generation = GenerateGeneration(scheduledTime),
            Threshold = threshold,
            WaitingDeadline = scheduledTime + waitingWindow,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 成员上报已就位。若就绪人数达到阈值，状态变为 Ready。
    /// </summary>
    public void MemberReady(string playerUid)
    {
        if (State != OnlineSessionState.Waiting) return;
        if (_readyMemberUids.Contains(playerUid)) return;

        _readyMemberUids.Add(playerUid);
        if (_readyMemberUids.Count >= Threshold)
        {
            State = OnlineSessionState.Ready;
        }
    }

    /// <summary>进入确认握手阶段。</summary>
    public void BeginConfirming()
    {
        if (State != OnlineSessionState.Ready) return;
        State = OnlineSessionState.Confirming;
    }

    /// <summary>成员确认全员就绪。</summary>
    public void ConfirmMember(string playerUid)
    {
        if (State != OnlineSessionState.Confirming) return;
        if (!_confirmedMemberUids.Contains(playerUid))
            _confirmedMemberUids.Add(playerUid);
    }

    /// <summary>所有待确认成员都已确认，进入执行阶段。</summary>
    public void BeginExecuting()
    {
        if (State != OnlineSessionState.Confirming) return;
        State = OnlineSessionState.Executing;
    }

    /// <summary>标记某成员已执行完本次上线绑定的配置组。</summary>
    public void MarkMemberExecuted(string playerUid)
    {
        if (State != OnlineSessionState.Executing) return;
        if (!_executedMemberUids.Contains(playerUid))
            _executedMemberUids.Add(playerUid);
    }

    /// <summary>标记本次 OnlineSession 执行完成。</summary>
    public void MarkExecuted()
    {
        if (State != OnlineSessionState.Executing) return;
        State = OnlineSessionState.Done;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>等待窗口超时，人数仍不足。</summary>
    public void MarkMissed(string reason)
    {
        if (IsTerminal) return;
        State = OnlineSessionState.Missed;
        FailureReason = reason;
        CompletedAt = DateTime.UtcNow;
    }

    public void Cancel(string? reason = null)
    {
        if (IsTerminal) return;
        State = OnlineSessionState.Cancelled;
        FailureReason = reason;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// 检查是否已在等待阶段超时。由 ScheduleEngine/OnlineSessionManager 定期调用。
    /// </summary>
    public bool CheckWaitingTimeout(DateTime now)
    {
        if (State != OnlineSessionState.Waiting) return false;
        if (now < WaitingDeadline) return false;

        MarkMissed($"等待窗口超时，就绪人数 {_readyMemberUids.Count}/{Threshold}");
        return true;
    }

    private static int GenerateGeneration(DateTime scheduledTime)
    {
        // 使用 UTC 时间戳生成单调递增的 generation，避免客户端本地 generation 冲突。
        return (int)(scheduledTime.ToUnixTimeSeconds() % int.MaxValue);
    }
}

public enum OnlineSessionState
{
    Scheduled,
    Waiting,
    Ready,
    Confirming,
    Executing,
    Done,
    Missed,
    Cancelled
}

internal static class DateTimeExtensions
{
    public static long ToUnixTimeSeconds(this DateTime dateTime)
        => ((DateTimeOffset)dateTime).ToUnixTimeSeconds();
}
