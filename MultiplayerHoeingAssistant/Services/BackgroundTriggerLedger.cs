using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
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

/// <summary>台账条目（所有权／挂载时刻／意图／撤销入口＝§2④a-1 四要素）。</summary>
public sealed class BackgroundTriggerEntry
{
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
    /// <summary>撤销入口（人类可读：实际撤销在何处发生）。</summary>
    public string RevokeEntry { get; init; } = "";
    /// <summary>作用域（三类界限之一）。</summary>
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
/// **R5.5 统一后台触发器台账（机制五a）——「挂载投影」语义（会诊整改）**：
/// 台账是宿主**已挂载集合的可查视图**；**撤销不在此处发生**（`Apply/Remove/Clear` 为 `internal`，仅供宿主集合同源同步调用）。
/// 实际撤销一律走宿主取消入口（页面「取消」按钮／`MistletoeViewModel.RevokeAllArmedTriggers`），集合移除后条目随之消失。
/// **范围如实（三轮会诊收窄）**：由此只能主张「**外部无法借集合或同步器只清投影**」；
/// **不构成**「撤销/通知/异步执行始终一致」的全称保证——集合通知订阅方抛错仍可能打断通知链，
/// 该情形由宿主取消入口以「先取消 CTS／退订 → 再移除 → `finally` 按集合重建投影」收敛。**进程级**：不持久化。
/// 线程安全（内部锁）；查询返回快照副本。
/// </summary>
public sealed class BackgroundTriggerLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BackgroundTriggerEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>确定性条目标识：各段**转义后拼接**（避免 `|`/`%` 分段歧义）。</summary>
    public static string TriggerIdOf(string kind, string ownerKind, string ownerRef, string intentKey)
        => Escape(kind) + "|" + Escape(ownerKind) + "|" + Escape(ownerRef) + "|" + Escape(intentKey);

    private static string Escape(string? value)
        => (value ?? "").Replace("%", "%25", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal);

    /// <summary>登记/更新（**internal**：只由宿主集合同源同步调用；幂等：同标识保留首次条目）。</summary>
    internal BackgroundTriggerEntry Apply(BackgroundTriggerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.TriggerId, out var existing)) return existing;
            _entries[entry.TriggerId] = entry;
            return entry;
        }
    }

    /// <summary>移除条目（**internal**：宿主集合移除后的同步动作；返回是否确实移除）。</summary>
    internal bool Remove(string triggerId)
    {
        lock (_gate)
        {
            return _entries.Remove(triggerId ?? "");
        }
    }

    /// <summary>移除某类全部条目（**internal**：集合 Reset 时重建用）。</summary>
    internal void RemoveKind(string kind)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Values.Where(e => string.Equals(e.Kind, kind, StringComparison.Ordinal))
                         .Select(e => e.TriggerId).ToList())
                _entries.Remove(id);
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
}

/// <summary>挂载实例的描述（由宿主提供：所有权引用／意图／撤销入口）。</summary>
public sealed record ArmedTriggerDescriptor(string OwnerRef, string Intent, string RevokeEntry);

/// <summary>
/// **已挂载集合 ↔ 台账的同源同步器（会诊整改）**：按 `NotifyCollectionChangedAction` 正确处理
/// Add／Remove／Replace／Reset／Move；**按实例分配进程内唯一编号**（不用哈希，避免碰撞），
/// **同实例重复加入按引用计数**（撤下一次不注销，归零才注销）。
/// 纯逻辑、可单测——VM 只负责把集合事件与快照转发进来。
/// </summary>
public sealed class ArmedTriggerLedgerSync
{
    private readonly BackgroundTriggerLedger _ledger;
    private readonly string _kind;
    private readonly string _ownerKind;
    private readonly TriggerScope _scope;
    private readonly string _mountedRevokeEntry;
    private readonly Func<object, ArmedTriggerDescriptor?> _describe;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<object, string> _instanceIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, string> _triggerIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _refs = new(ReferenceEqualityComparer.Instance);
    private long _sequence;

    internal ArmedTriggerLedgerSync(BackgroundTriggerLedger ledger, string kind, string ownerKind, TriggerScope scope,
        string mountedRevokeEntry, Func<object, ArmedTriggerDescriptor?> describe, Func<DateTimeOffset>? utcNow = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _kind = kind;
        _ownerKind = ownerKind;
        _scope = scope;
        _mountedRevokeEntry = mountedRevokeEntry;
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>集合变更入口（`currentSnapshot` = 变更**之后**的集合快照，Reset 重建用）。</summary>
    public void OnCollectionChanged(NotifyCollectionChangedAction action, IList? oldItems, IList? newItems,
        IReadOnlyList<object> currentSnapshot)
    {
        switch (action)
        {
            case NotifyCollectionChangedAction.Add:
                RegisterEach(newItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                DetachEach(oldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                DetachEach(oldItems);
                RegisterEach(newItems);
                break;
            case NotifyCollectionChangedAction.Move:
                break; // 集合成员未变
            case NotifyCollectionChangedAction.Reset:
                ResetTo(currentSnapshot);
                break;
            default:
                ResetTo(currentSnapshot); // 未知动作：按当前快照重建（保守取真）
                break;
        }
    }

    /// <summary>
    /// 按当前快照**对账**（`Reset`／兜底／「取消后收尾」）——**不再无条件重建**（会诊整改）：
    /// 仍在集合中的实例**保留**其条目标识、**首次挂载时刻**与登记信息；消失的实例注销（用登记时保存的标识）；
    /// 新出现的实例补登；**引用计数按快照重算**。因此「取消 A」不会改写仍挂载的 B 的标识或挂载时刻。
    /// </summary>
    internal void ResetTo(IReadOnlyList<object> currentSnapshot)
    {
        var counts = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        foreach (var item in currentSnapshot)
            if (item is not null) counts[item] = counts.TryGetValue(item, out var n) ? n + 1 : 1;

        // ① 已消失 ⇒ 注销（用登记时保存的完整标识；不重算描述）
        foreach (var tracked in _refs.Keys.ToList())
        {
            if (counts.ContainsKey(tracked)) continue;
            _refs.Remove(tracked);
            _instanceIds.Remove(tracked);
            if (_triggerIds.Remove(tracked, out var goneId)) _ledger.Remove(goneId);
        }

        // ② 新出现 ⇒ 补登
        RegisterEach(currentSnapshot);

        // ③ 引用计数以快照为准（幸存者身份/挂载时刻**不变**）。
        //    仅对**已成功登记**的实例记数：`_describe` 返回 null 的实例**不**写入 `_refs`，
        //    以便其描述恢复有效后**再次对账时能重新尝试登记**（会诊整改：否则永久漏登且只增计数）。
        foreach (var item in counts.Keys)
            if (_triggerIds.ContainsKey(item)) _refs[item] = counts[item];
    }

    private void RegisterEach(IEnumerable? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is null) continue;
            if (_refs.TryGetValue(item, out var n))
            {
                _refs[item] = n + 1; // 同实例重复加入：引用计数，不新建条目
                continue;
            }
            var descriptor = _describe(item);
            if (descriptor is null) continue;
            var instanceId = _kind + "#" + (++_sequence); // 进程内唯一编号（非哈希）
            _refs[item] = 1;
            _instanceIds[item] = instanceId;
            var triggerId = BackgroundTriggerLedger.TriggerIdOf(_kind, _ownerKind, descriptor.OwnerRef, instanceId);
            _triggerIds[item] = triggerId; // 会诊整改：注销用**登记时**的完整标识（描述变化/变 null 也能销）
            _ledger.Apply(new BackgroundTriggerEntry
            {
                TriggerId = triggerId,
                Kind = _kind,
                OwnerKind = _ownerKind,
                OwnerRef = descriptor.OwnerRef,
                MountedAtUtc = _utcNow(),
                Intent = descriptor.Intent,
                RevokeEntry = descriptor.RevokeEntry.Length > 0 ? descriptor.RevokeEntry : _mountedRevokeEntry,
                Scope = _scope,
            });
        }
    }

    private void DetachEach(IEnumerable? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is null || !_refs.TryGetValue(item, out var n)) continue;
            if (n > 1)
            {
                _refs[item] = n - 1; // 仍有同一实例在集合中：不注销
                continue;
            }
            _refs.Remove(item);
            _instanceIds.Remove(item);
            if (_triggerIds.Remove(item, out var registeredId)) _ledger.Remove(registeredId);
        }
    }
}