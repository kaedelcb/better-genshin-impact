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

    public bool IsDisposed => disposed;

    public CancellationContext()
    {
        Cts = new CancellationTokenSource();
        _externalCtsList = new List<CancellationTokenSource>();
        IsManualStop = false;
        disposed = false;
    }

    public void Set()
    {
        Cts = new CancellationTokenSource();
        _externalCtsList.Clear();
        IsManualStop = false;
        disposed = false;
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
                return;
            }

            cts = Cts;
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

    public void Clear()
    {
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
        }

        cts.Dispose();
    }
}
