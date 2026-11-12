using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>网络恢复专用的关图确认；只发送一次关闭操作，容忍动画和截图延迟。</summary>
internal static class NetworkRecoveryMapProbe
{
    /// <summary>保留地图时，以一次小幅缩放的实际响应验证恢复，不能把冻结的地图截图当作可操作。</summary>
    internal static async Task<bool> VerifyScaleResponseAsync(Func<double?> readScale,
        Func<double, CancellationToken, Task<bool>> tryChangeScale,
        Func<int, CancellationToken, Task> delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var initial = readScale();
        ct.ThrowIfCancellationRequested();
        if (initial is not double before || !double.IsFinite(before) || before < 0 || before > 1)
            return false;
        if (!await tryChangeScale(before, ct)) return false;
        var respondingFrames = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await delay(150, ct);
            ct.ThrowIfCancellationRequested();
            var current = readScale();
            ct.ThrowIfCancellationRequested();
            respondingFrames = current is double value && double.IsFinite(value) && value >= 0 && value <= 1
                && Math.Abs(value - before) > 0.01 ? respondingFrames + 1 : 0;
            if (respondingFrames >= 2) return true;
        }
        return false;
    }

    internal static async Task<bool> CloseAsync(Func<bool> isInMainUi, Func<bool> tryCloseMap,
        Func<int, CancellationToken, Task> delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 开图探测返回后，用户或迟到的游戏响应可能已经关图，不能再按 Esc 打开菜单。
        var alreadyInMainUi = isInMainUi();
        ct.ThrowIfCancellationRequested();
        if (!alreadyInMainUi && !tryCloseMap()) return false;
        await delay(500, ct);
        var consecutiveMainUiFrames = 0;
        for (var attempt = 0; attempt < 26; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var mainUi = isInMainUi();
            ct.ThrowIfCancellationRequested();
            consecutiveMainUiFrames = mainUi ? consecutiveMainUiFrames + 1 : 0;
            if (consecutiveMainUiFrames >= 2) return true;
            if (attempt < 25) await delay(100, ct);
        }
        return false;
    }
}
