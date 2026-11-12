using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common;

public partial class TaskControl
{
    private sealed class TeleportRecoveryStep(string? mapName, long version)
    {
        public string? MapName { get; } = mapName;
        public long Version { get; } = version;
        public int Paused;
    }

    private static readonly AsyncLocal<TeleportRecoveryStep?> CurrentTeleportStep = new();
    private static TeleportRecoveryStep? _activeTeleportStep;

    // 中止的是被恢复操作作废的这一次传送，不是用户任务。普通重试器不得吞掉它后继续旧动作。
    private sealed class TeleportStepInterruptedException()
        : OperationCanceledException("环境恢复已改变传送现场，需要重新确认地图和目标");

    internal static void ThrowIfTeleportStepInterrupted(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_inNetworkRecovery.Value) return;
        var step = CurrentTeleportStep.Value;
        if (step != null && step.Version != NetworkRecoveryVersion)
            throw new TeleportStepInterruptedException();
    }

    private static void SetTeleportStepPaused(bool paused)
    {
        if (CurrentTeleportStep.Value is { } step)
            Volatile.Write(ref step.Paused, paused ? 1 : 0);
    }

    /// <summary>
    /// 一次完整的地图操作。恢复后重做整个步骤，不从半次拖动/点击继续，也不消耗外层传送重试次数。
    /// 嵌套开图共用外层步骤；只有最外层捕获中断，保证旧坐标不会在内层恢复后继续使用。
    /// </summary>
    internal static async Task<T> RunTeleportStepAsync<T>(
        Func<Task<T>> operation, string? mapName, CancellationToken ct)
    {
        if (CurrentTeleportStep.Value != null)
        {
            var nestedResult = await operation();
            ThrowIfTeleportStepInterrupted(ct);
            return nestedResult;
        }

        using var environmentOperation = BeginEnvironmentOperation();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var step = new TeleportRecoveryStep(mapName, NetworkRecoveryVersion);
            if (Interlocked.CompareExchange(ref _activeTeleportStep, step, null) != null)
                throw new InvalidOperationException("已有传送步骤正在使用游戏输入，不能并发执行另一传送");
            CurrentTeleportStep.Value = step;
            try
            {
                var result = await operation();
                ThrowIfTeleportStepInterrupted(ct);
                return result;
            }
            catch (TeleportStepInterruptedException) when (!ct.IsCancellationRequested)
            {
                InputHub.ReleaseAll();
                Logger.LogWarning("传送经过环境恢复，丢弃旧坐标和未完成操作，重新执行当前步骤（不计重试次数）");
            }
            catch (MapUiUnavailableException) when (!ct.IsCancellationRequested)
            {
                await WaitForEnvironmentRecoveryAsync(
                    token => new TpTaskFastDrag(token).ProbeMapForRecoveryAsync(mapName), ct);
                // 已确认的地图留给下一次完整步骤；不再执行中断前余下的缩放或点击。
            }
            finally
            {
                CurrentTeleportStep.Value = null;
                Interlocked.CompareExchange(ref _activeTeleportStep, null, step);
            }
        }
    }
}
