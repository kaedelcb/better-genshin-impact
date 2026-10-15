using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>Flows with one dispatched job, including nested script/one-dragon tasks; never a process switch.</summary>
public sealed class CoordinatedHoeingScope : IDisposable
{
    private static readonly AsyncLocal<CoordinatedHoeingScope?> Ambient = new();
    private readonly CoordinatedHoeingScope? _previous = Ambient.Value;
    public bool Incomplete { get; private set; }
    public string? BatchId { get; }
    public IReadOnlyList<string> ExpectedParticipantUids { get; }
    public static CoordinatedHoeingScope? Current => Ambient.Value;
    public static bool IsActive => Ambient.Value != null;
    public static void MarkIncomplete() { if (Ambient.Value is { } scope) scope.Incomplete = true; }
    public CoordinatedHoeingScope(bool enabled, string? batchId = null, IEnumerable<string>? expectedParticipantUids = null)
    {
        BatchId = batchId;
        ExpectedParticipantUids = Array.AsReadOnly((expectedParticipantUids ?? Array.Empty<string>())
            .Where(uid => !string.IsNullOrWhiteSpace(uid)).Select(uid => uid.Trim())
            .Distinct(StringComparer.Ordinal).ToArray());
        if (enabled) Ambient.Value = this;
    }
    public void Dispose() => Ambient.Value = _previous;
}

public sealed class HoeingIncompleteException() : InvalidOperationException("hoeing_incomplete: 联机世界尚未全部完成");
