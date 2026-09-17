using System;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// [A6] 抢占意图门（租约语义的 local 态子集，ADR-2026-09-16 / 总计划阶段 A6）。
/// 承载"联机锄地抢占"的跨缝隙意图：suspend 声明租约意图（Arm），IPC 下发的批次任务在持锁处
/// 原子消费（ConsumeIfServedBy），本地调度/热键/UI 启动在持锁处让位（HonorCheck）。
///
/// 设计红线：抢占意图绝不寄生在 CancellationContext 上——取消令牌会被 Set()/Clear() 重置，
/// 组间缝隙场景下 suspend 的 Cancel() 会打在已 disposed 的上下文上成为空枪（2026-09-16 实机
/// 事故根因）。本门是独立静态结构，任何 Set()/Clear() 都抹不掉它。
///
/// 线程纪律：锁内只做标志读写，绝不在持锁时等待（调用方负责在等待场景于锁外操作）。
/// </summary>
public static class PreemptionGate
{
    /// <summary>租约意图的存活窗口：超时未被批次任务消费则自动失效（防陈旧意图误杀后续任务）。</summary>
    public static readonly TimeSpan IntentTtl = TimeSpan.FromSeconds(90);

    /// <summary>[ADR-2026-09-16] 有界退出契约：取消/抢占发出后，槽位必须在该上界内确认释放。
    /// 等待方以"槽位确认空闲"这一状态为通过判据，时间只是上限；超界必须响亮失败，不得谎报成功。</summary>
    public static readonly TimeSpan QuiesceBound = TimeSpan.FromSeconds(30);

    private static readonly object Sync = new();

    private static int _epoch;
    private static DateTime _armedAtUtc;
    private static int _savedForEpoch;
    private static string? _ticket;
    private static readonly System.Collections.Generic.HashSet<string> Revoked = new();
    private static readonly System.Collections.Generic.Queue<string> RevokedOrder = new();

    public static bool Authorize(string? ticket)
    {
        lock (Sync)
        {
            if (!IsArmedCore()) return ticket == null;
            return ticket != null && ticket == _ticket;
        }
    }

    public static int Arm(string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) throw new ArgumentException("takeover ticket required");
        lock (Sync)
        {
            if (Revoked.Contains(ticket)) throw new InvalidOperationException("stale_ticket");
            if (_ticket == ticket && !IsArmedCore()) throw new InvalidOperationException("stale_ticket");
            if (IsArmedCore())
            {
                if (_ticket != ticket) throw new InvalidOperationException("takeover_conflict");
                _armedAtUtc = DateTime.UtcNow;
                return _epoch;
            }
            _epoch++;
            _ticket = ticket;
            _armedAtUtc = DateTime.UtcNow;
            return _epoch;
        }
    }

    public static bool Renew(string? ticket)
    {
        lock (Sync)
        {
            if (ticket == null || _ticket != ticket || !IsArmedCore()) return false;
            _armedAtUtc = DateTime.UtcNow;
            return true;
        }
    }

    public static bool Release(string? ticket)
    {
        lock (Sync)
        {
            if (_ticket != null && _ticket != ticket) return false;
            if (_ticket != null)
            {
                Revoked.Add(_ticket);
                RevokedOrder.Enqueue(_ticket);
                while (RevokedOrder.Count > 256) Revoked.Remove(RevokedOrder.Dequeue());
            }
            _ticket = null;
            _armedAtUtc = DateTime.MinValue;
            _epoch++;
            return true;
        }
    }

    /// <summary>当前代际（每次 Arm 递增）。仅观察用。</summary>
    public static int CurrentEpoch
    {
        get { lock (Sync) { return _epoch; } }
    }

    /// <summary>声明租约意图：suspend/抢占路径调用。返回新代际。</summary>
    public static int Arm()
    {
        lock (Sync)
        {
            _epoch++;
            _ticket = null;
            _armedAtUtc = DateTime.UtcNow;
            return _epoch;
        }
    }

    /// <summary>用户收租：F11/F12 手动停止表达"全部停止"，抢占意图即刻失效。</summary>
    public static void Disarm()
    {
        lock (Sync)
        {
            if (_ticket != null)
            {
                Revoked.Add(_ticket);
                RevokedOrder.Enqueue(_ticket);
                while (RevokedOrder.Count > 256) Revoked.Remove(RevokedOrder.Dequeue());
            }
            _epoch++;
            _ticket = null;
            _armedAtUtc = DateTime.MinValue;
        }
    }

    /// <summary>门当前是否有效（armed 且未过 TTL）。读取时惰性判断过期。</summary>
    public static bool IsArmed
    {
        get
        {
            lock (Sync)
            {
                return IsArmedCore();
            }
        }
    }

    /// <summary>
    /// IPC 下发的任务（JobSource ∈ {V2, Ext, Resume}）在持锁处原子消费门：
    /// 门有效则消费（后续本地启动不再让位）并返回 true；门无效/来源非 IPC 返回 false。
    /// 消费与持锁同点发生，不存在"先消费后起步"的时间窗。
    /// </summary>
    public static bool ConsumeIfServedBy(JobSource source)
    {
        if (source is not (JobSource.V2 or JobSource.Ext or JobSource.Resume))
        {
            return false;
        }
        lock (Sync)
        {
            if (!IsArmedCore() || _ticket != null)
            {
                return false;
            }
            _armedAtUtc = DateTime.MinValue;
            return true;
        }
    }

    /// <summary>
    /// 本地任务让位判定：门有效且该任务来源不是 IPC 下发时返回 true（调用方应让位）。
    /// 不消费门——让给后续的 IPC 批次任务消费。
    /// </summary>
    public static bool ShouldYield(JobSource source)
    {
        if (source is JobSource.V2 or JobSource.Ext or JobSource.Resume)
        {
            return false;
        }
        return IsArmed;
    }

    /// <summary>
    /// 每代际中断上下文只存一次（防级联让位覆写首个受害者的恢复点）：
    /// 本轮代际尚未保存过则标记并返回 true。
    /// </summary>
    public static bool TryMarkContextSaved()
    {
        lock (Sync)
        {
            if (_savedForEpoch == _epoch)
            {
                return false;
            }
            _savedForEpoch = _epoch;
            return true;
        }
    }

    private static bool IsArmedCore()
    {
        return _armedAtUtc != DateTime.MinValue
               && DateTime.UtcNow - _armedAtUtc <= IntentTtl;
    }
}

/// <summary>[A6] 抢占超界：有界退出契约（PreemptionGate.QuiesceBound）内槽位仍未确认释放。</summary>
internal sealed class PreemptTimeoutException : Exception
{
    public PreemptTimeoutException(string message) : base(message)
    {
    }
}
