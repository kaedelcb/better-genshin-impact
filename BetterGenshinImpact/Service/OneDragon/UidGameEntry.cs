using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>UID 读取前的登录准备：识别并点击登录界面后，等待主界面实际出现。</summary>
internal static class UidGameEntry
{
    internal enum Screen { MainUi, EntryHandled, OtherUi, FocusLost, LoadingOrUnknown }

    internal static async Task<string?> EnterIfNeededAsync(Func<Screen> handleScreen, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null, int retryTimes = 120)
    {
        delay ??= Task.Delay;
        ct.ThrowIfCancellationRequested();
        var screen = handleScreen();
        // 只有已确认的游戏内菜单才交给返回主界面流程；未知画面可能是冷启动加载，不能发送 Esc。
        if (screen is Screen.MainUi or Screen.OtherUi) return null;
        var entryHandled = screen == Screen.EntryHandled;

        for (var attempt = 0; attempt < retryTimes; attempt++)
        {
            if (screen == Screen.FocusLost)
                return "进入游戏时窗口失去焦点，未继续点击或读取 UID";

            // 按钮可能连续出现或首次点击未生效；每次操作后跨帧等待，加载中只识别、不按 Esc。
            await delay(TimeSpan.FromSeconds(1), ct);
            ct.ThrowIfCancellationRequested();
            screen = handleScreen();
            if (screen == Screen.MainUi || (!entryHandled && screen == Screen.OtherUi)) return null;
            entryHandled |= screen == Screen.EntryHandled;
        }

        return screen == Screen.FocusLost
            ? "进入游戏时窗口失去焦点，未继续点击或读取 UID"
            : "进入游戏等待主界面超时，未执行 UID 点击";
    }
}
