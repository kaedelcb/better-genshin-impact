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
/// **它是什么**：一个**未接线的、有状态的判定组件**（由调用方驱动；`_handled` 使同一输入的首次与再次调用结果不同）。
/// 给定触发点与当前等待集合，它产出「**须重新走一次完整准入**」的请求集合。请求**结构上不含发送许可**（<see cref="LocalWaitReevaluationRequest.RequiresFullAdmission"/>
/// 恒 true 且不可写），消费方只能把它交给统一准入面（`ArbitrationAdmissionService.SubmitAsync`）；
/// 本组件**绝不**调用任何发送路径（无 sender／client／execution boundary 依赖）。
///
/// **它不是什么（边界，如实）**：本批**不接任何生产入口**（无事件订阅、无定时器、无后台线程、无宿主启动扫描），
/// 不解除任何生产门，不产生发送；`SafetyNet` 触发点**不接真实定时器**——「是否到安全网时刻」由调用方注入的
/// 纯判定给出，时间由调用方传入（判定本身不读时钟、不随机、不做 I/O）。
///
/// **幂等与去重**：①幂等键按稳定身份确定性派生（同身份 ⇒ 同键，异身份 ⇒ 异键是同口径设计意图，非零碰撞保证）；
/// ②同一批内同（稳定身份＋代际）的重复条目**只产一条**；③本实例内**已产出过**的（稳定身份＋代际）**不再产**
/// （在飞去重，见 <see cref="_handled"/>；配置 <see cref="StateScope"/> 时限定在该作用域内）；
/// ④取消令牌已取消 ⇒ 直接返回空集（且**不消费**幂等键，取消不算"已处理"）。
///
/// **键的编码（批次 15c 起）**：ReevaluationKey 的新形状见
/// <see cref="ComposeKeyPart"/>：`reval-key-v2|&lt;摘要&gt;|&lt;作用域字段&gt;|&lt;代际字段&gt;`，
/// 各字段带**长度前缀**且载荷为 **UTF-16 码元**十六进制（大写）⇒ 可**无歧义还原**（消除评审第 3 轮必改 #2 里
/// `:`／`|` 拼接造成的跨作用域**键别名**）。**唯一**与批次 15 逐字符相同的形态是
/// `scope == null &amp;&amp; generation == null` ⇒ `reval-&lt;摘要&gt;`；**带作用域的非空形态有意改为新编码**
/// （旧形状的别名不可修复，见 <see cref="ComposeKey"/>），此改动只改变键的**字符形状**，
/// 不改变"同一实例内、**同一作用域且同一代际**下每个身份至多产一条"的语义（第 13 轮重要 #1 措辞收窄：同一身份以**不同代际**传入**会**各产一条，见下方"在飞去重的用途"与 <see cref="Decide(LocalWaitReevaluationTriggerPoint, IReadOnlyList{LocalWaitItem}, DateTimeOffset, CancellationToken, string?)"/> 的契约）。
///
/// **在飞去重的用途**：防止**同一新候选的重复到达**反复重评（幂等，不是"一次性投入"）。**限制（第 14 轮建议更正）**：
/// **仅在调用方未提供可区分的代际入参时**，同一稳定身份代表**不同代际**等待项才会被一并屏蔽——该边界的
/// 处置通道是调用方的 **代际入参**（见公开五参重载 <see cref="Decide(LocalWaitReevaluationTriggerPoint, IReadOnlyList{LocalWaitItem}, DateTimeOffset, CancellationToken, string?)"/>：
/// 传入不同代际 ⇒ 各产一条）与 <see cref="StateScope"/>，本批不接线。
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
    /// 本文档与夹具只主张「同口径、同前缀长度」，**不**主张密码学强度、零碰撞或对**任意**身份串的单射性。
    /// </summary>
    public const int DocumentedHashPrefixLength = 16;
    /// <summary>
    /// **键字段编码**（批次 15c：消除评审第 3 轮必改 #2 的跨作用域密钥别名）。
    ///
    /// **为什么需要**：原实现把作用域与代际**原样**拼进键（`reval-[&lt;scope&gt;:]&lt;摘要&gt;[|&lt;代际&gt;]`），
    /// 因分隔符 `:`／`|` 可出现在作用域或代际内部，**不同三元组可映射到同一键**，例如
    /// `(scope=null, 身份=s-1, 代际="x:"+h+"|z")` 与 `(scope=h+"|x", 身份=s-1, 代际="z")` 同为 `reval-h|x:h|z`。
    /// 同一实例内 `StateScope` 不可变 ⇒ 不会造成本实例自屏蔽；但**跨实例／跨进程按该键对账或幂等**的消费方
    /// 会把两个不同作用域的产物当成同一条（幂等键别名）。
    ///
    /// **编码规则**（可无歧义还原，确定性：不读时钟、不随机、不抛异常）：
    /// ①`null`（无该字段）⇒ 单字符 `'-'`；
    /// ②非 null ⇒ 单字符 `'='` ＋ 载荷长度（字符数）＋ `':'` ＋ **UTF-16 码元序列的十六进制**。
    ///    **【批次 15c 第二轮修订，如实】** 第一版用 `Encoding.UTF8.GetBytes(value)`：**UTF-8 编码不是
    ///    对任意 .NET 字符串的单射**——孤立代理项（如 `"\uD800"`）会被替换字符 U+FFFD（`EF BF BD`）取代 ⇒
    ///    `"a\uD800"` 与 `"a\uFFFD"`（同身份、同代际）映射到**同一载荷 ⇒ 同一键**（评审第 4 轮必改 #2）。
    ///    **长度前缀解决不了编码前已合并的字符串**，故改为对**码元序列**编码：
    ///    取 `value` 的 UTF-16 码元（`char`，各 16 位），按**小端**写入 2 字节并转十六进制 ⇒
    ///    `Convert.ToHexString` 输出**大写**，因此写入键的载荷是**大写**十六进制（批次 15c 文档更正：
    ///    不再写"小写")。码元级编码对**任意** .NET 字符串（含孤立代理项）**单射**：不同码元序列 ⇒ 不同载荷。
    ///    **不做归一化**：载荷**不改写**、**不做 Unicode 归一化**，也不做 UTF-8 变换——键必须区分**任何**
    ///    不同字符串（含规范等价对，如 `"é"` 与 `"e"＋组合尖音符`，以及孤立代理项），否则会重新引入本修复
    ///    要消除的别名。长度前缀 ⇒ 载荷可无歧义切分（即使作用是**空串**、含 `:`／`|`／`-`，或为非 ASCII）。
    /// ③**残余别名面（如实，已收窄）**：唯一与批次 15 逐字符相同的旧形状为
    ///    `scope == null &amp;&amp; generation == null ⇒ reval-&lt;摘要&gt;`（见 <see cref="ComposeKey"/>）。
    ///    该形态与新形状 `reval-key-v2|…` 的**字面前缀不同** ⇒ 二者**不可能**互相相等，故
    ///    "伪造作用域撞摘要字段"的别名**在当前代码下构造不出来**（前版本文档所述"16 位十六进制作用域跨形状别名"
    ///    是**错误表述**，此处更正）。仍保留的边界是：摘要固定 16 位十六进制 ⇒ **两个不同稳定身份**的摘要
    ///    仍可能碰撞（见 <see cref="DocumentedHashPrefixLength"/> 的生日界披露）。
    ///    评审给出的 `(scope=h+"|x", …)` 类反例（含 `:`／`|`）已由长度前缀编码消除，见红夹具
    ///    `ReevaluationKey_DistinctScopeGenerationTriples_NeverAlias`；孤立代理项反例见
    ///    `ReevaluationKey_LoneSurrogateScope_NeverAliasesReplacementChar`。
    /// </summary>
    private static string EncodeKeyField(string? value)
    {
        if (value is null) return "-";
        var bytes = new byte[value.Length * 2];
        for (var i = 0; i < value.Length; i++)
        {
            var unit = (ushort)value[i];
            bytes[i * 2] = (byte)(unit & 0xFF);
            bytes[i * 2 + 1] = (byte)(unit >> 8);
        }
        var payload = Convert.ToHexString(bytes);
        return "=" + value.Length + ":" + payload;
    }

    /// <summary>
    /// 重评幂等键的**编码前组成部分**：稳定身份摘要（16 位小写十六进制）＋ 作用域字段 ＋ 代际字段，
    /// 均为 <see cref="EncodeKeyField"/> 的无歧义编码（见
    /// <see cref="ComposeKeyPart"/>）。
    /// </summary>
    private const string ReevaluationKeyEncodingPrefix = "reval-key-v2|";

    /// <summary>**批次 15c 新编码**：`reval-key-v2|&lt;摘要&gt;|&lt;作用域字段&gt;|&lt;代际字段&gt;`（各字段见 <see cref="EncodeKeyField"/>）。</summary>
    private string ComposeKeyPart(string stableIdentity, string? generation, string? scope)
        => ReevaluationKeyEncodingPrefix
           + DeriveReevaluationKey(stableIdentity)["reval-".Length..]
           + "|" + EncodeKeyField(scope)
           + "|" + EncodeKeyField(generation);

    /// <summary>
    /// **兼容口径（须如实声明，不得读成"与批次 15 逐字符相同"）**：`scope == null &amp;&amp; generation == null`
    /// 时**保留批次 15 的旧键形状** `reval-&lt;摘要&gt;`（唯一与批次 15 逐字符相同的情形）；
    /// 其余情况一律走 <see cref="ComposeKeyPart"/> 的无歧义编码。
    ///
    /// **为什么 `scope != null` 不再沿用旧形状**：旧形状 `reval-&lt;scope&gt;:&lt;摘要&gt;` 把作用域**原样**
    /// 拼进键，固有可构造的跨作用域别名（必改 #2），映射到同一旧形状的编码**必然**继承该别名；
    /// 因此本批**显式放弃**"带作用域的键与批次 15 逐字符相同"，以换取消歧义。
    /// 该放弃**不影响**在飞去重语义：同一实例内 `StateScope` 不可变 ⇒ 不同作用域的键**不再互相别名**（长度前缀编码），
    /// "同一等待项在同一实例内至多产一条"不变；改变的是**键的字符形状**（跨实例对账者需按新形状比对）。
    /// </summary>
    private string ComposeKey(string stableIdentity, string? generation, string? scope)
        => scope is null && generation is null
            ? DeriveReevaluationKey(stableIdentity)
            : ComposeKeyPart(stableIdentity, generation, scope);

    /// <summary>
    /// **重评幂等键**：由稳定身份确定性派生（与 <see cref="LocalWaitQueuePolicy.DeriveItemId"/> 同口径、
    /// 不同前缀：`reval-` vs `wait-`）。不读时钟、不随机、不做 I/O、不抛异常。
    ///
    /// **边界（如实）**：前缀长度 <see cref="DocumentedHashPrefixLength"/>＝64 位摘要，故「异身份 ⇒ 异键」
    /// 是**设计意图与夹具样例**，**不是**全空间的数学保证。与 `DeriveItemId` 的关系是「同摘要、异字面前缀」，
    /// 精确比较见夹具。
    /// </summary>
    public string DeriveReevaluationKey(string stableIdentity)
    {
        // 归一化口径与 LocalWaitQueuePolicy.DeriveItemId **在实测样例上一致**：null 与空串同键（不抛异常）。
        var normalized = stableIdentity ?? string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "reval-" + Convert.ToHexString(hash)[..DocumentedHashPrefixLength].ToLowerInvariant();
    }

    /// <summary>
    /// **幂等键隔离标识**（可选构造参数；默认 `null` ⇒ 与既有行为完全一致）。
    ///
    /// **为什么存在**：`_handled` 在"等待项**实例化之前**"也按稳定身份屏蔽后续产出，用途是防止同一新候选的
    /// 重复到达反复重评（**幂等，不是一次性**）。**限制（第 16 轮建议更正）**：**仅在调用方未提供可区分的代际
    /// 入参时**，同一稳定身份代表**不同代际的等待项**才会被一并屏蔽；若调用方**提供了**可区分的代际入参（见公开五参
    /// 重载 <see cref="Decide(LocalWaitReevaluationTriggerPoint, IReadOnlyList{LocalWaitItem}, DateTimeOffset, CancellationToken, string?)"/>），
    /// 则不同代际各产一条、**不会**被永久屏蔽；不提供时该永久屏蔽即为已披露的在飞去重边界。此参数把在飞去重
    /// **限定在该标识的作用域内**：
    /// ①`null`（默认）⇒ 与批内既有语义一致；
    /// ②非空 ⇒ 与稳定身份共同构成去重键 **且**写入产物的 <see cref="LocalWaitReevaluationRequest.ReevaluationKey"/>
    ///   （批次 15c 起形如 `reval-key-v2|&lt;摘要&gt;|&lt;作用域字段&gt;|&lt;代际字段&gt;`；**不是**旧的 `reval-&lt;scope&gt;:&lt;摘要&gt;` 形状），
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
    /// 判定本次触发应产出的重评请求（4 参**取消令牌**形态）。**批次 15c：改为 `private`**（不公开）。
    ///
    /// **为什么必须改**（源码层重载解析，已由真实编译器探针实证）：只要公共面**同时**存在
    /// 4 参 `CancellationToken` 与 4 参 `string? generation` 两个重载，`Decide(t, items, now, default)`
    /// 就会报 `CS0121` 歧义，`Decide(t, items, now, null)` 会被**静默改绑**到代际重载。
    /// 原始报告（评审第 3 轮必改 #1）要求的是**消除这两个 4 参形态的并存**，故两个 4 参重载**都**不公开。
    ///
    /// **可达性与兼容性（如实，已收窄）**：取消语义的唯一公共入口是 5 参主重载；`Decide(t, items, now, cts.Token)`
    /// 可经**可选第五参**绑定到主重载，故**直接调用**形式的取消契约不减损。**但不等于零源码级破坏**：
    /// 把原来的公共 4 参令牌方法赋给**四参数委托**（方法组转换）不再能绑定（编译器不用可选参数补齐委托签名）。
    /// 本组件原为**未接线**组件，仓库内无任何调用点／委托绑定（送审 diffs 内已核实）；生产接线方若需委托适配，
    /// 应显式包一层 lambda 调 5 参主重载。
    /// </summary>
    private LocalWaitReevaluationDecision Decide(LocalWaitReevaluationTriggerPoint trigger,
        IReadOnlyList<LocalWaitItem>? items, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => Decide(trigger, items, nowUtc, cancellationToken, null);

    /// <summary>
    /// 判定本次触发应产出的重评请求（4 参**代际**形态）。**批次 15c：改为 `private`**（不公开）。
    ///
    /// **为什么不公开**：与同形的 4 参 `CancellationToken` 重载并存会造成 `Decide(t, items, now, default)`
    /// 的 `CS0121` 歧义与 `Decide(t, items, now, null)` 的静默改绑（评审第 3 轮必改 #1；已用真实编译器探针实证）。
    /// 代际的公共入口是 **5 参主重载**：`Decide(t, items, nowUtc, CancellationToken.None, generation)`
    /// （或命名实参 `generation:`）。本重载保留为**内部实现细节**：实现体走 `CancellationToken.None`。
    /// </summary>
    private LocalWaitReevaluationDecision Decide(LocalWaitReevaluationTriggerPoint trigger,
        IReadOnlyList<LocalWaitItem>? items, DateTimeOffset nowUtc, string? generation)
        => Decide(trigger, items, nowUtc, CancellationToken.None, generation);

    /// <summary>
    /// 判定本次触发应产出的重评请求。
    ///
    /// **取消规则**：<paramref name="cancellationToken"/> 已取消 ⇒ **空集**（且**不**消费幂等键：
    /// 取消不算"已处理"，取消后仍可再次评估）。
    /// **安全网规则**：<see cref="LocalWaitReevaluationTriggerPoint.SafetyNet"/> 且注入判定给出"未到期"
    /// ⇒ **空集**（不消费幂等键：未到期不是"已处理"）。
    /// **幂等规则（如实口径）**：只对 `Waiting` 项产请求；同批同（稳定身份＋代际）只产一条；**本实例内**
    /// 已产过的（稳定身份＋代际）不再产。
    ///
    /// **代际（批次 15b 修复 #1／#4）**：<paramref name="generation"/> 是**显式的等待项代际**，与稳定身份
    /// 共同构成去重键 ⇒ **同一实例内**取消后以**新代际**重新等待**可以重新产出**（不再被实例级永久屏蔽）；
    /// 同一代际重复触发仍**不重复产**。`null`／空白 ⇒ 沿用批次 15 的**无代际**去重（"同一实例内已产过即不再产"
    /// 仍成立）。**键形状（批次 15c 更正）**：仅 `generation == null &amp;&amp; StateScope == null` 时与批次 15
    /// **逐字符相同**；带代际（或带作用域）⇒ 走 `reval-key-v2|…` 的**长度前缀编码**，不再用 `|` 原样拼接
    /// （见 <see cref="ComposeKeyPart"/>；此为消除评审第 3 轮必改 #2 的键别名所必需）。代际值**必须**来自
    /// 权威的等待项代际／租约纪元，**不得**用本地时间戳或随机值（否则等同关闭幂等）；本批**不接线**，
    /// 不实现任何代际来源。
    ///
    /// **契约边界（如实，含批次 15b 修复与更正）**：
    /// ①[批次 15b 修复 #2]**枚举与校验先于占键**：本方法先完整枚举 `items`、逐项校验并构造出完整候选，
    ///   **之后**才 `TryAdd` 占键；故枚举期间抛出的异常（含调用方自定义 `IReadOnlyList` 的 `MoveNext`）
    ///   **不消费任何键**。占键之后到 `return` 之间只剩内存操作（无 I/O、无 await、无用户代码），
    ///   但 `TryAdd` 仍是**状态预留**而**不**等于"请求已被交付／已被消费方接收"——登记**先于**调用方看到产物。
    /// ②去重只在**本实例进程内**成立，**不**跨实例、跨进程、跨重启（多实例或重启可重复产）。
    /// ③[批次 15b 修复 #3，批次 15d 补强]**产出侧校验身份一致性 + 值快照冻结**：`item.ItemId` 与
    ///   `LocalWaitQueuePolicy.DeriveItemId(item.StableIdentity)` **不一致 ⇒ 本项不产请求、不占键**
    ///   （改正后可重试）。这是必要的：`ItemId` 可写，产物指向错误等待项时预留**不可撤销**，
    ///   接收方的事后校验**撤销不了**已作出的预留。
    ///   **[批次 15d 修复 #1／#2]** 原实现只在枚举期校验，随后仍从**可变引用**读取产物字段，且**同一元素内**
    ///   会**重复读取**（先读校验、再读派生键与候选），占键在读取**之前** ⇒ 任一读值窗口内被改写都会产出
    ///   不一致请求且已占键。现改为：**每个元素只读一次**，把 `ItemId`／`StableIdentity`／`CandidateId`
    ///   读入局部值，**校验、派生键、构造候选全部只用同一组局部值** ⇒ 校验过的取值即产物取值。
    ///   **仍不声称**：快照**之后**对原 `LocalWaitItem` 的改写不再影响任何结论（本组件不持引用、不读回）；
    ///   `StableIdentity` 的**编码口径**仍归 `DeriveItemId`（见 ⑤）。
    /// ⑤[批次 15d]**摘要路径的已披露等价面（可复现，非概率）**：本类 `DeriveReevaluationKey` 对
    ///   `StableIdentity` 调 `Encoding.UTF8.GetBytes` 再取 SHA-256 前缀，而 UTF-8 编码**不是**对任意
    ///   .NET 字符串的单射（孤立代理项被替换字符 U+FFFD 取代）⇒ `"a\uD800"` 与 `"a\uFFFD"` 两个**不同**
    ///   `StableIdentity` 得到**同一**摘要、**同一**键。**本组件确实在产出侧校验身份一致性**（见 ③）：校验用的
    ///   期望值由本类按**同一**摘要口径派生，故两个等价身份**各自都能通过**校验（等价面**不是**校验漏检）⇒
    ///   若上游把这两个身份都写入等待项，同批内只产**先出现**的一项，跨调用则后者被在飞去重屏蔽。
    ///   **与 <see cref="LocalWaitQueuePolicy.DeriveItemId"/> 的内部实现细节不在本文档声明范围内**（本次材料未含
    ///   其源码）；只按实测行为陈述：夹具显示二者在样例身份集上"同摘要、仅字面前缀不同"。该等价面与 64 位
    ///   截断的**概率**碰撞（见 <see cref="DocumentedHashPrefixLength"/>）必须**分开**表述。
    ///   **合并的确切条件（评审第 7 轮重要 #3）**：①按 **`ItemId`** 对账/幂等 ⇒ 恒会合并（与作用域、代际**无关**）；
    ///   ②按 **`ReevaluationKey`** 对账/幂等 ⇒ 仅当**同作用域且同代际**时键相同才合并；更换作用域或代际只能
    ///   **区分重评键**、**不能**区分既有 `ItemId`。
    ///   接线约束：必须假定 `StableIdentity` 来自**权威身份面**（不含孤立代理项等非正规码元）。
    ///   夹具：`ReevaluationKey_StableIdentityDigest_Utf8Equivalence面_已披露_且ItemId校验同口径`。
    /// ④取消令牌只在进入时检查一次：遍历过程中令牌转为已取消**不会**中断本次求值；
    ///   `item.State` 等在遍历中被其它线程修改属共享可变状态竞态边界，本组件不做快照。
    /// ⑥[批次 15d／第 9 轮重要 #1]**已登记的共享可变输入风险（未解决，接线前须处置）**：本组件把三个字段
    ///   **各读一次**存为局部值，但**顺序读取不是原子快照**。产出侧一致性校验**只**覆盖 `ItemId` 与
    ///   `StableIdentity` 的互相失配，**挡不住"混代"**——例如并发线程在两次读之间把该项整体改成另一代际，
    ///   则 `ItemId`／`StableIdentity` 取自旧代、`CandidateId` 可能取自新代，产物会携带**跨代候选号**
    ///   （身份自洽但候选号不同代；键按旧代占位）。**故本组件要求调用方传入"字段值已冻结、且与权威代际一致"
    ///   的输入**，或保证同一等待项在处理期内不被并发改写（第 10 轮重要 #2 更正：**仅把含可变
    ///   `LocalWaitItem` 实例的集合包成 `IReadOnlyList` 不足以排除该反例**——`IReadOnlyList` 只约束集合本身，
    ///   不冻结元素的字段值）；本批**不接线、不解决**，亦不把该风险表述为"已被校验拒绝"。
    /// **本方法的产物永远只表达"须重新走一次完整准入"**：不产生发送、不含发送许可、不推进执行。
    /// </summary>
    public LocalWaitReevaluationDecision Decide(LocalWaitReevaluationTriggerPoint trigger,
        IReadOnlyList<LocalWaitItem>? items, DateTimeOffset nowUtc, CancellationToken cancellationToken,
        string? generation = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return Empty(trigger, "触发求值被取消：不产重评请求（幂等键未消费）");

        if (trigger == LocalWaitReevaluationTriggerPoint.SafetyNet && !_safetyNetDue(nowUtc))
            return Empty(trigger, "安全网未到期（注入式判定）：不产重评请求（幂等键未消费）");

        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
        var effectiveGeneration = NormalizeGeneration(generation);

        // 批次 15b 修复 #2／#1：#1 先**完整枚举并构建全部候选**，再一次性占键。
        // 枚举期间任何异常（含调用方自定义 IReadOnlyList 的 MoveNext 抛异常）都发生在占键之前
        // ⇒ 不消费任何幂等键，也不留下"键已占、决策未交付"的状态。
        var stateScope = StateScope;
        var candidates = new List<Pending>();

        foreach (var item in items ?? [])
        {
            if (item is null) continue;

            // 批次 15d 修复 #2（评审第 7 轮必改 #1／第 8 轮重要 #1 收窄）：原实现先读 `ItemId`／
            // `StableIdentity` 校验，随后又**重新读**同一可变项去派生键与构造候选 ⇒ 两次读之间（并发改写）
            // 可产出 `ItemId` 与 `StableIdentity` **失配**且已占键的请求。现把参与校验、派生键与候选构造的
            // 三个字段**各读一次**、存为局部值，后续**只用这组局部值**。
            // **不宜声称"单元素处理期内不再有可变读取窗口"**：①`item.State` 仍在本行之后单独读取；
            // ②多个字段的顺序读取本身**不是原子快照**——并发改写落在两次读取之间时，局部值仍可能互相失配。
            // **第 9 轮重要 #1 更正（不得过度声称校验的覆盖范围）**：下面的一致性校验**只**拒绝
            // `ItemId` 与 `StableIdentity` 二者互相失配；它**挡不住**"混代"情形——例如先读到身份 A 的
            // `ItemId`／`StableIdentity`，并发线程把该项整体改成身份 B，再读到 B 的 `CandidateId`：
            // 两个身份字段取自 A ⇒ 校验通过，`CandidateId` 却取自 B，产物携带**跨代候选号**。
            // 该风险属**已登记的共享可变输入风险**（见 <see cref="Decide(LocalWaitReevaluationTriggerPoint, IReadOnlyList{LocalWaitItem}, DateTimeOffset, CancellationToken, string?)"/>
            // 契约"并发"条），**须在接线前由调用方按权威代际/只读快照输入解决**，本批不接线、不解决。
            // （仍不声称：局部值快照之后对原项的改写无影响——本组件之后**不再读**该引用。）
            var itemId = item.ItemId;
            var stableIdentity = item.StableIdentity;
            var candidateId = item.CandidateId;

            if (item.State != LocalWaitItemState.Waiting) continue;
            if (string.IsNullOrWhiteSpace(stableIdentity)) continue;

            // 批次 15b 修复 #3：**产出侧校验身份一致性**——`ItemId` 是可写字段，若与 `StableIdentity`
            // 的派生结果不一致，产物会指向**错误**的等待项，而预留**不可撤销**（接收方校验撤销不了）。
            // 故不一致 ⇒ 本项**不参与产出**（不占键、不产请求）；改正后可重试。
            if (!string.Equals(itemId, LocalWaitQueuePolicy.DeriveItemId(stableIdentity), StringComparison.Ordinal))
                continue;

            // 批次 15b：外置作用域与显式「等待项代际」是**两条独立**的隔离通道。作用域**仍**参与织入去重键
            // （但自批次 15c 起**改走长度前缀编码**，不再与批次 15 的旧形状逐字符一致）；代际是**新增**的显式通道。
            var itemScope = stateScope;
            var itemGeneration = effectiveGeneration;
            // 批次 15b 修复 #1/#4：#4 去重键显式携带**等待项代际**（不再依赖构造后不可变的 StateScope）
            // ⇒ 同一实例内取消后以**新代际**重新等待可重新产出，同一代际重复触发仍不重复产。
            var key = ComposeKey(stableIdentity, itemGeneration, itemScope);

            // 同批去重：同一等待项在同一批里只产一条（不按条目数放大）——同样在占键之前完成。
            if (!seenInBatch.Add(key)) continue;

            candidates.Add(new Pending(key, itemGeneration, itemScope, itemId, stableIdentity, candidateId));
        }

        // 到这里已无枚举／校验失败点：开始**原子预留**（TryAdd 单次操作）。
        var requests = new List<LocalWaitReevaluationRequest>();
        foreach (var pending in candidates)
        {
            if (!_handled.TryAdd(pending.Key, 0)) continue;   // 并发下由 TryAdd 保证恰好一条
            requests.Add(new LocalWaitReevaluationRequest
            {
                ItemId = pending.ItemId,
                StableIdentity = pending.StableIdentity,
                CandidateId = pending.CandidateId,
                Trigger = trigger,
                ReevaluationKey = pending.Key,
                Generation = pending.Generation,
            });
        }

        return new LocalWaitReevaluationDecision
        {
            Trigger = trigger,
            Requests = requests,
            Reason = requests.Count == 0
                ? "无可重评的等待项（空集或均已处理/已取消/身份不一致）"
                : "已产出重评请求：" + requests.Count + " 条（须重新走完整准入；本组件不产生发送）",
        };
    }

    /// <summary>
    /// 占键候选（枚举与校验完成后的中间载体；不对外暴露）。
    ///
    /// **批次 15d 修复 #1（评审第 6 轮必改 #1）**：此载体保存的是**校验时读到的字段值快照**
    /// （`ItemId`／`StableIdentity`／`CandidateId`），**不再保存 `LocalWaitItem` 引用**。
    /// 原实现持引用，并在**占键之后**才从该引用读取字段 ⇒ 若在枚举与读取之间（如自定义
    /// `IReadOnlyList` 的 `MoveNext` 中）另一线程改写了 `ItemId`，会产出 `ItemId` 与
    /// `StableIdentity` **不一致**、却已**不可撤销**占键的请求，违反「不一致 ⇒ 不产、不占键」契约。
    /// 改为值快照后，校验通过即**冻结**产物取值：占键与产物写入只用校验过的同一批值。
    /// </summary>
    private readonly record struct Pending(
        string Key,
        string? Generation,
        string? Scope,
        string ItemId,
        string StableIdentity,
        string CandidateId);

    /// <summary>
    /// 代际入参归一化：空白／null ⇒ **`null`**（`null`＝沿用批次 15 的**无代际**去重口径，
    /// 不是空串；空白代际与 `null` 同键、不会与任何**显式**代际混同，因为显式代际**不得**为空白——见 <see cref="Decide(LocalWaitReevaluationTriggerPoint, IReadOnlyList{LocalWaitItem}, DateTimeOffset, CancellationToken, string?)"/>）。
    /// </summary>
    private static string? NormalizeGeneration(string? generation)
        => string.IsNullOrWhiteSpace(generation) ? null : generation;


    private static LocalWaitReevaluationDecision Empty(LocalWaitReevaluationTriggerPoint trigger, string reason)
        => new() { Trigger = trigger, Requests = [], Reason = reason };
}
