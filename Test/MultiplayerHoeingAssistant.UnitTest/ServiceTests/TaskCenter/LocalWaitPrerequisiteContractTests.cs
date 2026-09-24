using System.Reflection;
using System.Threading;
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
/// <summary>
/// [第四轮会诊处置] 本类的两个 **DEBUG 探针**用例（<see cref="LocalWaitPrerequisiteContractTests."/>
/// 中的 `Persist_MaterializedSnapshot_...` 与 `Persist_WriteSnapshotMaterialization_...`）通过
/// **进程级静态字段** `LocalWaitQueueStore.WriteSnapshotProbeMutator` 注入窗口。
/// 该字段在 xUnit 默认并行下会被**其它 collection 的用例**在「挂上 → 触发写盘」之间的窗口内并发读写，
/// 导致本行探针被**别人的调用**抢先消费、或被提前清空 ⇒ 夹具**假红**（全量负载下已实测）。
/// 故把本类单独放进一个 collection，使本类的用例与其它 collection **不并发**；
/// 并发写入则由探针内的「捕获一次」(CAS) 守卫兜住：只有**第一个**调用者能生效，
/// 其余并发调用一律**立即返回**，从而不会改写／吞掉本行的目标记录。
/// </summary>
[CollectionDefinition("LocalWaitSnapshotProbe", DisableParallelization = true)]
public sealed class LocalWaitSnapshotProbeCollection
{
}

[Collection("LocalWaitSnapshotProbe")]
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
    /// **显式传入「无求值器」的保守语义代理物**：本批**废除**了「无 evaluator 的旧签名」的**语义**
    /// （签名按批约束保留、调用即抛；见 `SelectNext(items)` 重载）。
    /// **注意口径**（会诊 R6／R7／R8 重要项）：本方法返回的是一个**非空**委托，其结论恒为「不可判定」——
    /// 它证明的是「求值器给出不可判定 ⇒ 不参选」，**不**证明「求值器为真正的 `null`」这条分支；
    /// 后者由 `SelectNext_NullEvaluatorExactly_IsConservative`（透传真正的 <c>null</c>）单独证明。
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
        Assert.True(overloads.Count >= 1,
            "LocalWaitQueuePolicy 必须提供 `SelectNext(IReadOnlyList<LocalWaitItem>, <只读 evaluator>)` 重载"
            + "（选择阶段须由 evaluator 求就绪，而**不是**读过期布尔；当前 2 参重载数=" + overloads.Count + "。");
        // 按**第二参数精确类型**取重载：owner 裁定 A 保留了**旧签名** `SelectNext(items)`
        // 作为二进制入口（`[Obsolete(error: true)]` + 抛异常），故不能再用「参数个数」反推旧签名已消失。
        var method = overloads.Single(m =>
            m.GetParameters()[1].ParameterType == RequireType(EvaluatorDelegateTypeName));
        // 双重保证：被取到的必须是接受**只读 evaluator**的那个重载。
        Assert.Equal(RequireType(EvaluatorDelegateTypeName), method.GetParameters()[1].ParameterType);
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

        // **批次 16／D2**：旧签名 `SelectNext(items)` 按批约束「冻结合同只允许纯加法」**保留为公开签名**，
        // 但其**语义**已废除（owner 裁决原文未提及该重载处置；保留只为不让签名集合做减法）。
        // 测试必须钉死两件事：①该重载仍在（签名不做减法）；②它**不**能被当作「缺引用即就绪」的退路——调用即抛异常。
        var legacyOverload = typeof(LocalWaitQueuePolicy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "SelectNext" && m.GetParameters().Length == 1);
        var obsolete = legacyOverload.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false)
            .Cast<ObsoleteAttribute>().SingleOrDefault();
        Assert.True(obsolete is not null && obsolete.IsError,
            "批次 16／D2：旧 `SelectNext(items)` 必须标注 "
            + "`[Obsolete(..., error: true)]`，以**编译期**封死任何源码调用点。");
        var legacyThrown = Record.Exception(() =>
            legacyOverload.Invoke(null, [new List<LocalWaitItem>()]));
        Assert.True(legacyThrown is not null,
            "批次 16／D2：旧 `SelectNext(items)` 不得实现旧语义——"
            + "它必须抛异常，而**不能**在缺前置引用时静默退回「已就绪」"
            + "（§24.106 C4 的恒 true 硬编码即为该缺陷）。");
        // 收紧断言（会诊 R8 建议 1）：仅「有异常」不足以证明「调用即抛」——绑定错误也会产生异常。
        // 必须检查反射包装后的**内层异常**是 `NotSupportedException`（即方法体真的执行并主动抛出）。
        var legacyInner = (legacyThrown as TargetInvocationException)?.InnerException ?? legacyThrown;
        Assert.IsType<NotSupportedException>(legacyInner);
        Assert.Null(SelectNextWithEvaluator([noReference], NoEvaluator()));
        Assert.Null(SelectNextWithEvaluator([Item("s-plain", ArbitrationTier.System, 99)], NoEvaluator()));
    }

    /// <summary>
    /// **无 evaluator 时不得再恒为就绪**：`SelectNext(items, null)`（显式「无求值器」）在项携带引用时必须
    /// 走保守默认。反例：现状 `ToWaitingFacts` 硬编码 `PrerequisiteReady = true` ⇒ 该断言失败（红）。
    /// 批次 16／D2：旧签名按批约束**保留为公开签名**但调用即抛异常（不可用），
    /// 真正的 `null` 求值器路径见 `SelectNext_NullEvaluatorExactly_IsConservative`。
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
        // 会诊第二轮（重要）：不得按**参数个数**盲选重载。若日后出现另一个同参数个数重载
        // （如「只读视图重载」），按个数取到的可能是另一个重载而本夹具**假绿**。
        // 故必须按**第二参数精确类型**（模型层 evaluator 委托）选型。
        var method = methods.Single(m => m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType == RequireType(EvaluatorDelegateTypeName));
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
    // ⑥′ 写入侧不变量的**真正**判据（第二轮会诊「必改 2」）：写出即读回
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **不变量：已写出的文件必定可被 `Load` 读回**（第二轮会诊「必改 2」）。
    ///
    /// 会诊指出：只在**函数入口**校验一次**可变对象**不够 —— 若对象在「校验之后、序列化之前」被改写为
    /// 非法形状（键存在但空白），仍会写出 `Load` 拒读的文件。本夹具以**确定性**方式复现「校验后、
    /// 写盘前对象被改写」这一交错：`Upsert` 会把**调用方持有的同一个对象实例**加入队列，因此
    /// 在 `Upsert` **返回之后**立即把该实例改写成非法形状，再触发**下一次必然写盘**的路径
    /// （`PersistCleanup`）——夹具并不断言「不可能被改写」，而是断言**不变量**：
    /// 任何一次写盘之后，`Load()` 必须成功，且文件里**不得**出现「键存在但空白」的形状。
    ///
    /// **判别力（诚实口径）**：本夹具只证明「**存量写盘路径** 不会把调用方后续改写泄露到磁盘」，
    /// **不足以**证明写前物化本身的判别力（该窗口无法从公开 API 触发：现有写路径均先 `Load()` 出新对象再写）。
    /// 写前物化的**反向突变判别力**由 <see cref="Persist_WriteSnapshotMaterialization_IsLoadBearing"/> 以 DEBUG 专用探针取证。
    /// </summary>
    [Fact]
    public void Upsert_MutationAfterEntryValidation_NeverWritesAFileThatLoadRejects()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waitq-write-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            var item = Item("s-race");
            SetPrerequisiteReference(item, "ref-valid");
            Assert.True(store.Upsert(item));

            // ①「入口校验之后」改写同一实例 → 下一次写盘必须仍然写出可读回的文件
            item.PrerequisiteReference = "   ";
            var second = Item("s-race-second");
            SetPrerequisiteReference(second, "ref-second");
            Assert.True(store.Upsert(second)); // 该路径写盘：写前必须按物化后的值判定
            Assert.NotNull(store.Load());

            // ②触发 cancel 写盘路径（写的是内存对象，非入口参数）
            store.PersistCleanup(i => i.StableIdentity == "s-race" ? "test" : null, DateTimeOffset.UtcNow);

            var loaded = store.Load(); // 任何一次写盘都必须留下可读回的文件
            // null（缺字段／v1 兼容）是**合法**形状；被拒的只是「键存在但空白」这一形状。
            Assert.DoesNotContain(loaded,
                i => i.PrerequisiteReference is not null && string.IsNullOrWhiteSpace(i.PrerequisiteReference));
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ⑥‴ 写前物化的**全字段校验**（第三轮会诊「必改 1」）：把「已写出必可读回」真正做实
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **不变量：已写出的文件必定可被 `Load` 读回 —— 对全部必需字段成立，而非只对引用字段成立**（第三轮会诊「必改 1」）。
    ///
    /// 第三轮会诊指出：<c>MaterializeAndValidatePayload</c> 只校验了 <c>PrerequisiteReference</c>，
    /// 调用方仍可在入口校验后、物化前把同一对象的 <c>ItemId</c> 改成空白／把 <c>Tier</c> 改成未定义值／
    /// 制造重复 <c>ItemId</c>，副本会照样写出这些非法值，随后 <c>Load()</c> 拒读。
    ///
    /// 本夹具用同一 DEBUG 专用探针把上述改写注入「读盘之后、写盘之前」的窗口（该窗口在探针所处位置即成立），
    /// 逐例断言：①写盘被**响亮拒绝**；②原文件**逐字节不变**（零副作用）；③<c>Load()</c> 仍成功。
    ///
    /// **判别力**：把 <c>MaterializeAndValidatePayload</c> 中的 <c>ValidatePersistableItemShape(...)</c>
    /// 与重复 <c>itemId</c> 去重整段删去（只保留原「校验引用」一项）⇒ 本夹具必红；保留则绿。
    ///
    /// **第三轮会诊 #1 补的行（新一轮）**：<c>empty-itemid-reactivation</c> 覆盖**重新激活**写盘路径——
    /// 该路径是**另一条会再次写盘**的分支（`Upsert` 命中「已取消的同载荷墓碑」时把 `existing.State` 置回
    /// `Waiting` 后再次 `Persist`），且它写的是 `Load().ToList()` 出来的那条 `existing`、**不是**调用方传入的 `item`
    /// （本批实现**不**把调用方实例放进批内——这是实测到的事实更正，原先此处「把调用方实例放进批内」的写法是错的）。
    /// 因此本行的目的是：证明**该分支也**同样经过写前全字段形状校验，而非重复 `empty-itemid` 的语义。
    ///
    /// **证据帧口径**：DEBUG 探针（<c>WriteSnapshotProbeMutator</c>）与引用它的夹具行都只在 **Debug** 构建存在；
    /// **Release** 构建下探针与依赖它的 5 条（<c>empty-itemid</c>／<c>undefined-tier</c>／<c>undefined-state</c>／
    /// <c>duplicate-itemid</c>／<c>empty-itemid-reactivation</c>）由 <c>[Fact]</c> 上的
    /// <c>#if DEBUG</c> 行守卫（Release 构建下不生成 ⇒ 不会因缺探针而编译失败）。
    /// 本夹具的**绿色帧**为 Debug 帧；Release 构建仅作**编译可用性**核对（不声称 Release 下跑过这些探针行）。
    /// </summary>
#if DEBUG
    // DEBUG 专用探针（`LocalWaitQueueStore.WriteSnapshotProbeMutator`）只在 Debug 构建存在；
    // 本方法依赖它注入「读盘后、写盘前」的窗口。Release 构建下探针字段不存在，故**整个方法**门控。
    // 本方法的**绿色证据帧**因此是 Debug 帧（见 §24.111 与本节 XML 注释）。
    [Theory]
    [InlineData("empty-itemid")]
    [InlineData("undefined-tier")]
    [InlineData("undefined-state")]
    [InlineData("duplicate-itemid")]
    [InlineData("empty-itemid-reactivation")]
    public void Persist_MaterializedSnapshot_IsValidatedAsAWhole_PerLoadShapeRules(string mutation)
    {
        var dir = Path.Combine(Path.GetTempPath(), "waitq-whole-shape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            // **每一行用互不相同的身份**（按突变名派生）：避免与同 Theory 其它行共享静态探针时互相命中，
            // 也避免上一行残留的探针在下一行的批内「碰巧」匹配到同名记录（全量负载下已实测到这一干扰）。
            var rowTag = mutation;
            var baselineIdentity = "s-whole-baseline-" + rowTag;
            var baseline = Item(baselineIdentity);
            SetPrerequisiteReference(baseline, "ref-whole-baseline-" + rowTag);
            Assert.True(store.Upsert(baseline)); // 首次登记：探针此时尚未挂上
            var before = File.ReadAllBytes(store.FilePath);

            // ItemId 由稳定身份确定性派生 ⇒ 必须**实证**换一个身份确实得到不同的 ItemId，
            // 否则本夹具会因「第二条与既有项同 Id ⇒ 被幂等路径拦下、根本走不到写盘」而假绿。
            var secondIdentity = "s-whole-second-" + rowTag;
            var second = Item(secondIdentity);
            Assert.NotEqual(baseline.ItemId, second.ItemId);
            Assert.NotEqual(baseline.StableIdentity, second.StableIdentity);
            SetPrerequisiteReference(second, "ref-whole-second");
            // [第四轮会诊处置] 与 `baseline` 逐字段比对：除身份/候选号/itemId/计划载荷外，**只**让
            // `enqueuedAtUtc` 成为「批内唯一可匹配键」。原实现里 `second.StableIdentity` 虽含突变名，但
            // 与 `Item(baselineIdentity)` 仅差一个前缀 ⇒ 不能作为唯一键：同一 Theory 的其它行（共享静态探针）
            // 或上一行的残留探针会**碰巧匹配**到本行记录，把改写落到错误对象上 ⇒ 探针自证失败（全量负载下已实测）。
            // 故此处把「唯一可匹配键」显式钉在 `enqueuedAtUtc` 这个每实例不同的时刻上，并在使用前自证其非空。
            var secondEnqueuedAtUtc = new DateTimeOffset(2026, 9, 24, 12, 34, 56, TimeSpan.Zero);
            second.EnqueuedAtUtc = secondEnqueuedAtUtc;
            Assert.True(second.EnqueuedAtUtc == secondEnqueuedAtUtc,
                "本夹具要求 second 携带**可作唯一键**的 enqueuedAtUtc（用于批内定位）；若该字段被改写作其它值，定位将失效。");

            // [第三轮会诊 #1 补] `empty-itemid-reactivation` 覆盖**重新激活**写盘路径：先登记 second，
            // 再把它清理成 `Cancelled` 墓碑；随后同身份同载荷的再次 `Upsert` 会走重新激活分支并再次写盘。
            // 该分支写的是 `Load` 出来的 `existing` 记录（**不是**调用方实例）；该路径同样必须经写前全字段校验，
            // 否则「激活后写回」这一步可能写出 `Load` 读不回的文件。
            if (mutation == "empty-itemid-reactivation")
            {
                Assert.True(store.Upsert(second));
                store.PersistCleanup(i => string.Equals(i.StableIdentity, secondIdentity, StringComparison.Ordinal)
                    ? "test-cancel" : null, DateTimeOffset.UtcNow);
                // 断言确实是 Cancelled 墓碑（否则下面那次 Upsert 会走幂等「已等待」路径、不写盘 ⇒ 空转）
                Assert.Contains(store.Load(), i => string.Equals(i.StableIdentity, secondIdentity, StringComparison.Ordinal)
                    && i.State == LocalWaitItemState.Cancelled);
                // 该路径自身已经写过一次盘 ⇒ 「写盘前」的基线必须在此**之后**重新取样，
                // 否则断言会落成「与更早的文件比」而假红（本轮已实测到这一假红来源）。
                before = File.ReadAllBytes(store.FilePath);
            }

            // 探针只在**第二次** Upsert（登记 second、真正把它写进这一批）时挂上；
            // 探针内**不得**使用会自己抛异常的脚手架断言 —— 否则 `Assert.ThrowsAny` 会把
            // 「探针脚手架抛的异常」误当成「守卫响亮拒绝」（本轮已实测到这一假绿）。
            // 因此：探针只做**最小改写**（改写前用局部变量读出旧值做自证），任何自证失败以
            // 独立的 `probeFailed` 标记回报，断言阶段再区分「守卫拒绝」与「探针没跑成」。
            var probeRan = 0;
            string? probeNote = null;
            // [第四轮会诊处置] **捕获一次**（CAS）守卫：本字段是**进程级静态**，默认并行下可能被
            // 其它 collection 的写盘路径并发调用。若不加守卫，第二个调用者会（a）在第一次改写之后
            // 再次改写（改写落到已被校验的副本上，无意义），(b) 更糟的是把本行的目标值改掉后
            // 「自证位数」对不上。故只有**第一个**调用者执行探针体，其余立即返回。
            var thisRowAction = (Action<List<LocalWaitItem>>)(items =>
            {
                // [本批自审修正] 实现在物化**前**先复制一份快照（`new List<LocalWaitItem>(items)`），
                // 再遍历**副本**做校验。因此探针**必须**把改写落到**批内副本已持有的那个实例**上、
                // 并且**不得**新增条目（新增条目在复制之后不会进入被校验的副本 ⇒ 探针就成了空转）。
                // 故此处对调用方同一批的每个实例逐一（用引用身份）匹配后改写；`probeRan` 只在**改动确实生效**时置 1。
                probeRan = 0;
                if (mutation == "empty-itemid-reactivation")
                {
                    // [第三轮会诊 #1 补] **重新激活**写盘路径：`Upsert` 命中已取消的同载荷墓碑时，
                    // 走的是 `Load().ToList()` 出来的那条 `existing`（**不是**调用方传入的 `item`）——
                    // 本批实现**不**把调用方实例放进批内（这是本轮实测到的事实修正：原假设「重新激活把 item 放进批内」是错的）。
                    // 因此这里与其余突变同法：**按内容**定位批内那条 `second` 记录，当场把其 `ItemId` 改成**空串**。
                    // 该批随后会连同激活后的 `existing` 一起写盘 ⇒ 若写前不校验 `itemId`，空串被写出 ⇒ `Load` 拒读 ⇒ 本行红。
                    var matchedInBatch = 0;
                    foreach (var victim in items)
                    {
                        // 唯一键：只认 enqueuedAtUtc（本行实例独有）；不得退回身份串匹配。
                        if (victim.EnqueuedAtUtc == secondEnqueuedAtUtc)
                        {
                            matchedInBatch++;
                            victim.ItemId = string.Empty;
                            if (victim.ItemId.Length == 0) probeRan = 1;
                        }
                    }
                    if (matchedInBatch != 1)
                        probeNote = "重新激活路径：批内唯一键命中 " + matchedInBatch + " 条（期望 1，突变：" + mutation + "）";
                    return;
                }
                // [本批自审修正 2] 实现走的是 `Load().ToList()` 路径：写盘批**只含**「`Load` 出来的记录
                // ＋本次 `item`」，`baseline` 这个**调用方实例**默认**不在**批内（只有重新激活分支才会命中它）。
                // 因此探针一律**按内容定位**「批内那条 baseline 记录」（而不是按引用身份），并对**它**改写；
                // 对 `second` 一例同理按 `ItemId` 定位，确保改写一定落在**将被校验的那一份**上。
                // 本夹具只改「不是 second 的那条」（=基线条），用以制造**同批内两条记录**的非法形状，
                // 从而在去重之前就由形状校验拦下；不改 `second`，避免与 `empty-itemid` 以外的突变语义混淆。
                foreach (var victim in items.ToList())
                {
                    var isSecond = string.Equals(victim.ItemId, second.ItemId, StringComparison.Ordinal)
                        && string.Equals(victim.StableIdentity, second.StableIdentity, StringComparison.Ordinal);
                    if (mutation == "empty-itemid")
                    {
                        // 触发写盘的那条（second）自身携带**空串** ItemId。
                        // 读取侧规则是 `RequiredString`：`Length > 0` 即可读回；空白串**能被读回**，
                        // 故「空白 itemId」不是读取侧判损坏的形状。真正会被 `Load` 拒读的是**空串**。
                        if (isSecond) { victim.ItemId = ""; if (victim.ItemId.Length == 0) probeRan = 1; }
                        continue;
                    }

                    if (!isSecond) // 批内唯一「不是 second」的那条 = baseline
                    {
                        switch (mutation)
                        {
                            case "undefined-tier":
                                victim.Tier = (ArbitrationTier)987;
                                if (victim.Tier == (ArbitrationTier)987) probeRan = 1;
                                break;
                            case "undefined-state":
                                victim.State = (LocalWaitItemState)987;
                                if (victim.State == (LocalWaitItemState)987) probeRan = 1;
                                break;
                            case "duplicate-itemid":
                                // 同一批内**第二条**（同 Id、同载荷）只能这样制造：把基线条的 StableIdentity
                                // 改成与 second 相同，使 `DeriveItemId` 派生出同一个 ItemId ⇒ 写前去重必须拦下。
                                // 键是 `ItemId`、去重也按 `ItemId`：必须**显式**把基线条的 `ItemId` 改成
                                // 与 second 相同。本批实现**不**在写入侧把 `ItemId` 重新派生自 `StableIdentity`
                                // （`Upsert`/`Persist` 都不调用 `DeriveItemId`），故只改 `StableIdentity` 不会
                                // 让两条记录同 Id ⇒ 该突变会变成空转（本轮已实测到这一假红来源）。
                                victim.ItemId = second.ItemId;
                                if (string.Equals(victim.ItemId, second.ItemId, StringComparison.Ordinal))
                                    probeRan = 1;
                                break;
                        }
                    }
                }
                if (probeRan == 0) probeNote = "探针对批内记录未产生任何生效改写（突变：" + mutation + "）";
            });
            // 让探针体本身只跑第一个调用者（并发抢占时后续调用直接返回，不产生任何改写）。
            var firstCallerGate = 1;
            var guardedAction = (Action<List<LocalWaitItem>>)(items =>
            {
                if (Interlocked.CompareExchange(ref firstCallerGate, 0, 1) == 1) thisRowAction(items);
            });
            // [第五轮会诊 #2 重要] **先**构造好带守卫的委托，**再一次** CAS 装上——
            // 不得先发布无守卫的委托、随后用普通赋值换成守卫版：两步之间若有写盘调用，会执行到**无守卫**的探针，
            // 从而破坏注释所声称的「只有第一个调用者生效」这一保证（本轮会诊指出的真缺口，已修）。
            var installed = false;
            for (var spin = 0; spin < 20000 && !installed; spin++)
                installed = Interlocked.CompareExchange(ref LocalWaitQueueStore.WriteSnapshotProbeMutator, guardedAction, null) is null;
            Assert.True(installed, "本行未能挂上写盘探针（进程级静态字段被并发占用且未及时释放）。");

            Exception? caught = null;
            try
            {
                store.Upsert(second);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                // 只清空**自己装上的那个引用**（避免并发下清掉别人的探针）。
                Interlocked.CompareExchange(ref LocalWaitQueueStore.WriteSnapshotProbeMutator, null, guardedAction);
            }

            // ①探针必须确实跑成（否则本夹具不具判别力，直接红，不得靠脚手架异常充数）
            Assert.Null(probeNote);
            Assert.Equal(1, probeRan);
            // ②必须是**守卫**的响亮拒绝（而不是任何其它异常）
            Assert.IsType<LocalWaitQueueCorruptException>(caught);

            Assert.Equal(before, File.ReadAllBytes(store.FilePath));
            Assert.NotNull(store.Load());
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
#endif

    // ─────────────────────────────────────────────────────────────────────────────
    // ⑥″ 写前物化的**判别力取证**（第二轮会诊「必改 2」）：DEBUG 专用探针 + 反向突变
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **写前物化是承重的（load-bearing）**：第二轮会诊「必改 2」要求证明「写盘前把引用物化为不可变快照」
    /// 这一处理真的在起作用，而不只是「看起来更安全」。
    ///
    /// 取证方式：用 <c>LocalWaitQueueStore.WriteSnapshotProbeMutator</c>（**仅 DEBUG 编译存在**）在
    /// `Persist` 校验之前把调用方实例的引用改写成**非法形状**（键存在但空白）。此时：
    /// - 若实现保留写前物化（现状）⇒ 物化时读出的是空白值 ⇒ **在任何写盘动作之前**拒绝 ⇒ 文件逐字节不变、`Load()` 仍成功；
    /// - 若把 `MaterializeAndValidatePayload(items)` 突变回 `items`（直接序列化调用方对象）⇒ 非法值被写出
    ///   ⇒ `Load()` 抛异常（本夹具红）。
    ///
    /// 该反向突变已实测：突变后本夹具红；还原后全绿（见 §24.111 落地登记）。
    /// </summary>
#if DEBUG
    // DEBUG 专用探针门控（同 Persist_MaterializedSnapshot_...；Release 下探针字段不存在）：
    // 整段方法（含特性与签名）都在 DEBUG 内，Release 下不生成，避免出现「有签名无方法体」。
    [Fact]
    public void Persist_WriteSnapshotMaterialization_IsLoadBearing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waitq-snapshot-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalWaitQueueStore(dir);
            var baseline = Item("s-probe-baseline");
            SetPrerequisiteReference(baseline, "ref-probe-baseline");
            Assert.True(store.Upsert(baseline));
            var before = File.ReadAllBytes(store.FilePath);

            // 探针挂在「Persist 校验之前」：把即将被写出的那一批里的合法引用改写成非法形状。
            LocalWaitQueueStore.WriteSnapshotProbeMutator = items =>
            {
                foreach (var each in items)
                    if (each.StableIdentity == "s-probe-baseline") SetPrerequisiteReference(each, "   ");
            };
            try
            {
                // 触发一次必然写盘：写前物化 ⇒ 读到空白 ⇒ 拒绝（异常），且拒绝发生在**任何**写盘动作之前。
                var second = Item("s-probe-second");
                SetPrerequisiteReference(second, "ref-probe-second");
                Assert.ThrowsAny<Exception>(() => store.Upsert(second)); // 探针改写的是**已在队列中**的实例 ⇒ 走「复用/重激活」分支
            }
            finally
            {
                LocalWaitQueueStore.WriteSnapshotProbeMutator = null;
            }

            // 不变量：拒绝零副作用 ⇒ 既有文件逐字节不变，且仍可读回。
            Assert.Equal(before, File.ReadAllBytes(store.FilePath));
            var loaded = store.Load();
            Assert.DoesNotContain(loaded,
                i => i.PrerequisiteReference is not null && string.IsNullOrWhiteSpace(i.PrerequisiteReference));
        }
        finally
        {
            if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
#endif
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
        // 会诊第二轮（重要）：同上，不得按参数个数盲选，按**第二参数精确类型**取重载。
        var method = methods.Single(m => m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType == RequireType(EvaluatorDelegateTypeName));

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
                // 会诊第二轮（重要）：同上，不得按参数个数盲选，按**第二参数精确类型**取重载。
            var method = typeof(LocalWaitQueuePolicy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Contains("Revalidate", StringComparison.OrdinalIgnoreCase) && m.IsPublic)
                .Where(m => m.ReturnType == decisionType)
                .Single(m => m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == RequireType(EvaluatorDelegateTypeName));
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

    /// <summary>
    /// **[会诊 R6／R7／R8 重要项处置] 真正的 `null` 求值器**：上面的 `NoEvaluator()` 传的是**非空**委托，
    /// 不能证明「未注入求值器（<c>null</c>）」这条保守分支。本夹具把**真正的 <c>null</c>** 透传给
    /// `SelectNext(items, evaluator)` 与 `RevalidateBeforeSend(item, evaluator)`，钉死：
    /// ①项带引用时仍**不可判定 ⇒ 不参选**（不得退回 C4 的恒 true）；
    /// ②该结论恒**不可判定**且 `RequiresFullAdmission` 恒 true（无发送许可、无抛出自异常）；
    /// ③发送前验算的结论 `RequiresFullAdmission` 恒 true（无发送许可）。
    /// 本夹具**不**引入任何生产消费点，**零发送**。
    /// </summary>
    [Fact]
    public void SelectNext_NullEvaluatorExactly_IsConservative()
    {
        var referenced = Item("s-null-ref", ArbitrationTier.System, 99);
        SetPrerequisiteReference(referenced, "wf|node|ticket");

        // 直接把真正的 null 作为第二参数：选择阶段不得参选、不得恒就绪
        Assert.Null(SelectNextWithEvaluator([referenced], null!));
        Assert.Null(SelectNextWithEvaluator([Item("s-null-plain", ArbitrationTier.System, 99)], null!));

        // 发送前再次验算：null 求值器必须响亮失败（不得据此取得发送许可）
        var revalidate = typeof(LocalWaitQueuePolicy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "RevalidateBeforeSend")
            .Single(m => m.GetParameters()[1].ParameterType == RequireType(EvaluatorDelegateTypeName));
        var decision = revalidate.Invoke(null, [referenced, null]);
        var decisionType = RequireType("MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision");
        Assert.True(decisionType.IsInstanceOfType(decision), "RevalidateBeforeSend 必须返回模型层决策对象。");
        var readinessProp = decisionType.GetProperty("Readiness")!;
        var requiresFullAdmission = decisionType.GetProperty("RequiresFullAdmission")!;
        // **null 求值器**下必须保守落「不可判定」，绝不能因缺求值器而回退「已就绪」。
        var readinessName = readinessProp.GetValue(decision)!.ToString()!;
        Assert.NotEqual("Ready", readinessName);
        Assert.Equal(UndeterminedName(), readinessName);
        // 结论永远不含发送许可：唯一合法后继是重新走完整准入。
        Assert.True((bool)requiresFullAdmission.GetValue(decision)!, "发送前验算不得给出发送许可。");
    }
}

