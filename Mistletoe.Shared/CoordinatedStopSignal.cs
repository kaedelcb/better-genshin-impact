using System;
using System.Diagnostics;
using System.Threading;

namespace Mistletoe.Shared;

/// <summary>
/// The two existing processes hold the same manual-reset event. A stop survives either
/// process restarting even when its progress file cannot be written. If both holders
/// disappear, Open returns null: absence is never permission to replay an old batch.
/// </summary>
internal sealed class CoordinatedStopSignal : IDisposable
{
    private static string Prefix => @"Local\BetterGI.HoeingStop.Session"
        + Process.GetCurrentProcess().SessionId + ".";
    private readonly EventWaitHandle _handle;
    private readonly EventWaitHandle _suppressRelaunch;
    internal string Name { get; }
    internal bool IsStopped => _handle.WaitOne(0);
    internal bool RelaunchSuppressed => _suppressRelaunch.WaitOne(0);

    private CoordinatedStopSignal(string name, EventWaitHandle handle)
    {
        Name = name; _handle = handle;
        _suppressRelaunch = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".closed");
    }

    internal static CoordinatedStopSignal Create()
    {
        var name = Prefix + Guid.NewGuid().ToString("N");
        return new(name, new EventWaitHandle(false, EventResetMode.ManualReset, name));
    }

    internal static CoordinatedStopSignal? Open(string? name)
    {
        if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[Prefix.Length..], "N", out _)) return null;
        try { return new(name, EventWaitHandle.OpenExisting(name)); }
        catch (WaitHandleCannotBeOpenedException) { return null; }
    }

    internal void Stop() => _handle.Set();
    internal void SuppressRelaunch() => _suppressRelaunch.Set();
    internal void AllowRelaunch() => _suppressRelaunch.Reset();
    public void Dispose() { _handle.Dispose(); _suppressRelaunch.Dispose(); }
}
