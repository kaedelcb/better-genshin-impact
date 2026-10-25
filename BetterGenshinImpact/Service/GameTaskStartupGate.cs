using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Service;

/// <summary>运行环境绑定与界面就绪是两个条件；每次启动独立确认，不能复用上次的就绪状态。</summary>
internal static class GameTaskStartupGate
{
    internal static async Task PrepareAsync(bool waitForMainUi,
        Func<CancellationToken, Task<bool>> ensureRuntime,
        Func<CancellationToken, bool> checkReady,
        CancellationToken ct,
        Action? verifyOwnership = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null)
    {
        void Verify()
        {
            ct.ThrowIfCancellationRequested();
            verifyOwnership?.Invoke();
        }

        Verify();
        bool started;
        try { started = await ensureRuntime(ct); }
        catch
        {
            // 停止可能同时关闭窗口/截图器；此时以停止为准，不把它误记成启动故障。
            Verify();
            throw;
        }
        Verify();
        if (!started)
            throw new InvalidOperationException("游戏运行环境未就绪，未开始执行任务");

        // 七圣召唤、音游等由调用方明确选择的特殊入口，只准备运行环境。
        if (!waitForMainUi) return;

        delay ??= Task.Delay;
        timeProvider ??= TimeProvider.System;
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        var start = timeProvider.GetTimestamp();
        await Task.Run(async () =>
        {
            while (true)
            {
                Verify();
                if (timeProvider.GetElapsedTime(start) >= limit)
                    throw new TimeoutException("等待游戏就绪超时，登录、加载或领奖尚未完成，未开始执行任务");

                bool ready;
                try { ready = checkReady(ct); }
                catch
                {
                    Verify();
                    throw;
                }
                // 识别期间收到停止时，即使当前帧已是主界面，也不能放行旧任务。
                Verify();
                if (ready) return;

                // 点击后跨帧等待；所有等待绑定本次令牌，不读取后来任务替换的全局令牌。
                await delay(TimeSpan.FromSeconds(1), ct);
            }
        }, ct);
    }
}
