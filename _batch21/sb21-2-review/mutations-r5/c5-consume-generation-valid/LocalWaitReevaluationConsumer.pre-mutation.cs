using System;
using System.Collections.Generic;
using System.Linq;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 消费前复核结论（[批次 20／Wave2／C5/IW-04] 纯函数产物；**不含**发送许可——有效消费的唯一后继
/// 仍是「重新走一次完整准入」）。
/// </summary>
public sealed class LocalWaitConsumptionDecision
{
    /// <summary>是否有效消费（true ⇒ 可继续走完整准入；false ⇒ 请求过期丢弃并留痕）。</summary>
    public bool Valid { get; init; }

    /// <summary>人类可读原因（审计留痕用；过期时说明过期依据）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>过期判定（Valid=false 时为 true；便于观测面区分「未过期」与各类过期）。</summary>
    public bool Expired => !Valid;
}

/// <summary>
/// 槲寄生 · R5 批次 20 Wave2（§24.117 C5 sharpening／§24.115 IW-04；D-E2=① 代际载体）：
/// **在途重评请求的消费前复核**（组件合同层；未接线；生产零消费点）。
///
/// **它解决什么（IW-04 ABA）**：在途重评请求从产出到消费存在延迟窗口；窗口内等待项可能被
/// 取消→同载荷重登记（重激活 ⇒ 代际由队列高水位单调分配，D-E2=①）——旧请求若被盲目消费，会把**旧代际**的
/// 重评意图作用于**新代际**的等待项（ABA）。本组件每次调用都从 Store 回读队列项，按**代际**判过期。
///
/// **合同（C5 sharpening）**：
/// ①项不存在（已被移除/清理）⇒ 过期；
/// ②项状态非 Waiting（已取消/已失效/已终局）⇒ 过期；
/// ③请求携带代际 ≠ 项当前代际 ⇒ 过期（取消→重激活＝新代际，旧请求作废）；
/// ④请求的 ItemId 与项 ItemId 不一致 ⇒ 过期（身份错位，不得跨项消费）；
/// ⑤全部通过 ⇒ 有效消费——**唯一后继仍是「重新走一次完整准入」**（RequiresFullAdmission 恒 true；
///   本组件不产生发送许可、不推进执行、不读时钟；Store 损坏时响亮失败）。
///
/// **线性化边界**：Store 读取在 `Load()` 快照处线性化；本方法不是消费去重/at-most-once 账本，调用方仍须执行完整准入。
///
/// **代际语义（D-E2=①）**：代际载体由 Store 唯一写入口按队列高水位分配（调用方不可自报）；
/// 请求携带的代际来自产出时点的项代际。请求未携带代际（null/空白）⇒ 视为批次 15 无代际形态，
/// **按过期处理**（D-E2 落地后无代际请求不再具消费资格——保守方向；迁移期形态由接线批裁决）。
/// </summary>
public static class LocalWaitReevaluationConsumer
{
    /// <summary>
    /// 消费前复核：只接受 Store 入口；每次调用回读当前队列快照，判断针对该 Load 时点。
    /// </summary>
    public static LocalWaitConsumptionDecision Consume(
        LocalWaitReevaluationRequest request, LocalWaitQueueStore store)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);

        var currentItem = store.Load().FirstOrDefault(item =>
            string.Equals(item.ItemId, request.ItemId, StringComparison.Ordinal));
        return Evaluate(request, currentItem);
    }

    private static LocalWaitConsumptionDecision Evaluate(
        LocalWaitReevaluationRequest request, LocalWaitItem? currentItem)
    {
        if (currentItem is null)
            return Expired("消费前复核：等待项不存在（已被移除或清理）——请求过期丢弃。");

        if (!string.Equals(request.ItemId, currentItem.ItemId, StringComparison.Ordinal))
            return Expired($"消费前复核：请求 ItemId（{request.ItemId}）与当前项 ItemId（{currentItem.ItemId}）"
                + "不一致——身份错位，不得跨项消费。");
        // [Wave2 R34 重要-F4] StableIdentity（模型定义的**权威身份**）同样比对：同 ItemId、异 StableIdentity
        // 的替换项（Remove→重登记舞步）不得消费在途请求——ItemId 是派生标识，权威身份以 StableIdentity 为准。
        if (!string.Equals(request.StableIdentity, currentItem.StableIdentity, StringComparison.Ordinal))
            return Expired($"消费前复核：请求 StableIdentity（{request.StableIdentity}）与当前项 StableIdentity"
                + $"（{currentItem.StableIdentity}）不一致——权威身份错位，不得跨身份消费。");

        if (currentItem.State != LocalWaitItemState.Waiting)
            return Expired($"消费前复核：等待项当前状态为 {currentItem.State}（非 Waiting）——请求过期丢弃。");

        // ABA 判过期（D-E2=① 代际载体）：请求代际 ≠ 项当前代际 ⇒ 窗口内发生过取消→重激活。
        // [Wave2 R31 必改-F1] **fail-closed**：不可解析为 long 的代际串**不可能等于**项当前代际（long），
        // 按合同③必须过期——TryParse 失败不得放行（否则「gen-1」类格式漂移整体绕过 ABA 防护）。
        // **代际映射合同（[Wave2 R31 重要-3] 落字）**：Decide 的 generation 入参**必须**为
        // `item.Generation.ToString(CultureInfo.InvariantCulture)`（逐项取值）；一批 items 含不同代际时
        // 调用方必须**按代际分组、逐代际一次 Decide**。任何其它格式（"gen-1"、Guid、纪元串）在消费侧
        // 一律过期（本 fail-closed 分支）。
        var requestGeneration = Normalize(request.Generation);
        if (requestGeneration is null)
            return Expired("消费前复核：请求未携带代际（D-E2=① 后无代际请求不具消费资格）——请求过期丢弃。");
        // [Wave2 R32 重要-F4] **规范形回环**：NumberStyles.None（拒绝符号/空白）＋解析结果回环
        // （s == reqGen.ToString(InvariantCulture)）——"+1"/"01"/"-0" 等「可解析但非 ToString 产物」
        // 的形态按映射合同一律过期（格式漂移即过期，fail-closed）。
        if (!long.TryParse(requestGeneration, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var reqGen)
            || !string.Equals(requestGeneration, reqGen.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            return Expired($"消费前复核：请求代际串（{requestGeneration}）非规范 long 代际"
                + "（映射合同要求 item.Generation.ToString(InvariantCulture) 的逐字符产物）——"
                + "格式漂移即过期（fail-closed）。");
        if (reqGen != currentItem.Generation)
            return Expired($"消费前复核：请求代际（{reqGen}）≠ 项当前代际（{currentItem.Generation}）——"
                + "窗口内发生过取消→重激活（ABA），旧请求过期丢弃。");

        return new LocalWaitConsumptionDecision
        {
            Valid = true,
            Reason = "消费前复核通过：项存在、Waiting、代际匹配——继续走一次完整准入（不含发送许可）。",
        };
    }

    private static LocalWaitConsumptionDecision Expired(string reason)
        => new() { Valid = false, Reason = reason };

    // [Wave2 R34 必改-F2] 不做 Trim：首尾空白代际串（" 1"/"1 "）不是 ToString(InvariantCulture) 的
    // 逐字符产物 ⇒ 保留原样走规范形回环 ⇒ 过期（fail-closed）；Trim 会把格式漂移静默洗白成合法请求，
    // 且与触发器侧按键原值编码（"1"/" 1" 两键）形成幂等键别名。
    private static string? Normalize(string? generation)
        => string.IsNullOrWhiteSpace(generation) ? null : generation;
}
