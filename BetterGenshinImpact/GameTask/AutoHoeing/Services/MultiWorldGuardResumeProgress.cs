using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>同一次守护重开的不可变进度快照：只记实际完成，保留原计划范围，不跨任务读取旧记录。</summary>
public sealed class MultiWorldGuardResumeProgress
{
    private readonly string _rosterSignature;
    private readonly int _roundLimit;
    private readonly HashSet<string> _completed;
    public IReadOnlySet<string> PlannedHostUids { get; }

    private MultiWorldGuardResumeProgress(string signature, int roundLimit,
        IReadOnlySet<string> planned, HashSet<string> completed)
    {
        _rosterSignature = signature;
        _roundLimit = roundLimit;
        PlannedHostUids = planned;
        _completed = completed;
    }

    public static MultiWorldGuardResumeProgress Create(string signature, int roundLimit, IEnumerable<string> order)
        => new(signature, roundLimit,
            new HashSet<string>(order.Take(Math.Max(0, roundLimit)), StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

    public bool Matches(string signature, int roundLimit)
        => !string.IsNullOrEmpty(signature) && signature == _rosterSignature && roundLimit == _roundLimit;

    public IReadOnlySet<string> CompletedForReport(string signature, int roundLimit, bool enabled)
        => enabled && Matches(signature, roundLimit)
            ? new HashSet<string>(_completed, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    public MultiWorldGuardResumeProgress ConfirmCompleted(string hostUid)
    {
        var completed = new HashSet<string>(_completed, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(hostUid) && PlannedHostUids.Contains(hostUid)) completed.Add(hostUid);
        return new(_rosterSignature, _roundLimit, PlannedHostUids, completed);
    }
}
