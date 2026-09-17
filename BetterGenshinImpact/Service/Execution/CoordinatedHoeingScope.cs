using System;
using System.Threading;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>Flows with one dispatched job, including nested script/one-dragon tasks; never a process switch.</summary>
public sealed class CoordinatedHoeingScope : IDisposable
{
    private static readonly AsyncLocal<CoordinatedHoeingScope?> Ambient = new();
    private readonly CoordinatedHoeingScope? _previous = Ambient.Value;
    public bool Incomplete { get; private set; }
    public static bool IsActive => Ambient.Value != null;
    public static void MarkIncomplete() { if (Ambient.Value is { } scope) scope.Incomplete = true; }
    public CoordinatedHoeingScope(bool enabled) { if (enabled) Ambient.Value = this; }
    public void Dispose() => Ambient.Value = _previous;
}

public sealed class HoeingIncompleteException() : InvalidOperationException("hoeing_incomplete: 联机世界尚未全部完成");
