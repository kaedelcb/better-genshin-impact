using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// **触发器作用域（三类界限入档，R5.5／§2④a-4）**——不得混称：
/// ①启动中心临时触发器（定时 arm／电子狗／日志）= <see cref="ProcessEphemeral"/>：**进程级、不跨重启恢复**（现状保留）；
/// ②任务中心根级触发器（`trigger.time`／`trigger.timeFixed`／`trigger.timeFlexible`）= <see cref="PersistedRecoverable"/>：
///   由 RunStore 持久化、可恢复；
/// ③节点伴随观察器（机制五b）= <see cref="NodeCompanion"/>：**后置未实施**（本类型仅登记界限）。
/// </summary>
public enum TriggerScope
{
    /// <summary>进程级临时（启动中心定时/电子狗/日志触发器）。</summary>
    ProcessEphemeral,
    /// <summary>持久化可恢复（任务中心根级触发器）。</summary>
    PersistedRecoverable,
    /// <summary>节点伴随观察器（后置未实施）。</summary>
    NodeCompanion,
}

/// <summary>台账条目（所有权／挂载时刻／意图／撤销入口＝§2④a-1 的四要素）。</summary>
public sealed class BackgroundTriggerEntry
{
    /// <summary>确定性标识＝kind|ownerKind|ownerRef|intentKey（同键＝同一挂载，重复登记幂等）。</summary>
    public string TriggerId { get; init; } = "";
    /// <summary>触发器种类：timer／watchdog／log。</summary>
    public string Kind { get; init; } = "";
    /// <summary>所有权种类：startup-chain／task-center-trigger／manual。</summary>
    public string OwnerKind { get; init; } = "";
    /// <summary>所有者引用：启动链（链身份+节点身份）／任务中心触发出现身份／`manual`。</summary>
    public string OwnerRef { get; init; } = "";
    /// <summary>挂载时刻（UTC）。</summary>
    public DateTimeOffset MountedAtUtc { get; init; }
    /// <summary>意图（人类可读：盯什么、成立时执行什么）。</summary>
    public string Intent { get; init; } = "";
    /// <summary>撤销入口（人类可读：从何处撤销，如「流程视图中的「取消」按钮」）。</summary>
    public string RevokeEntry { get; init; } = "";
    /// <summary>作用域（三类界限之一；启动中心背景触发器恒为进程级临时）。</summary>
    public TriggerScope Scope { get; init; } = TriggerScope.ProcessEphemeral;
}

/// <summary>所有权种类常量（§2④a-1 的三种挂载来源）。</summary>
public static class TriggerOwnerKinds
{
    public const string StartupChain = "startup-chain";
    public const string TaskCenterTrigger = "task-center-trigger";
    public const string Manual = "manual";
}

/// <summary>
/// **R5.5 统一后台触发器台账（机制五a）**：登记／查询／撤销三件套；**进程级**（启动中心临时触发器不跨重启恢复，
/// 故台账亦不持久化——重启后台账为空且无残留挂载，与 TriggerScope.ProcessEphemeral 一致）。
/// **范围如实**：台账是**登记与撤销的权威视图**，不自行 arm/disarm；实际撤销仍走各触发器的既有取消入口
/// （页面「取消」按钮／集合移除）。线程安全（内部锁）；查询返回快照副本。
/// </summary>
public sealed class BackgroundTriggerLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BackgroundTriggerEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>确定性条目标识（同键重复登记＝同一挂载，幂等不叠加）。</summary>
    public static string TriggerIdOf(string kind, string ownerKind, string ownerRef, string intentKey)
        => (kind ?? "") + "|" + (ownerKind ?? "") + "|" + (ownerRef ?? "") + "|" + (intentKey ?? "");

    /// <summary>登记（幂等：同 <see cref="BackgroundTriggerEntry.TriggerId"/> 已存在则保留首次条目、返回既有条目）。</summary>
    public BackgroundTriggerEntry Register(BackgroundTriggerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.TriggerId, out var existing)) return existing;
            _entries[entry.TriggerId] = entry;
            return entry;
        }
    }

    /// <summary>按标识撤销（真=确实移除；假=不存在，幂等）。</summary>
    public bool Revoke(string triggerId)
    {
        lock (_gate)
        {
            return _entries.Remove(triggerId ?? "");
        }
    }

    /// <summary>查询单条（不存在返回 null）。</summary>
    public BackgroundTriggerEntry? TryGet(string triggerId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(triggerId ?? "", out var e) ? e : null;
        }
    }

    /// <summary>快照（按 TriggerId 稳定排序；副本，调用方可安全遍历）。</summary>
    public IReadOnlyList<BackgroundTriggerEntry> List()
    {
        lock (_gate)
        {
            return _entries.Values.OrderBy(e => e.TriggerId, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>当前挂载数（诊断用）。</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>撤销全部（进程退出/流程整体撤下时的收尾；返回被撤销条数）。</summary>
    public int RevokeAll()
    {
        lock (_gate)
        {
            var n = _entries.Count;
            _entries.Clear();
            return n;
        }
    }
}