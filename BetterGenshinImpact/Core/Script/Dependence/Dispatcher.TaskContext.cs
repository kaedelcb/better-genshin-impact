using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Script.Dependence.Model;

namespace BetterGenshinImpact.Core.Script.Dependence;

public partial class Dispatcher
{
    private static readonly AsyncLocal<bool> CustomCtsContext = new();

    // 自定义令牌属于当前异步调用流，不能影响并行任务或下一条脚本。
    public static bool IsCustomCts
    {
        get => CustomCtsContext.Value;
        set => CustomCtsContext.Value = value;
    }

    public async Task RunTask(SoloTask soloTask, CancellationTokenSource customCts)
    {
        // 保持链接令牌的既有生命周期：任务结束时可能仍有收尾子任务持有它。
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            customCts.Token, CancellationContext.Instance.Cts.Token);
        await RunTask(soloTask, linkedCts.Token);
    }

    /// <summary>
    /// 运行独立任务
    /// </summary>
    /// <param name="soloTask">
    /// 支持的任务名称:
    /// - AutoGeniusInvokation: 启动自动七圣召唤任务
    /// - AutoWood: 启动自动伐木任务
    /// - AutoFight: 启动自动战斗任务
    /// - AutoDomain: 启动自动秘境任务
    /// </param>
    /// <param name="customCt">自定义取消令牌，允许从JS控制任务取消</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public Task<object?> RunTask(SoloTask soloTask, CancellationToken? customCt = null)
        => RunTaskInContextAsync(customCt.HasValue, () => RunTaskCore(soloTask, customCt));

    internal static async Task<T> RunTaskInContextAsync<T>(bool usesCustomCts, Func<Task<T>> runTask)
    {
        var previous = IsCustomCts;
        IsCustomCts = usesCustomCts;
        try
        {
            return await runTask();
        }
        finally
        {
            // 包括初始化异常、提前返回和取消；嵌套任务恢复外层状态。
            IsCustomCts = previous;
        }
    }
}
