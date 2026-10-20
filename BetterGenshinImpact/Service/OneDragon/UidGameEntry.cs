using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>UID 读取前的登录准备：识别并点击登录界面后，等待主界面实际出现。</summary>
internal static class UidGameEntry
{
    internal enum Screen { MainUi, EntryHandled, OtherUi, FocusLost }

    internal static async Task<string?> EnterIfNeededAsync(Func<Screen> handleScreen, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null, int retryTimes = 120)
    {
        delay ??= Task.Delay;
        ct.ThrowIfCancellationRequested();
        var screen = handleScreen();
        // 未识别到登录/领奖界面时，保留原有返回主界面流程，不额外点击或等待。
        if (screen is Screen.MainUi or Screen.OtherUi) return null;

        for (var attempt = 0; attempt < retryTimes; attempt++)
        {
            if (screen == Screen.FocusLost)
                return "进入游戏时窗口失去焦点，未继续点击或读取 UID";

            // 按钮可能连续出现或首次点击未生效；每次操作后跨帧等待，加载中只识别、不按 Esc。
            await delay(TimeSpan.FromSeconds(1), ct);
            ct.ThrowIfCancellationRequested();
            screen = handleScreen();
            if (screen == Screen.MainUi) return null;
        }

        return screen == Screen.FocusLost
            ? "进入游戏时窗口失去焦点，未继续点击或读取 UID"
            : "进入游戏等待主界面超时，未执行 UID 点击";
    }
}
