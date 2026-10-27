using System.Security.Cryptography;
using System.Text;
using BgiCoordinatorServer.Models;
using Microsoft.Extensions.Logging;

namespace BgiCoordinatorServer.Services;

internal interface IRoomLogScope
{
    RoomLogScopeSnapshot Resolve();
    RoomLogScopeSnapshot ResolveFailure() => Resolve();
}

internal sealed record RoomLogScopeSnapshot(IReadOnlyList<string> RoomCodes, string? GameSessionId = null) : IRoomLogScope
{
    public RoomLogScopeSnapshot Resolve() => this;
}

/// <summary>从服务端登记身份产生日志作用域和历史关联，不读取调用方自报的房间字段。</summary>
public sealed class RoomLogContext
{
    private readonly RoomManager _manager;
    private readonly RoomLogArchive _archive;
    private readonly ILogger<RoomLogContext> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, string[]> _controlRosters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ControlCode, string[] Uids)> _batches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ControlCode, string? BatchId)> _linkedSessions = new(StringComparer.Ordinal);

    public RoomLogContext(RoomManager manager, RoomLogArchive archive, ILogger<RoomLogContext> logger)
    {
        _manager = manager;
        _archive = archive;
        _logger = logger;
        manager.RoomLogSessionCreated += ObserveGameRoom;
        manager.RoomLogSessionEnded += EndGameRoom;
    }

    public IDisposable? BeginConnectionScope(ILogger logger, string connectionId)
        => logger.BeginScope(new ConnectionScope(this, connectionId, SnapshotConnection(connectionId)));

    internal IDisposable? BeginSnapshotScope(ILogger logger, RoomLogScopeSnapshot snapshot)
        => logger.BeginScope(snapshot);

    internal RoomLogScopeSnapshot SnapshotConnection(string connectionId)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var group = _manager.GetControlRoomGroupOrRemote(connectionId);
        var controlCode = RoomLogStore.NormalizeCode(group);
        if (controlCode is not null) codes.Add(controlCode);
        var (room, code) = _manager.GetRoomByConnectionId(connectionId);
        if (room is not null && code is not null) codes.Add(code);
        return new RoomLogScopeSnapshot(codes.ToArray(), room?.SessionId);
    }

    /// <summary>只在控制房间认证并登记成员之后调用。完整配置名单须与官方控制码算法吻合。</summary>
    public void RegisterControlRoster(string controlRoomCode, string memberUid, IReadOnlyList<string> configuredUids)
    {
        var code = RoomLogStore.NormalizeCode(controlRoomCode);
        var roster = NormalizeRoster(configuredUids);
        if (code is null || roster is null || !roster.Contains(memberUid, StringComparer.Ordinal)
            || ComputeControlCode(roster) != code) return;
        var collision = false;
        lock (_gate)
        {
            if (_controlRosters.TryGetValue(code, out var old) && !old.SequenceEqual(roster))
                collision = _ambiguousCodes.Add(code);
            else _controlRosters[code] = roster;
        }
        if (collision)
        {
            _archive.RecordHistoryWarning(code, "不同完整成员名单使用同一控制房间码，已拒绝新增历史关联；须核对历史覆盖范围。");
            _logger.LogWarning("控制房间 {RoomCode} 出现不同完整成员名单使用同一房间码，已停止新增历史关联，须核对历史覆盖范围", code);
            return;
        }
        foreach (var (room, _) in _manager.GetAllRoomsWithCodes()) ObserveGameRoom(RoomLogGameSession.Capture(room));
    }

    public void RegisterBatch(string controlRoomCode, string batchId, IReadOnlyList<string> participantUids)
    {
        var code = RoomLogStore.NormalizeCode(controlRoomCode);
        var roster = NormalizeRoster(participantUids);
        if (code is null || string.IsNullOrWhiteSpace(batchId) || roster is null) return;
        lock (_gate)
        {
            if (_ambiguousCodes.Contains(code)) return;
            if (_batches.TryGetValue(batchId, out var old)
                && (old.ControlCode != code || !old.Uids.SequenceEqual(roster))) return;
            _batches[batchId] = (code, roster);
        }
        foreach (var (room, _) in _manager.GetAllRoomsWithCodes()) ObserveGameRoom(RoomLogGameSession.Capture(room));
    }

    public void ObserveGameRoom(Room room) => ObserveGameRoom(RoomLogGameSession.Capture(room));

    private void ObserveGameRoom(RoomLogGameSession session)
    {
        var expected = NormalizeRoster(session.ExpectedParticipantUids);
        var actual = NormalizeRoster(session.ParticipantUids);
        // 必须是明确的完整名单。白名单包含玩家名，不能拿它当 UID 名单；UID 交集也不是关联依据。
        var roster = expected?.Length == session.ExpectedPlayerCount ? expected
            : actual?.Length == session.ExpectedPlayerCount ? actual : null;
        if (roster is null || !roster.Contains(session.HostUid, StringComparer.Ordinal)) return;
        string? controlCode = null;
        string source;
        string? verifiedBatchId = null;
        lock (_gate)
        {
            source = "完整成员名单与已认证控制房间配置一致";
            if (session.BatchId is not null && _batches.TryGetValue(session.BatchId, out var batch)
                && batch.Uids.SequenceEqual(roster) && !_ambiguousCodes.Contains(batch.ControlCode))
            {
                controlCode = batch.ControlCode;
                verifiedBatchId = session.BatchId;
                source = "服务端控制批次身份与完整参与名单一致";
            }
            else
            {
                var derivedCode = ComputeControlCode(roster);
                if (!_ambiguousCodes.Contains(derivedCode)
                    && _controlRosters.TryGetValue(derivedCode, out var configured) && configured.SequenceEqual(roster))
                    controlCode = derivedCode;
            }
            if (controlCode is null) return;
            if (_linkedSessions.TryGetValue(session.SessionId, out var linked)
                && linked.ControlCode == controlCode && (linked.BatchId is not null || verifiedBatchId is null)) return;
            _linkedSessions[session.SessionId] = (controlCode, verifiedBatchId);
        }
        // 持久化与日志均在 RoomManager/本服务锁外执行，不改变联机业务的锁顺序。
        _archive.RecordRoomLink(controlCode, session.Code, session.SessionId, session.CreatedAtUtc, DateTime.UtcNow,
            verifiedBatchId, session.HostUid, session.HostName);
        using var scope = _logger.BeginScope(new RoomLogScopeSnapshot([session.Code], session.SessionId));
        _logger.LogInformation("房间 {RoomCode} 会话 {SessionId} 已关联控制房间 {ControlCode}，创建于 {CreatedAt:O}；依据：{Source}",
            session.Code, session.SessionId, controlCode, session.CreatedAtUtc, source);
    }

    private void EndGameRoom(RoomLogGameSession session, DateTime endedAtUtc)
    {
        using var scope = _logger.BeginScope(new RoomLogScopeSnapshot([session.Code], session.SessionId));
        _logger.LogInformation("房间 {RoomCode} 会话 {SessionId} 已结束，创建于 {CreatedAt:O}，结束于 {EndedAt:O}",
            session.Code, session.SessionId, session.CreatedAtUtc, endedAtUtc);
        _archive.EndRoomLink(session.Code, session.SessionId, endedAtUtc);
    }

    private static string[]? NormalizeRoster(IReadOnlyList<string> uids)
    {
        if (uids.Count is < 1 or > 4 || uids.Any(uid => string.IsNullOrWhiteSpace(uid) || uid.Any(c => !char.IsAsciiDigit(c)))
            || uids.Distinct(StringComparer.Ordinal).Count() != uids.Count) return null;
        return uids.Order(StringComparer.Ordinal).ToArray();
    }

    internal static string ComputeControlCode(IReadOnlyList<string> sortedRoster)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", sortedRoster))))[..6];

    private sealed record ConnectionScope(RoomLogContext Owner, string ConnectionId, RoomLogScopeSnapshot Initial) : IRoomLogScope
    {
        public RoomLogScopeSnapshot Resolve() => Owner.SnapshotConnection(ConnectionId);
        public RoomLogScopeSnapshot ResolveFailure()
        {
            var current = Resolve();
            return current.RoomCodes.Count > 0 ? current : Initial;
        }
    }
}

/// <summary>RoomManager 锁内复制、锁外发布的纯观察快照。</summary>
internal sealed record RoomLogGameSession(string Code, string SessionId, DateTime CreatedAtUtc,
    string? BatchId, int ExpectedPlayerCount, string HostUid,
    IReadOnlyList<string> ExpectedParticipantUids, IReadOnlyList<string> ParticipantUids, string HostName = "")
{
    public static RoomLogGameSession Capture(Room room)
    {
        lock (room)
            return new(room.Code, room.SessionId, room.CreatedAt, room.HoeingBatchId, room.ExpectedPlayerCount,
                room.Players.FirstOrDefault(player => player.ConnectionId == room.HostConnectionId)?.PlayerUid ?? "",
                room.ExpectedParticipantUids.ToArray(), room.Players.Select(player => player.PlayerUid).ToArray(),
                room.Players.FirstOrDefault(player => player.ConnectionId == room.HostConnectionId)?.PlayerName ?? "");
    }
}
