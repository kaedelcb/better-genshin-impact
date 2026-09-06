namespace MultiplayerHoeingAssistant.Models;

/// <summary>ControlRoomPlayersUpdated 事件的新 payload（全量/增量两形态）。
/// Full=true 时 Players 为完整成员列表（Changed/Removed 为 null）；
/// Full=false 时 Changed 为变化成员、Removed 为被移除成员 uid（Players 为 null）。
/// Revision 为服务端广播序号，客户端当前仅透传不使用。</summary>
public class ControlRoomPlayersUpdate
{
    public bool Full { get; set; }
    public long Revision { get; set; }
    public List<ControlRoomPlayer>? Players { get; set; }
    public List<ControlRoomPlayer>? Changed { get; set; }
    public List<string>? Removed { get; set; }
}
