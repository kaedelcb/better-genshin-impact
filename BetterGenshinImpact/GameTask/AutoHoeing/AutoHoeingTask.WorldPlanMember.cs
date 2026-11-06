using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.GameTask.AutoHoeing;

public partial class AutoHoeingTask
{
    private async Task<WorldPlanAdmission?> NegotiateMemberWorldPlanAsync(
        CoordinatorClient client, CancellationTokenSource budget)
    {
        var ct = budget.Token;
        var readyConfigFailures = 0;
        var hostConfigApplied = false;
        async Task ApplyConfigAsync(RoomConfig config)
        {
            RequireMatchingRoomBatch(config);
            if (hostConfigApplied) return;
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, config.PartyTimeoutSeconds)));
            var needsUpdate = config.AutoUpdateRoutes && !_config.AutoUpdateRoutes;
            ApplyWorldHostConfig(config);
            if (needsUpdate) await RunAutoUpdateRoutesAsync(operationToken: ct);
            ct.ThrowIfCancellationRequested();
            _firstHostConfig = config;
            hostConfigApplied = true;
        }
        async Task<WorldPlanAdmission> AcceptAsync(WorldPlanAdmission admission)
        {
            if (admission.Snapshot.Stage != "Legacy")
                await ApplyConfigAsync(admission.Snapshot.HostConfig
                    ?? throw new InvalidOperationException("开队预判缺少本次房主配置"));
            return admission;
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // A stated single-world/disabled setting must not depend on the new plan RPC.
            // Still verify fixed-batch identity before returning to its original admission path.
            var hostConfig = await client.GetRoomConfigAsync(ct);
            if (hostConfig != null)
            {
                RequireMatchingRoomBatch(hostConfig);
                if (!hostConfig.MultiWorldEnabled || !hostConfig.MultiWorldResumeEnabled)
                {
                    if (hostConfigApplied)
                        throw new InvalidOperationException("本次已声明的多世界预判配置发生变化");
                    return null;
                }
                if (!hostConfigApplied)
                {
                    await ApplyConfigAsync(hostConfig);
                }
            }

            // A first-world-A plan can already have published HostReady. If config is unknown
            // or enabled, its ticket wins over readiness and this member's local defaults.
            var admission = await client.AcquireWorldPlanAsync(ct);
            if (admission != null) return await AcceptAsync(admission);

            if (await client.IsHostReadyAsync(ct))
            {
                // HostReady can change after the first acquire. Read the
                // plan again after that fact; never infer "no plan" from a stale first read.
                admission = await client.AcquireWorldPlanAsync(ct);
                if (admission != null) return await AcceptAsync(admission);
                if (hostConfig != null)
                    throw new InvalidOperationException("房主已就绪但未提供本次多世界预判身份");
                if (++readyConfigFailures >= 3)
                {
                    if (hostConfigApplied || _config.MultiWorldEnabled || CoordinatedHoeingScope.IsActive)
                        throw new InvalidOperationException("无法获取多世界或固定批次房主配置");
                    // Preserve the ordinary single-world contract: its existing startup path
                    // retries config three times and may then use local settings. It still
                    // performs normal host/world readiness checks before entering the game.
                    return null;
                }
            }
            await Task.Delay(250, ct);
        }
    }
}
