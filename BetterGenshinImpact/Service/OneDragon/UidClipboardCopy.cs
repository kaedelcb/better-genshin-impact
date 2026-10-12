using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>游戏菜单内的 UID 复制：鼠标先悬停，再点击，保持菜单直到剪贴板给出结果。</summary>
internal static class UidClipboardCopy
{
    internal sealed record Result(string Text, int ClicksSent, bool FocusLost);

    internal static async Task<Result> TryCopyAsync(Action moveToUid,
        Func<int, CancellationToken, Task> clickUid,
        Func<string> readClipboard, Func<bool> isGameFocused, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var click = 1; click <= 2; click++)
        {
            ct.ThrowIfCancellationRequested();
            if (!isGameFocused()) return new Result(string.Empty, click - 1, true);
            moveToUid();
            await delay(TimeSpan.FromMilliseconds(180), ct);
            if (!isGameFocused()) return new Result(string.Empty, click - 1, true);
            await clickUid(click, ct);

            // 游戏对移动、按下和复制的处理跨帧；复制结果出现前不按 Esc 关闭菜单。
            for (var poll = 0; poll < 8; poll++)
            {
                await delay(TimeSpan.FromMilliseconds(150), ct);
                var text = readClipboard();
                if (!string.IsNullOrWhiteSpace(text)) return new Result(text, click, false);
            }
        }
        return new Result(string.Empty, 2, false);
    }
}
