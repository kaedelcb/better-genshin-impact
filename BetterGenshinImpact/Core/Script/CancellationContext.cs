using System;
using BetterGenshinImpact.Model;
using System.Threading;
using System.Collections.Generic;

namespace BetterGenshinImpact.Core.Script;

public class CancellationContext : Singleton<CancellationContext>
{

    private List<CancellationTokenSource> _externalCtsList;

    public CancellationToken Token => Cts.Token;
    private readonly object _sync = new();
    public CancellationTokenSource Cts { get; private set; } = new();
    public bool IsManualStop { get; private set; }

    public bool IsCancellationRequested
    {
        get
        {
            lock (_sync)
            {
                return !disposed && Cts.IsCancellationRequested;
            }
        }
    }

    private bool disposed;

    /// <summary>最近一次任务是否被用户取消（F11 或取消热键）。任务开始时 Set() 清 false，取消时 Cancel()/ManualCancel() 设 true，Clear() 不清（供 task.status 查询）。</summary>
    public bool WasCancelled { get; private set; }

    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return disposed;
            }
        }
    }

    public CancellationContext()
    {
        Cts = new CancellationTokenSource();
        _externalCtsList = new List<CancellationTokenSource>();
        IsManualStop = false;
        WasCancelled = false;
        disposed = false;
    }

    /// <summary>
    /// [A1.3 锁粒度统一] 重建上下文。锁内更换引用/复位标志；旧 Cts 不 Dispose（既有语义：
    /// 在飞任务仍持有旧令牌，Dispose 会使其 Register 抛异常）。
    /// </summary>
    public void Set()
    {
        lock (_sync)
        {
            Cts = new CancellationTokenSource();
            _externalCtsList.Clear();
            IsManualStop = false;
            WasCancelled = false;
            disposed = false;
        }
    }

    /// <summary>[A1.3] 锁内登记，保证与 Set/Clear 的引用更换互斥（旧实现无锁，存在并发窗口）。</summary>
    public CancellationToken Register(CancellationToken externalToken)
    {
        lock (_sync)
        {
            if (!disposed)
            {
                var externalCts = CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
                _externalCtsList.Add(externalCts);
                return externalCts.Token;
            }
        }
        return CancellationToken.None;
    }

    /// <summary>手动停止（F11/停止热键）：置 IsManualStop，级联取消并释放外部 linked CTS。</summary>
    public void ManualCancel() => CancelCore(manualStop: true, cascadeExternal: true, resetToken: false);

    /// <summary>普通取消（UI 停止按钮/热键切换/suspend）：置 WasCancelled，只取消主令牌。</summary>
    public void Cancel() => CancelCore(manualStop: false, cascadeExternal: false, resetToken: false);

    /// <summary>只取消令牌但不设置 WasCancelled，用于 IPC task.start 等场景。</summary>
    public void CancelTokenOnly() => CancelCore(manualStop: false, cascadeExternal: false, resetToken: true);

    /// <summary>
    /// [A1.3 统一取消语义] 三个公开取消入口的单一核心实现（F2：双停止语义曾散落两处）。
    /// 语义矩阵（逐条对齐旧实现，公开行为零变化）：
    ///   ManualCancel    = IsManualStop=true + WasCancelled=true + 取消主令牌 + 级联取消/释放外部 CTS
    ///   Cancel          = WasCancelled=true + 取消主令牌（外部 linked 令牌随主令牌联动取消）
    ///   CancelTokenOnly = 取消旧主令牌 + 更换新主令牌；不动 IsManualStop/WasCancelled/外部列表
    /// 锁内只做标志与引用操作；Cancel()/Dispose() 一律锁外执行——取消回调是同步运行的，
    /// 持锁触发会把回调里的 CancellationContext 访问变成重入死锁。
    /// </summary>
    private void CancelCore(bool manualStop, bool cascadeExternal, bool resetToken)
    {
        CancellationTokenSource toCancel;
        List<CancellationTokenSource>? externals = null;
        lock (_sync)
        {
            if (disposed)
            {
                return;
            }

            if (manualStop)
            {
                IsManualStop = true;
            }
            if (!resetToken)
            {
                WasCancelled = true;
            }

            toCancel = Cts;
            if (resetToken)
            {
                // 更换新令牌（后续任务在新上下文中执行）；旧令牌对象不 Dispose（在飞任务仍持有）
                Cts = new CancellationTokenSource();
            }
            if (cascadeExternal)
            {
                externals = new List<CancellationTokenSource>(_externalCtsList);
                _externalCtsList.Clear();
            }
        }

        try
        {
            toCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 并发 Clear 可能已释放 CTS，这里视为已取消/已清理。
        }

        if (externals != null)
        {
            foreach (var externalCts in externals)
            {
                try
                {
                    externalCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 同上：并发清理视为已完成
                }
                try
                {
                    externalCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Dispose 幂等性兜底
                }
            }
        }
    }

    /// <summary>
    /// [A1.3] 释放当前上下文。修复旧实现死代码（disposed 先置 true 导致后半段恒不执行）；
    /// 行为等价：幂等（重复调用不再二次 Dispose），锁内摘取引用、锁外释放。
    /// </summary>
    public void Clear()
    {
        CancellationTokenSource cts;
        List<CancellationTokenSource> externals;
        lock (_sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            cts = Cts;
            externals = _externalCtsList;
            _externalCtsList = new List<CancellationTokenSource>();
        }

        cts.Dispose();
        foreach (var externalCts in externals)
        {
            try
            {
                externalCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Dispose 幂等性兜底
            }
        }
    }
}
