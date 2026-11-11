using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>网络恢复专用的关图确认；只发送一次关闭操作，容忍动画和截图延迟。</summary>
internal static class NetworkRecoveryMapProbe
{
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
