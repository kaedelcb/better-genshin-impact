using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

internal static class PickupCombatScriptExecutor
{
    private const int InitialPickupWaitMs = 250;

    internal static bool Supports(CombatScript script) => Supports(script, out _);

    internal static bool Supports(CombatScript script, out string? reason, CombatScenes? scenes = null)
    {
        if (script.CombatCommands.Count == 0)
        {
            reason = "策略没有可执行指令";
            return false;
        }

        for (var i = 0; i < script.CombatCommands.Count; i++)
        {
            var command = script.CombatCommands[i];
            var specialCurrentAttack = command.Method == Method.Attack
                && command.Name == CombatScriptParser.CurrentAvatarName
                && scenes?.GetAvatars().Any(avatar => avatar.Name == "阿蕾奇诺" && avatar.ArlecchinoAutoEnabled) == true;
            if (!IsSupportedCommand(command) || specialCurrentAttack)
            {
                var text = $"{command.Name} {command.Method.Alias[0]}({string.Join(",", command.Args ?? [])})";
                var detail = specialCurrentAttack ? "队伍启用了阿蕾奇诺特殊普攻，不能保证当前角色攻击的安全中断"
                    : command.Method == Method.KeyPress ? "仅支持 keypress(f) 和 keypress(x)"
                    : command.Method == Method.Attack && command.Name == "阿蕾奇诺" ? "特殊普攻包含额外连招"
                    : command.Method == Method.Attack || command.Method == Method.Wait ? "时长参数必须是可用的非负秒数"
                    : command.Method == Method.Skill ? "仅支持可安全中断的具名角色短按战技"
                    : "该指令尚不支持安全中断";
                reason = $"第 {i + 1} 条「{text}」：{detail}";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool IsSupportedCommand(CombatCommand command)
    {
        var method = command.Method;
        if (method == Method.Wait || method == Method.Attack)
        {
            if (method == Method.Attack && command.Name == "阿蕾奇诺")
                return false; // 特化普攻内部可能连续按住多个键，仍由旧执行器执行。
            return method == Method.Attack && command.Args is { Count: 0 }
                || command.Args is { Count: 1 } args && IsSafeDuration(args[0]);
        }

        if (method == Method.Skill)
        {
            return command.Args is { Count: 0 }
                && command.Name != CombatScriptParser.CurrentAvatarName
                && command.Name is not ("阿蕾奇诺" or "玛薇卡"); // 这些角色有额外的战斗状态处理。
        }

        return method == Method.KeyDown || method == Method.KeyUp || method == Method.MouseDown || method == Method.MouseUp
            || method == Method.Click || method == Method.MoveBy || IsSupportedPress(command);
    }

    private static bool IsSafeDuration(string text)
    {
        try
        {
            var seconds = CombatCommand.ParseSeconds(text);
            return seconds >= 0 && seconds <= int.MaxValue / 1000d;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSupportedPress(CombatCommand command) => command.Method == Method.KeyPress
        && command.Args is { Count: 1 } args
        && User32Helper.ToVk(args[0]) is User32.VK.VK_F or User32.VK.VK_X;

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
                var avatar = command.ResolveAvatar(scenes, commands[i == 0 ? 0 : i - 1], stop);
                stop.ThrowIfCancellationRequested();
                if (avatar != null)
                    await ExecuteCommandAsync(command, avatar, held, stop);
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
        else if (method == Method.Skill)
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
        var held = new HeldInputs(channel);
        var completed = false;
        try
        {
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
                {
                    throw;
                }
            }
            finally
            {
                held.ReleaseOwned();
            }
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == linked.Token && linked.IsCancellationRequested)
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
        private readonly HashSet<User32.VK> _owned = [];

        internal bool ReleaseFailed { get; private set; }

        internal void Press(User32.VK key)
        {
            // 别接管玩家、移动动作或其他执行器已经按住的键。
            if (!channel.IsKeyDown(key))
                _owned.Add(key);
            channel.Keyboard.KeyDown(key);
        }

        internal void Release(User32.VK key)
        {
            try
            {
                channel.Keyboard.KeyUp(key);
                _owned.Remove(key);
            }
            catch
            {
                // 松键失败不能降级成普通命令错误；finally 仍会重试本节点拥有的键。
                ReleaseFailed = true;
                throw;
            }
        }

        internal void ReleaseOwned()
        {
            List<Exception>? failures = null;
            foreach (var key in _owned)
            {
                try
                {
                    channel.Keyboard.KeyUp(key);
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }
            _owned.Clear();
            if (failures != null) throw new AggregateException("采集点收尾时释放按键失败", failures);
        }
    }
}
