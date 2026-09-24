using System.Reflection;
using System.Text;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 14／D1：新增 `AdmissionResultKind.WaitLocally`（不含发送许可）**——反例先行契约（红夹具）。
///
/// owner 2026-09-24 裁决 D1＝推荐项 A：新增独立等待结果值，语义唯一，**不授予发送许可**，
/// 与「可重试拒绝」「待抢占确认」严格区分。本批**只加值＋四处调用方分流显式闭环**，
/// **不接生产入口、不产生发送、不解除生产门**。
///
/// **能证明**：①枚举含且仅含一个语义唯一的等待值；②该值经现有调用方分流函数时落到**显式等待分支**
/// （不是 `_ =&gt;` 未知兜底、也不是「确定拒绝/可重试拒绝」）；③等待分支的文本不宣称「已发送/已执行/
/// 执行失败/结果不可考」；④等待值不携带发送许可（无 `SubmissionIdentity`/`SendSeq` 语义）。
///
/// **不能证明**：任何生产接线行为、真实发送是否发生、触发器时序、跨进程耐久；也不证明调用方
/// 在真实运行中会执行等待登记（本批未接入口）。本组不构成对 D2/D3/D4 的背书。
/// </summary>
public sealed class AdmissionWaitLocallyContractTests
{
    /// <summary>等待值在本仓库里的唯一合法名称（D1 裁决文本固定）。</summary>
    private const string WaitValueName = "WaitLocally";

    private static AdmissionResultKind WaitKind()
    {
        var field = typeof(AdmissionResultKind).GetField(WaitValueName, BindingFlags.Public | BindingFlags.Static);
        Assert.True(field is not null,
            $"AdmissionResultKind 必须新增等待值 {WaitValueName}（D1 裁决 A）；缺失即本批未实现。");
        return (AdmissionResultKind)field!.GetValue(null)!;
    }

    /// <summary>
    /// 适配层等待态的**运行期**取值：缺失时断言失败（红），**不产生编译期 CS0117**——
    /// 否则旧实现下整个助手测试工程无法编译，全量回归基线会被污染。
    /// </summary>
    private static ExternalStartAdmissionStatus WaitStatusKind()
    {
        var field = typeof(ExternalStartAdmissionStatus).GetField(WaitValueName, BindingFlags.Public | BindingFlags.Static);
        Assert.True(field is not null,
            $"ExternalStartAdmissionStatus 必须新增等待态 {WaitValueName}（D1：适配层须能穷尽处置等待结论）。");
        return (ExternalStartAdmissionStatus)field!.GetValue(null)!;
    }

    private static AdmissionResultKind[] AllKinds() => Enum.GetValues<AdmissionResultKind>();

    // ── ① 枚举面：存在且唯一、语义词与发送/失败词分离 ─────────────────────────────

    /// <summary>
    /// **等待值必须存在**（D1 的唯一落地形态）。
    /// 反例：现状枚举无等待值 ⇒ 本断言失败（红），直到实现补上该值。
    /// </summary>
    [Fact]
    public void AdmissionResultKind_HasWaitLocallyValue()
    {
        Assert.Contains(WaitValueName, Enum.GetNames<AdmissionResultKind>());
    }

    /// <summary>
    /// **等待值唯一**：不得出现第二个「等待/本地等待」语义的枚举值（例如 `WaitLocally2`、
    /// `WaitLocallyPending`、`LocalWait`）。语义唯一是 D1 选 A 而非「以原因码区分」的核心理由。
    /// </summary>
    [Fact]
    public void AdmissionResultKind_HasExactlyOneWaitSemanticValue()
    {
        var waitLike = Enum.GetNames<AdmissionResultKind>()
            .Where(n => n.Contains("Wait", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal([WaitValueName], waitLike);
    }

    /// <summary>
    /// **等待值不得与「发送已受理 / 执行失败 / 取消」同义折叠**：等待是「尚未进入发送面」的本地登记，
    /// 既不是受理，也不是已证实的执行失败，也不是取消。
    /// </summary>
    [Fact]
    public void AdmissionResultKind_WaitIsDistinctFromSendAndFailureSemantics()
    {
        var wait = WaitKind();
        foreach (var other in new[]
                 {
                     AdmissionResultKind.Accepted,
                     AdmissionResultKind.ExecutionFailed,
                     AdmissionResultKind.Cancelled,
                     AdmissionResultKind.TerminalRejected,
                     AdmissionResultKind.RetryableRejected,
                     AdmissionResultKind.NeedPreemptConfirm,
                 })
        {
            Assert.NotEqual(other, wait);
        }
    }

    /// <summary>
    /// **等待值必须区分于「未知/待对账」**：`NeedReconcile`/`Reconciling`/`Error` 表达「事实不可考」，
    /// 等待表达「已确定未发送地排队」——两者在调用方分流上必须走**不同**分支（下面分别断言）。
    /// </summary>
    [Fact]
    public void AdmissionResultKind_WaitIsDistinctFromUnknownFacts()
    {
        var wait = WaitKind();
        Assert.NotEqual(AdmissionResultKind.NeedReconcile, wait);
        Assert.NotEqual(AdmissionResultKind.Reconciling, wait);
        Assert.NotEqual(AdmissionResultKind.Error, wait);
        Assert.NotEqual(AdmissionResultKind.NotSelected, wait);
        Assert.NotEqual(AdmissionResultKind.F11Blocked, wait);
    }

    // ── ② 调用方分流：四处必须显式闭环，不得落进未知兜底 ───────────────────────────

    /// <summary>
    /// **分流①：节点后继提交三态映射**（`TaskCenterHost.MapAdmissionResultToBoundary`）。
    /// 等待值是**门面给出的确定结论**（已确定未发送），但**不是**普通终局拒绝，也**不是**可重试拒绝；
    /// 必须落到显式等待分支：既不 `Uncertain`（不猜成功也不猜失败），也不开重试窗口（开窗口会诱发重发）。
    /// **反例**：现状 `_ =&gt; UnknownWith(...)` 兜底会把等待误判为「事实不可考」（红）。
    /// </summary>
    [Fact]
    public void SuccessorMapping_WaitLocally_TakesExplicitWaitBranch_NotUnknownFallback()
    {
        var mapped = TaskCenterHost.MapAdmissionResultToBoundary(new AdmissionResult
        {
            Kind = WaitKind(),
            ReasonCode = "local_wait",
            Detail = "低优先级本地持久等待（未获准入前零发送）。",
        });

        Assert.False(mapped.Uncertain, "等待是确定结论（已确定未发送地排队），不得降级为「事实不可考」。");
        Assert.False(mapped.Accepted);
        Assert.False(mapped.Retryable, "等待**不得**开重试窗口：开窗口等于给出「可重发」语义。");
        Assert.Null(mapped.JobId);
        Assert.Contains("local_wait", mapped.RejectReason);
        // 显式等待分支的措辞必须可辨认（不是 `_ =>` 兜底的「仲裁面事实不可考」文案）。
        Assert.DoesNotContain("仲裁面事实不可考", mapped.RejectReason);
        Assert.Contains("等待", mapped.RejectReason);
    }

    /// <summary>
    /// **分流②：外部启动准入状态折叠**（`TaskCenterHost.AdmitExternalStartAsync` 的状态折叠）。
    /// 等待**不是** `NeedReconcile`（那会要求对账并禁止重发）、**不是** `Rejected`（那会随既有失败语义回执）、
    /// **不是** `Blocked`（那是门禁/占用阻断）；必须落到**显式等待结论**，使适配层能按「已在本地排队、
    /// 稍后重评」处置而不是按失败或错误回执。
    /// **反例**：现状 `_ =&gt; ExternalStartAdmissionStatus.NeedReconcile` 会把等待误判为「事实不可考」（红）。
    /// </summary>
    [Fact]
    public void ExternalStartMapping_WaitLocally_TakesExplicitWaitStatus_NotReconcileFallback()
    {
        var status = TaskCenterHost.MapAdmissionResultToExternalStartStatus(new AdmissionResult
        {
            Kind = WaitKind(),
            ReasonCode = "local_wait",
            Detail = "低优先级本地持久等待（未获准入前零发送）。",
        });

        Assert.NotEqual(ExternalStartAdmissionStatus.Accepted, status);
        Assert.NotEqual(ExternalStartAdmissionStatus.NeedReconcile, status);
        Assert.NotEqual(ExternalStartAdmissionStatus.ExecutionFailed, status);
        Assert.NotEqual(ExternalStartAdmissionStatus.Cancelled, status);
        Assert.Equal(WaitStatusKind(), status);
    }

    /// <summary>
    /// **适配层必须能穷尽处置等待结论**：`ExternalStartAdmissionStatus` 自身需要等待态，
    /// 且 `CommandExecutor` 的折叠不得把等待压成 `result_unknown`（那会让 UI 报「结果不可考」）。
    /// 本断言只查枚举面存在性；适配层分支的文本断言见下一夹具（需要真实 CommandExecutor 入口，
    /// 本批以 `MapAdmissionOutcome` 的显式分支断言替代，见下条）。
    /// </summary>
    [Fact]
    public void ExternalStartAdmissionStatus_HasWaitLocallyState()
    {
        Assert.Contains("WaitLocally", Enum.GetNames<ExternalStartAdmissionStatus>());
    }

    /// <summary>
    /// **分流③：边界三态映射的穷尽性**——枚举里每个值都必须被 `MapAdmissionResultToBoundary` 显式覆盖，
    /// 不得只靠 `_ =&gt;` 兜底：把兜底分支改成「抛异常」后仍不得有任何一个枚举值落入它。
    /// 该断言是对「4 处调用方必须显式处理新值」要求的**机械版本**（未来再加值会在此红）。
    /// </summary>
    [Fact]
    public void AdmissionResultKind_EveryValueIsExplicitlyHandledBySuccessorMapping()
    {
        foreach (var kind in AllKinds())
        {
            var result = new AdmissionResult { Kind = kind, ReasonCode = "rc", Detail = "d" };
            // 不得抛异常（兜底分支会被改成显式拒绝未知值；见实现批）。
            var mapped = TaskCenterHost.MapAdmissionResultToBoundary(result);
            Assert.NotNull(mapped);
        }
    }

    // ── ③ 等待值不得携带发送许可 ─────────────────────────────────────────────────

    /// <summary>
    /// **等待结果不授予发送许可**：等待分支的返回不得带 `JobId`/完整发送身份语义
    /// （本批未接线，故只断言分流产物：`JobId` 为空、`Accepted=false`）。
    /// 与 `LocalWaitEnqueueDecision.SendPermitted` 恒 false 的既有契约同向。
    /// </summary>
    [Fact]
    public void WaitLocally_NeverCarriesSendPermit()
    {
        var wait = LocalWaitQueuePolicy.DecideEnqueue(
            new RunningEncounter(RunningEncounterVerdict.WaitLocally, "更低", null));
        Assert.True(wait.Enqueued);
        Assert.False(wait.SendPermitted);

        var mapped = TaskCenterHost.MapAdmissionResultToBoundary(new AdmissionResult
        {
            Kind = WaitKind(), ReasonCode = "local_wait", Detail = "d",
        });
        Assert.False(mapped.Accepted);
        Assert.Null(mapped.JobId);
    }

    // ── ④ 既有分流分支不得因新增等待值而被误删（第二轮会诊两项重要发现的机械回归）─────────

    /// <summary>
    /// **分流①的既有分支不得被新增等待值挤掉**：`AdmissionResultKind.F11Blocked` 与
    /// `AdmissionResultKind.NeedPreemptConfirm` 在流程启动文案 switch 中**必须各有专用分支**——
    /// 首轮实现曾把二者误删而全部落入 `_ =&gt; 仲裁拒绝` 通用文案（第二轮会诊「重要」项）。
    /// **方法**：这些分支是纯文案 switch，无法从返回值区分（两个值无论走显式分支还是兜底都能编译）。
    /// 故本夹具对**源文件文本**断言：该 switch 段落内必须同时出现 `F11Blocked`、`NeedPreemptConfirm`、
    /// `WaitLocally` 三个显式分支。源文本断言是「分支被删」唯一可机械检出的事故面
    /// （不构成对运行行为的证明）。
    /// **反例（已验证判别力）**：删掉 `NeedPreemptConfirm` 专用分支后本夹具失败。
    /// </summary>
    [Fact]
    public void PanelStartCopySwitch_KeepsExistingF11AndPreemptBranches_AlongsideWaitBranch()
    {
        var section = ExtractSwitchSection(ReadHostAdmissionSource(), "仲裁未受理（");
        Assert.Contains("AdmissionResultKind.F11Blocked =>", section);
        Assert.Contains("AdmissionResultKind.NeedPreemptConfirm =>", section);
        Assert.Contains("AdmissionResultKind.WaitLocally =>", section);
        Assert.True(HasFallbackBranch(section), "流程启动文案 switch 必须保留以 `_ =>` 开头的兜底分支。");
    }

    /// <summary>
    /// **恢复准入文案 switch 的兜底不得被删除**：`Error`/`NotSelected`/`NeedPreemptConfirm`/`Cancelled`/
    /// `ExecutionFailed` 等既有可达值没有专用分支，删掉兜底会让它们抛 `SwitchExpressionException`
    /// （首轮实现的实际回归，第二轮会诊「重要」项）。
    /// **方法**：同上，纯文案 switch 只能以源文本断言——必须同时存在显式 `WaitLocally` 分支与兜底分支。
    /// 兜底判定**只看以 `_ =&gt;` 开头的代码行**，不判段内是否含该文本：段内中文注释本身含字面量，
    /// 只判文本会让「兜底被删」永不失败（首版夹具即此缺陷，已验证并修正）。
    /// **反例（已验证判别力）**：删掉兜底行后本夹具失败。
    /// </summary>
    [Fact]
    public void ResumeAdmissionCopySwitch_KeepsConservativeFallback_AlongsideWaitBranch()
    {
        var section = ExtractSwitchSection(ReadHostAdmissionSource(), "恢复被终局拒绝：");
        Assert.Contains("AdmissionResultKind.WaitLocally =>", section);
        Assert.True(HasFallbackBranch(section), "恢复准入文案 switch 的保守兜底不得被删除。");
    }

    // ── 源文本读取辅助（仅用于「分支被删」这类无法从返回值观察的回归）──────────────

    private static string ReadHostAdmissionSource()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var candidate = Path.Combine(root.FullName, "MultiplayerHoeingAssistant",
                "Services", "TaskCenter", "TaskCenterHost.Admission.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            root = root.Parent;
        }
        throw new InvalidOperationException("未能定位 TaskCenterHost.Admission.cs（源文本断言需要仓库路径）。");
    }

    /// <summary>取含 <paramref name="anchor"/> 的行起到其后第一个仅含 `});` 的行为止的段落（含两端）。</summary>
    private static string ExtractSwitchSection(string source, string anchor)
    {
        var lines = source.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains(anchor, StringComparison.Ordinal));
        if (start < 0) return "";
        var sb = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            sb.AppendLine(lines[i]);
            if (i > start && lines[i].Trim() == "});") break;
        }
        return sb.ToString();
    }

    /// <summary>是否存在一条以 `_ =&gt;` 开头的**代码行**（忽略前导空白；注释行不以 `_ =&gt;` 开头）。</summary>
    private static bool HasFallbackBranch(string section) => section
        .Split('\n')
        .Select(l => l.TrimStart())
        .Any(l => l.StartsWith("_ =>", StringComparison.Ordinal));
}
