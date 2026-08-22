using System;
using BetterGenshinImpact.Model;
using System.Threading;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

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

    public bool IsDisposed => disposed;

    public CancellationContext()
    {
        Cts = new CancellationTokenSource();
        _externalCtsList = new List<CancellationTokenSource>();
        IsManualStop = false;
        WasCancelled = false;
        disposed = false;
    }

    public void Set()
    {
        var prevWasCancelled = WasCancelled;
        var prevManualStop = IsManualStop;
        Cts = new CancellationTokenSource();
        _externalCtsList.Clear();
        IsManualStop = false;
        WasCancelled = false;
        disposed = false;
        try { BetterGenshinImpact.GameTask.Common.TaskControl.Logger.LogInformation("[CancelCtx][Set] 重置: prevWasCancelled={P} prevManualStop={M} -> 新Cts, WasCancelled=false", prevWasCancelled, prevManualStop); } catch { }
    }

    public CancellationToken Register(CancellationToken externalToken)
    {
        if (!disposed)
        {
            var externalCts = CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
            _externalCtsList.Add(externalCts);
            return externalCts.Token;
        }
        return CancellationToken.None;
    }

    public void ManualCancel()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (disposed)
            {
                return;
            }

            IsManualStop = true;
            WasCancelled = true;
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 并发 Clear 可能已释放 CTS，这里视为已取消/已清理。
            }

            foreach (var externalCts in _externalCtsList)
            {
                externalCts.Cancel();
                externalCts.Dispose();
            }

            _externalCtsList.Clear();
        }
    }

    public void Cancel()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (disposed)
            {
                try { BetterGenshinImpact.GameTask.Common.TaskControl.Logger.LogInformation("[CancelCtx][Cancel] 跳过: 已 disposed"); } catch { }
                return;
            }

            WasCancelled = true;
            cts = Cts;
            try { BetterGenshinImpact.GameTask.Common.TaskControl.Logger.LogInformation("[CancelCtx][Cancel] 已取消: WasCancelled=true, Cts.IsCancellationRequested={R}", cts.IsCancellationRequested); } catch { }
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 并发 Clear 可能已释放 CTS，这里视为已取消/已清理。
        }
    }

    /// <summary>只取消令牌但不设置 WasCancelled，用于 IPC task.start 等场景。</summary>
    public void CancelTokenOnly()
    {
        // 取消旧令牌（中断当前任务）
        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        // 创建新令牌（让后续任务在新上下文中执行，不重置 WasCancelled）
        Cts = new CancellationTokenSource();
    }

    public void Clear()
    {
        var wasWasCancelled = WasCancelled;
        Cts.Dispose();
        foreach (var externalCts in _externalCtsList)
        {
            externalCts.Dispose();
        }
        _externalCtsList.Clear();
        disposed = true;
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (disposed)
            {
                return;
            }

            cts = Cts;
            disposed = true;
            try { BetterGenshinImpact.GameTask.Common.TaskControl.Logger.LogInformation("[CancelCtx][Clear] 清理: disposed=true, WasCancelled={W}（保持不清）", wasWasCancelled); } catch { }
        }

        cts.Dispose();
    }
}
