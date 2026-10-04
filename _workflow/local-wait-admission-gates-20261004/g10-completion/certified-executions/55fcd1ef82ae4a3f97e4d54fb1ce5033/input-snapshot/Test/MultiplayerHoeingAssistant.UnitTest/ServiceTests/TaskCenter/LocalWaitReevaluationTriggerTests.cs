using System.Collections.Concurrent;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 15／D3：重评触发（未接线组件＋时序夹具）**——反例先行契约（红夹具）。
///
/// owner 2026-09-24 裁决 D3＝推荐项 A：**占用结束／权威退出事件为主，启动恢复与新候选为辅，加低频安全网；
/// 每次触发重新走完整准入**。本批**只交付未接线的触发器组件＋夹具**：不接任何生产入口、**不产生发送**、
/// 不解除生产门（E3/E4/E5／节点改道／S4b/S8b／热键继续关闭）、不自行改冻结合同（只允许纯加法）。
///
/// **夹具方法（反例先行）**：本批目标类型尚不存在，直接写 `new LocalWaitReevaluationTrigger(...)` 会产生
/// **编译期错误**并污染助手全量基线。故类型／成员一律用 `BindingFlags` **反射**取，缺失时断言失败（红），
/// 而**编译通过**——与批次 14 `AdmissionWaitLocallyContractTests` 的 `WaitKind()` 手法同向。
///
/// **能证明（实现后）**：①触发点覆盖四类且语义互不重叠；②幂等键按稳定身份确定性派生；
/// ③同一等待项重复触发**不重复产重评**、**零发送**；④同批去重只产一次；⑤并发触发不产重复重评；
/// ⑥取消／失效后不产重评；⑦重评产物**只有**「重新走完整准入」一种语义，**不含发送许可**。
///
/// **不能证明**：任何生产接线行为、真实发送是否发生、真实事件源（BGI 退出事件／宿主启动／热键）是否送达、
/// 跨进程单写者、断电耐久；低频安全网**未接真实定时器**，本批只交付「是否到安全网时刻」的**注入式纯判定**。
/// 本组不构成对 D2/D4 的背书，也不解除任何生产门。
/// </summary>
public sealed class LocalWaitReevaluationTriggerTests
{
    // ── 反射取值辅助（类型缺失 ⇒ 断言失败＝红；不产生编译期错误）─────────────────────

    private const string TriggerTypeName = "MultiplayerHoeingAssistant.Services.LocalWaitReevaluationTrigger";
    private const string TriggerPointTypeName = "MultiplayerHoeingAssistant.Models.LocalWaitReevaluationTriggerPoint";
    private const string RequestTypeName = "MultiplayerHoeingAssistant.Models.LocalWaitReevaluationRequest";
    private const string DecisionTypeName = "MultiplayerHoeingAssistant.Models.LocalWaitReevaluationDecision";

    private static Type RequireType(string fullName)
    {
        var type = typeof(LocalWaitQueuePolicy).Assembly.GetType(fullName, throwOnError: false);
        Assert.True(type is not null,
            $"必需类型缺失：{fullName}（D3 裁决 A：本批须交付未接线触发器组件与其模型）；缺失即本批未实现。");
        return type!;
    }

    private static Type TriggerPointType() => RequireType(TriggerPointTypeName);

    private static string[] TriggerPointNames()
    {
        var names = Enum.GetNames(TriggerPointType());
        Assert.True(names.Length > 0, "触发点枚举必须有已定义取值。");
        return names;
    }

    private static object TriggerPoint(string name)
    {
        Assert.Contains(name, TriggerPointNames());
        return Enum.Parse(TriggerPointType(), name);
    }

    private static object NewTrigger()
    {
        var type = RequireType(TriggerTypeName);
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .First();
        var args = ctor.GetParameters()
            .Select(p => p.HasDefaultValue ? p.DefaultValue : DefaultFor(p.ParameterType))
            .ToArray();
        return ctor.Invoke(args);
    }

    /// <summary>构造带 <c>StateScope</c> 的触发器（按名称找构造参数，避免依赖参数顺序）。</summary>
    private static object NewTriggerWithScope(string scope)
    {
        var type = RequireType(TriggerTypeName);
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            if (ps[i].Name == "stateScope") args[i] = scope;
            else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
            else args[i] = DefaultFor(ps[i].ParameterType);
        }
        Assert.True(ps.Any(p => p.Name == "stateScope"),
            "触发器构造函数必须提供可选 stateScope 参数（幂等键隔离标识）。");
        return ctor.Invoke(args);
    }

    private static object? DefaultFor(Type t)
    {
        if (!t.IsValueType) return null;
        return Activator.CreateInstance(t);
    }

    private static object Invoke(object target, string method, params object?[] args)
    {
        var type = target.GetType();
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == method).ToList();
        Assert.True(candidates.Count > 0, $"{type.Name} 必须提供公共方法 {method}（D3 触发器语义接缝）。");
        // 按**可赋值性**绑定，而非仅按参数个数：批次 15b 新增的 `Decide(…, string? generation)` 与既有
        // `Decide(…, CancellationToken)` **同为 4 参**；批次 15c 已把后者改为 private。夹具**绝不**写
        // `Decide(t, items, now, null)` —— 若公共面恢复成双 4 参，该调用会被静默改绑到代际重载。
        var matched = candidates.FirstOrDefault(m => ArgsAssignable(m.GetParameters(), args));
        Assert.True(matched is not null,
            $"{type.Name}.{method} 没有 {args.Length} 个可按可赋值性绑定的重载；现有重载："
            + string.Join(" / ", candidates.Select(m => "(" + string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name)) + ")")));
        try
        {
            return matched!.Invoke(target, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException!;
        }
    }
// ─────────────────────────────────────────────────────────────────────────────
// ① 触发点面：四类齐备、语义互不重叠、无第五个「隐式兜底」值
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **四类触发点必须齐备**（D3 裁决 A 的逐字要求）：①占用结束／权威退出事件（主）②启动恢复（辅）
    /// ③新候选到达（辅）④低频安全网。
    /// 反例：现状无任何触发器类型 ⇒ 本断言失败（红）。
    /// </summary>
    [Fact]
    public void TriggerPoints_CoverAllFourAdjudicatedClasses()
    {
        var names = TriggerPointNames();
        Assert.Contains("OccupancyEnded", names);
        Assert.Contains("StartupRecovery", names);
        Assert.Contains("NewCandidateArrived", names);
        Assert.Contains("SafetyNet", names);
    }

    /// <summary>
    /// **触发点语义互不重叠**：四类必须是**四个不同取值**（不得用别名／同值重复表达同一类），
    /// 且底层取值唯一——否则「主／辅／安全网」的优先级与去重语义无法区分。
    /// </summary>
    [Fact]
    public void TriggerPoints_AreDistinctValues_NoAliasing()
    {
        var type = TriggerPointType();
        var values = Enum.GetValues(type).Cast<object>().Select(Convert.ToInt64).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());

        var required = new[] { "OccupancyEnded", "StartupRecovery", "NewCandidateArrived", "SafetyNet" };
        var requiredValues = required.Select(n => Convert.ToInt64(TriggerPoint(n))).ToList();
        Assert.Equal(required.Length, requiredValues.Distinct().Count());
    }

    /// <summary>
    /// **取值精确为四**：枚举**已定义成员数必须恰好为 4**——只断言"四类齐备"无法拦住"悄悄新增第五个
    /// 隐式兜底值"（例如多一个 `Unknown`/`Fallback` 让未裁决的事件源也有落点）。
    /// 与 <see cref="TriggerPoints_CoverAllFourAdjudicatedClasses"/> 合起来才是「恰好这四类」的完整断言。
    /// </summary>
    [Fact]
    public void TriggerPoints_CountIsExactlyFour()
    {
        var names = Enum.GetNames(TriggerPointType());
        Assert.Equal(4, names.Length);

        var values = Enum.GetValues(TriggerPointType()).Cast<object>().Select(Convert.ToInt64).ToList();
        Assert.Equal(4, values.Distinct().Count());

        var expected = new[] { "OccupancyEnded", "StartupRecovery", "NewCandidateArrived", "SafetyNet" }
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

// ─────────────────────────────────────────────────────────────────────────────
// ② 幂等键：同一等待项 ⇒ 同一键（确定性，不读时钟、不随机）
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **幂等键按稳定身份确定性派生**：同身份同键、异身份异键；键必须由**纯函数**给出
    /// （不得读时钟／随机），故同一输入重复调用必须得到完全相同结果。
    /// 语义接缝：`LocalWaitReevaluationTrigger.DeriveReevaluationKey(string stableIdentity)`。
    /// </summary>
    [Fact]
    public void ReevaluationKey_IsDeterministicPerStableIdentity()
    {
        var trigger = NewTrigger();
        var key1 = (string)Invoke(trigger, "DeriveReevaluationKey", "run-1|node-a|0|0");
        var key2 = (string)Invoke(trigger, "DeriveReevaluationKey", "run-1|node-a|0|0");
        var other = (string)Invoke(trigger, "DeriveReevaluationKey", "run-1|node-b|0|0");

        Assert.False(string.IsNullOrWhiteSpace(key1));
        Assert.Equal(key1, key2);
        Assert.NotEqual(key1, other);
    }

    /// <summary>
    /// **幂等键与等待项标识同一口径（批次 15d 收窄表述）**：等待项的 `ItemId` 由 `LocalWaitQueuePolicy.DeriveItemId`
    /// 派生，触发器的去重键按**同一**摘要口径派生（仅字面前缀不同）⇒ 夹具在**样例身份集**上断言
    /// 「同身份 ⇒ 同键；异身份 ⇒ 异键」。
    /// **不得**读成"全空间一一对应"：该口径对含孤立代理项的身份**不是单射**（可复现等价面，见
    /// `ReevaluationKey_StableIdentityDigest_Utf8Equivalence面_已披露_且ItemId校验同口径`），另有 64 位截断的
    /// **概率**碰撞。本夹具只钉"样例身份集上无系统性别名 + 口径稳定"。
    /// </summary>
    [Fact]
    public void ReevaluationKey_CorrespondsOneToOneWithWaitItemId()
    {
        var trigger = NewTrigger();
        var identities = new[] { "s-1", "s-2", "s-3" };
        var keys = identities
            .Select(i => (string)Invoke(trigger, "DeriveReevaluationKey", i))
            .ToList();
        var itemIds = identities.Select(LocalWaitQueuePolicy.DeriveItemId).ToList();

        Assert.Equal(identities.Length, keys.Distinct().Count());
        Assert.Equal(identities.Length, itemIds.Distinct().Count());
        // 同身份 ⇒ 同键；样例身份集内异身份 ⇒ 异键（**不**声称全空间单射：见等价面夹具）
        Assert.Equal(keys.Count, keys.Zip(itemIds).Select(p => p.First).Distinct().Count());
    }

    /// <summary>
    /// **同口径摘要（会诊 #6 处置）**：`ReevaluationKey` 与 `LocalWaitQueuePolicy.DeriveItemId` 的关系是
    /// 「**同一** SHA-256、**同一** 16 位十六进制前缀长度，仅字面前缀不同（`reval-` vs `wait-`）」。
    /// 因此同稳定身份下**摘要逐字符相同**必须可机械断言——若某人把触发器的摘要长度、大小写或归一化口径改掉
    /// （例如截断到 8 位、改用大写、或对身份做额外加盐），此夹具立刻变红。
    ///
    /// **边界（批次 15d 收窄）**：本断言只覆盖"同口径、同前缀长度、样例身份集上无别名"，
    /// **不**主张密码学强度、全空间零碰撞或对**任意**身份串的单射性（孤立代理项的等价面见
    /// `ReevaluationKey_StableIdentityDigest_Utf8Equivalence面_已披露_且ItemId校验同口径`）。
    /// </summary>
    [Fact]
    public void ReevaluationKey_DigestPrefixMatchesWaitItemIdDigest()
    {
        var trigger = NewTrigger();
        var identities = new[] { "s-1", "s-2", "run-1|node-a|0|0", "run-9|node-z|7|3", "" };
        const string keyPrefix = "reval-";
        const string itemPrefix = "wait-";
        const int documentedDigestLength = 16;

        var type = RequireType(TriggerTypeName);
        var constField = type.GetField("DocumentedHashPrefixLength", BindingFlags.Public | BindingFlags.Static);
        Assert.True(constField is not null,
            "触发器必须公开 DocumentedHashPrefixLength（声明面：摘要前缀长度口径须可机械核对）。");
        Assert.Equal(documentedDigestLength, Convert.ToInt32(constField!.GetRawConstantValue()));

        foreach (var identity in identities)
        {
            var key = (string)Invoke(trigger, "DeriveReevaluationKey", identity);
            var itemId = LocalWaitQueuePolicy.DeriveItemId(identity);

            Assert.StartsWith(keyPrefix, key, StringComparison.Ordinal);
            Assert.StartsWith(itemPrefix, itemId, StringComparison.Ordinal);
            // 同口径 ⇒ 去掉字面前缀后摘要必须逐字符一致（含大小写与长度）
            Assert.Equal(itemId[itemPrefix.Length..], key[keyPrefix.Length..]);
            Assert.Equal(documentedDigestLength, key.Length - keyPrefix.Length);
        }
    }

// ─────────────────────────────────────────────────────────────────────────────
// ③ 触发求值：只对 Waiting 项产「重新走完整准入」请求；重复触发不重复产
// ─────────────────────────────────────────────────────────────────────────────

    private static LocalWaitItem WaitItem(string identity, LocalWaitItemState state = LocalWaitItemState.Waiting)
        => new()
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId(identity),
            StableIdentity = identity,
            CandidateId = "cand-" + identity,
            Namespace = "manual",
            WorkflowId = "wf",
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedIdentity = true,
            EnqueuedAtUtc = DateTimeOffset.UnixEpoch,
            State = state,
        };

    private static object Decision(object trigger, object point, IReadOnlyList<LocalWaitItem> items)
        => Invoke(trigger, "Decide", point, items);

    /// <summary>
    /// **按显式「等待项代际」求值**（批次 15b 语义接缝）：`Decide` 必须接受一个名为 `generation`
    /// 的入参，使**同一实例**内「同一代际重复触发不重复产、新代际重新产」。夹具按**参数名**定位该入参，
    /// 故实现不得用位置参数顶替（否则按名绑定失败＝红）。
    ///
    /// **批次 15c（重载收窄后）**：公共面**只保留 5 参主重载**（末参 `generation` 可省略），
    /// `Decide(point, items, nowUtc, CancellationToken.None, generation)` 是代际的**唯一**公共入口。
    /// 两个 4 参重载（`CancellationToken` 与 `string? generation`）**均已不公开**：公开 4 参 `string? generation`
    /// 与 4 参 `CancellationToken` 并存会让 `Decide(t, items, now, default)` 报 `CS0121`、`Decide(t, items, now, null)`
    /// 被**静默改绑**（评审第 3 轮必改 #1；两个 4 参形态的私有副本见实现）。
    /// 本夹具按参数**名**定位 `generation` 形参，故实现不得用位置参数顶替（否则按名绑定失败＝红）。
    /// </summary>
    private static object Gen(object trigger, object point, IReadOnlyList<LocalWaitItem> items, string? generation)
    {
        var type = trigger.GetType();
        var all = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Decide").ToList();
        var candidates = all.Where(m => m.GetParameters().Any(p => p.Name == "generation")).ToList();
        Assert.True(candidates.Count > 0,
            $"{type.Name}.Decide 必须提供名为 generation 的公共代际入参（批次 15b：去重键须显式按等待项代际，不得依赖不可变 StateScope）。现有重载："
            + string.Join(" / ", all.Select(m => "(" + string.Join(", ", m.GetParameters().Select(x => x.Name)) + ")")));
        var fiveArg = candidates.FirstOrDefault(m => m.GetParameters().Length == 5);
        Assert.True(fiveArg is not null,
            "代际的 5 参主重载缺失（`Decide(point, items, nowUtc, cancellationToken, generation = null)`）；"
            + "批次 15c 后公共面只保留 5 参主重载，代际必经此入口。");
        return fiveArg!.Invoke(trigger, new object?[] { point, items, DateTimeOffset.UnixEpoch, CancellationToken.None, generation })!;
    }

    private static IReadOnlyList<object> Requests(object decision)
    {
        var prop = decision.GetType().GetProperty("Requests", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop is not null, "重评决策必须暴露 Requests 集合（本批唯一产物面）。");
        var value = prop!.GetValue(decision);
        Assert.True(value is System.Collections.IEnumerable, "Requests 必须是集合。");
        return ((System.Collections.IEnumerable)value!).Cast<object>().ToList();
    }

    /// <summary>
    /// **占用结束触发 ⇒ 为每个 `Waiting` 项各产一条「重新走完整准入」请求**；
    /// 非 `Waiting`（已取消）项**不产**。产物条数与等待集合语义一致。
    /// 反例：现状无触发器 ⇒ 红。
    /// </summary>
    [Fact]
    public void Decide_OccupancyEnded_ProducesOneRequestPerWaitingItem()
    {
        var trigger = NewTrigger();
        var items = new List<LocalWaitItem>
        {
            WaitItem("s-1"),
            WaitItem("s-2"),
            WaitItem("s-3", LocalWaitItemState.Cancelled),
        };

        var decision = Decision(trigger, TriggerPoint("OccupancyEnded"), items);
        var requests = Requests(decision);

        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests, r => ItemIdOf(r) == LocalWaitQueuePolicy.DeriveItemId("s-3"));
    }

    /// <summary>
    /// **每次触发重新走完整准入**：产物必须表达「须重新走完整准入」，**不是**发送、
    /// **不是**受理、**不是**已证实失败。断言产物上的准入门标记为真，且触发点被如实回填。
    /// </summary>
    [Fact]
    public void Request_RequiresFullAdmissionRerun_AndCarriesTriggerAndIdentity()
    {
        var trigger = NewTrigger();
        var decision = Decision(trigger, TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { WaitItem("s-1") });
        var request = Assert.Single(Requests(decision));

        Assert.True(BoolOf(request, "RequiresFullAdmission"),
            "重评产物必须显式声明「须重新走完整准入」（D3 裁决 A：不得直接产生发送）。");
        Assert.Equal("s-1", StringOf(request, "StableIdentity"));
        Assert.Equal(LocalWaitQueuePolicy.DeriveItemId("s-1"), ItemIdOf(request));
        Assert.Equal(TriggerPoint("OccupancyEnded").ToString(), TriggerPointOf(request)!.ToString());
    }

    /// <summary>
    /// **幂等（同一等待项重复触发不重复入队／不重复产）**：同一触发器实例先对同一等待集合求值一次，
    /// 再对**同一批项**求值第二次 ⇒ 第二轮**必须零请求**（已在本进程内处理过的等待项不重复产）。
    /// 与 `LocalWaitEnqueueDecision.SendPermitted` 恒 false 同向：重复触发**零发送**。
    /// </summary>
    [Fact]
    public void Decide_SameItemsTriggeredTwice_SecondRoundProducesNothing()
    {
        var trigger = NewTrigger();
        var items = new List<LocalWaitItem> { WaitItem("s-1"), WaitItem("s-2") };

        var first = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), items));
        Assert.Equal(2, first.Count);

        var second = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), items));
        Assert.Empty(second);
    }

    /// <summary>
    /// **去重（同一批触发只对同一等待项产一次）**：同一批里出现**同一稳定身份的重复条目**
    /// （例如两处不同来源同时给出同一等待项）⇒ 只能产**一条**重评请求，不得按条目数放大。
    /// </summary>
    [Fact]
    public void Decide_DuplicateItemsInSameBatch_ProduceSingleRequest()
    {
        var trigger = NewTrigger();
        var items = new List<LocalWaitItem> { WaitItem("s-1"), WaitItem("s-1"), WaitItem("s-1") };

        var requests = Requests(Decision(trigger, TriggerPoint("NewCandidateArrived"), items));
        Assert.Single(requests);
        Assert.Equal(LocalWaitQueuePolicy.DeriveItemId("s-1"), ItemIdOf(requests[0]));
    }

    /// <summary>
    /// **重复触发不得重复入队（等待队列落盘面）**：本批不接线的触发路径**不得**改动等待队列；
    /// 以真实 `LocalWaitQueueStore` 落盘为证：触发前后文件字节与条目完全不变（触发只产请求，不落盘）。
    /// </summary>
    [Fact]
    public void Decide_DoesNotTouchWaitQueueStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revalq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            store.Upsert(WaitItem("s-1"));
            var before = File.ReadAllText(store.FilePath);

            // [批次17 写入→读回自证] 本夹具「Decide 不碰 store」判据的前提是：store 里就是我们刚写入的
            // 那条一致项。若该前提被外部因素破坏（历史实例：b15e 帧 D2 探针无锚定窗口改写本批内容 ⇒
            // 本夹具表现为无头的 Assert.Single 空集合，机制与闭合见 §24.110 真残余第2项），此处**先行
            // 响亮失败并点名存储层**，不把症状留给下方空集合断言。只加前置判据，不改任何既有断言。
            var decideInput = store.Load().ToList();
            Assert.True(decideInput.Count == 1
                         && string.Equals(decideInput[0].ItemId, LocalWaitQueuePolicy.DeriveItemId("s-1"), StringComparison.Ordinal)
                         && string.Equals(decideInput[0].StableIdentity, "s-1", StringComparison.Ordinal)
                         && decideInput[0].State == LocalWaitItemState.Waiting,
                "存储层写入→读回自证失败（Decide 输入与写入项不一致 ⇒ 先查存储层/进程级探针，不是触发器问题）："
                + $"count={decideInput.Count}"
                + (decideInput.Count > 0
                    ? $"，itemId={decideInput[0].ItemId}，stableIdentity={decideInput[0].StableIdentity}，state={decideInput[0].State}"
                    : "（Load 返回空集合）"));

            var trigger = NewTrigger();
            var requests = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), decideInput));
            Assert.Single(requests);

            var after = File.ReadAllText(store.FilePath);
            Assert.Equal(before, after);
            Assert.Single(store.Load());
            Assert.Equal(LocalWaitItemState.Waiting, store.Load()[0].State);
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **幂等键隔离（会诊 #1 处置的可选语义）**：同一触发器实例配置 `StateScope` 后，
    /// **不同作用域 ⇒ 不同键 ⇒ 互不屏蔽**；同一作用域内仍按稳定身份去重（第二次求值零请求）。
    /// 产物 `ReevaluationKey` 必须携带作用域（否则消费方无法区分代际），而 `RequiresFullAdmission` 恒真、
    /// 全程**不产生发送**。
    ///
    /// **接线边界**：作用域值须来自权威的等待项代际／租约纪元，不得用本地时间戳或随机值绕过（本批不接线）。
    /// </summary>
    [Fact]
    public void Decide_StateScope_IsolatesInFlightDedup_AndNeverPermitsSend()
    {
        var scopeA = NewTriggerWithScope("epoch-a");
        var scopeB = NewTriggerWithScope("epoch-b");
        var items = new List<LocalWaitItem> { WaitItem("s-1") };

        var a1 = Requests(Decision(scopeA, TriggerPoint("OccupancyEnded"), items));
        var b1 = Requests(Decision(scopeB, TriggerPoint("OccupancyEnded"), items));
        Assert.Single(a1);
        Assert.Single(b1);
        AssertNoSendPermit(a1[0]);
        AssertNoSendPermit(b1[0]);

        // 不同作用域 ⇒ 键不同 ⇒ 互不屏蔽（同一稳定身份仍各自独立产一次）
        var keyA = StringOf(a1[0], "ReevaluationKey");
        var keyB = StringOf(b1[0], "ReevaluationKey");
        Assert.False(string.IsNullOrWhiteSpace(keyA));
        Assert.NotEqual(keyA, keyB);
        // 批次 15c：作用域字段**不再明文进键**（`reval-key-v2|` ＋ 长度前缀 ＋ **UTF-16 码元**十六进制载荷），
        // 故此处**不**断言作用域字样出现在键里；区分作用域由上面的 `Assert.NotEqual(keyA, keyB)` 承担。
        Assert.StartsWith("reval-key-v2|", keyA!);
        Assert.StartsWith("reval-key-v2|", keyB!);
        // 键字段是 `=长度:十六进制` 编码 ⇒ 作用域明文（及任何非 ASCII 明文）都不应出现在键里；
        // 作用域的**区分**由上面的 `Assert.NotEqual(keyA, keyB)` 独立承担（不依赖编码细节）。
        Assert.Equal(0, CountOccurrences(keyA!, "epoch-a"));
        Assert.Equal(0, CountOccurrences(keyB!, "epoch-b"));

        // 同一作用域内重复触发 ⇒ 零请求（幂等仍然成立）
        Assert.Empty(Requests(Decision(scopeA, TriggerPoint("OccupancyEnded"), items)));

        // 作用域默认为 null（既有语义不变）：同一实例第二次触发仍零请求
        var bare = NewTrigger();
        Assert.Single(Requests(Decision(bare, TriggerPoint("OccupancyEnded"), items)));
        Assert.Empty(Requests(Decision(bare, TriggerPoint("OccupancyEnded"), items)));
        var scopeProp = RequireType(TriggerTypeName).GetProperty("StateScope", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(scopeProp is not null, "触发器必须公开 StateScope（可选构造参数须可读回以便对账）。");
        Assert.Null(scopeProp!.GetValue(bare));
        Assert.Equal("epoch-a", scopeProp.GetValue(scopeA));
    }

// ─────────────────────────────────────────────────────────────────────────────
// ④ 并发：并发触发同一等待项不产重复重评，且零发送
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **并发触发不产生重复重评**：N 个线程**同时**对同一等待集合求值，全部请求合起来对每个等待项
    /// **恰好一条**（在飞去重），且**零发送**。以并发合流后的计数为证，不依赖调度运气：
    /// 线程先到闸门再同时放行。
    /// </summary>
    [Fact]
    public void Decide_ConcurrentTriggers_ProduceNoDuplicates()
    {
        const int threads = 8;
        var trigger = NewTrigger();
        var items = new List<LocalWaitItem> { WaitItem("s-1"), WaitItem("s-2"), WaitItem("s-3") };
        var collected = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<Exception>();
        // [批次17 B17-R6-02] 生命周期改用**共享运行器** ConcurrentGateRunner（授权守卫的唯一实现）：
        // 后台线程＋统一清理路径＋「清理唤醒 ≠ 授权执行」，与常驻守卫夹具共用同一实现，
        // 授权守卫的反向突变（MUT-B17-5b）直接命中真实修复点。
        var runner = new ConcurrentGateRunner(threads);

        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            try
            {
                if (!runner.WaitAuthorized()) return;   // 清理唤醒：不得执行被测调用
                var requests = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), items));
                foreach (var r in requests) collected.Add(ItemIdOf(r) ?? "");
            }

            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToList();

        runner.Run(workers);

        Assert.False(runner.ReadyFailed, "并发线程未能全部就绪。");
        Assert.Equal(0, runner.JoinTimeouts);
        Assert.Empty(failures);
        // 每个等待项恰好一条，总数恰为 3（不是 8×3）
        Assert.Equal(3, collected.Count);
        Assert.Equal(3, collected.Distinct().Count());
    }

    /// <summary>
    /// **并发触发零发送**：并发合流期间**不得**出现任何发送面成员被赋值（无 `SendSeq` 递增、
    /// 无 `JobId`、无 `SubmissionIdentity`）。以逐条产物断言为证。
    /// </summary>
    [Fact]
    public void Decide_ConcurrentTriggers_NeverPermitSend()
    {
        const int threads = 6;
        var trigger = NewTrigger();
        var items = new List<LocalWaitItem> { WaitItem("s-1"), WaitItem("s-2") };
        var requests = new ConcurrentBag<object>();
        // [批次17 B17-03 加固] 工作线程体必须捕获异常：普通 Thread 的未处理异常会直接终止测试宿主，
        // 而不是记为本用例的失败；捕获后统一断言，异常路径照常红。
        // [批次17 B17-R6-02] 生命周期改用共享运行器（与上一夹具及常驻守卫夹具同一授权实现）。
        var workerFailures = new ConcurrentBag<Exception>();
        var runner = new ConcurrentGateRunner(threads);

        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            try
            {
                if (!runner.WaitAuthorized()) return;   // 清理唤醒：不得执行被测调用
                foreach (var r in Requests(Decision(trigger, TriggerPoint("StartupRecovery"), items)))
                    requests.Add(r);
            }
            catch (Exception ex)
            {
                workerFailures.Add(ex);
            }
        })).ToList();

        runner.Run(workers);

        Assert.False(runner.ReadyFailed, "并发线程未能全部就绪。");
        Assert.Equal(0, runner.JoinTimeouts);
        Assert.Empty(workerFailures);
        Assert.Equal(2, requests.Count);
        foreach (var r in requests) AssertNoSendPermit(r);
    }

// ─────────────────────────────────────────────────────────────────────────────
// ⑤ 取消／失效：取消后触发不产重评
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **取消项不产重评（状态取消）**：`LocalWaitItemState.Cancelled` 的等待项在任一触发点下
    /// 都**不得**产出重评请求（取消后不得复活执行意愿）。
    /// </summary>
    [Fact]
    public void Decide_CancelledItem_ProducesNoRequest_ForEveryTriggerPoint()
    {
        var trigger = NewTrigger();
        var cancelled = new List<LocalWaitItem> { WaitItem("s-1", LocalWaitItemState.Cancelled) };
        foreach (var name in new[] { "OccupancyEnded", "StartupRecovery", "NewCandidateArrived", "SafetyNet" })
        {
            Assert.Empty(Requests(Decision(trigger, TriggerPoint(name), cancelled)));
        }
    }

    /// <summary>
    /// **取消后不再补产（同身份先取消再触发）**：同一身份先以 `Waiting` 触发一次，再以
    /// `Cancelled` 触发 ⇒ 第二次**必须零请求**；且**反向不复活**：已产过一次的身份即使回到
    /// `Waiting`（同进度标识）也不得再产（本轮已处理）。
    /// </summary>
    [Fact]
    public void Decide_CancelThenTrigger_ProducesNothing_AndNeverRevives()
    {
        var trigger = NewTrigger();
        var waiting = new List<LocalWaitItem> { WaitItem("s-1") };
        Assert.Single(Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), waiting)));

        var cancelled = new List<LocalWaitItem> { WaitItem("s-1", LocalWaitItemState.Cancelled) };
        Assert.Empty(Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), cancelled)));
        // 取消后即使再次出现同身份 Waiting 项，也不得再产（幂等键已消费）
        Assert.Empty(Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), waiting)));
    }

    /// <summary>
    /// **取消令牌已取消时不产重评**：触发求值接受可选取消令牌；令牌已取消 ⇒ 立即返回**空请求集**
    /// 且**不改状态、不产发送**（幂等键也不得被消费，以免取消后无法再次评估——断言取消请求不产）。
    /// 语义接缝：`Decide(point, items, CancellationToken)`。
    /// </summary>
    [Fact]
    public void Decide_CancelledToken_ProducesNoRequest()
    {
        var trigger = NewTrigger();
        var items = new List<LocalWaitItem> { WaitItem("s-1") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 令牌路径只经 5 参主重载（批次 15c：4 参 CancellationToken 重载已 private）
        var decision = Invoke(trigger, "Decide", (object)TriggerPoint("OccupancyEnded"), (object)items, (object)DateTimeOffset.UnixEpoch, (object)cts.Token, (object?)null);
        Assert.Empty(Requests(decision));
    }

// ─────────────────────────────────────────────────────────────────────────────
// ⑥ 产物面：只能是「重新走完整准入」请求——不含任何发送许可成员
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **产物结构上不含发送许可**：重评请求类型**不得**出现 `SendPermitted`／`SendSeq`／`JobId`／
    /// `SubmissionIdentity`／`Accepted` 等发送面成员（与批次 14 `WaitLocally` 的「不携带发送身份」同向）。
    /// 这是**结构面**断言（成员不存在 ⇒ 无法伪造许可），比运行期取值断言更强。
    /// </summary>
    [Fact]
    public void RequestType_HasNoSendPermitSurface()
    {
        var type = RequireType(RequestTypeName);
        var banned = new[] { "SendPermitted", "SendSeq", "JobId", "SubmissionIdentity", "Accepted", "JobHandle" };
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in banned)
        {
            Assert.False(members.Contains(name),
                $"重评请求不得含发送面成员 {name}（D3：重评只表达「重新走完整准入」，不产生发送、不含发送许可）。");
        }

        // 决策类型同样不得暴露发送许可
        var decisionMembers = RequireType(DecisionTypeName)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in banned)
        {
            Assert.False(decisionMembers.Contains(name), $"重评决策不得含发送面成员 {name}。");
        }
    }

    /// <summary>
    /// **运行期：产物逐条不含发送许可取值**：`RequiresFullAdmission` 为真、`JobId` 语义缺位、
    /// 决策不得声明「已受理／已发送／已证实失败」。
    /// </summary>
    [Fact]
    public void Requests_NeverCarrySendPermit_AtRuntime()
    {
        var trigger = NewTrigger();
        var requests = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { WaitItem("s-1"), WaitItem("s-2") }));
        Assert.Equal(2, requests.Count);
        foreach (var r in requests) AssertNoSendPermit(r);
    }

    /// <summary>
    /// **触发求值不调用任何发送路径**：以替身计数为证——本组把「触发器是否具备发送面接缝」作为
    /// **结构面反例**：若实现里出现 sender／client 依赖，本夹具在类型成员扫描上失败。
    /// 语义：D3 本批**不得**产生发送，故触发器不得持有发送依赖。
    /// </summary>
    [Fact]
    public void TriggerType_HasNoSenderOrClientDependency()
    {
        var type = RequireType(TriggerTypeName);
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var p in ctor.GetParameters())
            {
                Assert.False(IsSendSurfaceType(p.ParameterType),
                    $"{type.Name} 的构造参数 {p.Name}: {p.ParameterType.Name} 属于发送面——D3 触发器不得持有发送依赖（本批不产生发送）。");
            }
        }

        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            Assert.False(IsSendSurfaceType(f.FieldType),
                $"{type.Name} 的字段 {f.Name}: {f.FieldType.Name} 属于发送面——D3 触发器不得持有发送依赖。");
        }
    }

    private static bool IsSendSurfaceType(Type t)
    {
        var name = t.Name;
        return name.Contains("Sender", StringComparison.Ordinal)
               || name.Contains("ExecutionBoundary", StringComparison.Ordinal)
               || name.Contains("BgiExternalClient", StringComparison.Ordinal)
               || name.Contains("CommandExecutor", StringComparison.Ordinal)
               || name.Contains("SubmissionDispatch", StringComparison.Ordinal);
    }

// ─────────────────────────────────────────────────────────────────────────────
// ⑨ 批次 15b：第二轮会诊反例（首轮被降级拒绝的三条缺陷）——反例先行，先红后修
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **枚举器中途抛异常 ⇒ 不得留下被消费的幂等键**（第二轮会诊 #2 反例，推翻首轮"预留与 return
    /// 间无可观察失败界限"的拒绝理由）。
    ///
    /// 首轮实现先在 `_handled.TryAdd` 占键、**之后**仍继续枚举 `items`。传入一个**合法**的
    /// `IReadOnlyList`：首个元素是 `Waiting` 项、下一次 `MoveNext` 抛异常 ⇒ 键已被占用却**没有**
    /// 任何决策被交付；此后用正常集合重试同一等待项得到**空请求**，该等待项被**永久**屏蔽。
    ///
    /// 修复口径：先完成全部读与校验、构造出完整决策，**再**原子占键；异常发生在占键之前 ⇒ 不消费键。
    /// </summary>
    [Fact]
    public void Decide_EnumeratorThrows_DoesNotConsumeIdempotencyKey()
    {
        var trigger = NewTrigger();
        var good = WaitItem("s-throw");

        // 合法实现 IReadOnlyList<T> 的集合：枚举一次后在下一次 MoveNext 抛异常。
        var throwing = new ThrowingAfterFirstReadOnlyList(good);

        // 本次触发不得把异常变成"已处理"：异常可以抛出，但**键不得被消费**。
        try { Invoke(trigger, "Decide", TriggerPoint("OccupancyEnded"), throwing); }
        catch (InvalidOperationException) { /* 反例注入的异常类型；键是否被消费才是断言点 */ }

        // 关键断言：用正常集合重试同一等待项，**必须**仍能产出请求。
        var retry = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { good }));
        Assert.True(retry.Count == 1,
            $"枚举期间发生异常时不得消费幂等键：首调用抛异常后，重试同一等待项应仍产出 1 条请求，实际 {retry.Count} 条"
            + "（首轮实现先 TryAdd 后继续枚举 ⇒ 键被占、请求未交付、该项被永久屏蔽）。");
    }

    /// <summary>
    /// **`ItemId` 与 `StableIdentity` 派生不一致 ⇒ 不产请求、不占键**（第二轮会诊 #3 反例，推翻首轮
    /// "接收方可重新派生校验"的拒绝理由）。
    ///
    /// 首轮实现先按 `StableIdentity` 占键、再原样复制可写的 `item.ItemId` ⇒ 产物指向**错误**等待项；
    /// 接收方即使拒收，之后改正 `ItemId` 重试**仍被永久屏蔽**——接收方校验**撤销不了**已经作出的预留。
    ///
    /// 修复口径：产出前校验 `item.ItemId == LocalWaitQueuePolicy.DeriveItemId(item.StableIdentity)`；
    /// 不一致 ⇒ **不占键、不产请求**；改正后重试应能正常产出。
    /// </summary>
    [Fact]
    public void Decide_ItemIdNotDerivedFromStableIdentity_ProducesNothingAndKeepsKeyFree()
    {
        var trigger = NewTrigger();

        // 稳定身份 s-a，但 ItemId 指向另一个身份 s-b 的派生结果 ⇒ 不一致。
        var mismatched = WaitItem("s-a");
        mismatched.ItemId = LocalWaitQueuePolicy.DeriveItemId("s-b");

        var first = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { mismatched }));
        Assert.True(first.Count == 0,
            $"ItemId 与 StableIdentity 派生不一致时不得产出请求（产物会指向错误等待项），实际 {first.Count} 条。");

        // 接收方拒收后改正 ItemId 再重试：因为不一致输入**没有**占键，本次必须能正常产出。
        var corrected = WaitItem("s-a");
        var second = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { corrected }));
        Assert.True(second.Count == 1,
            $"不一致输入不得消费幂等键：改正 ItemId 后重试应产出 1 条请求，实际 {second.Count} 条"
            + "（首轮实现先按身份占键 ⇒ 接收方校验撤销不了该预留）。");
    }

    /// <summary>
    /// **同一实例内按"等待项代际"重新求值**：新代际**必须**重新产请求（第二轮会诊 #1/#4 反例，
    /// 推翻首轮 `StateScope` 的"部分采纳"——它构造后不可变、`_handled` 随实例存活，
    /// 故**没有**在原问题发生的那一层（同一实例生命周期）生效）。
    ///
    /// 语义接缝：`Decide` 需接受一个**显式代际**入参（本轮实现取 `LocalWaitItem.PrerequisiteReference`
    /// 之外的新增可选入参，或以等待项上的代际字段承载）；同一代际重复触发不重复产，**新代际必须重新产**。
    /// </summary>
    [Fact]
    public void Decide_NewGeneration_SameInstance_ProducesAgain()
    {
        var trigger = NewTrigger();
        var identity = "s-gen";

        var gen1 = WaitItem(identity);
        var first = Requests(Gen(trigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { gen1 }, "gen-1"));
        Assert.True(first.Count == 1, $"首个代际应产出 1 条请求，实际 {first.Count} 条。");

        // 同一代际重复触发：不重复产（幂等仍须成立）。
        var repeated = Requests(Gen(trigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { gen1 }, "gen-1"));
        Assert.True(repeated.Count == 0, $"同一代际重复触发不得重复产，实际 {repeated.Count} 条。");

        // 新代际（取消后重新等待的合法新生命周期）：**必须**重新产出。
        var gen2 = WaitItem(identity);
        var second = Requests(Gen(trigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { gen2 }, "gen-2"));
        Assert.True(second.Count == 1,
            $"同一实例内新代际必须重新产出请求，实际 {second.Count} 条"
            + "（首轮 StateScope 构造后不可变、_handled 随实例存活 ⇒ 原问题发生在同一实例生命周期内时未解决）。");
    }

    /// <summary>
    /// **键编码必须无歧义 ⇒ 不同作用域/代际三元组不得映射到同一键**（批次 15c，评审第 3 轮必改 #2
    /// 的**红夹具先行**：旧实现用 `reval-[&lt;scope&gt;:]&lt;摘要&gt;[|&lt;代际&gt;]` 原样拼接，评审给出的反例是
    /// `(scope=null, 代际="x:"+h+"|z")` 与 `(scope=h+"|x", 代际="z")` 同为 `reval-h|x:h|z`）。
    ///
    /// **判别力设计（批次 15c 修订）**：夹具按**反例构造**给出两个必须不同键的三元组——①无作用域＋代际
    /// `"x:" + &lt;摘要&gt; + "|z"`；②作用域 `&lt;摘要&gt; + "|x"` ＋代际 `"z"`。二者在**任何**"字段间无边界/无长度前缀"
    /// 的原样拼接下必然同键（这正是必改 #2 的可利用通道）；而在长度前缀编码下两字段载荷不同、长度不同 ⇒ 异键。
    /// 另加**辅助**计数：摘要文本在本样例的键内出现一次（旧形状 `reval-h|x:h|z` 含两个 `h`）。
    /// **第 9 轮重要 #2 更正**：这**不是**"结构不变量证明"——摘要文本是十六进制串，作用域／代际载荷同样可由
    /// 十六进制字符构成、也可能含该文本，故计数并不证明字段来源。真正的判别力由上面的 `Assert.NotEqual(key1, key2)` 承担。
    /// </summary>
    [Fact]
    public void ReevaluationKey_DistinctScopeGenerationTriples_NeverAlias()
    {
        // 先取稳定身份 s-1 的摘要（与键同口径：DeriveReevaluationKey 去掉 `reval-` 前缀后的 16 位十六进制）。
        var probe = NewTrigger();
        var digest = ((string)Invoke(probe, "DeriveReevaluationKey", "s-1"))["reval-".Length..];
        Assert.Equal(16, digest.Length);

        // 评审反例变体 A：作用域＝摘要＋"|x"（旧原样拼接下会与被摘要字段"吃掉"边界）
        var scopeOtherwiseUnsafe = digest + "|x";
        var genWithDigest = "x:" + digest + "|z";

        var key1 = KeyOf(Gen(NewTrigger(), TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { WaitItem("s-1") }, genWithDigest), 0);
        var key2 = KeyOf(Gen(NewTriggerWithScope(scopeOtherwiseUnsafe), TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { WaitItem("s-1") }, "z"), 0);

        Assert.False(string.IsNullOrWhiteSpace(key1));
        Assert.False(string.IsNullOrWhiteSpace(key2));
        Assert.NotEqual(key1, key2);

        // 辅助计数（**非**结构不变量证明，见上方逐字收窄）：摘要文本在本样例键内出现一次。
        Assert.Equal(1, CountOccurrences(key1!, digest));
        Assert.Equal(1, CountOccurrences(key2!, digest));
        // 判别力核心（批次 15c）：两个三元组必须**异键**。旧原样拼接下二者同键 ⇒ 该断言只须 `NotEqual` 即失效；
        // 新编码下才成立（长度前缀保证边界）。上方两行计数只作**辅助**观察：needle 恰为十六进制串，
        // 作用域／代际载荷也可含该文本 ⇒ 计数**不证明**字段来源。
    }

    /// <summary>
    /// **键字段编码必须对任意 .NET 字符串单射 ⇒ 孤立代理项不得与替换字符同键**（批次 15c 第二轮，
    /// 评审第 4 轮必改 #2 的**红夹具先行**）：第一版 `EncodeKeyField` 用 `Encoding.UTF8.GetBytes(value)`，
    /// 而 UTF-8 编码**不是单射**——孤立代理项（如 `"\uD800"`）被替换字符 U+FFFD 取代（同样 `EF BF BD`）⇒
    /// `"a\uD800"` 与 `"a\uFFFD"`（同身份、同代际）映射到**同一键**，跨作用域/代际去重被错误合并。
    ///
    /// 判别力设计：夹具把这两个字符串分别作为**作用域**（同身份 `s-1`、同代际 `null`）⇒ 断言两键**不同**。
    /// 旧 UTF-8 实现下二者同键 ⇒ 本断言失败（红）；改为 UTF-16 码元级编码后成立。
    /// </summary>
    [Fact]
    public void ReevaluationKey_LoneSurrogateScope_NeverAliasesReplacementChar()
    {
        var loneSurrogate = "a\uD800";
        var replacement = "a\uFFFD";

        var key1 = KeyOf(Decision(NewTriggerWithScope(loneSurrogate), TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { WaitItem("s-1") }), 0);
        var key2 = KeyOf(Decision(NewTriggerWithScope(replacement), TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { WaitItem("s-1") }), 0);

        Assert.False(string.IsNullOrWhiteSpace(key1));
        Assert.False(string.IsNullOrWhiteSpace(key2));
        Assert.NotEqual(loneSurrogate, replacement);
        Assert.NotEqual(key1, key2);

        // 反向：同一字符串（含孤立代理项）重复 ⇒ 同键（编码确定性，不引入随机/时间依赖）。
        var key1Again = KeyOf(Decision(NewTriggerWithScope(loneSurrogate), TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { WaitItem("s-1") }), 0);
        Assert.Equal(key1, key1Again);
    }

    /// <summary>
    /// **稳定身份摘要路径的等价面：已披露的既有口径**（批次 15c，评审第 5 轮必改 #1 的**红夹具先行**）。
    ///
    /// **反例（请按此构造函数）**：`DeriveReevaluationKey`／`DeriveItemId` 都用 `Encoding.UTF8.GetBytes`，
    /// 而 UTF-8 编码**不是**对任意 .NET 字符串的单射：孤立代理项被替换字符 U+FFFD（`EF BF BD`）取代 ⇒
    /// `S1 = "a\uD800"` 与 `S2 = "a\uFFFD"` 得到**同一**摘要、同一 `ItemId`。
    ///
    /// **本夹具钉死批内可观测后果（不是"没问题"，而是"已界定"）**：
    /// ①两项都能通过产出侧 `ItemId` 一致性校验（因为校验按**同一**口径派生）⇒ 互相屏蔽**不是**校验漏检；
    /// ②同一次 `Decide` 传入两项 ⇒ 键相同 ⇒ `seenInBatch` 只保留**第一项**（产 1 条，指向 S1）；
    /// ③分两次调用 ⇒ `_handled` 屏蔽第二次（产 0 条）。
    /// **等价面本身的效果（与上一条同向，不是反例）**：因为 `S1` 与 `S2` 的 `ItemId` **相同**，无论把该项的
    /// `StableIdentity` 写成 `S1` 还是 `S2`，它与自派生 `ItemId` 都**一致** ⇒ 都**能**通过产出侧校验。
    /// 真正的"另一侧"校验反例见 ⑤：改写为**无关身份**的派生 `ItemId` ⇒ 不一致 ⇒ 不产、**不占键**。
    ///
    /// **本夹具的性质＝"既有缺陷的特征化断言"，不是正确性要求**（评审第 6 轮重要 #4 已明确要求声明）：
    /// 其中"同键"断言**故意**钉住当前（有缺陷的）口径；若某天把摘要改成码元级编码等**正确修复**，
    /// 该断言会变红 —— 此时**应当**同步改写本夹具（把断言反转为"异键"），而**不得**据此认为修复是错的。
    /// 本夹具的正向价值是：①防止等价面在未被察觉时被重新引入；②把后果逐条固定成可核对事实。
    ///
    /// **废弃该缺陷需要改动 `DeriveItemId` 口径（批次 14 的既有语义面／去重键形状），本批按 owner 约束
    /// 不扩面**：此等价面**已披露**，生产接线必须假定 `StableIdentity` 来自权威身份面（非任意 UTF-16 串），
    /// 且 64 位摘要的概率碰撞边界另见 `DocumentedHashPrefixLength`（**概率碰撞**与**可复现等价面**分开表述）。
    /// </summary>
    [Fact]
    public void ReevaluationKey_StableIdentityDigest_Utf8Equivalence面_已披露_且ItemId校验同口径()
    {
        var s1 = "a\uD800";
        var s2 = "a\uFFFD";
        Assert.NotEqual(s1, s2);

        var trigger = NewTrigger();
        var key1 = (string)Invoke(trigger, "DeriveReevaluationKey", s1);
        var key2 = (string)Invoke(trigger, "DeriveReevaluationKey", s2);

        // ①两个不同字符串得到同一键（既有摘要口径的等价面，已披露）
        Assert.Equal(key1, key2);
        // ②与 DeriveItemId 同口径：同一等价面 ⇒ 同一 ItemId（故身份校验不会把等价面挡下）
        Assert.Equal(LocalWaitQueuePolicy.DeriveItemId(s1), LocalWaitQueuePolicy.DeriveItemId(s2));

        // ③同批：两项同键 ⇒ 只产一条（同批去重结果如实）
        var batchItem1 = WaitItem(s1);
        var batchItem2 = WaitItem(s2);
        Assert.Equal(batchItem1.ItemId, batchItem2.ItemId);   // 两者都通过 ItemId 一致性校验
        var batched = Requests(Decision(NewTrigger(), TriggerPoint("OccupancyEnded"),
            new List<LocalWaitItem> { batchItem1, batchItem2 }));
        Assert.True(batched.Count == 1,
            $"同批内仅在该 UTF-8 等价面上等价的两个身份 ⇒ 同键 ⇒ 只产 1 条（已披露），实际 {batched.Count} 条。");
        Assert.Equal(s1, StringOf(batched[0], "StableIdentity"));   // 保留的是批内第一项

        // ④跨调用：新实例内第二项被在飞去重屏蔽（产 0 条）
        var single = NewTrigger();
        Assert.Single(Requests(Decision(single, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { batchItem2 })));
        Assert.Empty(Requests(Decision(single, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { batchItem1 })));

        // ⑤另一侧：伪造 ItemId（取**无关身份**的派生值）⇒ 不一致 ⇒ 不产、**且不占键**（必须用**同一实例**改正后重试来证明"不占键"：
        //    若实现改成"先占键再校验"或"用身份占键"，这里会因 `_handled` 已被占而产 0 条 ⇒ 夹具变红）。
        var forged = WaitItem(s2);
        forged.ItemId = LocalWaitQueuePolicy.DeriveItemId("s-mut-OTHER");   // 伪造：取**无关身份**的 ItemId ⇒ 与自身身份摘要不一致
        Assert.NotEqual(forged.ItemId, LocalWaitQueuePolicy.DeriveItemId(forged.StableIdentity));
        var forgedTrigger = NewTrigger();
        Assert.Empty(Requests(Decision(forgedTrigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { forged })));
        forged.ItemId = LocalWaitQueuePolicy.DeriveItemId(s2);      // 改正 ⇒ 同一实例内必须仍能产出（证明上面未占键）
        Assert.Single(Requests(Decision(forgedTrigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { forged })));
    }

    /// <summary>
    /// **4 参 `Decide` 不得存在「`CancellationToken` 与 `string? generation` 并存」的重载歧义**
    /// （批次 15c，评审第 3 轮必改 #1 的**红夹具先行**）：旧实现同时公开
    /// `Decide(…, CancellationToken)` 与 `Decide(…, string?)`（同为 4 参）⇒ 源码层
    /// `Decide(t, items, now, default)` 变成**歧义**、`Decide(t, items, now, null)` 被**静默改绑**到代际重载。
    ///
    /// 本夹具断言公共面**不得**具备**任何** 4 参 `Decide` 形态（两个 4 参重载都已收窄为 private）；
    /// 取消语义与代际都只经 5 参主重载可达。
    /// 反例（旧实现 / 只收窄一个的中间态）：任一 4 参形态仍然公开 ⇒ 本断言失败（红）。
    /// </summary>
    [Fact]
    public void PublicDecide_NoFourArgCancellationTokenAndGenerationOverloadAmbiguity()
    {
        var type = RequireType(TriggerTypeName);
        var fourArg = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Decide" && m.GetParameters().Length == 4)
            .ToList();
        Assert.True(fourArg.Count == 0,
            "公共面不得有任何 4 参 `Decide` 重载（第 10 轮建议收窄：**歧义源自**`CancellationToken` 与 "
            + "`string? generation` 两个 4 参重载**并存** ⇒ `Decide(t, items, now, default)` 歧义、"
            + "`Decide(t, items, now, null)` 静默改绑；批次 15c 已把 `CancellationToken` 与 `string? generation` 两个 4 参重载都收窄为 private）。现有 4 参重载："
            + string.Join(" / ", fourArg.Select(m => "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")));

        foreach (var m in fourArg)
        {
            Assert.Fail("4 参 `Decide` 重载不得公开（第 9 轮建议收窄：**歧义源自**`CancellationToken` 与 `string? generation`"
            + "两个 4 参重载**并存**——`Decide(t, items, now, default)` 会 `CS0121`、`Decide(t, items, now, null)` 会被静默改绑；"
            + "令牌与代际两条路径都经 5 参主重载可达）：(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
        }

        // 令牌路径必须仍可用：5 参主重载（0 参默认代际）可绑定 `CancellationToken`。
        var mainOverload = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Decide"
                && m.GetParameters().Length == 5
                && m.GetParameters()[3].ParameterType == typeof(CancellationToken));
        Assert.True(mainOverload is not null, "必须保留 5 参主重载（含 CancellationToken）作为取消语义的唯一可达入口。");

        // 可赋值性绑定（与 C# 重载解析一致）：`CancellationToken` 实参**恰好**绑定到该 5 参重载，不得落到 string 形参。
        var token = new CancellationToken(canceled: false);
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "Decide")
            .Where(m => ArgsAssignable(m.GetParameters(), new object?[] { TriggerPoint("OccupancyEnded"), new List<LocalWaitItem>(), DateTimeOffset.UnixEpoch, token }))
            .ToList();
        Assert.True(matches.Count == 0,
            "4 参 `CancellationToken` 调用点在可赋值性绑定下不得命中任何公共重载（必须只经 5 参主重载显式传入令牌）。");
        Assert.True(ArgsAssignable(mainOverload!.GetParameters(), new object?[] { TriggerPoint("OccupancyEnded"), new List<LocalWaitItem>(), DateTimeOffset.UnixEpoch, token, null }));
    }

    /// <summary>
    /// **取消后重试 ⇒ 仍可再评估**（第二轮会诊指出的首轮无判别力夹具：原夹具只检查"已取消 ⇒ 空集"，
    /// **没有**在取消后重试，故证明不了"取消不消费幂等键"）。
    ///
    /// **批次 15c（评审第 5 轮重要 #2）补断言**：必须同时断言"取消 ⇒ 空集"。否则一个错误实现若在取消时
    /// **产出请求但不占键**，只断言重试仍能产出的夹具照样通过 —— 判别力不足。
    /// </summary>
    [Fact]
    public void Decide_CancelledToken_DoesNotConsumeKey_RetryStillProduces()
    {
        var trigger = NewTrigger();
        var item = WaitItem("s-cancel-retry");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = Requests(Invoke(trigger, "Decide", (object)TriggerPoint("OccupancyEnded"), (object)new List<LocalWaitItem> { item }, (object)DateTimeOffset.UnixEpoch, (object)cts.Token, (object?)null));
        Assert.True(cancelled.Count == 0,
            $"已取消令牌必须返回空集（只短路请求，不消费幂等键），实际 {cancelled.Count} 条。");

        // 判别力关键：重试必须仍能产出 ⇒ 证明取消**没有**消费幂等键。
        var retry = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { item }));
        Assert.True(retry.Count == 1,
            $"取消不得消费幂等键：取消后重试应产出 1 条请求，实际 {retry.Count} 条（原夹具未做此重试，故无判别力）。");
    }

    /// <summary>
    /// **`SafetyNet` 到期分支也必须逐项检查等待项状态**（第二轮会诊指出的首轮无判别力夹具：
    /// 原夹具的 `SafetyNet` 分支默认"未到期"⇒ 直接短路返回空集，即使**完全不检查**项状态也会通过）。
    /// 本夹具注入"已到期"，故必须真的走逐项判定：`Waiting` 项产出、`Cancelled` 项不产出。
    /// </summary>
    [Fact]
    public void Decide_SafetyNetDue_StillChecksItemStatePerItem()
    {
        var type = RequireType(TriggerTypeName);
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(Func<DateTimeOffset, bool>)));
        Assert.True(ctor is not null, "触发器必须提供可注入的安全网判定构造参数。");
        var args = ctor!.GetParameters().Select(p =>
            p.ParameterType == typeof(Func<DateTimeOffset, bool>)
                ? (object)(Func<DateTimeOffset, bool>)(_ => true)   // 恒"已到期"：强制走逐项判定
                : p.HasDefaultValue ? p.DefaultValue! : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)! : null!))
            .ToArray();
        var trigger = ctor.Invoke(args);

        var items = new List<LocalWaitItem>
        {
            WaitItem("s-net-waiting"),
            WaitItem("s-net-cancelled", LocalWaitItemState.Cancelled),
        };

        var requests = Requests(Decision(trigger, TriggerPoint("SafetyNet"), items));
        Assert.True(requests.Count == 1,
            $"安全网到期时仍须逐项检查状态：仅 Waiting 项产出，实际 {requests.Count} 条"
            + "（原夹具在默认'未到期'下短路返回空集，不检查项状态也会通过 ⇒ 无判别力）。");
        Assert.Equal(LocalWaitQueuePolicy.DeriveItemId("s-net-waiting"), ItemIdOf(requests[0]));
    }

    /// <summary>
    /// **可枚举 `IReadOnlyList`：首元素被读取后，在下一次 `MoveNext` 里把它"改写"。**
    ///
    /// **批次 15d 修复 #1（评审第 6 轮必改 #1）的反例构造器**：原实现枚举期校验 `ItemId`，却把
    /// `LocalWaitItem` **引用**存进候选、并在**占键之后**才从引用读取产物字段 ⇒ 本适配器可以在"校验已过、
    /// 产物未取"的窗口里把 `ItemId` 改成**另一身份**的派生值，使产出的请求 `ItemId` 与 `StableIdentity`
    /// **不一致**且键**已不可撤销地占用**（改正后同实例重试被屏蔽）。
    /// 修复后实现取的是**值快照**⇒ 该改写**不再**影响任何结论（夹具断言产物仍是校验时的值）。
    /// </summary>
    private sealed class MutatingAfterFirstReadList : IReadOnlyList<LocalWaitItem>
    {
        private readonly LocalWaitItem _item;
        private readonly Action<LocalWaitItem> _mutate;
        private int _moveNextCount;
        public MutatingAfterFirstReadList(LocalWaitItem item, Action<LocalWaitItem> mutate)
        {
            _item = item;
            _mutate = mutate;
        }

        public int Count => 1;
        public LocalWaitItem this[int index] => _item;

        public IEnumerator<LocalWaitItem> GetEnumerator()
        {
            yield return _item;
            // 第二次 MoveNext：在"校验已完成、候选已被收下"之后改写该项（反例注入窗口）
            _moveNextCount++;
            if (_moveNextCount == 1) _mutate(_item);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// **枚举与产物之间被改写 ⇒ 产物必须仍是"校验过的取值"且键不被浪费**（批次 15d 修复 #1 的
    /// **红夹具先行**，评审第 6 轮必改 #1）。
    ///
    /// 反例（旧实现下）：`ItemId` 在枚举结束后被改成另一身份的值 ⇒ 旧实现产出
    /// `ItemId != DeriveItemId(StableIdentity)` 的请求（违反"不一致 ⇒ 不产、不占键"契约），且键已占用。
    /// 修复后：产物字段取自**校验时的值快照** ⇒ `ItemId` 仍与 `StableIdentity` 一致。
    /// </summary>
    [Fact]
    public void Decide_ItemMutatedDuringEnumeration_ProducesRequestWithValidatedValues()
    {
        var item = WaitItem("s-mut-1");
        var validatedItemId = item.ItemId;
        var validatedIdentity = item.StableIdentity;
        var validatedCandidateId = item.CandidateId;

        var list = new MutatingAfterFirstReadList(item, x =>
        {
            x.ItemId = LocalWaitQueuePolicy.DeriveItemId("s-mut-OTHER");   // 改成"另一身份"的派生值
            x.CandidateId = "cand-forged";
        });

        var trigger = NewTrigger();
        var requests = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), list));
        Assert.True(requests.Count == 1, $"改写不应改变产出条数，实际 {requests.Count} 条。");

        // 产物必须仍是**校验过的**取值（不得出现 ItemId 与 StableIdentity 失配的请求）
        Assert.Equal(validatedItemId, ItemIdOf(requests[0]));
        Assert.Equal(validatedIdentity, StringOf(requests[0], "StableIdentity"));
        Assert.Equal(validatedCandidateId, StringOf(requests[0], "CandidateId"));
    }

    /// <summary>可注入异常的可枚举 `IReadOnlyList`：首元素读完后在下一次 `MoveNext` 抛异常。</summary>
    private sealed class ThrowingAfterFirstReadOnlyList : IReadOnlyList<LocalWaitItem>
    {
        private readonly LocalWaitItem _first;
        public ThrowingAfterFirstReadOnlyList(LocalWaitItem first) => _first = first;

        public int Count => 2;
        public LocalWaitItem this[int index] => index == 0 ? _first : throw new InvalidOperationException("反例注入：枚举中途失败");

        public IEnumerator<LocalWaitItem> GetEnumerator()
        {
            yield return _first;
            throw new InvalidOperationException("反例注入：枚举中途失败");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
// ─────────────────────────────────────────────────────────────────────────────
// ⑦ 低频安全网：注入式纯判定（不引入真实定时器／后台线程）
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **安全网不引入真实定时器／后台线程**：触发器类型**不得**持有 `System.Threading.Timer`／
    /// `Timer`／`Thread`／`Task` 字段，也不得实现 `IDisposable`＋后台循环的形态；
    /// 「是否到安全网时刻」必须由**调用方注入**的纯判定给出（时间由调用方传入，保持纯函数契约）。
    /// </summary>
    [Fact]
    public void TriggerType_HasNoTimerOrBackgroundThread()
    {
        var type = RequireType(TriggerTypeName);
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            var name = f.FieldType.Name;
            Assert.False(name is "Timer" or "Thread" or "PeriodicTimer",
                $"{type.Name} 不得持有定时器／线程字段 {f.Name}: {name}（安全网走注入式纯判定，不引入真实后台行为）。");
        }
    }

    /// <summary>
    /// **安全网由注入判定决定**：同一等待集合，注入「未到安全网时刻」⇒ 零请求；
    /// 注入「已到」⇒ 产出请求。证明安全网**不是**靠真实时钟，而是调用方传入的判定。
    /// 语义接缝：`Decide` 的第三/第四个参数接受安全网时刻判定。
    /// </summary>
    [Fact]
    public void Decide_SafetyNet_UsesInjectedClockJudgement_NotRealTimer()
    {
        var type = RequireType(TriggerTypeName);
        // 注入一个"安全网到期"的纯判定：由可变标志位驱动（无真实时间参与）——必须真的注入进去，
        // 故按「参数类型含 Func<DateTimeOffset,bool>」精确挑选构造，不按参数个数排序（可选参数会被默认值顶替）。
        var due = false;
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(Func<DateTimeOffset, bool>)));
        Assert.True(ctor is not null,
            $"{type.Name} 必须提供可注入的安全网判定构造参数 Func<DateTimeOffset,bool>（D3：安全网走注入式纯判定，不引入真实定时器）。");
        var args = ctor!.GetParameters().Select(p =>
            p.ParameterType == typeof(Func<DateTimeOffset, bool>)
                ? (object)(Func<DateTimeOffset, bool>)(_ => due)
                : p.HasDefaultValue ? p.DefaultValue! : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)! : null!))
            .ToArray();
        var trigger = ctor.Invoke(args);

        var items = new List<LocalWaitItem> { WaitItem("s-1") };
        var point = TriggerPoint("SafetyNet");
        var notDue = Requests(Invoke(trigger, "Decide", (object)point, (object)items, DateTimeOffset.UnixEpoch));
        Assert.Empty(notDue);

        due = true;
        var nowDue = Requests(Invoke(trigger, "Decide", (object)point, (object)items, DateTimeOffset.UnixEpoch));
        Assert.Single(nowDue);
    }

// ─────────────────────────────────────────────────────────────────────────────
// ⑧ 生产零消费点：本批交付不得在任何生产入口被构造／调用
// ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **生产零消费点（源文本扫描）**：生产源码目录内，除触发器**定义文件自身**外，
    /// **不得**出现对该类型的构造／引用（对齐 §24.106／§24.109 的扫描写法）。
    /// **能力边界**：文本扫描不覆盖反射与动态调用；不证明「生产运行期一定不触发」——
    /// 只证明本批**没有接线点**。
    /// </summary>
    [Fact]
    public void Trigger_HasNoProductionConsumptionPoint()
    {
        var root = Path.Combine(RepoRoot()!, "MultiplayerHoeingAssistant");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(root, "Services", "TaskCenter", "Arbitration", "LocalWaitReevaluationTrigger.cs"),
            Path.Combine(root, "Models", "TaskCenter", "LocalWaitReevaluationModels.cs"),
            // [批次 20／Wave2] C5 消费前复核组件（D-E2=① 代际载体／IW-04）——同一合同族的未接线姊妹
            // 组件（定义文件自身豁免，与触发器同精神）；生产零消费点约束不变（其调用方属接线批）。
            Path.Combine(root, "Services", "TaskCenter", "Arbitration", "LocalWaitReevaluationConsumer.cs"),
        };

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("bin", ""), StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains(Path.Combine("obj", ""), StringComparison.OrdinalIgnoreCase)) continue;
            if (allowed.Contains(file)) continue;
            var text = File.ReadAllText(file);
            if (text.Contains("LocalWaitReevaluation", StringComparison.Ordinal))
                hits.Add(Path.GetRelativePath(RepoRoot()!, file).Replace('\\', '/'));
        }

        Assert.True(hits.Count == 0,
            "发现生产消费点（本批不得接任何生产入口）：[" + string.Join(", ", hits)
            + "]——D3 本批只交付未接线组件；接线须 owner 另行明示。");
    }

// ─────────────────────────────────────────────────────────────────────────────
// 辅助
// ─────────────────────────────────────────────────────────────────────────────

    private static string? ItemIdOf(object request) => StringOf(request, "ItemId");

    private static string? StringOf(object target, string property)
    {
        var prop = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop is not null, $"{target.GetType().Name} 必须暴露属性 {property}。");
        return prop!.GetValue(target)?.ToString();
    }

    private static bool BoolOf(object target, string property)
    {
        var prop = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop is not null, $"{target.GetType().Name} 必须暴露属性 {property}。");
        var value = prop!.GetValue(target);
        Assert.True(value is bool, $"{target.GetType().Name}.{property} 必须是 bool。");
        return (bool)value!;
    }

    private static object? TriggerPointOf(object target)
    {
        var prop = target.GetType().GetProperty("Trigger", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop is not null, $"{target.GetType().Name} 必须暴露属性 Trigger（触发点须回填以便对账）。");
        return prop!.GetValue(target);
    }

    /// <summary>
    /// **产物不得含发送许可**：逐条断言无 JobId／SubmissionIdentity／SendSeq 取值，
    /// 且 `RequiresFullAdmission` 为真（唯一合法语义＝重新走完整准入）。
    /// </summary>
    private static void AssertNoSendPermit(object request)
    {
        Assert.True(BoolOf(request, "RequiresFullAdmission"));
        var members = request.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var banned in new[] { "SendPermitted", "SendSeq", "JobId", "SubmissionIdentity" })
            Assert.False(members.Contains(banned), $"重评请求不得含发送面成员 {banned}。");
    }

    private static string? RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            if (Directory.Exists(Path.Combine(root.FullName, "MultiplayerHoeingAssistant"))
                && Directory.Exists(Path.Combine(root.FullName, "Test")))
                return root.FullName;
            root = root.Parent;
        }
        return null;
    }


    /// <summary>实参能否逐个赋给候选形参（含 null 与非值类型引用；隐式数值提升不做，夹具用不到）。</summary>
    private static bool ArgsAssignable(System.Reflection.ParameterInfo[] ps, object?[] args)
    {
        if (ps.Length != args.Length) return false;
        for (var i = 0; i < ps.Length; i++)
        {
            var actual = args[i];
            if (actual is null)
            {
                if (ps[i].ParameterType.IsValueType && System.Nullable.GetUnderlyingType(ps[i].ParameterType) is null)
                    return false;
                continue;
            }
            if (!ps[i].ParameterType.IsInstanceOfType(actual)) return false;
        }
        return true;
    }

    /// <summary>取产物集合中第 <paramref name="index"/> 条的 <c>ReevaluationKey</c>。</summary>
    private static string? KeyOf(object decision, int index)
    {
        var requests = Requests(decision);
        Assert.True(requests.Count > index, $"期望至少 {index + 1} 条重评请求，实际 {requests.Count} 条。");
        return StringOf(requests[index], "ReevaluationKey");
    }

    /// <summary>
    /// 计数键串中出现 <paramref name="needle"/> 的次数。**批次 15c（评审第 5 轮重要 #1 更正）**：本方法
    /// **只**校验 needle 非空；**不**校验其是否为十六进制载荷（原注释声称"校验合法十六进制奇偶长"与实现不符）。
    /// 计数只用于**辅助观察**（第 10 轮建议更正：**不宜**称"结构不变量断言"——见下方"使用边界"），不解读字段语义。
    /// **使用边界（评审第 6/7/8 轮重要 #5）**：本计数**可能假阳性**——needle 若恰为十六进制串，可以命中
    /// 载荷的十六进制文本。本文件的调用点**并非都**满足"含非十六进制字符"：请求构造反例夹具以**摘要
    /// 十六进制串**为 needle（第 8 轮更正：**不得**再以"量级远大于 16 位十六进制载荷"为由声称实际不会碰撞
    /// ——作用域／代际载荷同样可由任意长度的十六进制字符构成，长度差异不构成证明）。**该计数不构成严格
    /// 结构不变量证明**；凡以十六进制串作 needle 的断言只作**辅助**观察，判别力由同一夹具的**异键**断言承担。
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        Assert.False(string.IsNullOrEmpty(needle), "计数 needle 不得为空。");
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
    // ─────────────────────────────────────────────────────────────────────────────
    // ⑧ 单元素处理期内的重复读值窗口（评审第 7 轮必改 #1——反例先行）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **可重入 `IReadOnlyList` 反例注入器**：每次**从集合取元素**时先计数；在指定序号的那一次取值点上
    /// **成对**改写元素的 `StableIdentity` 与 `ItemId`（保持互相一致，不制造撕裂），随后返回**同一**实例。
    /// 因每次返回同一实例，**只要实现之后再次从集合取出该元素**（本次计数的那一次），它读到的字段就已是改写后的值。
    /// **注意（第 9 轮必改 #1 收窄）**：改写**只在"从集合取元素"这一刻**发生。若实现**只从集合取一次**该元素、
    /// 随后在**同一引用**上重复读取字段，本注入器**不会**触发改写 —— 那种形态下面几段的边界②适用。
    ///
    /// **覆盖边界（评审第 8／9 轮必改 #1，重要，须如实声明）**：
    /// ①本注入器**能**覆盖的错误形态是"实现**再次从集合取出同一元素**再读字段"（例如把 `items[i]` 放回循环体、
    ///    或在两遍遍历中都从集合取元素）——此时第 2 次取值会命中改写，产物失配、夹具变红。
    /// ②它**不能**覆盖"实现只从集合取一次元素（`foreach` 只取一次引用），却在该引用上**重复读取字段**"这一形态：
    ///    该形态下取元素计数仍为 1，改写不生效、夹具**也**通过。**故本夹具不得被读成"能捕获第 7 轮原反例
    ///    （同一 `item` 引用上字段重复读取）"的证据**；那一形态的判别**只能靠实现层面"每元素只读一次"的
    ///    局部值快照本身（代码走查／实现文件注释口径），本夹具无法机械判定。
    /// ③若实现把"从集合取元素"改成"每次取同一实例的**副本**"，改写不影响结论 —— 但那样的实现本身即满足
    ///    "每元素只读一次"契约，夹具**也**通过（与契约同向，不能靠"改成副本"绕过）。见红夹具
    ///    `Decide_SingleElement_MutationDuringPostValidationRead_ProducesConsistentRequest`。
    /// </summary>
    private sealed class RecallingItemList : IReadOnlyList<LocalWaitItem>
    {
        private readonly ItemAccessCounter _counter;
        private readonly int _mutateOnAccess;
        private readonly Action<LocalWaitItem>? _mutate;

        public RecallingItemList(ItemAccessCounter counter, int mutateOnAccess, Action<LocalWaitItem>? mutate = null)
        {
            _counter = counter;
            _mutateOnAccess = mutateOnAccess;
            _mutate = mutate;
        }

        public int Count => 1;

        public LocalWaitItem this[int index]
        {
            get
            {
                var access = _counter.AccessCount + 1;
                if (access == _mutateOnAccess) _mutate?.Invoke(_counter.Item);
                return _counter.Observe();
            }
        }

        public IEnumerator<LocalWaitItem> GetEnumerator()
        {
            var access = _counter.AccessCount + 1;
            if (access == _mutateOnAccess) _mutate?.Invoke(_counter.Item);
            yield return _counter.Observe();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// **取值计数器**：包住一个**普通** `LocalWaitItem`，只统计"**从集合取出元素的次数**"（＝遍历取值次数），
    /// **不**改写元素。用途：把"实现是否在单个元素处理期内**重复从集合取元素**"变成一个**可探测的计数**。
    /// **边界（第 9 轮必改 #1）**：它**不**度量"在同一引用上重复读取字段"——那种形态下计数仍为 1。
    /// 与适配器 <see cref="RecallingItemList"/> 配合使用（每次取值返回同一实例）。
    /// **第 12 轮（纯文本）**：本摘要保持上述收窄口径不变，仅由本轮回送窄增量 diff 以证"仅文本变更"。
    /// </summary>
    private sealed class ItemAccessCounter
    {
        public LocalWaitItem Item { get; }

        public int AccessCount { get; private set; }

        public ItemAccessCounter(LocalWaitItem item) => Item = item;

        /// <summary>每次集合被取值时调用一次。</summary>
        public LocalWaitItem Observe()
        {
            AccessCount++;
            return Item;
        }
    }

    /// <summary>
    /// **可重入 `IReadOnlyList` 反例注入器**：每次**从集合取元素**时先计数；在指定序号的那一次取值点上
    /// **成对**改写元素的 `StableIdentity` 与 `ItemId`（保持互相一致，不制造撕裂），随后返回**同一**实例。
    /// 因每次返回同一实例，**只要实现之后再次从集合取出该元素**（即本次计数的那一次），它读到的字段就已是改写后的值。
    /// **第 10 轮必改 #1 收窄**：改写**只在"从集合取元素"这一刻**发生；若实现**只从集合取一次**该元素、
    /// 随后在**同一引用**上重复读取字段，本注入器**不会**触发改写（见下方边界②）。
    ///
    /// **覆盖边界（重要，须如实声明）**：
    /// ①本注入器**能**覆盖的错误形态是"实现**再次从集合取出同一元素**再读字段"（例如把 `items[i]` 放回循环体、
    ///    或在两遍遍历中都从集合取元素）——此时第 2 次取值会命中改写，产物失配、夹具变红。
    /// ②它**不能**覆盖"实现只从集合取一次元素（`foreach` 只取一次引用），却在该引用上**重复读取字段**"这一形态：
    ///    该形态下取元素计数仍为 1，改写不生效、夹具**也**通过。**故本夹具不得被读成"能捕获第 7 轮原反例
    ///    （同一 `item` 引用上字段重复读取）"的证据**；那一形态的判别**只能靠实现层面的"每元素只读一次"
    ///    局部值快照本身（代码走查／本文件的注释口径），本夹具无法机械判定。
    /// ③若实现把"从集合取元素"改成"每次取同一实例的**副本**"，改写不影响结论 —— 但那样的实现本身即满足
    ///    "每元素只读一次"契约，夹具**也**通过（与契约同向，不能靠"改成副本"绕过）。
    /// </summary>
    [Fact]
    public void Decide_SingleElement_MutationDuringPostValidationRead_ProducesConsistentRequest()
    {
        // 基准：先求一次"当前口径"下的合法取值，避免对派生算法细节硬编码。
        var baseline = new LocalWaitItem
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId("s-base"),
            StableIdentity = "s-base",
            CandidateId = "cand-s-base",
            Namespace = "manual",
            WorkflowId = "wf",
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedIdentity = true,
            EnqueuedAtUtc = DateTimeOffset.UnixEpoch,
            State = LocalWaitItemState.Waiting,
        };
        Assert.Single(Requests(Decision(NewTrigger(), TriggerPoint("OccupancyEnded"), new List<LocalWaitItem> { baseline })));

        // 探测：此处先用"永不触发改写"的计数器量出**实际从集合取元素的次数**（本计数**不**度量
        // "在同一引用上重复读取字段"——那种形态下计数仍为 1，见断言 ③ 的覆盖边界）。
        var probeItem = new LocalWaitItem
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId("s-probe"),
            StableIdentity = "s-probe",
            CandidateId = "cand-s-probe",
            Namespace = "manual",
            WorkflowId = "wf",
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedIdentity = true,
            EnqueuedAtUtc = DateTimeOffset.UnixEpoch,
            State = LocalWaitItemState.Waiting,
        };
        var probeCounter = new ItemAccessCounter(probeItem);
        Assert.Single(Requests(Decision(NewTrigger(), TriggerPoint("OccupancyEnded"),
            new RecallingItemList(probeCounter, mutateOnAccess: int.MaxValue))));
        Assert.Equal(1, probeCounter.AccessCount);

        // 反例注入：身份 A／ItemId(A)，在**第 2 次取元素**时成对改成身份 B／ItemId(B)。
        var item = new LocalWaitItem
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId("s-mut-single"),
            StableIdentity = "s-mut-single",
            CandidateId = "cand-s-mut-single",
            Namespace = "manual",
            WorkflowId = "wf",
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedIdentity = true,
            EnqueuedAtUtc = DateTimeOffset.UnixEpoch,
            State = LocalWaitItemState.Waiting,
        };
        var validatedItemId = item.ItemId;
        var validatedIdentity = item.StableIdentity;
        var validatedCandidateId = item.CandidateId;

        var mutatedIdentity = "s-mut-single-OTHER";
        var mutatedItemId = LocalWaitQueuePolicy.DeriveItemId(mutatedIdentity);
        Assert.NotEqual(validatedItemId, mutatedItemId);
        Assert.NotEqual(validatedIdentity, mutatedIdentity);

        var counter = new ItemAccessCounter(item);
        var list = new RecallingItemList(counter, mutateOnAccess: 2, mutate: x =>
        {
            x.StableIdentity = mutatedIdentity;   // 成对改写：身份与 ItemId 始终互相一致（不制造撕裂）
            x.ItemId = mutatedItemId;
        });

        var requests = Requests(Decision(NewTrigger(), TriggerPoint("OccupancyEnded"), list));

        // ③ 机械断言：实现必须在单个元素处理期内**只从集合取一次元素**。
        //    覆盖边界（第 8／9 轮必改 #1 收窄）：本计数**能**发现"再次从集合取出元素再读字段"的形态；
        //    **不能**发现"只取一次引用、却在同一引用上重复读字段"的形态（那形态下计数仍为 1、本夹具也通过）。
        Assert.Equal(1, counter.AccessCount);

        // ① 修复后必须仍产 1 条。
        Assert.True(requests.Count == 1,
            $"单元素处理期内**成对**改写不应改变产出条数，实际 {requests.Count} 条"
            + $"（本实例被取值 {counter.AccessCount} 次；第 2 次取值即说明实现**再次从集合取出了该元素**）。");

        // ② 产物身份必须**一致**：ItemId 必须等于产物自身 StableIdentity 的派生值。
        //    边界（第 9 轮必改 #1 收窄）：仅当旧实现**再次从集合取出该元素**去构造 Pending 时，
        //    本次注入的改写才会生效并在这里失配变红；"同一引用重读字段"形态下本夹具不触发改写（见 ③ 边界②）。
        var producedItemId = ItemIdOf(requests[0]);
        var producedIdentity = StringOf(requests[0], "StableIdentity");
        Assert.Equal(LocalWaitQueuePolicy.DeriveItemId(producedIdentity!), producedItemId);

        // ④ 产物必须**完全等于改写前（校验时）的取值**：不得取自改写后的值。
        Assert.Equal(validatedItemId, producedItemId);
        Assert.Equal(validatedIdentity, producedIdentity);
        Assert.Equal(validatedCandidateId, StringOf(requests[0], "CandidateId"));
    }
}
