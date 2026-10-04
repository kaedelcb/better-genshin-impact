using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// **前置就绪三态**（R5 批次 16／D2：owner 2026-09-24 裁决 D2＝推荐项 A
/// 「**持久化稳定前置引用**（例如 workflow/node/ticket 引用）＋重评时由**只读 evaluator** 得出的就绪结果；
/// **发送前再次验算**」，理由是「重启可复核；**不信任过期布尔**；可解释」）。
///
/// 三态互不重叠、取值唯一（不得用别名表达同一态）：
/// <see cref="Ready"/>＝已就能选；<see cref="NotReady"/>＝**已确定**未就绪；
/// <see cref="Undetermined"/>＝**无法判定**（缺引用／缺求值器／引用不可解析／求值器无结论）。
///
/// **保守方向是唯一允许的方向**：<see cref="NotReady"/> 与 <see cref="Undetermined"/> **一律不参选**
/// ——不可判定**既不得当作已就绪，也不得当作「空闲」而抢发**。这与既有的
/// 「缺字段保守按不可信」（`hasTrustedIdentity` 默认 false）取向一致。
///
/// **本枚举不含任何发送许可**：它只回答「前置是否就绪」，不回答「是否可发送」。
/// </summary>
public enum PrerequisiteReadiness
{
    /// <summary>**已确定**未就绪（例如引用指向的流程/节点/票据尚未完成）：不参选。</summary>
    NotReady = 0,

    /// <summary>前置已就绪：可以进入「重新走一次完整准入」（**不等于**已获发送许可）。</summary>
    Ready = 1,

    /// <summary>
    /// **不可判定**（缺引用、缺求值器、引用格式非法或求值器给不出结论）：不参选。
    /// 这是**保守默认**——不得被读成「已就绪」，也不得被读成「空闲」。
    /// </summary>
    Undetermined = 2,
}

/// <summary>
/// **只读 evaluator**（R5 批次 16／D2）：给定**持久化稳定前置引用**，返回前置就绪三态。
///
/// **只读的含义**：委托只有一个输入（引用的解析视图）且**无副作用**——不读时钟、不写盘、不改动等待项、
/// 不调用任何发送路径。之所以在**读取时**求值而不落盘布尔，是为了「**不信任过期布尔**」：
/// 排队时刻的快照在重启/延后之后可能已经失真。
///
/// **边界（如实）**：本批**不接任何生产入口**——权威来源与失败语义（引用不可解析时到底算未就绪还是不可判定、
/// 由谁提供引用）属**接线前**事项；本组件只定义求值形状与保守默认。
/// </summary>
/// <param name="reference">持久化的稳定前置引用（由 <c>LocalWaitItem.PrerequisiteReference</c> 提供）。</param>
public delegate PrerequisiteReadiness PrerequisiteEvaluator(object? reference);

/// <summary>
/// **持久化稳定前置引用**（R5 批次 16／D2）：落盘可复核的引用，**不是**布尔就绪快照。
///
/// 采用「**结构版本 ＋ 稳定引用串**」形态：
/// ①<see cref="Version"/> 是**引用结构版本**（未来引用形状变化时可响亮区分，不降级解析）；
/// ②<see cref="Value"/> 是**稳定引用串**（例如 workflow／node／ticket 的稳定标识组合），
///   要求属于**稳定身份族**——**不得**用本地时间戳或随机值填充（否则重启后不可复核）。
///
/// ⚠ **本批的证明边界（已收窄，不得读强）**：真实**落盘**的只有引用串
/// （`LocalWaitItem.PrerequisiteReference` 是 <c>string?</c>，落盘键 `prerequisiteReference`），
/// <see cref="Version"/> **未落盘**、**未参与求值**，也**未**绑定任何真实代际／租约纪元；
/// 「引用指向什么权威事实源、版本绑哪个真实纪元」仍属接线前事项。
/// 本类型在本批是**模型层纯数据**（供将来引用形状演进时的登记面），不是已生效的持久格式。
///
/// **它不是"就绪"**：就绪**永远由 evaluator 在读取时求得**；本类型只回答「前置是谁」。
/// </summary>
public sealed class PrerequisiteReference : IEquatable<PrerequisiteReference>
{
    /// <summary>当前支持的引用结构版本（唯一权威常量）。</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// 引用结构版本（**本批未落盘、未参与求值**——见类型注释的证明边界；不在支持范围时按不可求值处理，不降级解析）。
    /// </summary>
    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;

    /// <summary>稳定引用串（workflow／node／ticket 等稳定标识组合；不得用时间戳或随机值）。</summary>
    [JsonPropertyName("value")] public string Value { get; set; } = "";

    /// <summary>是否为**可求值**形态：版本在支持范围且引用串非空白。</summary>
    [JsonIgnore]
    public bool IsEvaluable => Version >= 1 && Version <= CurrentVersion && !string.IsNullOrWhiteSpace(Value);

    /// <summary>确定性派生（同一引用串 ⇒ 同一对象内容；与稳定身份族同口径）。</summary>
    public static PrerequisiteReference FromStableReference(string value)
        => new() { Version = CurrentVersion, Value = value ?? "" };

    /// <summary>
    /// **稳定比较口径**：按 <see cref="Value"/> 的<strong>序号（ordinal）</strong>逐字符比较——
    /// 引用是**稳定身份**而非自然语言，不做区域性折叠（避免 Türkçe/大小写区域差异导致重启后比对不一致）。
    /// </summary>
    public bool Equals(PrerequisiteReference? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PrerequisiteReference other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? "");

    /// <summary>
    /// 稳定引用串（**只读视图**）：`ToString()` 只回引用串本身。
    /// **本批证明边界**：真实落盘的只有引用串（`LocalWaitItem.PrerequisiteReference` 是 `string?`，键 `prerequisiteReference`）。
    /// <see cref="Version"/> **未落盘**（故本类型**不**按 `version`＋`value` 两个字段写入落盘；
    /// 落盘面上引用就是**一个**字符串字段）、**未参与求值**，也**未**绑定任何真实代际／租约纪元。
    /// 下列 `[JsonPropertyName]` 标注只描述**将来**若把本类型**整体**序列化时的形状，本批**不**走该路径。
    /// </summary>
    public override string ToString()
        => Value ?? "";

    /// <summary>逐字符一致即视为同一引用（引用语义；不比较结构版本，版本用于可求值性判定）。</summary>
    public static bool operator ==(PrerequisiteReference? left, PrerequisiteReference? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>逐字符不一致即视为不同引用。</summary>
    public static bool operator !=(PrerequisiteReference? left, PrerequisiteReference? right) => !(left == right);
}

/// <summary>
/// **只读逐项前置求值结果**（R5 批次 16／D2）——**纯函数产物**，不改动等待项、不含发送许可。
/// 与 <c>LocalWaitQueuePolicy.EvaluatePrerequisites</c> 一一对应：一项一个结论，便于审计与「发送前再次验算」复用同一求值器。
///
/// **类型别名说明**：本类型与 <see cref="MultiplayerHoeingAssistant.Services.LocalWaitPrerequisiteEvaluation"/>
/// 承载同一契约面（<c>Item</c>／<c>Readiness</c>／<c>Reference</c>／<c>Reason</c>／<c>Selectable</c>）；
/// 定义在 <c>Models</c> 命名空间下是为了让「前置就绪」这一**值语义**与契约夹具的登记面一致
/// （生产零消费点扫描允许出现于模型目录定义处）。**本批不添加任何生产消费点**。
/// </summary>
public sealed class PrerequisiteEvaluation
{
    /// <summary>被求值的等待项（原对象；本求值不改动它）。</summary>
    [JsonIgnore] public object? Item { get; init; }

    /// <summary>被求值等待项的稳定标识（便于审计与纯反射读取）。</summary>
    public string ItemId { get; init; } = "";

    /// <summary>就绪结论（三态；<c>NotReady</c> 与 <c>Undetermined</c> **一律不参选**）。</summary>
    public PrerequisiteReadiness Readiness { get; init; } = PrerequisiteReadiness.Undetermined;

    /// <summary>本次求值用到的持久化稳定引用串（null ⇒ 缺引用 ⇒ 不可判定）。</summary>
    [JsonIgnore] public string? Reference { get; init; }

    /// <summary>人类可读原因（审计用；不得承载发送许可或成功断言）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>是否可参选（**唯一**允许参选的结论是 <see cref="PrerequisiteReadiness.Ready"/>）。</summary>
    [JsonIgnore] public bool Selectable => Readiness == PrerequisiteReadiness.Ready;
}
/// <summary>
/// **发送前再次验算结论（模型层登记面）**（R5 批次 16／D2，§24.106 **C5**：「被选出的项在取得发送许可前必须
/// 重新取得纪元、占用者事实、身份、级别和票据」）。
///
/// **唯一**的发送前验算结论类型（本批已删除服务层的同义重复类型：重复定义会使「模型层登记面」成为
/// 无产出者的死别名，属本批要消除的表面漂移）。定义在 <c>Models</c> 命名空间下，使「发送前验算」这一
/// **值语义**与前置就绪三态同处登记；产出者是 <c>LocalWaitQueuePolicy.RevalidateBeforeSend</c>（两个重载均返回本类型）。
///
/// **结构上不含发送许可**：**没有** <c>SendPermitted</c>／<c>SendSeq</c>／<c>JobId</c>／<c>SubmissionIdentity</c>；
/// <see cref="RequiresFullAdmission"/> **恒为 true**——无论验算通过与否，**唯一**合法后继都是
/// 「重新走一次完整准入」。本类型**不**表达「可直接发送」，**不**表达「已获执行许可」，
/// 也**不**把未知结果改写成成功或已证实失败。
///
/// **不得沿用排队时快照**：结论由**再次求值**得出（见 <c>LocalWaitQueuePolicy.RevalidateBeforeSend</c>）。
/// **本批不添加任何生产消费点**。
/// </summary>
public sealed record LocalWaitPrerequisiteDecision
{
    /// <summary>被复核的等待项（原对象；本求值不改动它）。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public object? Item { get; init; }

    /// <summary>被复核的等待项的稳定标识（审计用）。</summary>
    public string ItemId { get; init; } = "";

    /// <summary>发送前**重新**求得的就绪结论（不是排队时的快照）。</summary>
    public PrerequisiteReadiness Readiness { get; init; } = PrerequisiteReadiness.Undetermined;

    /// <summary>重新求值用到的持久化稳定前置引用串（null ⇒ 缺引用 ⇒ 不可判定）。</summary>
    public string? Reference { get; init; }

    /// <summary>人类可读原因（审计用；不得承载发送许可或成功断言）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>
    /// 恒为 true：**唯一**合法后继是「重新走一次完整准入」。
    /// 无 setter ⇒ 调用方不可伪造成 false 来暗示「可直接发送」。
    /// </summary>
    public bool RequiresFullAdmission => true;

    /// <summary>复核是否通过（仅 <see cref="PrerequisiteReadiness.Ready"/> 算通过；**通过也不等于已获发送许可**）。</summary>
    public bool Passed => Readiness == PrerequisiteReadiness.Ready;
}