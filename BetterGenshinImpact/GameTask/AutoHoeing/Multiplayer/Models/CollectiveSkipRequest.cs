#nullable enable

using System;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

/// <summary>
/// 一次集体跳段请求（collective-skip-applied-ack）。
///
/// 背景：服务端原先只广播 targetProgress（long）这一瞬时事件，客户端无法区分
/// "收到过几次"、"是否重复"、"是否已经执行"，服务端也无法知道谁执行了 →
/// 部分成员照旧走旧路线，各打各的。
///
/// 本模型给每次集体跳段一个唯一 <see cref="SkipId"/>：
///   - 客户端按 SkipId 幂等去重（同一 SkipId 只消费一次、只回报一次 Applied）；
///   - 服务端按 SkipId 校验回报（旧/未知 SkipId 一律忽略），并等待全部必要成员回报。
///
/// 旧协议兼容：服务端仍以 targetProgress 作为旧事件名的参数（旧客户端零感知），
/// 新客户端从 evt 载荷读取 skipId + targetProgress。
/// </summary>
public sealed class CollectiveSkipRequest
{
    /// <summary>本次跳段的唯一标识（服务端生成）。空的表示来自旧服务端（无 skipId）。</summary>
    public string SkipId { get; set; } = "";

    /// <summary>目标进度（编码：路线索引 × 1e6 + 段索引 × 1e3 + 路点索引）。</summary>
    public long TargetProgress { get; set; } = -1;

    /// <summary>收到时刻（UTC），仅用于诊断/超时判定。</summary>
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>是否带有服务端生成的 SkipId（false = 旧服务端，退化为旧的"只跳段不回报"行为）。</summary>
    public bool HasSkipId => !string.IsNullOrEmpty(SkipId);

    public override string ToString()
        => $"CollectiveSkipRequest[SkipId={SkipId}, Target={TargetProgress}]";
}
