using System.Diagnostics;
using System.Collections.Concurrent;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **批次17：并发夹具共享生命周期运行器（测试侧基础设施）**。
///
/// **它是什么**：把「启动（后台线程）→ 就绪栅栏 → 授权 → 放行 → 统一回收」的并发夹具生命周期
/// 收敛为**单一测试侧实现**，供真实并发夹具（`Decide_ConcurrentTriggers_*`）与本文件末尾的
/// 常驻故障注入守卫夹具（`CleanupWake_DoesNotAuthorizeTestedCall`）**共同调用**——
/// 授权守卫的反向突变（MUT-B17-5b）因此直接命中真实修复点（会诊 B17-R6-02）。
///
/// **授权守卫（清理唤醒 ≠ 授权执行）**：worker 经 <see cref="WaitAuthorized"/> 等待放行；
/// 只有主线程在就绪成功后写入授权标志，worker 才被允许进入被测调用。就绪失败/启动失败后的
/// 清理放行**不授权** ⇒ 被唤醒的 worker 直接退出，不执行被测调用（会诊 B17-R5-02）。
///
/// **边界（如实）**：本运行器**不**终止已进入被测调用的线程（.NET 无线程终止原语；被测调用
/// 无取消通道）；保证的是：失败可观测（Join 超时计数）、清理路径可达（finally 放行＋回收）、
/// 宿主退出不被拖住（后台线程）、清理唤醒不执行被测调用（授权分离）。残余＝「被测调用在
/// 宿主内持续自旋」需进程隔离承载，已登记 §24.110 批次 17 残项交 owner 三选一。
/// </summary>
internal sealed class ConcurrentGateRunner
{
    private readonly ManualResetEventSlim _gate = new(false);
    private readonly CountdownEvent _ready;
    private int _authorized;

    public ConcurrentGateRunner(int threads) => _ready = new CountdownEvent(threads);

    /// <summary>本轮就绪等待是否失败（true ⇒ 清理放行已发生且**未**授权）。</summary>
    public bool ReadyFailed { get; private set; }

    /// <summary>成功路径的 Join 超时线程数（0＝全部在时限内回收）。</summary>
    public int JoinTimeouts { get; private set; }

    /// <summary>回收阶段共享总预算（所有 worker 合计；B17-R8-01：不随线程数线性放大）。</summary>
    private static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// **worker 侧唯一入口**：等待放行。返回 false＝清理唤醒（未授权）⇒ worker **不得**执行被测调用；
    /// 返回 true＝已授权，可以执行。<c>Volatile</c> 保证主线程授权写入对 worker 可见。
    /// 【MUT-B17-5b 突变点：本方法的授权判断是真实夹具与守卫夹具共用的唯一实现】
    /// </summary>
    public bool WaitAuthorized()
    {
        _ready.Signal();
        _gate.Wait();
        return Volatile.Read(ref _authorized) == 1;
    }

    /// <summary>
    /// **主线程**：启动全部 worker（后台）→ 就绪等待 → 授权 → 放行 → 回收（统一 try/finally）。
    /// 就绪失败 ⇒ <see cref="ReadyFailed"/>=true 且**不授权**；finally 无条件清理放行＋逐线程
    /// try/catch 回收（单次失败不中断其余回收）。
    /// </summary>
    public void Run(IEnumerable<Thread> workers, TimeSpan? readyTimeout = null)
    {
        var started = new List<Thread>();
        try
        {
            foreach (var w in workers)
            {
                w.IsBackground = true;
                w.Start();
                started.Add(w);
            }
            if (!_ready.Wait(readyTimeout ?? TimeSpan.FromSeconds(30)))
                ReadyFailed = true;
            if (!ReadyFailed)
                Volatile.Write(ref _authorized, 1);
            _gate.Set();
            // [批次17 B17-R8-01/B17-R9-01] 回收用**共享总预算**（30s），不是逐线程 30s：N 个卡死线程的
            // 累计等待以 30s 为上限（逐线程口径最坏 8×2×30s≈480s）。计时用 **Stopwatch**（单调），
            // 不用 DateTime.UtcNow（墙钟可被校时回拨/前跳，放大或提前耗尽预算）。
            var clock = Stopwatch.StartNew();
            foreach (var w in started)
            {
                var remaining = JoinBudget - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    if (!w.Join(remaining)) JoinTimeouts++;
                }
                // [批次17 B17-C11-02] 预算耗尽分支**不得**短路跳过状态检查：用零等待 Join 核实真实状态，
                // 已退出的线程不计为超时（避免把「预算已消耗」误报为「该 worker 未结束」）。
                else if (!w.Join(TimeSpan.Zero))
                {
                    JoinTimeouts++;
                }
            }
        }
        finally
        {
            _gate.Set();
            // 收尾快扫：只收割在预算耗尽后自行结束的线程（每线程上限 100ms、与主回收预算**分开**计），
            // 不追加长等待；单次失败不得中断其余线程回收。
            foreach (var w in started)
            {
                try { w.Join(TimeSpan.FromMilliseconds(100)); }
                catch (Exception) { /* best-effort 回收：单次失败不得中断其余线程回收 */ }
            }
        }
    }
}

/// <summary>
/// **常驻故障注入守卫（会诊 B17-R6-02 的防回归判据）**：与真实并发夹具共用
/// <see cref="ConcurrentGateRunner"/>（同一授权实现）。注入「就绪失败 ⇒ 清理放行」路径，
/// 断言被清理唤醒的 worker **不执行**被测调用。
/// **反向突变（MUT-B17-5b，已实测）**：把 <see cref="ConcurrentGateRunner.WaitAuthorized"/>
/// 的授权判断改为恒真 ⇒ 本守卫红；还原 ⇒ 绿。因真实夹具调用**同一方法**，该突变同时命中
/// 真实修复点（区别于批次 17 早期用已删除复制件做的 MUT-B17-5）。
/// </summary>
public sealed class ConcurrentGateRunnerGuardTests
{
    [Fact]
    public void CleanupWake_DoesNotAuthorizeTestedCall()
    {
        // [批次17 B17-C01 确定性构造] 运行器期望 **2** 个就绪信号、实际只启动 **1** 个 worker
        // ⇒ 就绪计数按构造恒缺 1 ⇒ 主线程就绪等待**必然**超时（不依赖 Sleep 时序竞争）。
        // 随后 finally 清理放行（不授权）⇒ worker 被唤醒后必须因未授权而退出。
        var runner = new ConcurrentGateRunner(threads: 2);
        var executed = new ConcurrentBag<int>();

        // worker 不延迟：直接进入 WaitAuthorized（Signal 计数 1/2）后等待在 gate 上
        var worker = new Thread(() =>
        {
            if (runner.WaitAuthorized()) executed.Add(1);   // 代表「执行了被测调用」
        });
        worker.IsBackground = true;

        runner.Run(new[] { worker }, readyTimeout: TimeSpan.FromMilliseconds(50));

        Assert.True(runner.ReadyFailed, "注入前提：就绪计数恒缺 1 ⇒ 应构造性超时（失败路径）");
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "被清理唤醒的 worker 应自行退出");
        Assert.Empty(executed);   // 清理唤醒 ⇒ 不得执行被测调用
        Assert.Equal(0, runner.JoinTimeouts);
    }
}
