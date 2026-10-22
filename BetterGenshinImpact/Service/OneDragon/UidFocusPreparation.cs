using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>账号前置首次操作游戏前的焦点准备；不发送游戏键鼠，等待有界且可取消。</summary>
internal static class UidFocusPreparation
{
    internal sealed record Result(bool Focused, int ActivationAttempts);

    internal static async Task<Result> WaitAsync(Action activateGame, Func<bool> isGameFocused,
        CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null, int pollTimes = 40)
    {
        delay ??= Task.Delay;
        var activations = 0;
        for (var poll = 0; poll < pollTimes; poll++)
        {
            ct.ThrowIfCancellationRequested();
            if (isGameFocused()) return new Result(true, activations);
            // 启动界面和遮罩可能尚在切换；每秒尝试激活，其余时间只观察目标窗口焦点。
            if (poll % 4 == 0)
            {
                ct.ThrowIfCancellationRequested();
                activateGame();
                activations++;
            }
            await delay(TimeSpan.FromMilliseconds(250), ct);
        }

        ct.ThrowIfCancellationRequested();
        return new Result(isGameFocused(), activations);
    }
}
