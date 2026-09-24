using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 槲寄生 · R5 批次 15／D3：**本地等待重评触发**（依据 owner 2026-09-24 裁决 D3＝推荐项 A：
/// 「占用结束／权威退出事件为主，启动恢复与新候选为辅，加低频安全网；**每次触发重新走完整准入**」）。
///
/// **它是什么**：一个**未接线的纯函数判定组件**——给定触发点与当前等待集合，产出「**须重新走一次完整准入**」
/// 的请求集合。请求**结构上不含发送许可**（<see cref="LocalWaitReevaluationRequest.RequiresFullAdmission"/>
/// 恒 true 且不可写），消费方只能把它交给统一准入面（`ArbitrationAdmissionService.SubmitAsync`）；
/// 本组件**绝不**调用任何发送路径（无 sender／client／execution boundary 依赖）。
///
/// **它不是什么（边界，如实）**：本批**不接任何生产入口**（无事件订阅、无定时器、无后台线程、无宿主启动扫描），
/// 不解除任何生产门，不产生发送；`SafetyNet` 触发点**不接真实定时器**——「是否到安全网时刻」由调用方注入的
/// 纯判定给出，时间由调用方传入（保持纯函数契约：不读时钟、不随机、不做 I/O）。
///
/// **幂等与去重**：①幂等键按稳定身份确定性派生（同身份 ⇒ 同键，异身份 ⇒ 异键是同口径设计意图，非零碰撞保证）；
/// ②同一批内同稳定身份的重复条目**只产一条**；③本实例内**已产出过**的等待项**不再产**（在飞去重，见
/// <see cref="_handled"/>；配置 <see cref="StateScope"/> 时限定在该作用域内）；
/// ④取消令牌已取消 ⇒ 直接返回空集（且**不消费**幂等键，取消不算"已处理"）。
///
/// **在飞去重的用途**：防止**同一新候选的重复到达**反复重评（幂等，不是"一次性投入"）。代价是同一稳定身份
/// 代表**不同代际**等待项时会被一并屏蔽——该边界由调用方的 <see cref="StateScope"/> 界定，本批不接线。
///
/// **并发**：<see cref="Decide(LocalWaitReevaluationTriggerPoint, IReadOnlyList{LocalWaitItem})"/> 可被并发调用；
/// 状态预留用 <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> 单次原子操作 ⇒ **同一实例内**并发触发对同一等待项
/// **至多一条**请求。<see cref="_handled"/> 是**进程内**状态：**不等于**跨进程单写者，也**不**声称断电耐久；
/// 多实例/多进程/重启均可重复产，接收方须自行幂等。
///
/// **取消／失效**：`LocalWaitItemState.Cancelled` 的项一律不产；已产过的项即使之后取消/再次 `Waiting` 也不再产
/// （取消**不**复活执行意愿）。
/// </summary>
public sealed class LocalWaitReevaluationTrigger
{
    /// <summary>已产出过重评请求的等待项幂等键（在飞去重；进程内，非跨进程权威）。</summary>
    private readonly ConcurrentDictionary<string, byte> _handled = new(StringComparer.Ordinal);

    /// <summary>
    /// **安全网到期判定**（注入式纯函数；**默认恒 false ⇒ 安全网在当前配置下不触发**）。
    /// 时间由调用方传入，本组件不读时钟：判定只回答「以 <paramref name="nowUtc"/> 看是否已到安全网时刻」。
    /// 生产构造不注入真实定时器；本批亦未接线任何调用方。
    /// </summary>
    private readonly Func<DateTimeOffset, bool> _safetyNetDue;

    /// <summary>
    /// 构造：<paramref name="safetyNetDue"/>＝注入式安全网到期判定（默认恒 false ⇒ 安全网不触发）；
    /// <paramref name="stateScope"/>＝可选的**幂等键隔离标识**（见 <see cref="StateScope"/>；默认 `null` 保持既有语义）。
    /// 构造函数**不接受**任何发送端／调度器／定时器，本组件无外部依赖。
    /// </summary>
    public LocalWaitReevaluationTrigger(Func<DateTimeOffset, bool>? safetyNetDue = null, string? stateScope = null)
    {
        _safetyNetDue = safetyNetDue ?? (_ => false);
        StateScope = string.IsNullOrWhiteSpace(stateScope) ? null : stateScope;
    }

    /// <summary>
    /// 派生键与 <see cref="LocalWaitQueuePolicy.DeriveItemId"/> 共同保留的**十六进制摘要前缀长度**（64 位）。
    /// 有两点是**有意披露的边界**，不得被读成"绝不碰撞"：
    /// ①两个派生函数用**同一**摘要函数、**同一**前缀长度、仅在字面前缀（`reval-` vs `wait-`）上不同
    ///   ⇒「同身份 ⇒ 两者摘要逐字符一致」可机械断言（见夹具 `ReevaluationKey_DigestPrefixMatchesWaitItemIdDigest`）；
    /// ②16 个十六进制字符 = 64 位摘要 ⇒ **在巨大身份空间里仍可能碰撞**（生日界约 2^32 个身份）。
    /// 本文档与夹具只主张「同口径、同前缀长度、无系统性别名」，**不**主张密码学强度或零碰撞。
    /// </summary>
    public const int DocumentedHashPrefixLength = 16;

    /// <summary>
    /// **重评幂等键**：由稳定身份确定性派生（与 <see cref="LocalWaitQueuePolicy.DeriveItemId"/> 同口径、
    /// 不同前缀：`reval-` vs `wait-`）。纯函数：不读时钟、不随机、不做 I/O、不抛异常。
    ///
    /// **边界（如实）**：前缀长度 <see cref="DocumentedHashPrefixLength"/>＝64 位摘要，故「异身份 ⇒ 异键」
    /// 是**设计意图与夹具样例**，**不是**全空间的数学保证。与 `DeriveItemId` 的关系是「同摘要、异字面前缀」，
    /// 精确比较见夹具。
    /// </summary>
    public string DeriveReevaluationKey(string stableIdentity)
    {
        // 与 LocalWaitQueuePolicy.DeriveItemId 逐字一致的归一化：null 与空串同键（不抛异常、不产生别名歧义）。
        var normalized = stableIdentity ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "reval-" + Convert.ToHexString(hash)[..DocumentedHashPrefixLength].ToLowerInvariant();
    }

    /// <summary>
    /// **幂等键隔离标识**（可选构造参数；默认 `null` ⇒ 与既有行为完全一致）。
    ///
    /// **为什么存在**：`_handled` 在"等待项**实例化之前**"也按稳定身份屏蔽后续产出，用途是防止同一新候选的
    /// 重复到达反复重评（**幂等，不是一次性**）。但当同一稳定身份代表**不同代际的等待项**时，永久屏蔽会连
    /// 合法的新代际一并屏蔽。此参数把在飞去重**限定在该标识的作用域内**：
    /// ①`null`（默认）⇒ 与批内既有语义一致；
    /// ②非空 ⇒ 与稳定身份共同构成去重键 **且**写入产物的 <see cref="LocalWaitReevaluationRequest.ReevaluationKey"/>
    ///   （形如 `reval-&lt;scope&gt;:&lt;摘要&gt;`），故**不同作用域 ⇒ 不同键 ⇒ 互不屏蔽**。
    /// **接线边界**：作用域由**调用方**提供；生产接线时该值必须来自权威的等待项代际／租约纪元，**不得**用
    /// 本地时间戳或随机值绕过去重（否则等同关闭幂等）。本批**不接线**，不实现任何代际来源。
    /// </summary>
    public string? StateScope { get; }

    /// <summary>
    /// 判定本次触发应产出的重评请求（可不带时间；安全网判定以 <see cref="DateTimeOffset.UnixEpoch"/> 为输入）。
    /// </summary>
    public LocalWaitReevaluationDecision Decide(LocalWaitReevaluationTriggerPoint trigger,
        IReadOnlyList<LocalWaitItem>? items)
        => Decide(trigger, items, DateTimeOffset.UnixEpoch, CancellationToken.None);

    /// <summary>
    /// 判定本次触发应产出的重评请求（可带时间）。时间仅用于**注入式安全网判定**，不参与其它决策。
    /// </summary>
    public LocalWaitReevaluationDecision Decide(LocalWaitReevaluationTriggerPoint trigger,
        IReadOnlyList<LocalWaitItem>? items, DateTimeOffset nowUtc)
        => Decide(trigger, items, nowUtc, CancellationToken.None);

    /// <summary>
    /// 判定本次触发应产出的重评请求。
    ///
    /// **取消规则**：<paramref name="cancellationToken"/> 已取消 ⇒ **空集**（且**不**消费幂等键：
    /// 取消不算"已处理"，取消后仍可再次评估）。
    /// **安全网规则**：<see cref="LocalWaitReevaluationTriggerPoint.SafetyNet"/> 且注入判定给出"未到期"
    /// ⇒ **空集**（不消费幂等键：未到期不是"已处理"）。
    /// **幂等规则（如实口径）**：只对 `Waiting` 项产请求；同批同稳定身份只产一条；**本实例内**已产过的
    /// 稳定身份（若配置 `StateScope`，则限定在该作用域内的稳定身份）不再产。
    ///
    /// **契约边界（不得被读成更强保证）**：
    /// ①`TryAdd` 是**状态预留**，**不**等于"请求已被交付／已被消费方接收"——预留与 `return` 间无可观察的
    ///   失败界限（同一次调用内无 I/O、无 await），但登记**先于**调用方看到产物；
    /// ②去重只在**本实例进程内**成立，**不**跨实例、跨进程、跨重启（多实例或重启可重复产）；
    /// ③本方法**不**校验 `item.ItemId` 与 `StableIdentity` 的派生一致性（接收方可重新派生校验）；
    /// ④取消令牌只在进入时检查一次：遍历过程中令牌转为已取消**不会**中断本次求值；
    ///   `item.State` 等在遍历中被其它线程修改属共享可变状态竞态边界，本组件不做快照。
    ///
    /// **本方法的产物永远只表达"须重新走一次完整准入"**：不产生发送、不含发送许可、不推进执行。
    /// </summary>
    public LocalWaitReevaluationDecision Decide(LocalWaitReevaluationTriggerPoint trigger,
        IReadOnlyList<LocalWaitItem>? items, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Empty(trigger, "触发求值被取消：不产重评请求（幂等键未消费）");

        if (trigger == LocalWaitReevaluationTriggerPoint.SafetyNet && !_safetyNetDue(nowUtc))
            return Empty(trigger, "安全网未到期（注入式判定）：不产重评请求（幂等键未消费）");

        var requests = new List<LocalWaitReevaluationRequest>();
        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items ?? [])
        {
            if (item is null) continue;
            if (item.State != LocalWaitItemState.Waiting) continue;
            if (string.IsNullOrWhiteSpace(item.StableIdentity)) continue;

            // 幂等键：稳定身份派生；若配置了 StateScope，则把在飞去重限定在该作用域内
            // （不同作用域 ⇒ 不同键 ⇒ 互不屏蔽；同一作用域内仍按稳定身份去重）。
            var identityKey = DeriveReevaluationKey(item.StableIdentity);
            var key = StateScope is null
                ? identityKey
                : "reval-" + StateScope + ":" + identityKey["reval-".Length..];
            // 同批去重：同一等待项在同一批里只产一条（不按条目数放大）
            if (!seenInBatch.Add(key)) continue;
            // 在飞去重：本实例已产过 ⇒ 不再产（并发下由 TryAdd 单次原子预留保证恰好一条）
            if (!_handled.TryAdd(key, 0)) continue;

            requests.Add(new LocalWaitReevaluationRequest
            {
                ItemId = item.ItemId,
                StableIdentity = item.StableIdentity,
                CandidateId = item.CandidateId,
                Trigger = trigger,
                ReevaluationKey = key,
            });
        }

        return new LocalWaitReevaluationDecision
        {
            Trigger = trigger,
            Requests = requests,
            Reason = requests.Count == 0
                ? "无可重评的等待项（空集或均已处理/已取消）"
                : "已产出重评请求：" + requests.Count + " 条（须重新走完整准入；本组件不产生发送）",
        };
    }

    private static LocalWaitReevaluationDecision Empty(LocalWaitReevaluationTriggerPoint trigger, string reason)
        => new() { Trigger = trigger, Requests = [], Reason = reason };
}
