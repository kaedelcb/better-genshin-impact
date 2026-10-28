using System;
using System.Threading;
using BetterGenshinImpact.Core.Input;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>滚轮输入的诊断结果；Submitted 不表示游戏已响应。</summary>
internal enum MojangScrollOutcome
{
    BlockedBeforeChannel,
    BlockedBeforeSend,
    ZeroDelta,
    Submitted
}

/// <summary>模板拾取每次输入及等待都复核原运行会话，不持有跨会话输入通道。</summary>
internal sealed class MojangPickExecution(Func<bool> isAllowed, Func<IInputChannel> input, Action<int>? sleep = null,
    Action<int, int?, MojangScrollOutcome>? onScroll = null)
{
    private readonly Action<int> _sleep = sleep ?? Thread.Sleep;

    internal bool Wait(int milliseconds)
    {
        var remaining = Math.Clamp(milliseconds, 0, 5000);
        while (remaining > 0)
        {
            if (!isAllowed()) return false;
            var slice = Math.Min(remaining, 10);
            _sleep(slice);
            remaining -= slice;
        }
        return isAllowed();
    }

    internal bool Press(User32.VK key)
    {
        if (!isAllowed()) return false;
        var channel = input();
        if (!isAllowed()) return false;
        channel.Keyboard.KeyPress(key);
        return true;
    }

    internal bool ScrollDelta(int delta)
    {
        if (!isAllowed())
        {
            onScroll?.Invoke(delta, null, MojangScrollOutcome.BlockedBeforeChannel);
            return false;
        }
        // InputHub 接收格数，上游配置的单位是 Windows 原始 delta（120/格）。
        var clicks = (int)Math.Round(Math.Clamp(delta, -1200, 1200) / 120d, MidpointRounding.AwayFromZero);
        if (clicks == 0)
        {
            onScroll?.Invoke(delta, clicks, MojangScrollOutcome.ZeroDelta);
            return true;
        }
        var channel = input();
        if (!isAllowed())
        {
            onScroll?.Invoke(delta, clicks, MojangScrollOutcome.BlockedBeforeSend);
            return false;
        }
        channel.Mouse.VerticalScroll(clicks);
        // 仅表示输入后端调用已返回，不能证明游戏接收或物品选中项已变化。
        onScroll?.Invoke(delta, clicks, MojangScrollOutcome.Submitted);
        return true;
    }
}
