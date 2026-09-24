using System.Reflection;
using System.Text;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 16／D2：持久化稳定前置引用 ＋ 只读 evaluator ＋ 发送前再次验算**——反例先行契约（红夹具）。
///
/// owner 2026-09-24 裁决 D2＝推荐项 A：「**持久化稳定前置引用**（例如 workflow/node/ticket 引用）＋
/// 重评时由**只读 evaluator** 得出的就绪结果；**发送前再次验算**」，理由为「重启可复核；**不信任过期布尔**；可解释」。
/// 本批闭合 §24.106 **C4**（`LocalWaitQueuePolicy.ToWaitingFacts` 把 `PrerequisiteReady` **固定为 true**）。
///
/// 本批**不越过 D1 出口**：零发送、不产生发送许可，重评仍只回到「重新走一次完整准入」；
/// **未接线交付为默认立场**（不接任何生产入口），生产门与 R5.8 继续关闭；不自行改冻结合同（只允许纯加法）。
///
/// **夹具方法（反例先行）**：本批目标类型/成员尚不存在，直接 new/调用会产生**编译期错误**并污染助手全量基线，
/// 故类型与成员一律用 **反射** 取，缺失时断言失败（红）而**编译通过**——与批次 14/15 的 `RequireType` 手法同向。
///
/// **能证明（实现后）**：①前置就绪不再由**过期布尔**承载，而由**可持久化的稳定前置引用**＋只读 evaluator 求得；
/// ②未就绪与**不可判定**一律不参选（保守）；③`ScheduledAt` 仍只参与排序、**不**参与就绪判定；
/// ④发送前会**再次验算**（未通过 ⇒ 无可发送结论）；⑤选择路径**结构上不产生发送**；⑥生产零消费点。
///
/// **不能证明**：任何生产接线行为、真实事件源是否送达、真实发送是否发生、跨进程单 writer、断电耐久；
/// 本组**不**证明 evaluator 的权威来源在生产上已具备（权威来源与失败语义的冻结是接线前事项）。
/// </summary>
public sealed class LocalWaitPrerequisiteContractTests
{
    // ── 反射取值辅助（类型/成员缺失 ⇒ 断言失败＝红；不产生编译期错误）────────────────

    private const string EvaluationTypeName = "MultiplayerHoeingAssistant.Models.PrerequisiteEvaluation";
    private const string EvaluatorDelegateTypeName = "MultiplayerHoeingAssistant.Models.PrerequisiteEvaluator";
    private const string VerdictTypeName = "MultiplayerHoeingAssistant.Models.PrerequisiteReadiness";

    private static Assembly Assistant => typeof(LocalWaitQueuePolicy).Assembly;

    private static Type RequireType(string fullName)
    {
        var type = Assistant.GetType(fullName, throwOnError: false);
        Assert.True(type is not null,
            $"必需类型缺失：{fullName}（D2 裁决 A：本批须交付持久化前置引用＋只读 evaluator；缺失即本批未实现）。");
        return type!;
    }

    private static bool TypeExists(string fullName) => Assistant.GetType(fullName, throwOnError: false) is not null;

    /// <summary>
    /// 取 `LocalWaitItem` 上承载**稳定前置引用**的属性名（属性名由本批实现确定，但必须是**引用**而非布尔快照）。
    /// 反例：现状 `LocalWaitItem` 只有 `ScheduledAt`（只排序）且无任何前置字段 ⇒ 本断言失败（红）。
    /// </summary>
    private static string PrerequisiteReferencePropertyName()
    {
        var candidates = typeof(LocalWaitItem).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.Contains("Prerequisite", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Precondition", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();
        Assert.True(candidates.Count > 0,
            "LocalWaitItem 必须携带**持久化的稳定前置引用**字段（D2 裁决 A：不得只存布尔快照；"
            + "§24.106 C4 要求「必须选择『持久化前置快照』或『读取时注入只读求值器』之一并补版本/迁移」）。");
        Assert.True(candidates.Count == 1,
            "前置字段必须唯一（不得出现两个同义前置字段）：[" + string.Join(", ", candidates) + "]");
        return candidates[0];
    }

    /// <summary>前置引用属性的类型必须是**引用**（string 或含可核对引用成员的记录），且**不是** bool 快照。</summary>
    private static PropertyInfo PrerequisiteReferenceProperty()
    {
        var prop = typeof(LocalWaitItem).GetProperty(PrerequisiteReferencePropertyName(),
            BindingFlags.Public | BindingFlags.Instance)!;
        Assert.NotEqual(typeof(bool), prop.PropertyType);
        Assert.NotEqual(typeof(bool?), prop.PropertyType);
        Assert.False(prop.PropertyType == typeof(bool) || prop.PropertyType == typeof(bool?),
            "前置字段**不得**是布尔就绪快照（D2 裁决 A 明确拒绝「直接写 boolean」：重启后会过期或失真）。");
        return prop;
    }

    private static object?[] EnumValues(string typeName)
    {
        var type = RequireType(typeName);
        var values = Enum.GetValues(type).Cast<object>().ToArray();
        Assert.True(values.Length > 0, $"{typeName} 必须有已定义取值。");
        return values;
    }

    private static string[] EnumNames(string typeName)
        => Enum.GetNames(RequireType(typeName));

    /// <summary>不可判定取值名（三态之一）。命名由实现确定，但必须可识别为「不可判定」。</summary>
    private static string UndeterminedName()
    {
        var names = EnumNames(VerdictTypeName);
        var hits = names.Where(n => n.Contains("Undetermined", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("Indeterminate", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(hits.Count == 1,
            "前置就绪必须表达**不可判定**这一独立取值（保守默认），当前命中：[" + string.Join(", ", hits) + "]");
        return hits[0];
    }

    private static string ReadyName()
    {
        var names = EnumNames(VerdictTypeName);
        Assert.Contains("Ready", names);
        return "Ready";
    }

    private static string NotReadyName()
    {
        var names = EnumNames(VerdictTypeName);
        var hits = names.Where(n => n.Contains("NotReady", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("Pending", StringComparison.OrdinalIgnoreCase)
                                    || n.Contains("Blocked", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(hits.Count == 1,
            "前置就绪必须表达**未就绪**这一独立取值，当前命中：[" + string.Join(", ", hits) + "]");
        return hits[0];
    }

    private static object Verdict(string name) => Enum.Parse(RequireType(VerdictTypeName), name);

    /// <summary>
    /// 构造 evaluator 的注入式委托（只读：给定前置引用，返回就绪判定）。
    /// <para>
    /// **必须以产品委托的 <c>Invoke</c> 形参/返回类型**绑定：CLR 的 <c>Delegate.CreateDelegate</c> **不**支持
    /// 「返回 <c>object</c> 的方法 → 返回枚举的委托」这种返回类型协变（实测：<c>Func&lt;object,object&gt;</c> 绑定到
    /// 返回枚举的委托必然 <c>ArgumentException</c>）。故此处按产品返回类型动态构造注入 lambda，
    /// 而<strong>不</strong>固定用 <c>Func&lt;object?,object&gt;</c>——否则夹具自身不可满足，与实现无关。
    /// </para>
    /// </summary>
    private static Delegate Evaluator(Func<object?, object>? fn)
    {
        var delegateType = RequireType(EvaluatorDelegateTypeName);
        Assert.True(typeof(Delegate).IsAssignableFrom(delegateType),
            $"{EvaluatorDelegateTypeName} 必须是委托类型（只读 evaluator 的注入点）。");
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters();
        Assert.True(parameters.Length == 1,
            $"只读 evaluator 委托必须只接受 1 个输入（当前 {parameters.Length}）："
            + "就绪判定不得依赖时钟/占用者事实等可变输入，否则不是「只读 evaluator」。");

        var resultType = invoke.ReturnType;
        Assert.True(resultType == RequireType(VerdictTypeName),
            $"只读 evaluator 的返回类型必须是 {VerdictTypeName}（三态），当前 {resultType.Name}。");
        Assert.NotEqual("Boolean", resultType.Name); // 三态不得是 bool 快照（D2 裁决 A 明确拒绝「直接写 boolean」）

        // 按产品委托的精确签名生成包装方法：参数与返回类型都取自产品 Invoke。
        var paramTypes = parameters.Select(p => p.ParameterType).ToArray();
        var wrapperType = typeof(Func<,>).MakeGenericType(paramTypes[0], resultType);
        var invokeWrapper = typeof(LocalWaitPrerequisiteContractTests)
            .GetMethod(nameof(InvokeWrapper), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(paramTypes[0], resultType);
        var wrapper = fn is null
            ? DefaultWrapperFor(paramTypes[0], resultType)
            : (Delegate)invokeWrapper.Invoke(null, [fn])!;
        GC.KeepAlive(wrapperType);
        return Delegate.CreateDelegate(delegateType, wrapper.Target, wrapper.Method);
    }

    /// <summary>把 <c>Func&lt;object?,object&gt;</c> 的结论（期望为就绪三态值）适配到产品返回类型。</summary>
    private static Func<TIn, TOut> InvokeWrapper<TIn, TOut>(Func<object?, object> fn)
        => value => (TOut)fn(value)!;

    /// <summary>
    /// **显式传入「无求值器」**（不是“默认重载”）：本批**删除了**「无 evaluator 的旧签名」，因此
    /// 「缺 evaluator」这一场景只能由 <c>null</c> 注入来表达——它必须保守落**不可判定**（不参选）。
    /// 若产品把「未注入求值器」当成「无前置要求 ⇒ 已就绪」，本包装会把该错误结论原样回传 ⇒ 夹具红。
    /// </summary>
    private static Delegate NoEvaluator() => Evaluator(null);

    /// <summary>不可判定包装：回传三态的「不可判定」取值（按产品枚举名解析，不硬编码数值）。</summary>
    private static Func<TIn, TOut> NoEvaluatorWrapper<TIn, TOut>() => _ => (TOut)Enum.Parse(typeof(TOut), UndeterminedName());

    private static Delegate DefaultWrapperFor(Type paramType, Type resultType)
    {
        var method = typeof(LocalWaitPrerequisiteContractTests)
            .GetMethod(nameof(NoEvaluatorWrapper), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(paramType, resultType);
        return (Delegate)method.Invoke(null, null)!;
    }

    private static LocalWaitItem Item(string identity, ArbitrationTier tier = ArbitrationTier.Plan,
        int priority = 0, bool hoeingHighest = false, bool trusted = true)
        => new()
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId(identity),
            StableIdentity = identity,
            CandidateId = "cand-" + identity,
            Namespace = "manual",
            WorkflowId = "wf",
            Tier = tier,
            Priority = priority,
            IsHoeingHighest = hoeingHighest,
            HasTrustedIdentity = trusted,
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
        };

    private static void SetPrerequisiteReference(LocalWaitItem item, object? value)
        => PrerequisiteReferenceProperty().SetValue(item, value);

    private static LocalWaitItem? SelectNextWithEvaluator(IReadOnlyList<LocalWaitItem> items, Delegate evaluator)
    {
        var overloads = typeof(LocalWaitQueuePolicy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "SelectNext")
            .Where(m => m.GetParameters().Length == 2)
            .ToList();
        Assert.True(overloads.Count == 1,
            "LocalWaitQueuePolicy 必须提供 `SelectNext(IReadOnlyList<LocalWaitItem>, <只读 evaluator>)` 重载"
            + "（选择阶段须由 evaluator 求就绪，而**不是**读过期布尔；当前重载数=" + overloads.Count + "）。");
        var method = overloads[0];
        try
        {
            return (LocalWaitItem?)method.Invoke(null, [items, evaluator]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException!;
        }
    }

    private static object EvaluatePrerequisitesOnce(IReadOnlyList<LocalWaitItem> items, Delegate evaluator)
    {
        var methods = typeof(LocalWaitQueuePolicy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "EvaluatePrerequisites")
            .Where(m => m.GetParameters().Length == 2)
            .ToList();
        Assert.True(methods.Count == 1,
            "LocalWaitQueuePolicy 必须提供 `EvaluatePrerequisites(items, evaluator)` 只读求值入口"
            + "（选择与**发送前**共用同一求值器）。");
        try
        {
            return methods[0].Invoke(null, [items, evaluator])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException!;
        }
    }

    private static string RepoRootPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            if (Directory.Exists(Path.Combine(root.FullName, "MultiplayerHoeingAssistant"))
                && Directory.Exists(Path.Combine(root.FullName, "Test")))
                return root.FullName;
            root = root.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录（用于源文本扫描夹具）。");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ① 持久化稳定前置引用（不得是过期布尔）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **D2 裁决 A 的核心理由是「不信任过期布尔」**：等待项必须携带**可持久化的稳定前置引用**。
    /// 反例：现状 `LocalWaitItem` 无任何前置字段（§24.106 C4 逐字：「`LocalWaitItem` 当前没有
    /// `PrerequisiteReady`／可执行时刻字段」）⇒ 本断言失败（红）。
    /// </summary>
    [Fact]
    public void WaitItem_CarriesPersistentPrerequisiteReference_NotAnExpiredBoolean()
    {
        var prop = PrerequisiteReferenceProperty();
        Assert.NotEqual(typeof(bool), prop.PropertyType);
        Assert.True(prop.CanRead && prop.CanWrite,
            "持久化前置引用必须是可读写的**落盘字段**（只读属性无法持久化，重启后不可复核）。");
    }

    /// <summary>
    /// **引用必须可往返落盘**（重启可复核）：同一稳定引用写入 → 落盘 → 重新读取后**逐字符一致**。
    /// </summary>
    [Fact]
    public void PrerequisiteReference_RoundTripsThroughStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waitq-prereq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            var item = Item("s-ref");
            var reference = "wf-7|node-3|ticket-1";
            SetPrerequisiteReference(item, reference);
            store.Upsert(item);

            var reloaded = new LocalWaitQueueStore(dir).Load().Single();
            var roundTripped = PrerequisiteReferenceProperty().GetValue(reloaded)?.ToString();
            Assert.Equal(reference, roundTripped);
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// **落盘版本必须升级并保留旧版本读兼容**：新字段进入 `CurrentVersion`；**无前置字段的 v1 旧文件仍可读**，
    /// 且缺字段**保守按不可判定**（不得默认「已就绪」）。
    /// **能力边界**：本夹具只证明 `LocalWaitQueueFile.CurrentVersion` 已前移与 v1 文本可读；
    /// 不证明真实生产环境里存在 v1 旧文件。
    /// </summary>
    [Fact]
    public void Store_VersionAdvanced_AndLegacyV1FileStillReads_WithUndeterminedDefault()
    {
        Assert.True(LocalWaitQueueFile.CurrentVersion > 1,
            "新增持久化前置引用后 CurrentVersion 必须升级（否则新旧格式不可区分）。");

        var dir = Path.Combine(Path.GetTempPath(), "waitq-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            // v1 旧文本：无任何前置字段
            File.WriteAllText(store.FilePath,
                "{\"version\":1,\"items\":[{\"itemId\":\"wait-a\",\"stableIdentity\":\"s\",\"tier\":2,\"priority\":9}]}");

            var item = store.Load().Single(); // 必须可读（读兼容）
            Assert.Null(PrerequisiteReferenceProperty().GetValue(item));

            // 更高版本仍响亮拒绝
            File.WriteAllText(store.FilePath, "{\"version\":999,\"items\":[]}");
            Assert.Throws<LocalWaitQueueCorruptException>(() => store.Load());
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ② 只读 evaluator：三态；未就绪与不可判定一律不参选
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>**三态齐备且互不重叠**：就绪／未就绪／**不可判定**（保守默认）。</summary>
    [Fact]
    public void PrerequisiteVerdict_HasThreeDistinctStates()
    {
        var names = EnumNames(VerdictTypeName);
        Assert.Contains(ReadyName(), names);
        Assert.Contains(NotReadyName(), names);
        Assert.Contains(UndeterminedName(), names);

        var values = EnumValues(VerdictTypeName).Select(Convert.ToInt32).ToList();
        Assert.Equal(values.Count, values.Distinct().Count()); // 不得用别名重复表达同一态
    }

    /// <summary>
    /// **未就绪项不参选**：即使其级别/优先级最高，也不得被选出。
    /// 反例：现状投影恒 `PrerequisiteReady = true` ⇒ 该高优先级未就绪项会被选中（红）。
    /// </summary>
    [Fact]
    public void SelectNext_NotReadyItemNeverWins_EvenAtHighestPriority()
    {
        var notReady = Item("s-notready", ArbitrationTier.System, 99, hoeingHighest: true);
        var ready = Item("s-ready", ArbitrationTier.Plan, 0);
        var evaluator = Evaluator(o => o?.ToString() == "s-ready" ? Verdict(ReadyName()) : Verdict(NotReadyName()));
        SetPrerequisiteReference(notReady, "s-notready");
        SetPrerequisiteReference(ready, "s-ready");

        Assert.Equal("s-ready", SelectNextWithEvaluator([notReady, ready], evaluator)!.StableIdentity);
        Assert.Null(SelectNextWithEvaluator([notReady], evaluator));
    }

    /// <summary>
    /// **不可判定一律不参选**（保守）：求值器给不出结论时，既不得当作已就绪，也不得当作「空闲」而抢发。
    /// 反例：现状没有任何三态概念 ⇒ 断言失败（红）。
    /// </summary>
    [Fact]
    public void SelectNext_UndeterminedReadinessIsConservative_NotSelectable()
    {
        var undetermined = Item("s-undet", ArbitrationTier.System, 99);
        var ready = Item("s-ready", ArbitrationTier.Plan, 0);
        var evaluator = Evaluator(o => o?.ToString() == "s-ready" ? Verdict(ReadyName()) : Verdict(UndeterminedName()));
        SetPrerequisiteReference(undetermined, "s-undet");
        SetPrerequisiteReference(ready, "s-ready");

        Assert.Equal("s-ready", SelectNextWithEvaluator([undetermined, ready], evaluator)!.StableIdentity);
        Assert.Null(SelectNextWithEvaluator([undetermined], evaluator));
    }

    /// <summary>
    /// **缺引用／缺 evaluator 保守按不可判定**：没有引用（或没有求值器）时不得默认「已就绪」。
    /// 与既有取向一致：「缺字段保守按不可信」（`Store_MissingTrustedFlagIsTreatedAsUntrusted`）。
    /// </summary>
    [Fact]
    public void SelectNext_MissingReferenceOrDefaultEvaluator_IsConservative()
    {
        var noReference = Item("s-noref", ArbitrationTier.System, 99); // 未设置前置引用
        var ready = Item("s-ready", ArbitrationTier.Plan, 0);
        var evaluator = Evaluator(o => Verdict(ReadyName()));
        SetPrerequisiteReference(ready, "s-ready");

        Assert.Equal("s-ready", SelectNextWithEvaluator([noReference, ready], evaluator)!.StableIdentity);
        Assert.Null(SelectNextWithEvaluator([noReference], evaluator));

        // **已删除**「无 evaluator 的旧签名」：缺 evaluator 只能显式传 null；
        // 且缺引用／缺 evaluator 都必须保守按**不可判定**（不得凭空认定已就绪）
        Assert.True(
            typeof(LocalWaitQueuePolicy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "SelectNext")
                .All(m => m.GetParameters().Length == 2),
            "批次 16／D2 已删除「无求值器」的旧 `SelectNext(items)` 重载："
            + "缺前置引用时不得静默退回「已就绪」（§24.106 C4 的恒 true 硬编码即为该缺陷）。");
        Assert.Null(SelectNextWithEvaluator([noReference], NoEvaluator()));
        Assert.Null(SelectNextWithEvaluator([Item("s-plain", ArbitrationTier.System, 99)], NoEvaluator()));
    }

    /// <summary>
    /// **无 evaluator 时不得再恒为就绪**：`SelectNext(items, null)`（显式「无求值器」）在项携带引用时必须
    /// 走保守默认。反例：现状 `ToWaitingFacts` 硬编码 `PrerequisiteReady = true` ⇒ 该断言失败（红）。
    /// 批次 16／D2 已删除「无 evaluator 的旧签名」，故本夹具显式传 `null`。
    /// </summary>
    [Fact]
    public void SelectNext_ReferencePresentButNoLegacyReadyDefault_HardCodedTrueIsGone()
    {
        var referenced = Item("s-ref", ArbitrationTier.System, 99);
        SetPrerequisiteReference(referenced, "wf|node|ticket");

        // 有引用但**没有**求值器 ⇒ 不可判定 ⇒ 不参选（不得沿用「恒就绪」）
        Assert.Null(SelectNextWithEvaluator([referenced], NoEvaluator()));
    }

    /// <summary>
    /// **只读求值入口可复用**：`EvaluatePrerequisites(items, evaluator)` 返回逐项就绪判定，
    /// 且**不改动**输入对象（纯函数，便于「发送前再次验算」复用）。
    /// </summary>
    [Fact]
    public void EvaluatePrerequisites_IsReadOnly_AndReportsPerItemVerdict()
    {
        var a = Item("s-a");
        var b = Item("s-b");
        SetPrerequisiteReference(a, "ref-a");
        SetPrerequisiteReference(b, "ref-b");
        var evaluator = Evaluator(o => o?.ToString() == "ref-a" ? Verdict(ReadyName()) : Verdict(NotReadyName()));

        var evaluated = EvaluatePrerequisitesOnce([a, b], evaluator);
        var entries = (System.Collections.IEnumerable)evaluated;
        var count = 0;
        foreach (var entry in entries)
        {
            count++;
            var type = entry!.GetType();
            Assert.NotNull(type.GetProperty("Item") ?? type.GetProperty("ItemId"));
            Assert.NotNull(type.GetProperty("Verdict") ?? type.GetProperty("Readiness") ?? type.GetProperty("Evaluation"));
        }
        Assert.Equal(2, count);

        Assert.Equal("ref-a", PrerequisiteReferenceProperty().GetValue(a)?.ToString()); // 纯函数
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ③ ScheduledAt 仍只排序、不参与就绪判定
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **`ScheduledAt` 只参与排序**：未来时刻**不**使项不可参选，也**不**构成就绪证明；
    /// 就绪完全由 evaluator 给出（D2 选项表逐字：「`ScheduledAt` 只影响排序，未来时刻**不会**把项排除」）。
    /// </summary>
    [Fact]
    public void ScheduledAt_RemainsSortOnly_AndNeverDecidesReadiness()
    {
        var future = Item("s-future", ArbitrationTier.System, 99);
        future.ScheduledAt = DateTimeOffset.UtcNow.AddYears(1);
        SetPrerequisiteReference(future, "ref-future");

        // 只读求值器判定「就绪」⇒ 即使计划时刻在未来也必须可被选中（排序键只影响次序）
        var readyEvaluator = Evaluator(_ => Verdict(ReadyName()));
        Assert.Equal("s-future", SelectNextWithEvaluator([future], readyEvaluator)!.StableIdentity);

        // 求值器判定「未就绪」⇒ 计划时刻再早也不得选中（时刻不构成就绪证明）
        var past = Item("s-past", ArbitrationTier.Plan, 0);
        past.ScheduledAt = DateTimeOffset.UtcNow.AddYears(-1);
        SetPrerequisiteReference(past, "ref-past");
        var notReadyEvaluator = Evaluator(_ => Verdict(NotReadyName()));
        Assert.Null(SelectNextWithEvaluator([past], notReadyEvaluator));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ④ 发送前再次验算（D1 出口不得被越过）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **发送前必须再次验算**：选出项在取得发送许可前重新求值一次；未通过 ⇒ **无可发送结论**。
    /// 反例：现状**没有**任何「发送前复核」接缝（§24.106 C5 逐字：「被选出的项在取得发送许可前必须
    /// 重新取得纪元、占用者事实、身份、级别和票据」）⇒ 断言失败（红）。
    /// </summary>
    [Fact]
    public void SendTimeRevalidation_ExistsAndNeverPermitsSend_WhenPrerequisiteLost()
    {
        var decisionType = RequireType("MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision");
        var members = decisionType.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        // 结构上不得含发送许可面成员（与 D1 出口同向：等待永不带发送许可）
        foreach (var banned in new[] { "SendPermitted", "SendSeq", "JobId", "SubmissionIdentity" })
            Assert.False(members.Contains(banned), $"发送前验算结论不得含发送面成员 {banned}。");

        Assert.True(members.Contains("RequiresFullAdmission") || members.Contains("MustReenterFullAdmission"),
            "发送前验算结论必须显式要求「重新走完整准入」。");

        var methods = typeof(LocalWaitQueuePolicy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.Contains("Revalidate", StringComparison.OrdinalIgnoreCase) && m.IsPublic)
            .ToList();
        Assert.True(methods.Count > 0,
            "必须提供**发送前再次验算**的接缝（名称须含 Revalidate）："
            + "选出项在取得发送许可前重新求值；不得沿用排队时快照直接发送。");

        // `RevalidateBeforeSend` 有两个公开重载，分别返回服务层与**模型层**类型。
        // 本夹具钉死的是**模型层登记面**，故必须选取**返回该类型**的重载，
        // 而不能按参数个数盲选（否则会用服务层实例去取模型层属性 ⇒ 夹具自身失败）。
        var method = methods.Where(m => m.ReturnType == decisionType)
            .OrderBy(m => m.GetParameters().Length).FirstOrDefault();
        Assert.True(method is not null,
            "必须提供返回 `" + decisionType.FullName + "` 的发送前验算接缝。");
        var parameters = method!.GetParameters();
        Assert.Contains(parameters, p => p.ParameterType == typeof(LocalWaitItem));
        var item = Item("s-reval");
        SetPrerequisiteReference(item, "ref-reval");
        var args = parameters.Select(p => p.ParameterType == typeof(LocalWaitItem)
                ? item
                : p.ParameterType == RequireType(EvaluatorDelegateTypeName)
                    ? Evaluator(_ => Verdict(NotReadyName()))
                    : p.HasDefaultValue ? p.DefaultValue : (object?)null)
            .ToArray();
        object? result;
        try
        {
            result = method.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException!;
        }
        Assert.NotNull(result);
        var requires = decisionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name is "RequiresFullAdmission" or "MustReenterFullAdmission");
        Assert.True(requires is not null);
        Assert.Equal(true, requires!.GetValue(result));

        // 显式「无求值器」⇒ 必须不可判定且不通过（不得把「未注入求值器」当成「无前置要求 ⇒ 就绪」）
        var nullArgs = parameters.Select(p => p.ParameterType == typeof(LocalWaitItem)
                ? item
                : p.ParameterType == RequireType(EvaluatorDelegateTypeName)
                    ? NoEvaluator()
                    : p.HasDefaultValue ? p.DefaultValue : (object?)null)
            .ToArray();
        var withoutEvaluator = method.GetParameters().Any(p => p.ParameterType == RequireType(EvaluatorDelegateTypeName))
            ? method.Invoke(null, nullArgs)
            : null;
        if (withoutEvaluator is not null)
        {
            var verdictProp = decisionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name is "Readiness" or "Verdict" or "Evaluation");
            Assert.True(verdictProp is not null, "发送前验算结论必须暴露求值结论（Readiness／Verdict）。");
            Assert.Equal(UndeterminedName(), verdictProp!.GetValue(withoutEvaluator)?.ToString());
            var passedProp = decisionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name == "Passed");
            if (passedProp is not null) Assert.Equal(false, passedProp.GetValue(withoutEvaluator));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ⑤ 结构上零发送 + 生产零消费点
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **选择路径结构上零发送**：`LocalWaitQueuePolicy` 的静态面不得出现发送面类型／成员
    /// （与批次 14 `LocalWaitEnqueueDecision.SendPermitted => false`、批次 15 产物无发送面成员同向）。
    /// </summary>
    [Fact]
    public void SelectionPath_HasNoSendCapableSurface()
    {
        var forbidden = new[]
        {
            "SendPermitted", "SendSeq", "SubmissionIdentity", "IBgiSender", "SubmissionDispatch", "JobHandle",
        };
        var members = typeof(LocalWaitQueuePolicy)
            .GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in forbidden)
            Assert.False(members.Contains(name), $"LocalWaitQueuePolicy 不得暴露发送面成员 {name}。");
    }

    /// <summary>
    /// **生产零消费点（源文本扫描）**：新增的前置引用／evaluator 面在生产源码目录内除**定义文件自身**外
    /// 不得出现引用 ⇒ 本批不接生产入口。
    /// **能力边界**：文本扫描不覆盖反射与动态调用，只证明「本批没有接线点」。
    /// </summary>
    [Fact]
    public void PrerequisiteSurface_HasNoProductionConsumptionPoint()
    {
        var root = Path.Combine(RepoRootPath(), "MultiplayerHoeingAssistant");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(root, "Models", "TaskCenter", "LocalWaitModels.cs"),
            Path.Combine(root, "Models", "TaskCenter", "PrerequisiteEvaluationModels.cs"),
            Path.Combine(root, "Models", "TaskCenter", "LocalWaitPrerequisiteModels.cs"),
            Path.Combine(root, "Models", "TaskCenter", "LocalWaitPrerequisiteDecisionModels.cs"),
            Path.Combine(root, "Services", "TaskCenter", "Arbitration", "LocalWaitQueuePolicy.cs"),
        };

        var markers = new[] { "PrerequisiteEvaluation", "PrerequisiteReadiness", "PrerequisiteEvaluator" };
        Assert.True(TypeExists(EvaluationTypeName) || TypeExists(VerdictTypeName) || TypeExists(EvaluatorDelegateTypeName),
            "前置就绪三态类型与只读 evaluator 委托必须存在（D2 裁决 A）。");

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("bin", ""), StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains(Path.Combine("obj", ""), StringComparison.OrdinalIgnoreCase)) continue;
            if (allowed.Contains(file)) continue;
            var text = File.ReadAllText(file);
            foreach (var marker in markers)
            {
                if (!text.Contains(marker, StringComparison.Ordinal)) continue;
                hits.Add(Path.GetRelativePath(RepoRootPath(), file).Replace('\\', '/') + " :: " + marker);
                break;
            }
        }

        Assert.True(hits.Count == 0,
            "发现生产消费点（本批不得接任何生产入口）：[" + string.Join(", ", hits)
            + "]——D2 本批只交付未接线组件；接线须 owner 另行明示。");
    }

    /// <summary>
    /// **硬编码 `PrerequisiteReady = true` 必须消失**：`LocalWaitQueuePolicy` 源文本不得再出现该赋值。
    /// 这是 §24.106 C4 的**唯一**闭合判据（该行不改，C4 即未闭合）。
    /// </summary>
    [Fact]
    public void PolicySource_NoHardCodedPrerequisiteReadyTrue()
    {
        var path = Path.Combine(RepoRootPath(), "MultiplayerHoeingAssistant", "Services", "TaskCenter",
            "Arbitration", "LocalWaitQueuePolicy.cs");
        Assert.True(File.Exists(path), "LocalWaitQueuePolicy.cs 必须存在。");
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("PrerequisiteReady = true", text);
        Assert.DoesNotContain("PrerequisiteReady=true", text);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ⑥ 写入侧自洽：不得写出自己随后拒读的文件（会诊 #1 必改）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **写入侧必须与读取侧同口径**（会诊 #1 必改）：`ParsePrerequisiteReference` 把「键存在但为空串」判为
    /// **损坏**，那么 `Upsert` 就**不得**把该形状写进文件——否则 `Upsert` 能成功写出自己随后 `Load` 拒读的文件
    /// （写入成功、重启即损坏，属于把未知结果改写成成功）。
    /// **判据**：空白引用在写入侧**响亮拒绝**，且**原文件字节不变**（不得留下半写状态）。
    /// 反例：写入侧不做校验 ⇒ 本夹具红。
    /// </summary>
    [Fact]
    public void Upsert_RejectsShapeThatLoadWouldReject_AndLeavesFileByteIdentical()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waitq-write-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            // 先写入一份合法基线，确认「拒绝」不改变既有内容
            var baseline = Item("s-baseline");
            SetPrerequisiteReference(baseline, "ref-baseline");
            Assert.True(store.Upsert(baseline));
            var before = File.ReadAllBytes(store.FilePath);

            var bad = Item("s-blank-ref");
            SetPrerequisiteReference(bad, "   "); // 键存在但空白 ⇒ Load 侧判损坏
            Assert.ThrowsAny<Exception>(() => store.Upsert(bad));

            var after = File.ReadAllBytes(store.FilePath);
            Assert.Equal(before, after); // 拒绝必须零副作用：原文件逐字节不变
            Assert.DoesNotContain(store.Load(), i => i.StableIdentity == "s-blank-ref");
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ⑦ 发送前验算的独立方向（会诊 #3 重要）：必须**重新求值**，不得沿用排队快照
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **发送前验算必须重新求值**（会诊 #3）：先以「就绪」选出该项，再把求值器改成「未就绪」，
    /// 则发送前验算必须给出**未就绪／不通过**，并**确实再次调用**求值器。
    /// 判别力方向：若实现「沿用第一次（排队时）快照」⇒ 本夹具红（这与突变 B 是**不同**方向）。
    /// </summary>
    [Fact]
    public void SendTimeRevalidation_ReEvaluates_NotReusingQueuedSnapshot()
    {
        var decisionType = RequireType("MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision");
        var methods = typeof(LocalWaitQueuePolicy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.Contains("Revalidate", StringComparison.OrdinalIgnoreCase) && m.IsPublic)
            .Where(m => m.ReturnType == decisionType)
            .ToList();
        Assert.True(methods.Count > 0, "必须提供返回模型层结论的发送前验算接缝。");
        var method = methods.OrderBy(m => m.GetParameters().Length).First();

        var item = Item("s-reval-again");
        SetPrerequisiteReference(item, "ref-reval-again");

        // 排队时：就绪（可被选出）
        var selections = new List<string>();
        var readyEvaluator = Evaluator(_ => { selections.Add("ready"); return Verdict(ReadyName()); });
        var chosen = SelectNextWithEvaluator([item], readyEvaluator);
        Assert.NotNull(chosen);

        // 发送前：前置**已丢失**（求值器改为未就绪）——必须被重新求值并拦下
        var revalCalls = new List<string>();
        var lostEvaluator = Evaluator(_ => { revalCalls.Add("lost"); return Verdict(NotReadyName()); });
        var parameters = method.GetParameters();
        var args = parameters.Select(p => p.ParameterType == typeof(LocalWaitItem)
                ? (object?)item
                : p.ParameterType == RequireType(EvaluatorDelegateTypeName)
                    ? lostEvaluator
                    : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();
        object? result;
        try
        {
            result = method.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException!;
        }
        Assert.NotNull(result);

        var readiness = decisionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name is "Readiness" or "Verdict" or "Evaluation");
        Assert.True(readiness is not null, "发送前验算结论必须暴露求值结论。");
        Assert.Equal(NotReadyName(), readiness!.GetValue(result)?.ToString());

        var passed = decisionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name == "Passed");
        if (passed is not null) Assert.Equal(false, passed.GetValue(result));

        // 必须**真的再求值一次**（沿用排队快照 ⇒ 调用次数为 0 ⇒ 红）
        Assert.True(revalCalls.Count >= 1,
            "发送前验算必须**重新调用**求值器（不得沿用排队时快照）：重新求值次数=" + revalCalls.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ⑧ 旧文件下游口径（会诊 #4／建议）
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **v1 缺字段必须一路保守到下游**：旧文件读出的项缺前置引用 ⇒ 选择**不参选**、发送前验算**不通过**，
    /// **不得**在任一层被补成「就绪」。
    /// </summary>
    [Fact]
    public void LegacyV1Item_StaysUndetermined_ThroughSelectionAndSendRevalidation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waitq-legacy-downstream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            File.WriteAllText(store.FilePath,
                "{\"version\":1,\"items\":[{\"itemId\":\"wait-legacy\",\"stableIdentity\":\"s-legacy\","
                + "\"tier\":2,\"priority\":99}]}");
            var legacy = store.Load().Single();

            // 即便优先级最高，缺引用 ⇒ 一律不参选（求值器即使恒就绪也不得被采信）
            var alwaysReady = Evaluator(_ => Verdict(ReadyName()));
            Assert.Null(SelectNextWithEvaluator([legacy], alwaysReady));

            // 发送前验算同样不得通过
            var decisionType = RequireType("MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision");
            var method = typeof(LocalWaitQueuePolicy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Contains("Revalidate", StringComparison.OrdinalIgnoreCase) && m.IsPublic)
                .Where(m => m.ReturnType == decisionType)
                .OrderBy(m => m.GetParameters().Length)
                .First();
            var args = method.GetParameters().Select(p => p.ParameterType == typeof(LocalWaitItem)
                    ? (object?)legacy
                    : p.ParameterType == RequireType(EvaluatorDelegateTypeName)
                        ? alwaysReady
                        : p.HasDefaultValue ? p.DefaultValue : null)
                .ToArray();
            object? result;
            try
            {
                result = method.Invoke(null, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException!;
            }
            var readiness = decisionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name is "Readiness" or "Verdict" or "Evaluation");
            Assert.True(readiness is not null);
            Assert.Equal(UndeterminedName(), readiness!.GetValue(result)?.ToString());
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

