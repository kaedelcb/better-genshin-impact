using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 20 Wave1（§24.117 合同草案 v2 C3/C4①/C4②）夹具**：
/// ①C3 身份翻译层——队列本地身份（4 段裸拼＋wait- 摘要）与准入面身份（9 元组＋cand- 候选号）
/// 不是同一空间；翻译在**登记时点**完成并落盘绑定（<c>AdmissionIdentity</c>/<c>CandidateId</c>），
/// 消费侧重入必须过 <see cref="LocalWaitIdentityTranslation.ValidateReentry"/>（IW-05 登记点死锁防护）。
/// ②C4① 登记载荷合同——D1 登记点要么登记完整载荷（引用＋准入身份）、要么拒绝登记（零发送停驻、
/// 不回落提交）；引用来源未接线 ⇒ 拒绝登记（队列零变化）。
/// ③C4②（owner 裁决 D-E3=(c)）合同前存量显式标注——缺准入身份 ⇒ <c>LegacyPreContract</c>＝永不参选；
/// 不可经 Upsert 原地补全（不可变载荷合同不变）。
/// 突变验证标注见各夹具注释（批次 20 §24.120 登记：已突变验证的关键断言逐条列名）。
/// </summary>
public sealed class LocalWaitIdentityTranslationTests : IDisposable
{
    private readonly string _dir;

    public LocalWaitIdentityTranslationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b20id-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>完整载荷项：队列本地身份自洽＋准入绑定由权威组成函数求得。</summary>
    private static LocalWaitItem FullItem(
        string runId = "run-abc123def456", string nodeId = "n1", int occurrence = 2, int loopIteration = 3,
        int attempt = 1, string reference = "wf-x/n1/2/3@ticket-7")
    {
        var stableIdentity = runId + "|" + nodeId + "|" + occurrence + "|" + loopIteration;
        var (admissionIdentity, candidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(
            TaskCenterHost.BuildSuccessorIdentityCandidate(
                "bgi:local:e1", "wf-x", runId, nodeId, occurrence, loopIteration, attempt));
        return new LocalWaitItem
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId(stableIdentity),
            StableIdentity = stableIdentity,
            CandidateId = candidateId,
            AdmissionIdentity = admissionIdentity,
            Namespace = "successor",
            WorkflowId = "wf-x",
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedIdentity = false,
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            State = LocalWaitItemState.Waiting,
            PrerequisiteReference = reference,
        };
    }

    // ---------- C3：身份空间可证有异 + 翻译层 ----------

    /// <summary>
    /// 【事实夹具（无独立突变）——突变覆盖见 M2 共用突变：把 BuildAdmissionIdentity 的组成改为
    /// 本地拼接（不委托权威）后实测变红集合＝DelegatesToSingleAuthority＋ValidateReentry_ConsistentTriple_Ok
    /// ＋ValidateReentry_AttemptDiffers（经绑定链）；本夹具保持绿属预期——它只断言 wait-/cand-
    /// 前缀与摘要不同这一事实，不约束组成规则。】
    /// 队列本地派生（wait-）与准入面候选号（cand-）是**两个身份空间**：同一出现身份在两侧的标识
    /// 字面前缀与摘要均不同——4 段裸拼不足以恢复准入候选（IW-05 死锁面的事实基础）。
    /// </summary>
    [Fact]
    public void IdentitySpaces_QueueLocalAndAdmissionFace_AreProvablyDistinct()
    {
        const string runId = "run-abc123def456";
        var fourSegment = runId + "|n1|2|3";
        var (_, candidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(
            TaskCenterHost.BuildSuccessorIdentityCandidate("bgi:local:e1", "wf-x", runId, "n1", 2, 3, 1));

        Assert.StartsWith("wait-", LocalWaitQueuePolicy.DeriveItemId(fourSegment));
        Assert.StartsWith("cand-", candidateId);
        Assert.NotEqual(LocalWaitQueuePolicy.DeriveItemId(fourSegment), candidateId);
    }

    /// <summary>
    /// 【突变验证 ✔（M2：组成偏离权威 BuildStableIdentity/DeriveCandidateId ⇒ 红）】
    /// 翻译层的登记时点组成是**权威的薄委托**：与直接调用
    /// <c>ArbitrationOrdering.BuildStableIdentity</c>/<c>DeriveCandidateId</c> 逐字符一致，
    /// 且 9 元组含全部九个字段（scope/namespace/workflowId/triggerOccurrenceId/runId/nodeId/
    /// occurrence/loopIteration/attempt 的编码值都出现；namespace/triggerOccurrenceId 取值来自
    /// 共享权威工厂）。
    /// </summary>
    [Fact]
    public void BuildAdmissionIdentity_DelegatesToSingleAuthority()
    {
        var (identity, candidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(
            TaskCenterHost.BuildSuccessorIdentityCandidate("bgi:local:e1", "wf-x", "run-1", "n1", 2, 3, 1));

        var expected = ArbitrationOrdering.BuildStableIdentity(new ArbitrationCandidate
        {
            Scope = "bgi:local:e1", Namespace = "successor", WorkflowId = "wf-x",
            TriggerOccurrenceId = "successor:run-1", RunId = "run-1", NodeId = "n1",
            Occurrence = 2, LoopIteration = 3, Attempt = 1,
        });
        Assert.Equal(expected, identity);
        Assert.Equal(ArbitrationOrdering.DeriveCandidateId(expected), candidateId);
        // 9 段＝8 个分隔符（编码转义保证字段载荷内无裸 '|'）
        Assert.Equal(8, identity.Count(c => c == '|'));
    }

    /// <summary>
    /// 【突变验证 ✔（M3：桩实现恒 Ok ⇒ 红；绿轮实现后通过）】【C4②】
    /// 合同前存量（无 <c>AdmissionIdentity</c>）⇒ 翻译失败且 <c>LegacyPreContract</c> 显式标注
    /// ＝永不参选（owner 裁决 D-E3=(c)）；即使携带前置引用（v2 时期形状）同样标注——
    /// 缺准入绑定的项**结构上**无法重入。
    /// </summary>
    [Fact]
    public void Translate_MissingAdmissionIdentity_IsLegacyPreContract_NeverSelectable()
    {
        var withoutAny = FullItem();
        withoutAny.AdmissionIdentity = null;
        withoutAny.CandidateId = "";
        var r1 = LocalWaitIdentityTranslation.Translate(withoutAny);
        Assert.False(r1.Ok);
        Assert.True(r1.LegacyPreContract, "缺准入身份必须给出 C4② 合同前存量标注");
        Assert.Contains("永不参选", r1.Reason);

        var v2Shaped = FullItem(); // 有引用、无准入身份（版本 2 时期载荷形状）
        v2Shaped.AdmissionIdentity = null;
        var r2 = LocalWaitIdentityTranslation.Translate(v2Shaped);
        Assert.False(r2.Ok);
        Assert.True(r2.LegacyPreContract);
    }

    /// <summary>
    /// 【突变验证 ✔（M4：去掉队列本地自洽校验 ⇒ 红）】
    /// 队列本地自洽：<c>ItemId</c> 与 <c>DeriveItemId(StableIdentity)</c> 不一致 ⇒ 翻译失败
    /// （与 D3 产出侧校验同口径的自洽前置）。
    /// </summary>
    [Fact]
    public void Translate_ItemIdDerivationMismatch_Fails()
    {
        var item = FullItem();
        item.ItemId = "wait-0000000000000000";
        var r = LocalWaitIdentityTranslation.Translate(item);
        Assert.False(r.Ok);
        Assert.False(r.LegacyPreContract, "这是自洽破坏，不是合同前存量");
    }

    /// <summary>
    /// 【突变验证 ✔（M5：去掉绑定校验 ⇒ 红）】
    /// 准入绑定：<c>CandidateId</c> 必须等于 <c>DeriveCandidateId(AdmissionIdentity)</c>；
    /// 缺失或被改写 ⇒ 翻译失败（不得带着坏绑定重入）。
    /// </summary>
    [Fact]
    public void Translate_CandidateIdBindingMismatch_Fails()
    {
        var missing = FullItem();
        missing.CandidateId = "";
        Assert.False(LocalWaitIdentityTranslation.Translate(missing).Ok);

        var tampered = FullItem();
        tampered.CandidateId = "cand-0000000000000000";
        Assert.False(LocalWaitIdentityTranslation.Translate(tampered).Ok);
    }

    /// <summary>
    /// 完整载荷项翻译通过：返回持久化的准入身份/候选号与 8 段出现身份前缀。
    /// </summary>
    [Fact]
    public void Translate_WellFormedItem_Ok_WithBoundIdentities()
    {
        var item = FullItem();
        var r = LocalWaitIdentityTranslation.Translate(item);
        Assert.True(r.Ok, r.Reason);
        Assert.False(r.LegacyPreContract);
        Assert.Equal(item.AdmissionIdentity, r.AdmissionIdentity);
        Assert.Equal(item.CandidateId, r.CandidateId);
        Assert.Equal(item.AdmissionIdentity![..item.AdmissionIdentity.LastIndexOf('|')], r.OccurrenceIdentity);
    }

    /// <summary>
    /// 【突变验证 ✔（M13：形状阈值 8 改为 900（恒不命中）⇒ 红，实测本夹具变红——不通过结果断言
    /// 失败；首轮用 Record.Exception 弱断言时突变下仍绿，已就地改强断言后重测）】
    /// 【Wave1 会诊 F4】非 9 元组准入身份（如 "x"）＋按其派生的候选号：①②全过后，形状校验必须返回
    /// **不通过结果而非抛异常**（Translate 自述合同「失败不是异常」）。编码转义保证字段载荷内无裸
    /// <c>'|'</c> ⇒ 合法 9 元组的裸 <c>'|'</c> 计数必为 8。
    /// </summary>
    [Fact]
    public void Translate_MalformedAdmissionIdentityShape_FailsNotThrows()
    {
        var item = FullItem();
        item.AdmissionIdentity = "x";
        item.CandidateId = ArbitrationOrdering.DeriveCandidateId("x");
        var r = LocalWaitIdentityTranslation.Translate(item);
        Assert.False(r.Ok);
        Assert.False(r.LegacyPreContract, "形状非法不是合同前存量");
    }

    /// <summary>
    /// 【突变验证 ✔（M14：把共享工厂里 successor 口径字面量改坏 ⇒ 红）】【Wave1 会诊 F5】
    /// C3 锚定夹具：等待登记的准入身份组成与 successor 提交路径的候选构造**共用同一权威工厂**
    /// （<c>TaskCenterHost.BuildSuccessorIdentityCandidate</c>）——同输入下经权威组成函数求得的
    /// 稳定身份逐字符一致；任何单侧改口径（namespace/triggerOccurrenceId/字段序）都使本夹具变红。
    /// **范围说明（如实）**：本夹具锚定的是**组成口径**；scope 取值来源（运行台账
    /// <c>AdmissionSourceScope</c> vs 租约侧登记反查）对移交来源运行两者一致
    /// （ResolveAdmissionParent 回落支返回的正是该字段，TaskCenterHost.Admission.cs 有据），
    /// 面板来源运行该字段缺省 ⇒ scope 语义归接线批合同点（C3 文档注释已登记）。
    /// </summary>
    [Fact]
    public void SuccessorCandidateComposition_AnchoredToSharedFactory()
    {
        const string runId = "run-abc123def456";
        // 左侧：独立复述 successor 口径（与生产点原内联字面量一致；写死在本夹具内）；
        // 右侧：共享权威工厂。工厂口径被单侧改坏即红（M14 实测：TriggerOccurrenceId 前缀改写 ⇒ 红）。
        // 翻译层（BuildAdmissionIdentity）与 successor 生产点都消费同一工厂 ⇒ 口径漂移在登记侧同样红
        //（经 FullItem 绑定链）。
        var statedLiteral = ArbitrationOrdering.BuildStableIdentity(new ArbitrationCandidate
        {
            Scope = "bgi:local:e1", Namespace = "successor", WorkflowId = "wf-x",
            TriggerOccurrenceId = "successor:" + runId, RunId = runId, NodeId = "n1",
            Occurrence = 2, LoopIteration = 3, Attempt = 1,
        });
        var viaFactory = ArbitrationOrdering.BuildStableIdentity(
            TaskCenterHost.BuildSuccessorIdentityCandidate("bgi:local:e1", "wf-x", runId, "n1", 2, 3, 1));
        Assert.Equal(statedLiteral, viaFactory);
        var (viaTranslation, _) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(
            TaskCenterHost.BuildSuccessorIdentityCandidate("bgi:local:e1", "wf-x", runId, "n1", 2, 3, 1));
        Assert.Equal(statedLiteral, viaTranslation);
        // [Wave1 R16 建议-2] 发送注解（PayloadFingerprint/ResourceRef/Intent）**不参与身份组成**的机械锚：
        // 同一工厂产物 ± 注解后身份逐字符相等——若权威侧未来把注解字段纳入 BuildStableIdentity，
        // 生产提交面（带注解）与等待登记面（不带）两空间立即分裂，本断言即变红。
        var annotated = TaskCenterHost.BuildSuccessorIdentityCandidate("bgi:local:e1", "wf-x", runId, "n1", 2, 3, 1);
        annotated.PayloadFingerprint = "fp-1";
        annotated.ResourceRef = "node:n1";
        annotated.Intent = "start";
        Assert.Equal(viaFactory, ArbitrationOrdering.BuildStableIdentity(annotated));
    }

    // ---------- C3：D3 产物 → SubmitAsync 重入校验 ----------

    private static ArbitrationCandidate CandidateFor(LocalWaitItem item, int attempt)
    {
        var parts = item.StableIdentity.Split('|');
        return new ArbitrationCandidate
        {
            Scope = "bgi:local:e1", Namespace = "successor", WorkflowId = "wf-x",
            TriggerOccurrenceId = "successor:" + parts[0],
            RunId = parts[0], NodeId = parts[1],
            Occurrence = int.Parse(parts[2]), LoopIteration = int.Parse(parts[3]), Attempt = attempt,
        };
    }

    /// <summary>
    /// 【突变验证 ✔（M6：把绿轮「一致 ⇒ 通过」分支改为永不命中（对 item.AdmissionIdentity 追加
    /// 后缀再比较）⇒ 红，实测仅本夹具变红）】重入三件套（重评产物键＋队列项＋准入候选）一致 ⇒ 通过。
    /// </summary>
    [Fact]
    public void ValidateReentry_ConsistentTriple_Ok()
    {
        var item = FullItem();
        var request = new LocalWaitReevaluationRequest
        {
            ItemId = item.ItemId,
            StableIdentity = item.StableIdentity,
            Trigger = LocalWaitReevaluationTriggerPoint.OccupancyEnded,
        };
        var r = LocalWaitIdentityTranslation.ValidateReentry(request.ItemId, item, CandidateFor(item, attempt: 1));
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(item.AdmissionIdentity, r.AdmissionIdentity);
    }

    /// <summary>
    /// 【突变验证 ✔（M7：去掉请求-队列项身份比对 ⇒ 红）】
    /// 重评请求的 <c>ItemId</c> 与队列项不一致 ⇒ 响亮失败（D3 产物不得指向另一等待项重入）。
    /// </summary>
    [Fact]
    public void ValidateReentry_RequestItemIdMismatch_Fails()
    {
        var item = FullItem();
        var request = new LocalWaitReevaluationRequest
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId("run-other|n1|2|3"),
            StableIdentity = item.StableIdentity,
            Trigger = LocalWaitReevaluationTriggerPoint.OccupancyEnded,
        };
        var r = LocalWaitIdentityTranslation.ValidateReentry(request.ItemId, item, CandidateFor(item, attempt: 1));
        Assert.False(r.Ok);
    }

    /// <summary>
    /// 【突变验证 ✔（M8：去掉出现身份比对 ⇒ 红）】
    /// 重入候选的出现身份（8 段前缀）与持久化准入身份不一致（不同节点）⇒ 响亮失败（死锁防护）。
    /// </summary>
    [Fact]
    public void ValidateReentry_CandidateOccurrenceMismatch_Fails()
    {
        var item = FullItem();
        var request = new LocalWaitReevaluationRequest
        {
            ItemId = item.ItemId, StableIdentity = item.StableIdentity,
            Trigger = LocalWaitReevaluationTriggerPoint.OccupancyEnded,
        };
        var wrongNode = CandidateFor(item, attempt: 1);
        wrongNode.NodeId = "n9";
        var r = LocalWaitIdentityTranslation.ValidateReentry(request.ItemId, item, wrongNode);
        Assert.False(r.Ok);
    }

    /// <summary>
    /// 【突变验证 ✔（M9：把 attempt 维度并入全匹配/或直接放行 ⇒ 红）】
    /// attempt 维度独立：出现身份前缀一致但 attempt 不同 ⇒ **不算匹配**，失败原因显式指向
    /// 「新尝试＝新身份：重驱入口必须按新 attempt 显式重组身份（BuildAdmissionIdentity），
    /// 不得复用停驻时快照」。
    /// </summary>
    [Fact]
    public void ValidateReentry_AttemptDiffers_IsExplicitRecompositionDirection_NotMatch()
    {
        var item = FullItem(attempt: 1);
        var request = new LocalWaitReevaluationRequest
        {
            ItemId = item.ItemId, StableIdentity = item.StableIdentity,
            Trigger = LocalWaitReevaluationTriggerPoint.OccupancyEnded,
        };
        var r = LocalWaitIdentityTranslation.ValidateReentry(request.ItemId, item, CandidateFor(item, attempt: 2));
        Assert.False(r.Ok);
        Assert.Contains("attempt", r.Reason);
    }

    /// <summary>
    /// 【C4② 与 C3 的组合】合同前存量项（无准入绑定）的重入请求在翻译层被显式拦下
    /// （标注永不参选），不得走到候选比对。
    /// </summary>
    [Fact]
    public void ValidateReentry_LegacyPreContractItem_IsBlockedWithAnnotation()
    {
        var item = FullItem();
        item.AdmissionIdentity = null;
        item.CandidateId = "";
        var request = new LocalWaitReevaluationRequest
        {
            ItemId = item.ItemId, StableIdentity = item.StableIdentity,
            Trigger = LocalWaitReevaluationTriggerPoint.OccupancyEnded,
        };
        var r = LocalWaitIdentityTranslation.ValidateReentry(request.ItemId, item, CandidateFor(item, attempt: 1));
        Assert.False(r.Ok);
        Assert.True(r.LegacyPreContract);
    }

    // ---------- C4①：Store 往返 + v2 读兼容 + D1 登记点 ----------

    /// <summary>准入身份随项落盘往返（写入/读取逐字符一致；批次 20 版本 3 格式）。</summary>
    [Fact]
    public void Upsert_RoundTripsAdmissionIdentity()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = FullItem();
        store.Upsert(item);
        var loaded = Assert.Single(store.Load());
        Assert.Equal(item.AdmissionIdentity, loaded.AdmissionIdentity);
        Assert.Equal(item.CandidateId, loaded.CandidateId);
        Assert.Equal(LocalWaitQueueFile.CurrentVersion, 4);
        // 【Wave1 会诊 F2】直接断言落盘文件内容：版本号＝3 且 admissionIdentity 键真实写入
        //（不只断言内存常量——Store 静默丢字段/写错版本时此处必红）。
        var raw = File.ReadAllText(Path.Combine(_dir, "wait-queue.json"));
        Assert.Contains("\"version\": 4", raw);
        Assert.Contains("\"admissionIdentity\"", raw);
    }

    /// <summary>
    /// 【突变验证 ✔（M35：队列缺失回到 NotParked ⇒ 红，实测见突变记录）】【Wave1 R15 重要-2 语义更正】
    /// 队列未注入时登记口**必须**以零发送停驻收场（Park=true＋原因指向接线缺陷）：
    /// 调用点只在接缝命中的前提下调用本方法 ⇒ 队列缺失是接线缺陷，返回 NotParked 会让调用点落进
    /// 提交路径，把「门面已给出的零发送等待结论」改写成**真实发送**（不可逆的 fail-open）。
    /// （本夹具取代原 R4-F6 的 NotParked 语义锚点——原口径在「接缝命中而队列缺失」组合下是 fail-open。）
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_WithoutQueue_RefusesToRegisterNotFallThroughToSubmit()
    {
        var runner = new WorkflowRunner(new WorkflowStore(Path.Combine(_dir, "flows-nq")),
            new RunStore(Path.Combine(_dir, "runs-nq")), new FakeBoundary(), new FakePrerequisite(),
            new FakeTerminal(), localWaitQueue: null);
        var outcome = runner.TryRegisterLocalWait(MakeRun(), MakeOccurrence(), attempt: 1);
        Assert.True(outcome.Park, "队列缺失（接线缺陷）不得回落提交路径");
        Assert.Contains("拒绝", outcome.Reason);
    }

    /// <summary>
    /// 【Wave1 R3-F6 建议采纳②】provider 返回**空白串**一侧：与 null 同路径拒绝登记
    /// （Park=true＋队列零变化）。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_WithBlankReferenceSource_RefusesToRegister()
    {
        var queueDir = Path.Combine(_dir, "q-blank");
        var runner = MakeRunner(queueDir, referenceProvider: (_, _) => "   ");
        var outcome = runner.TryRegisterLocalWait(MakeRun(), MakeOccurrence(), attempt: 1);

        Assert.True(outcome.Park);
        Assert.Contains("拒绝", outcome.Reason);
        Assert.Empty(new LocalWaitQueueStore(queueDir).Load());
    }

    /// <summary>
    /// 【突变验证 ✔（M16：同源比对改恒通过 ⇒ 红，实测见突变记录）】【Wave1 R4 重要#1】
    /// 两空间同源（防御纵深）：各自自洽但互不同源的持久化项必须被 Translate 拦下
    /// （StableIdentity 与 AdmissionIdentity 的 runId/nodeId/occurrence/loopIteration 不一致）。
    /// </summary>
    [Fact]
    public void Translate_CrossSpaceDivergence_Fails()
    {
        var item = FullItem();
        // 准入身份改指另一节点（同时重派候选号保持②③通过）——只有同源校验能拦下。
        var diverged = TaskCenterHost.BuildSuccessorIdentityCandidate(
            "bgi:local:e1", "wf-x", item.StableIdentity.Split('|')[0], "n9", 2, 3, 1);
        item.AdmissionIdentity = ArbitrationOrdering.BuildStableIdentity(diverged);
        item.CandidateId = ArbitrationOrdering.DeriveCandidateId(item.AdmissionIdentity);
        var r = LocalWaitIdentityTranslation.Translate(item);
        Assert.False(r.Ok);
        Assert.False(r.LegacyPreContract);
    }

    /// <summary>
    /// 【Wave1 R3-F6 建议采纳①】admissionIdentity 空白串在 Upsert 锁前快速失败（与读取侧同口径）；
    /// null 放行（合同前存量形状）。
    /// </summary>
    [Fact]
    public void Upsert_BlankAdmissionIdentity_RejectedEarly()
    {
        var store = new LocalWaitQueueStore(Path.Combine(_dir, "q-blank-adm"));
        var blank = FullItem();
        blank.AdmissionIdentity = "  ";
        Assert.Throws<LocalWaitQueueCorruptException>(() => store.Upsert(blank));
        Assert.Empty(store.Load());

        var nullOk = FullItem();
        nullOk.AdmissionIdentity = null;
        nullOk.CandidateId = "";
        store.Upsert(nullOk);
        Assert.Single(store.Load());
    }

    /// <summary>
    /// 【突变验证 ✔（M25：登记点 scope 采纳去掉 canonical 谓词 ⇒ 红，实测见突变记录）】【Wave1 R10 重要-1】
    /// 非规范 scope（"garbage"／"bgi:local:" 截断纪元）⇒ 拒绝登记：采纳判据与提交面
    /// (ResolveAdmissionParent→IsCanonicalAdmissionScope) **同源**，不得让登记产物落在提交面
    /// 永不产生的身份空间（「坏 scope」形态的结构性永不可重入）。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_NonCanonicalScope_RefusesToRegister()
    {
        var cases = new[] { "garbage", "bgi:local:", "bgi:local:   ", "other:local:e1" };
        for (var i = 0; i < cases.Length; i++)
        {
            var queueDir = Path.Combine(_dir, "q-badscope" + i);
            var runner = MakeRunner(queueDir, referenceProvider: (_, _) => "wf-x/n1/2/3@ticket-7");
            var outcome = runner.TryRegisterLocalWait(MakeRun(scope: cases[i]), MakeOccurrence(), attempt: 1);
            Assert.True(outcome.Park, $"scope='{cases[i]}' 应停驻");
            Assert.Contains("拒绝", outcome.Reason);
            Assert.Empty(new LocalWaitQueueStore(queueDir).Load());
        }
    }

    /// <summary>
    /// 【突变验证 ✔（M24：Scope 来源注入被忽略 ⇒ 红，实测见突变记录）】【Wave1 R9-F1】
    /// 面板来源运行（台账字段缺省）经 Scope 来源注入取得权威 scope ⇒ **成功登记**且身份 scope 段
    /// ＝注入值（与提交面租约反查同一空间）——R9-F1 更正「字段缺省＝无权威 scope」错误等式后，
    /// 面板来源运行的自洽登记路径。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_PanelSource_WithScopeProvider_Registers()
    {
        var queueDir = Path.Combine(_dir, "q-scopeprovider");
        var runner = MakeRunner(queueDir, referenceProvider: (_, _) => "wf-x/n1/2/3@ticket-7",
            scopeProvider: _ => "bgi:local:e2");
        var outcome = runner.TryRegisterLocalWait(MakeRun(scope: null), MakeOccurrence(), attempt: 1);

        Assert.True(outcome.Park);
        Assert.Contains("已登记", outcome.Reason);
        var loaded = Assert.Single(new LocalWaitQueueStore(queueDir).Load());
        var parts = loaded.AdmissionIdentity!.Split('|');
        Assert.Equal("bgi:local:e2",
            ArbitrationOrdering.ArbitrationIdentityEncoding.DecodeString(parts[0]));
        Assert.True(LocalWaitIdentityTranslation.Translate(loaded).Ok);
    }

    /// <summary>
    /// 【突变验证 ✔（M23：规范形判定撤回 uint.TryParse ⇒ 红，实测见突变记录）】【Wave1 R8-F1】
    /// 符号/空白变体穿透：occurrence 段 "+0000002"（8 字符，uint.TryParse 默认样式容许前导符号）
    /// ＋候选号重派 ⇒ 必须被 ②' 拦下（Ok=false）——「Ok ⇒ 规范形」承诺在符号/空白输入下仍成立。
    /// </summary>
    [Fact]
    public void Translate_SignedOrWhitespaceIntegerSegments_Rejected()
    {
        foreach (var bad in new[] { "+0000002", " 0000002", "-0000002", "0000002 " })
        {
            var item = FullItem();
            var parts = item.AdmissionIdentity!.Split('|');
            parts[6] = bad;
            item.AdmissionIdentity = string.Join("|", parts);
            item.CandidateId = ArbitrationOrdering.DeriveCandidateId(item.AdmissionIdentity);
            var r = LocalWaitIdentityTranslation.Translate(item);
            Assert.False(r.Ok, $"整数段 '{bad}' 应被规范形校验拦下");
            Assert.False(r.LegacyPreContract);
        }
    }

    /// <summary>
    /// 【突变验证 ✔（M22：同源比对移除 workflowId 段 ⇒ 红，实测见突变记录）】【Wave1 R7 重要-3】
    /// 准入身份 workflowId 段（第 3 段）漂移＋候选号重派 ⇒ 同源校验必须拦下
    /// （不同 workflow 的同名节点是不同出现；下游 fail-closed 只是检测点延后，不是不防）。
    /// </summary>
    [Fact]
    public void Translate_WorkflowIdDivergence_Fails()
    {
        var item = FullItem();
        var parts = item.AdmissionIdentity!.Split('|');
        parts[2] = ArbitrationOrdering.ArbitrationIdentityEncoding.EncodeString("wf-y");
        item.AdmissionIdentity = string.Join("|", parts);
        item.CandidateId = ArbitrationOrdering.DeriveCandidateId(item.AdmissionIdentity);
        var r = LocalWaitIdentityTranslation.Translate(item);
        Assert.False(r.Ok);
        Assert.False(r.LegacyPreContract);
    }

    /// <summary>
    /// 【突变验证 ✔（M21：移除缺 scope 拒绝分支 ⇒ 红，实测见突变记录）】【Wave1 R7 重要-2】
    /// 面板来源运行（AdmissionSourceScope 缺省）⇒ 登记点**拒绝登记**：空段 scope 身份与 successor
    /// 提交面（权威反查 scope 构造）不同身份空间 ⇒ 结构性永不可重入，登记即预置死锁（C4① 禁止形态）。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_WithoutScope_RefusesToRegister()
    {
        var queueDir = Path.Combine(_dir, "q-noscope");
        var runner = MakeRunner(queueDir, referenceProvider: (_, _) => "wf-x/n1/2/3@ticket-7");
        var outcome = runner.TryRegisterLocalWait(MakeRun(scope: null), MakeOccurrence(), attempt: 1);
        Assert.True(outcome.Park);
        Assert.Contains("拒绝", outcome.Reason);
        Assert.Empty(new LocalWaitQueueStore(queueDir).Load());
    }

    /// <summary>
    /// 【突变验证 ✔（M20：移除锚失效停驻救援分支 ⇒ 红，实测见突变记录）】【Wave1 R7 重要-1】
    /// 混合情形：最后完成节点 n0 在新修订中被删除、停驻标记 n1 仍在 ⇒ **不得**按链尾放行
    /// （null ⇒ TailReached ⇒ 流程假成功、未发送节点被静默吞）；应按停驻出现 n1 继续
    /// （恢复后在该节点重走完整准入）。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_CompletedAnchorRemoved_ParkedNodeContinuesNotTail()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor4"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n1", "n2")); // n0 已从修订消失
        Assert.NotNull(relocated);
        Assert.Equal("n1", relocated!.NodeId);
    }

    /// <summary>
    /// 【Wave1 R6-F4 加严断言】非规范形（整数段非 D8 定宽，如 occurrence 段 "2" 而非 "00000002"）
    /// ＋候选号重派 ⇒ ②' 拦下（Ok=false）；「Ok ⇒ 规范形 ⇒ 可重入对齐」梯度闭合。
    /// </summary>
    [Fact]
    public void Translate_NonCanonicalIntegerSegments_Rejected()
    {
        var item = FullItem();
        var parts = item.AdmissionIdentity!.Split('|');
        parts[6] = "2"; // 非定宽
        item.AdmissionIdentity = string.Join("|", parts);
        item.CandidateId = ArbitrationOrdering.DeriveCandidateId(item.AdmissionIdentity);
        var r = LocalWaitIdentityTranslation.Translate(item);
        Assert.False(r.Ok);
        Assert.False(r.LegacyPreContract);
    }

    /// <summary>
    /// 【突变验证 ✔（M19：移除 ValidateReentry 的越界折叠 ⇒ 红，实测见突变记录）】【Wave1 R6-F2】
    /// 防护闸对不可信候选的异常合同：候选整数段越界（EncodeInt 必抛）⇒ 结构化不通过，
    /// 异常不穿透。
    /// </summary>
    [Fact]
    public void ValidateReentry_OutOfRangeCandidate_FailsNotThrows()
    {
        var item = FullItem();
        var request = new LocalWaitReevaluationRequest
        {
            ItemId = item.ItemId, StableIdentity = item.StableIdentity,
            Trigger = LocalWaitReevaluationTriggerPoint.OccupancyEnded,
        };
        var wild = CandidateFor(item, attempt: 1);
        wild.Occurrence = 100_000_000; // EncodeInt 必抛的越界值
        var ex = Record.Exception(() => LocalWaitIdentityTranslation.ValidateReentry(request.ItemId, item, wild));
        Assert.Null(ex);
        var r = LocalWaitIdentityTranslation.ValidateReentry(request.ItemId, item, wild);
        Assert.False(r.Ok);
    }

    /// <summary>
    /// 【突变验证 ✔（M18：权威 DecodeString 对非法转义改抛异常 ⇒ 红，实测见突变记录）】【Wave1 R5 重要-1】
    /// Translate 合同「失败不是异常」在**非法转义段**输入下的钉死：第 4 段含非法转义（"run~2x"，
    /// 孤立 '~'）＋候选号重派（穿过①②②'③）⇒ 必须返回不通过结果而非抛异常。
    /// 权威侧行为（ArbitrationOrdering.DecodeString 扫描语义：孤立 '~' 贡献空串跳过，**不抛**）
    /// 经本夹具钉死；解码产物与 StableIdentity 不同 ⇒ 落同源校验不通过，结构化失败。
    /// </summary>
    [Fact]
    public void Translate_MalformedEscapeAdmissionIdentity_FailsNotThrows()
    {
        var item = FullItem();
        var parts = item.AdmissionIdentity!.Split('|');
        parts[4] = "run~2x"; // 非法转义段（孤立 '~'）
        item.AdmissionIdentity = string.Join("|", parts);
        item.CandidateId = ArbitrationOrdering.DeriveCandidateId(item.AdmissionIdentity);
        var ex = Record.Exception(() => LocalWaitIdentityTranslation.Translate(item));
        Assert.Null(ex);
        var r = LocalWaitIdentityTranslation.Translate(item);
        Assert.False(r.Ok);
        Assert.False(r.LegacyPreContract);
    }

    // ---------- Wave1 R5 重要-2：RecomputeSuccessor 锚不得被停驻标记污染 ----------

    private static WorkflowPlan TwoNodePlan(params string[] nodeIds)
        => new(new WorkflowDocument
        {
            Name = "锚污染夹具",
            Nodes = nodeIds.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.oneDragonConfig",
                Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
            }).ToList(),
        });

    /// <summary>
    /// 【突变验证 ✔（M27：停驻路径改为在 RecordIntent 之后再登记 ⇒ 红，实测见突变记录）】
    /// 【Wave1 R11 F-A 闭合方式 (a)】**拒绝登记停驻的运行侧归宿有机械证据**：拒绝分支在
    /// RecordIntent **之前** ⇒ 该运行无在飞提交（CurrentSubmission 为 null）、无收尾意图 ⇒
    /// 重启恢复扫描（RunStore.RecoverOnStart）把它收敛为 <c>Interrupted</c>（**可显式恢复**），
    /// 不是「无重驱句柄的永久 Running」。
    /// 本夹具钉死该前提：拒绝登记路径上 CurrentSubmission 必须仍为 null（在飞提交是收敛为
    /// Unknown 的判据；一旦登记被挪到 RecordIntent 之后，本夹具即变红）。
    /// 局限（如实）：本夹具钉的是「拒绝路径不产生在飞提交」这一前提，不覆盖 RunStore 收敛实现
    ///（收敛本身属既有已提交合同，非本批改动面）。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_RefusalPath_LeavesNoInFlightSubmission()
    {
        var queueDir = Path.Combine(_dir, "q-refusal-inflight");
        var runner = MakeRunner(queueDir, referenceProvider: null);
        var run = MakeRun();
        Assert.Null(run.CurrentSubmission); // 未提交意图
        var outcome = runner.TryRegisterLocalWait(run, MakeOccurrence(), attempt: 1);
        Assert.True(outcome.Park);
        Assert.Null(run.CurrentSubmission); // 拒绝路径不产生在飞提交 ⇒ 重启收敛为 Interrupted（可恢复）
    }

    /// <summary>
    /// 【突变验证 ✔（M32：停驻救援探针锁死第 0 轮 ⇒ 红，实测见突变记录）】【Wave1 R14 F1】
    /// **第 k>0 轮**（结构性循环流程）停驻时的前插保全：探针必须与停驻**同轮次**起步。
    /// 旧实现从 FirstOccurrence()（第 0 轮）起步并守卫 LoopIteration 相等 ⇒ k>0 时循环体一次不执行
    /// ⇒ 停驻点前插的未执行节点被静默跳过。本夹具用 loopIteration=1 的停驻 + 前插节点钉死。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_ParkedInLaterLoop_PrecededByUnexecutedNode_ReturnsThatNode()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor9"), referenceProvider: null);
        var run = MakeRun();
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = "n0", SequenceIndex = 0, Occurrence = 0, LoopIteration = 1, Result = "succeeded", Reason = "夹具",
        });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = "n1", SequenceIndex = 1, Occurrence = 0, LoopIteration = 1,
            Result = WorkflowRunner.LocalWaitResultWord, Reason = "停驻（第 2 轮）",
        });
        // n0 在新修订中消失（不可定位）⇒ 进停驻救援；新链在 n1 前插 nPre。
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("nPre", "n1"));
        Assert.NotNull(relocated);
        Assert.Equal("nPre", relocated!.NodeId);
    }

    /// <summary>
    /// 【突变验证 ✔（M38：下界约束移除 ⇒ 红，实测见突变记录）】【Wave1 R18 必改-1 非链尾交错】
    /// 锚可定位（candidate=Next(锚) 非 null）时，救援探针**不得**返回早于 candidate 的未执行出现：
    /// 否则救援返回的早节点完成后，驱动循环线性推进会穿越中途已完成出现并**重复提交**（不变量①）。
    /// 场景：n0/n1 完成、n2 停驻；新链 [nPre, n0, n1, n2] ⇒ 锚 n1 定位 ⇒ candidate=n2；
    /// 探针若返回 nPre 即违规（nPre 之后隔着已完成的 n0/n1）⇒ 应返回 n2。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_AnchorLocatable_RescueMustNotReturnEarlierThanCandidate()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor13"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("nPre", "n0", "n1", "n2"));
        Assert.NotNull(relocated);
        Assert.Equal("n2", relocated!.NodeId);
    }

    /// <summary>
    /// 【BO-6 完成过滤推进】锚位于链尾时既有 tail 下界返回 n2；真实 Runner 随后重驱 n2，
    /// 并由恢复推进层过滤已完成 n0/n1。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_AnchorAtTail_ParkedFollowedByCompleted_ReturnsParkedMarker()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor14"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("nPre", "n2", "n0", "n1"));
        Assert.NotNull(relocated);
        Assert.Equal("n2", relocated!.NodeId); // tail 下界保护有效停驻义务；前插由候选路径另行承载
    }

    /// <summary>
    /// 【突变验证 ✔（M36：过期停驻标记过滤移除 ⇒ 红，实测见突变记录）】【Wave1 R16 必改-1】
    /// **过期停驻标记**：同一出现先停驻、恢复后完成（NodeOutcomes 追加式，旧 waitLocally 条目留存）
    /// ⇒ 该标记不得再当恢复点（否则恢复点被拖回已完成出现并重复提交，违反不变量①）。
    /// 夹具：n1 既有 waitLocally 又有 succeeded ⇒ 修订重载应返回 Next(n1)=n2，而不是 n1。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_StaleParkingMarkerOnCompletedOccurrence_NotReturnedAsResumePoint()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor12"), referenceProvider: null);
        var run = MakeRun();
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = "n1", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0,
            Result = WorkflowRunner.LocalWaitResultWord, Reason = "停驻",
        });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = "n1", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0,
            Result = "succeeded", Reason = "恢复后完成（旧停驻标记为过期）",
        });
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n1", "n2"));
        Assert.NotNull(relocated);
        Assert.Equal("n2", relocated!.NodeId);
    }

    /// <summary>
    /// 【BO-6 完成过滤推进】停驻点 n2 重排到已完成锚之前，恢复从 n2 开始，
    /// 然后跳过已完成 n0/n1 并继续执行 n3。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_AnchorLocatableParkedBeforeItAndWorkAfter_ReturnsParkedMarker()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor11"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n2", "n0", "n1", "n3"));
        Assert.NotNull(relocated);
        Assert.Equal("n2", relocated!.NodeId);
    }

    /// <summary>
    /// 【BO-6 完成过滤推进】多停驻标记＋过期＋前插＋锚链尾的组合场景：
    /// P1（pa）有效、P2（pb）已完成而过期；返回 pa，由真实 Runner 重驱 pa 并过滤 pb。
    /// **tailBound 过期过滤（R19 重要-2 修复）如实登记**：已实现为与 ParkedRescue 主循环对称的
    /// 结构性防御（下界选取共用 HasCompletedOutcome 过滤）；其**独立**可观察行为需要「loop 计划＋
    /// 有效停驻在锚后安全路径＋更晚过期标记」的组合仍由既有锚下界过滤保障。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_MultiParkingWithStale_ReturnsEarliestActiveMarker()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor15"), referenceProvider: null);
        var run = MakeRun();
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "pa", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord, Reason = "P1 停驻" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "pb", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord, Reason = "P2 停驻" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "pb", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded", Reason = "P2 随后完成＝过期" });
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("pre", "pa", "pb"));
        Assert.NotNull(relocated);
        Assert.Equal("pa", relocated!.NodeId);
    }

    /// <summary>
    /// 【突变验证 ✔（M43：下界字典序退回双独立条件 ⇒ 红）＋【R24 重要-2 裁决改写】】【Wave1 R21 必改-F1】
    /// 跨轮次下界：循环计划 [A,B,C]，A(loop0) 完成、C(loop1) 停驻 ⇒ 锚 A、candidate=B(loop0)、
    /// 探针 A(loop1)。**R24 全序取早者裁决后：B(loop0)（candidate，未完成）全序早于 A(loop1)（rescue）
    /// ⇒ 返回 B(loop0)**，线性推进 B→C→A(loop1) 自然重驱停驻点——R21 残余「rescue 覆盖更早 candidate」
    /// （BO-7 ①子项）就此闭合，两个义务都不丢。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_CrossLoopLowerBound_UsesPlanOrderNotSeparateConditions()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor17"), referenceProvider: null);
        var run = MakeRun();
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded", Reason = "夹具" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "C", SequenceIndex = 2, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord, Reason = "停驻（第 2 轮）" });
        var plan = new WorkflowPlan(new WorkflowDocument
        {
            Name = "跨轮次下界夹具",
            Nodes = new[] { "A", "B", "C" }.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.oneDragonConfig",
                Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
            }).ToList(),
            Loop = new WorkflowLoop(),
        });
        var relocated = runner.RecomputeSuccessor(run, plan);
        Assert.NotNull(relocated);
        // 全序取早者：candidate=B(loop0) 早于 rescue=A(loop1) ⇒ 先执行 B，线性推进自然重驱停驻点。
        Assert.Equal(("B", 0), (relocated!.NodeId, relocated.LoopIteration));
    }

    /// <summary>
    /// 【突变验证 ✔（M42：规范编码回环校验移除 ⇒ 红，实测见突变记录）】【Wave1 R21 必改-F3】
    /// 两类「解码等价但非权威编码」的对抗输入必须被拦下：
    /// ①scope 段为空串（权威 EncodeString("")="~"，空串段不是权威产物）；
    /// ②runId 段含孤立 '~'（"r~un-…" 解码丢弃 '~' 后与原值等价，但字符串本身非规范形）。
    /// </summary>
    [Fact]
    public void Translate_NonCanonicalStringSegments_Rejected()
    {
        // ①空 scope 段（9 段齐全但第 0 段为空串）
        var item1 = FullItem();
        var parts1 = item1.AdmissionIdentity!.Split('|');
        parts1[0] = "";
        item1.AdmissionIdentity = string.Join("|", parts1);
        item1.CandidateId = ArbitrationOrdering.DeriveCandidateId(item1.AdmissionIdentity);
        var r1 = LocalWaitIdentityTranslation.Translate(item1);
        Assert.False(r1.Ok, "空 scope 段（应为 '~' 占位）必须被回环校验拦下");
        Assert.False(r1.LegacyPreContract);

        // ②runId 段孤立 '~'（解码等价但非规范形）
        var item2 = FullItem();
        var parts2 = item2.AdmissionIdentity.Split('|');
        parts2[4] = "r~un-abc123def456";
        item2.AdmissionIdentity = string.Join("|", parts2);
        item2.CandidateId = ArbitrationOrdering.DeriveCandidateId(item2.AdmissionIdentity);
        var r2 = LocalWaitIdentityTranslation.Translate(item2);
        Assert.False(r2.Ok, "孤立 '~' 段（解码等价但非规范形）必须被回环校验拦下");
        Assert.False(r2.LegacyPreContract);
    }

    /// <summary>
    /// 【突变验证 ✔（M44：scope 解析次序改回 provider 优先 ⇒ 红，实测见突变记录）】【Wave1 R23 重要-1】
    /// 双源分歧消除：移交来源运行（台账字段 bgi:local:e1 规范非空）⇒ **恒取台账字段**，provider 即使
    /// 返回另一纪元（bgi:local:e2）也不得覆盖——否则登记身份与提交面（恒取台账字段）逐字符不等 ⇒
    /// 等待项结构性永不可参选。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_RegistrationScopeFieldWinsOverProvider()
    {
        var queueDir = Path.Combine(_dir, "q-scopeorder");
        var runner = MakeRunner(queueDir, referenceProvider: (_, _) => "wf-x/n1/2/3@ticket-7",
            scopeProvider: _ => "bgi:local:e2");
        var outcome = runner.TryRegisterLocalWait(MakeRun(scope: "bgi:local:e1"), MakeOccurrence(), attempt: 1);
        Assert.True(outcome.Park);
        Assert.Contains("已登记", outcome.Reason);
        var loaded = Assert.Single(new LocalWaitQueueStore(queueDir).Load());
        var parts = loaded.AdmissionIdentity!.Split('|');
        Assert.Equal("bgi:local:e1",
            ArbitrationOrdering.ArbitrationIdentityEncoding.DecodeString(parts[0]));
    }

    /// <summary>
    /// 【Wave1 R23 重要-2 形态钉死】跨代际高轮次停驻标记：旧计划含 Loop 跑到第 2 轮停驻、
    /// 修订**移除循环** ⇒ 锚（loop0 完成项）在无循环新计划链尾 ⇒ candidate=null ⇒ tailBound
    /// 分支取高轮次停驻点为下界 ⇒ ParkedRescue 返回该停驻点（重驱义务承载）。注释全称否定
    /// 「不可达」已按 R23 更正（该分支可达且必要）。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_CrossGenerationalHighLoopParking_CarriedByTailBoundBranch()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor18"), referenceProvider: null);
        var run = MakeRun();
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "B", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded", Reason = "夹具" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord, Reason = "旧计划第 2 轮停驻" });
        // 修订移除循环 ⇒ 新计划 [A, B] 无 Loop；锚 B@loop0 在链尾 ⇒ candidate=null。
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("A", "B"));
        Assert.NotNull(relocated);
        Assert.Equal(("A", 1), (relocated!.NodeId, relocated.LoopIteration));
    }

    /// <summary>
    /// 【突变验证 ✔（M46：rescue 与 candidate 恢复无条件覆盖 ⇒ 红，实测见突变记录）】【Wave1 R24 重要-2】
    /// 旧计划 [A,B] 含 Loop：A@0、B@0 完成，A@1 停驻；修订在链尾插 C ⇒ [A,B,C] 保留 Loop ⇒
    /// 锚 B@0 ⇒ candidate=C@0（新插未执行）；停驻 A@1 在锚后安全路径 ⇒ rescue=A@1。
    /// 全序 C@0 < A@1 ⇒ **必须返回 C@0**（先执行新插节点，线性推进自然到达停驻点 A@1）；
    /// 旧代码 rescue 覆盖 ⇒ C@0 永不进 NodeOutcomes ⇒ 假成功丢步。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_RescueAndCandidateByPlanOrder_EarlierWins()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor19"), referenceProvider: null);
        var run = MakeRun();
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded", Reason = "夹具" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "B", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded", Reason = "夹具" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord, Reason = "停驻（第 2 轮）" });
        var plan = new WorkflowPlan(new WorkflowDocument
        {
            Name = "全序取早夹具",
            Nodes = new[] { "A", "B", "C" }.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.oneDragonConfig",
                Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
            }).ToList(),
            Loop = new WorkflowLoop(),
        });
        var relocated = runner.RecomputeSuccessor(run, plan);
        Assert.NotNull(relocated);
        Assert.Equal(("C", 0), (relocated!.NodeId, relocated.LoopIteration));
    }

    /// <summary>
    /// 【突变验证 ✔（M47：安全性检查恢复 break 首中即返回 ⇒ 红，实测见突变记录）】【Wave1 R25 重要-1】
    /// 多有效停驻：P1（同身份加回锚前）＋P2（锚后）——最后一条 P2 在安全路径上不得短路跳过 P1；
    /// 最早有效停驻 P1 必须先作为救援点返回，随后 Runner 过滤已完成锚。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor20"), referenceProvider: null);
        var run = MakeRun();
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "P1", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord, Reason = "P1 停驻（后被删又加回锚前）" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "B", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded", Reason = "锚" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "P2", SequenceIndex = 2, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord, Reason = "P2 停驻（锚后）" });
        var relocated = runner.RecomputeSuccessor(run, new WorkflowPlan(new WorkflowDocument
        {
            Name = "三节点",
            Nodes = new[] { "P1", "B", "P2" }.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.oneDragonConfig",
                Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
            }).ToList(),
        }));
        // P1 被重排到已完成锚 B 之前 ⇒ P1 义务优先，不得因 P2 安全而静默吞掉它。
        Assert.NotNull(relocated);
        Assert.Equal("P1", relocated!.NodeId);
    }

    /// <summary>
    /// 【Wave1 R27 重要-1 行为护栏】非拉丁数字文化（fa-IR）下登记 → Translate 全通过：
    /// 写侧整数段显式 InvariantCulture 后，写侧/校验侧口径在**任何**文化下一致。
    /// **判别力如实登记（R5/R3）**：当前运行时（.NET 8 ICU）fa-IR 的数字位符号为 Latin ⇒
    /// 隐式 ToString 在此运行时也不产本土数字 ⇒ 本夹具对「写侧去掉显式 Invariant」的突变**不红**
    /// （判别力依赖运行时文化数据，无法本地突变验证）——夹具价值为**行为回归护栏**（钉死
    /// 「非拉丁文化下登记→翻译全通过」合同），修复本身是确定性消除口径不对称。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_NonLatinDigitCulture_TranslateStillPasses()
    {
        var queueDir = Path.Combine(_dir, "q-fa-ir");
        var runner = MakeRunner(queueDir, referenceProvider: (_, _) => "wf-x/n1/2/3@ticket-7");
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
            var outcome = runner.TryRegisterLocalWait(MakeRun(), MakeOccurrence(), attempt: 1);
            Assert.True(outcome.Park);
            var loaded = Assert.Single(new LocalWaitQueueStore(queueDir).Load());
            Assert.True(LocalWaitIdentityTranslation.Translate(loaded).Ok,
                "非拉丁数字文化下登记的项必须仍通过同源校验（写侧/校验侧同为 InvariantCulture）");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prev;
        }
    }

    /// <summary>
    /// 【R29 重要 行为钉死夹具（BO-8）】继承缺陷形态：[n3,n2,Y] 执行 n3✓n2✓ 后修订为 [n2,X,n3,Y] ⇒
    /// RecomputeSuccessor 返回 X（锚后首个未完成）——**返回点之后的推进段撞已完成 n3** 的缺口
    /// 归台账 BO-8（Wave3 C11：驱动推进层完成过滤）。本夹具钉死重算层当前行为，防误判回归。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_ReturnsFirstIncompleteAfterAnchor_Bo8BehaviorPin()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor21"), referenceProvider: null);
        var run = RunWithOutcomes(("n3", "succeeded"), ("n2", "succeeded"));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n2", "X", "n3", "Y").Document.Nodes.Count == 4
            ? new WorkflowPlan(new WorkflowDocument
            {
                Name = "BO-8 钉死",
                Nodes = new[] { "n2", "X", "n3", "Y" }.Select(id => new WorkflowNode
                {
                    NodeId = id, Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
                }).ToList(),
            })
            : null);
        Assert.NotNull(relocated);
        Assert.Equal("X", relocated!.NodeId);
    }

    /// <summary>
    /// 【BO-6 完成过滤推进】修订 [Y, n2, X]（X=n2 之后同轮次已完成）：锚 Y 可定位、n2 为有效停驻；
    /// 恢复从 n2 开始，真实 Runner 重驱后过滤已完成 X，不得把停驻义务改写成普通链尾。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor16"), referenceProvider: null);
        var run = RunWithOutcomes(("X", "succeeded"), ("Y", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("Y", "n2", "X"));
        Assert.NotNull(relocated);
        Assert.Equal("n2", relocated!.NodeId);
    }

    /// <summary>
    /// 【BO-6 完成过滤推进】停驻点重排到已完成锚之前时返回停驻点；真实 Runner 重驱它，
    /// 并在推进中跳过已完成节点，不允许以链尾成功吞掉停驻义务。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_ParkedReorderedBeforeCompletedAnchor_ReturnsParkedMarker()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor10"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
        // 新链把停驻点 n2 重排到锚 n1 之前；救援优先返回 n2，推进过滤由真实 Runner 覆盖。
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n2", "n0", "n1"));
        Assert.NotNull(relocated);
        Assert.Equal("n2", relocated!.NodeId);
    }

    /// <summary>
    /// 【突变验证 ✔（M31：停驻点前插节点保全移除 ⇒ 红，实测见突变记录）】【Wave1 R12 建议-1】
    /// 全部完成锚被删＋停驻点**之前**插入了从未执行的新节点 ⇒ 不得直接返回停驻点而静默跳过它。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_ParkedPrecededByUnexecutedNode_ReturnsThatNode()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor8"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("nPre", "n1"));
        Assert.NotNull(relocated);
        Assert.Equal("nPre", relocated!.NodeId);
    }

    /// <summary>
    /// 【突变验证 ✔（M30：完成性过滤移除 ⇒ 红，实测见突变记录）】【Wave1 R12 重要-1】
    /// 回溯命中后必须做**完成性过滤**：旧链 n0,n1,n2 全完成；新链删除 n2 并重排为 n1,n0,n3 ⇒
    /// 回溯锚 n1 可定位 ⇒ Next(n1)=n0 **已完成** ⇒ 必须跳至 n3（首个未完成出现），
    /// **不得**返回 n0（重复执行已完成节点＝外部副作用重复发生，不可撤销）。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_BacktrackNextAlreadyCompleted_SkipsToFirstIncomplete()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor7"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", "succeeded"));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n1", "n0", "n3"));
        Assert.NotNull(relocated);
        Assert.Equal("n3", relocated!.NodeId);
    }

    /// <summary>
    /// 【突变验证 ✔（M29：写侧归一移除 ⇒ 红，实测见突变记录）】【Wave1 R11 F-D】
    /// 非空注解字段（CandidateId）null→"" 写侧归一：内存对象持 null ⇒ 落盘读回 "" ⇒ 再次登记
    /// 同一内存对象必须仍判「同载荷」（幂等合同），不得响亮冲突。
    /// </summary>
    [Fact]
    public void Upsert_NullAnnotationsNormalized_IdempotentReregistration()
    {
        var store = new LocalWaitQueueStore(Path.Combine(_dir, "q-nullann"));
        var item = FullItem();
#pragma warning disable CS8625 // 故意赋 null：钉死写侧归一合同
        item.CandidateId = null!;
        item.Namespace = null!;
        item.WorkflowId = null!;
#pragma warning restore CS8625
        store.Upsert(item);
        store.Upsert(item); // 同身份同载荷 ⇒ 幂等复用（不得因 null/"" 不对称而冲突）
        var loaded = Assert.Single(store.Load());
        Assert.Equal(string.Empty, loaded.CandidateId);
        Assert.Equal(string.Empty, loaded.Namespace);
        Assert.Equal(string.Empty, loaded.WorkflowId);
    }

    /// <summary>
    /// 【突变验证 ✔（M28：回溯改为只取最后一条完成锚 ⇒ 红，实测见突变记录）】【Wave1 R11 F-C】
    /// 锚回溯：旧链 n0 succeeded、n1 succeeded、n2 停驻；新修订删 n1 并在 n0 与 n2 之间插入 nX
    /// （新链 n0,nX,n2）⇒ 应回溯到可定位的 n0 ⇒ Next(n0)=nX（既不丢停驻点也不丢新插节点）；
    /// **不得**因最后完成锚 n1 失效就直接返回停驻点 n2（那会静默跳过新插节点 nX）。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_LastAnchorRemoved_BacktracksToEarlierLocatableAnchor()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor6"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"),
            ("n2", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n0", "nX", "n2"));
        Assert.NotNull(relocated);
        Assert.Equal("nX", relocated!.NodeId);
    }

    /// <summary>
    /// 【Wave1 R10 建议-2 钉死落空支】完成锚被删＋停驻节点也已被删 ⇒ 按链尾（null/TailReached，
    /// 注释明示取舍：队列侧等待项重评/清理归队列层）。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_AnchorAndParkedBothRemoved_TailReached()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor5"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n2", "n3"));
        Assert.Null(relocated);
    }

    private static WorkflowRunRecord RunWithOutcomes(params (string NodeId, string Result)[] outcomes)
    {
        var run = MakeRun();
        foreach (var (nodeId, result) in outcomes)
        {
            run.NodeOutcomes.Add(new WorkflowNodeOutcome
            {
                NodeId = nodeId, SequenceIndex = 0, Occurrence = 0, LoopIteration = 0,
                Result = result, Reason = "夹具",
            });
        }
        return run;
    }

    /// <summary>
    /// 【突变验证 ✔（M17：RecomputeSuccessor 锚过滤移除 ⇒ 红，实测见突变记录）】【Wave1 R5 重要-2】
    /// 停驻标记（waitLocally）**不是完成**：仅停驻过 n1（未发送、未完成）⇒「最后完成身份」锚为空
    /// ⇒ 取链首 n1（恢复后重走 n1），**不得**取 Next(n1)=n2（静默跳过零发送节点），
    /// 也**不得**返回 null/TailReached（按链尾放行 ⇒ 流程假成功）。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_ParkingMarkerIsNotCompletion_AnchorFallsToChainHead()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor"), referenceProvider: null);
        var run = RunWithOutcomes(("n1", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n1", "n2"));
        Assert.NotNull(relocated);
        Assert.Equal("n1", relocated!.NodeId);
    }

    /// <summary>
    /// 【Wave1 R5 重要-2 组合面】完成词锚不被后续停驻标记顶掉：n0 完成、n1 停驻 ⇒ 锚=n0 ⇒ Next=n1
    /// （恢复后重走 n1，不跳到 n2）。另：停驻节点从新修订消失 ⇒ 锚（n0）仍定位成功 ⇒ Next=n1？——
    /// 新修订无 n1 时 Next(n0) 指向修订中的后继，锚本身有效即按定义走。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_CompletionAnchorSurvivesLaterParkingMarker()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor2"), referenceProvider: null);
        var run = RunWithOutcomes(("n0", "succeeded"), ("n1", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n0", "n1", "n2"));
        Assert.NotNull(relocated);
        Assert.Equal("n1", relocated!.NodeId);
    }

    /// <summary>
    /// 【突变验证 ✔（M17 共用）】【Wave1 R5 重要-2 死锁面变体】仅停驻过 n1、且 n1 从新修订消失：
    /// 锚为空 ⇒ 取链首（修订链首 n2'），**不得**返回 null（null ⇒ TailReached ⇒ 流程聚合假成功，
    /// 未发送节点被静默吞）。夹具构造与 R5 会诊逐步走查的反例同型。
    /// </summary>
    [Fact]
    public void RecomputeSuccessor_ParkingOnlyNodeRemovedFromRevision_DoesNotTailReach()
    {
        var runner = MakeRunner(Path.Combine(_dir, "q-anchor3"), referenceProvider: null);
        var run = RunWithOutcomes(("n1", WorkflowRunner.LocalWaitResultWord));
        var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n2", "n3")); // n1 已从修订消失
        Assert.NotNull(relocated);
        Assert.Equal("n2", relocated!.NodeId);
    }

    /// <summary>
    /// 【突变验证 ✔（M12：从写侧快照物化删除 AdmissionIdentity 行 ⇒ 红；实测见突变记录）】
    /// 【Wave1 会诊 F2】Store 持久化面的判别力：写侧快照丢 admissionIdentity ⇒ 往返夹具必红。
    /// </summary>
    [Fact]
    public void Upsert_RoundTripAdmissionIdentity_MutationTargetGuard()
    {
        var store = new LocalWaitQueueStore(_dir);
        var item = FullItem();
        store.Upsert(item);
        var loaded = Assert.Single(store.Load());
        Assert.Equal(item.AdmissionIdentity, loaded.AdmissionIdentity);
    }

    /// <summary>
    /// 【C4②】版本 2 时期文件（无 admissionIdentity 键）仍可读 ⇒ 字段为 null ⇒ 翻译层标注
    /// 合同前存量（永不参选）；键存在但形状非法（空串）⇒ 响亮拒绝。
    /// </summary>
    [Fact]
    public void Load_V2FileWithoutAdmissionIdentity_ReadsNull_AndClassifiesLegacy()
    {
        var v2Dir = Path.Combine(_dir, "v2");
        Directory.CreateDirectory(v2Dir);
        var item = FullItem();
        var v2Json = """
        {
          "version": 2,
          "items": [
            {
              "itemId": "%ID%",
              "stableIdentity": "%SID%",
              "candidateId": "",
              "namespace": "successor",
              "workflowId": "wf-x",
              "tier": 2,
              "priority": 0,
              "isHoeingHighest": false,
              "hasTrustedIdentity": false,
              "enqueuedAtUtc": "2026-09-26T00:00:00+00:00",
              "state": 0,
              "prerequisiteReference": "wf-x/n1/2/3@ticket-7"
            }
          ]
        }
        """.Replace("%ID%", item.ItemId).Replace("%SID%", item.StableIdentity);
        File.WriteAllText(Path.Combine(v2Dir, "wait-queue.json"), v2Json);

        var loaded = Assert.Single(new LocalWaitQueueStore(v2Dir).Load());
        Assert.Null(loaded.AdmissionIdentity);
        var r = LocalWaitIdentityTranslation.Translate(loaded);
        Assert.False(r.Ok);
        Assert.True(r.LegacyPreContract);
    }

    /// <summary>
    /// 【突变验证 ✔（M10：去掉登记点拒绝分支 ⇒ 红；桩阶段登记了不完整项）】【C4①】
    /// 引用来源未提供（注入点为 null）⇒ 登记点**拒绝登记**：Park=true（零发送停驻）、
    /// 原因显式指向载荷合同，且**队列零变化**（不得把结构性永不参选项写进队列）。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_WithoutReferenceSource_RefusesToRegister()
    {
        var queueDir = Path.Combine(_dir, "q-refusal");
        var runner = MakeRunner(queueDir, referenceProvider: null);
        var outcome = runner.TryRegisterLocalWait(MakeRun(), MakeOccurrence(), attempt: 1);

        Assert.True(outcome.Park, "登记被拒仍须零发送停驻（不得回落提交）");
        Assert.Contains("拒绝", outcome.Reason);
        Assert.Empty(new LocalWaitQueueStore(queueDir).Load());
    }

    /// <summary>
    /// 【突变验证 ✔（M11：去掉准入身份组装或引用填充 ⇒ 红）】【C4①】
    /// 引用来源提供 ⇒ 登记完整载荷：前置引用非空、准入身份/候选号绑定正确、队列本地身份自洽，
    /// 且翻译层对登记产物验证通过（登记成功＝可重评的身份面成立）。
    /// </summary>
    [Fact]
    public void TryRegisterLocalWait_WithReferenceSource_RegistersCompletePayload()
    {
        var queueDir = Path.Combine(_dir, "q-complete");
        var runner = MakeRunner(queueDir, referenceProvider: (_, _) => "wf-x/n1/2/3@ticket-7");
        var outcome = runner.TryRegisterLocalWait(MakeRun(), MakeOccurrence(), attempt: 1);

        Assert.True(outcome.Park);
        Assert.Contains("已登记", outcome.Reason);
        var loaded = Assert.Single(new LocalWaitQueueStore(queueDir).Load());
        Assert.Equal("wf-x/n1/2/3@ticket-7", loaded.PrerequisiteReference);
        Assert.False(string.IsNullOrWhiteSpace(loaded.AdmissionIdentity));
        Assert.Equal(ArbitrationOrdering.DeriveCandidateId(loaded.AdmissionIdentity!), loaded.CandidateId);
        Assert.True(LocalWaitIdentityTranslation.Translate(loaded).Ok);
    }

    // ---------- C4②：v1 存量不可经 Upsert 补全（不可变载荷合同回归护栏） ----------

    /// <summary>
    /// 【C4②（零新通道）】已取消的 v1 存量项（无引用）同载荷重登记 ⇒ 重新激活（既有合同）；
    /// 同身份但**补上引用**的登记 ⇒ 响亮冲突（不可变载荷：原地补全被拒绝，D-E3=(a) 未采纳）。
    /// </summary>
    [Fact]
    public void Upsert_V1LegacyReactivation_CannotCompleteReferenceInPlace()
    {
        var store = new LocalWaitQueueStore(_dir);
        var legacy = FullItem();
        legacy.PrerequisiteReference = null;
        legacy.AdmissionIdentity = null;
        legacy.CandidateId = "";
        store.Upsert(legacy);
        store.PersistCleanup(_ => "失效清理（夹具构造墓碑）", DateTimeOffset.UtcNow);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(store.Load()).State);

        var reactivated = FullItem();
        reactivated.PrerequisiteReference = null;
        reactivated.AdmissionIdentity = null;
        reactivated.CandidateId = "";
        store.Upsert(reactivated);
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(store.Load()).State);

        var withReference = FullItem(); // 同身份、载荷多出引用 ⇒ 必须冲突
        var ex = Assert.Throws<LocalWaitQueueCorruptException>(() => store.Upsert(withReference));
        Assert.Contains("登记载荷不同", ex.Message);
    }

    /// <summary>
    /// 【突变验证 ✔（M15：删除 HasSameRegistrationPayload 的 AdmissionIdentity 比较行 ⇒ 红，
    /// 实测记录见 _batch20/b20_wave1_mutations.md）】【Wave1 会诊 F3＋R2 必改 #1】
    /// 不可变登记载荷比较必须覆盖 AdmissionIdentity，且夹具必须**隔离该变量**：
    /// 两个方向上除 AdmissionIdentity/CandidateId 配对外其余载荷维度（含 PrerequisiteReference、
    /// CandidateId 两侧同值）全部相同 ⇒ 唯一判别维度就是准入绑定——该行被删即两方向都不再冲突。
    /// </summary>
    [Fact]
    public void Upsert_PayloadComparisonCoversAdmissionIdentity_BothDirections()
    {
        var dir = Path.Combine(_dir, "q-payload-adm");
        var store = new LocalWaitQueueStore(dir);

        var legacy = FullItem();
        legacy.PrerequisiteReference = null;
        legacy.AdmissionIdentity = null;
        var sharedTamperedCandidateId = "cand-tampered0000000000000000"; // 两侧同值：隔离 CandidateId 维度
        legacy.CandidateId = sharedTamperedCandidateId;
        store.Upsert(legacy);

        var bound = FullItem();
        bound.PrerequisiteReference = null;
        bound.CandidateId = sharedTamperedCandidateId; // 唯一差异＝AdmissionIdentity null→绑定值
        var ex1 = Assert.Throws<LocalWaitQueueCorruptException>(() => store.Upsert(bound));
        Assert.Contains("登记载荷不同", ex1.Message);

        var dir2 = Path.Combine(_dir, "q-payload-adm-r");
        var store2 = new LocalWaitQueueStore(dir2);
        var boundFirst = FullItem();
        boundFirst.PrerequisiteReference = null;
        boundFirst.CandidateId = sharedTamperedCandidateId;
        store2.Upsert(boundFirst);
        var legacySecond = FullItem();
        legacySecond.PrerequisiteReference = null;
        legacySecond.AdmissionIdentity = null;
        legacySecond.CandidateId = sharedTamperedCandidateId; // 唯一差异＝AdmissionIdentity 绑定值→null
        var ex2 = Assert.Throws<LocalWaitQueueCorruptException>(() => store2.Upsert(legacySecond));
        Assert.Contains("登记载荷不同", ex2.Message);
    }

    /// <summary>
    /// 【Wave1 R2 必改 #4（重要）闭合方式②：本批对抗字段夹具】字段载荷含 '|'／'~'／空串的候选，
    /// 经权威 EncodeString（'~'→"~0"、'|'→"~1"，顺序不可颠倒；空串/null→"~"）转义后，
    /// 裸 '|' 计数恒为 8。权威侧合同另有既有夹具（闭合方式①指针）：
    /// Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ArbitrationOrderingTests.cs:402-435
    /// （Encoding_StringEscape_RoundTrip／Encoding_Int_Boundary——转义单射往返＋整数越界响亮拒绝）。
    /// 本夹具把该合同接到等待登记面：对抗字段的登记产物 Translate 必须通过、'|' 计数恒 8、
    /// 第 6 段解码回原值（前缀切分不歧义）。
    /// </summary>
    [Fact]
    public void Translate_AdversarialFieldPayloads_PipeCountInvariantHolds()
    {
        const string runId = "run-abc123def456";
        foreach (var nodeId in new[] { "n|1", "n~1", "n|1~2", "" })
        {
            var item = FullItem(nodeId: nodeId);
            var r = LocalWaitIdentityTranslation.Translate(item);
            Assert.True(r.Ok, $"对抗字段 nodeId={nodeId} 应转义后合法：{r.Reason}");
            Assert.Equal(8, item.AdmissionIdentity!.Count(c => c == '|'));
            Assert.Equal(item.AdmissionIdentity[..item.AdmissionIdentity.LastIndexOf('|')], r.OccurrenceIdentity);
            // 出现身份前缀与队列本地 4 段裸拼在语义上同构（runId/nodeId/occurrence/loopIteration），
            // 9 元组第 6 段即转义后的 nodeId：解码回原值（单射合同）。
            var seg6 = item.AdmissionIdentity.Split('|')[5];
            Assert.Equal(nodeId, Services.ArbitrationOrdering.ArbitrationIdentityEncoding.DecodeString(seg6));
        }
    }

    // ---------- 装配 ----------

    private static WorkflowRunRecord MakeRun(string? scope = "bgi:local:e1")
        => new()
        {
            RunId = "run-abc123def456",
            WorkflowId = "wf-x",
            AdmissionSourceScope = scope,
            Cursor = new WorkflowNodeCursor { NodeId = "n1", Occurrence = 2, LoopIteration = 3, Attempt = 1 },
        };

    private static WorkflowNodeOccurrence MakeOccurrence() => new("n1", 0, 2, 3);

    private WorkflowRunner MakeRunner(string queueDir,
        Func<WorkflowRunRecord, WorkflowNodeOccurrence, string?>? referenceProvider,
        Func<WorkflowRunRecord, string?>? scopeProvider = null)
        => new(new WorkflowStore(Path.Combine(_dir, "flows")), new RunStore(Path.Combine(_dir, "runs")),
            new FakeBoundary(), new FakePrerequisite(), new FakeTerminal(),
            localWaitQueue: new LocalWaitQueueStore(queueDir),
            localWaitPrerequisiteReferenceProvider: referenceProvider,
            localWaitAdmissionScopeProvider: scopeProvider);

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
            => Task.FromResult(BoundarySubmitResult.AcceptedWith("job-1"));
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakePrerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
            => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "假适配器默认对账未决", record.JobId));
        public Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
            => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Cancelled, null, record.JobId));
    }

    private sealed class FakeTerminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("job-terminal"));
    }
}
