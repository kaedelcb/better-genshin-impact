using System.Collections.Concurrent;
using System.Linq;
using BgiCoordinatorServer.Models;
using Microsoft.Extensions.Logging;

namespace BgiCoordinatorServer.Services;

public class RoomManager
{
    private const string CodeChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int CodeLength = 6;
    private const int MaxPlayers = 4;
    /// <summary>任务运行态（TaskRunning）超时秒数：超过该时长未收到成员续期上报，服务端自动复位为未运行（超时自愈，防崩溃残留）。</summary>
    internal const int TaskRunningTimeoutSec = 60;

    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    // connectionId → roomCode 反向索引，加速查找
    private readonly ConcurrentDictionary<string, string> _connectionRoomMap = new();
    private readonly ConcurrentDictionary<string, List<PlayerInfo>> _lastRemovedPlayers = new();
    private readonly int _maxRooms;
    private readonly ILogger<RoomManager>? _logger;

    // 控制房间相关（multiplayer-hoeing-assistant）
    private readonly ConcurrentDictionary<string, List<ControlRoomPlayer>> _controlRooms = new();
    // 控制房间 AllReady 广播幂等标志（独立于锄地房间 Room 对象，因为控制房间可能没有对应的锄地房间）
    private readonly ConcurrentDictionary<string, bool> _controlRoomAllReadyBroadcasted = new();
    // 遥控端连接登记（group → connectionId 集合）。遥控端不入 _controlRooms 成员列表，
    // 只加 SignalR Group 收广播；登记到此集合供 SendRemoteCommand 发送方校验放行。
    private readonly ConcurrentDictionary<string, HashSet<string>> _remoteControlConnections = new();
    // 离线命令缓存：playerUid → 待执行的命令列表
    private readonly ConcurrentDictionary<string, List<RemoteCommand>> _pendingCommands = new();

    public RoomManager(int maxRooms = 50, ILogger<RoomManager>? logger = null)
    {
        _maxRooms = maxRooms;
        _logger = logger;
    }

    /// <summary>创建房间，返回唯一6位字母数字房间码。同一 UID 只保留最新房间。</summary>
    public string CreateRoom(string hostConnectionId, string playerName = "", List<string>? whitelist = null, string playerUid = "", int expectedPlayerCount = 4, string reportedVersion = "")
    {
        if (_rooms.Count >= _maxRooms)
            throw new InvalidOperationException("服务器房间数已达上限");

        // 同一 UID 只保留最新房间，关闭旧房间
        if (!string.IsNullOrEmpty(playerUid))
        {
            var oldRoomCodes = _rooms
                .Where(kv => kv.Value.Players.Count > 0
                    && kv.Value.Players[0].PlayerUid == playerUid
                    && kv.Value.HostConnectionId != hostConnectionId)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var oldCode in oldRoomCodes)
            {
                if (_rooms.TryRemove(oldCode, out var oldRoom))
                {
                    lock (oldRoom)
                    {
                        foreach (var p in oldRoom.Players)
                            _connectionRoomMap.TryRemove(p.ConnectionId, out _);
                    }
                }
            }
        }

        string code;
        do
        {
            code = GenerateCode();
        } while (!_rooms.TryAdd(code, new Room
        {
            Code = code,
            HostConnectionId = hostConnectionId,
            CreatedAt = DateTime.UtcNow,
            Whitelist = whitelist ?? [],
            ExpectedPlayerCount = expectedPlayerCount,
            HostBaselineVersion = reportedVersion ?? "",
            Players =
            [
                new PlayerInfo
                {
                    ConnectionId = hostConnectionId,
                    PlayerId = hostConnectionId,
                    PlayerName = string.IsNullOrEmpty(playerName) ? "房主" : playerName,
                    PlayerUid = playerUid,
                    ReportedVersion = reportedVersion ?? "",
                    Status = PlayerStatus.Waiting,
                    LastHeartbeat = DateTime.UtcNow
                }
            ]
        }));

        _connectionRoomMap[hostConnectionId] = code;
        return code;
    }

    /// <summary>加入房间，验证房间存在且人数 &lt; 4</summary>
    public (bool Success, string? Error) JoinRoom(string roomCode, string connectionId, string playerId, string playerName = "", string playerUid = "", string reportedVersion = "")
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return (false, "房间不存在");

        lock (room)
        {
            // === 宽限期内重连复用（resilience-framework §2c）===
            // 检查是否是宽限期内重连（同 playerUid 或同 playerName）
            var graceMember = room.Players.FirstOrDefault(p =>
                room.GracePendingMembers.ContainsKey(p.ConnectionId) &&
                ((!string.IsNullOrEmpty(playerUid) && p.PlayerUid == playerUid) ||
                 (!string.IsNullOrEmpty(playerName) && p.PlayerName == playerName)));
            if (graceMember != null)
            {
                // 复用：更新 ConnectionId，清除宽限期标记
                var oldConnId = graceMember.ConnectionId;
                room.GracePendingMembers.Remove(oldConnId);
                // 更新 ArrivalSets / FightDoneSets / FightParticipantSets 中的 connectionId
                foreach (var set in room.ArrivalSets.Values)
                {
                    if (set.Remove(oldConnId))
                        set.Add(connectionId);
                }
                foreach (var set in room.FightDoneSets.Values)
                {
                    if (set.Remove(oldConnId))
                        set.Add(connectionId);
                }
                foreach (var set in room.FightParticipantSets.Values)
                {
                    if (set.Remove(oldConnId))
                        set.Add(connectionId);
                }
                graceMember.ConnectionId = connectionId;
                graceMember.LastHeartbeat = DateTime.UtcNow;
                graceMember.ReportedVersion = reportedVersion ?? "";
                _connectionRoomMap.TryRemove(oldConnId, out _);
                _connectionRoomMap[connectionId] = roomCode;
                _logger?.LogInformation("[JoinRoom] 成员 {Name} 宽限期内重连复用，房间 {Code}", playerName, roomCode);
                return (true, null);
            }

            // 白名单检查（按玩家名称），房主自己（同 UID 或同名）跳过检查
            var isHost = room.Players.Count > 0 &&
                ((!string.IsNullOrEmpty(playerUid) && room.Players[0].PlayerUid == playerUid) ||
                 (!string.IsNullOrEmpty(playerName) && room.Players[0].PlayerName == playerName));
            if (!isHost && room.Whitelist.Count > 0 && !room.Whitelist.Contains(playerName))
                return (false, "不在白名单中");

            // === 重连判定：现有玩家集合中存在同名 / 同 UID 即视为重连（spec lock-room-after-start §3.1）===
            // 重连场景必须绕过 IsStarted / ExpectedPlayerCount / MaxPlayers 三个限制，
            // 走下方"替换 ConnectionId"分支放行（bugfix §2.4 / §3.2）。
            var reconnectByName = !string.IsNullOrEmpty(playerName)
                ? room.Players.FirstOrDefault(p => p.PlayerName == playerName)
                : null;
            var reconnectByUid = !string.IsNullOrEmpty(playerUid)
                ? room.Players.FirstOrDefault(p => p.PlayerUid == playerUid)
                : null;
            var isReconnect = reconnectByName != null || reconnectByUid != null;

            // === IsStarted 锁定（spec lock-room-after-start §2.1）：开锄后拒绝非重连新玩家 ===
            if (!isReconnect && room.IsStarted)
                return (false, "房间已开锄");

            // === ExpectedPlayerCount 上限（spec lock-room-after-start §2.2）：人数已达房主声明的期望值时拒绝 ===
            if (!isReconnect && room.Players.Count >= room.ExpectedPlayerCount)
                return (false, $"房间已满（{room.ExpectedPlayerCount}人）");

            if (room.Players.Count >= MaxPlayers)
            {
                // Allow replacement if same playerName already exists
                var existing = room.Players.FirstOrDefault(p => p.PlayerName == playerName && !string.IsNullOrEmpty(playerName));
                if (existing == null)
                    return (false, "房间已满（最多4人）");

                // Replace old connection with new one
                _connectionRoomMap.TryRemove(existing.ConnectionId, out _);
                foreach (var set in room.ArrivalSets.Values)
                {
                    if (set.Remove(existing.ConnectionId))
                        set.Add(connectionId);
                }
                foreach (var set in room.FightDoneSets.Values)
                {
                    if (set.Remove(existing.ConnectionId))
                        set.Add(connectionId);
                }
                foreach (var set in room.FightParticipantSets.Values)
                {
                    if (set.Remove(existing.ConnectionId))
                        set.Add(connectionId);
                }
                // 如果被替换的是房主，同步更新 HostConnectionId
                if (room.HostConnectionId == existing.ConnectionId)
                    room.HostConnectionId = connectionId;
                room.Players.Remove(existing);
            }

            // Replace existing player with same name (reconnect scenario)
            var existingByName = room.Players.FirstOrDefault(p => p.PlayerName == playerName && !string.IsNullOrEmpty(playerName));
            if (existingByName != null)
            {
                _connectionRoomMap.TryRemove(existingByName.ConnectionId, out _);
                foreach (var set in room.ArrivalSets.Values)
                {
                    if (set.Remove(existingByName.ConnectionId))
                        set.Add(connectionId);
                }
                foreach (var set in room.FightDoneSets.Values)
                {
                    if (set.Remove(existingByName.ConnectionId))
                        set.Add(connectionId);
                }
                foreach (var set in room.FightParticipantSets.Values)
                {
                    if (set.Remove(existingByName.ConnectionId))
                        set.Add(connectionId);
                }
                // 如果被替换的是房主，同步更新 HostConnectionId
                if (room.HostConnectionId == existingByName.ConnectionId)
                    room.HostConnectionId = connectionId;
                room.Players.Remove(existingByName);
            }

            if (room.Players.Any(p => p.ConnectionId == connectionId))
                return (false, "已在房间中");

            room.Players.Add(new PlayerInfo
            {
                ConnectionId = connectionId,
                PlayerId = playerId,
                PlayerName = string.IsNullOrEmpty(playerName) ? $"玩家{room.Players.Count + 1}" : playerName,
                PlayerUid = playerUid,
                ReportedVersion = reportedVersion ?? "",
                Status = PlayerStatus.Waiting,
                LastHeartbeat = DateTime.UtcNow
            });
        }

        _connectionRoomMap[connectionId] = roomCode;
        return (true, null);
    }

    /// <summary>从所有房间移除该连接，返回受影响的房间码列表</summary>
    public List<string> LeaveRoom(string connectionId)
    {
        var affected = new List<string>();

        if (_connectionRoomMap.TryRemove(connectionId, out var roomCode))
        {
            if (_rooms.TryGetValue(roomCode, out var room))
            {
                lock (room)
                {
                    room.Players.RemoveAll(p => p.ConnectionId == connectionId);
                    // 清理该连接在各同步集合中的记录
                    foreach (var set in room.ArrivalSets.Values)
                        set.Remove(connectionId);
                    foreach (var set in room.FightDoneSets.Values)
                        set.Remove(connectionId);
                    foreach (var set in room.FightParticipantSets.Values)
                        set.Remove(connectionId);
                    room.WorldJoinedSet.Remove(connectionId);
                    room.RouteVerificationDoneSet.Remove(connectionId);

                    // 房间空了则删除
                    if (room.Players.Count == 0)
                        _rooms.TryRemove(roomCode, out _);
                }
                affected.Add(roomCode);
            }
        }

        return affected;
    }

    /// <summary>记录到达，当房间内所有在线成员均到达时返回 true</summary>
    public bool RecordArrival(string roomCode, string syncPointId, string connectionId)
    {
        return RecordArrival(roomCode, syncPointId, connectionId, 0);
    }

    /// <summary>
    /// 记录到达，当指定数量的玩家到达时返回 true
    /// </summary>
    /// <param name="roomCode">房间码</param>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="connectionId">连接ID</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    /// <returns>是否已达到预期人数</returns>
    public bool RecordArrival(string roomCode, string syncPointId, string connectionId, int expectedCount)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            if (!room.ArrivalSets.TryGetValue(syncPointId, out var arrivals))
            {
                arrivals = [];
                room.ArrivalSets[syncPointId] = arrivals;
            }

            arrivals.Add(connectionId);
            
            // 如果指定了预期人数，使用指定人数判断
            if (expectedCount > 0)
            {
                return arrivals.Count >= expectedCount;
            }
            
            // 否则使用原有的"所有在线成员"判断
            return AllOnlineMembersReported(room, arrivals);
        }
    }

    /// <summary>
    /// 清理指定同步点的到达集合（用于新一轮开始时清除旧数据）
    /// </summary>
    public void ClearArrivalSet(string roomCode, string syncPointId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return;

        lock (room)
        {
            room.ArrivalSets.Remove(syncPointId);
        }
    }

    /// <summary>记录战斗完成，当房间内所有在线成员均完成时返回 true</summary>
    public bool RecordFightDone(string roomCode, string syncPointId, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            // 周期复位（design §11.3）：与 RecordFightParticipant 对称，防止"投票先于参与者上报到达"
            // 时读到上一轮残留集合（网络乱序兜底，D2）。
            if (room.FightDoneBroadcasted.Contains(syncPointId))
            {
                room.FightParticipantSets.Remove(syncPointId);
                room.FightDoneSets.Remove(syncPointId);
                room.FightDoneBroadcasted.Remove(syncPointId);
            }

            if (!room.FightDoneSets.TryGetValue(syncPointId, out var doneSet))
            {
                doneSet = [];
                room.FightDoneSets[syncPointId] = doneSet;
            }

            doneSet.Add(connectionId);

            // multiplayer-shared-fight-end-quorum-sync: 开关关闭维持现状全员语义（零回归）
            if (!(room.HostConfig?.SharedFightEndQuorumEnabled ?? false))
                return AllOnlineMembersReported(room, doneSet);

            // 开关开启：配额 + 战斗参与者分母
            var participants = room.FightParticipantSets.TryGetValue(syncPointId, out var ps) && ps.Count > 0
                ? ps
                : doneSet;
            var reached = SharedFightEndQuorumDecisions.IsQuorumReached(
                doneSet.Count, participants.Count, room.HostConfig?.SharedFightEndQuorumRatio ?? 0.5);

            if (reached)
            {
                // 标记该 syncKey 已广播终态 → 下一拨参与者/投票触发周期复位（design §11.3）
                room.FightDoneBroadcasted.Add(syncPointId);
            }
            return reached;
        }
    }

    /// <summary>记录战斗参与者（按 syncKey 分组，配额分母用）。multiplayer-shared-fight-end-quorum-sync spec。</summary>
    public void RecordFightParticipant(string roomCode, string syncKey, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        lock (room)
        {
            // 周期复位（design §11.3）：若该 syncKey 上一轮已广播终态，首个新参与者触发清空，开启新一轮，
            // 消除上一拨战斗在同一战斗点的残留 connectionId/done 票（D2 污染修复）。
            if (room.FightDoneBroadcasted.Contains(syncKey))
            {
                room.FightParticipantSets.Remove(syncKey);
                room.FightDoneSets.Remove(syncKey);
                room.FightDoneBroadcasted.Remove(syncKey);
            }
            if (!room.FightParticipantSets.TryGetValue(syncKey, out var set))
            {
                set = [];
                room.FightParticipantSets[syncKey] = set;
            }
            set.Add(connectionId);
        }
    }

    /// <summary>记录路线验证完成，当房间内所有在线成员均完成时返回 true</summary>
    public bool RecordRouteVerificationDone(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            room.RouteVerificationDoneSet.Add(connectionId);
            
            // 清理已离线玩家的验证记录
            var onlineConnectionIds = room.Players
                .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                .Select(p => p.ConnectionId)
                .ToHashSet();
                
            room.RouteVerificationDoneSet.IntersectWith(onlineConnectionIds);
            
            return AllOnlineMembersReported(room, room.RouteVerificationDoneSet);
        }
    }

    /// <summary>
    /// 记录成员达经验上限，当所有在线成员均处于达上限态且本轮未广播过时返回 true。
    /// multiplayer-hoeing-exp-cap-stop + exp-cap-prefinal-stop-by-two-noexp（提前停止）。
    /// 广播条件：ExpCapArmed ∧ 全员 ∈ (ExpCapReachedSet ∪ TwoConsecutiveNoExpSet)。
    /// </summary>
    public bool RecordExpCapReached(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            if (room.ExpCapBroadcasted) return false; // 本轮已广播，幂等短路

            room.ExpCapReachedSet.Add(connectionId);

            // 在线清理（与 RecordRouteVerificationDone 对称）
            var onlineConnectionIds = room.Players
                .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                .Select(p => p.ConnectionId)
                .ToHashSet();
            room.ExpCapReachedSet.IntersectWith(onlineConnectionIds);
            room.TwoConsecutiveNoExpSet.IntersectWith(onlineConnectionIds);

            // 团队 arming 门控（multiplayer-hoeing-exp-cap-stop R7.4）：全员上报 且 团队已 arming 才广播。
            // 收紧不放宽（P11）：未 arming 时即便全员上报也不广播，防"重启空线路误停"。
            // exp-cap-prefinal-stop-by-two-noexp：提前停止——ExpCapReachedSet ∪ TwoConsecutiveNoExpSet 覆盖全员即可。
            if (room.ExpCapArmed && AllOnlineMembersReported(room, room.ExpCapReachedSet))
            {
                room.ExpCapBroadcasted = true;
                return true;
            }
            if (room.ExpCapArmed && IsAllOnlineMembersInEitherSet(room, room.ExpCapReachedSet, room.TwoConsecutiveNoExpSet))
            {
                room.ExpCapBroadcasted = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 记录团队 arming（任意成员吃到经验，或连续 5 场无经验兜底触发）。置 ExpCapArmed=true 并重查广播条件：
    /// 若此前已全员上报但因未 arming 未广播（全员满级 + 全部走兜底自点亮场景），此刻补触发广播，返回 true。
    /// 否则返回 false。multiplayer-hoeing-exp-cap-stop R7.3/R7.5 + exp-cap-prefinal-stop-by-two-noexp。
    /// </summary>
    public bool RecordExpArmed(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            if (room.ExpCapBroadcasted) return false; // 本轮已广播，幂等短路

            room.ExpCapArmed = true;

            // 在线清理（与 RecordExpCapReached 对称），再重查广播条件（解全员满级死锁）
            var onlineConnectionIds = room.Players
                .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                .Select(p => p.ConnectionId)
                .ToHashSet();
            room.ExpCapReachedSet.IntersectWith(onlineConnectionIds);
            room.TwoConsecutiveNoExpSet.IntersectWith(onlineConnectionIds);

            if (AllOnlineMembersReported(room, room.ExpCapReachedSet))
            {
                room.ExpCapBroadcasted = true;
                return true;
            }
            // exp-cap-prefinal-stop-by-two-noexp：arming 后重查提前停止条件
            if (IsAllOnlineMembersInEitherSet(room, room.ExpCapReachedSet, room.TwoConsecutiveNoExpSet))
            {
                room.ExpCapBroadcasted = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 撤回成员达上限（又检测到经验）。仅在本轮未广播时从集合移除。
    /// multiplayer-hoeing-exp-cap-stop + exp-cap-prefinal-stop-by-two-noexp（同时清理预警集合）。
    /// </summary>
    public void RecordExpCapCleared(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        lock (room)
        {
            if (room.ExpCapBroadcasted) return; // 已广播，撤回无意义
            room.ExpCapReachedSet.Remove(connectionId);
            room.TwoConsecutiveNoExpSet.Remove(connectionId);
        }
    }

    /// <summary>
    /// 记录"连续2场无经验预警"上报（exp-cap-prefinal-stop-by-two-noexp）。
    /// 幂等：重复上报直接加入集合。重查提前停止广播条件。
    /// </summary>
    public bool RecordTwoConsecutiveNoExp(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            if (room.ExpCapBroadcasted) return false;

            room.TwoConsecutiveNoExpSet.Add(connectionId);

            // 在线清理
            var onlineConnectionIds = room.Players
                .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                .Select(p => p.ConnectionId)
                .ToHashSet();
            room.TwoConsecutiveNoExpSet.IntersectWith(onlineConnectionIds);
            room.ExpCapReachedSet.IntersectWith(onlineConnectionIds);

            // exp-cap-prefinal-stop-by-two-noexp：arming ∧ 全员覆盖 → 广播
            if (room.ExpCapArmed && IsAllOnlineMembersInEitherSet(room, room.ExpCapReachedSet, room.TwoConsecutiveNoExpSet))
            {
                room.ExpCapBroadcasted = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 撤回"连续2场无经验预警"（又检测到经验）。exp-cap-prefinal-stop-by-two-noexp。
    /// </summary>
    public void RecordTwoConsecutiveNoExpCleared(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        lock (room)
        {
            if (room.ExpCapBroadcasted) return;
            room.TwoConsecutiveNoExpSet.Remove(connectionId);
        }
    }

    /// <summary>
    /// 判断所有在线成员是否至少属于 setA 或 setB 之一。
    /// exp-cap-prefinal-stop-by-two-noexp：提前停止的核心判断。
    /// 版本A（实时解锁）：2场无经验（setB）只有在团队已有人正式达4场上限（setA 非空）时才参与判定；
    /// setA 为空时 setB 视为空集，退化为"全员必须都在 setA"（全员连续4场无经验才算上限）。
    /// 这样避免"无人到4场、全员仅靠连续2场无经验就被提前停止"的误停。
    /// </summary>
    private static bool IsAllOnlineMembersInEitherSet(Room room, HashSet<string> setA, HashSet<string> setB)
    {
        var onlinePlayers = room.Players
            .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
            .ToList();

        if (onlinePlayers.Count == 0) return false;

        // 版本A门控：setA（已正式达上限/4场）非空才解锁 2场（setB）参与判定。
        var effectiveB = setA.Count > 0 ? setB : [];

        return onlinePlayers.All(p => setA.Contains(p.ConnectionId) || effectiveB.Contains(p.ConnectionId));
    }

    public (int OnlineCount, int ReportedCount) GetRouteVerificationStatus(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return (0, 0);

        lock (room)
        {
            var onlineCount = room.Players
                .Count(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2));
            var reportedCount = room.RouteVerificationDoneSet.Count;
            return (onlineCount, reportedCount);
        }
    }

    /// <summary>记录已加入世界，当所有在线成员均加入时返回 true</summary>
    public bool RecordWorldJoined(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            room.WorldJoinedSet.Add(connectionId);
            return AllOnlineMembersReported(room, room.WorldJoinedSet);
        }
    }

    public void ResetWorldJoinedSet(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return;
        lock (room) { room.WorldJoinedSet.Clear(); }
    }

    public int GetWorldJoinedCount(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return 0;
        lock (room)
        {
            return room.WorldJoinedSet.Count;
        }
    }

    /// <summary>更新房间白名单</summary>
    public void UpdateWhitelist(string roomCode, List<string> whitelist)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return;

        lock (room)
        {
            room.Whitelist = whitelist;
        }
    }

    /// <summary>获取所有未满的在线房间摘要</summary>
    public List<RoomSummary> GetOnlineRooms()
    {
        var result = new List<RoomSummary>();
        foreach (var (code, room) in _rooms)
        {
            lock (room)
            {
                // spec lock-room-after-start §3.2：物理上限 + 已开锄 + 人数已达期望值 三道过滤
                if (!room.IsStarted
                    && room.Players.Count < room.ExpectedPlayerCount
                    && room.Players.Count < MaxPlayers)
                {
                    result.Add(new RoomSummary
                    {
                        Code = code,
                        HostName = room.Players.Count > 0 ? room.Players[0].PlayerName : "",
                        HostUid = room.Players.Count > 0 ? room.Players[0].PlayerUid : "",
                        PlayerCount = room.Players.Count,
                        ExpectedPlayerCount = room.ExpectedPlayerCount,
                        MaxPlayers = MaxPlayers
                    });
                }
            }
        }
        return result;
    }

    public Room? GetRoom(string roomCode)
    {
        _rooms.TryGetValue(roomCode, out var room);
        return room;
    }

    /// <summary>删除整个房间及其所有玩家的映射（只删除仍在该房间的玩家映射）</summary>
    public void DeleteRoom(string roomCode)
    {
        if (!_rooms.TryRemove(roomCode, out var room))
            return;
        lock (room)
        {
            foreach (var p in room.Players)
            {
                // 只删除映射值仍指向该房间的条目，避免误删已加入新房间的玩家映射
                if (_connectionRoomMap.TryGetValue(p.ConnectionId, out var mappedRoom) && mappedRoom == roomCode)
                    _connectionRoomMap.TryRemove(p.ConnectionId, out _);
            }
        }
    }

    public (Room? Room, string? RoomCode) GetRoomByConnectionId(string connectionId)
    {
        if (_connectionRoomMap.TryGetValue(connectionId, out var roomCode)
            && _rooms.TryGetValue(roomCode, out var room))
            return (room, roomCode);

        return (null, null);
    }

    public void UpdateHeartbeat(string connectionId)
    {
        if (_connectionRoomMap.TryGetValue(connectionId, out var roomCode)
            && _rooms.TryGetValue(roomCode, out var room))
        {
            lock (room)
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player != null)
                    player.LastHeartbeat = DateTime.UtcNow;
            }
        }
    }

    /// <summary>带路线进度信息的心跳更新（需求 6）</summary>
    public void UpdateHeartbeatWithProgress(string connectionId, int routeIndex, DateTime routeStartTime, double routeEstimatedSeconds)
    {
        if (_connectionRoomMap.TryGetValue(connectionId, out var roomCode)
            && _rooms.TryGetValue(roomCode, out var room))
        {
            lock (room)
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player != null)
                {
                    player.LastHeartbeat = DateTime.UtcNow;
                    player.CurrentRouteIndex = routeIndex;
                    player.RouteStartTime = routeStartTime;
                    player.RouteEstimatedSeconds = routeEstimatedSeconds;
                }
            }
        }
    }

    /// <summary>
    /// 带完整状态的心跳更新（multiplayer-abnormal-wait-coordination 需求 1.2）
    /// 更新玩家心跳时间、路线索引、异常状态和等待点信息
    /// </summary>
    /// <param name="connectionId">连接ID</param>
    /// <param name="routeIndex">当前路线索引</param>
    /// <param name="isAbnormal">是否为异常玩家</param>
    /// <param name="waitPointId">当前等待点ID（异常玩家专用）</param>
    public void UpdateHeartbeatWithState(string connectionId, int routeIndex, bool isAbnormal, string? waitPointId)
    {
        if (_connectionRoomMap.TryGetValue(connectionId, out var roomCode)
            && _rooms.TryGetValue(roomCode, out var room))
        {
            lock (room)
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player != null)
                {
                    player.LastHeartbeat = DateTime.UtcNow;
                    player.CurrentRouteIndex = routeIndex;
                    player.IsAbnormal = isAbnormal;
                    player.WaitPointId = waitPointId;
                }
            }
        }
    }

    /// <summary>
    /// 记录等待点到达（multiplayer-abnormal-wait-coordination 需求 5.2）
    /// 当所有预期玩家都到达时返回 true
    /// </summary>
    /// <param name="roomCode">房间码</param>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="playerUid">玩家UID</param>
    /// <param name="isAbnormal">是否为异常玩家</param>
    /// <returns>是否全员已到达</returns>
    public bool RecordWaitPointArrival(string roomCode, string syncPointId, string playerUid, bool isAbnormal)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
        {
            _logger?.LogWarning("[RecordWaitPointArrival] 房间 {RoomCode} 不存在", roomCode);
            return false;
        }

        lock (room)
        {
            // 获取或创建到达记录
            if (!room.WaitPointArrivals.TryGetValue(syncPointId, out var arrivals))
            {
                arrivals = new HashSet<string>();
                room.WaitPointArrivals[syncPointId] = arrivals;
            }
            
            arrivals.Add(playerUid);
            
            // 检查是否全员到达
            var unifiedWaitPoint = room.CurrentUnifiedWaitPoint;
            if (unifiedWaitPoint == null || unifiedWaitPoint.SyncPointId != syncPointId)
            {
                _logger?.LogDebug("[RecordWaitPointArrival] 无匹配的统一等待点或等待点ID不匹配: {SyncPointId}", syncPointId);
                return false;
            }
            
            // 预期人数 = 服务端计算的人数
            int expectedCount = unifiedWaitPoint.ExpectedWaitCount;
            
            _logger?.LogInformation("[RecordWaitPointArrival] 等待点 {SyncPointId} 到达人数: {Arrived}/{Expected}",
                syncPointId, arrivals.Count, expectedCount);
            
            return arrivals.Count >= expectedCount;
        }
    }

    /// <summary>
    /// 获取等待点到达状态（multiplayer-abnormal-wait-coordination 需求 5.2）
    /// 返回已到达人数和预期人数
    /// </summary>
    /// <param name="roomCode">房间码</param>
    /// <param name="syncPointId">同步点ID</param>
    /// <returns>(已到达人数, 预期人数)</returns>
    public (int Arrived, int Expected) GetWaitPointArrivalStatus(string roomCode, string syncPointId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return (0, 0);

        lock (room)
        {
            int arrived = room.WaitPointArrivals.TryGetValue(syncPointId, out var arrivals) ? arrivals.Count : 0;
            int expected = room.CurrentUnifiedWaitPoint?.SyncPointId == syncPointId 
                ? room.CurrentUnifiedWaitPoint.ExpectedWaitCount 
                : 0;
            return (arrived, expected);
        }
    }

    /// <summary>
    /// 检查等待点是否全员到达（multiplayer-abnormal-wait-coordination 需求 5.2）
    /// </summary>
    /// <param name="roomCode">房间码</param>
    /// <param name="syncPointId">同步点ID</param>
    /// <returns>是否全员到达</returns>
    public bool CheckAllWaitPointArrived(string roomCode, string syncPointId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return false;

        lock (room)
        {
            var unifiedWaitPoint = room.CurrentUnifiedWaitPoint;
            if (unifiedWaitPoint == null || unifiedWaitPoint.SyncPointId != syncPointId)
                return false;

            int arrived = room.WaitPointArrivals.TryGetValue(syncPointId, out var arrivals) ? arrivals.Count : 0;
            return arrived >= unifiedWaitPoint.ExpectedWaitCount;
        }
    }

    /// <summary>
    /// 清除等待点到达记录（multiplayer-abnormal-wait-coordination 需求 5.4）
    /// 全员到达后调用，清理 WaitPointArrivals 防止后续轮次数据污染
    /// </summary>
    /// <param name="roomCode">房间码</param>
    public void ClearWaitPointArrivals(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            return;

        lock (room)
        {
            room.WaitPointArrivals.Clear();
            _logger?.LogDebug("[ClearWaitPointArrivals] 已清除房间 {RoomCode} 的等待点到达记录", roomCode);
        }
    }

    /// <summary>移除超时玩家，返回受影响的房间码列表</summary>
    public List<string> RemoveDeadPlayers(TimeSpan timeout)
    {
        var affected = new List<string>();
        var cutoff = DateTime.UtcNow - timeout;

        foreach (var (code, room) in _rooms)
        {
            List<string> deadConnections;
            lock (room)
            {
                deadConnections = room.Players
                    .Where(p => p.LastHeartbeat < cutoff)
                    .Select(p => p.ConnectionId)
                    .ToList();
            }

            foreach (var connId in deadConnections)
            {
                _connectionRoomMap.TryRemove(connId, out _);
            }

            lock (room)
            {
                var deadPlayers = room.Players
                    .Where(p => p.LastHeartbeat < cutoff)
                    .ToList();

                if (deadPlayers.Count > 0)
                {
                    _lastRemovedPlayers[code] = deadPlayers;
                }

                var removed = room.Players.RemoveAll(p => p.LastHeartbeat < cutoff);
                if (removed > 0)
                {
                    affected.Add(code);
                    if (room.Players.Count == 0)
                        _rooms.TryRemove(code, out _);
                }
            }
        }

        return affected;
    }

    public List<PlayerInfo> GetLastRemovedPlayers(string roomCode)
    {
        _lastRemovedPlayers.TryRemove(roomCode, out var removed);
        return removed ?? new List<PlayerInfo>();
    }

    private static bool AllOnlineMembersReported(Room room, HashSet<string> reported)
    {
        // 只检查最近有心跳的在线玩家
        var onlinePlayers = room.Players
            .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
            .ToList();
            
        if (onlinePlayers.Count == 0) return false;
        
        // 所有在线玩家都已上报：reported 集合应该覆盖所有在线玩家的 ConnectionId
        return onlinePlayers.All(p => reported.Contains(p.ConnectionId));
    }
    
    /// <summary>
    /// 添加玩家到房间并注册连接映射（仅用于测试）
    /// </summary>
    public void AddPlayerForTesting(string roomCode, PlayerInfo player)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        lock (room)
        {
            room.Players.Add(player);
            _connectionRoomMap[player.ConnectionId] = roomCode;
        }
    }
    
    /// <summary>
    /// 更新指定连接的心跳（仅用于测试，绕过连接映射验证）
    /// </summary>
    public void UpdateHeartbeatForConnectionId(string roomCode, string connectionId)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
            if (player != null)
                player.LastHeartbeat = DateTime.UtcNow;
        }
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (int i = 0; i < CodeLength; i++)
            chars[i] = CodeChars[Random.Shared.Next(CodeChars.Length)];
        return new string(chars);
    }

    /// <summary>
    /// 获取所有房间及其房间码（用于重对齐超时检查）
    /// </summary>
    public IEnumerable<(Room Room, string RoomCode)> GetAllRoomsWithCodes()
    {
        foreach (var (roomCode, room) in _rooms)
        {
            yield return (room, roomCode);
        }
    }

    // === 联机锄地异常同步机制方法（multiplayer-abnormal-sync-server spec）===
    // Validates: Requirements REQ-4, REQ-5, REQ-6

    /// <summary>
    /// 计算指定线路同步点的有效等待人数
    /// </summary>
    /// <param name="roomCode">房间码</param>
    /// <param name="currentRouteIndex">当前线路索引</param>
    /// <returns>有效等待人数</returns>
    public int GetEffectiveWaitCount(string roomCode, int currentRouteIndex)
    {
        var room = GetRoom(roomCode);
        if (room == null) return 0;

        lock (room)
        {
            // 在线玩家数（2分钟内有心跳）
            int onlineCount = room.Players.Count(p =>
                DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2));

            // 在其他线路等待的异常玩家数
            int abnormalInOtherRoutes = room.AbnormalPlayerInfos.Values
                .Count(a => a.TargetRouteIndex != currentRouteIndex);

            return Math.Max(1, onlineCount - abnormalInOtherRoutes);
        }
    }

    /// <summary>
    /// 获取房间的同步超时秒数
    /// </summary>
    /// <param name="roomCode">房间码</param>
    /// <returns>超时秒数（无异常=60，有异常=300）</returns>
    public int GetSyncTimeoutSeconds(string roomCode)
    {
        var room = GetRoom(roomCode);
        if (room == null) return 60;

        lock (room)
        {
            return room.AbnormalPlayerInfos.Count > 0 ? 300 : 60;
        }
    }

    /// <summary>
    /// 重置房间的异常状态（多轮世界新轮次开始时调用）
    /// </summary>
    /// <param name="roomCode">房间码</param>
    public void ResetAbnormalStates(string roomCode)
    {
        var room = GetRoom(roomCode);
        if (room == null) return;

        lock (room)
        {
            room.AbnormalPlayerInfos.Clear();
        }

        _logger?.LogInformation("[ResetAbnormalStates] 房间 {RoomCode} 异常状态已重置", roomCode);
    }

    // === 控制房间相关方法（multiplayer-hoeing-assistant） ===

    /// <summary>将玩家添加到控制房间。按 ConnectionId 匹配（同 UID 多连接各自独立条目），
    /// 找不到时按 (PlayerUid, ClientInstanceId) 查找旧条目更新 ConnectionId（断线重连场景）。</summary>
    public void AddToControlRoom(string group, string connectionId, string playerUid, string playerName, string clientInstanceId = "")
    {
        var players = _controlRooms.GetOrAdd(group, _ => []);
        lock (players)
        {
            var existing = players.Find(p => p.ConnectionId == connectionId);
            if (existing == null && !string.IsNullOrEmpty(clientInstanceId))
            {
                // 断线重连：按 (PlayerUid, ClientInstanceId) 查找旧条目，更新 ConnectionId
                existing = players.Find(p => p.PlayerUid == playerUid && p.ClientInstanceId == clientInstanceId);
                if (existing != null)
                {
                    existing.ConnectionId = connectionId;
                    existing.Online = true;
                    existing.PlayerName = playerName;
                }
            }
            if (existing != null)
            {
                existing.Online = true;
                existing.PlayerUid = playerUid;
                existing.PlayerName = playerName;
                existing.ClientInstanceId = clientInstanceId ?? "";
            }
            else
            {
                players.Add(new ControlRoomPlayer
                {
                    ConnectionId = connectionId,
                    PlayerUid = playerUid,
                    PlayerName = playerName,
                    ClientInstanceId = clientInstanceId ?? "",
                    Online = true,
                    BgiStatus = "unknown"
                });
            }
        }
    }

    /// <summary>登记/更新遥控端连接（group 下可能多个遥控端，Set 去重）。</summary>
    public void RegisterRemoteConnection(string group, string connectionId)
    {
        var set = _remoteControlConnections.GetOrAdd(group, _ => []);
        set.Add(connectionId);
    }

    /// <summary>移除遥控端连接（组内最后一个连接移除后清理空组）。</summary>
    public void RemoveRemoteConnection(string group, string connectionId)
    {
        if (_remoteControlConnections.TryGetValue(group, out var set))
        {
            set.Remove(connectionId);
            if (set.Count == 0)
                _remoteControlConnections.TryRemove(group, out _);
        }
    }

    /// <summary>该连接是否遥控端（已加入 CTRL_ group、不在 _controlRooms 成员列表，且已登记为遥控端）。</summary>
    public bool IsRemoteConnection(string group, string connectionId)
    {
        return _remoteControlConnections.TryGetValue(group, out var set) && set.Contains(connectionId);
    }

    /// <summary>获取控制房间的玩家列表</summary>
    public List<ControlRoomPlayer> GetControlRoomPlayers(string group)
    {
        if (_controlRooms.TryGetValue(group, out var players))
        {
            lock (players) { return [.. players]; }
        }
        return [];
    }

    /// <summary>检查玩家是否在控制房间中</summary>
    public bool IsInControlRoom(string group, string connectionId)
    {
        if (_controlRooms.TryGetValue(group, out var players))
        {
            lock (players) { return players.Any(p => p.ConnectionId == connectionId); }
        }
        return false;
    }

    /// <summary>解析远程命令的目标连接列表（"*"=全部在线，否则按 UID 匹配）</summary>
    public List<string> ResolveTargets(RemoteCommand command)
    {
        var group = $"CTRL_{command.RoomCode}";
        if (!_controlRooms.TryGetValue(group, out var players))
            return [];

        lock (players)
        {
            if (command.Target.Count == 1 && command.Target[0] == "*")
                return players.Where(p => p.Online).Select(p => p.ConnectionId).ToList();

            return players
                .Where(p => p.Online && command.Target.Contains(p.PlayerUid))
                .Select(p => p.ConnectionId)
                .ToList();
        }
    }

    /// <summary>从控制房间移除玩家（设为离线）</summary>
    public void RemoveFromControlRoom(string group, string connectionId)
    {
        if (_controlRooms.TryGetValue(group, out var players))
        {
            lock (players)
            {
                var player = players.Find(p => p.ConnectionId == connectionId);
                if (player != null)
                {
                    player.Online = false;
                    player.ConnectionId = string.Empty;
                    player.BgiStatus = "";
                    player.TaskRunning = false;
                    player.CurrentTaskName = null;
                    // 断线时重置上线事件代序号：BGI 重启后 generation 从 1 重新开始，
                    // 若保留旧值（如 2/3），新 generation(1) <= 旧值会被永久忽略，导致无法上线。
                    player.OnlineEventGeneration = 0;
                    player.OnlineEventConsumed = true;
                    player.OnlineEventTime = DateTime.MinValue;
                    player.OnlineReady = false;
                }
            }
        }
    }

    /// <summary>更新控制房间中玩家的状态</summary>
    public void UpdateControlStatus(string group, string connectionId, ControlStatus status)
    {
        if (_controlRooms.TryGetValue(group, out var players))
        {
            lock (players)
            {
                var player = players.Find(p => p.ConnectionId == connectionId);
                if (player != null)
                {
                    player.BgiStatus = status.BgiStatus;
                    player.ConfigGroups = status.ConfigGroups;
                    player.OneClickConfigs = status.OneClickConfigs;
                    player.ConfigGroupTasks = status.ConfigGroupTasks;
                    player.OneClickTasks = status.OneClickTasks;
                    player.ConfigGroupTasksWithStatus = status.ConfigGroupTasksWithStatus;
                    player.OneClickTasksWithStatus = status.OneClickTasksWithStatus;
                    player.Hotkeys = status.Hotkeys;
                    player.TaskRunning = status.TaskRunning;
                    player.CurrentTaskName = status.TaskRunning ? status.CurrentTaskName : null;
                    player.CurrentTaskGroupName = status.TaskRunning ? status.CurrentTaskGroupName : null;
                    player.CurrentRouteDisplay = status.CurrentRouteDisplay;
                    // 任务运行态带过期时间（超时自愈）；TaskRunning=false 时复位
                    if (status.TaskRunning)
                        player.TaskRunningExpireTime = DateTime.UtcNow.AddSeconds(TaskRunningTimeoutSec);
                    else
                        player.TaskRunningExpireTime = DateTime.MinValue;
                    player.AutoHoeingRunning = status.AutoHoeingRunning;
                    player.AutoHoeingProgress = status.AutoHoeingProgress;
                    player.OnlineReady = status.OnlineReady;
                    player.OnlineMode = status.OnlineMode;
                    player.ScheduledOnlineTime = status.ScheduledOnlineTime;
                    player.OnlineHoeingGroupNames = status.OnlineHoeingGroupNames;
                    player.QuickCommands = status.QuickCommands ?? new();
                    player.ExpectedHoeingPlayers = status.ExpectedHoeingPlayers;
                    if (status.OnlineReady)
                    {
                        // 设置上线状态过期时间（默认 30 分钟）
                        player.OnlineReadyExpireTime = DateTime.UtcNow.AddMinutes(30);
                    }
                    player.LastHeartbeat = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>缓存离线命令（目标 UID → 命令）</summary>
    public void CachePendingCommand(string targetUid, RemoteCommand command)
    {
        var list = _pendingCommands.GetOrAdd(targetUid, _ => []);
        lock (list) { list.Add(command); }
    }

    /// <summary>获取并清空某玩家的离线缓存命令</summary>
    public List<RemoteCommand> GetAndClearPendingCommands(string targetUid)
    {
        if (_pendingCommands.TryRemove(targetUid, out var list))
        {
            lock (list) { return [.. list]; }
        }
        return [];
    }

    /// <summary>检查目标 UID 是否在线</summary>
    public bool IsPlayerOnline(string group, string playerUid)
    {
        if (_controlRooms.TryGetValue(group, out var players))
        {
            lock (players) { return players.Any(p => p.PlayerUid == playerUid && p.Online); }
        }
        return false;
    }

    /// <summary>获取所有控制房间的 Group 名称列表</summary>
    public List<string> GetAllControlRoomGroups()
    {
        return [.. _controlRooms.Keys];
    }

    /// <summary>通过 connectionId 查找所属的控制房间 group 名。用于 ReportOnlineEvent 端点。</summary>
    public string? GetControlRoomGroup(string connectionId)
    {
        // 控制房间的 group 名是 "CTRL_xxx"，从 _controlRooms 的 key 反查
        // 通过 _controlRooms 中的玩家列表匹配 connectionId
        foreach (var (group, players) in _controlRooms)
        {
            lock (players)
            {
                if (players.Any(p => p.ConnectionId == connectionId))
                    return group;
            }
        }
        return null;
    }

    /// <summary>房间级状态（用于 ReportOnlineEvent 状态机）。</summary>
    public class RoomAllReadyState
    {
        /// <summary>当前状态：idle / waiting / ready / confirming / all_ready_confirmed / exhausted</summary>
        public string State { get; set; } = "idle";
        /// <summary>当前轮次的 generation</summary>
        public int CurrentGeneration { get; set; } = 0;
        /// <summary>上次消费的 generation</summary>
        public int LastConsumedGeneration { get; set; } = 0;
        /// <summary>确认阶段：已确认的成员 UID 集合</summary>
        public HashSet<string> ConfirmedUids { get; set; } = new();
        /// <summary>确认阶段：已尝试发送确认消息的次数</summary>
        public int ConfirmAttempts { get; set; } = 0;
        /// <summary>确认阶段：目标成员 UID 快照</summary>
        public List<string> PendingConfirmUids { get; set; } = new();
    }

    private readonly ConcurrentDictionary<string, RoomAllReadyState> _roomAllReadyStates = new();

    /// <summary>上报上线事件（带 generation 代序号）。由 ReportOnlineEvent 端点调用。</summary>
    public void ReportOnlineEvent(string group, string connectionId, int generation)
    {
        if (_controlRooms.TryGetValue(group, out var players))
        {
            lock (players)
            {
                var player = players.FirstOrDefault(p => p.ConnectionId == connectionId);
                if (player == null) return;
                if (generation <= player.OnlineEventGeneration) return;  // 旧事件，忽略

                // 更新玩家 generation
                player.OnlineEventGeneration = generation;
                player.OnlineEventConsumed = false;
                player.OnlineEventTime = DateTime.UtcNow;
                player.OnlineReady = true;  // 保留 UI 显示用
                player.OnlineReadyExpireTime = DateTime.UtcNow.AddMinutes(30);
            }

            // 新 generation 上报 → 重置房间状态机到 idle，使 CheckAndTransition 可以再次触发
            if (_roomAllReadyStates.TryGetValue(group, out var state))
            {
                state.State = "idle";
            }
        }
    }

    /// <summary>检查并转换状态机。返回 true 表示可通过 AllReady 广播，out generation 为当前轮次。</summary>
    public bool CheckAndTransition(string group, out int generation)
    {
        generation = 0;
        var state = _roomAllReadyStates.GetOrAdd(group, _ => new RoomAllReadyState());
        // 状态机已就绪或已消费 → 不再重复触发，等待新事件
        if (state.State != "idle" && state.State != "waiting")
            return false;

        var players = GetControlRoomPlayers(group);
        var onlinePlayers = players.Where(p => p.Online).ToList();
        if (onlinePlayers.Count == 0) return false;

        // 就绪成员 = 有新上线事件（未消费）的成员
        var readyPlayers = onlinePlayers.Where(p => !p.OnlineEventConsumed && p.OnlineEventGeneration > 0).ToList();
        // 预期开锄人数 = 所有在线成员上报的 ExpectedHoeingPlayers 的最小值（下限保底 1，防止默认 0）
        var threshold = onlinePlayers.Count > 0 ? onlinePlayers.Min(p => Math.Max(1, p.ExpectedHoeingPlayers)) : 1;
        Console.WriteLine("[探针服务端] CheckAndTransition: group=" + group + " onlinePlayers=" + onlinePlayers.Count + " ready=" + readyPlayers.Count + " threshold=" + threshold + " state=" + state.State);

        // 就绪人数未达预期 → 保持"已上线等待"，不广播 AllReady、不消费
        if (readyPlayers.Count < threshold)
        {
            // [S5 止血] 可观测性日志：说明在等谁（在线但未就绪的成员），不改任何行为/状态机
            var waitingPlayers = onlinePlayers.Except(readyPlayers).ToList();
            var waitingDesc = waitingPlayers.Count > 0
                ? string.Join(", ", waitingPlayers.Select(p => p.PlayerName + "(" + p.PlayerUid + ")"))
                : "（无在线未就绪成员）";
            _logger?.LogInformation("[联机锄地] 房间 {Group} 就绪人数不足：{Ready}/{Threshold}，等待成员：{Waiting}",
                group, readyPlayers.Count, threshold, waitingDesc);
            Console.WriteLine("[探针服务端] CheckAndTransition: 未达预期人数，等待 ready=" + readyPlayers.Count + "/" + threshold + " 等待成员=" + waitingDesc + ", group=" + group);
            return false;
        }

        // 找出就绪成员中当前轮次的最小 generation
        var minGen = readyPlayers.Min(p => p.OnlineEventGeneration);
        state.State = "ready";
        state.CurrentGeneration = minGen;
        generation = minGen;
        Console.WriteLine("[探针服务端] CheckAndTransition: 就绪，generation=" + minGen + " 广播 AllReady, group=" + group);
        return true;
    }

    /// <summary>消费上线状态（复位 OnlineReady + 记录历史 + 标记 generation 已消费）。在广播 AllReady 后调用。</summary>
    public void ConsumeOnlineReady(string group, int generation)
    {
        Console.WriteLine("[探针服务端] ===== ConsumeOnlineReady 被调用, group=" + group + " generation=" + generation + " =====");
        if (_controlRooms.TryGetValue(group, out var rawPlayers))
        {
            int consumed = 0;
            var now = DateTime.UtcNow;
            lock (rawPlayers)
            {
                foreach (var p in rawPlayers)
                {
                    if (p.OnlineEventGeneration == generation)
                    {
                        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
                        var dateStr = localNow.Hour < 4
                            ? localNow.AddDays(-1).ToString("yyyy-MM-dd")
                            : localNow.ToString("yyyy-MM-dd");
                        p.OnlineHistory.Add(new
                        {
                            mode = p.OnlineMode,
                            onlineTime = TimeZoneInfo.ConvertTimeFromUtc(p.LastHeartbeat, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")).ToString("HH:mm"),
                            consumeTime = localNow.ToString("HH:mm"),
                            date = dateStr,
                            timestamp = now
                        });
                        while (p.OnlineHistory.Count > 20)
                            p.OnlineHistory.RemoveAt(0);
                        // 标记已消费（边沿检测：允许下次新的 generation 再次触发）
                        p.OnlineEventConsumed = true;
                        p.OnlineReady = false;
                        p.OnlineMode = "none";
                        p.OnlineReadyExpireTime = DateTime.MinValue;
                        consumed++;
                    }
                }
            }
            Console.WriteLine("[探针服务端] ConsumeOnlineReady: 消费了 " + consumed + " 个成员, group=" + group);
            // 更新房间状态
            if (_roomAllReadyStates.TryGetValue(group, out var state))
            {
                state.State = "consumed";
                state.LastConsumedGeneration = generation;
            }
        }
        else
        {
            Console.WriteLine("[探针服务端] ConsumeOnlineReady: 找不到 group=" + group);
        }
        Console.WriteLine("[探针服务端] ===== ConsumeOnlineReady 结束, group=" + group + " =====");
    }

    /// <summary>清除指定玩家的 OnlineHistory（已联机记录）。由 ClearOnlineHistory Hub 端点调用。</summary>
    public void ClearOnlineHistory(string roomCode, string playerUid)
    {
        if (_controlRooms.TryGetValue(roomCode, out var players))
        {
            lock (players)
            {
                var player = players.FirstOrDefault(p => p.PlayerUid == playerUid);
                if (player != null)
                {
                    player.OnlineHistory.Clear();
                    Console.WriteLine("[探针服务端] ClearOnlineHistory: 已清除玩家 " + playerUid + " 的 OnlineHistory");
                }
            }
        }
    }

    public void BeginConfirming(string group, int generation, List<string> targetUids)
    {
        if (_roomAllReadyStates.TryGetValue(group, out var state))
        {
            state.State = "confirming";
            state.CurrentGeneration = generation;
            state.ConfirmedUids = new HashSet<string>();
            state.PendingConfirmUids = targetUids;
            state.ConfirmAttempts = 0;
        }
    }

    public bool RegisterConfirmAck(string group, string uid, int generation)
    {
        if (!_roomAllReadyStates.TryGetValue(group, out var state)) return false;
        if (state.State != "confirming") return false;
        if (generation != state.CurrentGeneration) return false;
        state.ConfirmedUids.Add(uid);
        return state.ConfirmedUids.Count >= state.PendingConfirmUids.Count;
    }

    public void MarkExhausted(string group)
    {
        if (_roomAllReadyStates.TryGetValue(group, out var state))
            state.State = "exhausted";
    }

    public bool IsStateConfirming(string group)
    {
        return _roomAllReadyStates.TryGetValue(group, out var state) && state.State == "confirming";
    }

    public List<string> GetPendingConfirmUids(string group, List<string> targetUids)
    {
        if (!_roomAllReadyStates.TryGetValue(group, out var state)) return targetUids;
        return targetUids.Where(uid => !state.ConfirmedUids.Contains(uid)).ToList();
    }

    public bool IsAllConfirmed(string group, List<string> targetUids)
    {
        if (!_roomAllReadyStates.TryGetValue(group, out var state)) return false;
        return state.ConfirmedUids.Count >= targetUids.Count;
    }

    public List<string> GetUnconfirmedUids(string group, List<string> targetUids)
    {
        if (!_roomAllReadyStates.TryGetValue(group, out var state)) return targetUids;
        return targetUids.Where(uid => !state.ConfirmedUids.Contains(uid)).ToList();
    }

    public List<string> GetConfirmedUids(string group)
    {
        if (_roomAllReadyStates.TryGetValue(group, out var state))
            return state.ConfirmedUids.ToList();
        return new List<string>();
    }

    public void IncrementConfirmAttempts(string group)
    {
        if (_roomAllReadyStates.TryGetValue(group, out var state))
            state.ConfirmAttempts++;
    }

    public string? GetConnectionIdByUid(string group, string uid)
    {
        if (_controlRooms.TryGetValue(group, out var players))
            lock (players) { return players.FirstOrDefault(p => p.PlayerUid == uid)?.ConnectionId; }
        return null;
    }

    public string? GetUidByConnectionId(string group, string connectionId)
    {
        if (_controlRooms.TryGetValue(group, out var players))
            lock (players) { return players.FirstOrDefault(p => p.ConnectionId == connectionId)?.PlayerUid; }
        return null;
    }
}
