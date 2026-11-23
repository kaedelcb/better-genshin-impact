namespace BgiCoordinatorServer.Models;

public sealed record ControlRoomResult(bool Success, string Reason = "");
public sealed record ControlRoomCreateResult(bool Success, string RoomCode = "", string Reason = "");
public sealed record ControlRoomPlayersResult(bool Success, string Reason = "", ControlRoomPlayersUpdate? PlayersUpdate = null);
public sealed record ControlRoomLeaveResult(bool Success, bool Busy = false, string Reason = "");
public sealed record ControlRoomAccessResult(bool Success, bool Busy = false, bool Joined = false, string Reason = "");
