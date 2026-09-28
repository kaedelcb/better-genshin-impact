
## objective: _workflow/wave3-bo6bo7-receive/context.md L1-L10 SHA256=68953edeb4e0ae10235dcd5d08915b276ee9d0da344131ae3a2477f70286f283

# 接收批目标（objective）

把已独立交付、并按原等级闭环的 Wave3 BO-6/BO-7 修复**接收进茶包主线**，并在主线 HEAD 上重新取得定向回归、助手全量回归、反向突变与声明面证据；只做接收及由集成引起的最小适配，不施工 BO-8/BO-9、R5.6、R6.1、R6 diff guard，不重开已闭合的 BO-13、SB21-3 BO-4、SB21-2 BO-10/12。

来源交付：`wave3-bo6-bo7-2026-09-28`，隔离 worktree `C:\\Users\\Administrator\\.codex\\worktrees\\wave3-bo6-bo7\\better-genshin-impact-LCB`，交付 HEAD `e009068e22b18d89f4eb9f8947fa937dfa8c925c`，开工基线 `e2613a851bd45c28fdd56b84dfc10784e1d9c9b8`。
本批开工 HEAD：`6fd6207e58ec93dcc38645c12131a9a512f5fc69`（`main-OldTeaBag-B168`）。主线相对来源基线只多 3 个文档提交，与来源增量无文件重叠。

接收方式：不合并隔离分支；按 `git checkout e009068e2 -- <路径>` 逐字节导入来源相对基线的 8 个产品/测试/状态文档文件，另收录少量权威证据（检查点、两份独立会诊报告与元数据、会诊台账对账、突变证据索引、testId 对照、风险矩阵）。6 份 closeout 快照的 report.json/packet.md 与逐突变 TRX 日志包留在来源 worktree，不搬入。

完成判据（本批）：BO-6/7 修复确实进入主线；集成版本定向 93/93 与助手全量回归通过且与来源逐 testId 对照；8 项反向突变在集成字节上重新执行为 baseline Passed / mutant Failed / restored Passed 且精确恢复；四项原级义务闭环状态未被削弱；声明面按规则再生并评审；台账回填；生产门保持关闭。


## findings: _workflow/wave3-bo6bo7-receive/findings.md L1-L69 SHA256=5dae61b0ff7374416a4d2c0e7210cbf1c20b6d5338458fd990bea948c707c82c

# 发现、处置与残余（findings）

## 本批性质

接收批（mode=code）：把来源独立交付的内容逐字节导入主线并**重跑**验证，不复用来源数字。

**导入保真（含接收后改动，逐项区分）**：导入动作的暂存增量 `git diff --cached -- <8 文件>` 与来源增量 `git diff e2613a851 e009068e2 -- <8 文件>` **逐字节相同**（两侧 SHA-256 均为 `0642971729f3eff0…`）；逐文件核对索引 blob 等于交付 blob，全部为真。因此**导入时**8 个文件与来源已审增量逐字节相同，无改写、无适配。

**但"逐字节相同"只是导入时点的事实，不是最终树的事实**：本批随后按收口要求对 4 个状态文档追加了登记内容（R5.3 §24.126、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、总计划），并按 §17.4-A 第 1 条对 `ClaimSurfaceManifest.txt` 执行了强制再生。故最终工作区相对导入状态有改动；`verification/post-import-delta.diff` 完整列出这些改动，`verification/intake-equivalence.json` 逐文件记录"导入=交付"与"接收后是否修改"两项判定。**产品源码与两个夹具文件在接收后未被修改**（未暂存 diff 为空）。

## 主线工作区行尾与来源记录的对应

主线按 `core.autocrlf=true` 检出，工作区为 CRLF，仓库 blob 为 LF。两者内容逐字节相同（仅行尾差异）：
- `WorkflowRunner.cs`：仓库 blob（LF）`181aa93e000be99d8eed9ce192587a56194d270e47f67bd561297a57476f14c9`（即两名来源审查者核验时引用的"当前源码"），工作区（CRLF）`5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`。
- `ClaimSurfaceManifest.txt`：导入时（LF/blob 形态）`84E1D93DCC4D8B4B13EEB82C8CD9C25163FF9C18E59E3F772184997528B6D10D`；主线声明面再生后为 `5D91461178DA98729562B486060A4322F6A96C3469778A064DCA52B7D8B4D267`。
本批 manifest 的所有 `source_sha256` 记录**工作区 CRLF 形态**，内部自洽；来源清单记录的是混合形态（original/restored 为 LF、mutant 为 CRLF），仅作对照。

## 发现 F-1（建议级 · 声明面版本引用陈旧）——已登记，不改写来源工件

**事实**：§24.125.3、`_workflow/wave3-bo6-bo7/owner-checkpoint.md`、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、总计划均把声明面清单 SHA 记为 `9EAA1A7F6EFAECCB763DA8EE10520E01B128F3129E6DBFB831295EA58CD083DC`；随交付提交的清单实际为 `84E1D93DCC4D8B4B13EEB82C8CD9C25163FF9C18E59E3F772184997528B6D10D`。

**已核清原因**：来源目录 `regression/claim-surface-post-consultation/` 记录 `manifest-before=9EAA1A7F…`、再生后 `manifest-after-regen=manifest-after-no-env=84E1D93D…`。即 `9EAA1A7F…` 是"修正 §24.125 状态/预算声明"那一步的中间值；其后新增的 §24.125.5（owner 批准追加会诊的闭环文本）再次改变声明面，来源侧执行了再生却未回改前述散文引用。

**处置**：不改写已与检查点登记哈希 `A4AB7747…` 绑定的来源工件（改写会使登记失效），改在 R5.3 §24.126.4 登记权威口径（最终值以随提交清单为准，`9EAA1A7F…` 只作该步中间值）。**级别判定说明**：这是散文中的证据指针版本问题，不影响源码、夹具、突变、四项义务闭环或清单内容本身；两名来源审查者与来源批均未据此发现运行正确性缺陷。本批不据此升级为重要项，但也不隐藏：它进入本批的受审材料与最终报告。

## 发现 F-2（建议级 · 残留，来源已登记）BO-6/7-D1

`WorkflowRunner.cs` 中注释与实现不一致（`:1486` 仍写"取最后一条可定位停驻标记"，`:1535–1537` 仍以"DriveAsync/Relocate 无完成跳过"为下界理由）。两名来源审查者独立指出、均评为建议级。**本接收批的第 1 轮会诊进一步指出同一族问题还包含约 `:1400–1403` 的"最后一条/重复提交"旧表述；该扩大范围已采纳并并入 D1 的残项描述**，仍不改动已审源码。本批**不为它单独改写已审源码**：改动会作废 manifest 源哈希与 8 项突变绑定、需重跑整套构建/回归并重取同等级复审，而该处不影响行为正确性。按既定处置留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并修正并按该批质量门验证。

## 8 项反向突变（在集成后的主线字节上重新执行）

全部为 baseline Passed / mutant Failed / restored Passed，三阶段构建 exit 0、测试 exit 0→1→0，命中具名目标断言，源码逐字节精确恢复。`原始/恢复` SHA-256 = 工作区 `5470cfcb…`；8 个 `mutant_sha256` 与**来源交付记录完全相同**。

| id | target test | target testId | 断言位置 | mutant sha (前12位) |
|---|---|---|---|---|
| bo6-resume-live-park-v3 | Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 96212d01e57a |
| bo6-completed-filter-v3 | 同上 | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 3115f2c3a4d8 |
| bo6-tail-fail-closed-v3 | Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion | 113f020c-1c29-e336-4c1a-3a50b69cbcc8 | WorkflowRunnerTests.cs:1071 | ff0ff5bf3594 |
| bo6-tail-persisted-failure-v4 | 同上 | 113f020c-1c29-e336-4c1a-3a50b69cbcc8 | WorkflowRunnerTests.cs:1080 | a04531dc4871 |
| bo7-candidate-first-v3 | Resume_CandidateAndRescueAcrossLoop_UsesFullPlanOrderAndAdvancesThroughRunner | 1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc | WorkflowRunnerTests.cs:1131 | ef6ed36b1b8b |
| bo7-rescue-first-v3 | Resume_RescueBeforeNextLoopCandidate_UsesFullPlanOrderAndFiltersCompletedAnchor | 7a665a8f-f8e9-11b4-598f-a1f8d24b805a | WorkflowRunnerTests.cs:1173 | e5fcb0af5aec |
| bo7-earliest-park-v3 | Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 8668d00c7f7e |
| bo7-stable-identity-v3 | 同上 | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | f04db2ad7111 |

各突变的 baseline/mutant/restored 构建日志与 TRX、`mutation.patch`、`source-original.cs` 位于 `_workflow/wave3-bo6bo7-receive/mutations/<id>/`；机读记录见 `mutation-records.json`。突变执行后 `WorkflowRunner.cs` 与 `WorkflowRunnerTests.cs` 的 `git diff` 为空，即导入字节被逐字节还原。

## 回归与对照

- 接收前同条件基线（HEAD `6fd6207e5`）：定向三类 89/89；助手全量 1558 passed / 2 skipped / 0 failed / 1560。
- 集成后：定向三类 93/93（93 个 testId/名称/结果与来源交付最终 TRX **完全相同**）；助手全量 1562 passed / 2 skipped / 0 failed / 1564。
- testId 差集（基线→集成）：1556 unchanged / 8 added / 4 removed / 0 changed，与来源交付逐 ID 相同。`0 changed` **不**表示既有测试语义未变：两个保留 testId 的 helper（`ec3b4575…` `RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate`、`14addbd1…` `RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed`）断言由"期望 null（保守链尾）"改为"期望返回停驻标记"。
- 结果口径更正：`Counters@notExecuted` 在这些 VSTest/xUnit 运行中为 0，而实际有 2 条 `NotExecuted` 结果行（两个 opt-in P50 诊断：`P50_LoadRepro_WholeClass_UnderControlledLoad`、`P50_DiagnosticRepeat_OptIn`）。**行级口径为准**：基线 1558 passed + 2 NotExecuted = 1560；集成 1562 passed + 2 NotExecuted = 1564。本批早先的 `testid-comparison-summary.json` 误用计数器属性写成 skipped=0，已按行级口径更正并附 `verification/regression-outcomes.json`。
- 声明面：再生 1/1 通过（清单 599→602 行，**+3 行 / -0 行**，仅新增 §24.126 中三行承载声明关键词的文本；无既有声明行被改或消失），清除变量复跑 1/1 通过，两次 SHA 稳定为 `5D91461178DA98729562B486060A4322F6A96C3469778A064DCA52B7D8B4D267`。
- 部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant`（及非 x64 同名目录）在全部构建与测试前后 LastWriteTimeUtc 与文件数均未变化（1158 个文件，09/26 21:41:14 UTC）。

## 范围边界与未验证项

- 只接收 BO-6/BO-7；**BO-8 R29 IMPORTANT 与 BO-9 R34 F5 IMPORTANT 仍未并入、仍未闭合**；R5.6、R6.1、R6 diff guard 未提前集成；BO-13、SB21-3 BO-4、SB21-2 BO-10/12 未重开。
- BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭；**未做实机、生产或真实 User 验证**。外部执行边界仍为 fake。
- 本批不声称除上述同步之外的四项义务仍然为 closed；那由来源侧两次独立会诊按原等级裁定，本批只证明其被接收的字节未变且判别力仍在。

## 会诊轮次与逐项处置（本批子批计数）

见 `consultation/review-outcome-v1.md`。第 1 轮（既有 GPT 会诊工具，`gpt-6-astra` / `medium`，attempts=1，read-only，含自动附加 diff）给出 2 项 IMPORTANT 与 1 项建议：
- **IMPORTANT-1（送审材料未覆盖八文件导入保真）**：成立。原材料的 `scoped-*.diff` 只覆盖 manifest 的 `sources`（4 个代码/夹具文件），4 个状态文档的补丁未随材料提供，且"8 文件逐字节相同"缺少"导入时点"限定。**已修复**：新增 `verification/intake-equivalence.json`（逐文件导入=交付判定 + 导入增量与来源增量的 SHA-256 对照 + 接收后改动清单）、`verification/import-delta-8files.diff`（含 4 个文档补丁的完整导入增量）、`verification/post-import-delta.diff`（接收后改动），并修正本文件上文限定语。
- **IMPORTANT-2（替代摘要的结果口径不一致）**：成立。`testid-comparison-summary.json` 用 `Counters@notExecuted`（0）当 skipped，与两次运行各有 2 条 `NotExecuted` 行矛盾。**已修复**：按行级口径重写摘要，并新增 `verification/regression-outcomes.json` 记录每次运行的全部计数与 2 条 `NotExecuted` 的具体用例名。
- **建议（D1 注释范围更宽 + 夹具映射只有 10 条不足以独立证明 93 项完全一致）**：**采纳**。D1 范围扩大至约 `:1400–1403`（见上）；新增 `verification/targeted-93-ids.json`（完整 93 条 testId/名称/结果与来源交付 TRX 的逐条一致性与零 mismatch）与 `verification/mutation-verification.json`（8 项突变的 baseline/mutant/restored 结果、目标身份、断言行、文件摘要，以及与来源清单记录 mutant SHA 的逐项相等性）。

第 1 轮的收尾结论为"本接收批仍有 2 项未闭合 IMPORTANT"；按纪律 R1/R2 不得降级，已按同一修复批归集修复证据并执行验证轮会诊（见 `consultation/review-outcome-v2.md`）。


## budget: _workflow/wave3-bo6bo7-receive/budget.md L1-L33 SHA256=efcc8930d9574e8d4d6fe4cf48dcf1b58ec188564d67cc8436560032c068f9b8

# 会诊预算与分级台账（budget）

## 子批计数（R7）

- **本接收批是一个新的子批**，拥有自己的累计会诊请求上限（8 次，含首审/复审/验证/收口，且所有模型与渠道；已发出但失败或超时也计次；本地预检拦截而未发出者不计）。
- **来源 BO-6/BO-7 子批的 8/8 + owner 批准追加 2/2 不被重置、不被继承、也不被本批消耗**：其原始记录（6 次 GPT 工具调用、8 次 provider/tool 报告尝试 → 保守 8/8、余额 0；另有 owner 批准的请求 9/10 各 1 次，2/2 用尽、余额 0）保持原样登记于 `_workflow/wave3-bo6-bo7/owner-checkpoint.md` 与 `consultation/current-ledger-reconciliation.json`。
- 本批计数：见文末「本批实际发出记录」。

## 固定强度与点位

- 固定模型/强度：`gpt-6-astra` / `medium`（与来源子批一致），不降配。
- 本批固定会诊点：**验证轮 1 次**——在冻结的集成快照上审"接收是否正确、集成版本证据是否支持接收结论、四项原级义务的字节绑定是否仍成立、F-1/F-2 的处置是否合规"。
- 渠道：默认既有 GPT 会诊工具；送审前按设施计划做容量预检（附件数/单文件字节/合计字节、附件全文 token 估算、工具另附 diff 完整性）。仅当满足设施计划的三条分流条件时才改走独立本地只读 CLI，并保持同模型同强度、记录触发依据与退出码。

## 原等级与未闭合项（不得降级）

| 项 | 原级 | 状态 |
|---|---|---|
| BO-6 R19 | MUST | 来源侧 closed；本批证明被接收字节未变、判别力仍在 |
| BO-6 R21 F4 | IMPORTANT | 同上 |
| BO-7 R21 F1 | MUST | 同上 |
| BO-7 R21 F2 | MUST | 同上 |
| BO-6/7-D1（注释陈旧） | 建议级 | 未修正，按既定处置留待下次绑定 `WorkflowRunner.cs` 的批次 |
| BO-8 R29 | IMPORTANT | **未闭合、未并入** |
| BO-9 R34 F5 | IMPORTANT | **未闭合、未并入** |

本批**不**对 BO-8/BO-9 作任何等级或状态变更；若会诊给出重要/必改发现，只能升级不能降级，且必须闭环或形成 owner 检查点。

## 本批实际发出记录

本批发出的实质性会诊请求记录（渠道、模型/强度、附件与 token 估算、退出码、结论与逐项处置）写入同目录 `consultation/review-outcome-v1.md`，并在并行成果台账与 R5.3 §24.126 的收口材料中回填；本节不自引用该结果，以免送审材料随结果变化而失效。

`consultation/preflight-v1.json` 记录送审前的容量预检（文件清单、字节数、token 估算方法、占有效窗口比例，以及本次为何仍使用既有 GPT 会诊工具）。


## source: MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs L1-L2099 SHA256=5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96

using System.Collections.Concurrent;
using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>节点执行提交请求（流程只持类型化引用；收尾抑制标记随调用传递，D10）。</summary>
public sealed record WorkflowSubmitRequest(
    WorkflowRunRecord Run,
    WorkflowNodeOccurrence Occurrence,
    WorkflowNode Node,
    bool SuppressConfigCompletionAction);

public enum BoundarySubmitKind
{
    Accepted,
    Rejected,
    Unknown,
    Wait,
    Hold,
}

/// <summary>提交边界的互斥判别结果；Wait/Hold 不等于拒绝，也不授予发送许可。</summary>
public sealed record BoundarySubmitResult
{
    private BoundarySubmitResult(BoundarySubmitKind kind, string? jobId, string? rejectReason,
        bool retryable, LocalWaitDecisionRecord? waitDecision)
    {
        Kind = kind;
        JobId = jobId;
        RejectReason = rejectReason;
        Retryable = retryable;
        WaitDecision = waitDecision;
    }

    public BoundarySubmitKind Kind { get; }
    public bool Accepted => Kind == BoundarySubmitKind.Accepted;
    public string? JobId { get; }
    public string? RejectReason { get; }
    public bool Uncertain => Kind == BoundarySubmitKind.Unknown;
    public bool Retryable { get; }
    public LocalWaitDecisionRecord? WaitDecision { get; }

    public static BoundarySubmitResult AcceptedWith(string jobId) => new(BoundarySubmitKind.Accepted, jobId, null, false, null);
    public static BoundarySubmitResult Rejected(string reason) => new(BoundarySubmitKind.Rejected, null, reason, false, null);
    /// <summary>
    /// **[P8／§24.62]** 确定拒绝·**开重试窗口**（§3.2a）：证据＝**可证实未发送**（`BgiNotSentException`）
    /// 或门面已结清的等值无损拒绝（`AdmissionResultKind.RetryableRejected`）。语义与普通「终局确定拒绝」
    /// 分开：窗口内可经**新许可**（`RetryAsync`）再入场。
    /// </summary>
    public static BoundarySubmitResult RejectedWithRetryWindow(string reason)
        => new(BoundarySubmitKind.Rejected, null, reason, true, null);
    public static BoundarySubmitResult UnknownWith(string reason) => new(BoundarySubmitKind.Unknown, null, reason, false, null);
    public static BoundarySubmitResult WaitWith(string reason, LocalWaitDecisionRecord? decision = null)
        => new(BoundarySubmitKind.Wait, null, reason, false, decision);
    public static BoundarySubmitResult HoldWith(string reason, LocalWaitDecisionRecord? decision = null)
        => new(BoundarySubmitKind.Hold, null, reason, false, decision);
}

/// <summary>边界观察终态（R4.8 一轮 B1/I3 结构化：远端原词与本地查询不可考分开）。
/// Terminal=远端终态原词（succeeded/failed/cancelled/skipped/rejected；null=未观察到）；
/// Uncertain=true 时调用方走 Unknown 停驻，不得经词汇映射落 failed；
/// Reason/ErrorCode 受控原因上 UI。</summary>
public sealed record BoundaryTerminalResult(string? Terminal, bool Uncertain, string? Reason, string? ErrorCode = null)
{
    public static BoundaryTerminalResult Observed(string terminal, string? reason = null, string? errorCode = null)
        => new(terminal, false, reason, errorCode);
    public static BoundaryTerminalResult UncertainWith(string reason) => new(null, true, reason);
}

/// <summary>
/// 执行边界（R4.5 可测试接缝）：流程引擎 → BGI 单项/配置组/整龙能力的唯一出口。
/// 生产实现经 ext.task.start（严格合同）+ ext.job.status 终态查询；测试用假实现。
/// 等待不占槽位由引擎保证：等待期间不调用本接口、不提交任何作业。
/// </summary>
public interface IWorkflowExecutionBoundary
{
    /// <summary>执行端 task.single.native 能力实况（D4 预检输入）。</summary>
    bool SingleNativeSupported { get; }

    /// <summary>执行端 execution.suppressConfigCompletionAction 能力实况（B6/E4'；缺省 true=测试接缝免接线，生产按 capability 实况）。</summary>
    bool SuppressConfigCompletionSupported => true;

    /// <summary>提交节点执行（调用前引擎已持久化提交意图，D11）。</summary>
    Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct);

    /// <summary>等待作业终态（R4.8 一轮 B3 纯观察：取消只终止等待，绝不再发远端取消——取消走 RequestCancelAsync）。</summary>
    Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct);

    /// <summary>请求远端取消在飞作业（R4.8 一轮 B3：best-effort，应答不代表清理完成，终态以 AwaitTerminalAsync 观察为准）。
    /// 默认 no-op（测试假实现免接线）；生产实现 = ext.task.cancel（ownedOnly=v1）。</summary>
    Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>前置结果状态（R4.6 B3 结构化：Unknown 不按普通失败续跑，走保守路径）。</summary>
public enum PrerequisiteStatus
{
    /// <summary>放行资源执行。</summary>
    Proceed,
    /// <summary>真实失败（受控原因）。</summary>
    Failed,
    /// <summary>受理即拒（能力/队列/合同）。</summary>
    Rejected,
    /// <summary>取消（远端已确认——显式跳过确认链走完）。</summary>
    Cancelled,
    /// <summary>结果不确定（禁止续跑/重发，标 Unknown 待对账）。</summary>
    Unknown,
}

/// <summary>前置结果（R4.6 B3 结构化；JobId 受理即回报，供引擎落盘 submitted 事实）。</summary>
public sealed record PrerequisiteResult(PrerequisiteStatus Status, string? Reason, string? JobId = null)
{
    public static readonly PrerequisiteResult ProceedInstance = new(PrerequisiteStatus.Proceed, null);
    public bool Proceed => Status == PrerequisiteStatus.Proceed;
    public static PrerequisiteResult FailedWith(string reason) => new(PrerequisiteStatus.Failed, reason);
}

/// <summary>前置策略适配器（D8：账号/兑换等前置动作的受控执行出口；生产实现 = BgiWorkflowPrerequisiteAdapter）。</summary>
public interface IWorkflowPrerequisiteAdapter
{
    /// <summary>支持的前置策略类型（R4.6 I1 能力协商；缺省=全部——测试假实现免接线；生产按 ext capability 实况）。</summary>
    IReadOnlySet<string> SupportedKinds => WorkflowKindCatalog.StrategyKinds;

    /// <summary>执行前置策略；事实绑定 run/节点出现/attempt（意图记录由引擎先行落盘）。</summary>
    Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, CancellationToken ct);

    /// <summary>恢复对账（R4.6 B2：意图/在飞记录先查远端权威终态；查不到=Unknown，不盲目重发）。默认 Unknown（保守）。</summary>
    Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
        => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "适配器不支持对账", record.JobId));

    /// <summary>前置期显式跳过的远端取消确认（B4 同构；确认超时=Unknown）。默认直接确认（测试假实现无远端）。</summary>
    Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
        => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Cancelled, null, record.JobId));
}

/// <summary>收尾执行结果（R4.6 E3'：executed / rejected / unknown / cancelled；发送 ≠ 完成）。</summary>
public sealed record TerminalExecutionResult(string State, string? JobId, string? Reason)
{
    public static TerminalExecutionResult Executed(string? jobId) => new("executed", jobId, null);
    public static TerminalExecutionResult RejectedWith(string? reason) => new("rejected", null, reason);
    public static TerminalExecutionResult UnknownWith(string? jobId, string? reason) => new("unknown", jobId, reason);
    public static TerminalExecutionResult CancelledWith(string? jobId, string? reason) => new("cancelled", jobId, reason);
}

/// <summary>终止动作执行器（D10：仅流程成功边界调用；生产实现 = BgiWorkflowTerminalExecutor）。</summary>
public interface IWorkflowTerminalExecutor
{
    /// <summary>支持的收尾类型（缺省=全部——测试假实现免接线；生产按 ext capability 实况）。</summary>
    IReadOnlySet<string> SupportedKinds => WorkflowKindCatalog.TerminalKinds;

    /// <summary>执行收尾动作；生产实现受理即持久化 submitted 事实（jobId），再等待 executed。</summary>
    Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct);
}

/// <summary>显式运行动作（锚点 2：立即生效走显式动作，不硬切执行中叶子）。</summary>
public enum WorkflowRunAction
{
    /// <summary>停止流程（终态 Cancelled；不触发收尾，D10）。</summary>
    Stop,
    /// <summary>跳过当前节点（绑定请求时出现身份；远端取消确认后推进，B4）。</summary>
    SkipCurrent,
    /// <summary>重载流程定义（运行中改流：新修订在下一节点边界生效）。</summary>
    ReloadDefinition,
    /// <summary>暂停（≠停止；节点边界生效，保留等待记录；显式 ResumeAsync 恢复）。</summary>
    Pause,
}

/// <summary>引擎选项（失败策略/时钟/延时工厂/确认超时——测试可注入，全计时可取消）。</summary>
public sealed class WorkflowRunnerOptions
{
    /// <summary>节点失败/拒绝后是否继续后续节点（D12；默认 false=停止流程，保守防假成功续跑）。</summary>
    public bool ContinueOnNodeFailure { get; init; }

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>
    /// **[批次 20／Wave3／C11] 本地等待判定委托（可选；默认 null ⇒ 恒 false＝未接线）**。
    /// 接线批注入门面等待结论判定；测试注入停驻触发判定。**实例级**——随 Runner 生命周期，
    /// 无进程级静态污染（R43 重要-6）。
    /// </summary>
    public Func<WorkflowNodeOccurrence, bool>? ShouldRegisterLocalWait { get; init; }

    /// <summary>可取消延时（测试用手动时钟快进；生产 Task.Delay）。</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    /// <summary>显式跳过后确认远端终态的超时（B4：超时 = cancelUnconfirmed → Unknown，不猜成功）。</summary>
    public TimeSpan SkipConfirmTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>日志出口（留痕纪律；不含敏感账号字段）。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// **[批次 20／C4①] 本地等待登记结果**（D1 登记点的结构化产物）。
/// <see cref="Park"/>＝运行应按「确定零发送」停驻（登记成功**或**登记被拒都停驻——门面已给出零发送
/// 结论，**不得**回落到提交路径把停驻改写成发送尝试）；<see cref="Reason"/> 区分两种停驻与不适用。
/// </summary>
public sealed class LocalWaitRegistrationOutcome
{
    /// <summary>
    /// true＝零发送停驻（已登记／登记被拒／**接缝命中而队列缺失＝接线缺陷**，三种来源都停驻）。
    /// **[Wave1 R15 重要-2／R17 重要-1]** 当前**不存在 Park=false 的产出路径**（登记接缝命中后
    /// 队列缺失也属接线缺陷，必须以零发送停驻收场——不得回落提交把门面零发送结论改写成真实发送）；
    /// <c>NotParked</c> 仅为接缝未命中场景预留的防御形态、当前无产出方。
    /// </summary>
    public bool Park { get; init; }

    /// <summary>停驻原因（审计用；不适用时为空串）。</summary>
    public string Reason { get; init; } = "";
    public LocalWaitDecisionRecord? Decision { get; init; }

    /// <summary>
    /// **[Wave1 R17 建议-2] 当前无产出方**（<see cref="WorkflowRunner.TryRegisterLocalWait"/> 全部出口
    /// 均为 Park=true）：保留为「登记接缝未命中」场景的防御形态，**不是**「未注入队列 ⇒ 走提交路径」的
    /// 合法出口（该组合＝接线缺陷，须停驻——[Wave1 R15 重要-2]）。
    /// </summary>
    public static LocalWaitRegistrationOutcome NotParked { get; } = new();
}

/// <summary>
/// 槲寄生 · 任务中心——WorkflowRunner / Reconciler（R4.5，R4 分解 D7/D11/D12 + ASTRA 二轮处置）。
/// 单运行单驱动循环（串行状态转换）：所有触发（节点终态/动作队列/修订检查/定时唤醒）
/// 统一进入驱动循环边界处理，不并发推进。
/// ASTRA 二轮处置落点：
/// - B1 修订寻址：Store 一致性快照（文档+修订同源）；重载后按稳定出现身份从最后完成节点
///   重算后继，旧序列坐标绝不直接寻址新定义；链尾也是节点边界（追加节点会被执行）；
/// - B2 一提交一身份：幂等键按 runId+出现身份+attempt 确定性派生，提交事实不跨节点残留；
/// - B3 崩溃窗口：观察终态+节点结果+游标推进单次原子落盘；游标 null 由 TailReached 消歧；
///   ResumeAsync 显式恢复入口；聚合只信 NodeOutcomes（含恢复后历史结果重建）；
/// - B4 显式跳过：请求绑定出现身份，叶子建立空窗不丢动作，远端取消确认（超时=Unknown）后推进；
/// - B5 收尾：成功边界先落盘收尾意图（Completing+PendingCompletion 记录）再执行，收尾失败记 Failed；
/// - B9 轮次等待：新一轮边界统一执行（成功/过滤/失败续跑同路径），skipAcrossDays 公式化；
/// - 等待不占槽位：纯本地可取消延时，不持有执行锁、不提交等待作业；暂停可打断等待。
/// </summary>
public sealed class WorkflowRunner
{
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;
    private readonly IWorkflowExecutionBoundary _boundary;
    private readonly IWorkflowPrerequisiteAdapter _prerequisites;
    private readonly IWorkflowTerminalExecutor _terminal;
    private readonly WorkflowRunnerOptions _opt;
    /// <summary>
    /// **[批次 14／D1] 本地持久等待登记口（可选注入）。** 与 WorkflowRunner 的提交面**无耦合**：
    /// 只把「确定零发送的等待」落盘成可审计的等待项；`null`（未注入）⇒ 等待短路不生效（既有行为不变）。
    /// </summary>
    private readonly LocalWaitQueueStore? _localWaitQueue;
    private readonly WaitDecisionSource? _waitDecisionSource;

    /// <summary>
    /// **[批次 20／C4①] 等待登记前置引用来源（可选注入；生产未接线）。**
    /// D1 登记点载荷合同要求登记项携带持久化稳定前置引用；引用的**权威来源**属接线批事项（§24.111
    /// 「前置引用来源」残项），本注入点即其合同落点。`null` 或返回空白 ⇒ 登记点**拒绝登记**
    /// （不得登记结构性永不参选的等待项——那是 C4② 合同前存量的专属形态，不是新登记的合法产物）。
    /// </summary>
    private readonly Func<WorkflowRunRecord, WorkflowNodeOccurrence, string?>? _localWaitPrerequisiteReferenceProvider;

    /// <summary>
    /// **[批次 20／Wave1 R9-F1] 等待登记准入 Scope 来源（可选注入；生产未接线）。**
    /// 登记点要求**权威 scope**；其权威来源按运行来源类别分流——移交来源运行＝运行台账
    /// <c>AdmissionSourceScope</c>（受理时捕获、只比较不重写）；面板来源运行＝租约侧
    /// <c>FlowRegistration</c> 反查（<c>ResolveAdmissionParent</c>，宿主职责，Runner 不可达）⇒
    /// 由本注入点供给（接线批接宿主反查）。解析次序（[Wave1 R23 重要-1／R24 重要-1 同步]）：**台账字段规范非空 ⇒ 恒取台账字段**；provider 仅在台账字段缺省（面板来源运行）时取用；
    /// 否则回落运行台账字段；两者皆缺 ⇒ 登记点**拒绝登记**（空段身份与提交面不同空间，
    /// 结构性永不可重入——R7 重要-2 合同不变；「面板来源＝无权威 scope」是错误等式，R9-F1 更正）。
    /// </summary>
    private readonly Func<WorkflowRunRecord, string?>? _localWaitAdmissionScopeProvider;

    private readonly ConcurrentDictionary<string, RunControl> _controls = new(StringComparer.Ordinal);

    /// <summary>
    /// 等待结果的受控原因码（`AdmissionResultKind.WaitLocally` 的**唯一**呈现词）。适配层与持久化层共用，
    /// 避免多处手写字面量漂移出「未知/失败」词表。
    /// </summary>
    public const string LocalWaitReasonCode = "local_wait";

    /// <summary>
    /// **[批次 20／C3] 等待停驻的结果词**（NodeOutcomes.Result 与提交产物用的**唯一**停驻词）。
    /// 它**不是完成词**：RecomputeSuccessor 的「最后完成身份」锚**必须**排除它（停驻项零发送、
    /// 未完成；把它当锚会把未发送节点静默跳过——批次 14 明文禁止「作业静默丢步」）。
    /// </summary>
    public const string LocalWaitResultWord = "waitLocally";

    /// <summary>跳过请求（B4：绑定请求时的出现身份；身份漂移则丢弃，不误伤后续节点）。</summary>
    private sealed record SkipRequest(string NodeId, int Occurrence, int LoopIteration)
    {
        public bool Matches(WorkflowNodeOccurrence occ)
            => NodeId == occ.NodeId && Occurrence == occ.Occurrence && LoopIteration == occ.LoopIteration;
    }

    private sealed class RunControl
    {
        public required CancellationTokenSource RunCts { get; init; }
        public ConcurrentQueue<WorkflowRunAction> Actions { get; } = new();

        /// <summary>LeafCts/PendingSkip 互斥（B4：Cancel/Dispose 竞争消除）。</summary>
        public object Sync { get; } = new();
        public CancellationTokenSource? LeafCts;
        public SkipRequest? PendingSkip;
        public bool PauseRequested;
        public TaskCompletionSource PauseSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public WorkflowRunner(WorkflowStore workflows, RunStore runs, IWorkflowExecutionBoundary boundary,
        IWorkflowPrerequisiteAdapter prerequisites, IWorkflowTerminalExecutor terminal,
        WorkflowRunnerOptions? options = null, LocalWaitQueueStore? localWaitQueue = null,
        Func<WorkflowRunRecord, WorkflowNodeOccurrence, string?>? localWaitPrerequisiteReferenceProvider = null,
        Func<WorkflowRunRecord, string?>? localWaitAdmissionScopeProvider = null,
        WaitDecisionSource? waitDecisionSource = null)
    {
        _workflows = workflows;
        _runs = runs;
        _boundary = boundary;
        _prerequisites = prerequisites;
        _terminal = terminal;
        _opt = options ?? new WorkflowRunnerOptions();
        _localWaitQueue = localWaitQueue;
        _waitDecisionSource = waitDecisionSource;
        _localWaitPrerequisiteReferenceProvider = localWaitPrerequisiteReferenceProvider;
        _localWaitAdmissionScopeProvider = localWaitAdmissionScopeProvider;
    }

    /// <summary>运行控制登记实况（R4.8 一轮 I5：宿主动作结构化反馈用——Paused/终态运行无登记，动作不得无声吞）。</summary>
    public bool HasActiveControl(string runId) => _controls.ContainsKey(runId);

    /// <summary>登记显式动作（线程安全；驱动循环在下一边界消费，Stop 同时取消运行令牌）。</summary>
    public void RequestAction(string runId, WorkflowRunAction action)
    {
        if (!_controls.TryGetValue(runId, out var control)) return;
        control.Actions.Enqueue(action);
        switch (action)
        {
            case WorkflowRunAction.Stop:
                control.RunCts.Cancel();
                break;
            case WorkflowRunAction.Pause:
                control.PauseRequested = true;
                control.PauseSignal.TrySetResult();
                break;
            case WorkflowRunAction.SkipCurrent:
            {
                // B4：绑定请求时的当前游标身份；等待期间无执行中节点，不登记（等待可经 Stop 中断）
                var rec = _runs.Load(runId);
                if (rec?.State == WorkflowRunState.Waiting)
                {
                    Log(runId, "等待期间无执行中节点，跳过请求不登记。");
                    break;
                }
                control.PendingSkip = rec?.Cursor is { } cursor
                    ? new SkipRequest(cursor.NodeId, cursor.Occurrence, cursor.LoopIteration)
                    : new SkipRequest("", -1, -1); // 无游标：永不命中，仅留痕
                lock (control.Sync) control.LeafCts?.Cancel();
                break;
            }
        }
    }

    /// <summary>
    /// 启动流程运行（预检 → 建运行 → 驱动至终态/中断）。
    /// 预检失败抛 InvalidOperationException（响亮，不建运行）。
    /// </summary>
    public async Task<WorkflowRunRecord> StartAsync(string workflowId, CancellationToken ct = default)
    {
        var snapshot = _workflows.LoadSnapshot(workflowId); // 隔离文件在此响亮抛出；文档+修订同源（B1）
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        var run = _runs.CreateRun(workflowId, snapshot.Revision);
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(run.RunId, control))
            throw new InvalidOperationException("运行登记冲突：" + run.RunId);
        try
        {
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(run.RunId, out _);
            control.RunCts.Dispose();
        }
    }

    /// <summary>
    /// 移交受理前预检（R4.9 + ASTRA 二轮 B3：权威计划预检先于受理落盘——不可执行流程响亮拒绝、不消耗 IntentKey、不留运行记录）。
    /// 无执行副作用；与 StartExistingRunAsync/ResumeAsync 内的预检同口径（驱动入口仍各自复验，防御纵深）。
    /// </summary>
    public void PreflightStartable(WorkflowSnapshot snapshot)
    {
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported);
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));
    }
    /// <summary>
    /// 驱动既有 Planned 运行（R4.9 移交受理路径：受理点=宿主 CreateRun 落盘（含移交身份），本入口不再建运行——
    /// 与 StartAsync 的「预检→建运行→驱动」不同，这里是「驱动已受理运行」。预检失败/隔离文件响亮抛出
    /// （运行记录保留——受理不撤回，由宿主侧收敛标注，R4.9 §3 步骤 4）。
    /// armTriggerLaunch=true（armTrigger 移交）：在本入口实际加载的定义快照上复验可挂载前提（七轮 重要3）——
    /// 受理→驱动重载窗口内前提失效时不激活执行（受理事实保留，运行收敛 Interrupted 可处置，绝不提交）。
    /// </summary>
    public async Task<WorkflowRunRecord> StartExistingRunAsync(string runId, CancellationToken ct = default,
        bool armTriggerLaunch = false)
    {
        var run = _runs.Load(runId) ?? throw new FileNotFoundException("运行记录不存在：" + runId);
        if (run.State is not WorkflowRunState.Planned)
            throw new InvalidOperationException($"仅 Planned 新运行可经移交入口驱动（当前 {run.State}）。");

        var snapshot = _workflows.LoadSnapshot(run.WorkflowId); // 隔离响亮抛出；文档+修订同源（B1）
        if (armTriggerLaunch && !HasMountableTrigger(snapshot.Document))
        {
            // 七轮 重要3：arm 前提必须在驱动实际使用的定义快照上复验——受理（快照 A 有 trigger.time）→驱动重载
            // （快照 B 触发器已被移除）窗口内前提失效时不得顺势进入执行：受理事实保留、运行收敛 Interrupted
            // （可处置/可显式恢复），绝不做任何提交
            run.State = WorkflowRunState.Interrupted;
            run.Note = AppendNote(run.Note,
                "armTrigger 挂载前提失效（受理后流程已无 trigger.time 触发器），未激活执行（受理事实保留）。");
            _runs.Update(run);
            return run;
        }
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        run.WorkflowRevision = snapshot.Revision; // 与 Resume 同口径：起步对账到当前修订（受理与驱动同窗口，正常相等）
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(run.RunId, control))
            throw new InvalidOperationException("运行登记冲突：" + runId);
        try
        {
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(run.RunId, out _);
            control.RunCts.Dispose();
        }
    }
    /// <summary>
    /// 显式恢复运行（B3 恢复入口 + D7 生命周期触发的消费侧）。
    /// 仅接受 Interrupted/Paused；Unknown 拒绝自动恢复（结果不确定，需先按幂等键+job 查询对账）。
    /// 恢复点 = 游标身份在当前修订中重定位；已完成节点不重放；历史失败结果参与聚合。
    /// </summary>
    public async Task<WorkflowRunRecord> ResumeAsync(string runId, CancellationToken ct = default)
    {
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(runId, control))
        {
            control.RunCts.Dispose();
            throw new InvalidOperationException("运行登记冲突：" + runId);
        }
        try
        {
            // 先取得本 Runner 的运行控制，再读取与修改记录；控制冲突不得先写 Running/修订。
            var run = _runs.Load(runId) ?? throw new FileNotFoundException("运行记录不存在：" + runId);
            if (run.State == WorkflowRunState.Unknown)
                throw new InvalidOperationException("运行结果不确定（Unknown），需先按幂等键+job 查询对账，禁止自动恢复。");
            if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused
                or WorkflowRunState.LocalWaitParking))
                throw new InvalidOperationException($"仅 Interrupted/Paused/LocalWaitParking 可显式恢复（当前 {run.State}）。"
                    + "LocalWaitParking＝等待停驻（[批次 20／Wave3／C11=(a)]）：等待项就绪后显式重驱入口。");
            if (RunStore.HasUnresolvedExternalFact(run))
            {
                run.State = WorkflowRunState.Unknown;
                run.Note = AppendNote(run.Note, "显式恢复发现未决发送/收尾事实，标 Unknown，必须先对账，禁止重驱。");
                _runs.Update(run);
                throw new InvalidOperationException("运行记录含未决发送/收尾事实，已保留事实并标 Unknown；必须先对账，禁止自动恢复。");
            }

            var snapshot = _workflows.LoadSnapshot(run.WorkflowId);
            var plan = new WorkflowPlan(snapshot.Document);
            var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
                _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
            if (!preflight.Executable)
                throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

            if (run.LocalWaitDecision is { Kind: LocalWaitDecisionKind.Wait or LocalWaitDecisionKind.Hold } priorDecision
                && run.CurrentSubmission?.Intent == SubmitIntentState.LocalWaitDeferred)
            {
                var sameRevision = string.Equals(run.WorkflowRevision, snapshot.Revision, StringComparison.Ordinal);
                WorkflowNodeOccurrence? waitOccurrence = null;
                var sameCursor = false;
                if (run.Cursor is { } savedCursor
                    && plan.TryLocate(savedCursor.NodeId, savedCursor.Occurrence, savedCursor.LoopIteration, out var located))
                {
                    waitOccurrence = located;
                    sameCursor = true;
                }
                if (sameRevision && sameCursor)
                {
                    var currentOccurrence = waitOccurrence!;
                    var resumeRequest = CreateWaitDecisionRequest(run, currentOccurrence, run.Cursor!.Attempt);
                    var decision = DecideLocalWait(resumeRequest, currentOccurrence)
                        ?? new LocalWaitDecisionRecord
                        {
                            Kind = LocalWaitDecisionKind.ContinueAdmission,
                            Context = ContextFromRequest(resumeRequest),
                            Reason = "测试接缝恢复按显式请求重驱",
                        };
                    if (decision.Kind is LocalWaitDecisionKind.Wait or LocalWaitDecisionKind.Hold)
                    {
                        if (decision.Kind == LocalWaitDecisionKind.Wait)
                        {
                            var prepared = TryRegisterLocalWait(run, currentOccurrence, run.Cursor!.Attempt, decision);
                            if (prepared.Decision?.Binding is { } refreshedBinding)
                            {
                                if (priorDecision.Binding is { } priorBinding
                                    && !SameWaitBindingPayload(priorBinding, refreshedBinding))
                                {
                                    CancelPersistedLocalWait(priorBinding, "恢复复核发现等待绑定身份/载荷漂移");
                        run.LocalWaitDecision = SanitizeWaitDecision(decision with
                        {
                            Kind = LocalWaitDecisionKind.Hold,
                            Binding = null,
                            Reason = "恢复复核发现来源、候选或队列载荷已漂移；旧绑定已取消，不以新快照替换。",
                            NoSendConfirmed = true,
                        });
                                }
                                else
                                {
                                    // 已存在的绑定不可被当前快照替换（包含其来源、身份、scope 与登记载荷）。
                                    run.LocalWaitDecision = SanitizeWaitDecision(prepared.Decision with
                                        { Binding = priorDecision.Binding ?? refreshedBinding });
                                }
                            }
                            else
                            {
                                CancelPersistedLocalWait(priorDecision.Binding, "恢复复核无法重建可信等待绑定");
                                run.LocalWaitDecision = SanitizeWaitDecision(decision with
                                {
                                    Kind = LocalWaitDecisionKind.Hold,
                                    Binding = null,
                                    Reason = prepared.Reason,
                                    NoSendConfirmed = true,
                                });
                            }
                        }
                        else
                        {
                            CancelPersistedLocalWait(priorDecision.Binding, "等待复核转为 Hold");
                            run.LocalWaitDecision = SanitizeWaitDecision(decision with { Binding = null });
                        }
                        run.State = WorkflowRunState.LocalWaitParking;
                        run.Note = AppendNote(run.Note, "显式恢复复核仍需本地等待/保持，未启动驱动、未发送。"
                            + Sanitize(run.LocalWaitDecision.Reason));
                        _runs.Update(run);
                        PublishPersistedLocalWait(run);
                        return run;
                    }
                }

                // 继续准入或身份/修订漂移：先墓碑化旧队列项，再清除活动绑定；若写入失败则维持原停驻。
                CancelPersistedLocalWait(priorDecision.Binding, sameRevision && sameCursor
                    ? "显式恢复重新进入完整准入" : "流程修订或游标身份已漂移");
                run.LocalWaitDecision = null;
            }

            // BO-6/7: explicit resume is a rescue boundary even when the saved cursor still exists.
            // A prior revision stamp or a current cursor must not hide an earlier live parked
            // obligation; recompute the plan-order successor from completion + all park history.
            // A persisted tail flag with live parks stays fail-closed through final aggregation.
            if (!run.TailReached && run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord))
            {
                var resumeSuccessor = RecomputeSuccessor(run, plan);
                ApplyRelocation(run, resumeSuccessor);
                Log(run, "显式恢复复核当前计划中的停驻义务，按锚/停驻全序重算恢复点。");
            }

            run.WorkflowRevision = snapshot.Revision; // 恢复即对账到当前修订（节点边界语义）
            run.State = WorkflowRunState.Running;
            run.Note = AppendNote(run.Note, "显式恢复运行（游标身份重定位，不重放已完成节点）。");
            _runs.Update(run);
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(runId, out _);
            control.RunCts.Dispose();
        }
    }

    private async Task<WorkflowRunRecord> DriveAsync(WorkflowRunRecord run, WorkflowPlan plan, RunControl control)
    {
        var ct = control.RunCts.Token;
        var flowFailure = false;
        // BO-6: a resumed run with a persisted local-wait obligation filters already completed
        // stable occurrences while advancing from a rescue point. Keep this recovery behavior
        // scoped to parked runs; ordinary revision progression remains outside BO-8.
        var filterCompletedDuringParkRecovery = run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord);
        try
        {
            // 顶层触发器：入口等待（不占槽位；已消费则跳过——恢复不重等，B3）
            if (!run.TriggerConsumed)
            {
                await AwaitFlowTriggersAsync(run, plan, control, ct).ConfigureAwait(false);
                if (control.PauseRequested) return Pause(run);
                run.TriggerConsumed = true;
                run.State = WorkflowRunState.Running;
                _runs.Update(run);
            }

            var occurrence = Relocate(run, plan);
            if (occurrence is not null && run.Cursor is null)
            {
                // 首个待执行节点即落盘游标（B4：跳过动作绑定出现身份对首节点同样成立）
                ApplyRelocation(run, occurrence);
                _runs.Update(run);
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (control.PauseRequested) return Pause(run);

                // 边界①：显式动作 + 修订对账（新修订按稳定身份重算后继；链尾亦对账，B1）
                (plan, occurrence) = ProcessBoundaryActions(run, plan, occurrence, control);
                if (occurrence is null) break; // 链尾（TailReached 已落盘）

                // B3-③：提交终态已观察但结果未提交（仅遗留/手工记录可达；正常路径单次写已消除窗口）
                // ——按提交事实补记结果，绝不重跑该节点
                if (run.CurrentSubmission is { ObservedTerminal: { } observed } pendingSub
                    && pendingSub.NodeId == occurrence.NodeId
                    && pendingSub.Occurrence == occurrence.Occurrence
                    && pendingSub.LoopIteration == occurrence.LoopIteration)
                {
                    var mapped = MapTerminal(observed);
                    Log(run, $"提交 {pendingSub.Key} 终态已观察（{observed}）但结果未提交，按事实补记，不重跑。");
                    CommitOutcome(run, plan, occurrence, mapped.Result, mapped.Reason, rawTerminal: observed); // I2：ObservedTerminal 已是原始词
                    if (mapped.Result == "cancelled") throw new OperationCanceledException();
                    if (mapped.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan);
                    continue;
                }

                // B9：轮次起点等待统一在新一轮边界（成功/过滤跳过/失败续跑同路径；不占槽位）
                if (occurrence is { SequenceIndex: 0, LoopIteration: > 0 }
                    && occurrence.LoopIteration != run.LastScheduledRoundWait)
                {
                    if (!await AwaitLoopRoundStartAsync(run, plan, occurrence, control, ct).ConfigureAwait(false))
                    {
                        flowFailure = true;
                        break;
                    }
                    if (control.PauseRequested) return Pause(run);
                }

                // BO-6: a rescue point may precede completed anchors. Re-drive the parked
                // obligation, but never resubmit a stable occurrence already completed in this
                // run. One step per loop preserves definition-boundary and round-start handling.
                if (filterCompletedDuringParkRecovery && HasCompletedOutcome(run, occurrence))
                {
                    Log(run, $"停驻恢复推进过滤已完成出现 {occurrence.NodeId}#{occurrence.Occurrence}"
                             + $"（轮次 {occurrence.LoopIteration}），继续按计划全序前进。");
                    occurrence = plan.Next(occurrence);
                    ApplyRelocation(run, occurrence);
                    _runs.Update(run);
                    continue;
                }

                var node = plan.NodeAt(occurrence);
                var gate = plan.EvaluateNode(occurrence, _opt.Clock(), _boundary.SingleNativeSupported);
                if (gate.Action == NodeGateAction.Skip)
                {
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 过滤跳过：{gate.Reason}");
                    CommitOutcome(run, plan, occurrence, "skippedFilter", gate.Reason);
                    occurrence = Relocate(run, plan);
                    continue;
                }
                if (gate.Action == NodeGateAction.Reject)
                {
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 响亮拒绝：{gate.Reason}");
                    CommitOutcome(run, plan, occurrence, "rejected", gate.Reason);
                    if (!_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan);
                    continue;
                }

                // 前置策略（D8 + R4.6 结构化结果：Proceed/Failed/Rejected/Cancelled/Unknown；
                // 前置段已建立叶子令牌，SkipCurrent 可取消在飞前置并经确认链对账）
                if (await ExecutePrerequisitesAsync(run, node, occurrence, control, ct).ConfigureAwait(false) is { } prereqOutcome)
                {
                    if (prereqOutcome.Result is "unknown" or "cancelUnconfirmed")
                    {
                        // B3/四轮阻断 5：结果不确定——先置状态再 CommitOutcome（单次原子落盘，游标不推进；不触发收尾、禁止自动重跑）
                        run.State = WorkflowRunState.Unknown;
                        run.Note = AppendNote(run.Note, "前置动作结果不确定，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                        CommitOutcome(run, plan, occurrence, prereqOutcome.Result, prereqOutcome.Reason);
                        return run;
                    }
                    CommitOutcome(run, plan, occurrence, prereqOutcome.Result, prereqOutcome.Reason);
                    if (prereqOutcome.Result == "cancelled") throw new OperationCanceledException();
                    if (prereqOutcome.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan); // skippedUser/skippedFilter/失败续跑：推进
                    continue;
                }

                // 提交（意图先行 → 提交 → 终态；观察终态+结果+游标单次落盘，B2/B3）
                var outcome = await SubmitAndAwaitAsync(run, plan, node, occurrence, control).ConfigureAwait(false);
                if (outcome.Result is LocalWaitResultWord)
                {
                    // **[批次 14／D1] 本地持久等待＝确定零发送的停驻**：与 unknown/cancelUnconfirmed 同族——
                    // **游标不推进**（`ApplyRelocation(run, occurrence)` 留在当前出现）、**不终态化**运行、
                    // **不触发收尾**、**不标 Unknown**（等待不是「结果不确定」，标 Unknown 会错误要求按幂等键+job 对账，
                    // 而本笔从未进入发送面 ⇒ 无 job 可查 ⇒ 永久无法收敛）。
                    // **[批次 20／Wave3／C11=(a)] 停驻态显式化**：置 **LocalWaitParking**（D-E4=(a) 不复用
                    // Running/Waiting）——运行无活动驱动、等待项就绪后可显式重驱（ResumeAsync 接受本状态）；
                    // 重启恢复扫描将其收敛为 Interrupted（可恢复）——「无重驱句柄的永久 Running」形态消除
                    // （Wave1 R11 F-A/R25 处置链的闭环）。同代际再次重驱同一出现 ⇒ 再停驻 ⇒ 状态保持 LocalWaitParking。
                    // [批次 21／BO-1] 停驻原因留痕到运行级 Note（与 unknown 分支同族的可观测性义务）：
                    // 登记未完成／被拒的原文经 Sanitize 追加，运行记录盘上可查（夹具钉死「登记未完成＋异常」子串）。
                    run.State = WorkflowRunState.LocalWaitParking;
                    run.Note = AppendNote(run.Note, "本地等待登记停驻（零发送，游标不推进）：" + Sanitize(outcome.Reason));
                    if (run.CurrentSubmission is { } deferred
                        && deferred.Intent == SubmitIntentState.IntentRecorded
                        && !deferred.SendAttempted
                        && string.IsNullOrEmpty(deferred.JobId)
                        && string.IsNullOrEmpty(deferred.AcceptedSendIdentity)
                        && deferred.ObservedTerminal is null)
                        deferred.Intent = SubmitIntentState.LocalWaitDeferred;
                    CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, rawTerminal: null);
                    PublishPersistedLocalWait(run);
                    return run;
                }
                if (outcome.Result is "cancelUnconfirmed" or "unknown")
                {
                    // B4/四轮阻断 5：远端终态未确认——先置状态再 CommitOutcome（单次原子落盘，游标不推进）；
                    // rawTerminal=null → ObservedTerminal 保持 null（未观察到原始词），恢复扫描按在飞标 Unknown
                    run.State = WorkflowRunState.Unknown;
                    run.Note = AppendNote(run.Note, "远端终态未确认，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                    CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, rawTerminal: null);
                    return run;
                }
                CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, outcome.RawTerminal);
                if (outcome.Result == "cancelled") throw new OperationCanceledException(); // BGI 取消事实 → 流程取消（D12）
                if (outcome.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                occurrence = Relocate(run, plan);
            }

            // 流程边界：聚合判定只信 NodeOutcomes（B3：含恢复后的历史结果重建，失败不被成功覆盖）
            var unresolvedParkedOutcome = run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord
                && plan.TryLocate(o.NodeId, o.Occurrence, o.LoopIteration, out var parked)
                && !HasCompletedOutcome(run, parked));
            var hadBadOutcome = run.NodeOutcomes.Any(o =>
                o.Result is "failed" or "rejected" or "cancelled" or "cancelUnconfirmed" or "unknown"); // R4.6 B3
            if (hadBadOutcome || unresolvedParkedOutcome || flowFailure)
            {
                run.State = WorkflowRunState.Failed;
                run.Note = AppendNote(run.Note, flowFailure
                    ? "循环/触发时刻计算失败，聚合结果 Failed。"
                    : unresolvedParkedOutcome
                        ? "存在仍可定位且未完成的本地停驻义务，未按链尾成功处理；聚合结果 Failed。"
                        : "存在失败/拒绝节点，聚合结果 Failed（失败不被后续成功覆盖）。");
                _runs.Update(run);
                return run;
            }

            // B5/E3'：收尾意图先行落盘（Completing + PendingCompletion 记录），再执行收尾动作；
            // Planner 预检保证每流程至多一个收尾动作（E3'：多收尾无逐动作水位，响亮拒绝）；
            // executed 才清偿意图；rejected/异常 → Failed 保留意图；unknown/cancelled → Failed + 意图标 unknown 禁止补发。
            // D15 定案：skippedUser/skippedFilter 不算坏结果，不阻断成功边界收尾（用户显式跳过视为认可完成）。
            var terminalActions = plan.Document.Terminal;
            if (terminalActions.Count > 0)
            {
                var action = terminalActions[0]; // E3'：单动作（多收尾已被 Planner 预检响亮拒绝）
                run.State = WorkflowRunState.Completing;
                run.PendingCompletion = new PendingCompletionRecord
                {
                    ActionId = "$flow#0", // B1：收尾身份 nodeId=$flow、iteration=动作序号（0 起）
                    Kind = action.Kind,
                    Action = action.GetString("action"),
                    State = "pending",
                };
                _runs.Update(run);
                TerminalExecutionResult terminalResult;
                try
                {
                    terminalResult = await _terminal.ExecuteAsync(action, run, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; } // B8：意图事实交外层按 pending/submitted 纪律处置
                catch (Exception ex)
                {
                    run.State = WorkflowRunState.Failed;
                    run.Note = AppendNote(run.Note,
                        $"收尾动作 {action.Kind} 执行失败：{Sanitize(ex.Message)}（流程主体成功，收尾失败不记成功；待执行收尾保留待人工处置）。");
                    _runs.Update(run);
                    return run;
                }
                switch (terminalResult.State)
                {
                    case "executed":
                        run.PendingCompletion = null; // 已证实执行——意图清偿
                        break;
                    case "rejected":
                        run.State = WorkflowRunState.Failed;
                        run.Note = AppendNote(run.Note,
                            $"收尾动作被拒绝：{Sanitize(terminalResult.Reason)}（动作未执行；意图保留待人工处置）。");
                        _runs.Update(run);
                        return run;
                    default: // unknown / cancelled：结果不可考——事实持久保留，禁止自动补发
                        run.PendingCompletion.State = "unknown";
                        run.PendingCompletion.JobId ??= terminalResult.JobId;
                        run.State = WorkflowRunState.Failed;
                        run.Note = AppendNote(run.Note,
                            $"收尾动作结果不确定（{terminalResult.State}）：{Sanitize(terminalResult.Reason)}（事实持久保留，禁止自动补发，需人工对账）。");
                        _runs.Update(run);
                        return run;
                }
            }

            run.State = WorkflowRunState.Succeeded;
            _runs.Update(run);
            return run;
        }
        catch (OperationCanceledException)
        {
            // R4.8 一轮 B3：Stop 对在飞提交 best-effort 远端取消（独立短令牌 ≤5s；运行令牌已取消不可复用）；
            // 取消未确认不猜远端已停——在飞事实（ObservedTerminal 空）原样保留，Note 标注需对账
            var inflightJob = run.CurrentSubmission is { InFlight: true, JobId: { } j } ? j : null;
            if (inflightJob is not null)
            {
                try
                {
                    using var cancelBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _boundary.RequestCancelAsync(inflightJob, cancelBudget.Token).ConfigureAwait(false);
                }
                catch { /* best-effort：取消请求结果不阻断本地停止 */ }
            }
            run.State = WorkflowRunState.Cancelled;
            run.Note = AppendNote(run.Note, inflightJob is not null
                ? "流程被取消（手动停止）；在飞作业已请求远端取消（未确认，事实保留，需人工对账）；不触发收尾。"
                : "流程被取消（手动停止/BGI 取消事实）；不触发收尾。");
            // D10/B8：取消不清算为可执行收尾——pending（未提交）清除意图；submitted（已受理未证实）保留事实标 unknown，禁止补发
            if (run.PendingCompletion is { } pendingCompletion)
            {
                if (pendingCompletion.State == "pending") run.PendingCompletion = null;
                else pendingCompletion.State = "unknown";
            }
            _runs.Update(run);
            return run;
        }
    }

    /// <summary>提交 + 终态等待（意图先行落盘；SkipCurrent 经叶子令牌取消 + 远端确认，B4）。
    /// RawTerminal = 边界观察到的原始线协议词（I2；未确认路径为 null）。</summary>
    private async Task<(string Result, string? Reason, string? RawTerminal)> SubmitAndAwaitAsync(
        WorkflowRunRecord run, WorkflowPlan plan, WorkflowNode node, WorkflowNodeOccurrence occurrence, RunControl control)
    {
        var ct = control.RunCts.Token;
        const int attempt = 1; // 有界重试机制挂账 R4.6+（键结构已含 attempt，身份合同就绪）
        var submission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, occurrence.NodeId, occurrence.Occurrence,
                occurrence.LoopIteration, attempt),
            NodeId = occurrence.NodeId,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = attempt,
        };
        // 预检 Hold 只适用于没有未决外部事实的当前提交；不能用 LocalWaitDeferred 覆盖旧受理/发送事实。
        if (RunStore.HasUnresolvedExternalFact(run))
            return ("unknown", "提交前发现未决发送/收尾事实，保留原提交记录并待对账。", null);
        // 同步类型化裁定必须先于 RecordIntent。ContinueAdmission 不是许可：后续异步边界仍完整准入。
        var waitRequest = CreateWaitDecisionRequest(run, occurrence, attempt);
        var waitDecision = DecideLocalWait(waitRequest, occurrence);
        if (waitDecision is { Kind: not LocalWaitDecisionKind.ContinueAdmission })
        {
            var prepared = waitDecision.Kind == LocalWaitDecisionKind.Wait
                ? TryRegisterLocalWait(run, occurrence, attempt, waitDecision)
                : new LocalWaitRegistrationOutcome
                {
                    Park = true,
                    Reason = waitDecision.Reason,
                    Decision = waitDecision with { Binding = null },
                };
            run.LocalWaitDecision = SanitizeWaitDecision(prepared.Decision ?? waitDecision with
            {
                Kind = LocalWaitDecisionKind.Hold,
                Binding = null,
                Reason = string.IsNullOrWhiteSpace(prepared.Reason) ? "等待登记未完成" : prepared.Reason,
                NoSendConfirmed = true,
            });
            submission.Intent = SubmitIntentState.LocalWaitDeferred;
            run.CurrentSubmission = submission;
            return (LocalWaitResultWord, prepared.Reason, null);
        }

        _runs.RecordIntent(run, submission); // 提交意图先行（B2/B3：崩溃后按意图对账，不重跑）

        // B6/E4' 定案：任务中心提交固定 suppress=true（与流程是否声明 terminal 无关；原生手动入口缺省 false 不变）
        var submit = await _boundary.SubmitAsync(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), ct)
            .ConfigureAwait(false);
        if (submit.Kind is BoundarySubmitKind.Wait or BoundarySubmitKind.Hold)
        {
            if (submission.Intent != SubmitIntentState.IntentRecorded
                || submission.SendAttempted
                || !string.IsNullOrEmpty(submission.JobId)
                || !string.IsNullOrEmpty(submission.AcceptedSendIdentity)
                || submission.ObservedTerminal is not null)
                return ("unknown", "等待裁定与提交发送事实冲突，保留事实并待对账。", null);

            var boundaryWaitRequest = CreateWaitDecisionRequest(run, occurrence, attempt);
            var boundaryHold = submit.Kind == BoundarySubmitKind.Hold
                || submit.WaitDecision?.Kind == LocalWaitDecisionKind.Hold;
            var boundaryDecision = boundaryHold
                ? new LocalWaitDecisionRecord
                {
                    Kind = LocalWaitDecisionKind.Hold,
                    Context = ContextFromRequest(boundaryWaitRequest),
                    Reason = !string.IsNullOrWhiteSpace(submit.RejectReason)
                        ? submit.RejectReason!
                        : submit.WaitDecision?.Reason ?? "提交边界要求保持，未创建等待队列项",
                    NoSendConfirmed = true,
                }
                : DecideLocalWait(boundaryWaitRequest, occurrence)
                ?? new LocalWaitDecisionRecord
                {
                    Kind = LocalWaitDecisionKind.Hold,
                    Context = ContextFromRequest(boundaryWaitRequest),
                    Reason = "边界返回等待但缺少宿主等待判定来源",
                    NoSendConfirmed = true,
                };
            if (boundaryDecision.Kind == LocalWaitDecisionKind.ContinueAdmission)
                boundaryDecision = boundaryDecision with
                {
                    Kind = LocalWaitDecisionKind.Hold,
                    Binding = null,
                    Reason = "边界返回等待但同步快照未能确认等待条件",
                    NoSendConfirmed = true,
                };
            var settled = boundaryDecision.Kind == LocalWaitDecisionKind.Wait
                ? TryRegisterLocalWait(run, occurrence, attempt, boundaryDecision)
                : new LocalWaitRegistrationOutcome { Park = true, Reason = boundaryDecision.Reason, Decision = boundaryDecision };
            run.LocalWaitDecision = SanitizeWaitDecision(settled.Decision ?? boundaryDecision with
            {
                Kind = LocalWaitDecisionKind.Hold,
                Binding = null,
                Reason = string.IsNullOrWhiteSpace(settled.Reason) ? boundaryDecision.Reason : settled.Reason,
                NoSendConfirmed = true,
            });
            submission.Intent = SubmitIntentState.LocalWaitDeferred;
            return (LocalWaitResultWord, settled.Reason, null);
        }
        if (submit.Uncertain)
        {
            // R4.8 一轮 B1：受理与否不可考——Intent 保持 Submitted（发送已尝试事实），走 Unknown 停驻（调用点）；
            // 不按拒绝推进、不改游标；ObservedTerminal 保持空 = 在飞事实保留，恢复扫描按在飞标 Unknown
            // [R5.2 G6 会诊] **不得降级已确认的受理事实**：边界可能已把本轮落盘为 Accepted（含 jobId），
            // 受理事实是接管/恢复依据，只允许增强不允许回退——否则后续异常会被当作「未受理」处理。
            submission.Intent = submission.Intent == SubmitIntentState.Accepted
                ? SubmitIntentState.Accepted
                : SubmitIntentState.Submitted;
            return ("unknown", "提交结果不可考：" + Sanitize(submit.RejectReason), null);
        }
        if (!submit.Accepted)
        {
            // 同理：已 Accepted 的提交不得因本层拿到 Rejected 而回退（受理事实优先，冲突留待对账）。
            // 且此时**不得按「确定拒绝」推进游标**（会记拒绝结果并可能继续下一节点）——冲突一律按 Unknown 停驻。
            if (submission.Intent == SubmitIntentState.Accepted)
                return ("unknown", "提交被拒但已存在受理事实（冲突，保守 Unknown 留待对账）："
                                   + Sanitize(submit.RejectReason), null);
            submission.Intent = SubmitIntentState.Rejected;
            return ("rejected", "提交被拒绝：" + Sanitize(submit.RejectReason), null);
        }
        submission.Intent = SubmitIntentState.Accepted;
        submission.JobId = submit.JobId;
        _runs.Update(run); // 受理事实落盘（提交仍在飞：ObservedTerminal 未填写）

        CancellationTokenSource leaf;
        lock (control.Sync)
        {
            leaf = CancellationTokenSource.CreateLinkedTokenSource(ct);
            control.LeafCts = leaf;
            if (control.PendingSkip is { } skip)
            {
                control.PendingSkip = null;
                if (skip.Matches(occurrence)) leaf.Cancel(); // B4：空窗到达的跳过在叶子建立时生效
                else Log(run, "过期跳过动作已丢弃（出现身份漂移，不误伤后续节点）。");
            }
        }
        try
        {
            BoundaryTerminalResult terminal;
            try
            {
                terminal = await _boundary.AwaitTerminalAsync(submit.JobId!, leaf.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 叶子被取消（显式跳过）：远端取消确认后再推进（B4 确认阶段；Stop 会先取消运行令牌）
                return await ConfirmSkipAsync(run, submission, ct).ConfigureAwait(false);
            }
            if (terminal.Uncertain)
            {
                // R4.8 一轮 B1：终态查询不可考——Unknown 停驻（调用点），rawTerminal=null（ObservedTerminal 保持空）
                return ("unknown", "终态查询不可考：" + Sanitize(terminal.Reason), null);
            }
            var mapped = MapTerminal(terminal.Terminal!);
            return (mapped.Result, mapped.Reason ?? terminal.Reason, terminal.Terminal); // I2：原始词随结果返回，ObservedTerminal 只存它
        }
        finally
        {
            lock (control.Sync) control.LeafCts = null;
            leaf.Dispose();
        }
    }

    /// <summary>显式跳过确认（B4：请求跳过→取消中→已确认/未知；未确认不得当成功推进）。
    /// 第三元 = 确认阶段观察到的原始线协议词（I2；超时未观察到 = null）。</summary>
    private async Task<(string Result, string? Reason, string? RawTerminal)> ConfirmSkipAsync(
        WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
    {
        // R4.8 一轮 B3：取消与观察分离——先请求远端取消一次（独立有界令牌 ≤5s，best-effort 忽略结果），
        // 再纯观察确认；AwaitTerminalAsync 不再承担取消副作用（确认超时不重发取消，15s 预算即实际退出上界）
        try
        {
            using var cancelBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancelBudget.CancelAfter(TimeSpan.FromSeconds(5));
            await _boundary.RequestCancelAsync(submission.JobId!, cancelBudget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best-effort：取消请求失败不阻断确认观察，终态以观察为准 */ }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.SkipConfirmTimeout);
        try
        {
            var terminal = await _boundary.AwaitTerminalAsync(submission.JobId!, timeout.Token).ConfigureAwait(false);
            if (terminal.Uncertain)
                return ("cancelUnconfirmed", $"跳过请求后远端终态不可考（{Sanitize(terminal.Reason)}）", null);
            return terminal.Terminal switch
            {
                "cancelled" => ("skippedUser", "显式跳过当前节点（远端取消已确认）", terminal.Terminal),
                // I2：显式跳过意图与远端正常跳过竞态——如实记 skippedFilter（不计 skippedUser；两者均不阻断收尾，D15）
                "skipped" => ("skippedFilter", "跳过请求到达时远端已正常跳过（来源保留，不计入显式跳过）", terminal.Terminal),
                "succeeded" => ("succeeded", "跳过请求到达时节点已完成（留痕，不算跳过）", terminal.Terminal),
                var t => ("failed", $"跳过请求后观察到意外终态 {Sanitize(t)}", terminal.Terminal),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("cancelUnconfirmed", $"跳过请求后 {_opt.SkipConfirmTimeout.TotalSeconds:0}s 内远端终态未确认", null);
        }
    }

    /// <summary>
    /// 边界终态词汇 → 节点结果（R4.6 I2：原始线协议词与归一化业务词分开识别——ObservedTerminal 只存原始词，
    /// 恢复回流（B3-③）经本映射不会把 skippedUser/skippedFilter 误判 failed；未知词汇按 failed，保守不猜成功）。
    /// </summary>
    private static (string Result, string? Reason) MapTerminal(string terminal)
        => terminal switch
        {
            "succeeded" => ("succeeded", null),
            "cancelled" => ("cancelled", "BGI 侧取消事实"),
            "skipped" => ("skippedFilter", "BGI 侧正常跳过（远端词表 skipped；D15 不阻断收尾）"),
            // 归一化业务词恒等映射（仅恢复回流路径可达）
            "skippedUser" => ("skippedUser", null),
            "skippedFilter" => ("skippedFilter", null),
            "failed" => ("failed", null),
            "rejected" => ("rejected", null),
            var t => ("failed", $"作业终态 {t}"),
        };

    /// <summary>
    /// 前置策略执行（D8 + R4.6 E2-8'/B2/B3）：逐策略实例意图先行落盘（PrerequisiteActionRecord，发送前）；
    /// 同键成功事实跳过（崩溃恢复不重发）；在飞/未知记录先对账（查不到不盲目重发）；
    /// 前置段建立叶子令牌（SkipCurrent 绑定出现身份取消在飞前置，远端取消确认链同 B4）。
    /// 返回 null = 全部放行；否则 (结果词, 原因) 由调用点按结构化结果处置。
    /// </summary>
    private async Task<(string Result, string? Reason)?> ExecutePrerequisitesAsync(WorkflowRunRecord run, WorkflowNode node,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        if (node.Strategies.All(s => s.Kind == "condition.weekdays")) return null;

        CancellationTokenSource leaf;
        lock (control.Sync)
        {
            leaf = CancellationTokenSource.CreateLinkedTokenSource(ct);
            control.LeafCts = leaf;
            if (control.PendingSkip is { } skip)
            {
                control.PendingSkip = null;
                if (skip.Matches(occurrence)) leaf.Cancel(); // 空窗到达的跳过在叶子建立时生效
                else Log(run, "过期跳过动作已丢弃（出现身份漂移，不误伤后续节点）。");
            }
        }
        try
        {
            for (var i = 0; i < node.Strategies.Count; i++)
            {
                var strategy = node.Strategies[i];
                if (strategy.Kind == "condition.weekdays") continue; // 闸门已评估

                // E2-8' 身份来源：redeemCode 缺 uid 时注入同节点 prerequisite.account 的 uid（均无则适配器响亮失败；Planner 预检已拦截）
                var effective = strategy;
                if (strategy.Kind == "prerequisite.redeemCode" && string.IsNullOrWhiteSpace(strategy.GetString("uid")))
                {
                    var siblingUid = node.Strategies.FirstOrDefault(s => s.Kind == "prerequisite.account")?.GetString("uid");
                    if (!string.IsNullOrWhiteSpace(siblingUid))
                    {
                        var merged = strategy.Params is not null
                            ? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(strategy.Params)
                            : new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>();
                        merged["uid"] = System.Text.Json.JsonSerializer.SerializeToElement(siblingUid);
                        effective = new WorkflowStrategy { Kind = strategy.Kind, Params = merged };
                    }
                }
                var accountKey = RunStore.DeriveAccountKey(effective.GetString("uid"));

                // 四轮阻断 3：完整动作身份比对（出现身份 + 策略索引 + 操作类型 + 账号标识哈希）
                var record = run.PrerequisiteActions.FirstOrDefault(r =>
                    r.Matches(occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration, 1, i,
                        effective.Kind, accountKey));
                if (record is { State: PrerequisiteActionState.Succeeded })
                    continue; // 同键成功事实（游戏态事实跨纪元有效：切号/兑换效果不随 BGI 重启消失；同 attempt 恢复不重发）
                if (record is { State: PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled })
                {
                    // 四轮阻断 3/I4：既有终态事实不自动重试（新执行尝试 R4.6 不开放；attempt 恒 1，需人工处置）
                    return (record.State == PrerequisiteActionState.Cancelled ? "cancelled" : "failed",
                        $"前置策略 {effective.Kind} 存在既有{record.State}终态事实（不自动重试，需人工处置）：{record.Reason}");
                }
                if (record is { State: PrerequisiteActionState.Intent or PrerequisiteActionState.Submitted or PrerequisiteActionState.Unknown })
                {
                    // 恢复对账：先查远端权威终态，查不到不盲目重发（B2）；叶子令牌可中断对账（四轮阻断 1）
                    PrerequisiteResult reconciled;
                    try
                    {
                        reconciled = await _prerequisites.ReconcileAsync(record, leaf.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 对账期叶子取消 = 显式跳过：同走远端取消确认链
                        var confirmed0 = await _prerequisites.ConfirmCancellationAsync(record, ct).ConfigureAwait(false);
                        record.State = confirmed0.Status == PrerequisiteStatus.Cancelled
                            ? PrerequisiteActionState.Cancelled : PrerequisiteActionState.Unknown;
                        record.Reason = Sanitize(confirmed0.Reason);
                        _runs.Update(run);
                        return confirmed0.Status == PrerequisiteStatus.Cancelled
                            ? ("skippedUser", "前置对账期显式跳过（远端取消已确认）")
                            : ("cancelUnconfirmed", $"前置对账期跳过后远端终态未确认：{Sanitize(confirmed0.Reason)}");
                    }
                    record.State = reconciled.Status switch
                    {
                        PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                        PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                        PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                        _ => PrerequisiteActionState.Failed,
                    };
                    record.Reason = Sanitize(reconciled.Reason);
                    _runs.Update(run);
                    if (record.State == PrerequisiteActionState.Succeeded) continue;
                    if (record.State == PrerequisiteActionState.Unknown)
                        return ("unknown", $"前置策略 {effective.Kind} 结果不确定（对账未决）：{Sanitize(reconciled.Reason)}");
                    return (record.State == PrerequisiteActionState.Cancelled ? "cancelled" : "failed",
                        $"前置策略 {effective.Kind}（对账终态）：{Sanitize(reconciled.Reason)}");
                }

                // 意图先行落盘（发送前；E2-8'）
                record = new PrerequisiteActionRecord
                {
                    NodeId = occurrence.NodeId,
                    Occurrence = occurrence.Occurrence,
                    LoopIteration = occurrence.LoopIteration,
                    Attempt = 1,
                    StrategyIndex = i,
                    Kind = effective.Kind,
                    AccountKey = accountKey,
                    State = PrerequisiteActionState.Intent,
                    RecordedAt = _opt.Clock(),
                };
                run.PrerequisiteActions.Add(record);
                _runs.Update(run);

                PrerequisiteResult result;
                try
                {
                    result = await _prerequisites.ExecuteAsync(effective, run, occurrence, leaf.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 叶子取消 = 显式跳过：远端取消确认链（确认超时 = Unknown，不猜成功）
                    var confirmed = await _prerequisites.ConfirmCancellationAsync(record, ct).ConfigureAwait(false);
                    record.State = confirmed.Status == PrerequisiteStatus.Cancelled
                        ? PrerequisiteActionState.Cancelled : PrerequisiteActionState.Unknown;
                    record.Reason = Sanitize(confirmed.Reason);
                    _runs.Update(run);
                    return confirmed.Status == PrerequisiteStatus.Cancelled
                        ? ("skippedUser", "前置期显式跳过（远端取消已确认）")
                        : ("cancelUnconfirmed", $"前置期跳过后远端终态未确认：{Sanitize(confirmed.Reason)}");
                }

                // 终态与结果同写（引擎事务）
                record.State = result.Status switch
                {
                    PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                    PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                    PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                    _ => PrerequisiteActionState.Failed,
                };
                record.Reason = Sanitize(result.Reason);
                record.JobId ??= result.JobId;
                _runs.Update(run);

                switch (result.Status)
                {
                    case PrerequisiteStatus.Proceed: continue;
                    case PrerequisiteStatus.Rejected:
                        return ("rejected", $"前置策略 {effective.Kind} 被拒绝：{Sanitize(result.Reason)}");
                    case PrerequisiteStatus.Cancelled:
                        return ("cancelled", $"前置策略 {effective.Kind} 被取消：{Sanitize(result.Reason)}");
                    case PrerequisiteStatus.Unknown:
                        return ("unknown", $"前置策略 {effective.Kind} 结果不确定：{Sanitize(result.Reason)}");
                    default:
                        return ("failed", $"前置策略 {effective.Kind} 未通过：{Sanitize(result.Reason)}");
                }
            }
            return null;
        }
        finally
        {
            lock (control.Sync) control.LeafCts = null;
            leaf.Dispose();
        }
    }

    /// <summary>可挂载触发器判定（armTrigger 语义，宿主受理预验与驱动起步复验共用同一定义——七轮 重要3 单一事实源）。
    /// 四轮 重要5 收窄：**仅 trigger.time 入口等待才算可挂载**——结构性循环首轮立即执行不算（AwaitLoopRoundStartAsync
    /// 只在 LoopIteration&gt;0 生效），若承认 loop 可挂载，arm 会绕过 start 应有的混用/快照守卫却立即提交。</summary>
    internal static bool HasMountableTrigger(WorkflowDocument doc)
        => doc.Triggers.Any(t => t.Kind == "trigger.time");

    /// <summary>I3：持久化备注脱敏——长数字串（UID 形态）打码；受控原因码+脱敏摘要，不存原始敏感面。</summary>
    internal static string Sanitize(string? text)
        => string.IsNullOrEmpty(text) ? "" : System.Text.RegularExpressions.Regex.Replace(text, "\\d{5,}", "***");

    internal static LocalWaitDecisionRecord SanitizeWaitDecision(LocalWaitDecisionRecord decision)
        => decision with { Reason = Sanitize(decision.Reason) };

    /// <summary>顶层触发器等待（多触发器取最近；未知触发器响亮失败；暂停可打断）。</summary>
    private async Task AwaitFlowTriggersAsync(WorkflowRunRecord run, WorkflowPlan plan, RunControl control, CancellationToken ct)
    {
        if (plan.Document.Triggers.Count == 0) return;
        DateTimeOffset? earliest = null;
        foreach (var trigger in plan.Document.Triggers)
        {
            var next = WorkflowTriggerSchedule.NextFire(trigger, _opt.Clock(), out var reason)
                ?? throw new InvalidOperationException("触发器不可用：" + reason);
            earliest = earliest is null || next < earliest ? next : earliest;
        }
        run.State = WorkflowRunState.Waiting;
        _runs.Update(run);
        await WaitAsync(run, "trigger.time", earliest!.Value, control, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 轮次起点等待（B9：新一轮边界统一入口；skipAcrossDays 公式化，见 WorkflowLoopSchedule）。
    /// 返回 false = 时刻计算失败（流程级失败）。暂停打断时不记 LastScheduledRoundWait（恢复重排本轮）。
    /// </summary>
    private async Task<bool> AwaitLoopRoundStartAsync(WorkflowRunRecord run, WorkflowPlan plan,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        var loop = plan.Document.Loop;
        if (loop is null) return true; // LoopIteration>0 蕴含循环定义；防御性放行
        var next = WorkflowLoopSchedule.NextRoundStart(loop, _opt.Clock(), out var reason);
        if (next is null)
        {
            Log(run, "循环时刻计算失败：" + reason);
            return false;
        }
        if (next.Value > _opt.Clock())
        {
            await WaitAsync(run, "loop.scheduled", next.Value, control, ct).ConfigureAwait(false);
            if (control.PauseRequested) return true;
        }
        run.LastScheduledRoundWait = occurrence.LoopIteration;
        _runs.Update(run);
        return true;
    }

    /// <summary>等待（不占槽位：纯本地可取消延时 + RunStore 等待状态持久化；暂停打断保留等待记录）。</summary>
    private async Task WaitAsync(WorkflowRunRecord run, string kind, DateTimeOffset until, RunControl control, CancellationToken ct)
    {
        run.State = WorkflowRunState.Waiting;
        run.Wait = new WaitStateRecord
        {
            Kind = kind,
            NextTriggerAt = until,
            TriggerOccurrenceId = $"{kind}:{until:yyyyMMddHHmm}",
        };
        _runs.Update(run);
        Log(run, $"进入等待（{kind}）至 {until:yyyy-MM-dd HH:mm}（不持有执行锁、不提交等待作业）");
        var delay = until - _opt.Clock();
        if (delay > TimeSpan.Zero)
        {
            // R4.8 一轮 I5：延时走独立链接令牌——暂停打断即取消遗留 delayTask（手动时钟夹具不悬挂、计时器不泄漏）
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = _opt.DelayAsync(delay, delayCts.Token);
            if (await Task.WhenAny(delayTask, control.PauseSignal.Task).ConfigureAwait(false) != delayTask)
            {
                delayCts.Cancel();
                try { await delayTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* 暂停打断的主动取消，按暂停语义返回 */ }
                return; // 暂停打断：Wait 记录保留，恢复后按 NextTriggerAt 重排剩余
            }
            await delayTask.ConfigureAwait(false); // 传播 Stop 取消
        }
        run.Wait = null;
        run.State = WorkflowRunState.Running;
        _runs.Update(run);
    }

    /// <summary>边界动作处理：修订对账（默认节点边界生效）+ 显式动作消费。</summary>
    private (WorkflowPlan Plan, WorkflowNodeOccurrence? Occurrence) ProcessBoundaryActions(
        WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence? occurrence, RunControl control)
    {
        var reloadRequested = false;
        while (control.Actions.TryDequeue(out var action))
        {
            switch (action)
            {
                case WorkflowRunAction.ReloadDefinition:
                    reloadRequested = true;
                    break;
                case WorkflowRunAction.SkipCurrent:
                    Log(run, "显式跳过请求已登记（绑定请求时出现身份；叶子建立即生效）。");
                    break;
                // Stop/Pause 经运行令牌/暂停标志生效，不在此消费
            }
        }

        var snapshot = _workflows.LoadSnapshot(run.WorkflowId); // 文档+修订同源（B1）
        if (!reloadRequested && snapshot.Revision == run.WorkflowRevision) return (plan, occurrence);

        var newPlan = new WorkflowPlan(snapshot.Document);
        var preflight = newPlan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1：重载入口同接能力协商预检
        if (!preflight.Executable)
        {
            Log(run, "流程新修订预检未通过，沿用旧定义继续：" + string.Join("；", preflight.BlockingReasons));
            return (plan, occurrence);
        }

        // 新修订节点边界生效：从最后完成身份在新定义中重算后继（B1：插入/删除/重排不错位；
        // occurrence 为 null 时即链尾对账——新修订追加的节点会被执行）
        var relocated = RecomputeSuccessor(run, newPlan);
        run.WorkflowRevision = snapshot.Revision;
        ApplyRelocation(run, relocated);
        _runs.Update(run);
        Log(run, $"流程定义已重载（修订 {snapshot.Revision[..Math.Min(8, snapshot.Revision.Length)]}…），按稳定身份重算后继，节点边界生效。");
        return (newPlan, relocated);
    }

    /// <summary>
    /// 按稳定出现身份重算后继（B1：修订插入/删除/重排不错位）。
    /// **[批次 20／Wave1 R5 重要-2]** 锚＝最后一条**完成**结果（排除 <see cref="LocalWaitResultWord"/>
    /// 停驻标记——它零发送、未完成；**不得**按停驻节点算 Next 或按链尾放行）。
    /// **裁决次序（与实现逐字对齐，R15 建议-4）**：①完成锚（含回溯）算出候选；②候选为 null 或
    /// 存在可定位停驻出现时，**停驻义务优先**（不变量②：含前插保全）；③无停驻义务时取候选；
    /// ④完成锚全不可定位且无停驻 ⇒ 链尾。**「无完成节点 ⇒ 取链首」仅在无停驻义务时成立。**
    /// unknown/cancelUnconfirmed 同为非完成词，但其锚污染被 Unknown 禁恢复态挡住（恢复前必先对账并追加
    /// 真实终态词），维持既有口径不动（本批不扩改既有恢复语义）。
    /// **两条不变量（本批会诊逐轮收敛所得）**：
    /// ①**不重跑已完成出现**——返回点本身先过滤完成结果（R12 重要-1）；BO-6 为有停驻历史的恢复推进
    /// 增加同身份完成过滤。普通修订推进的完整过滤仍归 BO-8，本批不扩展该范围；
    /// ②**不吞停驻出现**——引擎自己立下的重驱义务（<see cref="LocalWaitResultWord"/>）：任何返回链尾
    ///   （null/TailReached）的路径都必须先复核「计划中是否仍有可定位的停驻出现」，有则按它继续
    ///   （R14 F2）；停驻出现之前若有**从未执行**的出现，则按更早者继续（R12 建议-1／R14 F1，
    ///   且探针必须**与停驻同轮次**起步，不得锁死在第 0 轮）。
    /// </summary>
    internal WorkflowNodeOccurrence? RecomputeSuccessor(WorkflowRunRecord run, WorkflowPlan plan)
    {
        var completionOutcomes = run.NodeOutcomes.Where(o => o.Result != LocalWaitResultWord).ToList();
        var parkedOutcomes = run.NodeOutcomes.Where(o => o.Result == LocalWaitResultWord).ToList();

        // 锚回溯：最后完成节点在新修订中已删除时，回溯更早的仍可定位完成节点（R11 F-C）。
        for (var i = completionOutcomes.Count - 1; i >= 0; i--)
        {
            var anchor = completionOutcomes[i];
            if (!plan.TryLocate(anchor.NodeId, anchor.Occurrence, anchor.LoopIteration, out var anchorOcc)) continue;
            if (i != completionOutcomes.Count - 1)
            {
                Log(run, $"最后完成身份 {completionOutcomes[^1].NodeId}#{completionOutcomes[^1].Occurrence} "
                         + $"在新修订中已消失，回溯到更早可定位完成身份 {anchor.NodeId}#{anchor.Occurrence} 重算后继。");
            }
            var candidate = plan.Next(anchorOcc);
            while (candidate is not null && HasCompletedOutcome(run, candidate))
                candidate = plan.Next(candidate); // 不变量①：跳过已完成出现
            // 锚可定位路径的返回可能落在**链尾**（candidate == null），也可能落在「锚之后仍有未完成工作」
            // 的节点上——两者都不得吞掉停驻义务（不变量②）：
            // ·链尾（R14 F2）：修订把停驻点重排到锚之前 ⇒ 纯向后走至 null ⇒ 假成功＋零发送节点被吞；
            // ·非链尾（R15 重要-1）：修订把停驻点重排到锚之前**且锚之后还有未执行节点** ⇒ 返回那个
            //   未执行节点、停驻点再也不被重驱 ⇒ 运行最终仍 Succeeded（同一可观察后果、另一条分支）。
            // 故此处**无条件**复核停驻义务：有可定位停驻出现 ⇒ 以停驻义务为准（含其前插保全）；
            // 无停驻 ⇒ 才用纯锚路径结果（保持 B1 修订语义不变）。
            // [Wave1 R18 必改-1] 下界：锚候选非 null ⇒ 候选本身；candidate 为 null（锚在链尾）⇒
            // **最后一条可定位停驻点**——探针不得返回早于下界的未执行出现：线性推进会从该早节点
            // 依次穿越中途**已完成**出现（含锚及其后节点）并重复提交（不变量①）。停驻点之前的
            // 新插节点不在此路径承载（其重驱义务由 C11 重驱合同覆盖，属接线批语义面）。
            // [Wave1 R19 必改-1 补强] 锚可定位且 candidate 非 null 时，仍须检查有效停驻点的
            // **位置安全性**：锚前的有效停驻义务优先作为恢复点；BO-6 驱动层会过滤其后已完成出现，
            // 由此同时保留停驻重驱与不重提已完成节点。
            // [Wave1 R25 重要-1] **全部**有效停驻点逐一做位置安全性检查（不得因最后一条安全而
            // 跳过更早的）：任一有效停驻不在锚后安全路径 ⇒ 记录冲突并优先返回最早有效停驻点。
            // R25 反例（删除 P1→执行越过→同身份加回锚前）中更早有效停驻逃出检测的形态由此封死。
            var hasUnsafeParkedBeforeAnchor = false;
            for (var pi = parkedOutcomes.Count - 1; pi >= 0; pi--)
            {
                var po = parkedOutcomes[pi];
                if (!plan.TryLocate(po.NodeId, po.Occurrence, po.LoopIteration, out var unsafeParked)) continue;
                if (HasCompletedOutcome(run, unsafeParked)) continue;
                var onSafePathAfterAnchor = unsafeParked.LoopIteration > anchorOcc.LoopIteration
                    || (unsafeParked.LoopIteration == anchorOcc.LoopIteration
                        && unsafeParked.SequenceIndex > anchorOcc.SequenceIndex);
                if (!onSafePathAfterAnchor)
                {
                    hasUnsafeParkedBeforeAnchor = true;
                    Log(run, $"有效停驻点 {unsafeParked.NodeId}#{unsafeParked.Occurrence} 被修订重排到已完成锚 "
                             + $"{anchorOcc.NodeId}#{anchorOcc.Occurrence} 之前：返回任一侧都会违反不变量①或②"
                             + "；优先保留停驻义务，恢复推进时过滤已完成出现（BO-6）。");
                }
            }
            WorkflowNodeOccurrence? lowerBound = candidate;
            if (lowerBound is null)
            {
                // [Wave1 R19 重要-2；R23 重要-2 更正；R34 重要-F5 口径统一] **跨代际高轮次停驻标记路径**
                // （TryLocate 透传 loopIteration ⇒ 高轮次标记可定位）。下界取**计划全序最早**的有效停驻点
                //（与 ParkedRescue 选取同口径，[R34 重要-F5]——追加序最后者会漏更早有效停驻所在的低轮次，
                // 使其前插探针被过期高轮次位置误挡）。
                foreach (var po in parkedOutcomes)
                {
                    if (!plan.TryLocate(po.NodeId, po.Occurrence, po.LoopIteration, out var tailBound)) continue;
                    if (HasCompletedOutcome(run, tailBound)) continue;
                    if (lowerBound is not null
                        && (tailBound.LoopIteration > lowerBound.LoopIteration
                            || (tailBound.LoopIteration == lowerBound.LoopIteration
                                && tailBound.SequenceIndex > lowerBound.SequenceIndex)))
                    {
                        continue; // 已有更早（全序）的有效停驻点作下界
                    }
                    lowerBound = tailBound;
                }
            }
            var parkedRescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes,
                lowerBound: lowerBound);
            if (hasUnsafeParkedBeforeAnchor && parkedRescue is null)
            {
                // A valid unsafe marker was found above, so a missing rescue is an inconsistent
                // state. The final aggregation guard turns any still-live marker into Failed.
                Log(run, "已发现可定位的锚前停驻义务，但未能构造恢复点；禁止将其当作普通链尾成功。");
            }
            // [Wave1 R24 重要-2] rescue 与 candidate 都非 null 时按**计划全序取较早者**：
            // 线性推进（从返回点沿 Next 走到链尾再回绕）保证较晚者自然到达（未完成 ⇒ 会被执行），
            // 两个义务都不丢。固定 rescue 覆盖 candidate 的旧形态（R24 反例：修订在锚后插入 C@0、
            // 停驻在更晚轮次 A@1 ⇒ 旧代码返回 A@1 ⇒ C@0 永不进 NodeOutcomes ⇒ 假成功丢步）。
            if (parkedRescue is not null && candidate is not null
                && (candidate.LoopIteration < parkedRescue.LoopIteration
                    || (candidate.LoopIteration == parkedRescue.LoopIteration
                        && candidate.SequenceIndex < parkedRescue.SequenceIndex)))
            {
                return candidate; // candidate 早于 rescue ⇒ 先执行 candidate，线性推进自然到达 rescue
            }
            return parkedRescue ?? candidate;
        }

        if (completionOutcomes.Count == 0)
        {
            var noAnchorRescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes,
                lowerBound: null);
            return noAnchorRescue ?? plan.FirstOccurrence();
        }

        // 全部完成锚在新修订中不可定位（R7 重要-1）
        var rescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes, lowerBound: null);
        if (rescue is not null) return rescue;
        Log(run, "完成身份与停驻身份在新修订中均已消失，按链尾处理（已完成节点不重跑）。");
        return null;
    }

    /// <summary>
    /// **停驻义务复核**（不变量②）：计划中仍有可定位停驻出现 ⇒ 返回恢复应继续的出现；
    /// 无可定位停驻 ⇒ null（调用方决定链首/链尾）。取最后一条可定位停驻标记为基准；
    /// 若其之前（**同一轮次内**）存在从未执行的出现，返回更早者（不静默跳过未执行节点，
    /// R12 建议-1／R14 F1：探针从**该停驻所在轮次的链首**起步——由基准沿 Next 反向不可行，
    /// 改为从链首逐轮推进到目标轮次，避免 R14 F1 的「锁死第 0 轮」缺陷）。
    ///
    /// **返回语义**：返回最早仍有效的停驻义务或其前插未执行出现；没有可定位的未完成停驻时返回 null。
    /// 若恢复点越过已完成出现，由 BO-6 驱动层在推进时过滤这些完成身份。
    /// </summary>
    private WorkflowNodeOccurrence? ParkedRescue(WorkflowRunRecord run, WorkflowPlan plan,
        List<WorkflowNodeOutcome> parkedOutcomes, List<WorkflowNodeOutcome> completionOutcomes,
        WorkflowNodeOccurrence? lowerBound)
    {
        // [Wave1 R25 重要-1] 取**计划全序最早**的有效停驻点（不再「最后追加者首中即返回」）：
        // 更早的有效停驻点（如「删除→执行越过→同身份加回锚前」舞步复活的标记）同样承载重驱义务，
        // 首中即返回会让它逃出救援与冲突检测（静默吞）。最早者重驱后由 BO-6 恢复推进过滤跳过已完成出现。
        var hasEarliest = false;
        var earliestOcc = default(WorkflowNodeOccurrence);
        foreach (var po in parkedOutcomes)
        {
            if (!plan.TryLocate(po.NodeId, po.Occurrence, po.LoopIteration, out var candidateOcc)) continue;
            if (HasCompletedOutcome(run, candidateOcc)) continue; // 过期标记（R16 必改-1）
            if (!hasEarliest
                || candidateOcc.LoopIteration < earliestOcc.LoopIteration
                || (candidateOcc.LoopIteration == earliestOcc.LoopIteration
                    && candidateOcc.SequenceIndex < earliestOcc.SequenceIndex))
            {
                earliestOcc = candidateOcc;
                hasEarliest = true;
            }
        }
        if (!hasEarliest) return null;
        for (var i = parkedOutcomes.Count - 1; i >= 0; i--)
        {
            var parked = parkedOutcomes[i];
            if (!plan.TryLocate(parked.NodeId, parked.Occurrence, parked.LoopIteration, out var parkedOcc)) continue;
            if (!parkedOcc.Equals(earliestOcc)) continue;
            // [Wave1 R16 必改-1] **过期停驻标记**：同一出现先停驻、恢复后完成（NodeOutcomes 为追加式，
            // 旧 waitLocally 条目不删除）⇒ 该标记已失去重驱义务，**不得**把它当恢复点，否则会把恢复点
            // 拖回已完成出现并重复提交（违反不变量①「不重跑已完成出现」）。
            if (HasCompletedOutcome(run, parkedOcc)) continue;
            // 直接构造**与停驻同轮次**的链首出现并向前探查（R14 F1：不得从第 0 轮起步；
            // 也不经 Next 回绕——计划可能无循环段，越尾即 null，会导致探针整体失效）。
            for (var probe = plan.FirstOccurrence() is { } head
                     ? new WorkflowNodeOccurrence(head.NodeId, head.SequenceIndex, head.Occurrence, parkedOcc.LoopIteration)
                     : null;
                 probe is not null && probe.SequenceIndex < parkedOcc.SequenceIndex;
                 probe = plan.Next(probe))
            {
                if (HasCompletedOutcome(run, probe)) continue;
                // [Wave1 R18 必改-1] **下界约束**：锚可定位路径下，探针结果不得早于锚候选——
                // 否则救援返回的早节点完成后，驱动循环按线性推进（DriveAsync/Relocate 无完成跳过）
                // 会穿越中途的**已完成**出现并重复提交（外部副作用二次发生），违反不变量①。
                // 早于下界的未执行出现由「锚可定位路径的 candidate」自身或其后续承载（修订语义内）。
                // [Wave1 R21 必改-F1] 比较必须用计划全序 **(LoopIteration, SequenceIndex) 字典序**——
                // 序列号与轮次拆成两条独立条件会把「轮次更晚但序列号更小」（实际在下界之后）的错误排除。
                if (lowerBound is not null
                    && (probe.LoopIteration < lowerBound.LoopIteration
                        || (probe.LoopIteration == lowerBound.LoopIteration
                            && probe.SequenceIndex < lowerBound.SequenceIndex)))
                {
                    continue;
                }
                Log(run, $"停驻点 {parkedOcc.NodeId}#{parkedOcc.Occurrence} 前存在从未执行的出现 "
                         + $"{probe.NodeId}#{probe.Occurrence}（轮次 {probe.LoopIteration}），按其继续。");
                return probe;
            }
            if (completionOutcomes.Count == 0 || !completionOutcomes.Any(c =>
                    plan.TryLocate(c.NodeId, c.Occurrence, c.LoopIteration, out _)))
            {
                Log(run, $"完成身份在新修订中不可定位；存在停驻标记 {parked.NodeId}#{parked.Occurrence}，"
                         + "按停驻出现继续（不按链尾放行，零发送节点不得被静默吞）。");
            }
            else
            {
                Log(run, $"锚可定位但向后走至链尾；存在停驻标记 {parked.NodeId}#{parked.Occurrence}，"
                         + "按停驻出现继续（不按链尾放行，零发送节点不得被静默吞）。");
            }
            // RecomputeSuccessor may select a marker before its completed anchors; DriveAsync
            // re-drives this obligation and skips those stable completed identities as it advances.
            return parkedOcc;
        }
        return null;
    }

    /// <summary>
    /// 该出现是否**已在 NodeOutcomes 中有完成结果**（[Wave1 R12 重要-1] 重定位完成性过滤；
    /// 停驻标记 <see cref="LocalWaitResultWord"/> 不算完成——它零发送、未完成，
    /// 与 <see cref="RecomputeSuccessor"/> 的锚口径一致）。
    /// </summary>
    private static bool HasCompletedOutcome(WorkflowRunRecord run, WorkflowNodeOccurrence occurrence)
        => run.NodeOutcomes.Any(o => o.Result != LocalWaitResultWord
            && string.Equals(o.NodeId, occurrence.NodeId, StringComparison.Ordinal)
            && o.Occurrence == occurrence.Occurrence
            && o.LoopIteration == occurrence.LoopIteration);

    /// <summary>游标 → 当前计划中的出现（恢复/推进共用；身份失效按最后完成身份重算，不回链首重跑）。</summary>
    private WorkflowNodeOccurrence? Relocate(WorkflowRunRecord run, WorkflowPlan plan)
    {
        if (run.TailReached) return null;
        if (run.Cursor is null) return plan.FirstOccurrence();
        if (plan.TryLocate(run.Cursor.NodeId, run.Cursor.Occurrence, run.Cursor.LoopIteration, out var occ))
            return occ;
        var relocated = RecomputeSuccessor(run, plan);
        ApplyRelocation(run, relocated);
        _runs.Update(run);
        return relocated;
    }

    private static void ApplyRelocation(WorkflowRunRecord run, WorkflowNodeOccurrence? relocated)
    {
        if (relocated is null)
        {
            run.Cursor = null;
            run.TailReached = true;
        }
        else
        {
            run.TailReached = false;
            run.Cursor = new WorkflowNodeCursor
            {
                NodeId = relocated.NodeId,
                Occurrence = relocated.Occurrence,
                LoopIteration = relocated.LoopIteration,
                Attempt = 1,
            };
        }
    }

    /// <summary>
    /// **[批次 14／D1] 本地持久等待登记（确定零发送的停驻路径）；[批次 20／C4①] 登记载荷合同。**
    /// 产物为结构化 <see cref="LocalWaitRegistrationOutcome"/>：<c>Park=true</c>＝零发送停驻
    /// （**已登记**／**登记被拒**／**接缝命中而队列缺失（接线缺陷）** 三种——[Wave1 R15 重要-2／R17 重要-1]
    /// 后者同样停驻，**不得**回落提交把门面零发送结论改写成真实发送）。当前本方法**全部出口均为
    /// Park=true**；<c>Park=false</c> 仅为接缝未命中预留、无产出方。
    /// 登记动作**不含任何发送许可**：只把等待项落盘（队列本地 <see cref="LocalWaitQueuePolicy.DeriveItemId"/> 幂等键；
    /// **[批次 20／C3]** 准入面 9 元组身份在登记时点由权威组成函数求得并随项落盘）。
    /// **[批次 20／C4①] 登记载荷合同**：新登记**必须**携带持久化稳定前置引用（来自注入的权威来源）；
    /// 来源缺失 ⇒ **拒绝登记**（零发送停驻、不回落提交、队列零变化）——不得登记结构性永不参选的等待项
    /// （那是 C4② 合同前存量的专属形态）。未注入队列时**同样停驻**（[Wave1 R15 重要-2]：接缝命中而
    /// 队列缺失＝接线缺陷，须零发送停驻；批次 14 的「不登记且不短路」口径在本批已按 fail-closed 收紧）。
    /// 纪律：这里是「确定未发送」的本地登记，**不得**借此推进游标或终态化运行。
    /// </summary>
    internal LocalWaitRegistrationOutcome TryRegisterLocalWait(WorkflowRunRecord run, WorkflowNodeOccurrence occurrence,
        int attempt, LocalWaitDecisionRecord decision)
    {
        // 等待来源判定（ShouldRegisterLocalWait 接缝）由**调用点**显式应用，不在此处重复——
        // 本方法是登记机制本体（含登记被拒路径），须可独立验证；判定函数与登记口均已就位，
        // R5 后续批次（D3/D2/D4）接入门面结论后只改判定接缝，不改本方法。
        // [Wave1 R15 重要-2] 队列未注入**不是**「不适用」：调用点只有在接缝命中（门面已给出确定
        // 零发送等待结论）时才会走到这里 ⇒ 此时队列缺失是**接线缺陷**，必须以**零发送停驻**收场
        // （返回 NotParked 会让调用点落进提交路径，把零发送结论改写成真实发送——不可逆 fail-open）。
        if (_localWaitQueue is null)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**拒绝登记**（local_wait）：登记接缝命中但未注入等待队列"
                    + "（接线缺陷）——维持零发送停驻，**不回落提交路径**（门面零发送结论不得被改写成发送）。",
            };
        }
        if (decision.Kind != LocalWaitDecisionKind.Wait
            || !decision.NoSendConfirmed
            || !decision.Context.HasTrustedRankingFacts
            || decision.Context.Tier is null
            || decision.Context.Priority is null)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：缺少可绑定的来源、排序或零发送证明，不创建队列项。",
            };
        }

        // [批次 20／C4①] 登记载荷合同（第一步）：前置引用来源未提供 ⇒ **拒绝登记**。
        // 登记结构性永不参选的等待项＝把死锁写进队列（§24.115 IW-05）；零发送停驻结论不被改写成提交，
        // 也不回落到既有提交路径（门面已给出「零发送」结论，提交即越过准入）。
        // [批次 20／Wave1 R3-F3 合同点登记（接线批必答）]
        // ①异常收敛语义：本方法（含 provider.Invoke 与 Upsert）在 RecordIntent **之前**执行，
        //   任何异常上抛都发生在「确定零发送」停驻路径上，零发送性质不被破坏；接线批必须保证
        //   该异常在驱动循环的收敛终态**不得为 Unknown**（无 job 可查 ⇒ 无法对账收敛，批次 14
        //   注释明文禁止该形态）——应收敛为可重试停驻或显式失败态，并在接线批会诊验证。
        // ①'异常面补充（[Wave1 R26 建议-1]）：BuildAdmissionIdentity → 权威 EncodeInt 对
        //   Occurrence/LoopIteration/Attempt 越界（>99,999,999）抛 ArgumentOutOfRangeException
        //   （结构性循环无轮次封顶，LoopIteration 理论无界、实际 1e8 轮不可达）——同样适用本条
        //   收敛义务（不得收敛为 Unknown）。
        // ②跨代际重登记（**已裁决落地**，[Wave1 R3-F3② → Wave2 R35 重要-2 落字]）：ItemId（4 段裸拼，
        //   无 attempt）× AdmissionIdentity（9 元组，含 attempt）⇒ 同一出现以新 attempt 再停驻、或载荷
        //   漂移（ticket 轮换）时，Upsert 载荷比较不通过 ⇒ **响亮冲突＝合同信号**（owner 裁决方向：不设
        //   「显式新通道」）⇒ 本方法 catch 折为 **Park=true「登记未完成」零发送停驻**（R25 建议采纳，
        //   冲突原文随 Reason 留痕）。收敛义务：收敛终态不得为 Unknown（见①）——Upsert 分支已在本层
        //   闭合，provider 分支仍归 BO-1。
        //   **attempt 维度本批不可达**（SubmitAndAwaitAsync const attempt = 1），但**载荷漂移在
        //   attempt=1 下可达**（R11 F-B）：停驻→取消→队列项置 Cancelled→重驱再停驻时，若前置引用含
        //   ticket 且 ticket 已轮换（"…@ticket-7"→"…@ticket-8"），重登记即撞上「已取消同身份项、
        //   载荷不同 ⇒ 响亮冲突」——收敛义务同 R3-F3①（异常不得使运行收敛为 Unknown）。
        // [批次 21／BO-1] provider.Invoke 异常收敛（R3-F3① 同形）：折为 Park=true「登记未完成」零发送停驻，
        // 绝不穿透驱动循环按「在飞事实」收敛 Unknown/Interrupted（登记路径在 RecordIntent 之前，无 job 可查
        // ⇒ Unknown 无法对账，批次 14 注释明载禁止该形态）；重驱时按可重试停驻重新登记。
        string? reference;
        try
        {
            reference = _localWaitPrerequisiteReferenceProvider?.Invoke(run, occurrence);
        }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：前置引用来源异常（" + ex.GetType().Name
                    + "，「" + ex.Message + "」）——维持零发送停驻，不回落提交路径。",
            };
        }
        var referenceAvailable = !string.IsNullOrWhiteSpace(reference);
        if (!referenceAvailable)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**拒绝登记**（local_wait）：C4① 登记载荷合同——"
                    + "前置引用来源未提供（PrerequisiteReference 缺失的等待项结构性永不参选，"
                    + "登记即把死锁写进队列）；维持零发送停驻，不回落提交路径。",
            };
        }

        // [批次 20／Wave1 R7 重要-2；R9-F1 更正；R10 重要-1 补 canonical] 登记载荷合同（第三步）：
        // 拿不到**权威且规范**的 scope ⇒ **拒绝登记**。
        // scope 权威来源按运行来源类别分流：移交来源运行＝运行台账 AdmissionSourceScope；面板来源运行＝
        // 租约侧 FlowRegistration 反查（宿主职责，经 _localWaitAdmissionScopeProvider 注入——面板来源
        // 运行**并非没有**权威 scope，R9-F1 更正「字段缺省＝无权威 scope」的错误等式）。
        // 解析次序（R23 重要-1）：台账字段规范非空 ⇒ 恒取；provider 仅补缺；两者皆缺 ⇒ 拒绝登记。
        // **采纳判据必须与提交面完全同源**（R10 重要-1）：提交面 ResolveAdmissionParent 对两条来源路径
        // 均强制 TaskCenterHost.IsCanonicalAdmissionScope（`bgi:local:{非空完整 epoch}`）——登记点若只判
        // 「非空白」，接受坏 scope（"garbage"／截断纪元）会把准入身份持久化成提交面永不产生的空间 ⇒
        // 项结构性永不可重入（C4① 禁止形态，从「缺 scope」换成「坏 scope」）。故复用**同一谓词**，
        // 不复制规则；不通过 ⇒ 并入既有拒绝登记分支（零发送停驻、不回落提交）。
        // [Wave1 R23 重要-1] 解析次序＝**台账字段优先，provider 仅补缺**：移交来源运行的权威 scope
        // ＝运行台账 AdmissionSourceScope（受理时捕获、只比较不重写）；provider 只在台账字段缺省
        // （面板来源运行）时取用。若允许 provider 覆盖台账字段，纪元轮换后 provider 返回当前纪元
        // 而台账字段是受理时固定纪元 ⇒ 登记身份与提交面（ResolveAdmissionParent 回落支恒取台账字段）
        // 逐字符不等 ⇒ 等待项结构性永不可参选（双源分歧死锁，IW-05 变体）。台账优先 ⇒ 移交来源双源
        // 恒等（provider 无从分歧）、面板来源行为不变（台账字段本为空）。
        var admissionScope = run.AdmissionSourceScope;
        if (string.IsNullOrWhiteSpace(admissionScope))
            // [批次 21／BO-1] 同上面：scope provider 异常 ⇒ 登记未完成零发送停驻，绝不 Unknown 收敛。
            try
            {
                admissionScope = _localWaitAdmissionScopeProvider?.Invoke(run);
            }
            catch (Exception ex)
            {
                return new LocalWaitRegistrationOutcome
                {
                    Park = true,
                    Reason = "本地持久等待**登记未完成**（local_wait）：授权 scope 反查来源异常（" + ex.GetType().Name
                        + "，「" + ex.Message + "」）——维持零发送停驻，不回落提交路径。",
                };
            }
        var scopeAvailable = TaskCenterHost.IsCanonicalAdmissionScope(admissionScope);
        if (!scopeAvailable)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**拒绝登记**（local_wait）：C4① 登记载荷合同——拿不到运行的"
                    + "**规范形状**固定授权 Scope（来源缺供或非 `bgi:local:{完整 epoch}`；非规范 scope 的"
                    + "身份与 successor 提交面不同身份空间，结构性永不可重入）。维持零发送停驻，不回落提交路径。",
            };
        }
        var context = decision.Context;
        if (!string.Equals(context.RunId, run.RunId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowId, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowRevision, run.WorkflowRevision, StringComparison.Ordinal)
            || context.RecordRevision != run.RecordRevision
            || !string.Equals(context.NodeId, occurrence.NodeId, StringComparison.Ordinal)
            || context.SequenceIndex != occurrence.SequenceIndex
            || context.Occurrence != occurrence.Occurrence
            || context.LoopIteration != occurrence.LoopIteration
            || context.Attempt != attempt
            || !string.Equals(context.CursorNodeId, run.Cursor?.NodeId, StringComparison.Ordinal)
            || context.CursorOccurrence != (run.Cursor?.Occurrence ?? 0)
            || context.CursorLoopIteration != (run.Cursor?.LoopIteration ?? 0))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：判定身份与当前运行/游标不一致，不创建队列项。",
            };
        }

        // [Wave1 R27 重要-1] 整数段显式 **InvariantCulture**：与 Translate ③' 重建侧（InvariantCulture）
        // 同一口径——隐式 int.ToString() 取 CurrentCulture，非拉丁数字文化（fa-IR 等）下写侧落盘本土数字、
        // 校验侧重建 ASCII ⇒ 同源校验必败 ⇒ 合法登记项被判「不同源」结构性永不参选。两侧显式统一后
        // 争议彻底消除（R22 曾撤回该怀疑、R27 复提——与其依赖运行时文化数据行为，不如一行确定性消除）。
        var stableIdentity = run.RunId + "|" + occurrence.NodeId + "|"
            + occurrence.Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
            + occurrence.LoopIteration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // [批次 20／C3] 身份翻译在登记时点完成：准入面 9 元组经**共享权威工厂**求得并随项落盘。
        // 该工厂与 successor 提交路径（TaskCenterHost.Admission.cs）**共用**（namespace/triggerOccurrenceId
        // 口径只此一处；锚定夹具 SuccessorCandidateComposition_AnchoredToSharedFactory 机械保证两侧同改）。
        // scope 取上文解析的权威值（台账字段优先，provider 仅补缺；两者皆缺已拒绝登记——R23 重要-1）。
        // [批次 21／BO-1] 身份构造异常（含①'：Occurrence/LoopIteration/Attempt 越界 >99,999,999 抛
        // ArgumentOutOfRangeException）⇒ 同形收敛：Park=true 登记未完成零发送停驻，绝不 Unknown。
        (string, string) admissionIdentityPair;
        try
        {
            admissionIdentityPair = LocalWaitIdentityTranslation.BuildAdmissionIdentity(
                TaskCenterHost.BuildSuccessorIdentityCandidate(admissionScope, run.WorkflowId,
                    run.RunId, occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration, attempt));
        }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：登记身份构造异常（" + ex.GetType().Name
                    + "，「" + ex.Message + "」）——维持零发送停驻，不回落提交路径。",
            };
        }
        var admissionIdentity = admissionIdentityPair.Item1;
        var candidateId = admissionIdentityPair.Item2;
        if (context.Scope is { } decisionScope
            && !string.Equals(decisionScope, admissionScope, StringComparison.Ordinal))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：来源 scope 与当前权威 scope 不一致，不创建队列项。",
            };
        }
        if (context.AdmissionIdentity is { } expectedIdentity
            && !string.Equals(expectedIdentity, admissionIdentity, StringComparison.Ordinal)
            || context.CandidateId is { } expectedCandidate
            && !string.Equals(expectedCandidate, candidateId, StringComparison.Ordinal))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：登记候选身份与判定快照不一致，不创建队列项。",
            };
        }
        if (context.SourceKind is null || string.IsNullOrWhiteSpace(context.SourceIdentity))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：缺少来源类别/身份绑定，不创建队列项。",
            };
        }
        var sourceKind = context.SourceKind
            ?? LocalWaitSourceKind.PanelFlowRegistration;
        DateTimeOffset enqueuedAtUtc;
        try { enqueuedAtUtc = _opt.Clock().ToUniversalTime(); }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：登记时钟异常（" + ex.GetType().Name
                    + "，「" + ex.Message + "」）——维持零发送停驻，不创建队列项。",
            };
        }
        var binding = new LocalWaitBinding
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId(stableIdentity),
            StableIdentity = stableIdentity,
            CandidateId = candidateId,
            AdmissionIdentity = admissionIdentity,
            Namespace = run.WorkflowId,
            WorkflowId = run.WorkflowId,
            SourceKind = sourceKind,
            SourceIdentity = context.SourceIdentity ?? run.RunId!,
            RunId = run.RunId!,
            Scope = admissionScope!,
            WorkflowRevision = run.WorkflowRevision,
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            RecordRevision = run.RecordRevision,
            CursorNodeId = run.Cursor?.NodeId,
            CursorOccurrence = run.Cursor?.Occurrence ?? 0,
            CursorLoopIteration = run.Cursor?.LoopIteration ?? 0,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = attempt,
            Tier = context.Tier.Value,
            Priority = context.Priority.Value,
            IsHoeingHighest = false,
            PrerequisiteReference = reference,
            EnqueuedAtUtc = enqueuedAtUtc,
        };
        return new LocalWaitRegistrationOutcome
        {
            Park = true,
            Reason = "本地等待绑定已形成（" + LocalWaitReasonCode + "）：运行停驻先落盘，之后发布等待队列项；未获准入前零发送。",
            Decision = decision with { Binding = binding },
        };
    }

    /// <summary>
    /// **[批次 14／D1] 等待结论判定接缝。** 门面结论尚未接线（本批明确不接生产入口）⇒ 恒 false。
    /// 该函数的存在使「等待短路」是**显式判定**而非隐式兜底：后续批次只改这里，不靠在提交路径上加 `_ =&gt;`。
    /// </summary>
    /// <summary>
    /// **[批次 20／Wave3／C11] 等待判定（实例级注入）**：<see cref="WorkflowRunnerOptions.ShouldRegisterLocalWait"/>
    /// 非 null ⇒ 由其判定（测试/接线批注入门面结论判定）；null ⇒ 恒 false（批次 14 既有语义，未接线）。
    /// **实例级**（非进程级静态）——避免跨测试类/跨运行的并行污染（R43 重要-6）。
    /// 判定接缝由调用点显式应用；本方法为登记机制本体（TryRegisterLocalWait）的入口闸。
    /// </summary>
    private LocalWaitDecisionRecord? DecideLocalWait(WaitDecisionRequest request, WorkflowNodeOccurrence occurrence)
    {
        if (_waitDecisionSource is not null) return _waitDecisionSource.Decide(request);
        if (_opt.ShouldRegisterLocalWait?.Invoke(occurrence) != true) return null;
        // 旧测试接缝只用于异常/状态夹具；生产装配始终注入类型化宿主来源。
        return new LocalWaitDecisionRecord
        {
            Kind = LocalWaitDecisionKind.Wait,
            Context = ContextFromRequest(request) with
            {
                SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
                SourceIdentity = request.RunId,
                Tier = ArbitrationTier.Plan,
                Priority = 0,
                HasTrustedRankingFacts = true,
            },
            Reason = "测试接缝命中本地等待",
            NoSendConfirmed = true,
        };
    }

    internal LocalWaitRegistrationOutcome TryRegisterLocalWait(WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, int attempt)
    {
        var request = CreateWaitDecisionRequest(run, occurrence, attempt);
        var context = ContextFromRequest(request) with
        {
            SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
            SourceIdentity = request.RunId,
            Scope = run.AdmissionSourceScope,
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedRankingFacts = true,
        };
        var prepared = TryRegisterLocalWait(run, occurrence, attempt, new LocalWaitDecisionRecord
        {
            Kind = LocalWaitDecisionKind.Wait,
            Context = context,
            Reason = "直接登记测试接缝",
            NoSendConfirmed = true,
        });
        if (prepared.Decision?.Binding is not { } binding) return prepared;
        try
        {
            _localWaitQueue!.Upsert(binding.ToQueueItem());
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待已登记（" + LocalWaitReasonCode + "）：测试直调路径。",
                Decision = prepared.Decision,
            };
        }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：等待队列拒绝写入或存储异常（"
                    + ex.GetType().Name + "）——「" + ex.Message + "」；维持零发送停驻，不回落提交路径。",
                Decision = prepared.Decision with { Kind = LocalWaitDecisionKind.Hold, Binding = null, Reason = ex.Message },
            };
        }
    }

    private static WaitDecisionRequest CreateWaitDecisionRequest(WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, int attempt)
        => new()
        {
            RunId = run.RunId ?? "",
            WorkflowId = run.WorkflowId ?? "",
            WorkflowRevision = run.WorkflowRevision ?? "",
            RecordRevision = run.RecordRevision,
            CursorNodeId = run.Cursor?.NodeId,
            CursorOccurrence = run.Cursor?.Occurrence ?? 0,
            CursorLoopIteration = run.Cursor?.LoopIteration ?? 0,
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = attempt,
        };

    private static LocalWaitDecisionContext ContextFromRequest(WaitDecisionRequest request)
        => new()
        {
            RunId = request.RunId,
            WorkflowId = request.WorkflowId,
            WorkflowRevision = request.WorkflowRevision,
            RecordRevision = request.RecordRevision,
            CursorNodeId = request.CursorNodeId,
            CursorOccurrence = request.CursorOccurrence,
            CursorLoopIteration = request.CursorLoopIteration,
            NodeId = request.NodeId,
            SequenceIndex = request.SequenceIndex,
            Occurrence = request.Occurrence,
            LoopIteration = request.LoopIteration,
            Attempt = request.Attempt,
        };

    private void PublishPersistedLocalWait(WorkflowRunRecord run)
    {
        if (run.LocalWaitDecision is not { Kind: LocalWaitDecisionKind.Wait, Binding: { } binding }) return;
        try
        {
            _localWaitQueue?.Upsert(binding.ToQueueItem());
        }
        catch (Exception ex)
        {
            run.Note = AppendNote(run.Note,
                "等待队列发布失败（运行台账已保存绑定，可在显式恢复时重建；" + ex.GetType().Name + "）。");
            _runs.Update(run);
        }
    }

    private void CancelPersistedLocalWait(LocalWaitBinding? binding, string reason)
    {
        if (binding is null) return;
        if (_localWaitQueue is null)
            throw new InvalidOperationException("存在等待绑定但 LocalWaitQueue 未注入；拒绝继续恢复。");
        _localWaitQueue.Cancel(binding.ItemId, reason, _opt.Clock());
    }

    private static bool SameWaitBindingPayload(LocalWaitBinding left, LocalWaitBinding right)
        => string.Equals(left.ItemId, right.ItemId, StringComparison.Ordinal)
           && string.Equals(left.StableIdentity, right.StableIdentity, StringComparison.Ordinal)
           && string.Equals(left.CandidateId, right.CandidateId, StringComparison.Ordinal)
           && string.Equals(left.AdmissionIdentity, right.AdmissionIdentity, StringComparison.Ordinal)
           && string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal)
           && string.Equals(left.WorkflowId, right.WorkflowId, StringComparison.Ordinal)
           && left.SourceKind == right.SourceKind
           && string.Equals(left.SourceIdentity, right.SourceIdentity, StringComparison.Ordinal)
           && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
           && string.Equals(left.Scope, right.Scope, StringComparison.Ordinal)
           && string.Equals(left.WorkflowRevision, right.WorkflowRevision, StringComparison.Ordinal)
           && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
           && left.SequenceIndex == right.SequenceIndex
           && left.CursorNodeId == right.CursorNodeId
           && left.CursorOccurrence == right.CursorOccurrence
           && left.CursorLoopIteration == right.CursorLoopIteration
           && left.Occurrence == right.Occurrence
           && left.LoopIteration == right.LoopIteration
           && left.Attempt == right.Attempt
           && left.Tier == right.Tier
           && left.Priority == right.Priority
           && left.IsHoeingHighest == right.IsHoeingHighest
           && string.Equals(left.PrerequisiteReference, right.PrerequisiteReference, StringComparison.Ordinal);

    /// <summary>
    /// 结果提交（B3-①：观察终态 + 节点结果 + 游标推进单次原子落盘，无中间态窗口）。
    /// I2/四轮重要 9：ObservedTerminal 只存原始线协议词（rawTerminal），业务词与「未确认」（null）绝不写入；
    /// 四轮阻断 5：unknown/cancelUnconfirmed 游标不推进（调用方先置 Unknown 状态再进本方法，保持单次原子落盘）；
    /// I3/四轮重要 10：持久化原因统一脱敏。
    /// </summary>
    private void CommitOutcome(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence occurrence,
        string result, string? reason, string? rawTerminal = null)
    {
        if (rawTerminal is not null && run.CurrentSubmission is { ObservedTerminal: null } sub)
            sub.ObservedTerminal = rawTerminal; // 提交终态与结果/游标同写（仅原始线协议词）
        // G8／§12.3：结果若来自**边界观察到的权威终态**，则把「产生它的提交键＋attempt」随结果落盘——
        // 供租约侧按完整发送关联独立结清该节点 Operation（不得凭「同一出现曾有过某结果」结清另一笔责任）。
        // 仅当当前提交确实属于本次出现身份时才记录（前置/闸门/恢复路径的 CurrentSubmission 可能属别的出现）。
        var producing = run.CurrentSubmission is { } cur
                        && string.Equals(cur.NodeId, occurrence.NodeId, StringComparison.Ordinal)
                        && cur.Occurrence == occurrence.Occurrence
                        && cur.LoopIteration == occurrence.LoopIteration
            ? cur
            : null;
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Result = result,
            RawTerminal = rawTerminal,
            Reason = Sanitize(reason),
            SubmissionKey = rawTerminal is not null ? producing?.Key : null,
            Attempt = rawTerminal is not null ? producing?.Attempt : null,
            // 完整发送身份（租约侧签发）：与 SubmissionKey/Attempt 同规则随结果落盘，
            // 使「节点操作独立终局」可证明该结果属于**这一笔发送**（提交键在同 attempt 多 sendSeq 间可复用）。
            AcceptedSendIdentity = rawTerminal is not null ? producing?.AcceptedSendIdentity : null,
        });
        // **[批次 14／D1]** `waitLocally` 与 `unknown`/`cancelUnconfirmed` 同族：**游标不推进**
        // （等待项就绪后须从同一节点重走完整准入）。若落进 `else` 分支推进游标，等于把「已登记等待」
        // 当成「已完成」——既跳过该节点（作业静默丢步），也让「重新走完整准入」的要求不成立。
        if (result is "unknown" or "cancelUnconfirmed" or LocalWaitResultWord)
            ApplyRelocation(run, occurrence); // 结果不确定／确定等待：游标留在当前出现，绝不推进
        else
            ApplyRelocation(run, plan.Next(occurrence));
        _runs.Update(run);
    }

    private WorkflowRunRecord Pause(WorkflowRunRecord run)
    {
        run.State = WorkflowRunState.Paused;
        run.Note = AppendNote(run.Note, "已暂停（≠停止；修订按节点边界生效；显式 ResumeAsync 恢复）。");
        _runs.Update(run);
        return run;
    }

    private void Log(WorkflowRunRecord run, string message) => Log(run.RunId, message);

    private void Log(string runId, string message) => _opt.Log?.Invoke($"[{runId}] {message}");

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrEmpty(note) ? addition : note + " | " + addition;
}


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs L1-L1177 SHA256=b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711

using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// WorkflowRunner / Reconciler（R4.5）验收夹具：
/// 顺序链跑通 + 提交意图先行（D11）、过滤节点不提交、失败默认停止且不触发收尾（D10/D12）、
/// 继续策略下拒绝节点聚合 Failed（D6 失败不被成功覆盖）、修订对账节点边界生效（D7）、
/// 显式跳过当前/停止（不触发收尾）、触发器等待不占槽位（无提交无锁、时刻持久化）。
/// 全部走假边界/假前置/假终止 + 手动时钟，无真实 IPC/磁盘外副作用。
/// </summary>
public class WorkflowRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public WorkflowRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wfrun-" + Guid.NewGuid().ToString("N")[..8]);
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        private readonly RunStore _runs;
        public bool SingleNativeSupported { get; set; }
        public List<(string NodeId, string? Config)> Submissions { get; } = new();
        public Queue<string> TerminalScript { get; } = new();
        public Func<WorkflowSubmitRequest, Task>? OnSubmit { get; set; }
        public Func<string, CancellationToken, Task<string>>? OnAwait { get; set; }
        public Func<string, CancellationToken, Task<BoundaryTerminalResult>>? OnAwaitEx { get; set; }
        public BoundarySubmitResult? SubmitOverride { get; set; }
        public List<string> CancelRequests { get; } = new();
        public List<string> IntentStateAtSubmit { get; } = new();

        public FakeBoundary(RunStore runs) => _runs = runs;

        public async Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submissions.Add((request.Occurrence.NodeId, request.Node.Ref?.Config));
            IntentStateAtSubmit.Add(_runs.Load(request.Run.RunId)!.CurrentSubmission?.Intent.ToString() ?? "None");
            if (OnSubmit is not null) await OnSubmit(request);
            return SubmitOverride ?? BoundarySubmitResult.AcceptedWith("job-" + Submissions.Count);
        }

        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (OnAwaitEx is not null) return await OnAwaitEx(jobId, ct);
            var word = OnAwait is not null ? await OnAwait(jobId, ct)
                : (TerminalScript.Count > 0 ? TerminalScript.Dequeue() : "succeeded");
            return BoundaryTerminalResult.Observed(word);
        }

        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        {
            CancelRequests.Add(jobId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePrerequisite : IWorkflowPrerequisiteAdapter
    {
        public List<string> Executed { get; } = new();
        public Func<WorkflowRunRecord, Task>? OnExecute { get; set; }
        public async Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
        {
            Executed.Add(strategy.Kind);
            if (OnExecute is not null) await OnExecute(run);
            return PrerequisiteResult.ProceedInstance;
        }
    }

    private sealed class FakeTerminal : IWorkflowTerminalExecutor
    {
        public List<string?> Actions { get; } = new();
        public Func<WorkflowTerminalAction, WorkflowRunRecord, Task>? OnExecute { get; set; }
        public TerminalExecutionResult Result { get; set; } = TerminalExecutionResult.Executed("job-terminal");
        public async Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
        {
            Actions.Add(action.GetString("action"));
            if (OnExecute is not null) await OnExecute(action, run);
            return Result;
        }
    }

    private static WorkflowNode DragonNode(string id, string config)
        => new() { NodeId = id, Kind = "resource.oneDragonConfig",
            Ref = new WorkflowResourceRef { Config = config, ConfigKey = config + "#k", Revision = "rev-1" } };

    private string SeedFlow(WorkflowDocument doc)
    {
        doc.Activation = new WorkflowActivation { Status = "active" };
        return _workflows.Save(doc, null) + "|" + doc.WorkflowId!;
    }

    private (WorkflowRunner, FakeBoundary, FakeTerminal) MakeRunner(bool continueOnFailure = false,
        Func<TimeSpan, CancellationToken, Task>? delay = null, DateTimeOffset? now = null,
        FakePrerequisite? prerequisite = null, TimeSpan? skipConfirmTimeout = null)
    {
        var boundary = new FakeBoundary(_runs);
        var terminal = new FakeTerminal();
        var runner = new WorkflowRunner(_workflows, _runs, boundary, prerequisite ?? new FakePrerequisite(), terminal,
            new WorkflowRunnerOptions
            {
                ContinueOnNodeFailure = continueOnFailure,
                Clock = () => now ?? DateTimeOffset.Now,
                DelayAsync = delay ?? ((_, _) => Task.CompletedTask),
                SkipConfirmTimeout = skipConfirmTimeout ?? TimeSpan.FromSeconds(15),
            });
        return (runner, boundary, terminal);
    }

    /// <summary>
    /// [R5.2 G6 会诊] **「已存在受理事实却收到确定拒绝」的冲突分支回归**：本层不得因拿到确定拒绝就
    /// 降级受理事实、更不得按确定拒绝**推进游标**（那会记 rejected 结果并可能继续下一节点、随后替换
    /// CurrentSubmission）——必须按 `unknown` 停驻、游标留在本节点、受理记录（Intent/JobId）保留。
    /// </summary>
    [Fact]
    public async Task AcceptedReceiptThenRejected_ConvergesUnknown_KeepsReceiptAndCursor()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "受理与拒绝冲突",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        });
        var workflowId = seed.Split('|')[1];
        var (runner, boundary, _) = MakeRunner();
        boundary.OnSubmit = req =>
        {
            // 模拟「边界已把本轮落盘为 Accepted（含 jobId）」，随后却向本层返回确定拒绝。
            var self = req.Run; // 与 Runner 持有的是同一实例（故本层可见该受理事实）
            self.CurrentSubmission!.Intent = SubmitIntentState.Accepted;
            self.CurrentSubmission.JobId = "job-accepted";
            _runs.Update(self);
            return Task.CompletedTask;
        };
        boundary.SubmitOverride = BoundarySubmitResult.Rejected("boundary_rejected");

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Unknown, run.State);                 // 保守 Unknown 停驻
        Assert.Equal("unknown", run.NodeOutcomes[^1].Result);              // 不得记为 rejected
        Assert.Single(boundary.Submissions);                               // 未继续下一节点
        Assert.Equal("n-1", run.Cursor!.NodeId);                           // 游标未推进
        Assert.Equal(SubmitIntentState.Accepted, run.CurrentSubmission!.Intent); // 受理事实保留
        Assert.Equal("job-accepted", run.CurrentSubmission.JobId);
    }

    [Fact]
    public async Task SequentialChain_SubmitsInOrder_IntentRecordedBeforeSubmit_TerminalFiresOnce()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "顺序链",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
            Terminal = [new WorkflowTerminalAction
            {
                Kind = "terminal.completionAction",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("关闭游戏") },
            }],
        });
        var workflowId = seed.Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner();

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(s => s.NodeId));
        Assert.All(boundary.IntentStateAtSubmit, s => Assert.Equal("IntentRecorded", s)); // 意图先行（D11）
        Assert.Equal(["关闭游戏"], terminal.Actions); // 成功边界收尾一次
        Assert.Equal(2, run.NodeOutcomes.Count(o => o.Result == "succeeded"));
        Assert.Equal(SubmitIntentState.Accepted, run.CurrentSubmission!.Intent);
        Assert.Equal("succeeded", run.CurrentSubmission!.ObservedTerminal); // B3：终态与结果同写
        Assert.StartsWith("idem-", run.CurrentSubmission!.Key); // B2：确定性派生键
    }

    [Fact]
    public async Task FilteredNode_NeverSubmitted()
    {
        var doc = new WorkflowDocument
        {
            Name = "过滤",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-off", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A" },
                    Strategies = [new WorkflowStrategy
                    {
                        Kind = "condition.weekdays",
                        Params = new Dictionary<string, System.Text.Json.JsonElement>
                        {
                            ["days"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "周一" }),
                            ["dayBoundary"] = System.Text.Json.JsonSerializer.SerializeToElement("localMidnight"),
                        },
                    }] },
                DragonNode("n-on", "配置B"),
            ],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];
        var wednesday = new DateTimeOffset(2026, 9, 16, 6, 0, 0, TimeSpan.FromHours(8));
        var (runner, boundary, _) = MakeRunner(now: wednesday);

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-on"], boundary.Submissions.Select(s => s.NodeId)); // 过滤节点不提交
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-off", Result: "skippedFilter" });
    }

    [Fact]
    public async Task NodeFailure_DefaultPolicy_StopsFlow_NoTerminal()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "失败停止",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner();
        boundary.TerminalScript.Enqueue("failed");

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Failed, run.State);
        Assert.Equal(["n-1"], boundary.Submissions.Select(s => s.NodeId)); // 后续节点未提交
        Assert.Empty(terminal.Actions); // D10：失败不触发收尾
    }

    [Fact]
    public async Task RejectedNode_ContinuePolicy_AggregatesFailed_NotCoveredByLaterSuccess()
    {
        var doc = new WorkflowDocument
        {
            Name = "聚合",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-single", Kind = "resource.singleTask",
                    Ref = new WorkflowResourceRef { Config = "配置A", TaskId = "t-1" } },
                DragonNode("n-dragon", "配置B"),
            ],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner(continueOnFailure: true);
        boundary.SingleNativeSupported = false;

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-dragon"], boundary.Submissions.Select(s => s.NodeId)); // 拒绝节点未提交、不连坐
        Assert.Equal(WorkflowRunState.Failed, run.State); // B1：拒绝不被后续成功覆盖
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-single", Result: "rejected" });
        Assert.Empty(terminal.Actions);
    }

    [Fact]
    public async Task RevisionChange_TakesEffectAtNodeBoundary()
    {
        var doc = new WorkflowDocument
        {
            Name = "改流",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        // 第一个节点终态后、第二节点边界前：外部改流（换第二个节点）
        boundary.OnAwait = async (_, _) =>
        {
            var current = _workflows.Load(workflowId);
            var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
            current.Nodes[1] = DragonNode("n-2", "配置C-改后");
            _workflows.Save(current, revision);
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["配置A", "配置C-改后"], boundary.Submissions.Select(s => s.Config)); // 新修订节点边界生效
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task SkipCurrent_CancelsLeafWait_NodeMarkedSkippedUser_FlowAdvances()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "显式跳过",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();
        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        boundary.OnSubmit = (req) => { runId = req.Run.RunId; return Task.CompletedTask; };
        var job1Calls = 0;
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId == "job-1") // 第一节点：首次等待阻塞（待显式跳过）；B4 确认调用返回远端已取消
            {
                if (Interlocked.Increment(ref job1Calls) == 1)
                {
                    awaitEntered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return "cancelled"; // 确认阶段：远端取消已确认
            }
            return "succeeded";
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Equal(WorkflowRunState.Succeeded, run.State); // 显式跳过不算坏结果
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedUser" });
        Assert.Equal(2, boundary.Submissions.Count); // 流程继续推进到第二节点
    }

    [Fact]
    public async Task Stop_DuringTriggerWait_Cancelled_NoTerminal_NoSubmission()
    {
        var future = DateTimeOffset.Now.AddHours(2);
        var doc = new WorkflowDocument
        {
            Name = "定时",
            Nodes = [DragonNode("n-1", "配置A")],
            Triggers = [new WorkflowTrigger
            {
                Kind = "trigger.time",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement(future.ToString("HH:mm")),
                    ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("nextDay"),
                },
            }],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        var gate = new TaskCompletionSource();
        var (runner, boundary, terminal) = MakeRunner(delay: async (_, ct) =>
        {
            gate.TrySetResult(); // 已进入等待（不占槽位）
            await Task.Delay(Timeout.Infinite, ct);
        });

        var task = runner.StartAsync(workflowId);
        await gate.Task;
        string? runId = null;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.Stop);
        var run = await task;

        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.Empty(boundary.Submissions); // 等待期无提交
        Assert.Empty(terminal.Actions); // D10：手动停止不触发收尾
    }

    [Fact]
    public async Task TriggerWait_PersistsWakeTime_NoSlotHeld_FiresOnTime()
    {
        var now = new DateTimeOffset(2026, 9, 16, 5, 0, 0, TimeSpan.FromHours(8));
        var doc = new WorkflowDocument
        {
            Name = "等待证据",
            Nodes = [DragonNode("n-1", "配置A")],
            Triggers = [new WorkflowTrigger
            {
                Kind = "trigger.time",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("06:30"),
                    ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("nextDay"),
                },
            }],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        TimeSpan? capturedDelay = null;
        var (runner, boundary, _) = MakeRunner(now: now, delay: (d, _) =>
        {
            capturedDelay = d;
            return Task.CompletedTask; // 手动时钟快进
        });

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(TimeSpan.FromMinutes(90), capturedDelay); // 05:00 → 06:30
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Single(boundary.Submissions);
        Assert.Null(run.Wait); // 等待结束后清空（运行中快照曾持久化 NextTriggerAt）
    }
    // ======== ASTRA 二轮处置夹具（B1 修订寻址 / B3 恢复 / B4 跳过确认 / B5 收尾 / B9 轮次等待） ========

    [Fact]
    public async Task RevisionReload_NodeInserted_RecomputesSuccessorByIdentity_NoMisaddress()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "插入改流",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        boundary.OnAwait = async (jobId, _) =>
        {
            // A 执行期间：外部在 A 与 B 之间插入新节点 X（B1：旧序列坐标寻址会错位/越界）；仅此次改流
            if (jobId == "job-1")
            {
                var current = _workflows.Load(workflowId);
                var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
                current.Nodes.Insert(1, DragonNode("n-x", "配置X-插入"));
                _workflows.Save(current, revision);
            }
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        // 新修订按稳定身份重算后继：A → X（插入节点被执行）→ B，不错位、不重跑
        Assert.Equal(["n-1", "n-x", "n-2"], boundary.Submissions.Select(s => s.NodeId));
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task RevisionReload_NodeDeleted_SkipsDeleted_NoReplay()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "删除改流",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B"), DragonNode("n-3", "配置C")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        boundary.OnAwait = async (jobId, _) =>
        {
            if (jobId == "job-1")
            {
                var current = _workflows.Load(workflowId);
                var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
                current.Nodes.RemoveAt(1); // 删除 B：新定义 [A, C]
                _workflows.Save(current, revision);
            }
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-1", "n-3"], boundary.Submissions.Select(s => s.NodeId)); // B 被删除不再执行；C 不以旧坐标错位执行
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task RevisionReload_TailAppended_ChainTailReconciles_ExecutesAppendedNode()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "链尾追加",
            Nodes = [DragonNode("n-1", "配置A")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        boundary.OnAwait = async (jobId, _) =>
        {
            if (jobId == "job-1")
            {
                var current = _workflows.Load(workflowId);
                var revision = _workflows.List().First(e => e.WorkflowId == workflowId).Revision;
                current.Nodes.Add(DragonNode("n-2", "配置B-追加")); // 链尾追加（B1：链尾也是节点边界）
                _workflows.Save(current, revision);
            }
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(s => s.NodeId)); // 追加节点被执行
        Assert.True(run.TailReached); // 最终链尾落盘（游标 null 消歧）
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task Resume_InterruptedRun_ContinuesFromCursor_NoReplay_HistoricalFailureAggregates()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "恢复",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B"), DragonNode("n-3", "配置C")],
        }).Split('|');
        var revision = seed[0];
        var workflowId = seed[1];

        // 模拟崩溃现场：A 已失败（历史结果）、游标指向 B、无在飞提交
        var rec = _runs.CreateRun(workflowId, revision);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "n-1", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "failed", Reason = "模拟崩溃前失败" });
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-2", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        _runs.Update(rec);

        var recovered = Assert.Single(_runs.RecoverOnStart());
        Assert.Equal(WorkflowRunState.Interrupted, recovered.State);

        var (runner, boundary, _) = MakeRunner(continueOnFailure: true);
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Equal(["n-2", "n-3"], boundary.Submissions.Select(s => s.NodeId)); // 不重放已完成节点
        Assert.Equal(WorkflowRunState.Failed, run.State); // B3：历史失败经 NodeOutcomes 重建，不被后续成功覆盖
    }

    [Fact]
    public async Task Resume_UnknownRun_Rejected_NeverAutoResumed()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "未知拒恢复",
            Nodes = [DragonNode("n-1", "配置A")],
        }).Split('|');
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        _runs.RecordIntent(rec, new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1),
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1,
        });
        Assert.Single(_runs.RecoverOnStart()); // 提交在飞 → Unknown

        var (runner, _, _) = MakeRunner();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ResumeAsync(rec.RunId)); // 禁止自动恢复
    }

    [Fact]
    public async Task CrashWindow3_SubmissionTerminalUncommitted_RecoveryBackfillsOutcome_NoRerun()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "补记",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|');
        // 遗留现场：n-1 提交终态已观察但结果未提交（正常路径单次写已消除此窗口，仅遗留/手工记录可达）
        var rec = _runs.CreateRun(seed[1], seed[0]);
        rec.State = WorkflowRunState.Running;
        rec.TriggerConsumed = true;
        rec.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        _runs.RecordIntent(rec, new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(rec.RunId, "n-1", 0, 0, 1),
            NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1,
        });
        rec.CurrentSubmission!.Intent = SubmitIntentState.Accepted;
        rec.CurrentSubmission!.JobId = "job-legacy";
        rec.CurrentSubmission!.ObservedTerminal = "succeeded";
        _runs.Update(rec);
        Assert.Single(_runs.RecoverOnStart()); // Interrupted（有终态事实）

        var (runner, boundary, _) = MakeRunner();
        var run = await runner.ResumeAsync(rec.RunId);

        Assert.Equal(["n-2"], boundary.Submissions.Select(s => s.NodeId)); // n-1 按事实补记，不重跑
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "succeeded" });
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task SkipCurrent_DuringPrerequisite_AppliesWhenLeafCreated_ConfirmedSkipped()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "空窗跳过",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [new WorkflowStrategy { Kind = "prerequisite.account",
                        Params = new Dictionary<string, System.Text.Json.JsonElement>
                        { ["uid"] = System.Text.Json.JsonSerializer.SerializeToElement("10001") } }] }, // 前置挂起期 = 叶子未建立空窗（R4.8 §4.5：账号策略必须带完整 uid）
                DragonNode("n-2", "配置B"),
            ],
        }).Split('|')[1];

        var prereqGate = new TaskCompletionSource();
        string? runId = null;
        var prereq = new FakePrerequisite { OnExecute = run => { runId = run.RunId; return prereqGate.Task; } };
        var (runner, boundary, _) = MakeRunner(prerequisite: prereq);

        var job1Calls = 0;
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId != "job-1") return "succeeded"; // 后续节点正常成功
            if (Interlocked.Increment(ref job1Calls) == 1)
                await Task.Delay(Timeout.Infinite, ct); // 首次等待：叶子已取消 → 抛 OCE 进入 B4 确认路径
            return "cancelled"; // 确认阶段：远端取消已确认
        };

        var task = runner.StartAsync(workflowId);
        // 前置挂起期请求跳过（LeafCts 尚不存在——B4 空窗）；动作绑定当前出现身份
        for (var i = 0; i < 100 && runId is null; i++) { if (runId is null) await Task.Delay(10); }
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        prereqGate.TrySetResult();
        var run = await task;

        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedUser" }); // 空窗动作未丢失
        Assert.Equal(["n-1", "n-2"], boundary.Submissions.Select(s => s.NodeId)); // 流程推进
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task SkipCurrent_Unconfirmed_RunUnknown_NoAdvanceNoTerminal()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跳过未确认",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner(skipConfirmTimeout: TimeSpan.FromMilliseconds(200));

        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        boundary.OnSubmit = req => { runId = req.Run.RunId; return Task.CompletedTask; };
        boundary.OnAwait = async (_, ct) =>
        {
            awaitEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 首次等待与确认等待都不主动返回（确认超时路径）
            return "succeeded";
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Equal(WorkflowRunState.Unknown, run.State); // B4：未确认不猜成功
        Assert.Single(boundary.Submissions); // 不推进到下一节点
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "cancelUnconfirmed" });
        Assert.Empty(terminal.Actions); // D10：未知不触发收尾
    }

    [Fact]
    public async Task SkipCurrent_LateLeafSucceeded_RecordedAsSucceeded_NotSkipped()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跳过太迟",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        var job1Calls = 0;
        boundary.OnSubmit = req => { runId = req.Run.RunId; return Task.CompletedTask; };
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId == "job-1" && Interlocked.Increment(ref job1Calls) == 1)
            {
                awaitEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return "succeeded"; // 确认阶段：节点实际已完成（跳过请求到达太迟）
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "succeeded" }); // 留痕为成功而非跳过
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task TerminalAction_Throws_RunFailed_PendingCompletionRetained()
    {
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "收尾失败",
            Nodes = [DragonNode("n-1", "配置A")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("关闭游戏") } }],
        }).Split('|')[1];
        var (runner, _, terminal) = MakeRunner();
        WorkflowRunState? stateAtExecution = null;
        PendingCompletionRecord? pendingAtExecution = null;
        terminal.OnExecute = (_, run) =>
        {
            stateAtExecution = run.State; // B5：收尾执行期 = Completing + 意图已落盘
            pendingAtExecution = run.PendingCompletion;
            throw new InvalidOperationException("模拟关机失败");
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Completing, stateAtExecution); // 先落盘收尾意图再执行
        Assert.NotNull(pendingAtExecution);
        Assert.Equal(WorkflowRunState.Failed, run.State); // 收尾失败不记成功
        Assert.NotNull(run.PendingCompletion); // 待执行收尾保留（恢复扫描标 Unknown，禁止自动补发）
        Assert.Contains("收尾动作", run.Note);
    }

    [Fact]
    public async Task Pause_DuringTriggerWait_PersistsPaused_ResumeContinuesToSuccess()
    {
        var future = DateTimeOffset.Now.AddHours(2);
        var doc = new WorkflowDocument
        {
            Name = "暂停恢复",
            Nodes = [DragonNode("n-1", "配置A")],
            Triggers = [new WorkflowTrigger
            {
                Kind = "trigger.time",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement(future.ToString("HH:mm")),
                    ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("nextDay"),
                },
            }],
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        var delayCalls = 0;
        var gate = new TaskCompletionSource();
        var (runner, boundary, _) = MakeRunner(delay: async (_, ct) =>
        {
            if (Interlocked.Increment(ref delayCalls) == 1)
            {
                gate.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // 首次等待挂起（待暂停打断）
            }
        });

        var task = runner.StartAsync(workflowId);
        await gate.Task;
        string? runId = null;
        for (var i = 0; i < 100 && runId is null; i++)
        {
            runId = _runs.List().FirstOrDefault()?.RunId;
            if (runId is null) await Task.Delay(10);
        }
        runner.RequestAction(runId!, WorkflowRunAction.Pause);
        var paused = await task;

        Assert.Equal(WorkflowRunState.Paused, paused.State); // 暂停 ≠ 停止
        Assert.NotNull(paused.Wait); // 等待记录保留（恢复后重排）
        Assert.False(paused.TriggerConsumed); // 触发未消费，恢复重等
        Assert.Empty(boundary.Submissions);

        var resumed = await runner.ResumeAsync(runId!);
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        Assert.Single(boundary.Submissions);
    }

    [Fact]
    public async Task ScheduledLoopWait_AppliesAfterFilterSkippedRound_B9()
    {
        var wednesday0500 = new DateTimeOffset(2026, 9, 16, 5, 0, 0, TimeSpan.FromHours(8));
        var doc = new WorkflowDocument
        {
            Name = "循环等待",
            Nodes =
            [
                new WorkflowNode { NodeId = "n-off", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "配置A", ConfigKey = "配置A#k", Revision = "rev-1" },
                    Strategies = [new WorkflowStrategy
                    {
                        Kind = "condition.weekdays",
                        Params = new Dictionary<string, System.Text.Json.JsonElement>
                        {
                            ["days"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "周一" }),
                            ["dayBoundary"] = System.Text.Json.JsonSerializer.SerializeToElement("localMidnight"),
                        },
                    }] },
                DragonNode("n-on", "配置B"),
            ],
            Loop = new WorkflowLoop
            {
                Mode = "scheduled",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("06:00"),
                    ["skipAcrossDays"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                },
            },
        };
        var workflowId = SeedFlow(doc).Split('|')[1];

        var capturedWaits = new List<TimeSpan>();
        using var stopAfterTwo = new CancellationTokenSource();
        var (runner, boundary, _) = MakeRunner(now: wednesday0500, delay: (d, _) =>
        {
            capturedWaits.Add(d);
            return Task.CompletedTask; // 手动时钟快进
        });
        boundary.OnSubmit = _ =>
        {
            if (boundary.Submissions.Count >= 2) stopAfterTwo.Cancel(); // 跑完两轮即停
            return Task.CompletedTask;
        };

        var run = await runner.StartAsync(workflowId, stopAfterTwo.Token);

        Assert.Equal(WorkflowRunState.Cancelled, run.State); // 外部停止收尾
        Assert.Equal(2, boundary.Submissions.Count(s => s.NodeId == "n-on")); // 两轮 B 各执行一次
        Assert.Contains(capturedWaits, d => d == TimeSpan.FromMinutes(60)); // B9：上一轮以过滤跳过结束，新一轮仍经轮次起点等待（05:00→06:00）
        Assert.Equal(2, run.NodeOutcomes.Count(o => o is { NodeId: "n-off", Result: "skippedFilter" }));
    }

    [Fact]
    public async Task SubmitUncertain_RunUnknown_IntentSubmitted_NoAdvanceNoTerminal()
    {
        // R4.8 一轮 B1：受理与否不可考 → Unknown 停驻（不按拒绝推进、不触发收尾、游标不动、在飞事实保留）
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "提交不可考",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
            Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
        }).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner();
        boundary.SubmitOverride = BoundarySubmitResult.UnknownWith("传输异常且按幂等键对账未命中");

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Unknown, run.State);
        Assert.Single(boundary.Submissions); // 不推进到下一节点
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "unknown" });
        Assert.Equal(SubmitIntentState.Submitted, run.CurrentSubmission!.Intent); // 发送已尝试事实，不按拒绝
        Assert.Null(run.CurrentSubmission!.ObservedTerminal); // 在飞事实保留（恢复扫描据此标 Unknown）
        Assert.Equal("n-1", run.Cursor!.NodeId); // 游标不推进
        Assert.Empty(terminal.Actions); // D10：未知不触发收尾
    }

    [Fact]
    public async Task AwaitUncertain_RunUnknown_ObservedTerminalStaysNull()
    {
        // R4.8 一轮 B1：终态查询不可考 → Unknown 停驻，ObservedTerminal 保持空（不猜失败）
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "终态不可考",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();
        boundary.OnAwaitEx = (_, _) => Task.FromResult(BoundaryTerminalResult.UncertainWith("等待终态超预算"));

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Unknown, run.State);
        Assert.Single(boundary.Submissions);
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "unknown" });
        Assert.Null(run.CurrentSubmission!.ObservedTerminal);
        Assert.Equal(SubmitIntentState.Accepted, run.CurrentSubmission!.Intent); // 已受理事实不丢
    }

    [Fact]
    public async Task SkipCurrent_RequestsRemoteCancel_BeforeConfirmObservation()
    {
        // R4.8 一轮 B3：确认链先请求远端取消一次，再纯观察确认；cancelled → skippedUser
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跳过发取消",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        var job1Calls = 0;
        boundary.OnSubmit = req => { runId = req.Run.RunId; return Task.CompletedTask; };
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId != "job-1") return "succeeded";
            if (Interlocked.Increment(ref job1Calls) == 1)
            {
                awaitEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // 叶子取消 → OCE → 确认链
            }
            return "cancelled"; // 确认观察：远端取消已确认
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.SkipCurrent);
        var run = await task;

        Assert.Contains("job-1", boundary.CancelRequests); // 取消请求已发出
        Assert.Contains(run.NodeOutcomes, o => o is { NodeId: "n-1", Result: "skippedUser" });
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
    }

    [Fact]
    public async Task Stop_InflightJob_RequestsRemoteCancel_NoteKeepsUnconfirmedFact()
    {
        // R4.8 一轮 B3：Stop 对在飞作业 best-effort 远端取消；本地 Cancelled + 未确认标注，事实保留
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "停止在飞",
            Nodes = [DragonNode("n-1", "配置A"), DragonNode("n-2", "配置B")],
        }).Split('|')[1];
        var (runner, boundary, _) = MakeRunner();

        var awaitEntered = new TaskCompletionSource();
        string? runId = null;
        boundary.OnSubmit = req => { runId = req.Run.RunId; return Task.CompletedTask; };
        boundary.OnAwait = async (_, ct) =>
        {
            awaitEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 直到运行令牌取消
            return "succeeded";
        };

        var task = runner.StartAsync(workflowId);
        await awaitEntered.Task;
        runner.RequestAction(runId!, WorkflowRunAction.Stop);
        var run = await task;

        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.Contains("job-1", boundary.CancelRequests); // 在飞作业已请求远端取消
        Assert.Contains("未确认", run.Note); // 远端未确认事实标注
        Assert.Null(run.CurrentSubmission!.ObservedTerminal); // 不猜远端已停
    }

    [Fact]
    public async Task Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences()
    {
        // 真实 Runner/loop 复现 BO-6 R19 + R21 F4 与 BO-7 R21 F2：P1 先停驻，
        // 被删除期间 A、Q、B 依次推进并让 Q 停驻；最终 P1 同身份插回完成锚前、Q 留在完成锚后，
        // 最新游标 R 保持可定位。较晚安全的 Q 不得遮蔽较早冲突的 P1；恢复需重驱两处并过滤 A/B。
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "删除后插回的多个停驻",
            Nodes = [DragonNode("lead", "配置前置"), DragonNode("P1", "配置P1")],
            Terminal = [new WorkflowTerminalAction
            {
                Kind = "terminal.completionAction",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("误触发收尾") },
            }],
        }).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner();
        var submitted = new List<(string NodeId, int SequenceIndex, int LoopIteration)>();
        string? waitNode = "P1";
        var waitLoop = 0;
        boundary.OnSubmit = req =>
        {
            submitted.Add((req.Occurrence.NodeId, req.Occurrence.SequenceIndex, req.Occurrence.LoopIteration));
            boundary.SubmitOverride = req.Occurrence.NodeId == waitNode && req.Occurrence.LoopIteration == waitLoop
                ? BoundarySubmitResult.WaitWith("测试夹具要求本地停驻")
                : null;
            return Task.CompletedTask;
        };

        string Revise(params string[] ids)
        {
            var revision = _workflows.List().Single(x => x.WorkflowId == workflowId).Revision;
            var document = _workflows.Load(workflowId);
            document.Nodes = ids.Select(id => DragonNode(id, "配置" + id)).ToList();
            document.Loop = ids.Contains("stop", StringComparer.Ordinal)
                ? new WorkflowLoop { Mode = "immediate" }
                : null;
            return _workflows.Save(document, revision);
        }

        var first = await runner.StartAsync(workflowId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, first.State);
        Assert.Contains(first.NodeOutcomes, o => o is { NodeId: "P1", LoopIteration: 0, Result: WorkflowRunner.LocalWaitResultWord });

        // 删除 P1 后，Runner 必须能走到 A，再在 Q 停驻；P1 的历史义务仍保留。
        waitNode = "Q";
        Revise("lead", "A", "Q");
        var second = await runner.ResumeAsync(first.RunId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, second.State);
        Assert.Contains(second.NodeOutcomes, o => o is { NodeId: "A", Result: "succeeded" });
        Assert.Contains(second.NodeOutcomes, o => o is { NodeId: "Q", LoopIteration: 0, Result: WorkflowRunner.LocalWaitResultWord });

        // 再删除 Q，让 B 推进并在 R 停驻；最终让 P1 位于已完成 A/B 前、Q 位于其后。
        waitNode = "R";
        Revise("lead", "A", "B", "R");
        var third = await runner.ResumeAsync(first.RunId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, third.State);
        Assert.Contains(third.NodeOutcomes, o => o is { NodeId: "B", Result: "succeeded" });

        waitNode = "stop";
        Revise("P1", "lead", "A", "B", "Q", "R", "stop");
        var finalStart = submitted.Count;
        var final = await runner.ResumeAsync(first.RunId);

        Assert.Empty(terminal.Actions); // 冲突不得按普通链尾成功或触发流程收尾
        Assert.NotEqual(WorkflowRunState.Succeeded, final.State);
        Assert.False(final.TailReached);
        Assert.Equal(new[] { "P1", "Q", "R", "stop" }, submitted.Skip(finalStart).Select(x => x.NodeId));
        Assert.Equal(WorkflowRunState.LocalWaitParking, final.State);
        Assert.All(submitted.Skip(finalStart), x => Assert.Equal(0, x.LoopIteration));
        Assert.Equal(1, final.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(1, final.NodeOutcomes.Count(o => o is { NodeId: "B", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(1, final.NodeOutcomes.Count(o => o is { NodeId: "lead", LoopIteration: 0, Result: "succeeded" }));
        Assert.Contains(final.NodeOutcomes, o => o is { NodeId: "P1", LoopIteration: 0, Result: "succeeded" });
        Assert.Contains(final.NodeOutcomes, o => o is { NodeId: "Q", LoopIteration: 0, Result: "succeeded" });
        Assert.Contains(final.NodeOutcomes, o => o is { NodeId: "R", LoopIteration: 0, Result: "succeeded" });

        // 进入下一轮仍不能把 P1@1/Q@1 当成 P1@0/Q@0；旧义务已在第 0 轮被清偿。
        waitNode = "P1";
        waitLoop = 1;
        var nextRoundStart = submitted.Count;
        var nextRound = await runner.ResumeAsync(first.RunId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, nextRound.State);
        Assert.Equal(new[] { ("stop", 0), ("P1", 1) },
            submitted.Skip(nextRoundStart).Select(x => (x.NodeId, x.LoopIteration)));
        Assert.Equal(1, nextRound.NodeOutcomes.Count(o => o is { NodeId: "P1", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(1, nextRound.NodeOutcomes.Count(o => o is { NodeId: "lead", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(1, nextRound.NodeOutcomes.Count(o => o is { NodeId: "Q", LoopIteration: 0, Result: "succeeded" }));
        Assert.Contains(nextRound.NodeOutcomes, o => o is { NodeId: "P1", LoopIteration: 1, Result: WorkflowRunner.LocalWaitResultWord });
    }

    [Fact]
    public async Task Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion()
    {
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "仍有效停驻不得链尾假成功",
            Nodes = [DragonNode("P1", "配置P1"), DragonNode("A", "配置A"),
                DragonNode("B", "配置B"), DragonNode("Q", "配置Q"), DragonNode("R", "配置R")],
            Terminal = [new WorkflowTerminalAction
            {
                Kind = "terminal.completionAction",
                Params = new Dictionary<string, System.Text.Json.JsonElement>
                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("不应执行收尾") },
            }],
        }).Split('|');
        var workflowRevision = seed[0];
        var workflowId = seed[1];

        // 构造防御性恢复快照：旧 TailReached 标志与当前计划中仍有效且未完成的零发送义务并存。
        // 正常路径由上面的 Runner 救援测试推进；本夹具单独证明该持久化不一致只能显式失败。
        var run = _runs.CreateRun(workflowId, workflowRevision);
        run.State = WorkflowRunState.Interrupted;
        run.TriggerConsumed = true;
        run.WorkflowRevision = workflowRevision;
        run.TailReached = true;
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "P1", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "B", SequenceIndex = 2, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "Q", SequenceIndex = 3, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord });
        _runs.Update(run);

        var (runner, boundary, terminal) = MakeRunner();
        var failed = await runner.ResumeAsync(run.RunId);

        Assert.Empty(boundary.Submissions);
        Assert.Equal(WorkflowRunState.Failed, failed.State);
        Assert.True(failed.TailReached);
        Assert.Contains(failed.NodeOutcomes, o => o is { NodeId: "P1", Result: WorkflowRunner.LocalWaitResultWord });
        Assert.Contains(failed.NodeOutcomes, o => o is { NodeId: "Q", Result: WorkflowRunner.LocalWaitResultWord });
        Assert.Null(failed.PendingCompletion);
        Assert.Empty(terminal.Actions);

        // 验收真实 Runner 的持久化结果，而不是只检查 ResumeAsync 返回的内存对象。
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Failed, persisted.State);
        Assert.True(persisted.TailReached);
        Assert.Contains(persisted.NodeOutcomes, o => o is
            { NodeId: "P1", Result: WorkflowRunner.LocalWaitResultWord });
        Assert.Contains(persisted.NodeOutcomes, o => o is
            { NodeId: "Q", Result: WorkflowRunner.LocalWaitResultWord });
        Assert.Null(persisted.PendingCompletion);
    }

    [Fact]
    public async Task Resume_CandidateAndRescueAcrossLoop_UsesFullPlanOrderAndAdvancesThroughRunner()
    {
        // 新修订在已跑完的第 0 轮 anchor 后插入 candidate；仍有效的 park 在第 1 轮。
        // SequenceIndex 较小的 rescue probe 位于更晚 loop，故真实 Runner 必须先执行 candidate@0。
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跨轮次 candidate/rescue 全序",
            Nodes = [DragonNode("anchor", "配置A"), DragonNode("removed", "配置旧"),
                DragonNode("park", "配置P"), DragonNode("tail", "配置T")],
            Loop = new WorkflowLoop { Mode = "immediate" },
        }).Split('|')[1];
        var revision = _workflows.List().Single(x => x.WorkflowId == workflowId).Revision;
        var current = _workflows.Load(workflowId);
        current.Nodes = [DragonNode("anchor", "配置A"), DragonNode("candidate", "配置C"),
            DragonNode("park", "配置P"), DragonNode("tail", "配置T")];
        _workflows.Save(current, revision);

        var run = _runs.CreateRun(workflowId, revision);
        run.State = WorkflowRunState.Interrupted;
        run.TriggerConsumed = true;
        run.Cursor = new WorkflowNodeCursor { NodeId = "removed", Occurrence = 0, LoopIteration = 1, Attempt = 1 };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "anchor", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "park", SequenceIndex = 2, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord });
        _runs.Update(run);

        var (runner, boundary, _) = MakeRunner();
        var submitted = new List<(string NodeId, int SequenceIndex, int LoopIteration)>();
        boundary.OnSubmit = req =>
        {
            submitted.Add((req.Occurrence.NodeId, req.Occurrence.SequenceIndex, req.Occurrence.LoopIteration));
            boundary.SubmitOverride = req.Occurrence.NodeId == "park" && req.Occurrence.LoopIteration == 1
                ? BoundarySubmitResult.WaitWith("到达第 1 轮停驻")
                : null;
            return Task.CompletedTask;
        };

        var resumed = await runner.ResumeAsync(run.RunId);

        Assert.Equal(WorkflowRunState.LocalWaitParking, resumed.State);
        Assert.Equal(("candidate", 1, 0), submitted[0]); // candidate@0 < rescue probe anchor@1 的计划全序
        Assert.DoesNotContain(submitted, x => x is { NodeId: "anchor", LoopIteration: 0 });
        Assert.Equal(("park", 2, 1), submitted[^1]);
        Assert.Empty(resumed.NodeOutcomes.Where(o => o is { NodeId: "candidate", LoopIteration: 0, Result: "waitLocally" }));
    }

    [Fact]
    public async Task Resume_RescueBeforeNextLoopCandidate_UsesFullPlanOrderAndFiltersCompletedAnchor()
    {
        // 锚 A@0 位于循环计划尾，candidate 是 P@1；有效停驻 P@0 全序更早。
        // Runner 必须先重驱 P@0，过滤已完成 A@0，再按计划推进到 P@1。
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "跨轮次 rescue 早于 candidate",
            Nodes = [DragonNode("P", "配置P"), DragonNode("A", "配置A")],
            Loop = new WorkflowLoop { Mode = "immediate" },
        }).Split('|')[1];
        var revision = _workflows.List().Single(x => x.WorkflowId == workflowId).Revision;
        var run = _runs.CreateRun(workflowId, revision);
        run.State = WorkflowRunState.Interrupted;
        run.TriggerConsumed = true;
        run.Cursor = new WorkflowNodeCursor { NodeId = "removed", Occurrence = 0, LoopIteration = 1, Attempt = 1 };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "P", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord });
        _runs.Update(run);

        var (runner, boundary, _) = MakeRunner();
        var submitted = new List<(string NodeId, int SequenceIndex, int LoopIteration)>();
        boundary.OnSubmit = req =>
        {
            submitted.Add((req.Occurrence.NodeId, req.Occurrence.SequenceIndex, req.Occurrence.LoopIteration));
            boundary.SubmitOverride = req.Occurrence is { NodeId: "P", LoopIteration: 1 }
                ? BoundarySubmitResult.WaitWith("到达下一轮停驻")
                : null;
            return Task.CompletedTask;
        };

        var resumed = await runner.ResumeAsync(run.RunId);

        Assert.Equal(WorkflowRunState.LocalWaitParking, resumed.State);
        Assert.Equal(("P", 0, 0), submitted[0]); // rescue P@0 < candidate P@1
        Assert.DoesNotContain(submitted, x => x is { NodeId: "A", LoopIteration: 0 });
        Assert.Equal(("P", 0, 1), submitted[^1]);
    }
}


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs L1-L1390 SHA256=8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6

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


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt L237-L239 SHA256=5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267
Declaration-surface manifest entry added by the new R5.3 24.126 registration text (generated file; 1 of 3). The manifest is a generated 220 KB list of 602 claim lines; only the entries this batch introduced are excerpted. The complete file hash is bound in every evidence source_sha256 and the whole-file regeneration result is proven by receive-claim-surface-regen / -no-env.
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3C169B077F2D8344F9F877CE1F207F7969D2B0C1BD06CECE13C37EB61DC82F081| 3 | **首节点绑定（§12.3 M1）**：父子关系与首节点绑定持久化 | R5.8 实现闭合序列（施工方：宿主实现＋夹具负责人） | 持久化父子/首节点绑定＋「E1 关闭后首节点另行取许可」夹具 | 节点改道门 | **部分已交付·未验收**（[批次四十四] §24.54：父子绑定由门面自行反查写入＋首/后继…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3D08ED876CA155691F693248D0B93254D9E4549AA77AAA3A6899C0306F1146081- **BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT** 仍未并入、仍未闭合；R5.6、R6.1、R6 diff guard 未提前集成。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3D57EDF6804F0853C382DEE2947263C10F966E08EDD2BD730411F39AC6290D2D1| 1 | **P50 负载敏感诊断套件**（33 节点端到端暂停，须覆盖整个用例类） | 独立诊断批（施工方） | 可单独重复运行的负载复现入口＋根因定位＋该类夹具稳定 | 节点改道门保留；Skip 与严格断言不放宽 | **已交付（组件/宿主层）**（[批次四十九] §24.61：负载复现入口（`BGI_R5_P5…


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt L335-L337 SHA256=5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267
Declaration-surface manifest entry added by the new R5.3 24.126 registration text (generated file; 2 of 3). The manifest is a generated 220 KB list of 602 claim lines; only the entries this batch introduced are excerpted. The complete file hash is bound in every evidence source_sha256 and the whole-file regeneration result is proven by receive-claim-surface-regen / -no-env.
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8AD6D948ADF86C971E26BA1EF60E7C52C5837FED5BA6B6C4EA38AE5AEB60D3D01- **R3**（收口轮，**无必改、无未闭合重要项**；3 建议全采纳）：页首「仅含 owner 既有未提交文件」与
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8BB3C28725E15D48B2F7205F587007050DE5591AF604693BA44A00E96F15AC771- 四项原级义务在来源侧由 owner 批准的两次追加独立会诊按**原等级**裁定 closed：BO-6 R19 **MUST**、BO-6 R21 F4 **IMPORTANT**、BO-7 R21 F1 **MUST**、BO-7 R21 F2 **MUST**；两份报告的收尾结论均为「本范围仍有未闭合的 MUS…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8CDDAB59B6593D428556E9311CFB817E513D9AE79607642326FCA912BCDBA1891⇒ 本批**不声称会诊已闭环**，按纪律以「owner 一次性批量裁决项」登记。


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt L453-L455 SHA256=5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267
Declaration-surface manifest entry added by the new R5.3 24.126 registration text (generated file; 3 of 3). The manifest is a generated 220 KB list of 602 claim lines; only the entries this batch introduced are excerpted. The complete file hash is bound in every evidence source_sha256 and the whole-file regeneration result is proven by receive-claim-surface-regen / -no-env.
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFBE89153DB0A570A1832D57B78495F3EC946CA01CC65CD8D3F4F37229ABA16121| **④ 生产时序与新鲜度** | ⚠ 生产解析**已接线**，但新增同步读取（运行台账 + 租约）的耗时、锁交错、以及读取后快照新鲜度/纪元一致性**尚未验证** |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFC153C1C4E1F5B6CFFEBBC5CCB331E2304C86AC1D4838E206C499EA4E2163EA91本批是**接收批**：把已独立交付、并按原等级闭环的 BO-6/BO-7 修复接入茶包主线。不施工 BO-8/BO-9，不提前集成 R5.6、R6.1 或 R6 diff guard，也不重开已闭合的 BO-13、SB21-3 BO-4、SB21-2 BO-10/12。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFCE6B4691F864A07829278D1C3670C068E38976805218538157254EC16F957801| 2 | **P19① 原因码逐次取证**（批次三十二原诊断已按 owner 裁决更新） | R5.8 真实入口与拒绝审计验收（施工方：门面/宿主夹具负责人） | 组件层已验证 `submission_conflict` 释放被拒请求槽位、保留拒绝原因与原未决责任；尚需真实入口端到端核对拒绝映射、审计可查与解除冲突后…


## Review readiness (mechanical claims, not quality verdict)
{
  "opening": {
    "opening_snapshot": "_workflow/wave3-bo6bo7-receive/opening.json",
    "opening_head": "6fd6207e58ec93dcc38645c12131a9a512f5fc69",
    "risk_matrix": "_workflow/wave3-bo6bo7-receive/risk-matrix.json",
    "risk_rows": 9,
    "subagents": "not_used",
    "existing_results": "none",
    "review_round": 2,
    "prior_findings": 3,
    "quality_verdict": "NOT PROVIDED"
  },
  "risk_matrix": {
    "schema_version": 1,
    "batch": "wave3-bo6bo7-receive",
    "rows": [
      {
        "id": "S1",
        "dimension": "state",
        "critical": true,
        "status": "covered",
        "scenario": "恢复记录含持久 waitLocally 历史、且保存游标在新 plan 仍可定位时发起显式 Resume（含删除后同身份插回锚前的舞步）。",
        "expected": "在覆盖修订号前重算恢复点，按 (LoopIteration,SequenceIndex) 计划全序选择最早仍有效停驻；提交序列恰为 P1,Q,R,stop，lead/A/B 各恰一次，不产生虚假成功或终态收尾。",
        "counterexample_ids": [
          "receive-targeted-93"
        ],
        "test_ids": [
          "398aed81-8691-87dd-a9cd-c62780f5f924",
          "113f020c-1c29-e336-4c1a-3a50b69cbcc8"
        ],
        "mutation_ids": [
          "bo6-resume-live-park-v3",
          "bo7-earliest-park-v3"
        ]
      },
      {
        "id": "S2",
        "dimension": "state",
        "critical": true,
        "status": "covered",
        "scenario": "恢复推进段跨越修订重排后落在恢复点之后的已完成 occurrence（同轮次或跨轮次旧轮身份）。",
        "expected": "按稳定身份 NodeId+Occurrence+LoopIteration 跳过本 run 已完成出现；已完成节点不被二次提交，新轮同名出现仍可执行。",
        "counterexample_ids": [
          "receive-targeted-93"
        ],
        "test_ids": [
          "398aed81-8691-87dd-a9cd-c62780f5f924",
          "ec3b4575-60e7-a499-c37f-c9ae12b11e0e",
          "14addbd1-4d7d-814d-ec3d-c070098441ad"
        ],
        "mutation_ids": [
          "bo6-completed-filter-v3",
          "bo7-stable-identity-v3"
        ]
      },
      {
        "id": "S3",
        "dimension": "state",
        "critical": true,
        "status": "covered",
        "scenario": "持久 TailReached 与仍可定位、未完成的停驻义务并存时到达聚合判定。",
        "expected": "聚合结果 Failed 并持久化，保留停驻义务与 TailReached，不创建 PendingCompletion、不执行终态动作。",
        "counterexample_ids": [
          "receive-targeted-93"
        ],
        "test_ids": [
          "113f020c-1c29-e336-4c1a-3a50b69cbcc8"
        ],
        "mutation_ids": [
          "bo6-tail-fail-closed-v3",
          "bo6-tail-persisted-failure-v4"
        ]
      },
      {
        "id": "S5",
        "dimension": "state",
        "critical": true,
        "status": "covered",
        "scenario": "candidate 与 rescue 分处不同 loop 轮次（candidate 较早 与 rescue 较早 两种方向）。",
        "expected": "按 (LoopIteration,SequenceIndex) 字典序选择全序较早者，较晚者由线性推进自然到达，两个义务都不丢。",
        "counterexample_ids": [
          "receive-targeted-93"
        ],
        "test_ids": [
          "1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc",
          "7a665a8f-f8e9-11b4-598f-a1f8d24b805a"
        ],
        "mutation_ids": [
          "bo7-candidate-first-v3",
          "bo7-rescue-first-v3"
        ]
      },
      {
        "id": "S4",
        "dimension": "state",
        "critical": false,
        "status": "covered",
        "scenario": "集成后声明面（R5.3 状态词/门禁词/证据等级行）与源码的一致性检查。",
        "expected": "需要再生时 CLAIM_SURFACE_REGENERATE=1 再生通过，清除变量后守卫通过，清单 SHA 在两次运行间一致。",
        "counterexample_ids": [
          "receive-claim-surface-regen",
          "receive-claim-surface-no-env"
        ],
        "test_ids": [
          "2a110364-66d8-3e33-fbe4-06d907c54f09"
        ]
      },
      {
        "id": "C1",
        "dimension": "concurrency",
        "critical": true,
        "status": "covered",
        "scenario": "恢复推进的重定位写与 Failed 聚合写相对返回/重载的可观察顺序（同一 run 单写者）。",
        "expected": "每次重定位后更新运行记录；Failed 在返回前持久化；重新 Load 可观察到同一状态（不出现内存 Failed、盘上 Running）。",
        "counterexample_ids": [
          "receive-targeted-93"
        ],
        "test_ids": [
          "113f020c-1c29-e336-4c1a-3a50b69cbcc8"
        ],
        "mutation_ids": [
          "bo6-tail-persisted-failure-v4"
        ]
      },
      {
        "id": "C2",
        "dimension": "concurrency",
        "critical": false,
        "status": "not_applicable",
        "scenario": "多有效停驻跨轮次推进、以及多实例/跨进程对同一运行的并发写。",
        "expected": "本接收批不作结论。",
        "reason": "多有效停驻跨轮次推进与多实例/跨进程并发写属 BO-9 R34 F5（IMPORTANT）与 BO-8/BO-9 范围，本接收批按范围不并入，故本批不对此类场景作任何结论；同 loop 内的多有效停驻已由 S1/S2 的真实 Runner 事实覆盖。"
      },
      {
        "id": "F1",
        "dimension": "fault",
        "critical": true,
        "status": "covered",
        "scenario": "停驻/完成身份在修订重排后被删除或同身份重新插入，SequenceIndex 发生变化。",
        "expected": "完成判定只用 NodeId+Occurrence+LoopIteration，不因 SequenceIndex 变化误判完成或漏判已完成。",
        "counterexample_ids": [
          "receive-targeted-93"
        ],
        "test_ids": [
          "398aed81-8691-87dd-a9cd-c62780f5f924",
          "ec3b4575-60e7-a499-c37f-c9ae12b11e0e",
          "14addbd1-4d7d-814d-ec3d-c070098441ad"
        ],
        "mutation_ids": [
          "bo7-stable-identity-v3"
        ]
      },
      {
        "id": "F2",
        "dimension": "fault",
        "critical": true,
        "status": "covered",
        "scenario": "恢复点无法构造，或停驻身份在当前 plan 不可定位（异常/退化路径）。",
        "expected": "fail-closed：不得静默按链尾成功或吞掉可定位的未完成停驻义务；聚合保护将其转 Failed。",
        "counterexample_ids": [
          "receive-targeted-93"
        ],
        "test_ids": [
          "113f020c-1c29-e336-4c1a-3a50b69cbcc8"
        ],
        "mutation_ids": [
          "bo6-tail-fail-closed-v3"
        ]
      }
    ]
  },
  "review_control": {
    "existing_results": {
      "decision": "none",
      "reason": "来源独立交付的 93/93、1562/2/0/1564 与 8 项突变均绑定隔离 worktree 的字节，不是本主线集成版本的运行结果；本批必须在集成后的主线 HEAD 重跑定向、全量与突变，不能复用为当前回归证明。"
    },
    "review_round": 2,
    "prior_findings": [
      {
        "id": "R1-IMPORTANT-1",
        "severity": "important",
        "source_review_evidence_id": "receive-consult-outcome-v1",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "verify-intake-equivalence",
          "verify-import-delta",
          "verify-post-import-delta"
        ]
      },
      {
        "id": "R1-IMPORTANT-2",
        "severity": "important",
        "source_review_evidence_id": "receive-consult-outcome-v1",
        "disposition": "candidate_fixed",
        "repair_evidence_ids": [
          "verify-regression-outcomes",
          "receive-testid-comparison-summary"
        ]
      },
      {
        "id": "R1-SUGGESTION-1",
        "severity": "suggestion",
        "source_review_evidence_id": "receive-consult-outcome-v1",
        "disposition": "accepted",
        "reason": "D1 comment scope widened to ~1400-1403 in findings.md (no source edit, deferral unchanged); the 93-entry comparison and compact mutation/hash chain were added as verification/targeted-93-ids.json and verification/mutation-verification.json."
      }
    ],
    "criticality_reason": "",
    "subagents": {
      "decision": "not_used",
      "reason": "本接收批的验证主体是把来源修复导入后由唯一写者重新执行定向/全量回归与 8 项反向突变（Runner 为共享状态链，设施计划禁止多子 Agent 并行改代码或操作测试产物）。固定开工 ref 上的只读子 Agent 只能重derive 两名独立审查者已在同一字节上给出结论的材料（会诊报告、突变 TRX、testId 差集），无新增独立信息，故不派遣。"
    },
    "repair_batch_id": "wave3-bo6bo7-receive-repair-r1"
  }
}

## Generated evidence overview (mechanical only)
{
  "tests": {
    "_workflow/wave3-bo6bo7-receive/regression/final/targeted-93.trx": {
      "total": 93,
      "passed": 93,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/regression/final/assistant-full-1564.trx": {
      "total": 1564,
      "passed": 1562,
      "failed": 0,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/wave3-bo6bo7-receive/regression/claim-surface/regen/claim-surface-regen.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/regression/claim-surface/no-env/claim-surface-no-env.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/baseline/assistant-full-baseline.trx": {
      "total": 1560,
      "passed": 1558,
      "failed": 0,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-resume-live-park-v3/baseline/bo6-resume-live-park-v3-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-resume-live-park-v3/mutant/bo6-resume-live-park-v3-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-resume-live-park-v3/restored/bo6-resume-live-park-v3-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-completed-filter-v3/baseline/bo6-completed-filter-v3-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-completed-filter-v3/mutant/bo6-completed-filter-v3-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-completed-filter-v3/restored/bo6-completed-filter-v3-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-tail-fail-closed-v3/baseline/bo6-tail-fail-closed-v3-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-tail-fail-closed-v3/mutant/bo6-tail-fail-closed-v3-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-tail-fail-closed-v3/restored/bo6-tail-fail-closed-v3-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-candidate-first-v3/baseline/bo7-candidate-first-v3-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-candidate-first-v3/mutant/bo7-candidate-first-v3-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-candidate-first-v3/restored/bo7-candidate-first-v3-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-rescue-first-v3/baseline/bo7-rescue-first-v3-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-rescue-first-v3/mutant/bo7-rescue-first-v3-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-rescue-first-v3/restored/bo7-rescue-first-v3-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-earliest-park-v3/baseline/bo7-earliest-park-v3-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-earliest-park-v3/mutant/bo7-earliest-park-v3-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-earliest-park-v3/restored/bo7-earliest-park-v3-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-stable-identity-v3/baseline/bo7-stable-identity-v3-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-stable-identity-v3/mutant/bo7-stable-identity-v3-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo7-stable-identity-v3/restored/bo7-stable-identity-v3-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-tail-persisted-failure-v4/baseline/bo6-tail-persisted-failure-v4-baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-tail-persisted-failure-v4/mutant/bo6-tail-persisted-failure-v4-mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/wave3-bo6bo7-receive/mutations/bo6-tail-persisted-failure-v4/restored/bo6-tail-persisted-failure-v4-restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    }
  },
  "comparison": {
    "identity_basis": "testId; provider changes require manual reconciliation",
    "added_ids": [
      "113f020c-1c29-e336-4c1a-3a50b69cbcc8",
      "1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc",
      "398aed81-8691-87dd-a9cd-c62780f5f924",
      "76ead242-85b8-7dc7-3c8a-579e6740c3c7",
      "7a665a8f-f8e9-11b4-598f-a1f8d24b805a",
      "90289ea3-1298-e377-5adf-bed75acd6fed",
      "c9b4acef-4348-1bd4-9f96-d3adfec68fca",
      "ce995f4c-484d-a224-3003-9641fa1549d2"
    ],
    "removed_ids": [
      "077c8f75-7e70-9bd3-24d2-ab5b2e0f340a",
      "71655608-dafa-0dad-93d1-6e1a857f7a62",
      "824155ca-d685-4a01-ff0e-b72a09954715",
      "f228c3af-9398-da9e-a651-19b8d435ffc3"
    ],
    "changed_ids": [],
    "unchanged_ids": [
      "00142b57-1df8-928f-2651-3cbbb3206447",
      "0029f7b4-4cbb-e63f-7b5a-c30fa71e3281",
      "003a600a-a77b-ea57-dac9-336c7597f3a1",
      "003d5177-4eb2-cf90-dfc0-0922617f3967",
      "004be13c-df7b-6dce-dc23-8f25831c275a",
      "00561123-4e38-0327-8a59-60c754b57e14",
      "009197d2-4962-c363-1781-ec974087b085",
      "00bae332-096a-6738-aa59-fa898eb9d249",
      "00f53e62-c820-0cfc-f1b8-92d3e07e3280",
      "00fd1f0c-d2db-d03a-ec3d-4c1fa2db9c79",
      "01079a79-95f7-8058-b3d3-c07611ef62d6",
      "0147a014-80d6-daff-6fa2-066622dd1389",
      "0189c645-a3c5-e893-d377-56937c56d6ba",
      "019a9877-8f34-5643-0454-4b04bbee58a2",
      "01bd8e51-65e1-bf78-ec97-394754066ce7",
      "02121445-9cfa-e6ae-5a08-f0096bf979e5",
      "021b7f9f-0aee-4bb3-82c2-cd0a485f48e5",
      "0223a883-d0f3-d8be-d213-a74a83e4f9f3",
      "02305864-f10b-4e97-86d7-e604303554de",
      "023359c9-267e-2daf-a7cb-411d05b8545e",
      "024e5643-ef97-1f6e-5b7f-8200ca550fe7",
      "025141ac-b6a6-7938-e786-37c41145f43a",
      "02d9d50c-8920-a196-ab6a-c853befc2e4b",
      "0315f37c-45f6-8125-cd57-8e9658b09a66",
      "03221eac-6101-edd7-8229-82e79bab7090",
      "034f5f27-dd97-f4ca-61d5-0a63fc519270",
      "0366307a-5837-3de2-867a-0aede6702d98",
      "03ee9dd3-83e0-5921-6230-80e96ef329a5",
      "04442ed7-ad82-465e-5627-36fe971841df",
      "045de764-8532-677d-4ced-5a18a58883a5",
      "046edc8b-21d9-ee32-c595-04fbca9472b5",
      "0495c5c2-f2bf-78cd-28e4-54742d868691",
      "04af6a23-eaa4-291f-b8d4-35f990558b0d",
      "0510bf67-65ea-147d-7277-c229d2ec2b59",
      "052784c0-6d50-5e34-ab72-0ed54588bfb6",
      "054386e2-c6fd-49a3-d5c2-de430858192c",
      "054abd8e-5417-4420-236d-48c480a779be",
      "054b652d-c40f-684d-f64e-0a22289fe986",
      "0571ad29-6729-373b-9dfc-e398f772fd17",
      "057ecaf9-acdd-0fb3-b353-5fea565edbdc",
      "05c5442b-1658-6289-be67-54e1b3d7fec8",
      "05fb4393-9d22-27a2-41d0-ee4f8727f8d0",
      "05fface1-85ac-b4ab-1c49-1097352578a6",
      "06492130-398c-399f-6e68-527b7b5bc499",
      "067149f9-0370-4184-d971-d15f695706a2",
      "06a8c9f2-fa4d-c63c-2244-7747daa4bc2f",
      "06cfa5f6-a7a8-1274-202b-4ea3d454f210",
      "06d16786-48d0-02c8-6d54-afd6eb30370d",
      "06dba09a-e49b-c2fe-9402-b1e827f6da92",
      "06f9f636-aaad-06a7-4e32-a78785d62290",
      "078165f1-6c31-a760-fed6-de6958300469",
      "0791e4d4-2d59-bd27-5e2d-0abfc4c478d8",
      "07a9ab30-beff-e25a-f695-ea667ef86527",
      "07fc34dd-5e13-1688-f973-796a9039381a",
      "08014a98-94c7-5c1b-85a5-711be1f776c4",
      "0804af87-f343-333f-61d8-ecc9d77ea5f3",
      "0808d2a7-164c-c1ad-9ddd-d2a377128055",
      "0871427e-dcbe-6848-48d8-aa138c852ce1",
      "0878fd71-2dda-a1af-e169-e42d2a96a5df",
      "08871413-002b-e4e4-bc40-b7a852c49f37",
      "08a0ae26-bbb0-4758-a916-604b6207168f",
      "08f1c7c1-f15d-3e4c-7238-12c527ae5213",
      "08f4e732-b1d6-2bb4-bd69-9137ccd7e904",
      "0927c39f-e0cf-acb6-30ee-ed6f7d2b048c",
      "095a3ac4-3dac-a8b7-0057-a40a703e7058",
      "095abd7d-263f-ade9-f942-1bc1430e7470",
      "099829df-2e76-bdfd-f274-26b2c9a6a5d3",
      "09a05ca4-4ace-0785-981a-184674460496",
      "09a61b22-8045-cbcb-8acf-b25c18cb2848",
      "09fe8795-85ae-161f-13de-b462eae553e2",
      "0a05ef84-0523-4a1f-e4e7-2188426ffb7b",
      "0a33feed-7e0a-c4fe-19b5-fb778bc0a5ec",
      "0a3d044c-ed18-dd73-584b-75754bf5815e",
      "0a43a784-9763-43ca-71d5-c7fa3ef8e364",
      "0a5455aa-4a2d-3d48-3283-452a5dba23b8",
      "0a889cb7-3bce-7150-66a8-55fc4a4ddf5f",
      "0aaeca06-c904-2318-3c17-4850fac4875c",
      "0ad18e39-1bac-3b01-70da-1861a75fdd8d",
      "0ad96afc-4b16-be75-51fc-47c360770156",
      "0af21ded-4f28-aac4-d2e5-fe525dfa2a11",
      "0b970562-a75b-ca3f-5720-58fa0cdb7a06",
      "0b9712bb-ebf9-4b53-7dc1-dc96a0be7129",
      "0bc18e5b-5778-0c88-a844-a1e45ca5923e",
      "0bf48d71-bf36-69d4-3ce9-b95b0a390e8e",
      "0bf9c977-664e-5ffe-c39c-c2dced02d6b3",
      "0bfa7728-821b-ed8c-3cf9-4a6cac8c6553",
      "0c1d538d-bf33-7474-c7b7-e4443ff168f7",
      "0c4adbe0-49c7-c8cf-a943-6701e7b4ac4c",
      "0ca9b55e-bbbd-4256-2736-b43bc4c9e04c",
      "0ce085f0-0e7e-2726-1397-71c4505cae11",
      "0cec4223-d71b-e7dc-d677-c175650edc03",
      "0d052212-97ab-692f-e1a7-11c43c1abfb7",
      "0d3d066f-c4a5-b9b7-efff-855452aa5b5c",
      "0d5cc7f5-04b8-111a-8a9b-8727025fdb5f",
      "0d5d4664-d63a-36dc-c4b7-e80a1ea41c29",
      "0d5d5b4b-c48d-6c1c-26e2-85f4147fe8f8",
      "0e1187d5-6686-1d05-0e83-e82e837b7271",
      "0e4105d6-0c09-6fbc-3cb2-dc172274d528",
      "0e419e65-ea97-9104-dc61-306ad26adf4b",
      "0e45c80c-838c-ea53-95b3-8566619958bd",
      "0e6f7184-5df0-48a4-50fd-f4114cc0615d",
      "0e893e81-3b49-a44c-7d41-c56d0fd6eff3",
      "0e8c34cf-fcb8-7d9a-5214-ada45efcfb98",
      "0e9cfdd0-9994-a02f-ab43-d87f585627b7",
      "0e9d82b0-bf7f-3cad-4ad7-b619035603d3",
      "0f1b4b43-2174-bb8e-2959-449657fcc9d8",
      "0f28ef63-1962-b2c7-ac46-e277ebe8f527",
      "0f2cb31f-05b0-568e-9d26-4a686b620ddf",
      "0fe6e276-2320-ac40-58b3-3a84023b9b58",
      "0fed73b3-5124-3481-43ee-ef3bddd56b86",
      "0ff4fb0e-be67-5083-a7fe-24a472139116",
      "0ffcb24a-3e07-098e-ebf0-bfa83871e165",
      "101929e7-d0dc-7453-c004-ed9714a6599e",
      "1024a001-8280-110a-3f94-41e4229b1de3",
      "102caee8-97ad-b1e9-2cc2-2de07015ea43",
      "1085391a-daa8-0a43-1e8e-19fe0dade5f7",
      "10b11358-0d38-ac41-3599-6e9c6e13826f",
      "10c6a8cc-f33f-af9c-7181-18960c333def",
      "10c706cd-f9e7-e2b8-df67-bca171864646",
      "10dfd457-b43a-77c1-5c41-6bea79edd369",
      "10e0235f-7546-84da-a25e-bb8a80fe025c",
      "10fc23c2-fa03-9d03-a34c-502d5972cb65",
      "11a8b597-c214-e249-f365-608e6fa633cd",
      "11c28d5e-d922-1dfd-ea48-4a52ddf39535",
      "11ce9403-f420-8054-c42e-70055a934c9f",
      "11d6262f-352d-e185-ac7e-ce17d38c0354",
      "11f522a8-241f-d91c-9304-1c32e6cd57c6",
      "121142e7-9bdb-3594-c6a0-9add18855844",
      "129fd047-3028-277b-c599-fbab9cafcc5b",
      "12a70f37-cf12-7de6-de6a-fd841495405a",
      "12d0e8ad-db9c-edf9-ee94-b64afd4db362",
      "131d17e6-b057-99df-39e8-01db618f7737",
      "13530bff-f176-5745-f265-8aaa2adce3e3",
      "1357f438-f8fd-ed43-413c-0a939516552b",
      "13a5bbe8-89f2-3d01-1a68-1c54ced7eccd",
      "14164b6a-cc69-3859-eee8-0630c138ce28",
      "141c3871-f4d2-0409-8fd8-74790a37f76a",
      "142ef5f1-b79f-c83a-cdce-c15299459e48",
      "1440192f-f2a7-024e-6ff0-c85606d3f668",
      "144e77c8-dde4-b90e-6ef4-67c275627d02",
      "148d427e-7143-588b-09e9-0074651b3590",
      "14addbd1-4d7d-814d-ec3d-c070098441ad",
      "14b12c92-31c4-d84d-5129-de01fac6b561",
      "14c7717c-4fab-0f2f-9e2b-5efcba4b124a",
      "14dd3e0f-2c52-548c-96f8-a12c8084c3c2",
      "151ac447-4101-a692-a5c2-d6b1f50b513b",
      "159174ae-6a51-5cfc-c09f-6f3bc06eaeb2",
      "15a13e67-cbe8-66f2-3b44-3878d536d5df",
      "15c2ace4-f76d-791f-78a2-7cec261109bd",
      "15dbe3b0-f449-f21b-aeb9-3d77ff171fee",
      "15f6117e-b614-0b91-832e-114d26b1209e",
      "1619b2b0-813d-3b20-086b-eb87ee62d0ff",
      "164246a2-ce6a-6430-5a57-7d6f1add27da",
      "16672732-edc7-4bb8-ca13-466826858396",
      "16860feb-8f9b-5ac1-d91a-8eb67eb8059f",
      "16a1b797-7b4b-37cd-339b-efbee393adc2",
      "16c35e31-f480-a72b-fc90-21246e4e2c42",
      "1716ee02-bfe0-2c6c-9e01-e6224bb95048",
      "1795f64c-da77-e8d7-2553-f809a15ead01",
      "17b632b8-4769-8870-9bcd-c783e73d90cc",
      "17fe3ac0-768a-8f20-c800-1122ffca4db4",
      "1845a269-edde-acc6-2863-aa8fda4ab402",
      "18b4a3db-dc7a-ea36-0f33-864f0940aea3",
      "194b3142-c253-a959-de7a-8a1b489c8e47",
      "194fcedb-5e86-679a-1cd6-c4485e36ae7c",
      "195e2102-a019-b234-3594-76fd527dafba",
      "197592fe-b175-34ac-9021-b4baa8d3575c",
      "1998488c-f983-de71-f1a9-a210758ec5f0",
      "19cf57a8-3e5d-df79-63d9-0f0c3007126c",
      "19e5baf9-b263-6b1f-5c8f-6ef294a14306",
      "19ff07a3-d7ef-2f7a-8f37-f827991c5aa3",
      "1a124453-a7b4-e27f-f8ff-45b6b1679d91",
      "1a26c543-b582-710f-dade-a54ce67ce9da",
      "1a270617-9920-6d68-3bae-9e32762b17c7",
      "1a3ca4a7-d969-e1aa-1ff0-0da847e2786f",
      "1a6fd421-c147-72bd-bf97-4f0f29722972",
      "1a7da4d6-d4b8-d91b-ed46-f34c5420aea7",
      "1a90fbc7-b7c3-9717-624c-6c4e4a6c3945",
      "1a981ac3-e494-c3e3-a8c9-1fa101f6398a",
      "1a9d4afa-5d09-4ef7-d1f7-d1751ee818e5",
      "1aceeccd-0427-4d41-c9de-cd3e54e63c02",
      "1ae03c6c-32f4-4acd-4022-f1433b83c5ff",
      "1b6b91d3-3dbe-ce42-ee59-4e73492fd276",
      "1b770f31-fabf-108e-9cff-f320a659957a",
      "1b7da3d7-39c4-3576-1f76-c13bedc18a2b",
      "1b966097-289d-5c9a-054c-6248d4c5624d",
      "1c3341c6-410d-0a23-173f-e21152311368",
      "1c491d2b-a18c-d800-908f-6f2b75dc0445",
      "1c703abe-5121-67a6-efca-61d127f0f93f",
      "1c9118b6-37e5-b14b-e5e3-402aa46d43fc",
      "1ca019c8-8c12-999c-aaf8-fb8e279ca622",
      "1ca3c6bf-e3ef-4045-269c-159fe8ae537e",
      "1ce1a004-8458-54cc-3ebd-b38929f9326d",
      "1d24eedb-5451-1e47-f74c-681c3a2d4d35",
      "1d68c536-f3ce-838c-2a56-f780b96d365d",
      "1d780516-9761-fa27-2f42-4d437a0b878c",
      "1dc7fe41-0d08-df13-8a38-e8102ce1fda7",
      "1dd4356a-07e8-ca20-9443-79d10b4919b6",
      "1de70f01-c8c7-4564-091c-8a3e9a15b0ca",
      "1e300de3-48b6-274a-aec5-41b657a7cd8e",
      "1e60fe17-d75e-c8f0-b16d-2dfd60edaaa6",
      "1e70d6a8-25e8-7050-1a1c-ecd6821ad4d8",
      "1f39b3bd-736e-11d1-8d3e-b2a7b4e64494",
      "1f648110-8e74-3dd2-a83a-4a5c96391189",
      "1f7911fd-2c6a-cb0d-71c3-fc9dc0693a3d",
      "1f939dd6-118c-65bb-43a4-7b9fbba84d36",
      "1fb4f53a-d7fd-a5fa-a807-ae35455e815e",
      "20011b8a-b299-4d6c-8208-e0d42beeb3d5",
      "200b98fa-0343-fcc4-5eba-8c81d2e7cb1a",
      "2010f702-2803-6ddb-49b4-2c517b3ab370",
      "2025f31f-39d0-43b6-a7b3-152d2285164f",
      "203c29c3-8043-1331-ce78-9891a7030967",
      "205da83c-d919-af08-cfec-07769e61f8a5",
      "20655ff5-3ae8-a011-2e28-f9405d8a1220",
      "20be6204-98ae-8571-9fe0-935a37ac0d9c",
      "20e9bf2f-8fce-e2c8-b678-3bf0893d9003",
      "2117176e-8aad-a2bb-a3c7-2459ccd33c86",
      "21ad2012-15d1-21f5-b558-c3807229324c",
      "21d5dab5-9c3b-8d2a-84ad-256d9b0a03a3",
      "21e83bdb-3a7d-3ac8-9e6a-1931e708118c",
      "21ec47d1-b180-e0f3-673b-647d6807a24b",
      "2211ab88-f62f-e169-f302-bea2c6355975",
      "2231c264-20e5-0421-62a8-f8e9f62ea308",
      "2245f9a6-c1e7-632a-e805-842f1d043f2b",
      "2267dc2e-4528-6c84-e77f-b1fdde4dad29",
      "2277a9d8-fbb9-8230-5298-2d3ec8960c86",
      "22ac99f3-9601-5fb7-d4a6-f2521be8c3a8",
      "2318056c-6e28-881e-7126-51cb9c6d2495",
      "233e2c80-440d-8d50-75a9-49fa2da015f2",
      "234f5a00-94a4-8ccc-7814-b9f85865a769",
      "23b1d4d8-798b-aced-dfee-f034086a46c6",
      "23e435a7-e1ad-669d-f741-b5faf0e3c47a",
      "2400f663-306d-9c34-3603-082688a612dd",
      "24028c13-098e-9d7d-2897-41888cdf195d",
      "24051436-cd3d-6e8b-f80a-5cc6eb6bc99d",
      "2406d19e-fdeb-2531-480b-04db277cdee4",
      "2408b12a-3be2-ed41-141e-f00bf650f0c8",
      "24420979-ecf1-5ecc-c550-e3430be1bdb7",
      "24596fd0-cb59-f8fb-764c-7a86429fbefa",
      "2486ea99-6168-bf04-8bbc-afeb6e5e1171",
      "248d5545-dff4-4a61-4840-f14b3c748475",
      "24a61d65-808c-9cc5-0347-dd0f2c6c6cd7",
      "24babe44-0d56-2504-dcc6-4d65f67e0cc1",
      "24c4b9f5-7a3b-5d1b-78c7-642291dda286",
      "24c8b883-ce39-5d1a-aeaa-95a50e4cc42f",
      "24d56785-d648-36a9-f60d-0ded110425be",
      "24ee8844-8c3c-5434-74d7-b57d3aeadc67",
      "24fddba1-235d-f407-f8fe-396dbd825383",
      "25421dda-36a6-5dc7-c426-bf907d060cec",
      "254a3475-b34c-5a30-4d58-68cc51efad60",
      "256e8bf4-1a23-fe77-66de-4309fa416c59",
      "259feb58-4a10-1216-3c17-14e62e308993",
      "25b25ff2-2389-8c5b-5aab-5b575640bf32",
      "25cfed8c-ec8d-6473-5b81-df7b709e89da",
      "260d9a6e-4e9e-c464-e723-c4ae8e20eaf4",
      "2616dd8c-c42c-6a14-5c29-ba1f9b6ba217",
      "2643e525-faf9-a7b2-961e-d9ae2cf8c670",
      "26ce627b-9b8d-0e56-5659-be317ae7c38a",
      "26d23683-7ab4-1fde-cd65-aedbd21fd783",
      "26df61dd-f5e1-2968-df0e-d1c3964c42e8",
      "26ea9147-8a3e-37b1-7ebf-7a7807578ca0",
      "26eaac62-563c-8447-1dde-e446dae7d965",
      "270ef12e-0f7f-57dc-e19d-fbcb33334708",
      "278249ab-bdf4-4a47-a0fa-c9e5435babea",
      "279a3bb5-25b8-3648-6c4a-1f0b5f0e6d1a",
      "27b7d19d-3d2c-5b8a-1093-79de2489f8ad",
      "27d09088-d434-9ba0-dfe6-99a16f57fa42",
      "27d2ae6c-bcfb-d9ef-698c-e4fd188b776d",
      "27d87b49-979e-9dc4-59bb-e846ab279bc4",
      "27e518f0-41f3-4a16-4164-e9bf5a28c36e",
      "280ee53c-d14f-2e67-2b26-734411ced7b8",
      "287c9ab8-7d2b-6385-1f1a-accbccedb6fa",
      "2889e327-2d87-fa7b-6beb-395a398342b3",
      "28add68c-ab0a-13e4-6b2e-6148412cb5c3",
      "28bfc9f6-f898-5914-8fd6-7458eec24ae2",
      "28e43da3-f3b7-324d-bff5-37796cfff13d",
      "291f7b72-705b-75d2-0a0a-8b4180529f40",
      "293a4953-c134-3300-f941-c26d943864f1",
      "2962cd4d-18b4-7352-f31f-57a3e538da55",
      "296c7867-baa5-9d54-ce57-7c4b0c97416b",
      "29a0500a-c5f6-5c72-434f-b76556fb7a1a",
      "29a8d075-62aa-15c7-957d-d3587a81a54c",
      "2a110364-66d8-3e33-fbe4-06d907c54f09",
      "2aaa57d9-3907-c14e-e49d-438fdfb977cc",
      "2abb8b27-1b51-4540-7944-3805c44969fa",
      "2adea4d4-1a8a-361b-f277-1b8270bcd680",
      "2af0c04a-24c4-5566-bce4-1ef851ad7c02",
      "2b0e2ad8-e5dc-0c76-f30c-91868822f5c7",
      "2b164511-52d5-1204-f0d3-00e398253f9e",
      "2b19c8ed-7268-d67f-bbc5-fc083217fdd4",
      "2b1e40c8-5ca9-9ad6-e47b-4a6b36a3e4cd",
      "2b53b7f9-96c6-e80b-7d81-0483decae077",
      "2b744d25-40e4-3ba5-e0b6-33b95bfdbb19",
      "2b9c2322-12ce-2ce7-5734-a10454f56a01",
      "2b9d16b0-f572-2c80-79e3-509fdf06ce21",
      "2badf7c5-debb-5c22-6f95-bf27e86cb28e",
      "2bd81863-2fcb-9085-046e-50132d053a40",
      "2bebf90b-1388-32fb-19a1-38ac2719bd94",
      "2c1cb34e-2d56-ae1f-905b-543dd5081224",
      "2c93fe06-84fb-f4a9-0d29-79d6ba10263e",
      "2c9676cb-7d12-527f-d69f-cdba9d909a7f",
      "2cda2753-aa21-2c97-c01e-392c9ba4597d",
      "2cfc22cb-30e2-c1e9-5c90-d4c16945eccf",
      "2d64e9fa-4870-1c0a-bf4c-8eff6585e981",
      "2d878291-278e-a262-ee5d-47deb57c0f48",
      "2d8c7290-12dd-431a-a803-50a28e216488",
      "2dd3c698-6b2b-14da-6221-2485b879d3d0",
      "2e0b725d-ea9f-4661-73d6-7cb40701321c",
      "2e64ef86-996c-8445-9928-110c7d31b7c9",
      "2ed0462b-8991-ea6b-da1f-5abf28a1f75b",
      "2eee92f8-1202-30b3-cd99-dcc01dff5d09",
      "2f8b74b2-0086-22c9-70dd-fb2e197b0eb7",
      "2faddd19-e14c-c0c9-fb1c-5b72ad3929ad",
      "2feaadcd-f066-244e-68a1-5189a7be58e9",
      "3014d945-4f35-ea3b-d307-a518df8bfb0a",
      "3052d686-b4dc-3483-eb86-ecaf168cbc7f",
      "307dc5d4-c66a-7067-7ef9-ec0f64cd8a73",
      "30a7044d-add6-ae8a-8532-5e952849f25e",
      "30df229e-c089-b540-d305-d8518b6836e3",
      "313690bb-8394-8f97-cf0f-05dd52a06cf8",
      "31461362-00a7-6c61-e7a5-bed9d1e52c79",
      "31533669-b089-5e91-5472-6f0fb4eb5164",
      "315d0ab0-2f3a-04cb-00e3-cb54f8f3ac36",
      "31b70e08-6e37-abdb-0062-340e6eb798e7",
      "31dc9127-cc23-e16c-b6c6-6c5570ecac7e",
      "3218ca8f-29ff-db16-8baf-c16b0a22ae5f",
      "3229fbc2-2071-bb37-f691-f819dca802af",
      "32318ef8-80e5-4e44-0de9-3cb145e4f9d5",
      "326f3e48-3afd-34ab-ba9b-67c13f6e4304",
      "32850e94-f98e-3a2f-a77d-21e4fca81ce7",
      "3326a896-144b-e150-0eaf-d7b4649c890d",
      "3333b721-a748-723d-5c57-db9a7ea9d91d",
      "3373fa8c-cd25-2766-0d88-ad72137275e6",
      "33bac64d-ddfa-e0bb-decf-bf592a67ed16",
      "33dad0c9-1307-413c-cb94-179dc4670246",
      "34079930-8fcc-359e-24f2-3a026229e749",
      "340f04df-8c38-5c09-b4c3-d9f7c6092404",
      "343f529c-dfbe-dbd4-b1b8-95f1d9a86eb7",
      "344b0c65-8da5-6c06-0284-ac938f7df7cd",
      "34fa88a2-f9e6-960e-1658-9eadbb57d168",
      "35081fa5-66ac-ea0e-24cf-cc6c6a788764",
      "352ed97a-288c-a49d-6fa3-d35cdd1e56ef",
      "353bb81d-87b3-af43-d1cc-8f3663401a5a",
      "35514ca7-980a-1683-6fdb-27d001c0c2d2",
      "3574b1b7-cab8-7636-88c6-18665782f837",
      "359cd6e3-210c-2a9e-e85f-f9c10991b076",
      "35e6a5a0-8c0f-900f-2a21-7e5d75294924",
      "35e758db-191b-115e-92ef-1b006f623f9a",
      "363c2e2f-5db1-212d-1d7d-5a687760f538",
      "364a69a4-a63c-2d18-fc7c-e468bc467ff5",
      "366dac3f-517e-9e95-f338-7aa239a4b828",
      "36c56f22-4298-490a-612e-6fd0ce5d6d36",
      "36d529c3-caba-2e02-719f-341d2f82c13b",
      "36f4e74f-a707-999c-b28a-89e4a567b848",
      "372631da-ed59-9b2b-0feb-20da3b93575e",
      "37e5053b-3332-ac3a-8261-b58ed834e88a",
      "37e6e5f7-6015-c1bf-1f1b-1e8440fab063",
      "37fc37ad-ffe7-6898-7d6a-051fc0ac0fc4",
      "382c07c9-8f34-f6de-8d07-ed7156479eb9",
      "382c98f0-bca0-c42d-8aee-dd4f2e47e361",
      "38be1be3-a879-fd2e-16de-405fc8ff2688",
      "3902c818-f2c4-51b7-0c11-253e93444f0a",
      "39061be3-21b3-5cda-e592-a82a073ad740",
      "390bd7e4-14d2-e6a9-7d72-e2b1405242ec",
      "3914599a-8bcb-17d5-0b53-3717e2394a25",
      "39215c65-fa73-b560-b0e3-ccc7a040f2ef",
      "39871320-b4f2-4cef-1997-f820d63a87c1",
      "39cd6343-7e04-0ac4-f9c7-f6b9807a4d83",
      "39d00e9e-8619-aa98-d691-2323ac234ec7",
      "39fc4642-47cf-6af3-5f76-90eb7f5a8996",
      "3a113757-0d17-a9c0-f008-0bf5577bc058",
      "3a2bd604-11bc-e782-d5b6-428776dd6532",
      "3a2f0b4b-e12b-8516-059b-207b8665adad",
      "3a35269e-a775-65c5-9e06-2f565f927afd",
      "3a51e788-5101-87a9-e879-d7d6edc76e18",
      "3a9bce68-3cf6-2529-c81b-75583ba0778d",
      "3b0e2682-0e17-0367-d2c8-ad471964d61b",
      "3b4e76c5-9c47-1435-e492-742fd7eca5f0",
      "3b61fbb9-0cee-8231-31d8-2f23403be93c",
      "3b6fede2-96fb-b17e-ac27-3f7b3bb91bfa",
      "3bbf9c77-542f-9a9f-771e-af2642175cda",
      "3bc58d94-5b43-8041-4ca2-04d3f796be33",
      "3bee2f37-a2d4-eb06-fb55-c6d458325ebd",
      "3bfba981-8fee-cc77-35da-e21159d7d982",
      "3c379d6c-ed89-0186-ac0e-40e6b7c86979",
      "3c38a48c-65ba-79ff-ab05-a4844fe70076",
      "3c547640-8ec9-4a8b-6233-3755c96b8adb",
      "3cba2927-7bd6-23d9-328e-d3a4c87ade1e",
      "3ccd6cee-4476-b4c8-e28a-13707ff33aeb",
      "3d3ec3e4-9ce2-739d-c9d4-a391984e7d45",
      "3d41e3e0-e7ab-e449-2fa4-9a5c52144ec2",
      "3dc6219b-1b8e-d643-1187-322310dd09f3",
      "3e152489-d2f5-5894-5907-7dd4aa2f7030",
      "3f0862e5-31d8-8bd9-ea3f-2436d733ceb9",
      "3f12d21d-5ff4-4fa1-4395-675022cdddc0",
      "3f66e0ac-bb20-f6f4-36d2-a485bd16d781",
      "3f9723da-820a-6354-4e81-5d930f0e979e",
      "3fa75949-b6c2-f686-0562-da2fd4e42cff",
      "3fc7ba0b-37ed-d9f3-fe23-8ae9c37acaf8",
      "3ff1ddaa-8ae5-4200-5007-7b861c907faf",
      "3ff20d13-2e10-89d6-da68-b64da8ea0d17",
      "400a4789-a5d8-0d12-5e25-2a00a43f256e",
      "402d0e6c-6af6-fd4c-a468-bae2b5cc0038",
      "4036baec-f77c-fffc-4fa9-161f095f51ba",
      "40407c66-3649-a016-1509-4a9cf349d04d",
      "404d1fd3-16c6-f091-96c2-73c8305ae7c5",
      "4070599e-5019-262f-dd8f-8f9c067801de",
      "407bcc93-4676-2806-19d4-7dd6df63df49",
      "4096cc77-05fa-5651-2dc5-f48a97090607",
      "40c0293b-9937-da09-9fa2-671eef57e267",
      "40f863c4-f1f2-e309-40b1-94d8f48d9a75",
      "41190329-7282-9735-ec28-c2bef9d9281f",
      "4149e980-b7b5-9c8b-d4f6-9fe030941f12",
      "4173a1ec-8816-9943-95a3-500f0697a380",
      "417d4984-7726-ea33-9c86-90c3605e3418",
      "4180eb13-1f46-c5ed-be10-6532e8ef0ad3",
      "41c4114f-bbe7-3923-e2da-bead1305bb56",
      "41ddbfc0-ad04-56fd-18dd-0e017c60cc79",
      "421725d6-46d4-3766-8459-3ab8a2bc17a6",
      "427ef644-6de3-d3bf-27cd-c2e17b237ee4",
      "42c55c44-0a5c-43a9-9cc3-12f57caa0be2",
      "430dcdfc-b551-71c9-3476-0d64bdb2d14e",
      "43136988-f212-dbe5-482c-5e06f65fe276",
      "4340bcaf-bb5d-c5fc-62d9-e7cd5565069d",
      "4373a707-431e-4ce4-5cd7-465c9a2f6055",
      "43ccc934-12e7-147f-dba8-33b34d5da1e8",
      "43defdd5-e426-359d-7a6d-5cb31d593b2a",
      "442143a3-9622-f76d-3190-78d5699f03ae",
      "444e6611-0ec8-b868-8daf-8d825ade1f00",
      "4459903b-e5cc-f2fc-2dfe-e17daafd8ec5",
      "44630786-69ca-5b33-b21e-18cab81152aa",
      "449ced58-937d-a8cc-afd5-5b8804d016af",
      "44b0d1e4-9421-9bcd-d759-8e4443e50602",
      "44ce09b4-1218-9db5-fabf-5bac9ddd29e7",
      "44e8eda7-63b5-e964-2143-36145d77d170",
      "44f41471-8e16-e628-1ede-7fd6532e4f00",
      "454b1b43-257d-4db1-6324-724985ff361b",
      "456d701d-ca5c-3f7c-98f7-663e19da003e",
      "45823701-b743-092e-85cd-c4bdbf601721",
      "45a33f9c-2031-0389-53e7-359e6fab768e",
      "45b6cbda-c088-e41d-bff2-2bf19f9a5275",
      "45c44c91-6e42-12d1-9c69-3375953176f3",
      "45d18079-26e3-4c48-d292-4075a8feedb5",
      "45fc69b6-e676-e952-293c-b603bacc3797",
      "46357ab0-c4f5-c189-b193-06a068bc4a97",
      "463fcd56-1cdf-e349-5466-7fda8a3c7875",
      "46466978-6b64-2411-fb5f-79ee88a8efd3",
      "467b04de-c12f-8910-5d78-840c704f2f52",
      "469abd48-059d-1a19-e028-a2c2ada93248",
      "46e9a027-0485-fb66-95b3-b8d26beb016d",
      "4713550b-1811-0188-a37a-b2c75c3e0f6e",
      "4781215b-17d3-9b1f-53e3-aa921efd8b12",
      "47ef8c07-9c6e-db44-c799-dcda2ae91c2e",
      "488a1de0-ad46-da22-8bdc-5db998a92549",
      "488a4974-ed1f-a01c-8c2d-1e3edb4efbdf",
      "489cdcb9-efb5-f3ca-0b2d-ee503e8b8c3b",
      "48c80e2d-f80f-7938-0b67-bb1f367c0333",
      "48d1bfe7-e7f2-4b14-7de2-36df4111c534",
      "48fbc11b-84ea-de07-741a-538865c92f7e",
      "496bfcaa-95da-268b-f489-66a3d9d73f5f",
      "498a7976-527c-fae1-ab3e-f136522b4597",
      "49ed957c-1109-9f53-6f87-ae6a491c78c8",
      "4a1851d8-fc7b-ece2-367f-b0cc60157f47",
      "4a21ef3d-bbc8-6a62-e77a-247f785c1fd1",
      "4a5512d4-628e-d16d-d31e-53d2f7cf3bfc",
      "4a75e252-2d65-8d7a-1b94-1d141cbc5087",
      "4a788db1-907e-8f77-836a-6d86b1f105f8",
      "4a857373-4a6c-848d-b1d0-85c11bd99db4",
      "4aed04fe-e158-2565-22ce-fef239336cbc",
      "4af6f741-9a64-6a93-e8ef-f17d7a969254",
      "4b326417-083c-118b-a51e-51537c8ecefa",
      "4b4a1177-756a-dadf-210f-48e695d0e526",
      "4b911f5b-ae11-c8af-88da-75d4559547f3",
      "4bb621f1-ca1e-513f-48b8-df1b331346c1",
      "4c2e5228-1cf8-5ddb-f9bf-eda6840ecfd7",
      "4c525d70-abc9-0fac-9ad9-2ad2be8f99f3",
      "4cb47db4-4f90-ed10-ec7c-8945d3b855de",
      "4cb96399-456d-c567-088d-863b7508577c",
      "4cea0377-5b28-781e-07c2-bff167001c27",
      "4cf69cf2-790f-1aa1-ca76-c9b53c7c78dc",
      "4d00bd52-058d-068e-2207-9f4b81916eed",
      "4d0eb756-7630-b7aa-98ea-d638c2c702c7",
      "4d197cd2-fed4-bd4f-bdbb-dfec108d5a10",
      "4de35c7e-1035-ecba-7a29-08c3a1d11bf5",
      "4e406f2d-d06c-27d6-a5a7-da653232fd32",
      "4e4aa0fc-b976-01bb-8b0d-ad14bf579e1f",
      "4e61a2fb-96f6-8455-5f6e-588c070e327a",
      "4eaab00a-7ebf-b8b7-3cc6-0802f98d2865",
      "4ee31cea-540d-a10f-621c-810afe820415",
      "4ef0b0ac-6e3d-cb46-5224-3bf06ca10292",
      "4f0a923a-448f-c1e9-d91f-53356f403644",
      "4f5f359e-b4c1-049c-8fda-21ed47742e94",
      "4f624e94-f115-1867-4afb-20396b6350fe",
      "4f63504a-bc7d-6b85-8dbb-3f11f1612f69",
      "4f68dc61-d0cb-9b7d-bdec-7724045ae65e",
      "4f81a805-398d-cb0d-2b4a-e5da4a2eca12",
      "4f9310c1-6997-f66e-681f-dbcd97180e91",
      "4fc58f5d-fcf1-55b8-d061-bfc4d9bb0d8a",
      "50164772-7759-8a68-7282-ddd43ccd05d0",
      "50a452fc-ba6f-51b8-1b9f-e567a2155908",
      "50a5aa43-7b95-8ccb-cdb6-f5b429b91b39",
      "50aaae5e-ef43-1f6a-bbf9-df3072f645b7",
      "50d40c8d-6da5-6f2b-f730-7d51a073cc34",
      "50eab2c0-a859-abfd-2dcb-cb8939a4a3c2",
      "50fab0f2-e62b-c198-667f-4fdf7c5d13a7",
      "51043591-a24f-67d3-5ecd-1decbd19bae2",
      "51063747-e39f-65f8-5810-9eb32848ffdd",
      "511ae5b9-1ed9-609c-034c-570bdfeb732b",
      "5125182b-5295-9070-aded-a378a1aae87b",
      "5136a3ed-0614-0e20-471a-92a892be626a",
      "515b5663-2e0d-ca58-ac21-7f09e168fe24",
      "51789f87-b7ae-7bc1-0b99-13a015114f0b",
      "517f96ad-7e82-6368-ec96-b9b30cbcaec7",
      "51948c78-38e9-4cd3-2236-ec0c2d24311f",
      "519ec9c0-d9c2-d39f-c9f9-c09f513954b5",
      "51b16b2f-884a-6404-4fbb-1bff6c445643",
      "51bb0e7b-5e4e-1de7-aaf0-5f81e3c8ee16",
      "51c61bab-dc31-060e-6844-488bc1e730dd",
      "51eaf727-a6c5-2b1c-f022-8f8ca2ddb128",
      "5216fa2b-a4c1-5c55-9861-d8fb71648a60",
      "52eaa852-1955-65a8-b69a-57f8cb149725",
      "53022e31-a0dd-646c-41c9-44cffa59a92b",
      "5391b1cd-62f0-ceeb-e3af-34b32c17a58f",
      "539369d4-a8c2-deac-54c3-5ec588b6f2ec",
      "53ab48b1-5f5c-a1a9-8155-3c4e89f10323",
      "53dcdb72-1ad9-dc62-ed9d-721f301f1935",
      "53eb4eb0-db1d-9422-6736-c60c089711a9",
      "5433dce8-8750-c648-87ab-80e77a39b186",
      "54e0919e-6773-27dd-3ec6-541ca9ce8cef",
      "54fde810-a27c-3593-8e75-b6c55569d11d",
      "552717dc-2b65-4078-0ff8-c94ded2266d9",
      "557d6155-af65-c4f8-6819-1257fb082f60",
      "55ce4aeb-02f6-5979-b443-9fdf6573f062",
      "55cff473-0845-32fd-88d3-76fe547cf2e3",
      "55edc41a-88cf-7ae9-cbc5-70c83f3d87fc",
      "561f1dfd-7160-9890-7eb7-75af8119de65",
      "5659aaca-894f-1601-c0ee-1f7d5b7790b0",
      "568894e9-bbf1-098b-77cf-969edcfdc624",
      "569bd082-3bd2-91f2-f681-9a644b77bc02",
      "56ae76bd-5a2f-8bb8-8e5e-036e8ac15054",
      "56e30932-a728-01ce-166d-4d55b22c287d",
      "5718a51e-5354-d285-3ec2-cc7ef5ce1e9c",
      "5718efab-8d36-f0d5-fd1e-b9eafe7f9eb9",
      "572ee832-b3b1-844f-322d-c228652edd8d",
      "574ed46b-d41f-8681-a47b-cb7a3bdccb29",
      "575a4bae-9f61-b2f9-141b-4f5e65385506",
      "5798f83f-c30d-a2f4-9f46-2e651a88da2b",
      "57d1d377-d041-c378-76ae-e1e418c64445",
      "57fe38c2-9660-2d37-f43b-e965519a82d4",
      "5807596e-f8c7-ff6f-3a21-72c0cc71a834",
      "583a7801-e84d-a372-3613-6e3932d531e3",
      "5841b333-cbcb-2031-376d-0a4743e5d56f",
      "58874a3b-da75-4e78-5ad2-c7187f42b431",
      "5897ac5e-9d2b-528e-9467-6a1dd1f50af4",
      "58b435b8-edd9-9b85-5131-0db8d5d9f362",
      "58bd3a17-00fb-c0f6-9bcb-04799083d997",
      "58efa830-c3a1-a543-206f-850608936b1d",
      "5908a2d4-569b-afba-7a65-1ad71d5aedb8",
      "5915e9e2-2d56-f99c-7b15-70d030523b07",
      "59262fee-b5d9-698c-0dc4-3cf0ea030c25",
      "597bdfe1-11e3-bf38-36b0-b3db5a805f8f",
      "5a249635-bb80-cfdf-c966-c14eb72273ff",
      "5a6d051d-e95f-0f1d-26c8-6010680d60e3",
      "5ad7d878-c6c5-abc5-264d-b1906b53837b",
      "5af18b30-2e08-5dd9-a0b0-9ff6645386ef",
      "5afc1445-eaac-d698-8d67-f569acf455ab",
      "5afee4a0-06a5-69cd-3e29-743e5c88ff35",
      "5b3bed37-b3b7-4093-5345-26cdd287fc58",
      "5b6c7709-2f58-0f39-4c81-baa9e2f9b58e",
      "5b86cb75-7ee6-52b6-f3c5-977e549b35e6",
      "5b8fca40-657e-0cf6-eca7-24018ae36df5",
      "5b97dad8-34e3-b58a-7da1-cd5d748bcf2b",
      "5bb00c9b-4ff0-433c-a4a8-41a36e0dfd25",
      "5be5c01c-9731-04f1-8bcf-46268ec86057",
      "5c33fd5a-a3e4-f3fb-f35c-97d9d1e5b600",
      "5c404c4f-d9d7-d6cc-ef13-f0618cc4d9c2",
      "5c829a04-e5ac-38c9-c8ba-4c8bb35f4eeb",
      "5c9341a4-949f-1933-495a-c43d4ba5c9a2",
      "5d538487-dd34-5015-e083-9e3feec90b60",
      "5d827860-4fac-6a1e-9c73-70bcc9b2d5d8",
      "5dad80f3-9410-56f9-9eb5-360400aa1a81",
      "5e371183-386e-ff1f-44b2-e14856c0e963",
      "5e3df100-9d59-1da7-0b6d-856f8edc0c30",
      "5e92274f-5ac9-4d59-85b7-faf5fed4d9d2",
      "5eaf26c7-ba7a-54c6-67af-de45face77ef",
      "5eb7f46e-c70a-d5e8-2673-ed417f569d74",
      "5ec818a4-3ae3-8feb-f680-332f53d2a5b1",
      "5f483b3e-7899-b5ff-c225-45498611e751",
      "5f5ea1f0-048a-1358-fcd4-06a1a256e9d0",
      "5f8cfd1f-43c4-354a-b893-2e6846db70bf",
      "5faefae5-5aae-ae0e-7e23-85282a01a1f0",
      "5fb9f07c-d5b2-0bfc-1027-3bbf1a51681b",
      "6057ce2e-a9d7-a5e1-2413-c4388cf15893",
      "6081f44e-def9-f660-5645-459740ac0501",
      "60da87c2-b4a4-9879-3896-79540701c4c6",
      "60dcb186-2a55-593c-ebf6-89f1fba738b6",
      "613e6e7f-598a-4a26-0e47-87939c008d18",
      "61a08ae5-a2fa-8fef-dc10-66d6f0ac262c",
      "61cc391c-137f-19cb-9c17-6e26d82313ce",
      "6239c6ce-32c9-613a-bac4-201d7bee9145",
      "6247a77c-edaf-9c51-7e5c-3bb4be6ac727",
      "624e258b-d3d4-50b6-a36b-ceaf404a56af",
      "62ad6694-f7e5-3bd3-0bf0-666cbaf0fe8c",
      "62d390bb-e91f-98d9-6e78-38ce05d3ea47",
      "62f12bcf-6aa1-1e2f-6e17-5d38431d20e1",
      "6349a170-bcd1-58c5-5b1c-dbc913d73c72",
      "6369e269-4e3b-f6b7-11b3-1f5e4617d98a",
      "63812b75-43f9-e6c5-f7dd-3a43f34c2173",
      "638f70a0-6b8b-e358-07c2-10661c8cebe1",
      "63d91430-9d6b-a92d-d6f3-86c97f9ef9a5",
      "63d96ba2-036e-8678-a572-0e466e6d1f6c",
      "63ddc4f9-9817-6440-2b93-da90cc2bd933",
      "63e4cd04-7e76-1ed7-c89b-3df73a6d6131",
      "63f367d0-1178-dd85-c327-26e5e7e0df0f",
      "640197df-6b9e-a54e-eb7c-0f05e64fd8b1",
      "645d1d12-869b-c7a7-b1c1-3402dbf194dd",
      "646546e2-16f7-ffea-297a-6bdec747adfb",
      "649615b1-05cd-37fd-dd38-a13785289b5f",
      "64aa666f-f101-947f-d529-4c3bfbf3cef7",
      "64c7557a-3207-724d-7865-2fa538f17648",
      "64c94c7e-fc5b-5d0a-8e03-9e04ea9fab37",
      "64df35de-ddf1-f851-01e3-488858776d3f",
      "64df4f0b-c7d8-5c8f-be27-d8984b569408",
      "654e4e1b-b033-ecf7-f97c-5e6c15ae9467",
      "65771a60-3bda-359a-9841-4771faf3a9af",
      "65ef8146-3f59-c61e-42e1-b08ebffc2a3d",
      "65f24447-f478-0cbd-31ab-1c8aae7d7d14",
      "66351d6f-e743-cab8-b6d8-65b400be1e7f",
      "66537de6-cb5b-50f8-b0b1-2efc47af0a32",
      "6658fc83-8cc1-cdcd-066e-b2cc6070608b",
      "666215f0-90f5-fdeb-9a14-fc8af73c53dc",
      "66666b6f-2a25-0878-fcef-ce9ff52ec9de",
      "666a3ad0-e1a5-1112-6c64-d861ec9a34c2",
      "6677c6d8-6c74-dcfa-9d88-2ec013642a1f",
      "66a4053d-8825-bf1d-4185-fd6d2de11b2a",
      "66b8e67f-d7f4-6798-ec24-925da1181981",
      "66d7155f-8f12-bd80-8c98-34dec2d5cc87",
      "66ee9c3d-7c32-96ea-680a-f1968c100541",
      "67377150-1c02-e229-74c1-dbc5a248a58d",
      "67e8a37f-9f0c-56ca-1c0c-689816fb5224",
      "67eddf97-714a-1341-b3de-3d85eb4166c3",
      "67ee60dc-3513-d62a-2fda-386eb835aad9",
      "67fd4085-dd87-668b-c3dc-fbde5b1b1204",
      "68369c1c-79b3-b49c-d144-905c68498cad",
      "6866a48c-b63b-b2ff-d8f5-3d038210898e",
      "68890aa5-589f-15a0-f0d6-ac8f02ea65c3",
      "68a57e5e-e7de-adb5-ef6c-33811eb16aec",
      "68de0cbb-5e09-32fd-6759-6a83cd78fb8a",
      "69215b2b-abd1-ce7a-4e07-c8c33d9fd608",
      "69255b5c-bf55-530b-fd12-141cc4a3103d",
      "692b2392-b635-8e21-db8c-4feb737e19d7",
      "692eee5c-7b6f-a5a5-2557-9f7ca4a08b06",
      "698440bd-c610-636d-33c7-7270f3adbf32",
      "6990b298-d26e-6e8e-802f-957f49ac37d9",
      "699a13c2-4844-6592-e1cd-1855889eb366",
      "699b31f1-4377-7420-1a47-8d9a16a72c5f",
      "69cfcd49-147c-5d6e-4c91-25f49e85a0ad",
      "69d05a3c-330b-371c-382f-c216c8d6bd6c",
      "6a3802fd-313a-1b78-9d0f-0144774be870",
      "6a56f166-6666-1a67-bd2e-b18509dd84b9",
      "6a5f540e-f310-14a4-0c4f-cbaf3b90cdda",
      "6a760905-0829-f683-b8e5-501e7034e0e3",
      "6a8b4411-c778-0621-be9c-42466eca6c8d",
      "6a9ea839-e7ab-6cd2-bd13-65a240f4159b",
      "6acbe7a2-e361-fffb-0cbf-b06cce2b1cfa",
      "6b4cc60d-6802-722f-a450-e6bd868ead29",
      "6b69710a-6a3d-d19b-f3e0-1076ab15c90d",
      "6bc4fcbe-72cc-3e2f-bb49-34cd51d4b073",
      "6be68028-1814-0386-6a21-15d4f1c7ae18",
      "6c143bd6-ea4d-de96-87bd-177b1ed3c357",
      "6c182843-0302-6578-141e-dde2a6fa7467",
      "6c52b4f3-58e4-f7b1-8342-1cf528cca56a",
      "6c73296d-dfb8-2705-f313-d2115e4518c4",
      "6ca463c6-ccc5-283e-efd4-c7cd4b979420",
      "6ce5dbd7-60cc-888f-7a5d-5586e3b70a58",
      "6d2c9f6d-d7e7-1ccd-3b94-54070d0ac83d",
      "6d837540-b421-3d0c-8b7c-b94be0cc3c16",
      "6de3cf50-62a2-a172-1f98-c51d0cdbe7a2",
      "6deb0ad3-6707-4b9b-043d-d899fa1d45e4",
      "6e2c4b0e-e2db-2cc9-403a-80d2ee4d650f",
      "6e45c2d4-1f9b-fad4-4a3e-d824ea323ab8",
      "6e6d908c-db04-d7b4-1d2d-6cef8dc02359",
      "6e8857fb-de2e-1cb8-784d-66d7ce501366",
      "6e914f28-6cfe-7662-8160-f5dbeda9cabd",
      "6e98a8c7-6961-e83e-faf8-e84975e7e7f5",
      "6ead67bb-60b4-4a5c-7065-40726383d558",
      "6ec042e3-e072-c721-6f7b-69846478d652",
      "6ec0761b-3c2d-3c0d-1bce-b00bd091b22a",
      "6ef17117-adc3-fb21-b6e8-ef8c2b1ba398",
      "6efe9d9a-6c24-955b-b312-23f624e79df1",
      "6f04d11b-8025-a915-acae-cbce8e10e031",
      "6f1129b3-8d03-92a8-3d0c-39d4cc57738c",
      "6f423c16-ea04-52a8-ea4d-024f1cef2f8e",
      "6f424851-5bfc-1b81-7ec1-147ab92f3371",
      "6f7ba4b6-08d4-67f4-337c-19c9e35b5d18",
      "6fae42b5-fea0-b290-12cc-c605bcb8aa6a",
      "7030ed9e-0765-b95b-8d6f-e1f121c160b0",
      "705dd29d-b9da-5d3e-3ef6-2e59d9b9174d",
      "70723dcb-8e2e-eb59-0b19-39fdca74bb55",
      "7073e39e-2832-8576-93fd-5ac5e161be3b",
      "70b0cb81-64ef-3e9d-91dd-c2f22298ef33",
      "70fcaa92-c531-fd76-183e-e6df81d1386a",
      "7102b748-1939-d3f9-80d2-cc1d3aeef256",
      "711d8e9a-8b6e-6451-d709-56882ca5dfb4",
      "713fc6f9-1608-10d6-0c66-bf4b438ae960",
      "714d181f-e303-c2df-dccb-a7e838953b44",
      "71a85982-f327-3997-3fc9-57654755269a",
      "720cb80f-e6b2-bde8-6d89-8ef98827bc15",
      "723e4738-0714-c395-1d61-496431f1e65c",
      "725fb04f-d29d-3ce0-0fbb-0b42f262afd3",
      "72857e13-f22b-d0d1-9663-0e23f5f58437",
      "7288d0b9-6ea7-52de-61fc-60aa382ac6c9",
      "72a16648-0dad-3de8-fd60-4e8f2f31c86a",
      "7350283a-ea3c-f4b8-ea7b-5bb866b39944",
      "736d5a30-f8f7-cd9f-1c0c-0e01f2bd2196",
      "736f5c91-76a6-2490-16b8-5866a1df83c2",
      "738076de-7b68-5ca8-976a-acfe5e607507",
      "7395ccf0-334a-b58c-49e3-cce9c8e13508",
      "73bd6ad3-0df1-0105-9e64-3c2aa017243d",
      "73f41565-cadf-2571-c690-71e8beed9b95",
      "74189cc5-c658-c1ae-7306-0b304704e987",
      "7487b1f9-a2ad-0cd1-c098-3f74b9ff3e11",
      "74fb28a2-6200-c1c9-5385-2f4e789e3ceb",
      "75426fb6-32b8-59e0-a821-309579d73e81",
      "75476ece-1e42-d1c7-3236-25de43f450e0",
      "7583e68e-16ea-7676-0ab6-1ae528cd4b8e",
      "759a2c11-2a07-f072-3563-d950d7aef548",
      "75a93fcd-3b0e-33fc-7051-e22eb3720a71",
      "75c664f1-9a86-c806-2881-5bf995f3e445",
      "75cd4cc9-3819-bd90-386f-e81b8d34616f",
      "75ec973b-ff93-59e1-97ed-137e607a4f47",
      "76085c53-eb05-f09e-da9c-4278b9bb8bce",
      "760b86fe-f352-d140-84bf-c9fb878efcc4",
      "760cac2c-1d29-5307-9186-9dd306ce80b6",
      "7656b2cf-2ec7-b9a7-1c97-e28bb20d19b6",
      "767fde72-a8ad-12d5-ad7f-a4e382fca72c",
      "76837a78-5830-d89e-daab-11ea2c45307b",
      "76c221e8-ec81-4306-2a22-c557668a3eb0",
      "76c711f2-cad2-d1ec-3746-76374839c56a",
      "76e2d0d2-bc5a-bf7b-e293-9a9365241e4b",
      "772d1bf2-8ac4-fa44-d75e-55d5d331f96b",
      "773721cb-ee7e-262c-6eb9-412675530aa8",
      "77c92358-db0c-9605-214c-f942d3d30cf5",
      "77d90737-dc5d-ae7e-abc8-f07932de328b",
      "77dbeb77-0faa-f6d3-840c-e728427ee0ab",
      "780a7b57-8dfa-1099-391c-e17b5a9b30fa",
      "78301e8c-11e0-6e72-8f58-3fdd73e18355",
      "7873794a-d403-1587-82b4-d8f671f79783",
      "78839c44-c92b-218d-dc21-f2aafa65f2df",
      "7893375f-b92b-a885-a8cb-2ed9d1b16009",
      "78954246-0049-98de-b58b-f39d7bc0ff66",
      "78c184fd-890f-24e0-5a27-44cbff4bdcf0",
      "7928e0a7-9722-9bbd-23f7-8dcb765b1c48",
      "7971bef3-c983-43d7-0b0e-c6c01d60fa85",
      "7986e2e2-09bc-905f-c643-b6187ca48872",
      "79ca3488-a80f-3105-c9fb-e2258def4f29",
      "7a243f52-7691-9fd2-5a5b-186094a915e8",
      "7a332db4-22ad-e359-d97a-66f1e32203c8",
      "7a6b2314-06a9-9229-378a-79df1d4ab886",
      "7a6fd1a8-c66d-a521-9082-550684005894",
      "7a825413-7687-f042-b2a6-167b9bac9f18",
      "7a8b9b97-d99e-c295-a7e0-139e3dabfcd9",
      "7a8e575f-8139-625b-d636-21ead5587def",
      "7acf19cd-2077-c1b6-21cc-e0e1da16bebb",
      "7aef64f4-5285-3013-5618-1fecbded6387",
      "7af5a285-e7f5-e904-67d3-ca5d546268c9",
      "7afa314a-ea98-97c6-dea8-fe882c0d10a1",
      "7b47f1e4-e29c-d328-df89-e28678371ba1",
      "7b68635c-8992-7217-ea5f-e92e24ef88f9",
      "7b719226-57a7-c3e6-1ecf-57ad304a8800",
      "7bd6e569-ef42-a7b1-1a8b-cc90de9fa56c",
      "7be74e22-37a2-2820-d7bb-29869cdd7cbd",
      "7c85c38f-9810-bb07-78fb-37c0f99955d5",
      "7cabd69f-aec3-4d53-74fc-3aca03a1d77f",
      "7cb9bc91-258b-787a-33dc-7666c76b4124",
      "7cbe185f-7900-350b-9a6c-9d85b0549d48",
      "7cc84f18-9830-07da-10e8-a65fd5e476f3",
      "7d2f4811-0f08-9b2f-f6d7-7ebc206fc705",
      "7d3eac56-cfe0-5d42-d7e4-5c2bcc1eea69",
      "7d5313ad-761e-8261-ea7c-4a223afb5be0",
      "7d8003b1-1329-5202-1f0c-ebe3803c605b",
      "7d8ee91a-7ade-ce53-f3d7-cf37a2109765",
      "7e223adb-59f3-58c6-fd03-4a9072c4b9ad",
      "7e248a7b-ab29-e9c0-625d-1e86ffb85fdf",
      "7e43e54b-0dc9-42b4-a5f7-9ce8041db7b2",
      "7ec4cc6c-9c78-2926-a9c6-2efa9e96804d",
      "7eda29bc-6ee4-9095-71cb-6b97cc21fa15",
      "7ef3f121-6ea8-d573-0a2d-0e4c1564cecb",
      "7f1d3825-cf4e-b371-4c02-c5d681052311",
      "7f3b575b-4a69-9f43-f7dd-691e78885e74",
      "7f955968-d40e-6c52-9e2c-925c8855dcee",
      "7fa3eb3d-9d2b-aec0-baea-f8e5675a6fd9",
      "7fd0ee46-9e8d-2a38-fb6b-7e57f0b56c3d",
      "7fe25de9-adb3-e402-bd29-d37f53de154e",
      "7ff2dc0d-a3c3-d14e-a619-fee985a3deca",
      "8014a689-9081-69ff-c2d5-a3eaa1cb8057",
      "8015ed2e-681b-55d0-63b1-bbc61ca3d73f",
      "80307ca6-ca76-08ac-e691-babe0083900b",
      "80896733-2a73-e8b2-5c17-0c1c7f40a673",
      "80b10c44-eb2b-c962-4b3e-29dd5778e7ea",
      "80da5e5c-1b62-042c-a1d3-df291db27123",
      "80dd7244-dcbc-97bd-6b3a-95376c0502f1",
      "80ef8514-9285-12c3-a284-0dfad2b4202d",
      "80efe9ac-2ebd-b621-de8e-3e2375379b5f",
      "80f886bc-cd92-c478-21b0-ec4a1ecff277",
      "814e3fbf-5748-2cfd-b123-8a44b33a7289",
      "81b3c148-baba-3b14-172a-1e089dafe787",
      "81e688b1-aeb5-5470-a9cd-ad764744c1cb",
      "82064300-6b56-8fe9-2cb5-1e249c63dcf2",
      "82772c31-8d3f-81be-a3af-af35aa9d0348",
      "82a4c9c0-c24d-a33f-0e0e-f6584dbd4234",
      "82fbdb40-2ad8-7b1e-4831-8a5d4d4b7dab",
      "82fd7130-8575-e0c2-381a-1bb3b78606fc",
      "8361a653-d600-4a4c-25e3-dd2dffe5129c",
      "837082bf-e7b4-a92c-f37b-8b46a4417beb",
      "8372e4b8-3e9e-335d-75c5-e23d64d5b46b",
      "83793e69-7c84-41d2-e9bc-91c3837101b7",
      "8393d549-abb1-af1f-e6f6-03a52fb289d8",
      "83c959b3-9330-ed7d-5464-ee731a7fc186",
      "842ab264-4f2f-9fc5-aaa9-61ce02135dba",
      "84648bb2-2c07-2853-f713-11f8b7feab5b",
      "847ee407-cb12-2885-3f18-0eda55880466",
      "84871a19-371f-90f6-4d32-669eb5005c62",
      "84963219-4d88-6ff5-2ec3-2dc2e94f8300",
      "84ae4b1e-5819-8bae-939c-870047780592",
      "84afa6d3-653d-92d0-6276-f75ae2bd0d4d",
      "8510a02a-4be4-bc6b-d445-23721e6e2ae6",
      "851f5003-ddf2-fadd-4aa5-b6f23b062990",
      "8586bbf2-64d6-917a-9b81-a9c1f1fd9249",
      "85bd80e3-8390-b6c8-2ac7-85d6ff5a0427",
      "861b8ba9-d558-bb68-fc91-cd909cdda25a",
      "865fd498-62dd-7d93-b891-b397251d7eed",
      "869b768e-98ee-d585-7ae5-4cf04b77fb6e",
      "86ed2b0f-442b-4243-87f8-267c90e95c72",
      "86f8e3f0-039d-5f3a-0829-6469b982ce78",
      "87068ade-91a2-f4e3-bd8d-2613bdd50497",
      "8735a2f8-1122-48e0-bf53-5621226b80c9",
      "874497c3-8157-1225-d0c7-f2a64157d19c",
      "877db3cc-cba6-21cf-8c17-1c7db15d5076",
      "87ca02bd-cff5-6a0c-ab14-604fd0bedeb0",
      "87d025c9-aeb8-1e1a-ebf8-529d13319f4d",
      "88092a45-8ec5-c1eb-0122-a5f3f0e4d55e",
      "880a5ff7-5027-a0d4-a574-1eea0f2ad653",
      "88204333-f80b-d8a7-6e0f-429c38d1dbea",
      "88248b1d-6959-4f15-4337-df52fd7e8e3d",
      "88c2b012-c964-7673-98fc-90d11b2b00ca",
      "89034b87-8aa1-82b7-a630-3cc9e73fb524",
      "893bf6ce-a24a-b471-ae1c-bc2306c7a4f9",
      "894d5ca3-83ba-1940-218c-64336c055690",
      "895b4929-9a8e-afc8-fceb-b7fa9a14a966",
      "8983db62-6f34-8d98-20b8-01879036554a",
      "899248e7-9818-da95-6fa3-228fc29fb751",
      "89c67c42-78c0-de01-0b34-87270e2fe6be",
      "89e435f6-f5b5-6806-f2d4-f72878f59ba5",
      "89e894ed-a5da-b5fc-6d81-e4bc88c6a1e7",
      "89eaea41-9afd-ee71-fd57-83a8c6621029",
      "8a05c8b7-fbb9-b2da-3c84-13d86d9b1b93",
      "8a09e069-d2b2-2d5d-a800-1ab772a77244",
      "8a3e7534-d3fb-7b7f-09ee-e458c8b1b080",
      "8a457035-20ae-9cf3-d500-dd73ba2e4b11",
      "8a694ddb-f36a-22be-df6d-496e39f5c470",
      "8a6b1406-8a13-0232-3e8d-fb41db791c1d",
      "8a8e9755-54f1-6f92-3533-12b8a4f04f0c",
      "8abf6d28-3ac9-13db-eb92-a16a0e10788a",
      "8b1d7451-4ab4-d98e-2062-c261c37b3fe0",
      "8b3d2128-05df-a441-3071-87b6c38d1fd2",
      "8b7fe6e8-c6ad-d9c6-d3d3-a4816acf43fc",
      "8bd70622-52b2-e8c6-f62d-85eeda216bdf",
      "8be2facc-5e7f-285d-d027-26c34d8ae6ee",
      "8bed1e40-df5e-24b3-8bad-e2d6045bbeb7",
      "8befcd88-b898-97e6-123c-7ea9299eebe7",
      "8c208ad4-8b20-b9c2-bbea-acbcb28fd355",
      "8c5b06d8-fcbe-6df6-c777-5be3b3d50de4",
      "8c755af4-dd29-b6f2-05be-a97dc99faf6f",
      "8c8c302a-91dd-95c8-94c6-0590627cc210",
      "8c94cc54-6b7c-98fe-c6c1-d81766e43f39",
      "8cc49789-0421-ce7c-4823-1fa9b60bff9c",
      "8cd0b119-129c-b7d3-03f3-dd49123fb28c",
      "8cf866f2-8783-3704-e441-778ee590c009",
      "8d231493-e185-9bfc-a046-d57190da855b",
      "8d3bfdae-8840-147d-4125-daaf43f6d300",
      "8e21a9fc-2e94-c62b-a32f-e20449ed2b77",
      "8e45193f-4f42-f5ef-45ff-2011135eb106",
      "8ea0495d-c3c2-f6f9-2cab-ad5f4d6194c4",
      "8eabe7f4-9b9b-8593-c90f-e8bbf87a4bbe",
      "8eb055eb-1c6d-c932-2ffc-9ca3c8663273",
      "8eb66fb4-76e0-e878-de56-36217daf5348",
      "8ee8db36-db39-c067-f2eb-6844abc9ac33",
      "8f04d1e1-943d-5461-6694-814b2764acfe",
      "8f42df5e-22d5-3215-5af8-dde79b9d50ef",
      "8f6b7019-09d4-812a-ac52-53f2a155a0e1",
      "8f847d58-5a37-6fb2-5ca8-1dedbd81e720",
      "8f8e25aa-d737-db32-4999-a26b749149d5",
      "8fcfdb85-9109-4d02-190c-c72e325cfec1",
      "8ff9468c-cf91-4fe7-e72b-3282f5a7a983",
      "90256b14-6b7c-15e9-a504-4c5525201b67",
      "9065e0f9-51c9-740c-d304-a3bf84144c5f",
      "90e063c0-731e-ea4e-d806-070f1b50335e",
      "90f09267-3d3d-9bec-59dc-c31b84103ee5",
      "911e9b1b-e4b2-1408-f2f5-d9ea5048db2b",
      "912ba26e-1916-23d6-7a66-d96a40e3cf3b",
      "918f4556-79fa-40d2-c09a-1422572084a6",
      "91b00edf-e1b8-ad0d-32b7-a6c7f9fc52d9",
      "91e58181-c31b-6251-1ed7-627827071837",
      "92104663-e2dc-5d4e-e456-72d1bc9edde0",
      "923cfc33-7d54-bbb5-e106-048fd6b1b50e",
      "9271d5e0-f606-211c-a421-cada3354bb35",
      "92977682-ee74-fac9-02ff-cee360704c48",
      "92b23966-7faa-d517-5edd-4b82945a7131",
      "933f78e3-56d6-6519-3ce3-39aeda492676",
      "936366aa-9fd8-9d17-1db4-c50aa35574e1",
      "93849a9b-0f6c-9c37-f740-6806df995078",
      "93cb944d-0591-077a-eab6-beb41d545cca",
      "9405d208-f6a2-688e-96f9-e97b6056611f",
      "9439c691-e98a-52ae-4258-802c88fa2847",
      "94549705-4351-9051-87ca-29388f0104d4",
      "9489320d-cf9d-486f-e2f6-009a6608ee6d",
      "94ae9d4b-383d-9a6b-7344-88957b28f185",
      "94f36e5b-1ad4-cdb8-f518-fdf98f83c857",
      "951684ab-1e6d-8cc7-9b4a-340d1da473cd",
      "963d9811-68d5-33db-6bde-f81642da06be",
      "9646c079-6241-0af7-d227-0375300bfb92",
      "968007db-4db7-5367-596e-d131f53ef64e",
      "96c6466c-69a4-54ce-7bed-1c3ac3bc6cf4",
      "96d8e824-044d-39a1-9191-a6ed05b7341c",
      "96f9bcba-8e0e-e62c-430d-9fad33fac89e",
      "971ec3c6-6384-c5f6-11b2-3ed1c25084e8",
      "9737a467-886e-6dda-a7b2-86503b091f06",
      "97435ee7-dcbd-dba9-7f85-44c119a338e5",
      "97dd0ed7-8d33-0187-90ea-b0a9946cb52b",
      "97fe93ad-0865-00b2-89e9-bf1deb3f3921",
      "98a0c7cc-0bcd-75a0-f847-0f97b023e8bb",
      "98b9677b-593b-6949-8fce-15c4ba374e45",
      "98db95f1-beb7-5558-89d1-db18b5f9c013",
      "9915c01a-7d94-b680-cac8-be917e4a29cb",
      "996446b5-1ae4-6327-3218-52c29e49fca4",
      "9972e0cf-787e-e172-0fb2-8503b0b608bf",
      "99811af7-f815-3da1-553c-1d87404b9f56",
      "99b8bc1d-8b88-fee5-1bec-076dad675378",
      "99cad053-97a8-b695-5cf6-fa716809b1cc",
      "99e7eae6-7f97-6eeb-fab0-95e2f3323d88",
      "9a25482d-c348-8529-7962-0126bb8304ed",
      "9a2d7aac-e198-4366-dfec-e637eb96f85d",
      "9a56d593-ab1b-a4c3-3d01-42d18b223bbd",
      "9a66d29b-2eff-620d-28e7-54b74e87be41",
      "9a6f8718-b80d-489e-1c0b-c4a278fc15b1",
      "9a7fb2a1-0a7c-e0ae-b203-5d14343203da",
      "9a81a1d8-32c1-65a8-5bdd-ae6216a1df89",
      "9a98ab4d-cf58-f768-9678-256995e82bf3",
      "9ac254e1-bd83-b33a-33a0-94329ef1dfaf",
      "9af2a623-08d2-d735-58a9-e94765329424",
      "9af9b078-b523-24bc-a21d-487f85b8814e",
      "9b17441d-20fb-77a6-db45-f11ff47d08e7",
      "9b66cc4f-2a21-4172-efbe-18114302536d",
      "9b73800a-866a-708c-a522-97b9c3ebb178",
      "9b946079-d04c-3b9a-bab3-fc1456b3f644",
      "9b998d3a-bda0-29c6-f24d-c71217a46cdf",
      "9b9f4d81-38ae-1c17-8060-84ba6d225f62",
      "9bc9e4a9-2c47-a14b-02e8-79fa5e429598",
      "9c027c3e-6748-8055-fb9b-b013ce516431",
      "9c1b5316-7611-a44e-4044-776359ac3820",
      "9c2e5f01-38d8-fa07-565b-0ea3b2d2b87b",
      "9c7cb5b6-bb66-bb39-f8ac-efaeb0fb33fb",
      "9c9169bc-7ed6-ac60-c5ef-b6996f3f114c",
      "9cb5dc6d-2af6-1fc2-ec11-bb617e6ecc56",
      "9ced6a91-f120-759b-ab26-8b4229013c40",
      "9d0c2649-c9c1-2e65-9f7e-340c51556929",
      "9df539b5-f73a-016c-8f36-8e7f06364231",
      "9e1fd64f-901b-b542-c8a9-1d82950e8ffe",
      "9e3de44b-c69c-72ac-1da2-d7d4490c84eb",
      "9e4c2d1d-7b8a-556d-4e2b-5fa8d5d0b938",
      "9e5eee28-827d-6c1d-ea7e-baeff8118ea4",
      "9e61878b-dc75-146a-cf09-ef195041bf59",
      "9e6ec7cb-45da-b4f2-5dae-edb3f0785320",
      "9e84d879-bf54-eab8-2006-64abd280339a",
      "9ef83e00-aa14-6090-371d-cb3bb9aa548f",
      "9f4bb619-08e7-3bef-ab8e-76b87ffba09f",
      "9f657598-63c4-1500-45fe-4bb368fb136c",
      "9f8686f4-cd75-07ef-0025-85d41d5e409b",
      "9fd90cfe-7ec5-4125-6827-0fbbb4aac316",
      "9fdddd40-bedf-80e5-985c-00ed094e0406",
      "a02885b6-4038-8229-b4cd-d25ee1158162",
      "a02eec5b-6d27-b43e-1e16-8b12252cc834",
      "a033485c-5419-aaea-f2bf-1290dfe5d08b",
      "a046eb36-4494-d9aa-4ba4-5c1e3b75a1a4",
      "a04c8b33-7293-6bc0-3315-876d7a9f9e0c",
      "a055dc0d-9d80-59f5-94ed-154f301eb7ea",
      "a080e2dd-ec7e-5300-3d23-7d2c28c8d59a",
      "a0a6b886-5f09-8505-40ea-6c077da5ba41",
      "a0d1b2fa-c2df-669a-fbd8-d2c452733fdf",
      "a0e75229-c2a1-7652-895b-41dffc7ea9f3",
      "a12816e2-cbf4-9179-a38e-e0b42b266b7f",
      "a12c96ac-faba-0015-a0bc-56261fda44fc",
      "a142ef19-9b26-d819-3b22-c3f2cabc9243",
      "a1ae7be7-c439-f0fe-a51a-b7caec3cf1a7",
      "a1fdd2bb-77b5-780a-d89a-1feb17d9021b",
      "a202cfbe-6ef3-5274-f3fa-cb64be0c0781",
      "a208630c-8da8-6c72-0121-12e64786b8c7",
      "a20d4812-eed6-be0b-4c6f-8861175716de",
      "a21e4779-835c-bde8-dee2-f9c7e93a6cfb",
      "a25ca7c2-bc47-55af-668d-f337aba4a60f",
      "a26037ba-7ae5-81e8-787d-d7d599ab9a96",
      "a276dd1a-01b7-0a12-6531-801606eeec0a",
      "a281fc5a-d569-cb87-4288-a34f393c7302",
      "a311047d-ae2a-ebad-7b13-5608c0e8e4d7",
      "a37b2143-1fbc-5237-4038-5159afb5e5f2",
      "a3e11870-70cb-c58b-de69-e2035fbcf95a",
      "a3e51f8c-a874-0a0e-1781-3a6968f78236",
      "a40be035-9544-87a3-b992-eb67bab64b9c",
      "a44393f2-976b-19c5-22ed-0d3551c03ea9",
      "a44e4465-6f31-fdb9-f527-f182fad3e0dd",
      "a458bd48-920f-67aa-c71a-b67ae76f90c3",
      "a47d39f8-a01e-a21e-ed81-d39501a20e1c",
      "a49f2c8d-93c5-f2ae-1b73-4080260aa1eb",
      "a4c3164b-5606-e270-67fb-7d2be71aec5a",
      "a4ec2af6-aea4-8b4e-adc4-bb2c1e1f750a",
      "a511a70d-c5f1-4d5c-42f5-3a16b1073142",
      "a538b491-e632-d770-7f1a-eae4ecab9073",
      "a5396007-6b3e-7f56-909e-cef76c9e4251",
      "a55af77b-bc6d-cf8e-6670-2eff261d8210",
      "a56e4704-c39a-0775-23df-3e54de4598c0",
      "a58392fb-c186-e75e-d939-982918b23b57",
      "a5a74505-7628-820a-3315-112d2e594df5",
      "a5bdfa23-e791-78b2-8680-5de0db1bfed2",
      "a617330c-7d66-7909-c706-b45dca4399dd",
      "a623e94f-fd46-75e0-bd25-8cadbcda6d18",
      "a6452412-ff11-98f8-4067-63f6d4c20b20",
      "a700d3c0-45a1-a5ad-0717-bae9902dfc33",
      "a788771a-88cf-f2d7-2728-d314b0670dd5",
      "a7aefc60-dae1-3dbe-f6f5-704f4e7920f1",
      "a81579c6-9db0-aed8-7de8-3932a6b6b39f",
      "a81c1321-fa9c-f219-f4c5-5ae9c1f7b92a",
      "a85e1ebe-f6d9-1aaa-226f-2478694efed5",
      "a870bb61-226c-9cbf-1666-28f844d75f68",
      "a898c3b6-8258-f587-b697-964e858dd638",
      "a8b46732-0d2f-b368-939c-e0bb48284507",
      "a8ddda99-020e-2e1c-fb8d-6ffdf24e8029",
      "a9561eba-0296-a30e-4f77-994c87e95b0d",
      "a9a0c2b3-b5b4-d003-e4e1-74a7d7800934",
      "a9aa052c-2b0d-063c-2f39-5fdb9103ffd2",
      "a9d1abb8-f7b8-9ddc-4a34-75be568d9ff8",
      "a9eaf6ab-4e64-a03f-eb49-6b2a3e911063",
      "aa5e754a-463f-2d39-98d4-f498bf6f680c",
      "aa82eff6-65e2-a3d7-b4b0-9a65321c1e41",
      "aab86597-1a7b-abe9-0ad7-92fee8b56ed2",
      "aac86acf-44f1-277b-29ff-7747e89eaa47",
      "ab088440-aca8-7f8e-0abc-c6177d62ea91",
      "ab2b85c0-acfb-f209-2d93-96028ad8374d",
      "ab4aab07-f762-1503-3d04-4380ad5ac08f",
      "ab7bbfbe-6e89-ff16-4731-a4f8c32f0229",
      "ab7f5b36-df74-e590-3b05-f3c59fa4b2a0",
      "ab9b59f8-7288-19b5-8e01-e09e44579e13",
      "abc66a89-927d-2d67-5ee9-72b7a78a24bd",
      "abce83b0-690a-c2b0-2e8f-202cbf3fc8c9",
      "abe21c56-4aac-b6dc-e5fc-19f6e075e451",
      "abe780c8-c2eb-ee37-2e5a-d082a0725591",
      "ac0109f5-198e-eac1-9efe-10dc1f1c69e5",
      "ac21525f-6fee-9f89-ef87-fc44b017eac8",
      "aca9c611-534a-8139-633e-075a04831a1f",
      "acb27c9f-3056-4397-8ce3-dd940ed7702a",
      "accc02b1-7769-f87b-8c7b-fef81d993d6a",
      "acf00fea-f072-621b-92d3-5f2a6de14c26",
      "acff16b7-9ded-2798-a32a-cb8d66de13c0",
      "ad1a97df-4b52-00ab-6825-991405d63541",
      "ad3a2071-4862-006a-d9ff-5d513c3f6fc5",
      "ad4fd168-a878-663d-f282-024a21b47ce5",
      "ad82e640-718a-2be1-26a8-1aa26f70d20d",
      "adee83f1-f18e-dd69-c4fb-dc874f42afca",
      "ae14766a-0229-fe8b-dd1b-645fb6c55a8a",
      "ae359a66-856e-6c93-1fc4-8bd50341fefd",
      "ae44b708-e4b0-00a8-f7a4-91d07258b5cf",
      "ae4b1a05-4654-7ad0-d1c2-7c2670a0a887",
      "ae6ce38f-85c3-c668-8d30-8b43e2a88325",
      "ae8ffde4-cb14-0f92-1afa-bdc4ba52289d",
      "aeb3f42b-9f39-9e7c-e078-fa2eb9288a05",
      "aeb96a8c-6f0e-3924-7f7a-0ecb784c8bb7",
      "aed05ebe-4b3e-8fe9-21c1-ed57706cf161",
      "aee5ce05-9b3f-9571-4685-f1937c9ef6cf",
      "aeee2fde-9061-8179-335d-bcc49f3dbc33",
      "af14d3fd-e462-493c-c64a-e8f78600485f",
      "af33bf0f-decd-c1bc-8a0d-8de588fe380b",
      "af42380c-305c-a308-7083-ff9ab377d273",
      "afd02175-836a-8b89-d63b-af57c1a95dce",
      "afe5b63d-7f37-1b47-9697-88aa86c63c00",
      "afe94cab-75ee-f1b8-c3ab-d83b6200998a",
      "b025c52d-76fd-9407-e2fd-76b141bc65d4",
      "b02becde-ba64-3f5a-2629-793df746a331",
      "b0426639-2d51-1406-e6c7-7e7bcda05c59",
      "b099ba85-da4b-88fa-aa8b-2242e273217a",
      "b0cdd7a9-8863-56ce-3dc3-11a57ca2ae2d",
      "b1058be0-9250-db9e-32d1-ff6f51d45f90",
      "b117040f-9755-a6ad-c142-e4533d284dad",
      "b1846c87-aae6-3933-3ce5-e929f767adc0",
      "b18d69ff-1c54-5de1-7c7b-3151ed727efa",
      "b1ad2fb8-105d-0cdb-a762-d436fd80acf7",
      "b1ed7faa-88fd-2955-7609-7c0257d61bd0",
      "b2662a8a-5c35-9d45-7dbb-5cbe87a7d9ce",
      "b2752d82-e5e3-610d-1b1e-587f1de14f90",
      "b2969b94-ae96-3cad-3dd8-778c75dd320c",
      "b2984909-581f-aba7-c49a-efe6be7274aa",
      "b2da051f-5df4-f52c-533e-ad86cc4e4cf5",
      "b2e95089-e2fe-c5c1-419a-618fc554622d",
      "b33bf27b-d201-a264-a8b3-4e2c606cd566",
      "b35660df-8c01-1f50-e3c9-d4c91b875282",
      "b39720c5-5394-12cc-b913-a602e52c7a0a",
      "b3a6b900-bbee-a20a-c8d5-72c5c3bb235d",
      "b476a91a-c3db-8a87-b210-88dc1751064f",
      "b4889ba5-4619-38c3-fd08-2378776b593a",
      "b4e46616-068c-bf5c-8169-c99dce4a5a09",
      "b4f217ea-8acf-3abc-6052-6aee105297f8",
      "b5379f70-83ad-ed38-cb4d-d3256db30b6b",
      "b58c6876-72eb-bd76-d75a-6a5325aed482",
      "b59258e2-c419-b7b0-8793-864203703dff",
      "b5ca91cd-918c-5f8e-c84c-13285ac990ae",
      "b5f640b7-27ee-0862-a4d0-1f5805adeabf",
      "b608f2e8-f152-f742-a3c4-dbf2c1778281",
      "b60a0169-20b9-9db5-1884-51be54d4ded0",
      "b6396849-c1e2-6232-61c7-fb12ff5f1b0b",
      "b64017f1-4528-ce02-89cc-3193ae008620",
      "b651b121-b34c-f077-bdf3-ff2c28170d99",
      "b69d58d4-b46f-fcf4-91d8-bf2e89e2e910",
      "b6a07e11-f65f-b9ae-37df-b72060eb9407",
      "b72e7b30-e0c5-8c4d-1b1b-7f267a91f3d2",
      "b748cde9-c496-1ebb-3c33-edc8f41057de",
      "b78a12f3-397a-f9fc-b119-41d3e33e09dc",
      "b7a75d35-a2c7-6c50-77e5-34a139cc0a7c",
      "b7b01544-e902-cecd-cb0a-bf8664f4ddb8",
      "b7f26c10-c43c-4288-c46d-b1c13688f643",
      "b8307bf2-0e31-dc11-2b09-40e882aa73c9",
      "b839296a-2279-7b22-fa88-8f53d3d21624",
      "b840330c-1a05-d734-7a1d-2d4adc2464c1",
      "b8981943-1c52-1ca9-d2f2-c64cd4d1a14d",
      "b8d1b571-663e-5e95-0a9c-a7ff1a1d5e31",
      "b91c0ae3-80b1-afbe-1e30-7702661e47c2",
      "b9486373-2a29-240e-8610-5f6572fb320b",
      "b9719ed6-5ac7-fa44-f083-de96b9531db0",
      "b98b3b36-2c6e-6d9c-8eeb-a567e56f25ed",
      "b9a2eb4d-cebb-f7c3-7e19-ae0963de42d5",
      "ba147715-6745-f852-3605-6bbde2164cb4",
      "ba26ee93-4329-808a-a998-4cc78122a16c",
      "ba5ca97b-7ef6-8323-2427-7b80713d2c32",
      "ba940fb9-1e4f-4efb-0b9a-d5d378524b13",
      "ba9fb665-f192-5e3c-2654-d91472d538ee",
      "bad4e68b-9422-901d-fa68-24049aa9f961",
      "bad52820-ac89-e884-1362-d29c700a27cd",
      "baeaaf58-d448-7acf-7fc0-f7132fbcfa77",
      "bb0fbfa9-5820-1fae-a9df-61341b7e6e19",
      "bb27989e-3df7-2494-1fff-2d2c31222c19",
      "bb6733c8-a825-beb9-0964-7d080b1e700c",
      "bbdbb4de-2f86-82e7-d6da-97d02d23a4e4",
      "bc205159-8e3e-c60c-93d1-79c042702ef1",
      "bc2e211b-1553-54d2-cb10-889907353619",
      "bc4ab773-29ac-dd52-579c-16ffd15db72b",
      "bc86898c-227b-5e35-d4a9-a688b7a392f3",
      "bcaf90a1-48be-bbc7-4a41-38e5e4efdc69",
      "bcc2bf58-98b5-d955-e087-19cf55c3f4df",
      "bce68864-aa24-a6bc-1be9-b7c2b11fe24e",
      "bd1cfe8d-482f-b1cf-8323-30750d88691d",
      "bd238e74-eb5f-8311-1e3b-10aca683f093",
      "bd427e5e-3945-8701-e5eb-e1d789f05e93",
      "bd46be7b-4899-daf1-758b-84c67648a913",
      "bd4c854d-a728-5ac2-9670-2c2fac74dd45",
      "bd82e0ef-7d9a-476f-b7ce-6c7fb2c6c0e5",
      "bd86a304-6c0c-1538-8fa8-12081d33eb83",
      "bdd6ebd2-0c1e-924e-2764-2f68c5213ea2",
      "be053156-a112-af95-44f1-9e61cfc1827e",
      "beca1b27-fa5c-8cea-8319-f80d1f49b9c1",
      "bedbe20e-7aea-bb3a-5f3b-5a6a6f22a562",
      "beeb158b-20d1-ae97-8282-9f545bc3c169",
      "bf5b87f3-2f15-b511-454b-33645d3dd95c",
      "bf847586-1809-9f99-2e7c-dde3a35fb94c",
      "bf8a4668-7c0d-853e-4061-bc94b2a7d2fa",
      "bfc00787-64db-e692-f439-1d1578022362",
      "bfca1a6a-8189-f0ae-a4eb-0609fdfcd596",
      "bfe70ebc-a6bb-5a60-31b9-1fa2a3fb8760",
      "bfeb8ca7-7fa2-45b9-c5f0-b343c9f4a962",
      "bff90f30-0903-a85a-fa0b-cafb80b80566",
      "c0388e6b-a779-390d-4eca-cfe95b03e6c5",
      "c0ca8036-9243-87c1-6153-47852f96d910",
      "c1061266-abd4-1ecf-0012-4a4f0d6fce42",
      "c1134bc2-edec-27af-b6e8-e987ec3c3804",
      "c1268eb3-fbd0-b57c-5fb9-857a044c564a",
      "c12d1c6d-d670-0b00-d04a-80ea08b7850f",
      "c136b7ef-7428-ff9a-71d3-c3c84b15754d",
      "c13c5a30-f4a4-5958-65f5-f7e0f05f1de5",
      "c1410478-9c9d-7b0c-9149-da879cf8c7b3",
      "c1417c88-f282-b3f9-1102-d85485db4ba2",
      "c15f67e1-1752-0479-b725-996c976e1aee",
      "c1b1040c-beb8-84b2-eb77-9ddfe727f772",
      "c1c3dbb1-c244-61d3-0de7-74d731377f06",
      "c1e56288-5dfe-1233-8b7b-6b54f0128a3f",
      "c1e8fe69-38df-2900-499c-e7db12866b10",
      "c2205b34-3cd9-7c38-b2df-3c0e88feefd0",
      "c23d457b-80f4-ddbc-da0e-2baf0fe48c7b",
      "c24594e0-cfcb-9f36-1dfa-036be7bfa2d2",
      "c2546203-7f19-41c1-5142-784bf3b0b003",
      "c28ae0d1-0869-c051-102e-81b6f59bc853",
      "c28c94b3-b36c-ba25-88f1-79e1dc5c7107",
      "c29cbc77-a3d4-87d1-9480-3349dc035bf0",
      "c2a068ba-9744-7b47-ab9c-69f33df229d9",
      "c2b672d8-0460-028e-3a0b-aff093ca57b7",
      "c2e26faf-be0a-7209-39fe-53f40394cdb6",
      "c30b49f6-ae8c-9fff-184c-c3f70e5cf99f",
      "c32336a5-f3c3-442e-6baa-eb1b7dd6bce1",
      "c35068d3-257e-584b-f218-84cd364ab584",
      "c3d82dd4-bd83-85b5-485b-3a9b45989bfa",
      "c4785784-fb5b-8de4-3a9e-e53345b26d4e",
      "c4a1ea89-bfb1-1347-7808-5cdfc8b5b3c5",
      "c4f4e397-5cf5-b163-ca36-5dddcd9b4049",
      "c53c8d9b-6cf1-843e-0fa5-f82276c015bc",
      "c5402bcc-6cd0-4d51-121e-43538c795310",
      "c56e6f92-a76d-a206-0550-3e746d573bf7",
      "c578c4d1-8c94-6d45-4c82-97dde6ed6292",
      "c5a6d31c-7f24-efef-9d9f-3a9b18fe42ef",
      "c5ad594b-8a5b-6d83-5d63-6c79ee11ac11",
      "c5dcb5fe-69a8-bb82-ddd1-d4c0a7336ae6",
      "c5e50355-2692-d36a-3f2e-c6ee4fbb77b1",
      "c5ebd4f9-162f-d619-f486-2ecbaede4b90",
      "c60c6a03-d1e1-1641-c24c-f156eac5249c",
      "c62d1954-a59a-c2a0-8e51-94bede232e14",
      "c64fa76b-d17f-34c8-587e-66ad0ff69b6a",
      "c66f754f-853b-1642-197b-7188dad6a98d",
      "c683f015-2de3-f0ba-2388-3aa9ce813dea",
      "c698df09-f0d2-cd18-a81c-32e3175df2ca",
      "c6a0ac05-cc40-2911-399e-464300553e2e",
      "c6bfe26a-d9dc-545e-e8b2-f26eb0797bae",
      "c6e7f77c-01b1-aa60-3ae0-54fee81ca16c",
      "c703f675-50dc-6a8d-b774-e4b778d39cec",
      "c7316f15-6ccc-8b79-d74d-12c2dfb0b9f4",
      "c7a73b9d-3a8e-6ac4-d397-b8c9d9c160cb",
      "c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72",
      "c7bd9941-0447-8865-9314-43affd9c739f",
      "c7fe583b-9b28-df59-fd36-0dff1f3ea80d",
      "c80bb198-745c-dc68-69a6-c6daf5de15c4",
      "c8628d3f-06f5-d7ec-e8fc-c30f90a22105",
      "c86d684e-cd09-41f8-0d65-ff19b0af8833",
      "c8a93f9b-d0a9-7eae-272f-f3a0a962832a",
      "c8af7f83-9dee-eac6-a5c3-ce5b46334f3c",
      "c8f5cee5-7002-62ca-e0e4-cf3c9b97f595",
      "c8fec91f-2228-757a-765a-d7836170e59e",
      "c963ad1e-7e19-ee71-014c-e924fedcdc0f",
      "c9816994-8ee7-8137-33a7-1a189c1cf855",
      "c991722b-498b-d997-2e9e-42131edeb9c5",
      "c9ae45e0-35af-0de5-e444-5442860f7752",
      "c9b0e7ac-71f0-c5d4-4f67-7d07b8e4d548",
      "c9e3ca06-e337-098f-0a82-ce98e4645566",
      "c9e8ba67-b836-e455-ee0e-73f2fdf1612e",
      "ca130fc0-427d-56db-262a-42752d6e24d8",
      "ca1c8505-6954-15fe-a352-9fcd9c8d016a",
      "ca666596-b5dd-b365-1378-269010325c4b",
      "ca6ebebd-8d0b-be86-1cec-082f2ba6fccc",
      "ca824df4-888f-fc27-bee1-02574015e285",
      "caa7c472-a8e6-1e33-2e78-3ff3728347f1",
      "caeaccee-53dd-ae82-3148-ba0d4bb6ca16",
      "caeeff58-b580-0b28-20bb-6e1ff2b85fdc",
      "cb0a2701-1c97-253f-c246-93ccc53b3da6",
      "cb67b78b-3210-dd63-8f94-883c95957d0a",
      "cb8d8648-aa1f-9206-a66f-73f83be03612",
      "cba2b69f-7088-3c06-f799-a71ac4c76acd",
      "cbbf3575-a94b-6374-eaa8-8728fd983f2e",
      "cbebdf54-d98f-1634-f259-2cf16ab1f79b",
      "cbee2d96-8126-e745-27cf-bbb2528132c4",
      "cc29b660-0a24-79a5-53e6-7ce49d55af0e",
      "cc2c12b6-ee50-e9f8-665c-25862a617b71",
      "cc314f58-8fd3-2a19-a631-b26ad95ce873",
      "cc76eb84-7fe5-ec22-c0b7-5014805d96dd",
      "cc9619ce-46cb-e6c1-1545-35c32d437d3d",
      "ccf88082-4067-348c-0a4a-c01cf6745d9a",
      "cde88516-3ae4-65e0-006c-5b525a397641",
      "ce2be85d-6036-ee64-fca7-46ee4c910ec6",
      "ce60ed81-80c0-c3e1-c655-b65c7b77fc58",
      "ce6cb83f-190b-4474-48e5-d29037003498",
      "cea59186-244a-adde-4a92-797fd46e8cea",
      "cf17108e-f657-1dcf-4459-357a89b39b47",
      "d00e56b0-3f75-651e-513d-089d4f3a38c6",
      "d0100932-678c-be95-8325-918a3c3e3bf5",
      "d04f3510-324e-8256-fff1-60400f21bf61",
      "d08c0e65-3527-64ac-68cc-dbe6fc00a486",
      "d09a3aed-b967-5d1b-293d-099ecf774005",
      "d0a5e2ac-cf5a-2cfe-50bd-7eda4a99531e",
      "d0aead39-6e7b-5789-3ca3-6f75ee198940",
      "d0ee6a57-cf7c-19cb-ed95-5d95451081a7",
      "d14037a9-ca34-15ea-624f-929dfb876160",
      "d159f1a4-8121-957b-c6e2-16b0ca107434",
      "d163fd10-5447-9738-3f4b-904cc48c0d6a",
      "d1662770-0ecf-a126-5f04-6dbacff93514",
      "d17d5f3a-46c8-2c79-6d9b-9d547705c15a",
      "d19d1fcf-a289-a87d-30b7-7152b34af4da",
      "d1b0cfa2-c180-94b2-7eea-5a7679a01227",
      "d1b2a0b5-ba0f-6af9-6831-b3e5c4b2bfc4",
      "d1baa080-bc7f-f0f4-8b16-96d1f9e021ab",
      "d1c3f543-c51e-0a70-bd82-1faae5e24295",
      "d2570c26-41b8-dc60-a14d-6941b37e3b7e",
      "d28c252d-f771-0056-9779-443a9a3a9c90",
      "d2b042d4-c14f-c8f5-133a-5be9cbd39ab8",
      "d2c74a77-0997-c5f6-22c3-2f19818493ea",
      "d2cade33-70ea-f88a-d2e3-5971d3b7019b",
      "d2d06ee1-0ec2-0c3d-f737-a137a3126091",
      "d30d5903-0ff5-96e0-46b9-fccb93b9d3e9",
      "d30f1d16-2efc-78cb-c858-14bb121548d4",
      "d35a0c27-1454-5df6-9769-bf9c85c00488",
      "d3beb7d3-29fe-1689-ae12-6caf16c8c949",
      "d3db43c6-950c-a6d5-8771-bf447ddabd7b",
      "d3ef1b78-a11b-b822-9221-4836c358ac2e",
      "d46b4eab-5a5e-c3b8-4795-aa0828c3dce9",
      "d4817dcb-2cc9-a186-3c51-a45da00c5980",
      "d4989791-e3d8-33f3-03f5-01bdc2fb73a7",
      "d4c65ac7-c265-5bab-e3c7-f603c48f92a0",
      "d500c339-4dd8-6e90-24ee-1e3ee401d2fb",
      "d5346fb8-6aa5-493a-ed88-1415e0c2005b",
      "d53c3129-3271-dc9f-0987-9d069b42304f",
      "d546f04d-2345-b0b0-5c1e-e2bf5bcf270d",
      "d549dcee-15b7-7ec4-5358-e81e914ad2d8",
      "d5953d95-d9e2-a837-ed07-60643992cb16",
      "d5970f32-ce1b-50be-c762-a77134c5456e",
      "d5a75c20-8265-9dc8-9213-10cdc5af8688",
      "d5b73704-705a-d089-b50c-b716eba59c0c",
      "d5bf0427-c3ea-f9c8-933d-deb15ff23d3d",
      "d5debe7b-da69-a4a0-c60a-d7f9fcf71b06",
      "d5fab709-9ddf-7581-227a-d50b9587a6a5",
      "d6082ab9-80dd-1157-6553-2d03aff7fbe9",
      "d6219e1d-87a5-5a7c-e114-9321849add8c",
      "d66d5d60-9f1b-7425-03c8-bc1b1a9a0910",
      "d672aef8-6565-e8f3-6df3-7cb4ecb019d2",
      "d67fa128-22c1-5b86-56b8-046a47578664",
      "d6afddb4-c774-fd8c-cee9-8edd6e831c53",
      "d6c524f3-c801-302d-be1e-cc16fda99d0b",
      "d6f88c23-2f87-964f-b33b-f31b8e0aceb2",
      "d6f8df19-66d2-4b57-2d5f-b6726de7b667",
      "d6fa1359-5b6a-b3eb-a67b-b1cf714291d5",
      "d702fa9d-b05e-f70c-b26c-1eae48912fd5",
      "d71bd310-4404-257d-f344-e161f5d80db6",
      "d7427258-34d9-44e9-73c5-630308a8b138",
      "d79c6998-3271-e88a-0451-56601bb12bff",
      "d7b3cf40-4f50-fd7a-17c5-df5eae6a61b5",
      "d7bc8d89-361c-d61d-29f9-19c3d9414155",
      "d7e65a54-5dec-994e-09b5-602004f3347d",
      "d7f08099-cd22-9ed0-a95a-6b469abcc680",
      "d82c26a1-f316-f72f-d1d7-441caf19728a",
      "d83d5ac0-1923-c45b-563c-dd961754ad5e",
      "d86f8c85-1f42-3bd7-9e85-78036e4b7177",
      "d8716125-dfe6-69c4-eb64-c8a8da2c2128",
      "d884e1c7-cfde-e2da-b504-c88cf3621cec",
      "d8e3917b-930e-f719-e1c6-450ea3cb6e80",
      "d9071f6d-87a8-c2ff-2a8c-609fbe331cb8",
      "d90da23d-8cd7-ae08-1d22-269cae0da83f",
      "d98fcbec-80bd-cc85-4784-5326aaf21ffa",
      "d9defa5b-19db-bd1d-5700-3d70a731c4d7",
      "d9e07297-dabb-1965-130a-5950b96d6d0a",
      "da1cb414-98b2-5b08-8475-30b5db256ae1",
      "da4ddb5a-1190-b86e-b81a-5f91399dee9b",
      "da710d26-932b-e95f-627a-19565d2d531e",
      "dab01bcb-763f-c6be-3c34-a6bf7396d663",
      "dacd6390-2842-651f-ee61-d11512f91e86",
      "db0aaf8f-4bd9-cca7-40bc-c07708ac2fc7",
      "db269c06-33f7-a19a-f25d-2664f1276d2e",
      "db816d77-a2c5-6f2d-799b-593a81f6cc92",
      "dc0fc0d9-634a-ccfd-ad46-7d3c83d2108a",
      "dc3c9962-c515-2a7d-281b-052a8952da9b",
      "dc60c383-14a4-5a2e-32fb-cddb52453bb0",
      "dc789115-cf84-fe7e-73a3-2e16153cc4e2",
      "dca10ce6-e8cb-4a06-7d49-d197529de614",
      "dcaab5e5-ebb8-fb51-6398-d3734aa9d300",
      "dd42ef66-6029-2d67-dd1f-c28bd4ad4c6d",
      "dd97e287-1c3c-1171-803b-5498eb00b620",
      "dd9c8652-ec81-3c52-d7db-2788a2935b2d",
      "ddb5f52b-5610-a708-5271-23915cdab993",
      "ddd138af-472f-296b-b747-74eebe9a2859",
      "dde61f40-31cf-8fe9-7585-9bf05a408fd6",
      "de8a4ac3-c8d9-5b58-f98f-ad8d12406a6f",
      "de8c793b-100e-3eed-e813-a1cbdccd67ee",
      "dee93a05-3313-61aa-8165-dd596c9cc623",
      "df16766f-51ae-c4ce-0bd6-7a45b36b4529",
      "df4c7c95-80e4-6726-2249-c42e36d98d79",
      "dfc05e75-bedd-cabe-2e13-60e0279cc92e",
      "e036c956-8869-3c98-bbab-86c5caf8ed12",
      "e080bf87-a185-708c-b158-83fac382b89c",
      "e097eb80-4e39-3ef8-0a82-1b12fbb54115",
      "e0b6391f-1bef-9887-a303-a50236c327df",
      "e0dac2f7-3d8a-c806-336f-0651010826c2",
      "e0ea3f2e-6d32-7593-6b2b-794998ca63e1",
      "e1014146-479a-7fc5-0a59-fa8b417a8b91",
      "e10ad457-d9a0-15db-921a-2e8eaa99cdbe",
      "e14eb3a1-9ff2-2320-9065-b14e2f7adfc3",
      "e17d1b88-833b-4b22-3124-1ef48d03a184",
      "e17eaf15-ebd7-8ba3-8ff9-b23ea5afc8c4",
      "e1b4ec19-ae93-cf18-9c95-eb309759d0bb",
      "e21e83a1-ffe7-4b09-6c74-4ca9a298543f",
      "e2276d16-c1c3-6f2f-1a88-c092e12b672e",
      "e239661c-1d58-afc3-271b-bf149b275724",
      "e2c86e00-bf80-5a0d-005e-6154b26f59d0",
      "e2d79df7-7b4c-88ab-daeb-402665514a88",
      "e3789a69-8c3a-32d1-e515-1e1bbb601de1",
      "e380f952-4c83-3e73-0dea-4539f0bdf019",
      "e3c72cf3-6a64-a2ed-8c8f-69dfa089e589",
      "e3d1bfb7-4b26-251e-66e8-677a4cb0362d",
      "e4472897-ca7d-9e21-0d82-2b8e83a4064f",
      "e46b3bfb-2f7c-6f76-d174-eda79ded9686",
      "e4fa22f0-a063-eee0-d7db-c64412dcd3f5",
      "e54cb2bd-0991-a935-1c64-8733e139d96e",
      "e5913a3b-0ddf-3006-1b42-bb1b2640a748",
      "e5d0627b-a7a5-c151-ae1e-4dcd48bb9983",
      "e5eebb47-7818-27d1-a83a-dd54f1d1033b",
      "e60ed4db-d840-e44b-416e-9c9f27b2932f",
      "e63c0dfb-bff7-a1a1-8f3a-84dc48cab3b2",
      "e65ddf51-7b60-3578-57a0-36997a5bb425",
      "e6998624-8225-68af-17c0-1b44b7df3d1c",
      "e6a7fe1b-eeae-5e58-9a67-b255931653c7",
      "e6f9a325-02a2-fb2a-85fd-28ca16afbccf",
      "e7081c24-ddad-8ee4-6eac-4d386b0dc36b",
      "e711bd89-7df2-904b-2472-2972022f4839",
      "e714e94c-b208-3b4b-4318-8b2dbb7c0200",
      "e739383f-adf0-8b84-1326-fdde32a9e8cd",
      "e761c576-16a1-c058-4aa6-58df7f447436",
      "e7a5fae4-049e-f75c-ca82-97838d9c37a3",
      "e7b09558-b4e4-d87a-766f-91a2f8518cbb",
      "e7c4ca7e-d0cc-1ae3-fb36-dbc9d73ffbc9",
      "e7cac818-d71b-6823-b66d-49a123afda02",
      "e7ce18a8-61f6-b562-c338-7709830030fa",
      "e7e7294b-f915-4846-1e8c-eda0f7022430",
      "e860e4e4-fce3-08c1-c97e-97e9ce859435",
      "e89e85bb-40aa-03c1-3197-1db87aa61207",
      "e92178ee-c616-1629-b20d-f12bec381b04",
      "e9335fec-4d38-38cb-fbad-25dff403d5b7",
      "e93c7055-0c67-e6b1-2dd3-6069d53c1c2e",
      "e99473e9-999c-66da-7997-c22f9c007a08",
      "e9dae09a-8ff7-9f1f-2104-68da27c88f21",
      "e9ef12de-d81c-f872-2f2c-b861751ad8d2",
      "e9fa2305-e7fe-a04d-41fd-9c9382bba390",
      "e9fb8b6b-5809-d292-c348-2b0962e8eca6",
      "ea0be47e-99ed-ca63-8386-c5d2c5caaef6",
      "ea2159f2-e93b-4a94-f8bd-3fd37b9298c5",
      "ea258620-a86d-62f7-7caf-fc2e4cca8d30",
      "ea61ca6f-f2dc-6ce2-c488-53992bb56fec",
      "eac3f5de-47ee-8ebc-2c06-9aa619e42e6f",
      "eaca5708-e469-40f3-2c12-8da66b2dfa4c",
      "eaf3e1be-ec85-a0d1-3dea-c11e6e4aa63a",
      "eb0d969b-65f2-1ab7-8471-d514c443f569",
      "eb107684-f4ed-a90f-5ebe-580369c9b08a",
      "eb300dab-65c4-49f0-5a82-4cbb1e08d2d1",
      "eb57bb9e-e574-a7a9-b285-48c6f9f3786a",
      "eb6f6d9d-0718-0bf0-cd06-80f8e5930e22",
      "eb7dc658-a74c-2170-54f1-577b5379970d",
      "eb8abca0-a5c8-5b39-9ea3-f044196efde4",
      "eb93065d-b188-1aad-7f9a-7c330be3dc25",
      "ebdad1d2-5eae-e9ce-e99d-ff3d5a84a82a",
      "ebe838e1-6ce2-680f-0b1e-8c4a533e5464",
      "ec3b4575-60e7-a499-c37f-c9ae12b11e0e",
      "ec59f453-1855-f508-32d2-f63f10699526",
      "ecb5781d-7c00-ac84-5831-c2d971c76b9b",
      "ecb9fcda-9a8e-86e9-b548-12327b05fe26",
      "eccc9735-913f-1ea9-2d80-146da0716d28",
      "ecdb4257-4905-f6a4-e28f-656ea33ab87c",
      "ed31d169-323a-f162-6020-3882412d04d8",
      "ed70c501-bf76-ffed-7f7e-59421d4f35f3",
      "eddbefb5-44ff-415b-f9c7-8f0b2c63fe5e",
      "ee010e8e-ab94-0cd2-d826-4a0e144c4ffa",
      "eea2ec2e-9e02-f19c-81ab-238412df461f",
      "eea6f80c-b871-fad7-f989-7021f8db58e0",
      "ef0af52f-cdb2-f336-d575-22848a385532",
      "ef2335ef-865d-4921-c2e5-977ec4dc9ac2",
      "ef3d8eef-ad86-0910-7c0b-550eeb8f5451",
      "ef51d721-4265-e390-f4c0-2dcef95c03d9",
      "ef881666-f6d3-c565-a332-8b1c32d6767b",
      "efcb95a0-6f5b-fba1-f700-465ba398fcef",
      "efd15ca8-c9fd-e5b5-fd7c-153c5417b7ec",
      "efeb6a1e-c75b-3d03-0db8-ebf4d67528bd",
      "f054397a-1259-52a6-55bf-0d870a5e19a5",
      "f07ca678-966f-7c86-2ff7-1e2e5c789f00",
      "f0816440-682a-f732-697d-16863f42e962",
      "f0821ab7-a335-b778-a899-2e4096e77ab8",
      "f086dd03-5f1d-d609-0481-6437df537f6f",
      "f098ca40-9b7b-ac95-36e5-cdecc772bc8e",
      "f0ea43e7-de92-1bd8-83b8-07525c223864",
      "f0efb4e5-2f72-55a7-cee4-10d0a5f0efe1",
      "f1151544-8f4b-8366-9a6f-34d169b63e93",
      "f13d3eb6-1328-46b1-e6d8-bcbcf32f9f88",
      "f18c11cc-0c7e-f94d-9d96-d2b970763b61",
      "f197ff05-afd2-ce9b-577c-de4381d9d0dc",
      "f1d89459-9368-d428-634d-64dbf50df233",
      "f228c3c1-e50a-135f-86fc-f39f6be3e994",
      "f248d8c3-9cde-2774-fa1b-268e5d9915be",
      "f255031c-4d59-a77e-c3c4-07360beb6a70",
      "f2a42dc6-736e-c19b-3734-eb3e01e6e936",
      "f2bcbb9d-d6bd-1323-ab8c-58ef05672709",
      "f2e0653b-18c3-91e4-84a4-0f361625b012",
      "f34ffdd6-6f1e-d798-d8e8-3a9667727a50",
      "f35c1682-0ad8-c906-053c-8ec3c11842af",
      "f3fc55cb-a112-5768-f291-7aa7e6e6910d",
      "f40fa041-b021-e7be-30cd-72a3878e1a5c",
      "f41fd1aa-0799-bc5f-a72d-f53c68cccea5",
      "f4301ac0-7b76-7251-fa56-7151d12627c0",
      "f46afc5b-48d2-01e2-26df-96de77496f2e",
      "f485f448-6463-bf0c-5c73-5612226e638f",
      "f49674cd-6d23-cee5-afc3-2ab0c44378c2",
      "f4b3d190-9819-f908-08d3-428aaaad3863",
      "f4c12d61-e836-d654-58a5-fe93bf176a87",
      "f4e29300-d124-0127-52fb-14d5be0f1ef6",
      "f5232418-3705-8661-dda0-651e98d991b6",
      "f57c5e07-d978-28da-be57-ba8699acc3f8",
      "f5940a1e-17a9-6ad1-da0e-ef7bf8e5d738",
      "f5a5c886-4f07-a6a9-729d-44ccd2ca053a",
      "f5b1db8f-9d56-d29e-2ed3-67d0159e2807",
      "f5d0ddfb-dc85-27c0-9491-279a16aa736b",
      "f5e25625-e4c0-0e8e-4c5a-7aa0601bc418",
      "f5e64514-8fc9-7102-5d02-cc8964685457",
      "f62833bc-4e33-5c5a-16a7-30f44f0dfb16",
      "f6357b69-8777-4f27-73a1-6639e4421881",
      "f69b4ac2-c7c0-9aef-a771-2e558daa4ce1",
      "f6aeab43-7d7b-4476-fe6d-bbe69de759fe",
      "f75a4003-4438-e59e-7d69-4d70f99a81a1",
      "f7d03d9a-23f1-83f9-afb7-770f58aec66c",
      "f7f0235f-f1f3-15d2-ccf5-b870a48b2963",
      "f8ad1f8b-aa8f-75cf-c387-ee2fb475e9b7",
      "f8e9fc76-8f79-3ba2-865c-86297dc7736b",
      "f904c9f7-f66c-3fb3-0783-0460f363145c",
      "f94a18d9-2212-e986-d735-e8d6517f51c7",
      "f999102b-70e4-326e-b4b7-212381fa92b6",
      "f9d83b86-6120-7b43-ec87-953ea6d355eb",
      "f9f3ee49-dd77-1283-b2ef-03abe5e9b4ee",
      "f9f607e8-cae7-06d8-4518-e4bfb811cf48",
      "fa9831c6-6775-a870-e951-3dc897a8c398",
      "fab66767-82dc-d7ff-0214-48dc81671e1d",
      "faee7d4e-e7c7-6b61-6162-8bfbc9873b44",
      "fb199ce7-c83e-19ee-ddd3-cb678550198e",
      "fb903198-40d0-d653-e337-c5a78e92602a",
      "fc0129e3-c37e-b179-e94b-e65159182b50",
      "fc0c17bf-cf1e-736f-2ade-d0520a71e6eb",
      "fc2a9a46-ecb8-7b64-a029-35a65d96b24b",
      "fc2c10cf-d31e-3c6a-5748-8e8e1db47718",
      "fc46a616-3618-60bc-a085-aa97017af805",
      "fcb28975-0bc5-e238-cdec-aceb492b7ddb",
      "fd0b20bc-4cce-db68-f3d1-5689f740af1b",
      "fd60def2-8c4e-a7b0-97b3-7b4a14996ff1",
      "fd6c8b1b-fb78-ab68-ae62-7c279a5f23f4",
      "fdc03c36-ee06-7477-74f4-673a4497fcdc",
      "fe1be7b9-f02e-daf5-2bb4-2fdcdfefe12b",
      "fe3f1ace-2794-6bdf-1e0d-7340c089a2f1",
      "fe3ff1fd-a9c4-18b8-585a-de1977f72e68",
      "fe4a601e-5ea1-ebe2-237f-57e1797814d8",
      "fe679bba-77d8-3999-4971-fadc7385be80",
      "fe9e3b28-ae3f-606d-39b5-eba2e817c2cd",
      "feac5974-899d-282b-f5cf-c9fabb9d9958",
      "feaffd78-30aa-6c1c-d1df-d14f31e98ded",
      "fec44bff-0abd-e91a-a277-550beba03fbf",
      "fec7937b-b7fb-3cb6-3b80-30cbcbefc07c",
      "fef43715-094d-af8e-8056-a4352e57eb4e",
      "fefdf415-49e2-0b92-2a44-68af13b388d8",
      "ff176f6d-d64b-12fa-9d50-98a7f02bcee0",
      "ff22b4ef-785e-c033-fd9d-4429492b3ac6",
      "ff270387-accb-0a70-bf15-3a7ce1232ca2",
      "ff4d67c7-ee7e-3876-1e73-afa2b7e500fa",
      "ff811e0e-4e3f-f433-c5e8-481e818ec140",
      "ffb8fdc4-b3e9-a755-ddb2-ee062c30e2da",
      "ffca3d0e-6852-2c7f-a626-ce5d701ae2bb",
      "ffcd2c36-e746-3f85-e2d7-e73f4f0a9e28"
    ]
  },
  "mutations": [
    {
      "id": "bo6-resume-live-park-v3",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "bo6-completed-filter-v3",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "bo6-tail-fail-closed-v3",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "bo7-candidate-first-v3",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "bo7-rescue-first-v3",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "bo7-earliest-park-v3",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "bo7-stable-identity-v3",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "bo6-tail-persisted-failure-v4",
      "mechanical_status": "ok",
      "source_sha256": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    }
  ],
  "evidence": [
    {
      "id": "receive-assistant-project-rebuild",
      "path": "_workflow/wave3-bo6bo7-receive/regression/final/assistant-project-rebuild.log",
      "level": "build",
      "binding": "current",
      "purpose": "Integrated mainline rebuild of the assistant product project with deployment disabled.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0; deployment target not written (see receive-deploy-target-before).",
      "file_sha256": "3022f7b91c48ef0839afb3b51636546f84d3fb4257e68576ff874845e9654429"
    },
    {
      "id": "receive-testproject-rebuild",
      "path": "_workflow/wave3-bo6bo7-receive/regression/final/testproject-rebuild.log",
      "level": "build",
      "binding": "current",
      "purpose": "Integrated mainline rebuild of the assistant unit-test project.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0.",
      "file_sha256": "13c57566d740d4f2ac1afd4558f4fb90510af0bef3d191eb2c545840c140851f"
    },
    {
      "id": "receive-targeted-93",
      "path": "_workflow/wave3-bo6bo7-receive/regression/final/targeted-93.trx",
      "level": "test",
      "binding": "current",
      "purpose": "Integrated-version targeted runner/local-wait suite (3 classes); the counterexample evidence for the behaviour risk rows.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "dotnet test --no-build --filter <LocalWaitIdentityTranslationTests|LocalWaitParkingStateContractTests|WorkflowRunnerTests>; 93/93 passed; executed after the final claim-surface regeneration so the binding is exact.",
      "file_sha256": "3769703968a4bad368d1be4a57a9994ccfd7e794820a39d9f9fe991650406fbe"
    },
    {
      "id": "receive-full-1564",
      "path": "_workflow/wave3-bo6bo7-receive/regression/final/assistant-full-1564.trx",
      "level": "test",
      "binding": "current",
      "purpose": "Integrated-version full assistant regression.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "dotnet test --no-build (no filter); 1562 passed / 2 skipped / 0 failed / 1564; executed after the final claim-surface regeneration.",
      "file_sha256": "2881fcb25d1ffe7c590783ae8a07436d213ef928e72b895ebbd8debd10353f92"
    },
    {
      "id": "receive-claim-surface-regen",
      "path": "_workflow/wave3-bo6bo7-receive/regression/claim-surface/regen/claim-surface-regen.trx",
      "level": "test",
      "binding": "current",
      "purpose": "Declaration-surface regeneration after the R5.3 24.126 registration was added.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "CLAIM_SURFACE_REGENERATE=1; ClaimSurfaceGuardTests 1/1 passed; manifest 599 -> 602 lines (+3/-0).",
      "file_sha256": "f4d6d70716f74af1a231005c768c72bb543e72409a3acd0f29701fbbd20be0d6"
    },
    {
      "id": "receive-claim-surface-no-env",
      "path": "_workflow/wave3-bo6bo7-receive/regression/claim-surface/no-env/claim-surface-no-env.trx",
      "level": "test",
      "binding": "current",
      "purpose": "Declaration-surface guard re-run with the regeneration variable cleared.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "CLAIM_SURFACE_REGENERATE unset; ClaimSurfaceGuardTests 1/1 passed; manifest SHA stable at 5D914611... across regen and no-env.",
      "file_sha256": "b7eb70498b9d44d549fabeb837307823a72c45e235e9d92cb0f1809c3d394187"
    },
    {
      "id": "receive-fact-testids",
      "path": "_workflow/wave3-bo6bo7-receive/regression/final/targeted-fact-testids.json",
      "level": "document",
      "binding": "current",
      "purpose": "testIds of the four new real-Runner facts and the six relocated/changed helper fixtures.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Extracted from receive-targeted-93.trx by testMethod name.",
      "file_sha256": "35af90f926c81123b98e9602b10b3229f1e56b58b7da61b0175e958e2f87dcaa"
    },
    {
      "id": "receive-testid-comparison",
      "path": "_workflow/wave3-bo6bo7-receive/regression/testid-comparison-receive.json",
      "level": "document",
      "binding": "current",
      "purpose": "testId-level structural comparison of the opening baseline full TRX against the integrated full TRX.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "tools/mistletoe/workflow.py trx --baseline <opening full> --final <integrated full>; 1556 unchanged / 8 added / 4 removed / 0 changed; added and removed id sets identical to the source delivery.",
      "file_sha256": "cb0167a82ce4a9487c1def98df2e6dc03ac54d55cf86d61423bb3d87ca62e9e4"
    },
    {
      "id": "receive-targeted-identity",
      "path": "_workflow/wave3-bo6bo7-receive/regression/targeted-testid-identity.json",
      "level": "document",
      "binding": "current",
      "purpose": "Proof that the integrated targeted suite has exactly the source delivery final targeted testId set.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "93 testIds, names and outcomes identical to the source worktree final targeted TRX.",
      "file_sha256": "3b6c4690ac9025eaeb946f5446503385d916c99cd9646ae47fe588dfa5d02cff"
    },
    {
      "id": "receive-mutation-records",
      "path": "_workflow/wave3-bo6bo7-receive/mutation-records.json",
      "level": "document",
      "binding": "current",
      "purpose": "Machine-readable records of the eight reverse mutations re-executed on the integrated bytes.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Produced by _workflow/wave3-bo6bo7-receive/run-receive-mutants.ps1 under pwsh 7; per-mutation build/test logs and TRX under _workflow/wave3-bo6bo7-receive/mutations/<id>/.",
      "file_sha256": "f82e8c08c31b28a93765274fe86789268391b33fbeb2fdc5f9d0c158bfb2e66d"
    },
    {
      "id": "receive-manifest-diff",
      "path": "_workflow/wave3-bo6bo7-receive/regression/claim-surface/manifest-diff-summary.json",
      "level": "document",
      "binding": "current",
      "purpose": "Reviewed diff of the declaration-surface regeneration.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Exactly 3 added lines (all from the new R5.3 24.126 text), 0 removed, no pre-existing claim line altered.",
      "file_sha256": "b35e2e81462f0cadc7120e08719ff89684efb10011648c4d7cc532e7355e4f6f"
    },
    {
      "id": "receive-deploy-target-before",
      "path": "_workflow/wave3-bo6bo7-receive/deploy-target-before.txt",
      "level": "document",
      "binding": "current",
      "purpose": "Deployment-target state captured before the first build; the same LastWriteTimeUtc/file count holds afterwards.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Path BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant; 1158 files; 09/26/2026 21:41:14 UTC unchanged after all builds/tests.",
      "file_sha256": "40eac866780d6f45b9a7d1d7f66e2dbc334140a92e4164a2be314f289ef780b4"
    },
    {
      "id": "receive-intake-note",
      "path": "_workflow/wave3-bo6-bo7/INTAKE-NOTE.md",
      "level": "document",
      "binding": "current",
      "purpose": "Boundary note for the partially imported source evidence directory.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "States what is imported and what remains only in the retained source worktree.",
      "file_sha256": "5c6a4869b94779e4bfea567dbd19f531341bdd16455b14da214cbc117c552c70"
    },
    {
      "id": "receive-findings",
      "path": "_workflow/wave3-bo6bo7-receive/findings.md",
      "level": "document",
      "binding": "current",
      "purpose": "Batch findings, mutation summary, regressions, and residual items.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Packet findings role.",
      "file_sha256": "5dae61b0ff7374416a4d2c0e7210cbf1c20b6d5338458fd990bea948c707c82c"
    },
    {
      "id": "receive-budget",
      "path": "_workflow/wave3-bo6bo7-receive/budget.md",
      "level": "document",
      "binding": "current",
      "purpose": "Consultation budget, fixed model/strength, original severities, unclosed items.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Packet budget role.",
      "file_sha256": "efcc8930d9574e8d4d6fe4cf48dcf1b58ec188564d67cc8436560032c068f9b8"
    },
    {
      "id": "receive-context",
      "path": "_workflow/wave3-bo6bo7-receive/context.md",
      "level": "document",
      "binding": "current",
      "purpose": "Batch objective and completion criteria.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Packet objective role.",
      "file_sha256": "68953edeb4e0ae10235dcd5d08915b276ee9d0da344131ae3a2477f70286f283"
    },
    {
      "id": "source-owner-checkpoint",
      "path": "_workflow/wave3-bo6-bo7/owner-checkpoint.md",
      "level": "document",
      "binding": "current",
      "purpose": "Authoritative source checkpoint; SHA-256 A4AB77476298FD7AB3847930C0A92BA72F234424EC6A4D3B1706AB17809C75B1 verified at intake.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "c365370e4d963fe5d66d755120e81202bf5f0bc3cfd822e47e68ab8833f611e9"
    },
    {
      "id": "source-risk-matrix",
      "path": "_workflow/wave3-bo6-bo7/risk-matrix.json",
      "level": "document",
      "binding": "current",
      "purpose": "Source batch risk matrix and its coverage mapping.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "1c9843aaf1d29fa5ba3fe5706322c2730bc603ba60e858c43123ac35d089d4aa"
    },
    {
      "id": "source-mutation-index",
      "path": "_workflow/wave3-bo6-bo7/mutations/raw-mutation-evidence-index.md",
      "level": "document",
      "binding": "current",
      "purpose": "Source batch mutation evidence index.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "04159b526f8ad77365044f3f885bd7b1b7d4a1ef74bfeedf17314d6955895594"
    },
    {
      "id": "source-ledger-reconciliation",
      "path": "_workflow/wave3-bo6-bo7/consultation/current-ledger-reconciliation.json",
      "level": "document",
      "binding": "current",
      "purpose": "Source consultation ledger reconciliation (8/8 conservative count, balance 0).",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "15313b6fbb1efa5edbca3f6630119170d9f24854e28f53b2112900a3b1224829"
    },
    {
      "id": "source-disposition-9-10",
      "path": "_workflow/wave3-bo6-bo7/consultation/owner-approved-requests-9-10.md",
      "level": "document",
      "binding": "current",
      "purpose": "Item-by-item disposition of the two owner-approved additional consultations.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "6b6aea8e75c20bdd9719a1660c7661789b59aea0353a9ffab4430084402c3734"
    },
    {
      "id": "source-testid-comparison",
      "path": "_workflow/wave3-bo6-bo7/regression/final/testid-comparison-final.json",
      "level": "document",
      "binding": "current",
      "purpose": "Source delivery testId comparison used for the independent cross-check.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "9325a28b118604ad588a727a4bc310f55785226054c2d7ed5e23b5062c230e65"
    },
    {
      "id": "source-context",
      "path": "_workflow/wave3-bo6-bo7/context.md",
      "level": "document",
      "binding": "current",
      "purpose": "Source batch objective (context role).",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "55be4367678e8881b33fdea147a4241759da94b230f4dbba0f8a28a73a599585"
    },
    {
      "id": "source-findings",
      "path": "_workflow/wave3-bo6-bo7/findings.md",
      "level": "document",
      "binding": "current",
      "purpose": "Source batch findings (contrast material).",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "41af621a8d1def2b741cc7edf181d29e796b201e34b20bea1da427d37b45419d"
    },
    {
      "id": "source-budget",
      "path": "_workflow/wave3-bo6-bo7/budget.md",
      "level": "document",
      "binding": "current",
      "purpose": "Source batch budget ledger (contrast material).",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.",
      "file_sha256": "577e5dd2e5b903e71589508fedd3bc7ac049445cad59c8c4cb194735dac51ed7"
    },
    {
      "id": "source-review-a-bo6",
      "path": "_workflow/wave3-bo6-bo7/review-cli-a-bo6/run/review.md",
      "level": "consult",
      "binding": "current",
      "purpose": "Independent read-only review report closing BO-6 R19 MUST and BO-6 R21 F4 IMPORTANT at the delivered bytes.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Local Codex CLI read-only, gpt-6-astra / medium, exit 0, 182.8 s, fixed snapshot HEAD 0f46128f; report SHA-256 de8a8176...b4e8bc.",
      "file_sha256": "4d0a8cd77fc4c39d3841d83e6ab9cc2f6f3640721ae3f238c92acadeea0fdc8e"
    },
    {
      "id": "source-review-b-bo7",
      "path": "_workflow/wave3-bo6-bo7/review-cli-b-bo7/run/review.md",
      "level": "consult",
      "binding": "current",
      "purpose": "Independent read-only review report closing BO-7 R21 F1/F2 MUST at the delivered bytes.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Local Codex CLI read-only, gpt-6-astra / medium, exit 0, 183.0 s, fixed snapshot HEAD 0f46128f; report SHA-256 5b886136...438479.",
      "file_sha256": "b385e7732c83b51e7b0e9a256efd1c13e3e252912f9fd8d142bceecc37bd59e9"
    },
    {
      "id": "source-review-a-meta",
      "path": "_workflow/wave3-bo6-bo7/review-cli-a-bo6/run-metadata.json",
      "level": "document",
      "binding": "current",
      "purpose": "Run metadata (model, strength, read-only, exit code, usage) for the BO-6 review.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Request 9; model verified from the local rollout record.",
      "file_sha256": "6fd057a5100a7429c8304662cb4007052af74b1623af0debf518c481e01ff817"
    },
    {
      "id": "source-review-b-meta",
      "path": "_workflow/wave3-bo6-bo7/review-cli-b-bo7/run-metadata.json",
      "level": "document",
      "binding": "current",
      "purpose": "Run metadata (model, strength, read-only, exit code, usage) for the BO-7 review.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Request 10; model verified from the local rollout record.",
      "file_sha256": "992eb9090ce17609063ca028a942a350870cfc92854f61b97eb9b3f63f688296"
    },
    {
      "id": "opening-baseline-build",
      "path": "_workflow/wave3-bo6bo7-receive/baseline/testproject-build.log",
      "level": "build",
      "binding": "historical",
      "purpose": "Pre-intake mainline baseline build (opening HEAD 6fd6207e5).",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "858c3ba6be3385c4a7701efe41efc3a6cff56f262cad0549996d72f1a3515972",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "6f206e3bd36e97ac9b5f6faaf2c5fcab90ebc9510313570829bbf071f4292270",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e"
      },
      "conditions": "dotnet build -t:Rebuild -p:DeployToBgiTools=false; exit 0; executed before any file was imported.",
      "file_sha256": "8b25fb731dbf94734dbac492165f96ae280f7b09446ac160cad1129df9d081f2"
    },
    {
      "id": "opening-baseline-targeted",
      "path": "_workflow/wave3-bo6bo7-receive/baseline/targeted-baseline.trx",
      "level": "test",
      "binding": "historical",
      "purpose": "Pre-intake targeted three-class baseline (89 tests) for the testId differential.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "858c3ba6be3385c4a7701efe41efc3a6cff56f262cad0549996d72f1a3515972",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "6f206e3bd36e97ac9b5f6faaf2c5fcab90ebc9510313570829bbf071f4292270",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e"
      },
      "conditions": "Same filter as receive-targeted-93; 89/89 passed; 4 helper tests carry their pre-intake names.",
      "file_sha256": "4762e8edbadce100cdfef099b1e1474bf46a9fb36411ea0d7f6a18f4d5faf283"
    },
    {
      "id": "opening-baseline-full",
      "path": "_workflow/wave3-bo6bo7-receive/baseline/assistant-full-baseline.trx",
      "level": "test",
      "binding": "historical",
      "purpose": "Pre-intake full assistant baseline; same-condition reference for the comparison.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "858c3ba6be3385c4a7701efe41efc3a6cff56f262cad0549996d72f1a3515972",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "6f206e3bd36e97ac9b5f6faaf2c5fcab90ebc9510313570829bbf071f4292270",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e"
      },
      "conditions": "1558 passed / 2 skipped / 0 failed / 1560; matches the source delivery opening baseline.",
      "file_sha256": "b39b4db49ca099364f8dfc83dbbfdc4490ec2e730972187a6914e99dab4548c7"
    },
    {
      "id": "receive-preRegen-targeted",
      "path": "_workflow/wave3-bo6bo7-receive/regression/targeted-integrated-93.trx",
      "level": "test",
      "binding": "historical",
      "purpose": "Intermediate integrated targeted run executed before the declaration-surface regeneration.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "93/93; superseded by receive-targeted-93 after ClaimSurfaceManifest.txt changed, kept for traceability (same outcome, not used as current green evidence).",
      "file_sha256": "f9ff310392b9bf46382164425f2a3630a5f11b2bd587b1f208d6bfbb855829ec"
    },
    {
      "id": "receive-preRegen-full",
      "path": "_workflow/wave3-bo6bo7-receive/regression/assistant-full-integrated.trx",
      "level": "test",
      "binding": "historical",
      "purpose": "Intermediate integrated full run executed before the declaration-surface regeneration.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "1562/2/0/1564; superseded by receive-full-1564 for the same reason; kept for traceability.",
      "file_sha256": "ce938dbbc4fe434e15d4d6135e1318efc45bc671eb35e11049ee511fef14c56c"
    },
    {
      "id": "receive-testid-comparison-summary",
      "path": "_workflow/wave3-bo6bo7-receive/regression/testid-comparison-summary.json",
      "level": "document",
      "binding": "current",
      "purpose": "Reviewable testId differential summary (counts, added/removed id sets, and the two retained-id tests whose assertions changed).",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Derived from testid-comparison-receive.json; the generator output itself (~1.0 MB) is not attached because it embeds two full parsed TRX row sets.",
      "file_sha256": "e6ccfd4e1e5c1034d2d066fe0b21425b589883291ebb021b154da23faa617c9e"
    },
    {
      "id": "receive-consult-request",
      "path": "_workflow/wave3-bo6bo7-receive/consultation/review-request-v1.md",
      "level": "document",
      "binding": "current",
      "purpose": "Frozen consultation request: objective, six questions, boundaries, material list, exclusions.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Written before dispatch so the reviewed scope cannot drift with the result.",
      "file_sha256": "f33099e3ccb5493994b6a9cea35a0dc6698d62d68bcb5d6cf05327284f39d9b8"
    },
    {
      "id": "receive-consult-preflight",
      "path": "_workflow/wave3-bo6bo7-receive/consultation/preflight-v1.json",
      "level": "document",
      "binding": "current",
      "purpose": "Capacity pre-check record: allowed files, bytes, token estimate, transport limits, exclusions, fallback assessment.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "conditions": "Local computation only; no request dispatched by this step.",
      "file_sha256": "168b2b0a99ed258c2b481e343931d83542fc5f2eead19a55ce41fc32487e0695"
    },
    {
      "id": "verify-intake-equivalence",
      "path": "_workflow/wave3-bo6bo7-receive/verification/intake-equivalence.json",
      "level": "document",
      "binding": "current",
      "purpose": "Per-file import fidelity (index blob == delivery blob) plus byte-identical comparison of the imported delta against the source delivery delta, and the post-import modification list. (Review round 1 IMPORTANT-1 repair evidence.)",
      "conditions": "delta_byte_identical=true; both deltas SHA-256 0642971729f3eff0...; only the four status documents and ClaimSurfaceManifest.txt changed after import.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "2efda41e5e3a420a2cefa4befc1715237f4014e286bac2b09ec4dc2d09767509"
    },
    {
      "id": "verify-import-delta",
      "path": "_workflow/wave3-bo6bo7-receive/verification/import-delta-8files.diff",
      "level": "document",
      "binding": "current",
      "purpose": "Complete imported delta over all eight source files, including the four status-document patches that review round 1 found missing. (IMPORTANT-1 repair evidence.)",
      "conditions": "git diff --cached --no-ext-diff --no-textconv -- <8 files>; byte-identical to the source delivery delta.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "0642971729f3eff09bde3bf4a437865710ac4966ffd814cb42e36d55d910b3b4"
    },
    {
      "id": "verify-post-import-delta",
      "path": "_workflow/wave3-bo6bo7-receive/verification/post-import-delta.diff",
      "level": "document",
      "binding": "current",
      "purpose": "Everything this batch changed after the verbatim import, so imported bytes are distinguishable from registration edits. (IMPORTANT-1 repair evidence.)",
      "conditions": "git diff -- <8 files>; contains the four registration appends and the mandated declaration-surface regeneration only.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "a6dc2a9cfc343599372a0219431a4a36507a2f2cfdd74914d57457ce078fe960"
    },
    {
      "id": "verify-regression-outcomes",
      "path": "_workflow/wave3-bo6bo7-receive/verification/regression-outcomes.json",
      "level": "document",
      "binding": "current",
      "purpose": "Corrected row-level outcome accounting for every cited run, including the two NotExecuted rows that the earlier summary mis-stated. (Review round 1 IMPORTANT-2 repair evidence.)",
      "conditions": "Counters@notExecuted is 0 in this VSTest/xUnit shape while two rows carry NotExecuted: P50_LoadRepro_WholeClass_UnderControlledLoad and P50_DiagnosticRepeat_OptIn; total = passed + failed + NotExecuted rows in every run.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "bee10282f6518368429e1e8f8d31078a64f37f37deeaee60f9e4a2afc08dfcdd"
    },
    {
      "id": "verify-targeted-93-ids",
      "path": "_workflow/wave3-bo6bo7-receive/verification/targeted-93-ids.json",
      "level": "document",
      "binding": "current",
      "purpose": "Complete 93-entry testId/name/outcome comparison against the source delivery final targeted TRX. (Review round 1 suggestion repair evidence.)",
      "conditions": "testId sets identical; name and outcome identical for every testId; zero mismatches.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "2d3e3bf52276dbf57b60b1b113d4928d4eb01d97d0183066831a4ea3b6fe9de2"
    },
    {
      "id": "verify-mutation-verification",
      "path": "_workflow/wave3-bo6bo7-receive/verification/mutation-verification.json",
      "level": "document",
      "binding": "current",
      "purpose": "Compact per-mutation verification: outcomes, target identity, assertion location, file digests, and the source-vs-receive mutant SHA equality. (Review round 1 suggestion repair evidence.)",
      "conditions": "All eight mutant SHA-256 values equal the source delivery manifest records; every mutation is Passed/Failed/Passed on the integrated bytes.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "cbcc9b0bccf11516a999b6faa06cd0aed85a02288680cbbb95c385a75644d5a2"
    },
    {
      "id": "receive-consult-outcome-v1",
      "path": "_workflow/wave3-bo6bo7-receive/consultation/review-outcome-v1.md",
      "level": "consult",
      "binding": "current",
      "purpose": "Round 1 consultation outcome and item-by-item disposition (2 IMPORTANT kept at original severity, 1 suggestion accepted).",
      "conditions": "gpt-6-astra / medium, existing GPT tool, attempts=1, read-only; batch counter 1/8.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "34f007227459d858d8eda56b21cd6c333abb05b52a1d76bfdac144c34553b6a1"
    },
    {
      "id": "receive-consult-request-v2",
      "path": "_workflow/wave3-bo6bo7-receive/consultation/review-request-v2.md",
      "level": "document",
      "binding": "current",
      "purpose": "Verification-round request (round 2): strict scope of the two IMPORTANT items and the no-new-findings question.",
      "conditions": "Frozen before dispatch so the reviewed scope cannot drift with the result.",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs": "8a334e394acf307e8040d72961c1db412b9a1dcb189423a81aa17c057b6a5ad6",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "5d91461178da98729562b486060a4322f6a96c3469778a064dca52b7d8b4d267"
      },
      "file_sha256": "04eb6f08d9b21f5c2e4f2eed64473a68140a013cbabdeac7ff0e8b36a2a3f09e"
    }
  ],
  "quality_verdict": "NOT PROVIDED"
}

## git status --porcelain (all changes; ownership requires manual classification)
 M Docs/design/mistletoe-session-relay-2026-09-24.md
MM Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
 M Docs/design/unified-job-registry-master-plan.md
M  MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs
MM Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
M  Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs
M  Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs
MM _batch21/b21_plan.md
MM _batch21/sb21-4-handoff-2026-09-28.md
A  _workflow/wave3-bo6-bo7/budget.md
A  _workflow/wave3-bo6-bo7/consultation/current-ledger-reconciliation.json
A  _workflow/wave3-bo6-bo7/consultation/owner-approved-requests-9-10.md
A  _workflow/wave3-bo6-bo7/context.md
A  _workflow/wave3-bo6-bo7/findings.md
A  _workflow/wave3-bo6-bo7/mutations/mutation-summary.md
A  _workflow/wave3-bo6-bo7/mutations/raw-mutation-evidence-index.md
A  _workflow/wave3-bo6-bo7/owner-checkpoint.md
A  _workflow/wave3-bo6-bo7/regression/final/testid-comparison-final.json
A  _workflow/wave3-bo6-bo7/review-cli-a-bo6/run-metadata.json
A  _workflow/wave3-bo6-bo7/review-cli-a-bo6/run/review.md
A  _workflow/wave3-bo6-bo7/review-cli-b-bo7/run-metadata.json
A  _workflow/wave3-bo6-bo7/review-cli-b-bo7/run/review.md
A  _workflow/wave3-bo6-bo7/risk-matrix.json
MM 槲寄生调度器总计划.md
?? .zcodeignore
?? AGENTS.md.bak-20260926-142346
?? AGENTS.md.bak-20260926-1438
?? AGENTS.md.bak-20260927-brake
?? AGENTS.md.bak-auto-handoff-20260927
?? BetterGenshinImpact/GameTask/AutoFight/ArlecchinoAutoEqDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/ArlecchinoBurstGateDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/AutoFightTask.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/CombatHealthDetector.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/Model/Avatar.cs.bak
?? BetterGenshinImpact/GameTask/AutoFightOfficial/OfficialAutoFightRouter.cs.bak
?? BetterGenshinImpact/GameTask/AutoFightOfficial/OfficialParamAdapter.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/CameraRotateDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/CameraRotateTask.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/MiniMapPositionDiagnostics.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/ZeroCoordGuard.cs.bak
?? BetterGenshinImpact/GameTask/Common/CaptureRetryDecisions.cs.bak
?? BetterGenshinImpact/GameTask/Common/FocusRecoveryDecisions.cs.bak
?? BetterGenshinImpact/GameTask/Common/TaskControl.cs.bak
?? BetterGenshinImpact/GameTask/Common/TaskControl.cs.bak2
?? Docs/design/mistletoe-parallel-recovery-2026-09-27.md
?? Docs/design/mistletoe-r62-registration-2026-09-27.md
?? Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md.bak_b16r4_doc
?? Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md.bak_b16r5_1
?? MultiplayerHoeingAssistant.dll
?? MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs.bak_b16r4_2
?? MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitPrerequisiteModels.cs.bak_b16r4_1
?? Test/BetterGenshinImpact.UnitTest/GameTaskTests/AutoPathingTests/DeathRespawnRestartBugConditionTest.cs.stale
?? Test/BetterGenshinImpact.UnitTest/GameTaskTests/AutoPathingTests/DeathRespawnRestartPreservationPbtTest.cs.stale
?? Test/BetterGenshinImpact.UnitTest/TestResults/
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_10
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_11
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_12
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_13
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_3
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_4
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_5
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_6
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_7
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_8
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_9
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationModels.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTrigger.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTriggerTests.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTriggerTests.cs.wip-batch16
?? Test/MultiplayerHoeingAssistant.UnitTest/TestResults/
?? TestResults/
?? _addev.py
?? _addev2.py
?? _aout.txt
?? _apply_docs.py
?? _assist_xamlcheck.log
?? _audit1.txt
?? _audit2.txt
?? _audit3.txt
?? _audit4.txt
?? _audit5.txt
?? _audit6.txt
?? _backup_assistant-config.json
?? _backup_dodoco_settings.json
?? _batch13/
?? _batch14/
?? _batch14_admissionkind_hits.txt
?? _batch15/
?? _batch16/
?? _batch17/
?? _batch19/b19_commit_verify.txt
?? _batch20/
?? _batch21/b21_red.trx
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/evidence.json
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/mutant.log
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/restored.log
?? _batch21/sb21-1-r8-review/accepted-no-job-mutant/
?? _batch21/sb21-1-r8-review/accepted-no-job/
?? _batch21/sb21-1-r8-review/assistant-full-post-r8-2.log
?? _batch21/sb21-1-r8-review/assistant-full-post-r8.log
?? _batch21/sb21-1-r8-review/claim-surface-final-docs/
?? _batch21/sb21-1-r8-review/claim-surface-final-docs2/
?? _batch21/sb21-1-r8-review/claim-surface-final-no-env/
?? _batch21/sb21-1-r8-review/claim-surface-manifest-before.txt
?? _batch21/sb21-1-r8-review/claim-surface-post-r16-no-env/
?? _batch21/sb21-1-r8-review/claim-surface-regen/
?? _batch21/sb21-1-r8-review/claim-surface-verify/
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-reference-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-reference-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-boundary-hold-precedence-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-boundary-hold-precedence-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-integrity-guard-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-integrity-guard-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-resume-control-after-write-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-resume-control-after-write-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-unresolved-send-attempted-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-unresolved-send-attempted-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-wait-reason-sanitization-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-wait-reason-sanitization-restored.log
?? _batch21/sb21-1-r8-review/doc-relay-before-inventory.txt
?? _batch21/sb21-1-r8-review/final-full/
?? _batch21/sb21-1-r8-review/git-diff.txt
?? _batch21/sb21-1-r8-review/git-status.txt
?? _batch21/sb21-1-r8-review/ledger-batch21-before-sb21-1-closeout-refresh.json
?? _batch21/sb21-1-r8-review/ledger-batch21-pre-disposition.json
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/batch51-r16-design-excerpt.txt
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/committed-sb21-1.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-scoped-working-tree.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-staged.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-status-porcelain.txt
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-unstaged.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/ledger-batch21-before-r9-update.json
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/r8-findings-and-dispositions.md
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/review-context.md
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/sb21-1-design-closeout-excerpt.txt
?? _batch21/sb21-1-r8-review/pre-accepted-fact-guard-inventory.txt
?? _batch21/sb21-1-r8-review/pre-repair-source-inventory.txt
?? _batch21/sb21-1-r8-review/previous-reverse-mutants.md
?? _batch21/sb21-1-r8-review/relay-before-final-refresh/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/catch-summary.json
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-balanced/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-balanced/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-valid/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-balanced/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-balanced/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-valid/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-ev1-two-scans-valid/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-ev1-two-scans-valid/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-successor-ranking-mapping-valid/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-successor-ranking-mapping-valid/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/summary.json
?? _batch21/sb21-1-r8-review/reverse-mutation-evidence.md
?? _batch21/sb21-1-r8-review/review-context.md
?? _batch21/sb21-1-r8-review/send-attempted-current-mutant/
?? _batch21/sb21-1-r8-review/targeted-after-r8.log
?? _batch21/sb21-1-reverse-mutants/
?? _batch21/sb21-2-review/assistant-full-baseline/
?? _batch21/sb21-2-review/assistant-full-final/
?? _batch21/sb21-2-review/assistant-full-r5-counter-final/
?? _batch21/sb21-2-review/assistant-full-r5-final/
?? _batch21/sb21-2-review/assistant-full-r5-note-fixes.log
?? _batch21/sb21-2-review/assistant-full-r5-note-fixes/
?? _batch21/sb21-2-review/assistant-full-r5-original-parser-final/
?? _batch21/sb21-2-review/assistant-full-red-baseline/
?? _batch21/sb21-2-review/assistant-full-test-diff-r4.json
?? _batch21/sb21-2-review/assistant-full-test-diff-r4.md
?? _batch21/sb21-2-review/assistant-full-test-diff-strict-parser-r5.json
?? _batch21/sb21-2-review/assistant-full-test-diff-strict-parser-r5.md
?? _batch21/sb21-2-review/assistant-full-test-diff.json
?? _batch21/sb21-2-review/assistant-full-test-diff.md
?? _batch21/sb21-2-review/claim-final-noenv/
?? _batch21/sb21-2-review/claim-final-regen/
?? _batch21/sb21-2-review/claim-manifest-before-r5-closeout.txt
?? _batch21/sb21-2-review/claim-manifest-before-r5-finalize.txt
?? _batch21/sb21-2-review/claim-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-final-noenv.log
?? _batch21/sb21-2-review/claim-r5-closeout-final-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-regen/
?? _batch21/sb21-2-review/claim-r5-counters-final-noenv/
?? _batch21/sb21-2-review/claim-r5-counters-final-regen/
?? _batch21/sb21-2-review/claim-r5-current-docs-noenv.log
?? _batch21/sb21-2-review/claim-r5-current-docs-noenv/
?? _batch21/sb21-2-review/claim-r5-exact-final-noenv-retry.log
?? _batch21/sb21-2-review/claim-r5-exact-final-noenv-retry/
?? _batch21/sb21-2-review/claim-r5-exact-final-regen.log
?? _batch21/sb21-2-review/claim-r5-exact-final-regen/
?? _batch21/sb21-2-review/claim-r5-final-noenv/
?? _batch21/sb21-2-review/claim-r5-final-regen/
?? _batch21/sb21-2-review/claim-r5-final-state-noenv.log
?? _batch21/sb21-2-review/claim-r5-final-state-noenv/
?? _batch21/sb21-2-review/claim-r5-noenv.log
?? _batch21/sb21-2-review/claim-r5-noenv/
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-noenv.log
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-noenv/
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-regen.log
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-regen/
?? _batch21/sb21-2-review/claim-r5-regen.log
?? _batch21/sb21-2-review/claim-r5-regen/
?? _batch21/sb21-2-review/claim-regen/
?? _batch21/sb21-2-review/claim-review-noenv/
?? _batch21/sb21-2-review/claim-review-regen/
?? _batch21/sb21-2-review/consultation-ledger-before-r4-record.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-dispatch.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-finalization.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-update.json
?? _batch21/sb21-2-review/implementation-pre-edit-inventory.json
?? _batch21/sb21-2-review/item-generation-overflow-diagnostic/
?? _batch21/sb21-2-review/item-generation-overflow-fixed-v2/
?? _batch21/sb21-2-review/item-generation-overflow-fixed-v3/
?? _batch21/sb21-2-review/item-generation-overflow-fixed/
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/LocalWaitQueueStore.strict-current.cs
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/original-source-nonincr-build.log
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/original-source-overflow-test.log
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/test-results/
?? _batch21/sb21-2-review/ledger-pre-r5-finalization.json
?? _batch21/sb21-2-review/ledger-pre-r5-update.json
?? _batch21/sb21-2-review/legacy-gap-v2/
?? _batch21/sb21-2-review/localwait-r5-counter/
?? _batch21/sb21-2-review/localwait-r5-final/
?? _batch21/sb21-2-review/localwait-r5-note-fixes.log
?? _batch21/sb21-2-review/localwait-r5-note-fixes/
?? _batch21/sb21-2-review/localwait-r5-original-parser-final/
?? _batch21/sb21-2-review/localwait-suite-final-pre-docs/
?? _batch21/sb21-2-review/localwait-suite-final/
?? _batch21/sb21-2-review/localwait-suite-v1/
?? _batch21/sb21-2-review/mutations-r3/legacy-reservation/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow-v2/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow-v3/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow/
?? _batch21/sb21-2-review/mutations-r5/c5-consume-generation/
?? _batch21/sb21-2-review/original-parser-confirmation-build.log
?? _batch21/sb21-2-review/original-parser-confirmation/
?? _batch21/sb21-2-review/pre-edit-inventory.json
?? _batch21/sb21-2-review/pre-implementation-inventory.json
?? _batch21/sb21-2-review/pre-r5-closeout/b21_plan-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/handoff-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/r5-3-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/red-results-summary-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/reverse-mutations-before-r5.md
?? _batch21/sb21-2-review/r4-item-overflow-fix-pre-edit.json
?? _batch21/sb21-2-review/r5_3_anchor_excerpt.md
?? _batch21/sb21-2-review/r5_3_sb21_2_current_excerpt.md
?? _batch21/sb21-2-review/red-final-before-implementation-v2/
?? _batch21/sb21-2-review/red-final-before-implementation/
?? _batch21/sb21-2-review/red-r1/
?? _batch21/sb21-2-review/red-r2/
?? _batch21/sb21-2-review/red/
?? _batch21/sb21-2-review/review-r5-material-out-staged.diff
?? _batch21/sb21-2-review/targeted-final/
?? _batch21/sb21-2-review/targeted-r5-counter/
?? _batch21/sb21-2-review/targeted-r5-generation-final/
?? _batch21/sb21-2-review/targeted-r5-generation/
?? _batch21/sb21-2-review/targeted-r5-note-fixes.log
?? _batch21/sb21-2-review/targeted-r5-note-fixes/
?? _batch21/sb21-2-review/targeted-r5-original-parser-final/
?? _batch21/sb21-2-review/targeted-v1/
?? _batch21/sb21-2-review/targeted-v2/
?? _batch21/sb21-2-review/targeted-v3/
?? _batch21/sb21-2-review/test-project-build-r5-counter-nonincr.log
?? _batch21/sb21-2-review/test-project-build-r5-note-fixes-nonincr.log
?? _batch21/sb21-2-review/test-project-build-r5-original-parser.log
?? _batch21/sb21-3-review/
?? _c16out.txt
?? _cmp_targeted.py
?? _csdiff.py
?? _dpiprobe/
?? _eol_probe.py
?? _extprobe/
?? _factids.py
?? _fixdocs.py
?? _hashprobe.py
?? _incidentprobe/
?? _mergebuild.log
?? _mergebuild2.log
?? _mergebuild3.log
?? _mergebuild4.log
?? _mergebuild5.log
?? _mergebuild6.log
?? _mergebuild7.log
?? _mergebuild8.log
?? _mergebuild9.log
?? _mkdocs.py
?? _mkmanifest.py
?? _mkscript.py
?? _mktrxcmp.py
?? _mutcheck.py
?? _note.py
?? _out1.py
?? _preflight.py
?? _preflight2.py
?? _probe/
?? _probe_taskline.png
?? _probe_taskline2.png
?? _r17.txt
?? _r5_batch9_assistant_full.log
?? _r5_test_temp/
?? _req.py
?? _scaffold_receive.py
?? _slim.py
?? _statusprobe/
?? _styleprobe/
?? _tools/
?? _trxdetail.py
?? _uidmask_preview.png
?? _v1.py
?? _v2.py
?? _v3.py
?? _v4.py
?? _workflow/import-integration/
?? _workflow/import-pilot-r56/
?? _workflow/parallel-recovery/
?? _workflow/parallel-registry-integration/
?? _workflow/r62-registration/
?? _workflow/sb21-3-bo4/
?? _workflow/sb21-4/baseline/
?? _workflow/sb21-4/claims/
?? _workflow/sb21-4/closeout-final-20260928-v2/
?? _workflow/sb21-4/closeout-final-20260928-v3/
?? _workflow/sb21-4/closeout-final-20260928-v4/scoped-staged.diff
?? _workflow/sb21-4/closeout/
?? _workflow/sb21-4/consultation-budget.md
?? _workflow/sb21-4/deliveries/capture-parallel-index-diff-r7.py
?? _workflow/sb21-4/deliveries/closeout-discovery-r3.json
?? _workflow/sb21-4/deliveries/final-closeout-r1.exit-code
?? _workflow/sb21-4/deliveries/final-closeout-r1.json
?? _workflow/sb21-4/deliveries/final-closeout-r2.json
?? _workflow/sb21-4/deliveries/final-closeout-r3.json
?? _workflow/sb21-4/deliveries/final-r7-discovery.json
?? _workflow/sb21-4/deliveries/natural-boundary-final-r2.json
?? _workflow/sb21-4/deliveries/natural-boundary-v2.json
?? _workflow/sb21-4/deliveries/natural-r7-continuation.json
?? _workflow/sb21-4/deliveries/natural-r7.json
?? _workflow/sb21-4/deliveries/parallel-index-json-malformed-pre-fix.bin
?? _workflow/sb21-4/deliveries/parallel-index-r7-diff.patch
?? _workflow/sb21-4/deliveries/parallel-index-r7-hashes.json
?? _workflow/sb21-4/deliveries/post-completion-registered-r1.json
?? _workflow/sb21-4/deliveries/post-registration-r2.json
?? _workflow/sb21-4/deliveries/post-registry-update-r1.json
?? _workflow/sb21-4/deliveries/pre-r7-registration/
?? _workflow/sb21-4/deliveries/pre-review-r2.json
?? _workflow/sb21-4/deliveries/r8-review-discovery.json
?? _workflow/sb21-4/deliveries/register-continuation-candidates.py
?? _workflow/sb21-4/deliveries/repair-parallel-index-r7.py
?? _workflow/sb21-4/evidence-closeout-r4/
?? _workflow/sb21-4/evidence-final-20260928-v1/
?? _workflow/sb21-4/evidence-final-20260928-v2/
?? _workflow/sb21-4/evidence-final-20260928-v3/
?? _workflow/sb21-4/evidence-final-20260928-v4/
?? _workflow/sb21-4/evidence-final-r2-retry1/
?? _workflow/sb21-4/evidence-final-r2-retry2/
?? _workflow/sb21-4/evidence-final-r2-retry3/
?? _workflow/sb21-4/evidence-final-r2-retry4/
?? _workflow/sb21-4/evidence-final-r2-retry5/
?? _workflow/sb21-4/evidence-postcommit-r1/
?? _workflow/sb21-4/evidence-postcommit-r2/
?? _workflow/sb21-4/evidence-pre-review-v2/
?? _workflow/sb21-4/evidence-pre-review-v3/
?? _workflow/sb21-4/evidence-pre-review/
?? _workflow/sb21-4/evidence-r5-05b/
?? _workflow/sb21-4/evidence-r5-05c/
?? _workflow/sb21-4/evidence-r5-05d/
?? _workflow/sb21-4/evidence-r8-20260928-v1/
?? _workflow/sb21-4/evidence-r8-budget-fit-20260928-v1/
?? _workflow/sb21-4/evidence-r8-corrected-20260928-v1/
?? _workflow/sb21-4/final/
?? _workflow/sb21-4/mutations-review-r1-retry1/
?? _workflow/sb21-4/mutations-review-r1-retry2/
?? _workflow/sb21-4/mutations-review-r1/
?? _workflow/sb21-4/mutations/
?? _workflow/sb21-4/pre-closeout-refresh-20260928-0307/
?? _workflow/sb21-4/red/
?? _workflow/sb21-4/refresh_closeout_docs.py
?? _workflow/sb21-4/review-packet-r8-final-20260928-v1/
?? _workflow/sb21-4/review-prep-final/
?? _workflow/sb21-4/review-prep-r2/
?? _workflow/sb21-4/review-r5-05d/
?? _workflow/sb21-4/review-snapshot-r2-retry3/
?? _workflow/sb21-4/review-snapshot-r2-retry4/
?? _workflow/sb21-4/review/committed-diff-afe84.patch
?? _workflow/sb21-4/review/gpt-r1-request.md
?? _workflow/sb21-4/review/gpt-r1-review.md
?? _workflow/sb21-4/review/gpt-r1-snapshot-v2/
?? _workflow/sb21-4/review/gpt-r1-snapshot-v3/
?? _workflow/sb21-4/review/gpt-r2-attempt1-failure.md
?? _workflow/sb21-4/review/gpt-r2-request.md
?? _workflow/sb21-4/review/gpt-r3-budget.md
?? _workflow/sb21-4/review/gpt-r3-objective.md
?? _workflow/sb21-4/review/gpt-r3-request.md
?? _workflow/sb21-4/review/gpt-r3-review.md
?? _workflow/sb21-4/review/gpt-r4-review.md
?? _workflow/sb21-4/review/gpt-r5-review.md
?? _workflow/sb21-4/review/gpt-r6-review.md
?? _workflow/sb21-4/review/gpt-r7-review.md
?? _workflow/sb21-4/review/ledger-closeout-before-owner-checkpoint.json
?? _workflow/sb21-4/review/ledger-owner-checkpoint-final.json
?? _workflow/sb21-4/review/ledger-owner-checkpoint-snapshot.json
?? _workflow/sb21-4/review/ledger-pre-r3-update.json
?? _workflow/sb21-4/review/ledger-r2-snapshot.json
?? _workflow/sb21-4/review/manifest-r3.json
?? _workflow/sb21-4/review/manifest-r3b.json
?? _workflow/sb21-4/review/manifest-r3c.json
?? _workflow/sb21-4/review/material-out-tracked.diff
?? _workflow/sb21-4/review/parallel-recovery-registration-2026-09-28.json
?? _workflow/sb21-4/review/parallel-recovery-registration-latest.json
?? _workflow/sb21-4/review/parallel-recovery-report-2026-09-28.md
?? _workflow/sb21-4/review/parallel-recovery-report-latest.md
?? _workflow/sb21-4/review/pre-edit-r3.json
?? _workflow/sb21-4/review/r3-baseline/
?? _workflow/sb21-4/review/r3-repair/
?? _workflow/sb21-4/review/r4-prep/
?? _workflow/sb21-4/review/r5-3-contract-extract.md
?? _workflow/sb21-4/review/r5-3-sb21-4-current.md
?? _workflow/sb21-4/review/r5-repair/
?? _workflow/sb21-4/review/r6-audit-evidence-v6/
?? _workflow/sb21-4/review/r6-repair/
?? _workflow/sb21-4/review/r6-review-20260928-v1/
?? _workflow/sb21-4/review/r6-review-snapshot-final/
?? _workflow/sb21-4/review/r6-review-snapshot-v2/
?? _workflow/sb21-4/review/r6-review-validation-final/
?? _workflow/sb21-4/review/r6-review-validation/
?? _workflow/sb21-4/review/r7-repair/
?? _workflow/sb21-4/review/r7-review-20260928-v1/
?? _workflow/sb21-4/review/r8-review-20260928-v1/.keep
?? _workflow/sb21-4/review/r8-review-20260928-v1/budget-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/findings-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-before-r8.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-r7-before-r8-sync.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-r7-sync-evidence.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard-run2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard/
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m2-tombstone-retry-idempotence-run2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/objective-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/parallel-deliveries-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/active-ledger-before-final-disposition.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-build.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/builds-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-final-closeout.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/edit-inventory-after-final-docs.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/edit-inventory-pre-final-docs.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v3/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/ledger-before-final-disposition.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-exact-240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-exact-240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-sb21-exact240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-sb21-exact240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-scoped-240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-scoped-240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/next-batch-prompt-final.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/test-project-build.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-postreview.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-postreview.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/validation-summary.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-comparisons-r8.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-diff-opening-to-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-diff-r6-to-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/validation-summary-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/workflow-preflight-correction.md
?? _workflow/sb21-4/review/redundant-mutant-exclusion.md
?? _workflow/sb21-4/review/snapshot-r3/
?? _workflow/sb21-4/review/snapshot-r3c/
?? _workflow/sb21-4/review/snapshot-r4f/
?? _workflow/sb21-4/review/snapshot-r4g/
?? _workflow/sb21-4/review/staged-supplement.patch
?? _workflow/sb21-4/review/status-at-r2-prep.txt
?? _workflow/sb21-4/review/supplemental-tracked-diffs.patch
?? _workflow/sb21-4/review/unstaged-supplement.patch
?? _workflow/sb21-4/settled-final-r2/
?? _workflow/sb21-4/settled-final-r3/
?? _workflow/sb21-4/settled-final-r4/
?? _workflow/sb21-4/write_manifest.py
?? _workflow/wave3-bo6-bo7/INTAKE-NOTE.md
?? _workflow/wave3-bo6bo7-receive/
?? build_final.log
?? build_head_output.txt
?? test_preservation.txt
?? testrun_preservation.log

## scoped unstaged diff
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
index 292bfd75a..8f88d903e 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
@@ -235,6 +235,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md383977C52DA4D3
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3A0EB9288B61ADA73AA983061EAEE1415BEE6B3D13CE2F91E88A3144FD27CDC01| **占用者级别/优先级与 `HighestClass` 的生产来源** | ❌ 未接线：生产映射恒 null/未知 ⇒ 生产上普通任务对占用者一律保守停驻；需运行台账↔执行身份关联（后续批次） |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3AF9DFCAB38EAFDAE46FE371108FF498D3CF1FD076D9572C15F39FAF3E967E641| 9 | **外部启动（E3/E4/E5/E6）路径接管失败映射端点；子进程级重启；E4 取消入口映射** | R5.8 真实入口层（§23.1；施工方：入口夹具负责人） | 该路径上的端到端夹具或实机证据 | 外部启用门＋R5.8 门 | 已移交·**未验收** |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3C169B077F2D8344F9F877CE1F207F7969D2B0C1BD06CECE13C37EB61DC82F081| 3 | **首节点绑定（§12.3 M1）**：父子关系与首节点绑定持久化 | R5.8 实现闭合序列（施工方：宿主实现＋夹具负责人） | 持久化父子/首节点绑定＋「E1 关闭后首节点另行取许可」夹具 | 节点改道门 | **部分已交付·未验收**（[批次四十四] §24.54：父子绑定由门面自行反查写入＋首/后继…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3D08ED876CA155691F693248D0B93254D9E4549AA77AAA3A6899C0306F1146081- **BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT** 仍未并入、仍未闭合；R5.6、R6.1、R6 diff guard 未提前集成。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3D57EDF6804F0853C382DEE2947263C10F966E08EDD2BD730411F39AC6290D2D1| 1 | **P50 负载敏感诊断套件**（33 节点端到端暂停，须覆盖整个用例类） | 独立诊断批（施工方） | 可单独重复运行的负载复现入口＋根因定位＋该类夹具稳定 | 节点改道门保留；Skip 与严格断言不放宽 | **已交付（组件/宿主层）**（[批次四十九] §24.61：负载复现入口（`BGI_R5_P5…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3DDDE11B9941D8B8B9F2DC64BD8F981E4F8FB26D298E420A52CE8F78DD4280BA1| 等待组件 | `LocalWaitQueuePolicy`／`LocalWaitQueueStore`／`LocalWaitModels` 仍无生产调用方（见 §24.106） | ❌ 仍**未接线** |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md3DDFA6587CB2D67F687F3F47B950D21DDA76BDD7ED9E0FA055539074677EF6491**键形状变更的兼容性（如实）**：带**非空作用域**（或带代际）的键**有意**不再与批次 15 逐字符一致——旧形状的别名不可修复，只能改字面形状换取消歧义。这不改变**本实例内**的在飞去重语义（`StateScope` 不可变 ⇒ 每身份键仍单射，"同一等待项至多产一条"不变）；受影响的是**跨实例／跨进程按…
@@ -332,6 +333,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8796CBCB8D6621
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md87E6B96129FEA9419AD3C226322C884888F572B83C496E2AB7BB2DB31822D3F31- 会诊：**GPT-6-Astra／medium 三轮各 1 次成功**（首轮 3 必改＋3 重要；验证轮判 ①闭合、③未闭合并新增必改 c；
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md894EAEF276AD4510FA2CAA45B7D2D0A101051DDE448D98A0C051B62079D19BFE1**局部施工与复审**：代码以入口显式 `ProjectRegistryOutcome` 为边界，注册表锁内复制结果；明确拒绝／可证失败才投影 `failed`，已确认的前置成功才投影 `completed`。未知、取消、逃逸异常及仅有破坏性动作意图时保留 `ResultUnknown/result_unknown`，…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8AD6D948ADF86C971E26BA1EF60E7C52C5837FED5BA6B6C4EA38AE5AEB60D3D01- **R3**（收口轮，**无必改、无未闭合重要项**；3 建议全采纳）：页首「仅含 owner 既有未提交文件」与
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8BB3C28725E15D48B2F7205F587007050DE5591AF604693BA44A00E96F15AC771- 四项原级义务在来源侧由 owner 批准的两次追加独立会诊按**原等级**裁定 closed：BO-6 R19 **MUST**、BO-6 R21 F4 **IMPORTANT**、BO-7 R21 F1 **MUST**、BO-7 R21 F2 **MUST**；两份报告的收尾结论均为「本范围仍有未闭合的 MUS…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8CDDAB59B6593D428556E9311CFB817E513D9AE79607642326FCA912BCDBA1891⇒ 本批**不声称会诊已闭环**，按纪律以「owner 一次性批量裁决项」登记。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8E2883FCF8EACA5C9AA4023C7419F6F7F0CFD1123DA9BB30E87B848A0F5D833613. **终态（完成层报 `Succeeded`／`Cancelled`／`ExecutionFailed`）且接管一致性验证通过**：受理→接管台账落盘→**同一次权威发布写 `ExecutionResult` ＋ `PendingTerminal`**→台账转 `Terminal`→关闭 Submission（或按…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8EB3FD01AA65CAB8F38B8D7FA4F33F1A4C17F7E09BCA2F93DB31D7F4E8BF030F1**与旧轮次的接入边界**：运行中低级请求必须先进入等待登记，不能先走 `ProcessRoundAsync` 再把输家回队列；现有整轮会把落选者写为终局 `NotSelected`，占用分支只保留获选者。等待激活后仍由唯一绑定的 Operation 进入原轮，但落选时应回到其等待绑定而非按普通 `NotSelecte…
@@ -449,6 +451,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdF9394E678D87C6
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFA57DD5BD68AC7A2D10EB26F4F850385A4905DB2B11C62573B4353E841E499D11| D2 前置模型 | `SelectNext` 投影把 `PrerequisiteReady` **固定 true**（§24.106 C4） | ⚠ **未闭合，属 D2 范围，本批不动** |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFB944D03EB853B3A359D85ABCF4A88B46FAB522D0F048B1CCD465FEDDE9FD11C1| 生产消费端 | ❌ 未接线：抢占执行、退出确认、恢复专用准入仍未消费本批事实（生产门保持关闭） |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFBE89153DB0A570A1832D57B78495F3EC946CA01CC65CD8D3F4F37229ABA16121| **④ 生产时序与新鲜度** | ⚠ 生产解析**已接线**，但新增同步读取（运行台账 + 租约）的耗时、锁交错、以及读取后快照新鲜度/纪元一致性**尚未验证** |
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFC153C1C4E1F5B6CFFEBBC5CCB331E2304C86AC1D4838E206C499EA4E2163EA91本批是**接收批**：把已独立交付、并按原等级闭环的 BO-6/BO-7 修复接入茶包主线。不施工 BO-8/BO-9，不提前集成 R5.6、R6.1 或 R6 diff guard，也不重开已闭合的 BO-13、SB21-3 BO-4、SB21-2 BO-10/12。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFCE6B4691F864A07829278D1C3670C068E38976805218538157254EC16F957801| 2 | **P19① 原因码逐次取证**（批次三十二原诊断已按 owner 裁决更新） | R5.8 真实入口与拒绝审计验收（施工方：门面/宿主夹具负责人） | 组件层已验证 `submission_conflict` 释放被拒请求槽位、保留拒绝原因与原未决责任；尚需真实入口端到端核对拒绝映射、审计可查与解除冲突后…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFE3C9DA096826D54AA3505702CCBA877AB65B6B6F2C1622E5E941C2835F8A88B1但 Runner 的 `attempt` 仍固定为 1、未驱动 `RetryAsync` ⇒ **重试链未闭环**（窗口只能由显式恢复/后续批次消费）。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdFE6A5508ED80DF42F43A81D61D9DE8D844DFF7A49E80AB474566B07C4AE650131- **Owner 收口裁决（2026-09-27）**：owner 本轮指令要求收尾 SB21-1；据 R9 两项原级必改的补证已完成本地机械核验，关闭本子批且不追加会诊。反向突变审计中的 11 组 mutant/restored TRX、命名 Fact 结果、TRX SHA 与当前恢复态源码 SHA 均逐项复核；R…

## scoped staged diff
diff --git a/MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs b/MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs
index 8b0bb5d93..d07ea7926 100644
--- a/MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs
+++ b/MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs
@@ -561,6 +561,17 @@ public sealed class WorkflowRunner
                 run.LocalWaitDecision = null;
             }
 
+            // BO-6/7: explicit resume is a rescue boundary even when the saved cursor still exists.
+            // A prior revision stamp or a current cursor must not hide an earlier live parked
+            // obligation; recompute the plan-order successor from completion + all park history.
+            // A persisted tail flag with live parks stays fail-closed through final aggregation.
+            if (!run.TailReached && run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord))
+            {
+                var resumeSuccessor = RecomputeSuccessor(run, plan);
+                ApplyRelocation(run, resumeSuccessor);
+                Log(run, "显式恢复复核当前计划中的停驻义务，按锚/停驻全序重算恢复点。");
+            }
+
             run.WorkflowRevision = snapshot.Revision; // 恢复即对账到当前修订（节点边界语义）
             run.State = WorkflowRunState.Running;
             run.Note = AppendNote(run.Note, "显式恢复运行（游标身份重定位，不重放已完成节点）。");
@@ -578,6 +589,10 @@ public sealed class WorkflowRunner
     {
         var ct = control.RunCts.Token;
         var flowFailure = false;
+        // BO-6: a resumed run with a persisted local-wait obligation filters already completed
+        // stable occurrences while advancing from a rescue point. Keep this recovery behavior
+        // scoped to parked runs; ordinary revision progression remains outside BO-8.
+        var filterCompletedDuringParkRecovery = run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord);
         try
         {
             // 顶层触发器：入口等待（不占槽位；已消费则跳过——恢复不重等，B3）
@@ -634,6 +649,19 @@ public sealed class WorkflowRunner
                     if (control.PauseRequested) return Pause(run);
                 }
 
+                // BO-6: a rescue point may precede completed anchors. Re-drive the parked
+                // obligation, but never resubmit a stable occurrence already completed in this
+                // run. One step per loop preserves definition-boundary and round-start handling.
+                if (filterCompletedDuringParkRecovery && HasCompletedOutcome(run, occurrence))
+                {
+                    Log(run, $"停驻恢复推进过滤已完成出现 {occurrence.NodeId}#{occurrence.Occurrence}"
+                             + $"（轮次 {occurrence.LoopIteration}），继续按计划全序前进。");
+                    occurrence = plan.Next(occurrence);
+                    ApplyRelocation(run, occurrence);
+                    _runs.Update(run);
+                    continue;
+                }
+
                 var node = plan.NodeAt(occurrence);
                 var gate = plan.EvaluateNode(occurrence, _opt.Clock(), _boundary.SingleNativeSupported);
                 if (gate.Action == NodeGateAction.Skip)
@@ -714,14 +742,19 @@ public sealed class WorkflowRunner
             }
 
             // 流程边界：聚合判定只信 NodeOutcomes（B3：含恢复后的历史结果重建，失败不被成功覆盖）
+            var unresolvedParkedOutcome = run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord
+                && plan.TryLocate(o.NodeId, o.Occurrence, o.LoopIteration, out var parked)
+                && !HasCompletedOutcome(run, parked));
             var hadBadOutcome = run.NodeOutcomes.Any(o =>
                 o.Result is "failed" or "rejected" or "cancelled" or "cancelUnconfirmed" or "unknown"); // R4.6 B3
-            if (hadBadOutcome || flowFailure)
+            if (hadBadOutcome || unresolvedParkedOutcome || flowFailure)
             {
                 run.State = WorkflowRunState.Failed;
                 run.Note = AppendNote(run.Note, flowFailure
                     ? "循环/触发时刻计算失败，聚合结果 Failed。"
-                    : "存在失败/拒绝节点，聚合结果 Failed（失败不被后续成功覆盖）。");
+                    : unresolvedParkedOutcome
+                        ? "存在仍可定位且未完成的本地停驻义务，未按链尾成功处理；聚合结果 Failed。"
+                        : "存在失败/拒绝节点，聚合结果 Failed（失败不被后续成功覆盖）。");
                 _runs.Update(run);
                 return run;
             }
@@ -1332,12 +1365,8 @@ public sealed class WorkflowRunner
     /// unknown/cancelUnconfirmed 同为非完成词，但其锚污染被 Unknown 禁恢复态挡住（恢复前必先对账并追加
     /// 真实终态词），维持既有口径不动（本批不扩改既有恢复语义）。
     /// **两条不变量（本批会诊逐轮收敛所得）**：
-    /// ①**不重跑已完成出现**——**仅限返回点本身**：返回的出现若已有完成结果，沿链继续向后跳过
-    /// （R12 重要-1）。**已知缺口（R29 重要，继承缺陷——批次 20 前旧实现 plan.Next(lastOcc) 行为相同）**：
-    /// 返回点**之后**的线性推进段（DriveAsync/Relocate）无完成过滤，修订重排把已完成节点挪到恢复点
-    /// 之后时该节点会被二次提交（反例：[n3,n2,Y] 执行 n3✓n2✓ 后修订为 [n2,X,n3,Y] ⇒ 返回 X ⇒ X 完成
-    /// 后推进撞 n3）。修复归驱动推进层（DriveAsync/Relocate 完成过滤）＝台账 BO-8（Wave3 C11），
-    /// 本批不改生产推进机制；
+    /// ①**不重跑已完成出现**——返回点本身先过滤完成结果（R12 重要-1）；BO-6 为有停驻历史的恢复推进
+    /// 增加同身份完成过滤。普通修订推进的完整过滤仍归 BO-8，本批不扩展该范围；
     /// ②**不吞停驻出现**——引擎自己立下的重驱义务（<see cref="LocalWaitResultWord"/>）：任何返回链尾
     ///   （null/TailReached）的路径都必须先复核「计划中是否仍有可定位的停驻出现」，有则按它继续
     ///   （R14 F2）；停驻出现之前若有**从未执行**的出现，则按更早者继续（R12 建议-1／R14 F1，
@@ -1373,12 +1402,12 @@ public sealed class WorkflowRunner
             // 依次穿越中途**已完成**出现（含锚及其后节点）并重复提交（不变量①）。停驻点之前的
             // 新插节点不在此路径承载（其重驱义务由 C11 重驱合同覆盖，属接线批语义面）。
             // [Wave1 R19 必改-1 补强] 锚可定位且 candidate 非 null 时，仍须检查有效停驻点的
-            // **位置安全性**：有效停驻点（未过期、可定位）若不在锚之后的安全路径上（同轮次早于锚，
-            // 或轮次早于锚）⇒ 返回 candidate 会吞掉它（R15 形态）、返回它又会隔着已完成出现
-            // （R19 形态）⇒ 两不变量冲突 ⇒ 保守 null ＋ 响亮日志（BO-6）。
+            // **位置安全性**：锚前的有效停驻义务优先作为恢复点；BO-6 驱动层会过滤其后已完成出现，
+            // 由此同时保留停驻重驱与不重提已完成节点。
             // [Wave1 R25 重要-1] **全部**有效停驻点逐一做位置安全性检查（不得因最后一条安全而
-            // 跳过更早的）：任一有效停驻不在锚后安全路径 ⇒ 保守 null ＋ 响亮日志（BO-6）。
+            // 跳过更早的）：任一有效停驻不在锚后安全路径 ⇒ 记录冲突并优先返回最早有效停驻点。
             // R25 反例（删除 P1→执行越过→同身份加回锚前）中更早有效停驻逃出检测的形态由此封死。
+            var hasUnsafeParkedBeforeAnchor = false;
             for (var pi = parkedOutcomes.Count - 1; pi >= 0; pi--)
             {
                 var po = parkedOutcomes[pi];
@@ -1389,10 +1418,10 @@ public sealed class WorkflowRunner
                         && unsafeParked.SequenceIndex > anchorOcc.SequenceIndex);
                 if (!onSafePathAfterAnchor)
                 {
+                    hasUnsafeParkedBeforeAnchor = true;
                     Log(run, $"有效停驻点 {unsafeParked.NodeId}#{unsafeParked.Occurrence} 被修订重排到已完成锚 "
                              + $"{anchorOcc.NodeId}#{anchorOcc.Occurrence} 之前：返回任一侧都会违反不变量①或②"
-                             + "（保守：本轮重算按链尾处理），停驻重驱的推进语义归台账 BO-6（Wave3 C11）。");
-                    return null;
+                             + "；优先保留停驻义务，恢复推进时过滤已完成出现（BO-6）。");
                 }
             }
             WorkflowNodeOccurrence? lowerBound = candidate;
@@ -1417,11 +1446,13 @@ public sealed class WorkflowRunner
                 }
             }
             var parkedRescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes,
-                lowerBound: lowerBound, out var rescueConflict);
-            // [Wave1 R20 重要-1] rescueConflict=true（真冲突）⇒ **不得**回落锚候选——保守裁决对
-            // 「锚<停驻<已完成」形态同样成立（candidate 可能是被重排保护的已完成节点之后的未执行项，
-            // 停驻点仍会被吞）；按链尾处理（日志已由 ParkedRescue 留痕）。
-            if (rescueConflict) return null;
+                lowerBound: lowerBound);
+            if (hasUnsafeParkedBeforeAnchor && parkedRescue is null)
+            {
+                // A valid unsafe marker was found above, so a missing rescue is an inconsistent
+                // state. The final aggregation guard turns any still-live marker into Failed.
+                Log(run, "已发现可定位的锚前停驻义务，但未能构造恢复点；禁止将其当作普通链尾成功。");
+            }
             // [Wave1 R24 重要-2] rescue 与 candidate 都非 null 时按**计划全序取较早者**：
             // 线性推进（从返回点沿 Next 走到链尾再回绕）保证较晚者自然到达（未完成 ⇒ 会被执行），
             // 两个义务都不丢。固定 rescue 覆盖 candidate 的旧形态（R24 反例：修订在锚后插入 C@0、
@@ -1439,14 +1470,12 @@ public sealed class WorkflowRunner
         if (completionOutcomes.Count == 0)
         {
             var noAnchorRescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes,
-                lowerBound: null, out var noAnchorConflict);
-            // [Wave1 R20 重要-1] 无完成锚分支：candidate 语义＝链首；真冲突时同样不得按链首放行
-            //（冲突形态已含「停驻点之后有已完成」⇒ 链首起线性推进同样会撞已完成），保守 null。
-            return noAnchorConflict ? null : (noAnchorRescue ?? plan.FirstOccurrence());
+                lowerBound: null);
+            return noAnchorRescue ?? plan.FirstOccurrence();
         }
 
         // 全部完成锚在新修订中不可定位（R7 重要-1）
-        var rescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes, lowerBound: null, out _);
+        var rescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes, lowerBound: null);
         if (rescue is not null) return rescue;
         Log(run, "完成身份与停驻身份在新修订中均已消失，按链尾处理（已完成节点不重跑）。");
         return null;
@@ -1459,20 +1488,16 @@ public sealed class WorkflowRunner
     /// R12 建议-1／R14 F1：探针从**该停驻所在轮次的链首**起步——由基准沿 Next 反向不可行，
     /// 改为从链首逐轮推进到目标轮次，避免 R14 F1 的「锁死第 0 轮」缺陷）。
     ///
-    /// **返回语义**（[Wave1 R20 重要-1] 两种 null 必须可区分）：
-    /// 返回出现＝按停驻义务继续；null＋<paramref name="conflict"/>=false＝无有效停驻义务
-    /// （调用方可回落锚候选）；null＋<paramref name="conflict"/>=true＝检测到不变量①/②真冲突
-    /// （保守裁决：调用方**不得**回落锚候选，按链尾处理＋日志已留痕，归台账 BO-6）。
+    /// **返回语义**：返回最早仍有效的停驻义务或其前插未执行出现；没有可定位的未完成停驻时返回 null。
+    /// 若恢复点越过已完成出现，由 BO-6 驱动层在推进时过滤这些完成身份。
     /// </summary>
     private WorkflowNodeOccurrence? ParkedRescue(WorkflowRunRecord run, WorkflowPlan plan,
         List<WorkflowNodeOutcome> parkedOutcomes, List<WorkflowNodeOutcome> completionOutcomes,
-        WorkflowNodeOccurrence? lowerBound, out bool conflict)
+        WorkflowNodeOccurrence? lowerBound)
     {
-        conflict = false;
         // [Wave1 R25 重要-1] 取**计划全序最早**的有效停驻点（不再「最后追加者首中即返回」）：
         // 更早的有效停驻点（如「删除→执行越过→同身份加回锚前」舞步复活的标记）同样承载重驱义务，
-        // 首中即返回会让它逃出救援与冲突检测（静默吞）。最早者不可承载（之后同轮次有已完成）⇒
-        // 保守 null（更晚者重驱后推进亦会撞已完成，且最早被吞问题无解）。
+        // 首中即返回会让它逃出救援与冲突检测（静默吞）。最早者重驱后由 BO-6 恢复推进过滤跳过已完成出现。
         var hasEarliest = false;
         var earliestOcc = default(WorkflowNodeOccurrence);
         foreach (var po in parkedOutcomes)
@@ -1535,26 +1560,8 @@ public sealed class WorkflowRunner
                 Log(run, $"锚可定位但向后走至链尾；存在停驻标记 {parked.NodeId}#{parked.Occurrence}，"
                          + "按停驻出现继续（不按链尾放行，零发送节点不得被静默吞）。");
             }
-            // [Wave1 R19 必改-1] **停驻点之后（同轮次）存在已完成出现 ⇒ 不得返回停驻点**：
-            // 停驻点重驱完成后，驱动循环线性推进（DriveAsync/Relocate 无完成跳过）会穿越那些
-            // **已完成**出现并重复提交（外部副作用二次发生）——不变量①（不重跑）与不变量②
-            // （不吞停驻）在「停驻点被修订重排到已完成锚之前」的形态下**真冲突**，本批取
-            // 保守方向（①优先）：返回 null（链尾）＋ 响亮日志，语义归台账条目 BO-6
-            // （Wave3 C11：带完成过滤的推进 / 显式失败态，接线批实现），不得静默。
-            for (var after = plan.Next(parkedOcc);
-                 after is not null && after.LoopIteration == parkedOcc.LoopIteration;
-                 after = plan.Next(after))
-            {
-                if (HasCompletedOutcome(run, after))
-                {
-                    Log(run, $"停驻点 {parkedOcc.NodeId}#{parkedOcc.Occurrence} 在新修订中被重排到"
-                             + $"已完成出现 {after.NodeId}#{after.Occurrence} 之前：线性推进将重复提交"
-                             + "已完成节点（不变量①优先），本轮重算按链尾处理；停驻重驱的推进语义归 "
-                             + "台账 BO-6（Wave3 C11 裁决）。");
-                    conflict = true;
-                    return null;
-                }
-            }
+            // RecomputeSuccessor may select a marker before its completed anchors; DriveAsync
+            // re-drives this obligation and skips those stable completed identities as it advances.
             return parkedOcc;
         }
         return null;
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
index 84b9e01e7..292bfd75a 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
@@ -225,6 +225,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md28F2F925204757
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md2AD85ECEEF3F3729A4E5D87F1B1E65C90F4953204D68353C4CCA17989A018BBC1| D-E5 | **(b) D5 维持休眠，C13 降备案** | D5 已交付实现/测试/登记全部保留（≠废弃）；D5 接线留待等待合同变绿后的独立批，届时 C13（IW-09 投影合同）为其前置；(c) 拆除未采纳 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md2C6293C112A370C4C93895499A84B7B9D6A94E10ACECCEEB9EF34013CF4D80B41| §24.41-C | 11 | S5 通用直发粗化 | **已交付（组件/宿主层）**：五层登记（字面量 6 操作类别／9 拼写＋非字面量 10 文件＋**间接形态 4 类零容忍**＋**`OpCode` 字面量直方图**（含只读，真·单遍扫描＋**完整 C# 标识符左边界**（`\p{L}\p{Nl}\p{Nd}…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md2C70FA06428A9AA3CDE1F7113138D966E6C44C100DE398F0694D2BBB8E7B71591| §24.41-C | 6 | 集合② 持续观察重绑 | **已交付（组件/宿主层）** | 完成证据 | `ArbitrationAdmissionServiceTests.RecoverObservations_ReboundThenAuthoritativeTerminal_SettlesLater` |
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md337A0AEC8BC6CE2AA8A9C0BE27F4D63B7B64009C157C9DF0A15F728C581C9FA81实现选择 BO-6 允许的**恢复推进时完成过滤**。当恢复记录含持久 `waitLocally` 历史，显式 `ResumeAsync` 在覆盖当前修订号前重算恢复 successor；即使保存游标在新 plan 仍可定位，也必须审查所有未完成停驻义务。`RecomputeSuccessor` 逐一检查仍有效停驻义务…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md33942E4F1B96BA029706ECB6957B11FA6756995800D7A5CBE0544587FABCA6781- 定向 Batch21WiringRedTests：4/4 通过，0 失败；相邻 LocalWait/Batch21/OccupantLevel/StartupHandoff/Resume：278/278 通过，0 失败。助手测试项目全量：1479 通过、2 个 opt-in P50 诊断未执行、0 失败，共 148…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md33ED99473F735819E9E898FC78F440E4F736C810D6EF6C8B0408F7A4E55CBB7F1| 与接管票据／同纪元当前占用的**绑定** | ❌ 未闭合：凭证与空闲是两次查询，未与本次接管票据及同一纪元的占用交叉校验；不能声称"接管绑定完整闭合" |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md33EF69541BCE443C279604756150CE09AD11079E7D7224D0B15171233F4D081A1| （未采纳）**选项 2（登记例外）** | 承认这是**调度器组件内部的未接线 API**（生产**零**调用方），补记一条「内部未接线 API 例外」，并把「旧调用语义兼容」明确排除在本批声明之外 | 保留现状；在 owner 决策单补登记例外条目，后续接线前复核 |
@@ -376,6 +377,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBB2ED328E7AD5B
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBBC0B5075ED4420D3158259700FDA155BD674470110432A41B8A8D185D51D0621| §23.8 | 6 | 外部入口未纳入宿主生命周期、发送回调恒 `None` | **部分**：owner 已选择软件关闭取消并核查；宿主令牌已接入部分发送链，E3/E4/E5 全路径交错证据仍未闭合 | 部分证据 | §24.68 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBCCF2AB9A16C977CF6947983827AA2F6BB335EFB609C684E4956590097289AE51| #5 多实例／跨进程 | 「书面拒绝 + 登记」（与 #2 同源） | 随 #2 修复而改变；**跨进程单写者**仍属未接线事项，如实登记 | — |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBE80339C4EF6903C4032CA7384547A8FC5E02E1014A018F23430E81871735EDB1| 1 | **重要（必改）** | `_handled` 按稳定身份**永久**去重 ⇒ 占用期间先由 `NewCandidateArrived` 产过一次后，随后的 `OccupancyEnded` 不再产；同一稳定身份代表不同**代际**等待项时，合法的新代际被一并屏蔽 | **部分采纳**：①"同身份重复到达不…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBFD716415D969151BCD27C12F635C07704E788FA93B9CD59F244FFA181076D891> 时点说明：本清单记录 batch20 冻结归属；BO-6/BO-7 的后续独立子批结果见 §24.125。BO-8/BO-9 仍保持原级未闭合。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC015CC0C5424152E9212942379AE4E53A663605D3CA3EFDF71797C1E5267326D1**B. 状态**：§24.41-C#17 记「**已交付（组件层）**」；其余残项不受影响。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC042A6923E232C1129F9FAA84023AF2D5D09773391B3D9B25100C17F649CC7CC1## 24. B3 外部启动生命周期补全设计（[2026-09-21 **已冻结**：第 23 轮会诊判「无必改项」，§17.4 收口；生产接线仍关闭]）
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC07C2D4C99D576D53D3A623B301CAE05A74AA8621D39D08486EAB20871D5BE951| 不可逆动作抛错且能证明零效果；或已部分执行 | 按各步骤独立证据判断，抛错本身不证明零效果 | 前者可 `Failed`；后者 `Unknown/Committing`，保留已完成步骤 | 部分效果不得整体重放；需动作专用对账 |
@@ -413,6 +415,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdD821DD962AE251
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdD830E9DFAB07053AACA25785EE5812B68D4C1F87CEBDED33A26EFB36CE82A7791| **声明面变更（须纳入本批提交评审）** | 本批**触及声明面**（新增「生产接线 ❌ 未接线」「等待组件接线状态 ❌ 仍未接线」「D2 前置模型 ⚠ 未闭合，属 D2 范围」等承载门禁词的声明行，以及交接稿批次 15 状态行）：设 `CLAIM_SURFACE_REGENERATE=1` 再生 `ClaimSu…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdD890DEAC87E4FBBF0F042C6A745C524C4C91112934FF5FEFDF7A0358AC049B151| §23.8 | 7 | 缺生产组合根闭环夹具 | 已交付（**测试接线态**，生产注入仍关闭）：12 枚组合根夹具 | 完成证据 | §24.42 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdD93DBC5CD967D951A7FD10BE78437D0CAF925DE0F5BBAC82DC1024A71608272D1线性化点为 Store 的 Load 快照；Load 返回后再并发 Remove 不撤销已计算出的 Valid，连续重复调用也可能都 Valid。本批不声称 at-most-once 消费或请求领取去重。助手仓库仍无生产 `LocalWaitReevaluationConsumer` 调用点，不声称完整准入下游已执行，…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdD9C80A6A25E6E6A8607F627C258D8F7B8D999C756F108C0CE499333CD407D5571**逐项原级裁决**：BO-6 R19 **MUST** closed；BO-6 R21 F4 **IMPORTANT** closed；BO-7 R21 F1 **MUST** closed；BO-7 R21 F2 **MUST** closed。两份报告的收尾结论均为"本范围仍有未闭合的 MUST/IMPORTAN…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdDA117543326E94DF516E2A08580129F28AF1363491F51EB73F0C10114D9D33A71| 预留≠交付 | ⚠ 契约边界（会诊 #2）：`TryAdd` 是**状态预留**，**不等于**"请求已交付／消费方已接收"；同一调用内无可观察的失败界限，但登记**先于**调用方看到产物。**不得**把该实现读成"保证请求已被处理" |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdDD4E339BDFFA9D9E56D734AB71449F8747F560D04F9715A9B94CA3E9C4116E371（不再透传入参字段），避免「已执行外部副作用、接管按错误持久化类型分派」。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdDD7F4B3B696A52E8497D50B556D536491F99D4C3BC01443CA243811BDA3577A91`OpCode = <表达式>` 4 文件）；**保留门禁**＝外部启用门；**残余**（IL 级间接分析、Roslyn 化降误报）为**可选加固**。
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs
index 0040df82b..5f1374cf0 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs
@@ -693,17 +693,17 @@ public sealed class LocalWaitIdentityTranslationTests : IDisposable
     }
 
     /// <summary>
-    /// 【R19 必改-1 裁决改写（原 M38 链尾夹具）】同上保守语义：新链 [nPre, n2, n0, n1] 中
-    /// 停驻点 n2 之后（同轮次）有已完成 n0/n1 ⇒ 返回 n2 会让线性推进穿越它们 ⇒ 不变量①优先 ⇒
-    /// 返回 null（nPre/n2 的重驱配对归 BO-6/Wave3）。
+    /// 【BO-6 完成过滤推进】锚位于链尾时既有 tail 下界返回 n2；真实 Runner 随后重驱 n2，
+    /// 并由恢复推进层过滤已完成 n0/n1。
     /// </summary>
     [Fact]
-    public void RecomputeSuccessor_AnchorAtTail_ParkedFollowedByCompleted_ConservativeNoRerun()
+    public void RecomputeSuccessor_AnchorAtTail_ParkedFollowedByCompleted_ReturnsParkedMarker()
     {
         var runner = MakeRunner(Path.Combine(_dir, "q-anchor14"), referenceProvider: null);
         var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
         var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("nPre", "n2", "n0", "n1"));
-        Assert.Null(relocated);
+        Assert.NotNull(relocated);
+        Assert.Equal("n2", relocated!.NodeId); // tail 下界保护有效停驻义务；前插由候选路径另行承载
     }
 
     /// <summary>
@@ -733,30 +733,28 @@ public sealed class LocalWaitIdentityTranslationTests : IDisposable
     }
 
     /// <summary>
-    /// 【R19 必改-1 裁决改写（原 M34 夹具）】同上保守语义：停驻点 n2 重排到已完成锚之前
-    /// （即便锚之后还有未执行 n3）⇒ 返回停驻点会让线性推进穿越已完成 n0/n1 ⇒ 不变量①优先 ⇒
-    /// 返回 null ＋ 响亮日志（BO-6）。n3 与 n2 的重驱配对归 BO-6/Wave3 承载。
+    /// 【BO-6 完成过滤推进】停驻点 n2 重排到已完成锚之前，恢复从 n2 开始，
+    /// 然后跳过已完成 n0/n1 并继续执行 n3。
     /// </summary>
     [Fact]
-    public void RecomputeSuccessor_AnchorLocatableParkedBeforeItAndWorkAfter_ConservativeNoRerun()
+    public void RecomputeSuccessor_AnchorLocatableParkedBeforeItAndWorkAfter_ReturnsParkedMarker()
     {
         var runner = MakeRunner(Path.Combine(_dir, "q-anchor11"), referenceProvider: null);
         var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
         var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n2", "n0", "n1", "n3"));
-        Assert.Null(relocated);
+        Assert.NotNull(relocated);
+        Assert.Equal("n2", relocated!.NodeId);
     }
 
     /// <summary>
-    /// 【R19 裁决改写（原 M40 夹具）】多停驻标记＋过期＋前插＋锚链尾的组合场景：
-    /// P1（pa）有效但其后同轮次有已完成 pb（P2 过期）⇒ 按 R19 必改-1 保守裁决，pa/pb 重驱推进
-    /// 会穿越已完成出现 ⇒ **整个形态落台账 BO-6**（返回 null，不重复执行）。
+    /// 【BO-6 完成过滤推进】多停驻标记＋过期＋前插＋锚链尾的组合场景：
+    /// P1（pa）有效、P2（pb）已完成而过期；返回 pa，由真实 Runner 重驱 pa 并过滤 pb。
     /// **tailBound 过期过滤（R19 重要-2 修复）如实登记**：已实现为与 ParkedRescue 主循环对称的
     /// 结构性防御（下界选取共用 HasCompletedOutcome 过滤）；其**独立**可观察行为需要「loop 计划＋
-    /// 有效停驻在锚后安全路径＋更晚过期标记」的组合——该组合在保守裁决下同样返回 null（BO-6），
-    /// 过滤的判别力被保守语义遮蔽 ⇒ 无独立突变（R5：非唯一证据不强制；已如实登记）。
+    /// 有效停驻在锚后安全路径＋更晚过期标记」的组合仍由既有锚下界过滤保障。
     /// </summary>
     [Fact]
-    public void RecomputeSuccessor_MultiParkingWithStale_ConservativeNoRerun()
+    public void RecomputeSuccessor_MultiParkingWithStale_ReturnsEarliestActiveMarker()
     {
         var runner = MakeRunner(Path.Combine(_dir, "q-anchor15"), referenceProvider: null);
         var run = MakeRun();
@@ -764,7 +762,8 @@ public sealed class LocalWaitIdentityTranslationTests : IDisposable
         run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "pb", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord, Reason = "P2 停驻" });
         run.NodeOutcomes.Add(new WorkflowNodeOutcome { NodeId = "pb", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded", Reason = "P2 随后完成＝过期" });
         var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("pre", "pa", "pb"));
-        Assert.Null(relocated);
+        Assert.NotNull(relocated);
+        Assert.Equal("pa", relocated!.NodeId);
     }
 
     /// <summary>
@@ -899,8 +898,8 @@ public sealed class LocalWaitIdentityTranslationTests : IDisposable
 
     /// <summary>
     /// 【突变验证 ✔（M47：安全性检查恢复 break 首中即返回 ⇒ 红，实测见突变记录）】【Wave1 R25 重要-1】
-    /// 多有效停驻：P1（同身份加回锚前）＋P2（锚后）——最后一条 P2 在安全路径上不得短路跳过 P1 的
-    /// 安全性检查：P1 在锚前 ⇒ 保守 null（响亮日志），P1 的重驱义务不得被静默吞。
+    /// 多有效停驻：P1（同身份加回锚前）＋P2（锚后）——最后一条 P2 在安全路径上不得短路跳过 P1；
+    /// 最早有效停驻 P1 必须先作为救援点返回，随后 Runner 过滤已完成锚。
     /// </summary>
     [Fact]
     public void RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed()
@@ -919,8 +918,9 @@ public sealed class LocalWaitIdentityTranslationTests : IDisposable
                 Ref = new WorkflowResourceRef { Config = "c-" + id, ConfigKey = "c-" + id + "#k", Revision = "rev-1" },
             }).ToList(),
         }));
-        // P1 被重排到已完成锚 B 之前 ⇒ 两不变量冲突 ⇒ 保守 null（不得因 P2 安全而静默吞 P1）。
-        Assert.Null(relocated);
+        // P1 被重排到已完成锚 B 之前 ⇒ P1 义务优先，不得因 P2 安全而静默吞掉它。
+        Assert.NotNull(relocated);
+        Assert.Equal("P1", relocated!.NodeId);
     }
 
     /// <summary>
@@ -978,10 +978,8 @@ public sealed class LocalWaitIdentityTranslationTests : IDisposable
     }
 
     /// <summary>
-    /// 【突变验证 ✔（M41：锚路径把「真冲突」当「无义务」回落锚候选 ⇒ 红，实测见突变记录）】
-    /// 【Wave1 R20 重要-1】修订 [Y, n2, X]（X=n2 之后同轮次已完成）：锚 Y 可定位 ⇒ candidate=n2
-    /// （未完成）⇒ ParkedRescue 检测到 n2 之后有已完成 X ⇒ 真冲突 ⇒ 必须**整体按链尾**（null），
-    /// **不得**回落返回 n2——否则 n2 重驱完成后线性推进穿越已完成 X，重复提交（不变量①）。
+    /// 【BO-6 完成过滤推进】修订 [Y, n2, X]（X=n2 之后同轮次已完成）：锚 Y 可定位、n2 为有效停驻；
+    /// 恢复从 n2 开始，真实 Runner 重驱后过滤已完成 X，不得把停驻义务改写成普通链尾。
     /// </summary>
     [Fact]
     public void RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate()
@@ -989,23 +987,23 @@ public sealed class LocalWaitIdentityTranslationTests : IDisposable
         var runner = MakeRunner(Path.Combine(_dir, "q-anchor16"), referenceProvider: null);
         var run = RunWithOutcomes(("X", "succeeded"), ("Y", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
         var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("Y", "n2", "X"));
-        Assert.Null(relocated);
+        Assert.NotNull(relocated);
+        Assert.Equal("n2", relocated!.NodeId);
     }
 
     /// <summary>
-    /// 【R19 必改-1 裁决改写（原 M33 夹具）】停驻点被重排到**已完成**锚之前 ⇒ 两不变量真冲突
-    /// （返回停驻点 ⇒ 重驱后线性推进穿越已完成节点重复提交；返回链尾 ⇒ 停驻被吞＋假成功）。
-    /// 本批取保守方向：**不变量①优先** ⇒ 返回 null ＋ 响亮日志，语义归台账 BO-6
-    /// （Wave3 C11：带完成过滤的推进／显式失败态）。本夹具钉死该保守语义。
+    /// 【BO-6 完成过滤推进】停驻点重排到已完成锚之前时返回停驻点；真实 Runner 重驱它，
+    /// 并在推进中跳过已完成节点，不允许以链尾成功吞掉停驻义务。
     /// </summary>
     [Fact]
-    public void RecomputeSuccessor_ParkedReorderedBeforeCompletedAnchor_ConservativeNoRerun()
+    public void RecomputeSuccessor_ParkedReorderedBeforeCompletedAnchor_ReturnsParkedMarker()
     {
         var runner = MakeRunner(Path.Combine(_dir, "q-anchor10"), referenceProvider: null);
         var run = RunWithOutcomes(("n0", "succeeded"), ("n1", "succeeded"), ("n2", WorkflowRunner.LocalWaitResultWord));
-        // 新链把停驻点 n2 重排到锚 n1 之前，且 n2 之后（同轮次）有已完成 n0/n1 ⇒ 不得返回 n2。
+        // 新链把停驻点 n2 重排到锚 n1 之前；救援优先返回 n2，推进过滤由真实 Runner 覆盖。
         var relocated = runner.RecomputeSuccessor(run, TwoNodePlan("n2", "n0", "n1"));
-        Assert.Null(relocated);
+        Assert.NotNull(relocated);
+        Assert.Equal("n2", relocated!.NodeId);
     }
 
     /// <summary>
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs
index 31846f9ee..1dffbac49 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs
@@ -936,4 +936,242 @@ public class WorkflowRunnerTests : IDisposable
         Assert.Contains("未确认", run.Note); // 远端未确认事实标注
         Assert.Null(run.CurrentSubmission!.ObservedTerminal); // 不猜远端已停
     }
+
+    [Fact]
+    public async Task Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences()
+    {
+        // 真实 Runner/loop 复现 BO-6 R19 + R21 F4 与 BO-7 R21 F2：P1 先停驻，
+        // 被删除期间 A、Q、B 依次推进并让 Q 停驻；最终 P1 同身份插回完成锚前、Q 留在完成锚后，
+        // 最新游标 R 保持可定位。较晚安全的 Q 不得遮蔽较早冲突的 P1；恢复需重驱两处并过滤 A/B。
+        var workflowId = SeedFlow(new WorkflowDocument
+        {
+            Name = "删除后插回的多个停驻",
+            Nodes = [DragonNode("lead", "配置前置"), DragonNode("P1", "配置P1")],
+            Terminal = [new WorkflowTerminalAction
+            {
+                Kind = "terminal.completionAction",
+                Params = new Dictionary<string, System.Text.Json.JsonElement>
+                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("误触发收尾") },
+            }],
+        }).Split('|')[1];
+        var (runner, boundary, terminal) = MakeRunner();
+        var submitted = new List<(string NodeId, int SequenceIndex, int LoopIteration)>();
+        string? waitNode = "P1";
+        var waitLoop = 0;
+        boundary.OnSubmit = req =>
+        {
+            submitted.Add((req.Occurrence.NodeId, req.Occurrence.SequenceIndex, req.Occurrence.LoopIteration));
+            boundary.SubmitOverride = req.Occurrence.NodeId == waitNode && req.Occurrence.LoopIteration == waitLoop
+                ? BoundarySubmitResult.WaitWith("测试夹具要求本地停驻")
+                : null;
+            return Task.CompletedTask;
+        };
+
+        string Revise(params string[] ids)
+        {
+            var revision = _workflows.List().Single(x => x.WorkflowId == workflowId).Revision;
+            var document = _workflows.Load(workflowId);
+            document.Nodes = ids.Select(id => DragonNode(id, "配置" + id)).ToList();
+            document.Loop = ids.Contains("stop", StringComparer.Ordinal)
+                ? new WorkflowLoop { Mode = "immediate" }
+                : null;
+            return _workflows.Save(document, revision);
+        }
+
+        var first = await runner.StartAsync(workflowId);
+        Assert.Equal(WorkflowRunState.LocalWaitParking, first.State);
+        Assert.Contains(first.NodeOutcomes, o => o is { NodeId: "P1", LoopIteration: 0, Result: WorkflowRunner.LocalWaitResultWord });
+
+        // 删除 P1 后，Runner 必须能走到 A，再在 Q 停驻；P1 的历史义务仍保留。
+        waitNode = "Q";
+        Revise("lead", "A", "Q");
+        var second = await runner.ResumeAsync(first.RunId);
+        Assert.Equal(WorkflowRunState.LocalWaitParking, second.State);
+        Assert.Contains(second.NodeOutcomes, o => o is { NodeId: "A", Result: "succeeded" });
+        Assert.Contains(second.NodeOutcomes, o => o is { NodeId: "Q", LoopIteration: 0, Result: WorkflowRunner.LocalWaitResultWord });
+
+        // 再删除 Q，让 B 推进并在 R 停驻；最终让 P1 位于已完成 A/B 前、Q 位于其后。
+        waitNode = "R";
+        Revise("lead", "A", "B", "R");
+        var third = await runner.ResumeAsync(first.RunId);
+        Assert.Equal(WorkflowRunState.LocalWaitParking, third.State);
+        Assert.Contains(third.NodeOutcomes, o => o is { NodeId: "B", Result: "succeeded" });
+
+        waitNode = "stop";
+        Revise("P1", "lead", "A", "B", "Q", "R", "stop");
+        var finalStart = submitted.Count;
+        var final = await runner.ResumeAsync(first.RunId);
+
+        Assert.Empty(terminal.Actions); // 冲突不得按普通链尾成功或触发流程收尾
+        Assert.NotEqual(WorkflowRunState.Succeeded, final.State);
+        Assert.False(final.TailReached);
+        Assert.Equal(new[] { "P1", "Q", "R", "stop" }, submitted.Skip(finalStart).Select(x => x.NodeId));
+        Assert.Equal(WorkflowRunState.LocalWaitParking, final.State);
+        Assert.All(submitted.Skip(finalStart), x => Assert.Equal(0, x.LoopIteration));
+        Assert.Equal(1, final.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 0, Result: "succeeded" }));
+        Assert.Equal(1, final.NodeOutcomes.Count(o => o is { NodeId: "B", LoopIteration: 0, Result: "succeeded" }));
+        Assert.Equal(1, final.NodeOutcomes.Count(o => o is { NodeId: "lead", LoopIteration: 0, Result: "succeeded" }));
+        Assert.Contains(final.NodeOutcomes, o => o is { NodeId: "P1", LoopIteration: 0, Result: "succeeded" });
+        Assert.Contains(final.NodeOutcomes, o => o is { NodeId: "Q", LoopIteration: 0, Result: "succeeded" });
+        Assert.Contains(final.NodeOutcomes, o => o is { NodeId: "R", LoopIteration: 0, Result: "succeeded" });
+
+        // 进入下一轮仍不能把 P1@1/Q@1 当成 P1@0/Q@0；旧义务已在第 0 轮被清偿。
+        waitNode = "P1";
+        waitLoop = 1;
+        var nextRoundStart = submitted.Count;
+        var nextRound = await runner.ResumeAsync(first.RunId);
+        Assert.Equal(WorkflowRunState.LocalWaitParking, nextRound.State);
+        Assert.Equal(new[] { ("stop", 0), ("P1", 1) },
+            submitted.Skip(nextRoundStart).Select(x => (x.NodeId, x.LoopIteration)));
+        Assert.Equal(1, nextRound.NodeOutcomes.Count(o => o is { NodeId: "P1", LoopIteration: 0, Result: "succeeded" }));
+        Assert.Equal(1, nextRound.NodeOutcomes.Count(o => o is { NodeId: "lead", LoopIteration: 0, Result: "succeeded" }));
+        Assert.Equal(1, nextRound.NodeOutcomes.Count(o => o is { NodeId: "Q", LoopIteration: 0, Result: "succeeded" }));
+        Assert.Contains(nextRound.NodeOutcomes, o => o is { NodeId: "P1", LoopIteration: 1, Result: WorkflowRunner.LocalWaitResultWord });
+    }
+
+    [Fact]
+    public async Task Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion()
+    {
+        var seed = SeedFlow(new WorkflowDocument
+        {
+            Name = "仍有效停驻不得链尾假成功",
+            Nodes = [DragonNode("P1", "配置P1"), DragonNode("A", "配置A"),
+                DragonNode("B", "配置B"), DragonNode("Q", "配置Q"), DragonNode("R", "配置R")],
+            Terminal = [new WorkflowTerminalAction
+            {
+                Kind = "terminal.completionAction",
+                Params = new Dictionary<string, System.Text.Json.JsonElement>
+                { ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("不应执行收尾") },
+            }],
+        }).Split('|');
+        var workflowRevision = seed[0];
+        var workflowId = seed[1];
+
+        // 构造防御性恢复快照：旧 TailReached 标志与当前计划中仍有效且未完成的零发送义务并存。
+        // 正常路径由上面的 Runner 救援测试推进；本夹具单独证明该持久化不一致只能显式失败。
+        var run = _runs.CreateRun(workflowId, workflowRevision);
+        run.State = WorkflowRunState.Interrupted;
+        run.TriggerConsumed = true;
+        run.WorkflowRevision = workflowRevision;
+        run.TailReached = true;
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "P1", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord });
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "A", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "B", SequenceIndex = 2, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "Q", SequenceIndex = 3, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord });
+        _runs.Update(run);
+
+        var (runner, boundary, terminal) = MakeRunner();
+        var failed = await runner.ResumeAsync(run.RunId);
+
+        Assert.Empty(boundary.Submissions);
+        Assert.Equal(WorkflowRunState.Failed, failed.State);
+        Assert.True(failed.TailReached);
+        Assert.Contains(failed.NodeOutcomes, o => o is { NodeId: "P1", Result: WorkflowRunner.LocalWaitResultWord });
+        Assert.Contains(failed.NodeOutcomes, o => o is { NodeId: "Q", Result: WorkflowRunner.LocalWaitResultWord });
+        Assert.Null(failed.PendingCompletion);
+        Assert.Empty(terminal.Actions);
+
+        // 验收真实 Runner 的持久化结果，而不是只检查 ResumeAsync 返回的内存对象。
+        var persisted = _runs.Load(run.RunId)!;
+        Assert.Equal(WorkflowRunState.Failed, persisted.State);
+        Assert.True(persisted.TailReached);
+        Assert.Contains(persisted.NodeOutcomes, o => o is
+            { NodeId: "P1", Result: WorkflowRunner.LocalWaitResultWord });
+        Assert.Contains(persisted.NodeOutcomes, o => o is
+            { NodeId: "Q", Result: WorkflowRunner.LocalWaitResultWord });
+        Assert.Null(persisted.PendingCompletion);
+    }
+
+    [Fact]
+    public async Task Resume_CandidateAndRescueAcrossLoop_UsesFullPlanOrderAndAdvancesThroughRunner()
+    {
+        // 新修订在已跑完的第 0 轮 anchor 后插入 candidate；仍有效的 park 在第 1 轮。
+        // SequenceIndex 较小的 rescue probe 位于更晚 loop，故真实 Runner 必须先执行 candidate@0。
+        var workflowId = SeedFlow(new WorkflowDocument
+        {
+            Name = "跨轮次 candidate/rescue 全序",
+            Nodes = [DragonNode("anchor", "配置A"), DragonNode("removed", "配置旧"),
+                DragonNode("park", "配置P"), DragonNode("tail", "配置T")],
+            Loop = new WorkflowLoop { Mode = "immediate" },
+        }).Split('|')[1];
+        var revision = _workflows.List().Single(x => x.WorkflowId == workflowId).Revision;
+        var current = _workflows.Load(workflowId);
+        current.Nodes = [DragonNode("anchor", "配置A"), DragonNode("candidate", "配置C"),
+            DragonNode("park", "配置P"), DragonNode("tail", "配置T")];
+        _workflows.Save(current, revision);
+
+        var run = _runs.CreateRun(workflowId, revision);
+        run.State = WorkflowRunState.Interrupted;
+        run.TriggerConsumed = true;
+        run.Cursor = new WorkflowNodeCursor { NodeId = "removed", Occurrence = 0, LoopIteration = 1, Attempt = 1 };
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "anchor", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "park", SequenceIndex = 2, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord });
+        _runs.Update(run);
+
+        var (runner, boundary, _) = MakeRunner();
+        var submitted = new List<(string NodeId, int SequenceIndex, int LoopIteration)>();
+        boundary.OnSubmit = req =>
+        {
+            submitted.Add((req.Occurrence.NodeId, req.Occurrence.SequenceIndex, req.Occurrence.LoopIteration));
+            boundary.SubmitOverride = req.Occurrence.NodeId == "park" && req.Occurrence.LoopIteration == 1
+                ? BoundarySubmitResult.WaitWith("到达第 1 轮停驻")
+                : null;
+            return Task.CompletedTask;
+        };
+
+        var resumed = await runner.ResumeAsync(run.RunId);
+
+        Assert.Equal(WorkflowRunState.LocalWaitParking, resumed.State);
+        Assert.Equal(("candidate", 1, 0), submitted[0]); // candidate@0 < rescue probe anchor@1 的计划全序
+        Assert.DoesNotContain(submitted, x => x is { NodeId: "anchor", LoopIteration: 0 });
+        Assert.Equal(("park", 2, 1), submitted[^1]);
+        Assert.Empty(resumed.NodeOutcomes.Where(o => o is { NodeId: "candidate", LoopIteration: 0, Result: "waitLocally" }));
+    }
+
+    [Fact]
+    public async Task Resume_RescueBeforeNextLoopCandidate_UsesFullPlanOrderAndFiltersCompletedAnchor()
+    {
+        // 锚 A@0 位于循环计划尾，candidate 是 P@1；有效停驻 P@0 全序更早。
+        // Runner 必须先重驱 P@0，过滤已完成 A@0，再按计划推进到 P@1。
+        var workflowId = SeedFlow(new WorkflowDocument
+        {
+            Name = "跨轮次 rescue 早于 candidate",
+            Nodes = [DragonNode("P", "配置P"), DragonNode("A", "配置A")],
+            Loop = new WorkflowLoop { Mode = "immediate" },
+        }).Split('|')[1];
+        var revision = _workflows.List().Single(x => x.WorkflowId == workflowId).Revision;
+        var run = _runs.CreateRun(workflowId, revision);
+        run.State = WorkflowRunState.Interrupted;
+        run.TriggerConsumed = true;
+        run.Cursor = new WorkflowNodeCursor { NodeId = "removed", Occurrence = 0, LoopIteration = 1, Attempt = 1 };
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "A", SequenceIndex = 1, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
+        run.NodeOutcomes.Add(new WorkflowNodeOutcome
+        { NodeId = "P", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord });
+        _runs.Update(run);
+
+        var (runner, boundary, _) = MakeRunner();
+        var submitted = new List<(string NodeId, int SequenceIndex, int LoopIteration)>();
+        boundary.OnSubmit = req =>
+        {
+            submitted.Add((req.Occurrence.NodeId, req.Occurrence.SequenceIndex, req.Occurrence.LoopIteration));
+            boundary.SubmitOverride = req.Occurrence is { NodeId: "P", LoopIteration: 1 }
+                ? BoundarySubmitResult.WaitWith("到达下一轮停驻")
+                : null;
+            return Task.CompletedTask;
+        };
+
+        var resumed = await runner.ResumeAsync(run.RunId);
+
+        Assert.Equal(WorkflowRunState.LocalWaitParking, resumed.State);
+        Assert.Equal(("P", 0, 0), submitted[0]); // rescue P@0 < candidate P@1
+        Assert.DoesNotContain(submitted, x => x is { NodeId: "A", LoopIteration: 0 });
+        Assert.Equal(("P", 0, 1), submitted[^1]);
+    }
 }

## materials outside this batch
本批只改下列路径：接收批工作目录 `_workflow/wave3-bo6bo7-receive/**`（新增，本批证据与登记）、`_workflow/wave3-bo6-bo7/**` 的 14 个逐字节导入文件 + 新增 `INTAKE-NOTE.md`、以及导入并追加登记的 8 个来源文件（WorkflowRunner.cs、WorkflowRunnerTests.cs、LocalWaitIdentityTranslationTests.cs、ClaimSurfaceManifest.txt、R5.3、_batch21/b21_plan.md、_batch21/sb21-4-handoff-2026-09-28.md、槲寄生调度器总计划.md）。材料外变更（非本批写入者）：`Docs/design/mistletoe-session-relay-2026-09-24.md` 与 `Docs/design/unified-job-registry-master-plan.md` 的既有未提交修改（开工前即存在，本批未触碰、未提交）；工作区另有大量既有未跟踪的 `.bak`/`.stale`/历史批次目录/TestResults/日志/截图与 `_workflow/sb21-4/**`、`_batch21/**` 未跟踪工件，均保持原样。本批不据路径推断这些材料外变更的写者，也不清理它们。
