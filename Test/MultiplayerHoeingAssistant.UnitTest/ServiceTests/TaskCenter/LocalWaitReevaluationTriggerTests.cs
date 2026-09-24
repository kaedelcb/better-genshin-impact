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
        var matched = candidates.FirstOrDefault(m => m.GetParameters().Length == args.Length);
        Assert.True(matched is not null, $"{type.Name}.{method} 没有 {args.Length} 个参数的重载。");
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
    /// **幂等键必须与等待项标识同一口径**：等待项的 `ItemId` 由 `LocalWaitQueuePolicy.DeriveItemId`
    /// 派生，触发器的去重键必须能**唯一指回该等待项**（否则「同一等待项重复触发不得重复产」无法成立）。
    /// 本夹具断言：同稳定身份下，键与 `ItemId` 一一对应且稳定（允许不同前缀，但不得碰撞）。
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
        // 同身份 ⇒ 同键；异身份 ⇒ 异键（键到等待项的映射必须单射）
        Assert.Equal(keys.Count, keys.Zip(itemIds).Select(p => p.First).Distinct().Count());
    }

    /// <summary>
    /// **同口径摘要（会诊 #6 处置）**：`ReevaluationKey` 与 `LocalWaitQueuePolicy.DeriveItemId` 的关系是
    /// 「**同一** SHA-256、**同一** 16 位十六进制前缀长度，仅字面前缀不同（`reval-` vs `wait-`）」。
    /// 因此同稳定身份下**摘要逐字符相同**必须可机械断言——若某人把触发器的摘要长度、大小写或归一化口径改掉
    /// （例如截断到 8 位、改用大写、或对身份做额外加盐），此夹具立刻变红。
    ///
    /// **边界**：本断言只覆盖"同口径、同前缀长度、无系统性别名"，**不**主张密码学强度或全空间零碰撞。
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

            var trigger = NewTrigger();
            var requests = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"),
                store.Load().ToList()));
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
        Assert.Contains("epoch-a", keyA!);
        Assert.Contains("epoch-b", keyB!);

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
        using var gate = new ManualResetEventSlim(false);
        var ready = new CountdownEvent(threads);
        var collected = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<Exception>();

        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            try
            {
                ready.Signal();
                gate.Wait();
                var requests = Requests(Decision(trigger, TriggerPoint("OccupancyEnded"), items));
                foreach (var r in requests) collected.Add(ItemIdOf(r) ?? "");
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToList();

        foreach (var w in workers) w.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "并发线程未能全部就绪。");
        gate.Set();
        foreach (var w in workers) Assert.True(w.Join(TimeSpan.FromSeconds(30)), "并发线程未在时限内结束。");

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
        using var gate = new ManualResetEventSlim(false);
        var ready = new CountdownEvent(threads);
        var requests = new ConcurrentBag<object>();

        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            ready.Signal();
            gate.Wait();
            foreach (var r in Requests(Decision(trigger, TriggerPoint("StartupRecovery"), items)))
                requests.Add(r);
        })).ToList();

        foreach (var w in workers) w.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
        gate.Set();
        foreach (var w in workers) Assert.True(w.Join(TimeSpan.FromSeconds(30)));

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

        var decision = Invoke(trigger, "Decide", TriggerPoint("OccupancyEnded"), (object)items, DateTimeOffset.UnixEpoch, cts.Token);
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
}
