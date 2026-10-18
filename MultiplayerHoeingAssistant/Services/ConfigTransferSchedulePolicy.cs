using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Imported settings carry a schedule value, never a foreign execution authorization.</summary>
internal static class ConfigTransferSchedulePolicy
{
    internal static bool RequiresConfirmationAfterImport(AssistConfig? local, AssistConfig imported)
    {
        if (string.IsNullOrEmpty(imported.ScheduledOnlineTime)) return false;
        return local is null || local.ScheduledOnlineRequiresConfirmation
            || local.ScheduledOnlineTime != imported.ScheduledOnlineTime
            || local.ServerUrl != imported.ServerUrl || local.ControlRoomPassword != imported.ControlRoomPassword
            || local.PlayerUid != imported.PlayerUid || local.ObserverMode != imported.ObserverMode
            || local.StandaloneMode != imported.StandaloneMode
            || !local.TeamUids.OrderBy(uid => uid, StringComparer.Ordinal)
                .SequenceEqual(imported.TeamUids.OrderBy(uid => uid, StringComparer.Ordinal))
            || !local.OnlineHoeingGroupNames.SequenceEqual(imported.OnlineHoeingGroupNames, StringComparer.Ordinal)
            || !local.OnlineHoeingGroupTypes.SequenceEqual(imported.OnlineHoeingGroupTypes, StringComparer.Ordinal)
            || local.OnlineHoeingGroupIndex != imported.OnlineHoeingGroupIndex
            || local.ExpectedHoeingPlayers != imported.ExpectedHoeingPlayers
            || local.OnlineHoeingCompletionPolicy != imported.OnlineHoeingCompletionPolicy
            || local.OnlineHoeingSpecifiedTaskType != imported.OnlineHoeingSpecifiedTaskType
            || local.OnlineHoeingSpecifiedTaskName != imported.OnlineHoeingSpecifiedTaskName
            || local.OnlineHoeingSpecifiedTaskPriority != imported.OnlineHoeingSpecifiedTaskPriority;
    }
}
