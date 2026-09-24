using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// [R5 A4 第二步] 执行根的**一次性退出凭证**（未接生产门；不改变任何既有入口的开关）。
/// 它只声明一件事：**这一颗已接纳的执行作用域已完成释放状态迁移**（Dispose 的线性化时刻已到）。
/// 它**不**声明：调用方执行体已返回；该根派生的全部叶子／逃逸任务都已退出；任务槽一定空闲
/// （<see cref="ExecutionExitReceipt.SlotObservedFree"/> 只是**释放后采样**，可能已看到继任根占用，
/// 既不保证空闲也不构成释放凭证）。
/// <see cref="ExecutionExitReceipt.ObservedOutcome"/> 为 false 时**没有任何终态被观测**，
/// 此时 <see cref="ExecutionExitReceipt.Result"/> 为 null——默认值不是事实，不得当作真实结果。
/// <c>StopAttribution</c> 同样只在确有入口发起停止/让位时才有值（来源名，例如 manual_stop／
/// directional_stop_requested／lease_expired），**不是** <see cref="ExecutionScope.StopReason"/>
/// 的默认值；未发起停止时为 null。
/// </summary>
internal sealed record ExecutionExitReceipt(
    int ProcessId,
    long ProcessStartTicksUtc,
    Guid ExecutionInstanceId,
    Guid RunId,
    Guid? JobId,
    string Name,
    string Kind,
    string Source,
    bool ObservedOutcome,
    TaskRunResult? Result,
    bool StopRequested,
    string? StopAttribution,
    long StopVersion,
    DateTime ExitedAtUtc,
    bool SlotObservedFree,
    long Order,
    /// <summary>
    /// 执行根释放那一刻，**已登记**（JobRegistry 内 ParentJobId 链可达）且**未终局**的派生子作业集合。
    /// <see cref="DescendantScanAvailable"/> 为 false 时该集合为空但**不构成证据**（注册表未创建/读取失败）。
    /// 该事实**不覆盖**未登记叶子与逃逸任务（见 §24.103）。
    /// </summary>
    bool DescendantScanAvailable,
    IReadOnlyList<Guid> OutstandingRegisteredDescendantJobIds);

/// <summary>
/// [R5 A4 第二步] 进程级**单槽**退出凭证台账：只保留顺序号最大的一次执行根退出事实，按执行实例身份查询。
/// 三条不变量：
/// ①**按 ExecutionInstanceId 精确匹配**——旧代凭证永远不能证明或授权新一代（身份不同即无凭证）；
/// ②**单槽 + 顺序守卫**——顺序号由执行根释放的线性化点（同一把根锁内）分配，台账只接受更大的顺序号，
///   因此"先退出但晚写入"的迟到旧记录不会覆盖继任根；被覆盖的旧代查询返回"无凭证"（保守，不伪造）；
/// ③**不能被"空闲观察"伪造**——凭证只由执行根生命周期结束写入，`TaskSemaphore` 空闲本身不产生凭证。
/// </summary>
internal static class ExecutionExitLedger
{
    private static readonly object Sync = new();
    private static ExecutionExitReceipt? _last;

    /// <summary>
    /// 写入一条退出凭证：**仅当顺序号大于当前槽**才替换。返回是否被接受；被拒绝不代表执行根没退出，
    /// 只代表台账选择保留更晚的退出事实。
    /// </summary>
    public static bool Record(ExecutionExitReceipt receipt)
    {
        lock (Sync)
        {
            if (_last is { } current && current.Order >= receipt.Order)
            {
                return false;
            }

            _last = receipt;
            return true;
        }
    }

    /// <summary>按执行实例身份精确查询；不匹配（含跨代、被更晚退出覆盖）返回 false。</summary>
    public static bool TryGet(Guid executionInstanceId, out ExecutionExitReceipt? receipt)
    {
        lock (Sync)
        {
            if (_last is { } last && last.ExecutionInstanceId == executionInstanceId)
            {
                receipt = last;
                return true;
            }
        }

        receipt = null;
        return false;
    }

    /// <summary>测试专用：清空台账，避免用例之间互相影响（生产路径不调用）。</summary>
    internal static void ResetForTest()
    {
        lock (Sync)
        {
            _last = null;
        }
    }
}
