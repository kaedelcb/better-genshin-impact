using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

internal static class PickupCombatScriptExecutor
{
    private const int InitialPickupWaitMs = 250;

    private static bool IsPickupPress(CombatCommand command) => command.Method == Method.KeyPress
        && command.Args is { Count: 1 } args && User32Helper.ToVk(args[0]) == User32.VK.VK_F;

    internal static Task<bool> ExecuteAsync(CombatScript script, CombatScenes scenes, CancellationToken routeToken,
        CancellationToken reachedToken, ILogger logger)
    {
        if (!CombatScriptExecutor.HasRequiredAvatar(script, scenes, logger))
        {
            // 保持既有语义：缺角色只记日志，不视作指令报错。
            return Task.FromResult(true);
        }

        return RunWithOwnedInputsAsync(InputHub.Foreground, routeToken, reachedToken, logger, async (held, stop) =>
        {
            var commands = script.CombatCommands;
            var leadingPickupCount = 0;
            while (leadingPickupCount < commands.Count && IsPickupPress(commands[leadingPickupCount]))
                leadingPickupCount++;

            for (var i = 0; i < commands.Count; i++)
            {
                stop.ThrowIfCancellationRequested();
                if (i != 0 && i == leadingPickupCount)
                {
                    // 先给原有 F 的结果一点时间；过程中拾取达标仍可立即打断，不追加拾取输入。
                    await WaitAsync(InitialPickupWaitMs, stop);
                }

                var command = commands[i];
                try
                {
                    var avatar = command.ResolveAvatar(scenes, commands[i == 0 ? 0 : i - 1], stop);
                    stop.ThrowIfCancellationRequested();
                    if (avatar != null)
                        await ExecuteCommandAsync(command, avatar, held, stop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !stop.IsCancellationRequested)
                {
                    logger.LogError(ex, "【拾取达标】第 {Index} 条指令「{Name} {Method}({Args})」执行失败",
                        i + 1, command.Name, command.Method.Alias[0], string.Join(",", command.Args ?? []));
                    throw;
                }
            }
        });
    }

    internal static async Task ExecuteCommandAsync(CombatCommand command, Avatar avatar, HeldInputs held, CancellationToken stop)
    {
        var method = command.Method;
        if (method == Method.Wait)
        {
            await WaitAsync(DurationMs(command), stop);
        }
        else if (method == Method.Attack)
        {
            avatar.AttackForPickup(DurationMs(command), stop);
        }
        else if (method == Method.Skill && command.Args is { Count: 0 }
            && command.Name != CombatScriptParser.CurrentAvatarName
            && command.Name is not ("阿蕾奇诺" or "玛薇卡"))
        {
            avatar.TapSkillForPickup(stop);
        }
        else if (method == Method.KeyDown || method == Method.KeyUp)
        {
            var text = command.Args![0];
            var key = KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(text));
            // 对显式侧键沿用旧 CombatCommand 的输入含义。
            if (text == "VK_XBUTTON2") key = User32.VK.VK_XBUTTON1;
            stop.ThrowIfCancellationRequested();
            if (method == Method.KeyDown)
                held.Press(key);
            else
            {
                held.Release(key);
                CombatCommand.TryTriggerESkillCdCheck(avatar, text);
            }
        }
        else if (method == Method.MouseDown || method == Method.MouseUp)
        {
            var name = command.Args is { Count: > 0 } ? command.Args[0].ToLower() : "left";
            var key = name switch
            {
                "left" => User32.VK.VK_LBUTTON,
                "right" => User32.VK.VK_RBUTTON,
                "middle" => User32.VK.VK_MBUTTON,
                _ => (User32.VK?)null
            };
            stop.ThrowIfCancellationRequested();
            if (key is { } button)
            {
                if (method == Method.MouseDown) held.Press(button);
                else held.Release(button);
            }
        }
        else
        {
            // F、X、移动视角、单次鼠标点击沿用原输入处理。
            stop.ThrowIfCancellationRequested();
            command.Execute(avatar);
        }
    }

    private static int DurationMs(CombatCommand command) => command.Args is { Count: > 0 } args
        ? (int)TimeSpan.FromSeconds(CombatCommand.ParseSeconds(args[0])).TotalMilliseconds
        : 0;

    private static async Task WaitAsync(int ms, CancellationToken stop)
    {
        Avatar.PreparePickupCommandWait(stop);
        await Task.Delay(ms, stop);
        stop.ThrowIfCancellationRequested();
    }

    internal static async Task<bool> RunWithOwnedInputsAsync(IInputChannel channel, CancellationToken routeToken,
        CancellationToken reachedToken, ILogger logger, Func<HeldInputs, CancellationToken, Task> run)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(routeToken, reachedToken);
        var completed = false;
        try
        {
            // 所有既有指令都在同一输入作用域内执行，包括原先未列入白名单的长按、行走和角色特化动作。
            using (InputHub.BeginOperation(linked.Token, channel))
            {
                var held = new HeldInputs(InputHub.Foreground);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    await run(held, linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    completed = true;
                }
                catch (Exception ex) when (!held.ReleaseFailed && ex is not OperationCanceledException)
                {
                    if (CombatScriptExecutor.LogCommandFailureAndCheckRetry(ex, logger))
                        throw;
                }
            } // 输入作用域先松键；释放失败不能被普通命令报错的 catch 吞掉。
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            routeToken.ThrowIfCancellationRequested();
            reachedToken.ThrowIfCancellationRequested();
            throw;
        }

        routeToken.ThrowIfCancellationRequested();
        return completed;
    }

    internal sealed class HeldInputs(IInputChannel channel)
    {
        internal bool ReleaseFailed { get; private set; }

        internal void Press(User32.VK key) => channel.Keyboard.KeyDown(key);

        internal void Release(User32.VK key)
        {
            try
            {
                channel.Keyboard.KeyUp(key);
            }
            catch
            {
                ReleaseFailed = true;
                throw;
            }
        }
    }
}
