using System;
using System.Threading;
using BetterGenshinImpact.Core.Input;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>模板拾取每次输入及等待都复核原运行会话，不持有跨会话输入通道。</summary>
internal sealed class MojangPickExecution(Func<bool> isAllowed, Func<IInputChannel> input, Action<int>? sleep = null)
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
        if (!isAllowed()) return false;
        // InputHub 接收格数，上游配置的单位是 Windows 原始 delta（120/格）。
        var clicks = (int)Math.Round(Math.Clamp(delta, -1200, 1200) / 120d, MidpointRounding.AwayFromZero);
        if (clicks == 0) return true;
        var channel = input();
        if (!isAllowed()) return false;
        channel.Mouse.VerticalScroll(clicks);
        return true;
    }
}
