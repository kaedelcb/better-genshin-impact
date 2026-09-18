using System;
using System.Threading;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>龙内子项显式执行结果（R4.7 结果传播通道）。</summary>
public enum OneDragonItemOutcome
{
    /// <summary>正常完成。</summary>
    Succeeded,
    /// <summary>失败（含 IntoSereniteaPot false、寻找失败等非异常失败）。</summary>
    Failed,
    /// <summary>取消。</summary>
    Cancelled,
    /// <summary>正常跳过（未配置策略/未选择目标/活动已结束/不在运行日期——非失败）。</summary>
    SkippedNormal,
}

/// <summary>子项显式上报失败时由执行包装抛出的标记异常（携带上报原因，供子作业终态与根作业聚合）。</summary>
public sealed class OneDragonItemFailedException : Exception
{
    public OneDragonItemFailedException(string message) : base(message) { }
}

/// <summary>
/// R4.7 结果传播显式通道（R3 终审挂账工程项）：
/// 壶/幽境等 Start 吞异常的子项，在吞异常点/非异常失败点把真实结果显式上报到本通道；
/// 执行包装（OneDragonFlowViewModel 默认条目分支）消费上报，失败时以
/// <see cref="OneDragonItemFailedException"/> 显式抛出——子作业终态与根作业聚合据此拿到真实结果，
/// 失败不被后续成功覆盖（D6 聚合规则）。
///
/// 边界声明：Start 调用外观与公版容错语义不变（吞异常保留、整龙页面展示语义不变）；
/// 本通道只服务外部严格合同结果（注册表/桥接观察面），与原生展示语义区分（对照表 B17）。
/// AsyncLocal 承载：随 ExecutionContext 流入 Task.Run 内的子项执行；不受 RunnerContext.Clear 影响。
/// </summary>
public sealed class OneDragonItemResultChannel : IDisposable
{
    private static readonly AsyncLocal<OneDragonItemResultChannel?> Ambient = new();

    private readonly OneDragonItemResultChannel? _previous;
    private readonly object _sync = new();
    private bool _disposed;

    private OneDragonItemResultChannel(OneDragonItemResultChannel? previous)
    {
        _previous = previous;
        Ambient.Value = this;
    }

    /// <summary>当前执行上下文上的通道（无通道 = 非龙内子项执行，上报静默丢弃）。</summary>
    public static OneDragonItemResultChannel? Current => Ambient.Value;

    /// <summary>开启通道（执行包装在每个默认条目前调用；嵌套时退出恢复上一层）。</summary>
    public static OneDragonItemResultChannel OpenScoped() => new(Ambient.Value);

    /// <summary>结果+原因不可变快照（ASTRA 二轮 I②：成对原子替换，杜绝"结果来自一报、原因来自另一报"的错配）。</summary>
    private sealed record ItemResultSnapshot(OneDragonItemOutcome Outcome, string? Reason);

    private ItemResultSnapshot? _snapshot;

    /// <summary>已上报的结果（null = 子项未上报；不得把 null 当作失败之外的任何结论）。</summary>
    public OneDragonItemOutcome? Outcome => _snapshot?.Outcome;

    /// <summary>失败/跳过原因（与结果同报同源；失败快照不被后续上报覆盖）。</summary>
    public string? Reason => _snapshot?.Reason;

    /// <summary>上报结果。优先级：Failed &gt; Cancelled &gt; SkippedNormal &gt; Succeeded（失败不被后续上报覆盖）。</summary>
    public void Report(OneDragonItemOutcome outcome, string? reason = null)
    {
        lock (_sync)
        {
            if (_disposed) return;
            // 结果与原因作为不可变快照整体替换：仅更高优先级上报替换快照，同优先级/更低保留首报（I②）
            if (_snapshot is null || Priority(outcome) > Priority(_snapshot.Outcome))
                _snapshot = new ItemResultSnapshot(outcome, reason);
        }
    }

    /// <summary>便捷静态上报：当前上下文有通道才生效（独立运行/无通道时静默丢弃，保持公版行为）。</summary>
    public static void ReportCurrent(OneDragonItemOutcome outcome, string? reason = null)
        => Current?.Report(outcome, reason);

    /// <summary>
    /// 吞异常点统一上报判据（ASTRA 二轮 B6：OperationCanceledException = 取消，不得误报失败；
    /// TaskCanceledException 是其子类同路径）。prefix 为场景描述（如"领取尘歌壶奖励异常: "）。
    /// </summary>
    public static void ReportExceptionCurrent(Exception e, string prefix)
    {
        if (e is OperationCanceledException) ReportCurrent(OneDragonItemOutcome.Cancelled, prefix + e.Message);
        else ReportCurrent(OneDragonItemOutcome.Failed, prefix + e.Message);
    }

    private static int Priority(OneDragonItemOutcome outcome) => outcome switch
    {
        OneDragonItemOutcome.Failed => 4,
        OneDragonItemOutcome.Cancelled => 3,
        OneDragonItemOutcome.SkippedNormal => 2,
        OneDragonItemOutcome.Succeeded => 1,
        _ => 0,
    };

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
    }
}
