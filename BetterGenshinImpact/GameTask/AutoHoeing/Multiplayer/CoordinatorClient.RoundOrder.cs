using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

public partial class CoordinatorClient
{
    public sealed record RoundOrderStartupSnapshot(string SessionId, string HostUid, string[] ParticipantUids, int RoundCount);

    internal static bool IsUsableRoundOrder(RoundOrderStartupSnapshot startup, IReadOnlyList<string> order,
        IEnumerable<PlayerInfo> players, string? uid, string hostUid, int roundLimit)
    {
        if (string.IsNullOrWhiteSpace(startup.SessionId) || string.IsNullOrWhiteSpace(uid)
            || string.IsNullOrWhiteSpace(hostUid) || startup.HostUid != hostUid
            || startup.ParticipantUids is not { Length: > 0 } roster || roster.Any(string.IsNullOrWhiteSpace)
            || order.Count != roster.Length || startup.RoundCount <= 0
            || startup.RoundCount != Math.Min(roundLimit, order.Count)) return false;

        var snapshot = players.Select(p => p.PlayerUid).ToArray();
        var expected = roster.ToHashSet(StringComparer.Ordinal);
        return expected.Count == roster.Length && expected.Contains(uid) && expected.Contains(hostUid)
            && expected.SetEquals(order) && expected.SetEquals(snapshot) && snapshot.Length == expected.Count;
    }

    internal async Task<List<string>> ConfirmWhitelistRoundOrderAsync(IReadOnlyList<PlayerInfo> players,
        string initialHostUid, int roundLimit, CancellationToken ct)
    {
        using var total = CreateRpcBudget(ct, TimeSpan.FromSeconds(30));
        await WaitForBusinessReadyAsync(total.Token);
        RoomSession session;
        long generation;
        lock (_roomLock)
        {
            session = _roomSession ?? throw new MultiplayerSessionFailedException("白名单轮换的开锄房间不存在");
            generation = session.TransportGeneration;
        }
        if (string.IsNullOrWhiteSpace(session.Uid))
            throw new MultiplayerSessionFailedException("白名单轮换需要每个玩家填写 UID");

        while (IsCurrentTransport(session, generation))
        {
            total.Token.ThrowIfCancellationRequested();
            var response = await _gateway!.QueryAsync(GatewayProtocol.Names.RoomGetRoundHostOrder,
                null, session.RoomCode, total.Token);
            if (!IsCurrentTransport(session, generation)) break;
            var startup = response.Get<RoundOrderStartupSnapshot>("startup");
            if (startup != null)
            {
                var order = response.Get<List<string>>("order") ?? [];
                if (!IsUsableRoundOrder(startup, order, players, session.Uid, initialHostUid, roundLimit))
                    throw new MultiplayerSessionFailedException("白名单轮换顺序与本场房主、成员或轮数不一致");
                // The server has one deadline shared by all confirmations. Allow its terminal answer
                // to arrive even if this client began querying well before the room was locked.
                using var confirmation = CreateRpcBudget(ct, TimeSpan.FromSeconds(32));
                var result = await _gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomConfirmRoundOrder,
                    new { sessionId = startup.SessionId, order, roundCount = startup.RoundCount }, session.RoomCode, confirmation.Token);
                ct.ThrowIfCancellationRequested();
                if (!IsCurrentTransport(session, generation) || !result.GetBool("ready"))
                    throw new MultiplayerSessionFailedException("全员未确认同一份白名单轮换顺序");
                return order;
            }
            await Task.Delay(500, total.Token);
        }
        throw new MultiplayerSessionFailedException("确认期间原联机房间连接已变化");
    }
}
