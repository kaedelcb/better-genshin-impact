namespace MultiplayerHoeingAssistant.Services;

public partial class CommandExecutor
{
    /// <summary>Only a proven exited original process can discharge its old local authority.</summary>
    public static bool? ProbeOriginalBgiProcess(BgiEpoch epoch)
        => epoch.ProcessId > 0 && epoch.StartTicksUtc > 0
            ? TaskCenterHost.ProbeOriginalBgiProcess(epoch.ProcessId, epoch.StartTicksUtc) : null;

    public async Task<bool> AbandonExitedCoordinatedOwnershipAsync(
        string? ticket, BgiEpoch epoch, CancellationToken ct)
    {
        if (ProbeOriginalBgiProcess(epoch) != false) return false;
        await _suspendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_resetStateGate)
            {
                // A later ticket belongs to a later operation. Never clear or send it.
                if (_takeoverTicket != ticket) return true;
                if (_takeoverTicket is null) return true;
                if (_takeoverEpoch is null || _takeoverEpoch.ProcessId != epoch.ProcessId
                    || _takeoverEpoch.StartTicksUtc != epoch.StartTicksUtc) return false;
                ClearTicket(ticket);
                return true;
            }
        }
        finally { _suspendGate.Release(); }
    }
}
