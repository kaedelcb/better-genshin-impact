using MultiplayerHoeingAssistant.Dto;

namespace MultiplayerHoeingAssistant.Services.NewArchitecture;

/// <summary>
/// 维护从服务器获取的控制房间状态（成员列表、期望状态）。
/// 不保存定时器状态，定时上线完全由服务器驱动。
/// </summary>
public class ControlRoomStateService
{
    private readonly Dictionary<string, MemberDto> _members = new();

    public IReadOnlyDictionary<string, MemberDto> Members => _members;

    public MemberDto? GetMember(string playerUid)
    {
        _members.TryGetValue(playerUid, out var member);
        return member;
    }

    public void UpdateMembers(List<MemberDto> members)
    {
        _members.Clear();
        foreach (var member in members)
        {
            if (!string.IsNullOrEmpty(member.PlayerUid))
                _members[member.PlayerUid] = member;
        }
    }

    public void UpdateDesiredState(MemberDesiredStateDto state)
    {
        if (string.IsNullOrEmpty(state.PlayerUid)) return;
        if (!_members.TryGetValue(state.PlayerUid, out var member)) return;

        if (state.ScheduledOnlineTime != null)
            member.ScheduledOnlineTime = state.ScheduledOnlineTime;
        if (state.OnlineHoeingGroupNames != null)
            member.OnlineHoeingGroupNames = state.OnlineHoeingGroupNames;
        if (state.OnlineHoeingGroupTypes != null)
            member.OnlineHoeingGroupTypes = state.OnlineHoeingGroupTypes;
        if (state.ExpectedHoeingPlayers.HasValue)
            member.ExpectedHoeingPlayers = state.ExpectedHoeingPlayers.Value;
        if (state.QuickCommands != null)
            member.QuickCommands = state.QuickCommands;
    }
}
