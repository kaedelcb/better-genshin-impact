namespace BgiCoordinatorServer.Models;

/// <summary>
/// ControlRoomPlayersUpdated 的统一 payload（带宽优化：全量/增量两态）。
/// Full=true 时填 Players（全量列表）；Full=false 时填 Changed（变更成员）+ Removed（离房 uid 列表）。
/// Revision 为服务端单调递增的广播序号（从 1 开始），供客户端丢包/乱序自检；
/// 无变化时服务端不发送，Revision 允许跳号（客户端不得假设连续）。
/// JSON 走 SignalR 默认 camelCase（full/revision/players/changed/removed）。
/// </summary>
public class ControlRoomPlayersUpdate
{
    /// <summary>是否全量快照。true=Players 为完整列表；false=Changed/Removed 为相对上次广播的增量。</summary>
    public bool Full { get; set; }
    /// <summary>广播序号（per-group 单调递增，从 1 开始；无变化不发送故允许跳号）。</summary>
    public long Revision { get; set; }
    /// <summary>Full=true 时填：当前成员全量列表。</summary>
    public List<ControlRoomPlayer>? Players { get; set; }
    /// <summary>Full=false 时填：相对上次广播有字段变化（或新增）的成员。</summary>
    public List<ControlRoomPlayer>? Changed { get; set; }
    /// <summary>Full=false 时填：相对上次广播已不在成员列表中的 uid。</summary>
    public List<string>? Removed { get; set; }
}
