using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 同 UID 离线幽灵条目清理回归测试。
/// 背景：设置弹窗 BuildConfig 曾漏复制 ClientInstanceId，换房间保存设置后实例标识被清空，
/// 重新入房时 AddToControlRoom 无法按 (UID, ClientInstanceId) 回收旧条目 → 追加新条目、
/// 旧条目残留为离线幽灵。广播（全量+增量快照均按 UID 键控）中幽灵覆盖在线状态，
/// 客户端表现为"假离线"（显示离线、弹窗提示"该成员BGI未连接"，但 ResolveTargets 匹配
/// 任一在线条目，命令照常送达）。本测试锁定服务端清理语义：
/// 1) Fix：实例标识为空/变更的重连追加新条目前，清除同 UID 离线幽灵；
/// 2) Fix：PruneOfflineControlRoomGhosts 清除"有在线条目"的 UID 的离线重复（存量自愈）；
/// 3) Preservation：同 UID 双开双在线条目不受影响；无在线条目的全离线成员条目保留
///    （离线成员缓存配置可见的设计不变）。
/// </summary>
public class GhostEntryPruneTests
{
    private static string Group() => $"CTRL_G{Guid.NewGuid():N}"[..14];

    private static string Conn(string tag) => $"conn-{tag}-{Guid.NewGuid():N}";

    [Fact]
    public void RejoinWithWipedInstanceId_RemovesOfflineGhost()
    {
        var rm = new RoomManager();
        var group = Group();
        var conn1 = Conn("old");

        // 旧连接入房（带实例标识），随后断线 → 残留离线幽灵
        rm.AddToControlRoom(group, conn1, "uid1", "玩家1", "inst-old");
        rm.RemoveFromControlRoom(group, conn1);

        // ClientInstanceId 被清空后重新入房：实例匹配失败走追加分支，应剪除幽灵
        rm.AddToControlRoom(group, Conn("new"), "uid1", "玩家1", "");

        var players = rm.GetControlRoomPlayers(group);
        var entries = players.Where(p => p.PlayerUid == "uid1").ToList();
        Assert.Single(entries);
        Assert.True(entries[0].Online);
    }

    [Fact]
    public void Prune_RemovesOfflineDuplicate_WhenSameUidHasOnlineEntry()
    {
        var rm = new RoomManager();
        var group = Group();
        var connLive = Conn("live");
        var connGhost = Conn("ghost");

        // 构造"幽灵在活条目之后"的存量现场：活条目先入房，第二个实例入房后断线
        rm.AddToControlRoom(group, connLive, "uid1", "玩家1", "inst-live");
        rm.AddToControlRoom(group, connGhost, "uid1", "玩家1", "inst-ghost");
        rm.RemoveFromControlRoom(group, connGhost);
        Assert.Equal(2, rm.GetControlRoomPlayers(group).Count(p => p.PlayerUid == "uid1"));

        rm.PruneOfflineControlRoomGhosts(group);

        var entries = rm.GetControlRoomPlayers(group).Where(p => p.PlayerUid == "uid1").ToList();
        Assert.Single(entries);
        Assert.True(entries[0].Online);
        Assert.Equal(connLive, entries[0].ConnectionId);
    }

    [Fact]
    public void Prune_KeepsDualOnlineEntries_SameUidDualBoxing()
    {
        var rm = new RoomManager();
        var group = Group();

        // 同 UID 双开：两个在线实例都必须保留（ResolveTargets 需要各自送达）
        rm.AddToControlRoom(group, Conn("a"), "uid1", "玩家1", "inst-a");
        rm.AddToControlRoom(group, Conn("b"), "uid1", "玩家1", "inst-b");

        rm.PruneOfflineControlRoomGhosts(group);

        Assert.Equal(2, rm.GetControlRoomPlayers(group).Count(p => p.PlayerUid == "uid1" && p.Online));
    }

    [Fact]
    public void Prune_KeepsOfflineEntry_WhenNoOnlineEntryForThatUid()
    {
        var rm = new RoomManager();
        var group = Group();
        var connOffline = Conn("off");

        // uid1 全离线（离线成员缓存配置保留的设计不变），uid2 在线
        rm.AddToControlRoom(group, connOffline, "uid1", "玩家1", "inst-off");
        rm.AddToControlRoom(group, Conn("on"), "uid2", "玩家2", "inst-on");
        rm.RemoveFromControlRoom(group, connOffline);

        rm.PruneOfflineControlRoomGhosts(group);

        var players = rm.GetControlRoomPlayers(group);
        Assert.Single(players.Where(p => p.PlayerUid == "uid1" && !p.Online));
        Assert.Single(players.Where(p => p.PlayerUid == "uid2" && p.Online));
    }
}
