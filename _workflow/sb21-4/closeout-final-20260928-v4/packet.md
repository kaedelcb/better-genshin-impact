
## objective: _workflow/sb21-4/review/r8-review-20260928-v1/objective-r8.md L1-L15 SHA256=055b345cbe411e1cbe019e0e292d0684faa8b8501f1cffd4a6aa272038621c44

# SB21-4 BO-13 R8 最终复审目标

本轮对 BO-13 原始 R1 五项 IMPORTANT 逐项作原级最终复核，并复核已登记的 R3/R4 IMPORTANT 延伸。owner 当前明确要求五项原始 IMPORTANT 在复审前全部仍处于未闭合状态；R7 对部分项目的技术闭合建议尚未被接受。请仅依据本快照所列当前源码、具名测试、当前源 SHA、突变与回归证据，给每项结论：

- `CLOSED_AT_IMPORTANT`：仅限明确写出的有限合同，列出支持证据与 source-only/未验证边界；或
- `OPEN_IMPORTANT` / `MUST`：保留原等级，写明具体反例/缺证、后果、已尝试处理和验收条件。

重点核对：

1. `RequestRunAction` 与 `StopParkedRun` 的完整顺序：请求身份、初读、锁内最终复读、记录体 runId、状态、drive/reservation、外部责任、前置责任、Wait/Hold decision 与完整绑定校验，是否全都先于第一次 queue 副作用；queue tombstone 后 run `Cancelled` 持久化失败的返回语义与重试。
2. 全部五项的状态/身份/异常/并发合同是否由展示的代码与具名测试支持，尤其 run publish failure 后 queue tombstone 保持、再次 Stop 完成 run 而不改写 tombstone。评估两个 R8 当前源码突变是否各自在预期关键断言处失败。
3. R1 #1/#2/#4 与 R3/R4 延伸的证据范围是否足以在 IMPORTANT 原级关闭；不要把 GPT 早期建议写成 owner 已接受。
4. 哪些路径仅有源代码推断、组件边界或指定 Host 交错证据；明确不证明跨存储原子事务、任意外部 writer、断电耐久、新 OS 进程、真实 admission 服务、BGI/生产 User 或实机。

不得降级任何 IMPORTANT/MUST，不得把测试、workflow 机械检查或局部组件证据升格为生产运行证据。BO-6/7 未启动；BO-8/9 按 BO-11 原级冻结；BO-4、BO-10/12 不重开；所有生产门继续关闭。若任一 MUST/IMPORTANT 在本次复核后仍开放，结论必须是 BO-13 未闭环，由于原台账已 8/8 停止新增会诊并给 owner checkpoint。


## findings: _workflow/sb21-4/review/r8-review-20260928-v1/findings-r8.md L1-L29 SHA256=d6d380c172a1b9385d52a1090f6ffed1bd7ad453e6100117374bccea7475ee4c

# R8 当前证据与待裁决问题

## 复审前状态

原 R1 五项全部保持 IMPORTANT 且未闭合；owner 明确要求重新复核五项。R7 对 #1/#2/#4 与若干 R3/R4 延伸仅给出有限范围技术关闭建议，未被 owner 接受；#3/#5 的 R7 缺口是送审包没有包含完整 Host 顺序与关键测试正文，R7 没有确认新增产品缺陷。R8 提供补齐材料和当前版本证据。

## R1 #1：前置责任与零发送边界

`StopParkedRun` 用 Stop 专用 `HasUnresolvedPrerequisiteResponsibility`，只允许 `Succeeded/Failed/Cancelled`；`Intent/Submitted/Unknown` 和未知 enum 数值保持未决。`RunStore.HasUnresolvedExternalFact` 继续阻止未决发送/收尾责任。具名测试分别验证所有未决状态拒绝、已知终态允许，以及终态旁置 `SendAttempted/jobId` 的有限兼容行为，并比较 run/queue 原字节。请仅按这些独立持久状态与 Stop 合同评定；不推导 producer 对终态分类必然正确或穷尽任意历史字段组合。

## R1 #2：身份、恢复与 repark

`HasValidParkedDecision` 校验 run/workflow/revision、cursor、deferred submission/key、完整 binding/context、source identity、规范候选/准入 identity 与 `0 < binding.RecordRevision <= context.RecordRevision <= run.RecordRevision`。StartupHandoff 来源必须绑定同一 run 与 admission scope；PanelFlowRegistration 仅校验 admission 时解析父记录生成的成对快照，不构成重授权。具名正反例比较字节；恢复后 `Interrupted`/revision 持久化、Resume/repark Context 更新且保留 binding/generation/HWM，覆盖组件内恢复和同进程路径。请明确组件重建不等于 OS 进程/断电验证。

## R1 #3：请求 runId 与最终复读身份

当前完整源码位于 `TaskCenterHost.cs` 第 380–650 行。入口 first read 核验请求 ID 与 body ID；停驻 Stop 在 `_gate` 内进行最终 `_runs.Load(runId)`，核验 null/body ID/state 后再检查 reservation/drive、未决责任、decision/binding；队列取消与 run 更新才发生在这些检查之后。`LocalWaitParkingStop_FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes` 对最终复读时把 A 路径替换成 B 记录的情形要求 `Unavailable` 且两个 run/queue 字节不变。R8 当前源码突变删除最终 body-ID guard 后，测试在预期 `Expected: Unavailable / Actual: Effective` 状态断言失败：testId `14b12c92-31c4-d84d-5129-de01fac6b561`；Host 原/ mutant /恢复 SHA 分别为 `b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f` / `915be30a876c4b33a736ec0f76613ee547e97e0b3d400882a83d53e72227ac78` / 原 SHA。

## R1 #4：Admission 终局对账

`StopParkedRun` 在 gate 外进行终局回写；read timeout/拒绝/write exhaustion/final read failure、显式 retry、Host 恢复扫描、logger 异常 sibling、双 worker barrier 与另一 run 隔离均有组件用例。请把边界限定为已展示同 Host/service 的状态转移与可观察 retry；不声称永久存储故障下自动可用或跨任意进程压力安全。R3 same-session retry/sibling 与 R4 overlap 扩展要逐项映射到对应当前证据。

## R1 #5：Queue/Run/read/reservation 失败窗口

完整 `LocalWaitParkingStop_RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt` 位于 `LocalWaitFinalizationContractTests.cs` 第 1156–1180 行：首次 Stop 注入 run `Cancelled` publish failure；断言返回 `Unavailable`、run 文件原字节不变、Run 仍 `LocalWaitParking`、queue 为 `Cancelled` tombstone；保存 tombstone bytes 后移除故障，再次 Stop 成功且 Run `Cancelled`、queue bytes 与保存值精确相等。R8 final TRX 具名结果通过。当前 QueueStore 中 AlreadyCancelled 分支被突变为再次改写 tombstone 后，同一测试在最终集合字节断言失败，testId `c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72`；原/ mutant /恢复 SHA `e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b` / `e78790179f479e5ea978d23d169d687ed423aa90cae771b63570bc559820fb24` / 原 SHA。其他具名项覆盖 queue publish failure、read exception/corruption、最终读错绑、Resume reservation 与 payload mismatch。这里证明有限测试接缝，不能称跨文件原子事务、崩溃耐久或任意 gate 外 writer 竞争。

## R3/R4 扩展与验证

R3 #1/#2、R4 overlap/identity 的 R7 意见和边界见 `gpt-r7-review.md`。R8 当前 build/回归、ClaimSurface 和 testId 差集见 `validation-summary-r8.md`；R8 两个突变的独立 baseline/build/mutant/restored 日志/TRX、预期命名失败及 SHA 见 `mutations/mutations-r8-v1.json`。首次助手全量有一项 DocsFixtureReferenceGuard 失败，因 R5.3 中 enum-like 英文状态被当作 fixture；已改为中文状态叙述后重新 ClaimSurface regen/no-env 和全量通过。两次结果均保留；请审查最终通过帧，不隐藏第一次失败。


## budget: _workflow/sb21-4/review/r8-review-20260928-v1/budget-r8.md L1-L6 SHA256=8dd5dd219644f1f8b64633a098acd3ff5be6fbf689a1a5d11e09491024394876

# SB21-4 独立会诊预算（R8）

- 唯一权威计数源：`C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch21-sb21-4.json`。开 R8 前实际 `rounds` 为 R1–R7 共 7 条，最大 8；R2 是已发出但执行器失败，仍计次；本地未发送预检不计。当前 Goal 摘要写 2/8，但机器账本原件与 handoff 历史均为 R1–R7/7，不能重置或按过期摘要覆盖。R8 是 8/8 最后一轮。
- 仅 GPT `gpt-6-astra`、medium。只发送一次最终复审请求；若请求成功发送后失败/超时仍计第 8 次，不因预算上限重试。
- 原始五项 R1 IMPORTANT 在复审前全部保持开放；R3/R4 IMPORTANT 扩展同样不降级。R8 若仍有任一 MUST/IMPORTANT，停止会诊与批次闭环，保留 owner checkpoint、生产门关闭，不交付后续 BO-6/7 Goal。
- BO-6/7 未启动；BO-8/9 仅按 BO-11 冻结。BO-4、BO-10/12 不重开；生产入口、真实 User、R5.8、E3/E4/E5、热键和 BGI 生产进程保持关闭。


## contract: _workflow/sb21-4/raw-bo-obligations.json L1-L58 SHA256=9cfaecb1b1029bcb64226404e79c08ac1f16435a2f8481b5703a54db837b9a65

{
  "source": "C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch20.json",
  "batch": "batch20",
  "captured_at": "2026-09-28T00:55:48.5047952+08:00",
  "obligations": [
    {
      "id": "BO-6",
      "title": "停驻点重排至已完成出现之前的重驱推进语义",
      "content": "停驻点被修订重排到已完成锚之前时，不变量①（不重跑已完成出现）与②（不吞停驻出现）真冲突。Wave1 保守取①：返回 null＋响亮日志。C11/Wave3 必须裁决并实现「带完成过滤的推进」或「显式失败态」，并以端到端驱动夹具（救援返回后前进 N 步、已完成节点零重复提交）闭合，R5 突变验证适用。",
      "source": "Wave1 R19 必改-1 + 建议-4",
      "owner": "Wave3"
    },
    {
      "id": "BO-7",
      "title": "停驻救援全序裁决与多有效停驻点处理",
      "content": "①救援返回点与锚候选的全序裁决（跨轮次形态：rescue 覆盖更早未完成 candidate）；②多个有效停驻标记按计划全序完整裁决（「最新有效停驻在锚后 ⇒ break」漏看更早有效停驻；「删 P1→插回 P1」连续修订场景）。两者完整修复依赖 BO-6 的带完成过滤推进＋端到端驱动夹具（带 loop 计划），Wave1 冻结点生产不可达。",
      "source": "Wave1 R21 必改-F1残余/F2",
      "owner": "Wave3"
    },
    {
      "id": "BO-8",
      "title": "恢复点之后的推进段无完成过滤（继承缺陷）",
      "content": "RecomputeSuccessor 返回点之后的线性推进段（DriveAsync/Relocate）无完成过滤：修订重排把已完成节点挪到恢复点之后时该节点被二次提交（反例①[ n3,n2,Y]→[n2,X,n3,Y] 纯完成重排生产可达；反例②停驻＋探针形态）。修复归驱动推进层完成过滤＝Wave3 C11（本批纪律不擅改生产推进语义）；RecomputeSuccessor 不变量①已收窄为「仅限返回点本身」。",
      "source": "Wave1 R29 重要（继承缺陷）",
      "owner": "Wave3"
    },
    {
      "id": "BO-9",
      "title": "多有效停驻点跨轮次推进语义（接线前阻塞残项）",
      "content": "多有效停驻并存且分处不同轮次/锚前后时，救援返回点之后的推进段语义（是否会穿越已完成出现）需在 C11 重驱机制实现时以端到端驱动夹具裁决；tailBound/ParkedRescue 排序口径已在 Wave1 R34 统一为计划全序最早。",
      "source": "Wave2 R34 重要-F5",
      "owner": "Wave3"
    },
    {
      "id": "BO-11",
      "title": "Wave3 冻结残项打包（owner 止损裁决）",
      "content": "R44 三项中未修部分（宿主层停驻重驱端到端夹具）＋Wave4 名义缺口（C12 映射合同落字）＋RecomputeSuccessor 推进段完成过滤（BO-8）——全部归 Wave3/接线批 C11 验证面；接线批开工先读 BO-1~BO-11。",
      "source": "owner 止损裁决 2026-09-26",
      "owner": "接线批/Wave3"
    },
    {
      "id": "BO-13",
      "title": "停驻运行的会话内终局处置出口缺失（接线批）",
      "content": "停驻运行（LocalWaitParking）无 Stop 通道（仅 Paused 受理）、恢复→再停驻循环、同流程新跑被 ActiveStates 阻塞、等待项清理路径缺失。接线批必须实现：停驻运行显式放弃通道（终局化＋等待项清理）＋持久拒登形态的收敛终态，并以夹具核销。生产不可达（接缝恒 false）。",
      "source": "Wave3 R47 重要",
      "owner": "接线批"
    }
  ],
  "original_bo13_finding": {
    "level": "重要",
    "location": "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs:46-50（ActiveStates 新增",
    "description": "LocalWaitParking 被同时赋予「活跃（阻塞同流程新启动）」与「仅可恢复重驱」两个语义，\n        但没有**终局处置出口**：①ActiveStates 含停驻 ⇒ StartWorkflowAsync／SubmitFlowStartViaAdmissionAsync／\n        RegisterStartHandoff 三处 `sameFlow.Any(ActiveStates)` 全部阻塞同流程新跑；②RequestRunAction 的\n        Stop 终态化分支只接 Paused，停驻运行走 `_drives` 查找落空 ⇒ Unavailable「请用『恢复』」；\n        ③若停驻成因是**持久性**的（登记被拒的接线缺陷形态、scope 长期不可得、或下述 F4 的存量载荷\n        冲突在 24h 墓碑裁剪前），ResumeAsync 重驱只会再停驻——会话内形成「恢复→再停驻」循环，\n        用户在会话内**没有任何放弃该运行的通道**。唯一逃逸是进程重启（RecoverOnStart 收敛 Interrupte",
    "consequence": "（接线后）某流程的停驻运行在登记持续被拒/等待项永不就绪时，会话内永久占用该流程",
    "counterexample_attempts": "我尝试寻找既有处置通道证伪：①RequestRunAction Stop——仅 Paused 分支（源码确认）；",
    "disposition": "已按 owner 冻结裁决登记 **BO-13**（不修）：停驻运行的会话内终局处置出口缺失（Stop 仅受理 Paused、恢复→再停驻循环、同流程新跑被 ActiveStates 阻塞、等待项清理路径缺失）——接线批必须实现「停驻运行显式放弃通道＋持久拒登形态收敛终态＋等待项清理」，并以夹具核销。生产不可达（接缝恒 false）。",
    "evidence": "test/ledger-batch20.json; MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs"
  }
}

## original-findings: _workflow/sb21-4/review/gpt-r1-review.md L1-L21 SHA256=d9a2433e48361dca62928534780a95ffc018ec811b80430cd24beb6f4cc340a4

# SB21-4 GPT R1 review

- Provider/model/effort: GPT / `gpt-6-astra` / medium.
- Sent requests: 1; this review consumes round 1 of the separate SB21-4 8-request budget.
- Review packet: `_workflow/sb21-4/review/gpt-r1-snapshot-v3`; workflow review audit and verify both passed before dispatch.
- Scope limitation reported by the packet: the review saw the complete SB21-4 scoped source diff and status, but the package did not contain every full diff for other tracked documentation/claim-surface changes. Those remain material-out and are not reviewed as part of the BO-13 code assessment.
- GPT did not execute commands or validate hashes, builds, TRX files, or runtime behavior; those require local evidence.

## Findings (all original level IMPORTANT)

1. **Prerequisite responsibility was not covered by the Stop guard.** `RunStore.HasUnresolvedExternalFact` intentionally excludes prerequisite actions to preserve its recovery behavior. A parked record can therefore retain `PrerequisiteActionRecord` responsibility such as `Submitted`/`Unknown`, `SendAttempted=true`, or a job ID after the workflow strategy changes, yet the new Stop path could tombstone the wait and cancel the run. Required closure: a Stop-specific responsibility guard that leaves recovery classification unchanged, with named tests asserting the run and queue bytes remain unchanged.
2. **A persisted Wait decision could be incomplete or internally inconsistent.** The host did not require the context cursor snapshot and deferred submission identity to match the run; required admission/candidate identity fields could be absent or inconsistent. One fixture itself used a null admission identity and an invented candidate while expecting Stop to succeed. Required closure: canonical identity and cursor checks, precise Deferred submission binding, fixtures built from the shared identity factory, and negative byte-preservation cases. Do not require the saved decision revision to equal the run's later post-update revision.
3. **The requested run ID was not checked against the record embedded in the loaded file.** A file addressed as run A could contain a parked record for run B; `Stop(A)` could then cancel B's queue binding and write B's run record. Required closure: reject empty/mismatched IDs before queue access, with a misbound filename fixture asserting both run files and queue bytes stay unchanged.
4. **Successful Host Stop omitted admission terminal reconciliation.** `ObserveDriveAsync.finally` can finish when the runner parks, before the run becomes terminal. An admission-wired accepted flow-registration operation can then remain `Accepted` after Stop releases the run slot. Required closure: invoke the existing terminal reconciliation after successful run persistence, outside the Host gate, and test an admission-wired accepted registration.
5. **Failure and race branches lacked behavior evidence.** Queue cancel write failure, tombstone-success/run-publish-failure retry, Stop racing a Resume reservation, and run-record read failure were not behavior-tested; the initial and second run reads also needed structured error handling. Required closure: storage fault hooks/barriers and named tests for retry, reservation refusal, and read failure.

GPT considered BO-11's BO-6/7/8/9 classifications appropriately conservative: helper and component ordering evidence does not establish Runner advancement; the historical MUST/IMPORTANT levels remain open for owner follow-up. Recommendation was to keep BO-13 IMPORTANT and production gates closed pending closure evidence.

## Local disposition after R1

The five findings were treated as implementation work in the already authorized BO-13 scope. The resulting named counterexamples were red on the then-current implementation and pass after repair; current reverse-mutation evidence is in `_workflow/sb21-4/mutations-review-r1-retry2/`. R1's prerequisite finding is checked through the new Stop-specific guard; the old standalone `RunStore.HasUnresolvedExternalFact` reverse mutant became redundant after the stricter Deferred submission identity check and is explicitly excluded from the current independent mutation count. Admission terminal write-back is covered by an admission-wired Host fixture. A second GPT review is required to disposition R1 at its original severity before marking it closed.


## consultation-ledger-brief: _workflow/sb21-4/review/r8-review-20260928-v1/ledger-r8-brief.json L1-L89 SHA256=b6974636c9d81b65094937a7f5b59492f79ee41cad2b2c3fa57476f76777d06a

{
  "schema_version": 1,
  "purpose": "Compact R8 review view of the current independent machine ledger; the complete immutable pre-R8 snapshot remains alongside this file.",
  "external_machine_ledger": "C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch21-sb21-4.json",
  "snapshot_path": "_workflow/sb21-4/review/r8-review-20260928-v1/ledger-before-r8.json",
  "snapshot_sha256": "83414505abf17f33cadff3053d07bcb521bff80b38508dde83f46d475be867f5",
  "max_rounds": 8,
  "round_count": 7,
  "rounds": [
    {
      "number": 1,
      "provider": "GPT",
      "model": "gpt-6-astra",
      "effort": "medium",
      "attempts": 1,
      "recorded_at": "2026-09-27 18:05 UTC",
      "status": null,
      "verdict": "5 项 BO-13 IMPORTANT 未闭合；BO-11 保持原级未闭合"
    },
    {
      "number": 2,
      "provider": "GPT",
      "model": "gpt-6-astra",
      "effort": "medium",
      "attempts": 1,
      "recorded_at": "2026-09-27 20:00 UTC",
      "status": null,
      "verdict": "执行器启动失败，未返回评审报告；无质量结论"
    },
    {
      "number": 3,
      "provider": "GPT",
      "model": "gpt-6-astra",
      "effort": "medium",
      "attempts": 1,
      "recorded_at": "2026-09-27 21:04 UTC",
      "status": null,
      "verdict": "R1 finding 2 core closed at original IMPORTANT; R1 findings 1/3/4/5 remain open, with an additional IMPORTANT evidence-gap finding for 1/3/5 and SHOULD packet/provenance gap; no new MUST confirmed. BO-11 registration faithful and still open at original grades."
    },
    {
      "number": 4,
      "provider": "GPT",
      "model": "gpt-6-astra",
      "effort": "medium",
      "attempts": 1,
      "recorded_at": "2026-09-27 22:07 UTC",
      "status": null,
      "verdict": "R4: original IMPORTANT 1/3/5 can close at IMPORTANT within bounded persisted-state/parked-Stop scope; IMPORTANT 2/4 remain open. R4 IMPORTANT #4 overlap/terminal-write idempotency gap; R4 #2 full revision/generation matrix gap. SHOULD stale docs/current records and §24.120.5 coverage remain."
    },
    {
      "number": 5,
      "provider": "GPT",
      "model": "gpt-6-astra",
      "effort": "medium",
      "attempts": 1,
      "recorded_at": "2026-09-27 23:24 UTC",
      "status": null,
      "verdict": "R5: R1 #1/#3/#5 may close at original IMPORTANT within bounded Stop contract. R1 #2/#4 remain OPEN_IMPORTANT; R3 IMPORTANT #1 and R4 overlap/identity IMPORTANT remain open; R3 IMPORTANT #2 closed for named gaps. SHOULD docs/provenance remains open. BO-13 cannot close. BO-6/7 not started; production gates closed."
    },
    {
      "number": 6,
      "provider": "GPT",
      "model": "gpt-6-astra",
      "effort": "medium",
      "attempts": 1,
      "recorded_at": "2026-09-28 UTC",
      "status": null,
      "verdict": "R1 #2/R4 identity remain OPEN_IMPORTANT due StartupHandoff SourceIdentity not bound to run.RunId and recovery/repark assertions incomplete; R6 technical dispositions recommend bounded original-grade closure for R1 #1/#3/#4/#5, R3 #1/#2 and R4 overlap, but owner keeps all five original R1 items open pending acceptance. SHOULD docs/provenance corrections required. BO-13 not closed."
    },
    {
      "number": 7,
      "provider": "GPT",
      "model": "gpt-6-astra",
      "effort": "medium",
      "attempts": 1,
      "recorded_at": "2026-09-28 01:33 UTC",
      "status": null,
      "verdict": "R7: R1 #1/#2/#4, R3 #1/#2, R4 overlap/identity receive bounded CLOSED_AT_IMPORTANT technical recommendations; R1 #3/#5 remain OPEN_IMPORTANT because the current Stop source order and run-publish-failure retry fixture were not fully displayed. Three SHOULD documentation/provenance/mutation-count corrections. BO-13 remains open; R8 is the final allowed request."
    }
  ],
  "current_disposition": "R7 returned at 7/8. Owner explicitly keeps all five R1 BO-13 IMPORTANT findings open pending R8; R7 #1/#2/#4 finite technical closure recommendations were not accepted, and #3/#5 lacked full source/test evidence in that packet. Updated R8 snapshot now includes complete Host Stop order, run-publish-failure test body, two current-source reverse mutations, final ClaimSurface/build/targeted/full validation and exact testId diffs. R8 is the last allowed request; if any MUST/IMPORTANT remains open after it, stop at 8/8 and keep production gates closed. BO-6/7 not started; BO-8/9 remain frozen under BO-11.",
  "owner_checkpoint": {
    "batch": "SB21-4",
    "status": "OPEN_IMPORTANT_PENDING_R8",
    "bo13": "R7 returned at 7/8. Owner keeps R1 IMPORTANT #1-#5 all open pending a complete R8 review; R7 recommendations for #1/#2/#4 are not owner acceptance. R7 #3/#5 packet gaps are now supplied: complete RequestRunAction/StopParkedRun order, complete run-publish-failure fixture and R8 current-source reverse mutations. Review each finding at original grade. R8 is 8/8 and last request; any remaining MUST/IMPORTANT requires an owner checkpoint with evidence, risk, attempted handling and acceptance criteria. See rounds[6] and the R8 packet.",
    "production_gates": "CLOSED: product entry, real User, R5.8, E3/E4/E5, hotkey surface, BGI production process; no production runtime validation claimed."
  },
  "budget_rule": "R2 was sent and failed, so it counts. Pre-send local rejections do not count. R8 is the final request at 8/8."
}


## prior-review-result: _workflow/sb21-4/review/gpt-r7-review.md L1-L48 SHA256=5cc27997765c79b350fe65e922ba0c0b7ead315bae47c6df49c728c2ab3782b9

# GPT R7 复审结果 — SB21-4 BO-13

- Provider/model/effort: GPT / `gpt-6-astra` / medium；已发送请求 1 次，R7 计入 8 次累计预算。
- 工具返回成功，attempts=1；只评审 `_workflow/sb21-4/review/r7-review-20260928-v1/audit-review-r7-v3/packet.md`。`diff_included=false`；完整 status、scope diff 和材料外 diff 均在该 packet 正文内。
- GPT 未运行本地命令或独立读取/核对源文件哈希、TRX、构建日志；下列测试/哈希均为包内所报证据，非 GPT 自行执行验证。
- 独立台账 R1-R6 六条保留；R2 已发送但执行器失败仍计次。R7 后总计 7/8，仅余 R8。

> 记录说明：本文件为本轮返回结论的逐项记录。会诊工具的长文本在本地工具输出显示时被截断，故本文件保留了全部可见 finding、裁决和验收条件，并明确标出不能从截断显示恢复的逐字原文；不声称是完整原始转录。

## GPT 总体结论

- **不要宣布 BO-13 整体闭环。** R1 #3 与 #5 保持 OPEN_IMPORTANT；R8 最小范围是补齐当前完整 `RequestRunAction`、`StopParkedRun` 顺序与 run publish failure 后首次返回/墓碑保持/重试不重写的测试正文和固定版本结果。
- R1 #1/#2/#4、R3 #1/#2、R4 overlap、R4 identity 均获 `CLOSED_AT_IMPORTANT` 技术处置建议，但仅限各自列明的合同和直接证据。它们不自动更新 owner 台账；owner 必须接受有限边界。
- SHOULD：修正文档/台账当前状态矛盾；把突变结论说准确为六次分别执行的断言实验、五种不同源码 mutant（M3/M5 的原始/突变/恢复源码 SHA 完全相同）；对齐 §24.124 的突变枚举顺序。

## 原始五项 R1

| 项目 | GPT 原级技术结论 | 证据范围及限制 |
|---|---|---|
| #1 前置责任 | `CLOSED_AT_IMPORTANT`，有限持久状态 Stop 合同。 | Intent、Submitted、Unknown、枚举外值拒绝并保留 run/queue 原字节；Succeeded/Failed/Cancelled 为允许收敛的终态，另有 Succeeded＋历史 send/job 正例。不能据此证明 producer 正确标终态或穷举所有终态/历史字段组合；恢复谓词仍是独立的源码支持。 |
| #2 身份一致性 | `CLOSED_AT_IMPORTANT`，限持久快照检查与所列生命周期。 | StartupHandoff 另一 RunId 双快照借用负例拒绝且字节不变；合法身份正例放行。恢复 Interrupted/revision 与重启、同进程两类 Context refresh 均有断言，binding/generation/HWM 保留。Panel registration 只证明配对快照；没有证明 Stop 重新鉴权/发送授权。调用链对 admission 父记录解析仅展示至有限范围；并非每个字段都有独立负例。 |
| #3 请求 RunId 与正文身份 | **OPEN_IMPORTANT**。 | 已见最终读错绑、损坏、IO 异常及 reservation 字节断言；但 GPT 未见完整 RequestRunAction/StopParkedRun 主体与最终复读后到 queue 副作用前的完整顺序，不能确认最后读取对象的身份、状态、责任、binding 检查全都位于首次队列副作用前。没有确认新产品缺陷，这是当前审查材料缺口。 |
| #4 Admission 终局对账 | `CLOSED_AT_IMPORTANT`，限组件内可观察重试合同。 | 覆盖 read timeout、逻辑拒绝、write exhaustion、final read failure、坏状态/版本、Host 重建后显式重试；throwing logger 不阻断 sibling，重试不改已终局 sibling revision/time。Run 已 Cancelled 而 registration 暂为 Accepted 时允许返回 Unavailable/重试，不承诺永久存储故障自动收敛；Host 重建不是新 OS 进程或自动启动扫描证明。 |
| #5 Queue/Run/Reservation/Read 故障与竞争 | **OPEN_IMPORTANT**。 | 已见 queue prepublish failure、最终 run 读取故障与 reservation-held 字节比较；但 `RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt` 关键后半测试正文及当前完整 Host 顺序未随包展开，不能确认 run publish failure 首次返回后 tombstone 保持且重试不改写/覆盖并发更新。独立 queue I/O-read exception、missing item、任意 gate 外 writer 和崩溃耐久也未证明。 |

## R3/R4 延伸项

- R3 #1 `CLOSED_AT_IMPORTANT`：与 R1 #4 同一有限合同，caller 能观察失败并重试，logger failure 不破坏 sibling；不是永久可用性承诺。
- R3 #2 `CLOSED_AT_IMPORTANT`，只限已展示的独立状态输入、最终读取错绑/损坏/异常与 reservation byte evidence gap；历史逐分支突变不重绑成 R7 实验，也不替代 R1 #3/#5 完整实现审查。
- R4 overlap `CLOSED_AT_IMPORTANT`，限同 Host 确定交错。测试等待两个 reconciliation core 都越过 finally，再看单一 lease revision、UpdatedRevision、另一 run 序列化 operation 和全 lease bytes。两个调用来自同一 admission service 且 `_gate` 已串行；不能外推为多 service/多进程并发。`WithLock` 未展开，另 run 是重序列化 operation bytes，不是对应文件原始文本 bytes。
- R4 identity/revision `CLOSED_AT_IMPORTANT`，按 R1 #2 所列快照和恢复边界；合法旧 binding 允许。不能写成任意双快照恶意篡改检测。

## R7 六次突变实验的分级解释

1. M1 StartupHandoff 借用身份负例；在状态结果断言处失败，未执行后续 byte assertion。负例的字节保持由 restored source 用例支持。
2. M2 恢复状态未落盘；M3 恢复后 Context refresh；M4 恢复 revision；M5 同进程 Context refresh；M6 合法 StartupHandoff 正例。
3. M3 与 M5 使用完全相同的 WorkflowRunner 原始、mutant、restored SHA，分别命中不同生命周期断言。因此这是 **6 次测试突变实验、5 个不同源码 mutant**，不是六种互异源码突变。

GPT 只能确认 packet 所报的 baseline/build/mutant/restored 与 SHA 相等文字，未独立运行工具。testId 共享不证明测试正文未改。

## R8 最小验收条件

1. 提供当前 `TaskCenterHost.RequestRunAction` 与完整 `StopParkedRun` 源码，显示请求身份、最终 read、状态/责任/binding、reservation/drive 检查、queue→run 写序和异常结构化结果的真实顺序。
2. 展开 `LocalWaitParkingStop_RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt` 全测试正文，记录 final 当前版本具名结果，证明 run 发布失败首次返回、queue tombstone 保持及后续重试不重写该 tombstone；不宣称跨存储原子事务。
3. 通过 §24.124、plan、handoff、机器独立台账同步修正 R7 指出的当前摘要/历史摘要混淆、允许终态文字、回归/ClaimSurface 路径和 M3/M5 去重说明；保留历史轮次与原等级，不覆盖早期状态。
4. 新快照必须附完整 `git status --porcelain`、本批相关 diff、材料外 diff；再次执行 workflow `audit --stage review` + `verify`，model/effort 保持 GPT `gpt-6-astra`/medium。复审后若任一 IMPORTANT/MUST 仍开放，即到 8/8 停止新增会诊并交 owner checkpoint，不得降级/收口/交付下一施工 Goal。

所有生产入口、真实 User、R5.8、E3/E4/E5、热键、BGI 生产进程继续关闭。BO-6/7 未启动；BO-8/9 仍为 BO-11 原级冻结项。


## state-matrix: _workflow/sb21-4/scope-and-state-table.md L1-L62 SHA256=4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6

# SB21-4：范围、状态转移与 BO-11 残项处置

## 授权范围

- **BO-13**：给 `LocalWaitParking` 增加会话内显式 Stop 出口；安全地终态化本地等待和持久 Hold/拒登；只清理由该运行完整绑定的队列项。
- **BO-11**：逐项核对 BO-6/7/8/9 的原始台账和现行实现。可实现被授权的具体范围，也可按原等级列明证据、风险、owner 和验收条件继续登记。本批不因同属 Wave3/C11 扩成四个额外修复批次。
- BO-4 已由 SB21-3 按 owner 接受的“历史修复＋本批验证”路径验收；BO-10/12 已由 SB21-2 验收。本批不重开。
- 生产入口、真实 User、R5.8、E3/E4/E5、热键和 BGI 生产进程门继续关闭。

## BO-13 停驻运行终局转移

| 当前记录与事实 | 动作/事件 | 期望持久状态 | 必须保留的身份 | 队列和同流程槽位 |
|---|---|---|---|---|
| `LocalWaitParking`，NoSend 已确认；Wait 裁定与当前 run/cursor/submission/context/binding 一致；无未决外部/前置动作责任 | 用户显式 Stop，宿主已无该流程 drive/reservation | `Cancelled`；追加可观察说明；不执行 flow terminal/收尾动作 | 保留 run/workflow/revision、cursor 的 node/occurrence/loop/attempt、submission key、旧 outcomes、Wait context 和原 binding | 原子检查全部队列可见不可变字段，仅精确匹配时写 `Cancelled` 墓碑；代际和 HWM 不回退，其他项不变；`Cancelled` 释放 `ActiveStates` 槽位 |
| `LocalWaitParking`，持久 Hold/拒登、NoSend 已确认、无 binding、无未决外部事实 | 显式 Stop | `Cancelled`；保留 Hold reason/context；不执行收尾 | 保留 run/cursor/submission/outcomes/decision context | 无 binding 就不写队列；终态释放同流程槽位 |
| Wait binding 对应队列项缺失或已是墓碑 | 显式 Stop | 无未决事实且运行身份有效时，终态 `Cancelled` | 不从队列重建或替换旧身份 | 缺失即已清；已有墓碑不重复写；HWM 不变 |
| Wait binding 的 `itemId` 命中项，但稳定身份、候选号、准入身份、流程/命名空间、排序、前置引用或登记时刻任一不匹配 | 显式 Stop | `LocalWaitParking`，返回 `Unavailable` | 完整保留原运行记录 | 队列原字节和不匹配项保持不变，避免旧 run 错取消新载荷 |
| `LocalWaitParking`/Hold 有未决发送/收尾事实；前置动作责任仍处于非终态（Intent/Submitted/Unknown/枚举外值）；或同流程仍有 drive/reservation | 显式 Stop | 返回 `Unavailable`，运行不转移 | 所有未决责任事实原样保留 | 不触碰等待队列，维持现有互斥保护。已终态前置责任可保留历史 send/job 字段，不单独阻止 Stop；按其 Succeeded/Failed/Cancelled 状态解释 |
| `StartupHandoff` 的 Context 与 binding 共用另一 run 的来源身份，或本 run 缺 scope/handoff 佐证 | 显式 Stop | 返回 `Unavailable`，不终态化 | run 与 queue 原字节保留 | 合法来源要求 `SourceIdentity == run.RunId`、规范 `AdmissionSourceScope` 与同 run 的 Start/ArmTrigger handoff。`PanelFlowRegistration` 来源必须在判定时由唯一父登记解析；Stop 只核对 Context/binding 配对快照，不把它当发送许可 |
| 请求 runId 与记录体 runId 不同、记录损坏/读失败、Wait 裁定游标快照不一致、deferred submission key 不一致、候选/准入身份不是共享 factory 的规范身份，或 Hold 错带 binding | 显式 Stop | 返回 `Unavailable`，不终态化 | run 文件和其所载其他身份记录原字节保留 | 不清理任何队列项；拒绝用路径名替记录身份背书 |
| 等待队列无法读取/损坏或取消写入失败 | 显式 Stop | 返回 `Unavailable`，运行仍可重试 | 原运行身份不改 | Store 错误不当作空队列；完成 Stop 前不终态化 |
| 已经 `Cancelled` | 再次 Stop | 返回终态提示，不产生第二次转移 | 记录修订与 decision/outcomes 不变 | 不改队列墓碑/HWM |
| 停驻后显式恢复，复核仍为 Wait/Hold | `ResumeRunAsync` | 仍 `LocalWaitParking`，不是 `Unknown`/`Cancelled` | 同一 run 和 cursor occurrence/loop/attempt；Wait binding 沿既有 Resume 规则复核 | 保持/重发同一 binding；之后 Stop 仍按当前完整 binding 清理 |
| 原 run 已显式停止 | 同流程新 Start | 可受理一个全新 run；旧 run 留在历史 | 新 run 有新 RunId，旧 run/outcomes 不改写 | 终态记录不再占 `ActiveStates` 槽位；Unknown/未决记录仍受原护栏拦截 |

实现顺序是先按 binding 写队列墓碑，再将 run 更新为 `Cancelled`，最后在 gate 外把已关联的 admission registration 回写到 terminal。若队列发布失败，原 run/queue 字节保留并可重试；若 run 终态落盘失败，队列墓碑已写而 run 保持原状态，重试识别 tombstone 后完成 run 状态且不重复写队列；队列读失败/载荷不匹配拒绝终态化。缺失项允许终态化；Hold 无 binding 时不访问队列。读损坏记录、路径/记录身份不符、游标或提交身份不符、前置动作责任未结及并发 reservation 均不可清理队列。错绑最终复读测试的字节基线为替换注入后的 A/B 记录与队列，不声称恢复注入前 A 内容；独立 queue 文件读取 I/O 故障未单独注入验证。

### BO-13 当前行为与验证

- `TaskCenterHost.RequestRunAction(Stop)` 仍保留原有 `Paused` 出口，另为无 drive 的 `LocalWaitParking` 走专用终态化路径；入口和 gate 内复读均核对路径 runId 与记录体 runId。转移前核对同流程无 reservation/drive，并分别用 `RunStore.HasUnresolvedExternalFact` 和 Stop 专用 `HasUnresolvedPrerequisiteResponsibility` 拒绝未决提交/收尾事实及前置动作责任；后者不改变恢复扫描的谓词合同。
- `HasValidParkedDecision` 绑定当前 run/workflow/revision、cursor 和 decision cursor 快照；要求当前 `submission` 为同一派生 key 的 `LocalWaitDeferred`，无 send attempt/job/accepted identity/terminal receipt。Hold 必须无 binding；Wait 必须以 context/binding 全字段互相一致，并经共享身份翻译和 successor identity factory 得到规范候选/准入身份。合法关系为 `0 < binding.RecordRevision <= context.RecordRevision <= run.RecordRevision`：恢复复核可更新 Context 并保留旧 binding；不要求 equality，但拒绝 Context 早于 binding 或任一快照领先当前 run。
- 来源身份分类型：`StartupHandoff` 的 `SourceIdentity` 必须等于 `run.RunId`，同一 run 还须有规范 `AdmissionSourceScope`、匹配的 binding scope 和 `Start`/`ArmTrigger` handoff 记录。`PanelFlowRegistration` 的身份来自该 run 唯一父登记的 `RequestIdentity`，Context 与 binding 保存同一 RequestIdentity 快照；Stop 检查配对一致性，不把快照当作发送许可，也不从队列读取来源身份（队列镜像不携带它）。
- `LocalWaitQueueStore.Cancel(LocalWaitBinding, ...)` 在路径锁内核对队列可见载荷；仅精确匹配时墓碑化。身份复用/载荷漂移返回 `PayloadMismatch`，不会取消它。
- NoSend 等待和 Hold 均保留在历史；Wait 清理保留其 generation、队列全局 `generationHighWater` 和无关等待项。重复 Stop 不再写盘。
- R6 正式复审后处理：#4/#R3-1 的 logger 异常由异常安全 `TryLog` 阻断，命名 throwing-logger 夹具先在旧实现以 sibling 仍为 Accepted 失败；R4 overlap 夹具加入 reconciliation 完成信号，在两条 worker 均退出后才查单次 revision、UpdatedRevision、另一 run operation JSON 和重复 Stop 全租约原始字节。R6 定向为 47 个用例。
- R7 补足 R1 #2/R4 identity：StartupHandoff 双快照借用其他 run identity 的具名反例要求 `Unavailable` 且 run/queue 精确字节不变；合法本 run identity、scope 与 handoff 的正向用例正常完成 Stop。重启恢复先验证持久 `Interrupted` 和 revision 递增，再显式 Resume/repark 保留原 binding 与 queue generation/HWM 并刷新 Context；same-process repark 也比较旧/新 Context revision。R7 当前定向覆盖 49 个用例。六次具名突变实验覆盖上述身份、状态持久化与 revision/Context 断言，其中 M3/M5 共享同一源码 mutant，因此为五种不同源码变体。
- R1 早期反向突变为 23 项；R5 的 5 项摘要见 `_workflow/sb21-4/review/r5-repair/reverse-mutations-r5-final/summary.json`；R6 五项与 R7 六次新增突变实验均逐项 build 成功、仅目标命名断言变红、源字节/SHA 精确恢复且恢复构建/测试通过，分别见 `_workflow/sb21-4/review/r6-repair/mutations/` 与 `_workflow/sb21-4/review/r7-repair/mutations/`。R7 M3/M5 是两个断言实验、一个唯一源码 mutant；合计 R7 五种源码 mutant。R1 历史 execution source 哈希只沿用各自原台账记录；本轮不从当前源哈希反推或重绑。早期无效/重叠及本地预检失败尝试保留并明确排除。

## BO-13 R8 最终复审与原级验收

GPT R8 对原始 R1 #1–#5 及登记的 R3/R4 IMPORTANT 扩展均建议在明示有限合同内按原 IMPORTANT 等级关闭，未发现新增 MUST/IMPORTANT。本 Goal 要求完成本批原级验收，故接受该有限裁决；不代表生产运行验收或 Wave3 全面清零。逐项证据、审查边界及报告见 `_workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md`。

R1 #3 最终复读错绑具名测试以注入后的 A/B run 文件和 queue 内容为基线，只证明 Stop 不进一步改写当时可见记录，不声称恢复注入前 A 文件；独立 queue 文件读取 I/O 故障没有单独注入，不宣称由其他读写故障用例代替覆盖。R1 #5 的 run publish failure 具名测试证明 queue tombstone 先落盘，重试终态化 run 且 tombstone 全文件字节不变。R3/R4 扩展限定于展示的 Host/service 组件用例及确定性交错。

修复两项 SHOULD 后，最终 assistant build/ClaimSurface/BO-13/LocalWait/full-suite 与 testId 差集证据见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/`。所有证据仍是助手组件/Host 测试；真实 BGI/User/生产门保持关闭。

## BO-11：Wave3 冻结残项逐项处置

| 原台账项 | 原级别 | 当前实现/组件证据 | SB21-4 处置 | 风险与 owner 后续验收 |
|---|---|---|---|---|
| **BO-6** 锚前停驻与已完成出现的真实冲突；来源 R19 必改-1，并有 R21 重要级“普通链尾可被聚合为成功”同族发现 | **必改**（R19）；关联**重要**（R21 F4） | `RecomputeSuccessor` 检出有效停驻与已完成锚冲突后记日志并返回 `null`；源注释仍明确称由 BO-6 承担。当前 `DriveAsync` 的 `hadBadOutcome` 不把 `waitLocally` 计为坏结果，不能证明冲突是显式失败态。现有重定位/锚帮助器用例只断言返回点或 null。 | **未闭合，原级交 owner；生产门继续关闭。**本批没有把 helper/null 解释成失败闭环，也没有改驱动推进语义。 | 选择并实现完成过滤推进或显式失败状态；用真实 Runner/loop 驱动在救援返回后推进 N 步，证明已完成节点零重复提交、停驻义务不丢、冲突不会聚合为 `Succeeded`/触发收尾；关键断言反向突变并做助手回归。 |
| **BO-7** rescue/candidate 计划全序及多个有效停驻点逐项裁决；来源 R21 F1/F2 | **必改 F1、必改 F2**（原台账） | 现行 `RecomputeSuccessor` 组件代码已比较 `(LoopIteration, SequenceIndex)`、取 rescue/candidate 较早者并遍历有效停驻点；`LocalWaitIdentityTranslationTests` 有跨 loop 排序及早期 unsafe parking 用例。它们没有驱动救援后续推进，且 BO-7 原文把完整闭合依赖于 BO-6。 | **组件部分已存在；因 BO-6 及端到端闭环未完成，两个原必改均仍开并交 owner。**不把历史/组件证据重记为本批完成。 | 在 BO-6 验收驱动路径中同时构造 loop candidate/rescue 竞争、多个有效停驻、删除后同身份插回等连续修订；证明全序决定不漏 candidate、不吞任一有效停驻，也不越过已完成出现；每个反例命名并反向突变。 |
| **BO-8** 返回恢复点后的线性推进无完成过滤；来源 R29 | **重要** | `WorkflowRunner` 当前仍显式记录该缺口：`RecomputeSuccessor` 只过滤锚之后的返回点；返回点之后的 `DriveAsync/Relocate` 可能再次提交已完成节点。`RecomputeSuccessor_ReturnsFirstIncompleteAfterAnchor_Bo8BehaviorPin` 只钉死 helper 返回 `X`，不证明 Runner 后续不会重做 `n3`。 | **未闭合，原重要级交 owner。**本批只保留证据和验收，不改 `DriveAsync/Relocate`。 | 用真实 runner 的已完成 history `[n3,n2]` 与新链 `[n2,X,n3,Y]` 执行恢复，证明 `n3` 的提交/副作用不重复，游标和 outcome 正确推进；执行反向突变并跑适用助手回归。 |
| **BO-9** 多有效停驻跨轮次/锚前后时，救援返回后的推进语义；来源 R34 重要-F5 | **重要** | `ParkedRescue` 当前按 loop/sequence 全序选择最早有效停驻；现有 loop 组件测试覆盖 tailBound 与 candidate/rescue 顺序。没有实际 runner 在返回后经历多轮推进的闭环。 | **未闭合，原重要级交 owner。**不把全序 helper 证据扩大为跨轮次运行结论。 | 用 loop 计划和至少两个有效停驻点构造跨轮次锚前/后组合，真实恢复并推进所有义务；证明无跳步、重复已完成出现或错误链尾成功，并做定向反向突变。 |
| **BO-11** owner 冻结打包：R44 未修部分、C12 名义缺口、BO-8 | owner 冻结的打包义务 | 其 C12→BO-4 项已由 SB21-3 依据 owner “历史修复＋本批验证”裁决闭合；SB21-3 只为映射/组件识别证据，没有开生产 successor 门。BO-8 如上仍重要未闭合；R44 宿主停驻出口由本批 BO-13 具名 Host 测试补证。 | **本批完成逐项登记与依赖说明；不代表 Wave3 所有残项清零。** | BO-6/7/8/9 各自继续保留原级和验收条件；owner 决定是否授权将来施工。SB21-3 BO-4 不重开；不启动后续批次。 |

### BO-11 证据边界

- 只读原始来源：`C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch20.json` 的 BO-6/7/8/9/11/13 与 rounds 19/21/29/34/47；同步核对开工时真实 R5.3 §24.120.4、§24.121、§24.122、§24.123 和 `_batch21/b21_plan.md`。
- BO-6 的源注释在 `WorkflowRunner.RecomputeSuccessor` / `ParkedRescue` 明示“链尾保守裁决归 BO-6”；BO-8 源注释明示“返回点之后推进无完成过滤”；BO-7/9 的现有证据是组件/排序级。不得把注释、helper 或纯计划表写成 end-to-end 闭合。
- C12/BO-4 只引用 SB21-3 已完成记录：历史修复提交 `47571736f27bfbc1a20e4d7bb67ce6db967cc0b5`；owner 接受提交证据 `d4b406a176a7dad151a9d3ea4382115c07324b98` 与最终收口 `5e7e7e22f11daad0c86795368e9a21bb14d79b19`。本批不改 BO-4 等级或归属。
- 所有证据均为源代码、单元/组件测试和 Host 测试接缝；没有真实 BGI 实机、生产 User 或生产入口运行。


## prior-validation: _workflow/sb21-4/review/r7-review-20260928-v1/validation-summary.md L1-L11 SHA256=76ec4076a6009fd4b22d35d2caf788e68af03c3998f50d158e93373f772e680e

# R7 最终构建、回归与 testId 证据

- 分支 `main-OldTeaBag-B168`；开工 HEAD `afe84f22d0ebc49d7481bb0adf58f2ea6e29ec5b`。本批 source 与回归数据均位于明细清单和 evidence audit。
- 助手项目：非增量 build，`--no-restore --no-incremental -p:DeployToBgiTools=false -p:UseSharedCompilation=false`，退出 0，58 warnings / 0 errors。
- 助手测试项目：相同部署禁用条件，退出 0，79 warnings / 0 errors。
- 构建后 BO-13 定向：49/49 passed，0 failed；LocalWait：240/240 passed，0 failed。
- 构建后助手全量：1558 passed / 2 skipped / 0 failed / 1560 total。两个 skip 是既有 opt-in P50 项；未在本轮移除或筛掉测试。
- R6→R7：1558 shared / 2 added / 0 removed / 0 changed；开工全量基线→R7：1511 shared / 49 added / 0 removed / 0 changed。Added 的两个具名用例分别为有效 StartupHandoff run identity 正例，以及借用其他 run identity 时 `Unavailable` 且 run/queue 字节不变的负例。逐项 ID 与名称在相邻 diff 文件中。
- ClaimSurface：`CLAIM_SURFACE_REGENERATE=1` regen guard 成功；清除此变量后 no-env guard 再通过；两次生成清单 SHA 相同（`466ba5573a9986618e568ebd514f79d55899e596c7c1e46e631e178b65a66982`）。
- 六项 R7 反向突变均含独立 baseline/build/mutant/restored 日志和 TRX；mutant build 成功，具名 testId 在预期 assertion 失败；restore build/test 成功，源 SHA 精确恢复。首个换行预检拦截尝试已留档但不计有效 mutation。
- 上述皆为源代码、组件和助手测试证据，不是 BGI/真实 User/生产运行。所有生产与真实运行门继续关闭。


## prior-mutations: _workflow/sb21-4/review/r7-review-20260928-v1/mutations-r7.json L1-L80 SHA256=368094189abc9c2fbff6c7a25cbd8a4e0d650a828980fb5320407f8f6679025e

[
  {
    "id": "r7-m1-startup-source-identity-v2",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs",
    "original_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
    "mutant_sha256": "222ab40a8e00b34a5d2bd6b70f51679a68054821d553f6a0dfead605538becfb",
    "restored_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
    "baseline_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m1-startup-source-identity-v2/baseline/baseline.trx",
    "mutant_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m1-startup-source-identity-v2/mutant/mutant.trx",
    "restored_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m1-startup-source-identity-v2/restored/restored.trx",
    "mutant_failure": "Assert.Equal() Failure: Values differ\r\nExpected: Unavailable\r\nActual:   Effective",
    "expected_assertion_in_stack": "LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes",
    "all_build_and_restore_exits_zero": true
  },
  {
    "id": "r7-m2-recovery-persisted-transition",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs",
    "original_sha256": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
    "mutant_sha256": "3d0a93440bd0a324539ed046960ad277dfd44021e489aa2cf62406dbf3d583c3",
    "restored_sha256": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
    "baseline_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m2-recovery-persisted-transition/baseline/baseline.trx",
    "mutant_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m2-recovery-persisted-transition/mutant/mutant.trx",
    "restored_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m2-recovery-persisted-transition/restored/restored.trx",
    "mutant_failure": "Assert.Equal() Failure: Values differ\r\nExpected: Interrupted\r\nActual:   LocalWaitParking",
    "expected_assertion_in_stack": "RestartRecoveryThenResumeReparksRetainsQueueGenerationAndAllowsExplicitStop",
    "all_build_and_restore_exits_zero": true
  },
  {
    "id": "r7-m3-repark-refreshes-context",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "original_sha256": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
    "mutant_sha256": "5e55a05ccc0e7801aeeef4db762610a69f44241b92226fa9a87f44f5b1c33b34",
    "restored_sha256": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
    "baseline_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m3-repark-refreshes-context/baseline/baseline.trx",
    "mutant_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m3-repark-refreshes-context/mutant/mutant.trx",
    "restored_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m3-repark-refreshes-context/restored/restored.trx",
    "mutant_failure": "explicit Resume must refresh the decision snapshot while retaining the prior immutable binding",
    "expected_assertion_in_stack": "RestartRecoveryThenResumeReparksRetainsQueueGenerationAndAllowsExplicitStop",
    "all_build_and_restore_exits_zero": true
  },
  {
    "id": "r7-m4-recovery-revision-advancement",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs",
    "original_sha256": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
    "mutant_sha256": "c50d41c8d6e55c978ae53a6b1accbdfa26f12a38c8ee97eb74f63bf967c44384",
    "restored_sha256": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
    "baseline_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m4-recovery-revision-advancement/baseline/baseline.trx",
    "mutant_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m4-recovery-revision-advancement/mutant/mutant.trx",
    "restored_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m4-recovery-revision-advancement/restored/restored.trx",
    "mutant_failure": "startup recovery must persist the LocalWaitParking to Interrupted transition before explicit Resume",
    "expected_assertion_in_stack": "RestartRecoveryThenResumeReparksRetainsQueueGenerationAndAllowsExplicitStop",
    "all_build_and_restore_exits_zero": true
  },
  {
    "id": "r7-m5-same-process-context-refresh",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "original_sha256": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
    "mutant_sha256": "5e55a05ccc0e7801aeeef4db762610a69f44241b92226fa9a87f44f5b1c33b34",
    "restored_sha256": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
    "baseline_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m5-same-process-context-refresh/baseline/baseline.trx",
    "mutant_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m5-same-process-context-refresh/mutant/mutant.trx",
    "restored_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m5-same-process-context-refresh/restored/restored.trx",
    "mutant_failure": "same-process Resume must refresh the decision snapshot while retaining the original queue binding",
    "expected_assertion_in_stack": "ResumeThatReparks_RemainsStoppableAndReleasesSameFlowForNewRun",
    "all_build_and_restore_exits_zero": true
  },
  {
    "id": "r7-m6-startup-source-valid-positive",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs",
    "original_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
    "mutant_sha256": "9ce8f8fcc0b31aa109f528d33fb7895e0abfd7626add818148a9066bce30f6ea",
    "restored_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
    "baseline_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m6-startup-source-valid-positive/baseline/baseline.trx",
    "mutant_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m6-startup-source-valid-positive/mutant/mutant.trx",
    "restored_trx": "_workflow/sb21-4/review/r7-repair/mutations/r7-m6-startup-source-valid-positive/restored/restored.trx",
    "mutant_failure": "Assert.Equal() Failure: Values differ\r\nExpected: Effective\r\nActual:   Unavailable",
    "expected_assertion_in_stack": "LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding",
    "all_build_and_restore_exits_zero": true
  }
]


## current-mutations: _workflow/sb21-4/review/r8-review-20260928-v1/mutations/mutations-r8-v1.json L1-L40 SHA256=d05662b60f6db24be76b640c6cadf838a539e4d57870ec4c117b96364ca0b88b

[
  {
    "id": "r8-m1-final-reread-runid-guard-run2",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs",
    "original_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
    "mutant_sha256": "915be30a876c4b33a736ec0f76613ee547e97e0b3d400882a83d53e72227ac78",
    "restored_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
    "target_test": "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes",
    "target_test_id": "14b12c92-31c4-d84d-5129-de01fac6b561",
    "mutant_failure_message": "Assert.Equal() Failure: Values differ\r\nExpected: Unavailable\r\nActual:   Effective",
    "mutant_failure_stack": "   at MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes() in E:\\Program Files\\better-genshin-impact-LCB\\Test\\MultiplayerHoeingAssistant.UnitTest\\ServiceTests\\TaskCenter\\LocalWaitFinalizationContractTests.cs:line 977\r\n   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)\r\n   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
    "baseline_build_exit": 0,
    "baseline_test_exit": 0,
    "mutant_build_exit": 0,
    "mutant_test_exit": 1,
    "restored_build_exit": 0,
    "restored_test_exit": 0,
    "exact_source_restore": true,
    "evidence_dir": "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard-run2"
  },
  {
    "id": "r8-m2-tombstone-retry-idempotence-run2",
    "source": "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs",
    "original_sha256": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
    "mutant_sha256": "e78790179f479e5ea978d23d169d687ed423aa90cae771b63570bc559820fb24",
    "restored_sha256": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
    "target_test": "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt",
    "target_test_id": "c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72",
    "mutant_failure_message": "Assert.Equal() Failure: Collections differ\r\n                        ↓ (pos 731)\r\nExpected: [···, 49, 46, 51, 56, 56, ···]\r\nActual:   [···, 49, 46, 52, 48, 50, ···]\r\n                        ↑ (pos 731)",
    "mutant_failure_stack": "   at MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt() in E:\\Program Files\\better-genshin-impact-LCB\\Test\\MultiplayerHoeingAssistant.UnitTest\\ServiceTests\\TaskCenter\\LocalWaitFinalizationContractTests.cs:line 1179\r\n   at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments, Signature sig, Boolean isConstructor)\r\n   at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
    "baseline_build_exit": 0,
    "baseline_test_exit": 0,
    "mutant_build_exit": 0,
    "mutant_test_exit": 1,
    "restored_build_exit": 0,
    "restored_test_exit": 0,
    "exact_source_restore": true,
    "evidence_dir": "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m2-tombstone-retry-idempotence-run2"
  }
]


## validation-summary: _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/validation-final-closeout.md L1-L27 SHA256=03d1360fb5f9982c94fd59b1b4aba22231cd6cc6644453490e103efa3a28d9d3

# SB21-4 BO-13 / BO-11 最终验证与收口证据

## 构建

- 助手项目：exit 0，1 warning / 0 errors；`_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-build.log`。
- 助手测试项目：exit 0，1 warning / 0 errors；`_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/test-project-build.log`。
- 两者均为最终交接文件字节下的构建，使用 `-p:DeployToBgiTools=false -p:UseSharedCompilation=false`。

## 回归和声明面

- BO-13 定向：49/49 通过，0 失败；`_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/bo13/bo13.trx`。
- 精确 SB21 LocalWait 集合：240/240 通过，0 失败；`_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/localwait/localwait.trx`。
- 助手全量：1558 passed / 2 skipped / 0 failed / 1560 total；`_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx`。
- ClaimSurface 在 `CLAIM_SURFACE_REGENERATE=1` 下 regen 通过，清除环境变量后 no-env 复跑通过，SHA 稳定为 `bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e`；见 `claim-surface-evidence.json`。
- testId 差集见 `testid-comparisons-closeout-final.md/.json`：开工基线→最终新增49/移除0/变化0；R6→最终新增2/移除0/变化0；R7 和复审前 R8→最终新增/移除/变化均0；精确 LocalWait 前后240项且无差异。

## 会诊、原级处置及突变

- GPT R8（`gpt-6-astra` / medium）为原独立台账第8/8次。R8 建议 R1 #1–#5 及已登记 R3/R4 IMPORTANT 扩展在报告列明的有限合同内按原 IMPORTANT 等级关闭；本 Goal 当前用户指令要求完成原级闭环，故采纳该有限结论。未报新 MUST/IMPORTANT。逐项处置与边界见 `findings-closeout.md` 和 `../gpt-r8-review.md`。
- R8 两项当前源码反向突变均保留独立 baseline/build/mutant/restored 日志和 TRX；mutant 编译成功、在预期断言失败，源码恢复 SHA 与原 SHA 完全一致，恢复测试通过。见 `../mutations/mutations-r8-v1.json` 与各独立目录。
- 两项 SHOULD 文档问题已修正：R7 M3/M4 编号与 mutation ledger 一致；handoff/R5.3 的时点、最终证据、开工 HEAD 和快照记录方式已同步。首次 DocsFixtureReferenceGuard 失败与最终修复结果均保留；宽筛选284项运行仅为补充，精确240项单独验证。

## 并行成果和范围限制

- `parallel-task-status-closeout.json` 对齐当前 registry 报告、文件 SHA、提交及 Codex turn 快照；`_workflow/sb21-4/deliveries/final-closeout-r4.json` 扫描 exit 0、queue 12、unregistered 0、errors 0。notLoaded/身份不唯一不被推断为完成。无成果提前集成。
- R8 边界：错误 RunId 最终读取测试的字节基线是注入后的 A/B/queue，未证明恢复注入前 A；未单独注入 queue 文件读取 I/O 故障；恢复重启是重建 Host 后显式 Stop，不是启动自动扫描；并发只到同 Host/service 确定交错。
- 以上是源码、构建和助手组件/单元测试，不是 BGI 生产进程、真实 User、实机、R5.8、E3/E4/E5 或热键验证。所有生产门继续关闭。BO-6/7 未启动；BO-8/9 保持原级冻结。


## testid-diff: _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-closeout-final.md L1-L17 SHA256=5049ec515737630683721cce69fbd3f49a2cac587a0ccc7518cf8af09a455e67

# SB21-4 修复后 testId 差集（最终源码）

| 比较 | 基线 → 最终 | shared | added | removed | changed | unchanged |
|---|---:|---:|---:|---:|---:|---:|
| opening_to_final | 1511 → 1560 | 1511 | 49 | 0 | 0 | 1511 |
| r6_to_final | 1558 → 1560 | 1558 | 2 | 0 | 0 | 1558 |
| r7_to_final | 1560 → 1560 | 1560 | 0 | 0 | 0 | 1560 |
| r8_pre_to_final | 1560 → 1560 | 1560 | 0 | 0 | 0 | 1560 |
| localwait_exact240_pre_to_final | 240 → 240 | 240 | 0 | 0 | 0 | 240 |

比较由 `tools/mistletoe/workflow.py` 的 `parse_trx`/`compare_trx` 生成。shared 表示共同 testId；unchanged 还要求名称、类、方法和结果一致。此比较不证明测试正文未变。

R6→最终新增的两个测试：
- `ea2159f2-e93b-4a94-f8bd-3fd37b9298c5` `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes`
- `76837a78-5830-d89e-daab-11ea2c45307b` `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding`

开工→最终新增49/移除0/变化0；R6→最终新增2/移除0/变化0；R7 与复审前 R8→最终新增/移除/变化均0；精确 LocalWait 前后均240项且无差异。三个宽筛选的284项补充运行均通过，不替代精确240项单独验证。


## claim-surface: _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-evidence.json L1-L22 SHA256=440234c68f1fe6878f36ed93031a5bb7f50be5400ec3b1c44c5a5036b3ff6fbf

{
  "schema_version": 1,
  "manifest_path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt",
  "manifest_sha256": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
  "manifest_bytes": 219060,
  "regeneration": {
    "environment": "CLAIM_SURFACE_REGENERATE=1",
    "log": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-regen/claim-regen.log",
    "trx": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-regen/claim-regen.trx",
    "passed": true
  },
  "no_environment": {
    "environment": "CLAIM_SURFACE_REGENERATE removed",
    "log": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-noenv/claim-noenv.log",
    "trx": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-noenv/claim-noenv.trx",
    "passed": true,
    "sha256_before": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
    "sha256_after": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e"
  },
  "sha_stable": true,
  "deploy_to_bgi_tools": false
}


## preflight-history: _workflow/sb21-4/review/r8-review-20260928-v1/workflow-preflight-correction.md L1-L3 SHA256=b6ae6fb27aee1d44e28af6b5f4692cef89ee56bf9d6090555577d961e38156c3

# R8 workflow preflight correction

The first local `audit --stage review` attempt was rejected before a snapshot or GPT request was created: the manifest referenced `claim-surface-r8-final-evidence.json` at the review directory root, while the evidence file is under `postreview-regression/`. The path was checked against the current filesystem and corrected in the manifest. No consultation was sent and no ledger round was consumed. The successful evidence audit was run before that manifest edit; it is being regenerated against the corrected manifest before review.

## historical-claim-surface: _workflow/sb21-4/review/r7-review-20260928-v1/claim-surface-evidence-r7.json L1-L9 SHA256=81fa92e023d57ff8e6bce4fc54ff6ff862a918694cb8df8f0c2bdc765b2a76d6

{
  "manifest": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt",
  "sha256_current_after_regen_and_noenv": "466ba5573a9986618e568ebd514f79d55899e596c7c1e46e631e178b65a66982",
  "sha256_reported_after_regen_and_noenv": "466ba5573a9986618e568ebd514f79d55899e596c7c1e46e631e178b65a66982",
  "regenerate_run": "_workflow/sb21-4/review/r7-repair/claim-regen/claim-regen-r7.trx",
  "no_environment_run": "_workflow/sb21-4/review/r7-repair/claim-noenv/claim-noenv-r7.trx",
  "both_runs_passed": true,
  "environment_cleared_before_noenv": true
}


## parallel-delivery-status: _workflow/sb21-4/review/r8-review-20260928-v1/parallel-deliveries-r8.md L1-L4 SHA256=66a98a4141b00c80eebccfe9bf08affadf80b0135570bd21264864b1a5774949

# R8 并行交付核对

- `python -B tools/mistletoe/deliveries.py --root .` 退出码 0；`ok=True`；队列 12；未登记报告 0；错误 0。原始机器输出：`_workflow/sb21-4/deliveries/r8-review-discovery.json`。
- 工具明确不从 Git 推断任务终态；当前扫描的 `task_status` 为 `not inferred from Git; executor must query thread status`。关联候选均按索引保留各自目标消费批次和阻断；SB21-4 不消费、不合并 R5.6、R6.1、R6 diff guard 成果。


## parallel-delivery-status: _workflow/sb21-4/deliveries/r8-review-discovery.json L1-L170 SHA256=365b57ca05a624f0b23d1121d1abe8a586d5498ec2dd10832d15eca677e5fbae

{
  "schema_version": 1,
  "quality_verdict": "NOT PROVIDED",
  "automatic_merge": false,
  "queue": [
    {
      "id": "r56-migration-audit",
      "target_batch": "R5.6 主线迁移集成批",
      "integration_state": "pending",
      "blockers": [
        "真实引用/激活元数据、静止窗口及生产检查点尚未完成；真实 User 门仍关闭"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r56-migration-audit\\better-genshin-impact-LCB\\_r56_parallel\\report.md",
      "head": "c11cb45f6de2436b46c58b91d47f2c9768132ef3",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r61-distribution-candidate",
      "target_batch": "R6.1 正式分发与互用集成批",
      "integration_state": "blocked",
      "blockers": [
        "资源缺失及过滤语义待决；正式分发/运行/实机门关闭"
      ],
      "covered_by": "r61-integration-validation",
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r61-parallel\\better-genshin-impact-LCB\\_r61_parallel\\report.md",
      "head": "28d8c764f5e1ae6ae0fee5a1d99a2e41d107d964",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r61-integration-validation",
      "target_batch": "R6.1 正式分发与互用集成批",
      "integration_state": "blocked",
      "blockers": [
        "缺ScriptGroup/砍树.json",
        "四类脚本目录及12/33路线目录缺失",
        "legacyFiltered及连续计划触发语义待owner裁决",
        "应用加载/保存/互导/真实运行未验证"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r61-integration\\better-genshin-impact-LCB\\_r61_integration\\report.md",
      "head": "111f6abe0c68330b5acdd867a21586071ce4662a",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r62-bgi-guard",
      "target_batch": "R6 diff 收敛/持续守卫集成批",
      "integration_state": "pending",
      "blockers": [
        "尚未加入现有 BGI 单元测试项目；须主线适配及集成回归",
        "仅覆盖12个C#文本标识符，不证明整体退出或运行安全；生产门保持关闭"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r62-bgi-guard\\better-genshin-impact-LCB\\_r62_bgi_guard\\report.md",
      "head": "d50d28e80dc550a4d21ed1ac1f9dd1402d69fd1e",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r62-contact-audit",
      "target_batch": "R6 接点/diff 收敛安全窗口",
      "integration_state": "pending",
      "blockers": [
        "F13：五个地脉结束检测字段及复制/导出映射缺少已确认接收处，须主线逐字段证明映射或形成正式迁移/退役裁决并补定向回归",
        "F08-F10、F12、F16、F18-F23、F27-F28 的运行级行为尚未验证",
        "候选仅为固定提交的离线差异清单和工具，不授权产品接收、删除/退役、集成或生产门开放"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r6-contact-audit\\better-genshin-impact-LCB\\_r6_contact_audit\\report.md",
      "head": "8ee3f9df38991fabf49e945b8382128516a77b20",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r5-prepared-process-cross-process-2026-09-28",
      "target_batch": "R5 D18/D26 受理账/存储验证安全批次",
      "integration_state": "pending",
      "blockers": [
        "主线适配、主线回归、BGI/助手/服务端运行、断电耐久均未验证；该交付只含隔离 Prepared 组件证据。",
        "D18/D26 统一受理事务、连续物理槽、退出证明和 R5.8 实机门继续关闭。"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r5-prepared-process\\better-genshin-impact-LCB\\_r5_prepared_process\\report.md",
      "head": "912efbeb9b74f8ecad01b1456844227f8ff70228",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r5-slot-process-cross-process-2026-09-28",
      "target_batch": "D14 物理槽底座/生产槽集成安全批",
      "integration_state": "pending",
      "blockers": [
        "D14 消费批尚未完成主线适配、主线回归及产品入口验收；此独立交付不授权集成或生产运行。",
        "BGI/助手/服务端运行、真实 User、R5.8 与断电耐久仍未验证；生产门保持关闭。"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r5-slot-process\\better-genshin-impact-LCB\\_r5_slot_process\\report.md",
      "head": "44576c63d9e45b545943a8c1edef1ee715890c8c",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "parallel-delivery-review-requirements-2026-09-28",
      "target_batch": "各既有候选的计划消费批（仅登记审查要求；非产品功能交付）",
      "integration_state": "pending",
      "blockers": [
        "该报告仅评估既有并行候选的接收前复核必要性，不是产品实现、完整交付复验或主线验收。其源 Codex thread 身份/终态未从可用线程快照唯一映射；提交和文件已核实，采取 pending，不据 Git 推断任务终态。",
        "Q-R61-ORDER、Q-R61-DEPENDENCY 与 R6.2 F13 的审查要求须在各自目标批次重新绑定并处理；R5 slot 首次最终交付复核须等源任务 closeout 后。不得提前消费、合并或开放生产门。"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\parallel-recovery\\better-genshin-impact-LCB\\_r5_parallel_review\\report.md",
      "head": "6d23092b98833bd580e6688ab2d5107379ae4cbb",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r56-activation-prep",
      "target_batch": "R5.6 主线迁移集成批",
      "integration_state": "pending",
      "blockers": [
        "A-F real integration/activation/production consumer checks remain pending; no production, real User or R5.8 acceptance.",
        "Worktree currently contains 11 untracked evidence files; tracked product source is unchanged."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r56-activation-prep\\better-genshin-impact-LCB\\_r56_activation_prep\\report.md",
      "head": "90588159b4769c41284ade475052d8b56f92e2d6",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r57-identity-order",
      "target_batch": "R5.7 正式旧配置身份/顺序兼容消费批",
      "integration_state": "blocked",
      "blockers": [
        "IMPORTANT verification gap: missing/empty TaskDefinitions can rebuild task GUIDs and leave NextTaskId dangling after first save.",
        "Duplicate TaskOrder ID contract remains unresolved.",
        "No production code change or real User access; not integrated."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r57-identity-order\\better-genshin-impact-LCB\\_r57_identity_order\\report.md",
      "head": "2e66f4a2cf98b395be7586550b6d0b13b07618a9",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r57-retirement-prep-2026-09-28",
      "target_batch": "R5.7 正式旧链路退役实现复核与并存防护验收",
      "integration_state": "pending",
      "blockers": [
        "No conclusion that the old scheduler is fully absent or coexistence/no-dual-run is safe.",
        "Important compatibility identity/order case remains unverified.",
        "R5.6 integration, R5.7/R5.8 and §23.4 gates remain open."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r57-retirement-prep\\better-genshin-impact-LCB\\_r57_retirement_prep\\report.md",
      "head": "051852fdfe805fd1e0b807bae23300c923e5fb42",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r58-acceptance-prep",
      "target_batch": "R5.8 集成验收准备/收口批",
      "integration_state": "pending",
      "blockers": [
        "Product acceptance not signed; protocol, real entry, hardware, R5.8 and production gates remain open.",
        "No unique thread identity/status was available in current snapshot."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r58-acceptance-prep\\better-genshin-impact-LCB\\_r58_acceptance_prep\\report.md",
      "head": "4c1316df1765756d49740c62fdd14c14cbd32e09",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    }
  ],
  "unregistered_reports": [],
  "errors": [],
  "ok": true,
  "task_status": "not inferred from Git; executor must query thread status"
}


## contract: Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md L4765-L4776 SHA256=bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad
R5.3 §24.120.4/24.120.5 and SB21-4 historical gates/obligation map.
### §24.120.4 残项清单（BO-1~BO-13，归后续批次）

台账 test/ledger-batch20.json batch_obligations 数组：BO-1 登记异常收敛（接线批）、BO-2 面板 scope 注入源（接线批）、BO-3 跨代际重登记通道（Wave2 期间裁决＝冲突即合同信号，已 R35 落字）、BO-4 facade WaitLocally 映射（Wave4/接线批）、BO-5 停驻×孤儿扫描（已闭合）、BO-6 停驻重排冲突保守裁决＋端到端（Wave3 C11）、BO-7 停驻救援全序裁决（Wave3 C11）、BO-8 恢复点后推进段完成过滤（继承缺陷，Wave3 C11）、BO-9 多有效停驻跨轮次推进（Wave3 C11）、BO-10 代际单调性边界（接线批）、BO-11 Wave3 冻结残项打包（接线批）、BO-12 Remove/裁剪→重登记代际回绕（Wave3/接线批 C5）、BO-13 停驻运行会话内终局处置出口（接线批）。

### §24.120.5 门禁与边界

生产入口门、真实 User 门与 R5.8 签署**继续关闭**；停驻语义生产不可达（ShouldRegisterLocalWait 生产恒 null、waitLocally 无生产产出方）。**不援引措辞类豁免**；声明面清单变更随本批提交评审。R44 起新发现按 owner 止损裁决登记台账残项（BO-14 起），未修复项已在 §24.120.4 与台账逐条登记归属。

## §24.121 SB21-1 收口登记——生产等待裁定接线（2026-09-27）

### §24.121.1 授权范围与实现



## current-clause: Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md L4916-L4945 SHA256=bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad
Current §24.124 BO-13 contract, BO-11 original-grade inventory, validation, review state and closed gates.
### §24.124.1 范围与原始等级

本子批只处理 BO-13 会话内 Stop 出口，以及 BO-11 对 BO-6/7/8/9 的逐项冻结登记。BO-13 原等级为 batch20 R47 **重要**；BO-6 原等级为 R19 **必改**、同族 R21 F4 **重要**；BO-7 的 R21 F1、F2 均 **必改**；BO-8 的 R29 与 BO-9 的 R34 F5 均 **重要**。本批不重开 BO-4、BO-10 或 BO-12。原始来源见 `_workflow/sb21-4/raw-bo-obligations.json` 和独立机器台账；状态与交错矩阵见 `_workflow/sb21-4/scope-and-state-table.md`。

### §24.124.2 停驻运行终局状态与队列绑定

仅对已持久化 `LocalWaitParking` 记录、明确 NoSend、完整 Wait/Hold 裁定身份、无同流程 drive/reservation 且无未决外部发送/收尾事实及前置动作责任的 run 接受同步 Stop。前置动作 enum 为 `Intent/Submitted/Succeeded/Failed/Cancelled/Unknown`：仅 `Succeeded/Failed/Cancelled` 是允许收敛的已知终态；`Intent/Submitted/Unknown` 和未识别数值状态均保持未决。终态记录上残留的 `SendAttempted` 或 `JobId` 不单独阻止 Stop，未决状态则无论这些旁置字段为何都拒绝。该护栏为 Stop 专用，不改变恢复扫描原谓词。入口与 gate 内复读都验证请求 runId 与记录体 runId；读取异常返回 `Unavailable`。Wait 清理按 `LocalWaitBinding` 全部队列可见不可变载荷作锁内比较；只把精确匹配项写为 `Cancelled` 墓碑，不降低 generation/high-water、不改无关项。身份漂移、队列格式损坏及已具名的 run 读取或 queue/run 持久化故障路径返回不可用并保留可重试状态；独立 queue 文件读取 I/O 异常未单独注入，不作为等同已验证分支。最终复读错绑测试比较错绑注入后的 A/B run 文件与队列字节，证明 Stop 不进一步改写当时可见内容，不声称恢复注入前 A 文件；缺少队列项或已有墓碑不伪造新项；持久 Hold 无 binding 时只终态化 run。

Wait 决策必须绑定当前 run/workflow/revision、cursor snapshot、同 key 的 `LocalWaitDeferred` submission、完整 context/binding 与共享 identity factory 的规范候选身份。合法修订顺序是 `0 < binding.RecordRevision <= context.RecordRevision <= run.RecordRevision`：恢复复核可以更新 Context 并保留不可变旧 binding，不要求两者相等，也不允许 Context 早于 binding。来源身份按类别校验：StartupHandoff 的权威身份在同一运行台账中，Stop 要求 `SourceIdentity == run.RunId`、存在 Start/ArmTrigger 受理绑定且 `AdmissionSourceScope` 与 binding scope 一致；PanelFlowRegistration 的权威身份是唯一流程登记父操作，Context 与 binding 保存该 RequestIdentity 的配对快照，Stop 检查两者相等，但不把快照当作发送授权。终态化保留 run、cursor occurrence/loop/attempt、submission key、旧 outcomes、裁定和 binding；不调用 flow 收尾或发送；在 gate 外回写 admission registration。队列墓碑先落盘而 run 终态写入失败时，返回可观察重试结果；后续 Stop 识别墓碑并完成 run 终态化而不重复改队列。`Cancelled` 留在历史并释放 `ActiveStates`；同流程恢复后再次停驻可 Stop，Stop 后新 Start 使用新 RunId。

### §24.124.3 实现、反例与关键断言判别

本批改动集中于 `TaskCenterHost.RequestRunAction/StopParkedRun/HasValidParkedDecision`、`TaskCenterHost.Admission` 终局回写、RunStore 和 Host 测试接缝。R6 复审前具名反例覆盖 throwing logger 不阻断 sibling 回写、非正/未来 binding 与 context revision 拒绝且保留 run/queue 原字节、重启恢复后再停驻沿用旧 binding 与 queue generation/HWM、新流程 RunId，以及两个 reconciliation worker 均完成后只发生一次终局 revision 迁移并隔离另一 run。R7 新增 StartupHandoff 借用其他 run ID 的反例与合法 run ID 正例，以及重启恢复态先持久迁移并推进 revision、显式 Resume 后 Context 刷新且 binding/generation/HWM 保留的断言。错借身份夹具在 R6 源码上以 `Expected Unavailable, Actual Effective` 失败，R7 修复后通过；具名证据见 `_workflow/sb21-4/review/r7-repair/identity-red-current-v2/` 与 `source-guard-final-target/`。

R5/R1 历史突变保留原执行证据，不在本轮重绑；R6 五项有效突变与 R7 六次具名反向突变实验均以成功 baseline/build 为前提，mutant 构建成功且具名测试在指定目标断言失败，恢复后源码 SHA 与原字节精确一致并复跑通过。R7 M1–M6 分别命中 StartupHandoff 来源身份拒绝、恢复状态落盘、重启恢复后 Resume/repark 刷新 Context、恢复 revision 递增、同进程 repark 刷新 Context、合法 StartupHandoff run identity 保持放行；M3/M5 是不同测试断言实验但共享同一源码 mutant，六次实验对应五种源码 mutant。每项 baseline/build/mutant/restored 独立日志/TRX、命名失败、源码原始/恢复 SHA 和 mutant SHA 见 `_workflow/sb21-4/review/r7-repair/mutations/`；第一版 M1 只因换行预检未发送修改，单独保留且不计有效突变。R6 M1–M5 的 TryLog、revision 与并发完成 join 证据见 `_workflow/sb21-4/review/r6-repair/mutations/`。R1/R5 历史哈希只沿用当时记录，未知执行哈希继续未知；无效/重叠及本地未发送预检尝试保留并排除。

并发回写依据也在范围内复核：Host 每实例互斥只串行化该实例的任务；`MarkOperationTerminal` 通过 `MutateHandoffLatest` 在跨进程文件锁中读取最新租约，并在锁内再次要求 request 仍为 `Accepted` 后以同一 revision 发布终态。确定性 Barrier 夹具证明重叠 timeout/retry 只有一次有效转移、重复 Stop 不再写、同一 run 操作的 `UpdatedRevision` 对应发布 revision、另一 run 的 operation JSON 字节不变。该证据限于助手组件内线程交错，不证明跨进程压力、断电耐久或产品进程运行。

### §24.124.4 BO-11 Wave3 残项冻结处置

BO-6 的现行 helper 在停驻与已完成锚点冲突时返回 `null` 并记录诊断，但 Runner 的 `hadBadOutcome` 不把 `waitLocally` 计作坏结果；真实 Runner 冲突失败/成功聚合语义仍未证明。R19 必改与 R21 F4 重要均原级交 owner。BO-7 已有 `(LoopIteration, SequenceIndex)` 全序与多有效停驻遍历组件用例，但没有救援后的真实 Runner 推进闭环；R21 F1/F2 必改均原级交 owner，且 BO-6/7 本批未启动。BO-8 源码仍承认恢复点之后推进没有完成过滤，R29 重要未闭合。BO-9 只有 helper/loop 全序组件证据，没有 Runner 跨轮次恢复推进证明，R34 F5 重要未闭合。逐项证据、风险、尝试和验收条件见状态表；方案、注释或组件测试不算端到端闭合。BO-11 在本批完成的是冻结残项逐项打包登记，不表示 Wave3 清零。C12→BO-4 已由 SB21-3 按 owner 接受的“历史修复＋本批验证”路径收口，本条不重开。

### §24.124.5 验证、会诊与门禁

R8 最终文档修订后的助手主项目与测试项目均使用 `-p:DeployToBgiTools=false` 非增量构建通过（分别 58/79 warnings、0 errors）；ClaimSurface regen/no-env 均通过，最终 SHA 与 TRX 见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-evidence.json`。BO-13 定向 49/49、LocalWait 240/240、助手全量 1558 passed / 2 skipped / 0 failed / 1560 total；具名 TRX、完整 TRX、build 日志及差集见同目录。R6→最终全量 testId shared 1558、added 2、removed 0、changed 0、unchanged 1558；开工→最终 shared 1511、added 49、removed 0、changed 0、unchanged 1511；R7→最终 shared 1560、added 0、removed 0、changed 0、unchanged 1560。两个新增 ID 是 StartupHandoff 本 run identity 正例及借用另一 RunId 时 Unavailable/字节保持反例。首次助手全量文档守卫失败及其修正记录仍保留；最终回归通过。上述为源码/助手组件/Host 测试证据，不代表生产实机运行。

GPT R8（gpt-6-astra / medium，独立台账累计 8/8）对 BO-13 原始 R1 #1–#5 及已登记 R3/R4 IMPORTANT 扩展均建议在明示有限合同内按原 IMPORTANT 等级关闭，未发现新增 MUST/IMPORTANT。依本 Goal 的 owner 指令，本批接受这些有限范围原级裁决；不将其扩大为生产验收或 Wave3 清零。R8 报告及逐项证据边界见 `_workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md`。R8 指出的两项 SHOULD 已修正：R7 M3/M4 映射与实际 mutations-r7.json 对齐；交接/R5.3 当前状态与 R8 最终验证同步。R8 是预算最后一轮，不再增加会诊。BO-6/7 未启动；BO-8/9 仍按原级冻结登记。

最终声明面清单在上述文档修正后以 `CLAIM_SURFACE_REGENERATE=1` 再生，再清除环境变量复跑；两次均通过且 SHA 稳定，确切 SHA 与独立 TRX 见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-evidence.json`。R8 两项目构建和定向/助手全量结果见同目录。未验证 BGI 生产进程、真实 User、R5.8、E3/E4/E5、热键面或实机；所有生产入口及运行门保持关闭。
生产入口门、真实 User 门与 R5.8 签署继续关闭；停驻语义生产不可达（`ShouldRegisterLocalWait` 生产恒 null，`waitLocally` 无生产产出方）。不援引措辞类豁免；声明面清单变更随本批提交评审。R44 起新发现按 owner 止损裁决登记台账残项（BO-14 起），未修复项已在 §24.120.4 与台账逐条登记归属。E3/E4/E5、热键面及 BGI 生产进程也保持关闭。以上均为源码、助手构建和组件/Host 测试接缝证据，不是 BGI 实机、真实 facade 调度或生产 User 验证；BO-6/7/8/9 的重要/必改风险尚未排除，不据此打开推进或收尾生产门。


## plan: _batch21/b21_plan.md L25-L33 SHA256=498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb
SB21-4 current R8 state, frozen BO-11 items and boundaries; BO-6/7 not authorized here.
- **SB21-1 等待判定接线（收口完成）**：TaskCenterHost 装配 `LocalWaitQueueStore`＋`WaitDecisionSource` 并由 `CreateRunner` 注入；类型化 pre-intent Wait/Hold/Continue、身份绑定与队列/运行恢复规则按 owner 有限扩展实现。BO-1、BO-2、EV1-R1 与批次 14 残项 #1 有本批代码/测试证据。owner 按 R5.3 §24.121.4 依据本地可复核材料收口：R9 两项必改证据缺口已机械补齐，原等级保留；不声称顾问接受后补证，也不追加 SB21-1 会诊。R16 历史会诊缺失继续作为 §24.63 U 的后续处理债务。现有生产构造的后继门 `_successorAdmissionWired` 仍关闭，未开放等待消费路径或真实 User/R5.8 最终入口。
- **SB21-2 代际边界（实现、R5 复核与恢复态回归完成）**：只含 BO-10（Remove 后重登的 HWM 单调边界）和 BO-12（Remove/裁剪后不降 HWM、legacy int 隔离、long 回绕/ABA、C5 Store 消费前复核）。GPT 会诊独立台账累计 5/8，R5 未发现新 MUST/IMPORTANT，并认可 R4-1/2 重要级证据闭环；另 2 次本地预检未发送、不计次。助手项目和测试项目分别非增量构建 0 错误/58 警告、0 错误/79 警告；恢复态 generation 定向 45/45、LocalWait 190/190、助手全量 1506/2/0/1508。Remove/prune 重登使用同一重开 Store 读回持久状态，C5 旧/新请求消费计数为过期 1、有效 1；组件无 sender 依赖、无生产调用点，本批发送数为 0，生产门继续关闭。主矩阵 10 个保护点、legacy 强化、R5 C5 代际比较及 long-generation `long.TryParse` 边界均由命名断言反向突变检出并精确恢复。最新 testId 差集：共享 1497、移除 1、新增 11、变化 17、不变 1480，见 `sb21-2-review/assistant-full-test-diff-r5-counter.md/.json`。R5 后复验及声明面最终守卫证据见 R5.3 §24.122 与 `_batch21/sb21-2-review/`。生产入口、真实 User、R5.8 签署、E3/E4/E5 与热键面继续关闭；未做实机验证。
- **SB21-3 facade 映射（BO-4；owner 按历史修复＋本批验证裁决收口）**：仅本批 BO-4。历史错误映射由提交 47571736f27bfbc1a20e4d7bb67ce6db967cc0b5 改为 WaitWith；历史 4/4 wiring 测试不直接证明 mapper。本批没有生产源码修复；v15 文档定稿后的直接映射与消费验证 14/14、助手全量 1509/2/0/1511、testId shared1508/removed0/added3/changed0。owner 明确接受历史修复＋当前验证，IMPORTANT #1 保持原等级并关闭验收项，不将历史修复记为 SB21-3 生产修复。IMPORTANT #2 经 GPT round4 接受为组件范围证据闭合。round4 SHOULD 已修正 E1/E2 与 successor 节点门表述；会诊累计 4/8，3 次本地预检未发送，不计次。生产入口/真实 User/R5.8/E3/E4/E5/热键与 BO-4 successor 门继续关闭，未做实机验证。BO-10/12 不重开；SB21-4 的 BO-6/8/11/13 按原范围保留。
- **SB21-4 停驻出口**：BO-13（停驻运行会话内终局处置出口）＋BO-11（Wave3 冻结残项打包：BO-6/7/8/9 处置或显式登记）。
- **SB21-4 BO-13 / BO-11（R8 复审与收口）**：BO-13 的 R1 #1–#5 及 R3/R4 登记的重要扩展经 GPT `gpt-6-astra` / medium 最终复审，均建议按明示有限合同在原 IMPORTANT 等级内关闭，没有新增 MUST/IMPORTANT；R8 使用独立台账第 8/8 次且未重置。R8 指出的 M3/M4 mutation 映射和旧时点交接文字已修正。复审前的最终验证：两项目构建 exit 0（58/79 warnings，DeployToBgiTools=false），BO-13 49/49、LocalWait 240/240、助手全量 1558/2/0/1560；最终文档修订后的修复回归、ClaimSurface 和 testId 证据见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/`。BO-11 按原级逐项登记并冻结：BO-6 R19 MUST/R21 F4 IMPORTANT、BO-7 R21 F1/F2 MUST、BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT；BO-6/7 未启动，组件/方案不冒充端到端闭合。SB21-3 BO-4 按已接受路径不重开；SB21-2 BO-10/12 不重开。生产门关闭，R5.6、R6.1、R6 diff guard 不提前集成。
- 横向：EV1-R1（RunStore.List 静默跳过修复）随 SB21-1 同批（占用者级别事实源的同族面）。

## 开工纪律
反例先行（每子批先红夹具）；会诊刹车（≤8 轮、严重度地板）；提交 --only；声明面变更再生＋无 env 复跑；每子批收尾刷新接力文件。


## handoff: _batch21/sb21-4-handoff-2026-09-28.md L1-L78 SHA256=b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a
Complete current SB21-4 status, state contract, evidence, R8 budget, BO-11 scope and future prompt explicitly not started.
# SB21-4 交接与验收记录

> 状态：SB21-4 BO-13 的五项 R1 IMPORTANT 与已登记 R3/R4 IMPORTANT 扩展已按 R8 建议和本 Goal 验收指令，在明示有限合同内保持原级关闭。R8 GPT gpt-6-astra/medium 为独立台账第 8/8 次；无新增 MUST/IMPORTANT。R8 提出的两项 SHOULD 文档问题已修正并完成修复后回归。BO-11 仅完成冻结残项打包；BO-6/7 未启动，BO-8/9 按原级交 owner。BO-4、BO-10/12 不重开，所有生产门继续关闭。

## 本批目标与边界

- BO-13：允许已停驻、NoSend 且身份/责任都可核验的运行在会话内显式 Stop；精确清理自身等待绑定、持久拒登/ Hold 收敛到 `Cancelled`；不执行流程收尾、不发送。
- BO-11：按原台账分别处置 BO-6/7/8/9。SB21-4 仅冻结登记，未把方案、注释或 helper/排序组件证据算作端到端闭合。BO-6 R19 必改及 R21 F4 重要、BO-7 R21 F1/F2 必改、BO-8 R29 重要、BO-9 R34 F5 重要均原级交 owner。
- BO-4 已按 SB21-3 owner 的历史修复＋当前验证接受路径完成；BO-10/12 属 SB21-2。本批不重开以上义务。
- 生产入口、真实 User、R5.8、E3/E4/E5、热键及 BGI 生产进程继续关闭；无实机或生产 User 验证。

## BO-13 状态合同与实现

可逐状态核对的转移矩阵、字段保留和拒绝条件见 [`_workflow/sb21-4/scope-and-state-table.md`](../_workflow/sb21-4/scope-and-state-table.md)。当前实现的关键规则：

1. Stop 入口与 `_gate` 内复读都比较请求 runId 和记录体 runId；读坏记录或身份不符时返回 `Unavailable`，不改 run/queue 字节。
2. 仅 `LocalWaitParking`、明确 NoSend、当前有效 Wait/Hold 决策、无 drive/reservation、无未决外部事实和前置动作责任才可终态化。前置动作状态枚举为 `Intent/Submitted/Succeeded/Failed/Cancelled/Unknown`；仅 `Succeeded/Failed/Cancelled` 三个已知终态可放行，`Intent/Submitted/Unknown` 及未识别数值状态均由 Stop 专用护栏拦截。已终态记录上残留的 `SendAttempted` 或 `jobId` 本身不阻止 Stop；不改变恢复扫描原谓词。
3. Wait 绑定当前 run/workflow/revision、cursor 及 decision cursor snapshot，当前 submission 必须是同派生 key 的 `LocalWaitDeferred` 且无 send attempt/job/accepted identity/terminal。Context、binding、排序/来源事实与共享身份翻译必须一致；候选/准入身份由共享 factory 复算。修订关系为 `0 < binding.RecordRevision <= context.RecordRevision <= run.RecordRevision`。StartupHandoff 的来源 ID 必须等于 runId，并由本 run 的受理 scope/handoff 记录佐证；PanelFlowRegistration 的 RequestIdentity 来自唯一父登记，Stop 只核对 Context/binding 配对快照、不将其当发送许可。Hold 必须无 binding。
4. Wait 仅按完整 `LocalWaitBinding` 在队列锁内比较并写精确墓碑；generation/HWM 和无关项不变。缺项/已有墓碑可收敛；载荷漂移、损坏、读写错误均保留 run 并拒绝误清理。先 queue tombstone 后 run `Cancelled`；run 写失败后重试识别已有墓碑、不重复写队列。
5. 成功后保留 run、workflow revision、cursor occurrence/loop/attempt、submission key、outcomes、decision 和原 binding；不触发发送/收尾；在 gate 外将关联 admission registration 回写到 terminal。Cancelled 历史记录释放 ActiveStates 同流程槽位；恢复后再次停驻可再 Stop，之后新 Start 生成新 run。

源码涉及 `TaskCenterHost.cs`、`LocalWaitQueueStore.cs`；新增具名事实在 `LocalWaitFinalizationContractTests.cs`。

## 验证证据
- SB21-4 原始实现提交为 `afe84f22d0ebc49d7481bb0adf58f2ea6e29ec5b`，分支 `main-OldTeaBag-B168`；R6/R7 的后续生产/测试/文档修订是该提交之后的未提交差异。SB21-3 历史修复/owner 接受路径只作前情，不重开 BO-4。
- 开工 HEAD 下 BO-13 6 项新增反例三项按旧实现预期失败；R1 修复前另有 16 项中 7 项失败。R1 历史具名 Host 事实 17/17、23 个有效突变位于旧证据路径；R6 增加 logger 异常 sibling、三种非法 revision 输入及重启恢复后 repark Fact，BO-13 定向 47/47、LocalWait 回归 238/238。R6 五项有效突变分别覆盖 TryLog、正 revision、future revision、binding/context 次序及双 reconciliation worker completion join；独立 baseline/build/mutant/restored 日志/TRX 和源码 SHA 见 `_workflow/sb21-4/review/r6-repair/mutations/`。R1/R5 历史哈希仅沿用当时记录，不以当前 SHA 回填；无效/重叠及未发送预检尝试继续保留并排除。
- R6 文档同步后，两项目非增量构建分别 0 error（58/79 warning）；Host 47/47、LocalWait 238/238；助手全量 1556 passed / 2 skipped / 0 failed / 1558。R7 修订后再次非增量构建通过（58/79 warnings、0 errors），Host 49/49、LocalWait 240/240、助手全量 1558 passed / 2 skipped / 0 failed / 1560。R6→R7 testId shared1558/added2/removed0/changed0；开工→R7 shared1511/added49/removed0/changed0。两个新增 ID 是 StartupHandoff 本 run identity 正例与借用其他 run identity 的拒绝/字节保持反例。R7 最终日志/TRX 位于 `_workflow/sb21-4/review/r7-review-20260928-v1/postbuild-regression/`。DocsFixtureReferenceGuardTests 的旧失败及修正记录仍保留在 `_workflow/sb21-4/review/r6-review-validation/assistant-full.log`。
- R5 的早期定向与全量首次回归曾复现临时写探针竞争，失败栈在 `SeedParkedRun` fixture queue Upsert，非 BO-13 目标断言；两个测试类已并入 `[Collection("LocalWaitSnapshotProbe")]`。相关失败 TRX/日志保留为 R5 历史，不计为 R6 通过证据。
- R7 ClaimSurface SHA `466ba5573a9986618e568ebd514f79d55899e596c7c1e46e631e178b65a66982` 是历史证据。R8 完整文档修正后再次 regen/no-env，两次通过且 SHA 稳定；修复后证据、当前清单 SHA 与 TRX 位于 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-evidence.json`。构建和测试命令均显式使用 `-p:DeployToBgiTools=false`。
- 当前源码和 Host 接缝测试不是 BGI 实机、真实 facade 调度、生产 User 或助手服务端运行验证。没有打开生产门。
## 会诊义务与状态

- 独立台账：`C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch21-sb21-4.json`。现场机器原账保留 R1–R8 八次累计请求，R2 已发送失败仍计次；R8 已返回且是最后一次，不再新增会诊。Goal 摘要中的“2/8”与原件不符，按实际累计记录 8/8，不重置或覆盖历史轮次。
- GPT R8 已逐项复核 BO-13 原始 R1 #1–#5 与登记的 R3/R4 IMPORTANT 扩展，建议均在明示有限合同内按原 IMPORTANT 等级关闭，未发现新增 MUST/IMPORTANT。本 Goal 要求完成原级验收，故本批接受有限范围裁决；其边界不是生产运行验收。R8 报告 `_workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md` 明确说明：最终读错绑只证明不进一步修改错绑注入后的 A/B/queue；未单独验证 queue 文件读取 I/O 异常；重启证据为重建 Host 后显式 Stop；并发结论限同 Host/service 确定性交错。两项 SHOULD 文档问题已按 R7 mutation ledger 与最终 R8 状态修正。

- **R8 复审前待决清单（历史时点）**：当时 R1 IMPORTANT #1–#5 与 R3/R4 延伸均保持原级开放，#3/#5 等待完整 Host 与测试材料。R8 已在补齐材料下逐项给出有限原级关闭建议；最终验收和边界见本节状态及 R5.3 §24.124。
- R8 于 2026-09-28 返回并计为第 8/8 次。报告对五项 R1 IMPORTANT 和登记的 R3/R4 IMPORTANT 扩展均建议原级有限合同关闭，未留 MUST/IMPORTANT；两项 SHOULD 文档问题已修正。台账不重置、不追加会诊；BO-6/7 不启动，生产门继续关闭。

## BO-11 owner 检查点（Wave3 未清零）

| ID / 原级 | 当前证据和未闭合风险 | owner 后续验收条件 |
|---|---|---|
| BO-6：R19 MUST；关联 R21 F4 IMPORTANT | `RecomputeSuccessor` 在有效停驻与已完成锚冲突时返回 null/log；Runner 的 `hadBadOutcome` 不把 waitLocally 算作失败。未证明冲突不会成为 Succeeded/触发收尾。 | 在真实 Runner/loop 驱动中实现原台账允许的完成过滤推进或显式失败态；救援后前进 N 步，证明已完成出现零重提、停驻义务不丢、冲突不成功/不收尾；命名反例、反向突变及助手回归。 |
| BO-7：R21 F1/F2 MUST | helper 已有 `(LoopIteration, SequenceIndex)` 全序、多有效停驻遍历及组件用例；未驱动端到端推进，原文明确依赖 BO-6。 | 在 BO-6 的 Runner 验收内覆盖 rescue/candidate 竞争、多个有效停驻、删除后同身份插回；证明无漏候选/漏停驻且不越过完成出现；逐项反例和突变。 |
| BO-8：R29 IMPORTANT | 返回点后 `DriveAsync/Relocate` 无完成过滤，修订后可能二次提交已完成节点；目前只钉 helper 返回 X。 | 真实 Runner 用 `[n3,n2] → [n2,X,n3,Y]` 恢复并证明 n3 无重复副作用、游标/outcome 正确；反向突变和适用回归。 |
| BO-9：R34 F5 IMPORTANT | 有 helper/loop 全序测试，无跨轮次多停驻恢复后的 Runner 推进证据。 | 跨轮次且锚前/后至少两个有效停驻的真实恢复，证明无跳步、重做或错误成功；定向突变回归。 |

以上均归 BO-11 冻结打包，不是 SB21-4 生产实现。每项风险继续关闭对应生产门。原级与完整出处详见 R5.3 §24.124、`_workflow/sb21-4/raw-bo-obligations.json` 和状态表。

## 并行成果核对（仅登记/发现，不集成）
开工、自然边界均执行 `python -B tools/mistletoe/deliveries.py --root .`；开工发现 R6.2 接点报告漏登并已登记；收口核验并登记 R5 Prepared 与最终完成的 R5 slot。收口扫描回执为 `_workflow/sb21-4/deliveries/final-closeout-r1.json`（exit 0、errors 0、queue 8）；另有仅审查既有候选接收要求的报告，仍 supplemental-review-only/pending。所有成果按目标批次 pending，不集成、不认作验收。

| 任务快照 | 实际提交 / 报告 SHA-256 | 当前登记状态与材料边界 |
|---|---|---|
| R5.6「核验 R5.6 迁移回滚组件」`01a0e060-f824-7bb2-8490-d53b6e6dd88d` | `c11cb45f6de2436b46c58b91d47f2c9768132ef3` / `044A62D06DEE49FCDB8E87FAD6ADB30521E03A276217785FACEB2EF817369D97` | 目标批次 R5.6，独立交付；未消费，原 `_r56_parallel/tmp/` 保留。 |
| R5 Prepared「核验 Prepared 身份账跨进程争用」`01a0e3f4-e58c-7221-b584-df1ba44f600d` | `912efbeb9b74f8ecad01b1456844227f8ff70228` / `44D89448CEDE56880ECFE43D96F0CC5DA583E00B4CB58CDB05B57459ACD09F8E` | 2026-09-27 18:52:26 UTC 最新 turn completed；报告/acceptance SHA 已核实。登记目标 D18/D26、pending；主线未消费/未验收，不提前接收。隔离组件 12/12、子进程 4/4、最终 16/16、2 mutants；无主线改动候选。 |
| R5 物理槽「核验 R5 物理槽跨进程交接」`01a0e420-364c-7ad1-af63-fe450cc707a0` | 最终 HEAD `44576c63d9e45b545943a8c1edef1ee715890c8c`；六个提交、报告 SHA `E9759F47D0B2A7D1912C571E001DCCE9C233E6D702C91BFF911CB0436F354F20`、acceptance SHA `594FB3720D436BFEDF62076DD292772440F12F17093DE045B0FFEC37B9F7AD45` 已逐项核实 | 线程 idle/终态 completed，17/17、8 个独立突变有效、1096 项 SHA 清单缺失/不匹配均 0；公共索引已更新且最终 deliveries 扫描 exit 0。交付仍仅 pending D14，不消费、不验收。 |
| 并行成果接收前复核评估（非功能交付） | commit `18e186f897b1d0691faa72837dc897c50d017623` / report SHA `BCB9704B74C8066C99F7E06499FF0174C357720025B5811655DD27837669E905` | 只审查既有候选的接收前验证缺口；源 thread 身份/状态未知，按 supplemental-review-only pending 登记，留待各自计划批次处理；未消费。 |
| R6.1 标准分发候选与集成验证 | `28d8c764f5e1ae6ae0fee5a1d99a2e41d107d964`、`111f6abe0c68330b5acdd867a21586071ce4662a` | 仍由各自 R6.1 目标批次处理；资源/过滤/运行验证阻断，未合并。 |
| R6.2 公版接点清单与 BGI guard | 接点报告 `6074DE54C24008163CFDE4BB2670BE9CC02FE53F3C4F6B7BD91376C8A702B345`；guard HEAD `d50d28e80dc550a4d21ed1ac1f9dd1402d69fd1e` | 接点报告仅登记、不消费；guard 留待 R6 diff 收敛批次。 |

R5.6、R6.1 和 R6 diff guard 均未提前集成。R5 Prepared 虽已独立任务完成，仍要等 D18/D26 指定主线消费批次和安全写入窗口。R5 slot 留待 D14 消费批次复核最终任务状态与完整材料；补充评估提出的 Q-R61/Q-R62 要求留给各自消费批次处理。
## 工作区与收口证据

- R8 复审与两项 SHOULD 文档修复均已完成；最终验证为两项目非增量构建通过、ClaimSurface regen/no-env 通过、BO-13 49/49、精确 LocalWait 240/240、助手全量 1558/2/0/1560。日志、TRX、清单 SHA 与 testId 差集见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/`；workflow closeout/verify 快照路径与状态记于 SB21-4 external active-ledger。提交后 SHA/HEAD 记录于上述外部 active-ledger。
- 本批在 `main-OldTeaBag-B168` 开工，复核 HEAD 为 `afe84f22d0ebc49d7481bb0adf58f2ea6e29ec5b`。该 SHA 是本批复审前基线，不是最终提交；本批文件在收口审计后按明确路径提交，准确提交 SHA/最终 HEAD 记录于 `C:/Users/Administrator/.tools/zcode-relay/task-relays/active-ledger.json`。并行索引、relay、独立台账、日志和历史材料均在 Git 提交范围外。
- R6/R7 记录保留为历史；R8 修复后回归、差集及 ClaimSurface 证据见 `postreview-closeout/`。R8 GPT 报告属限定范围技术裁决，不证明生产入口、真实 User、BGI 进程或实机行为；BO-11 其余原级义务继续冻结。工作流审计只做机械核验，结论由报告与代码/测试证据支持。
- 独立台账保留 R1–R8 八次累计记录，R2 已发送失败仍计次，R8 为最后一次；没有重置或新增请求。R1 五项与 R3/R4 扩展按原 IMPORTANT 有限合同关闭；BO-11 BO-6/7/8/9 原级仍为 owner checkpoint。active-ledger 指向本批最终收口状态；next-batch-prompt.md 保存一份未来 BO-6/7 完整 Goal，仅供 owner 后续启动，不表示已经创建/启动任务。
- 最终提交使用 `git commit --only -m ... -- <逐项显式路径>`。材料外已知保留项包括三份无关 tracked 文档 `Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md`、`槲寄生调度器总计划.md`，untracked parallel registry/reports、各类 `.bak`/`.stale`、历史批次、日志/TestResults、DLL/tool 输出和截图；均不进入提交。

## 下一批完整 Goal（只作为交接，不在本任务启动）

```text
/goal
结果：在独立 Wave3 子批中，以真实 Runner/loop 端到端关闭 BO-6 停驻与已完成锚冲突语义，并只纳入其直接依赖的 BO-7 R21 F1/F2 救援全序、多有效停驻问题；按原台账选择并实现完成过滤推进或显式失败态。
约束：这是未来独立子批，只有 owner 启动后才施工。开工先核对本交接 `_batch21/sb21-4-handoff-2026-09-28.md`、R5.3 §24.120.4/§24.124、`_workflow/sb21-4/raw-bo-obligations.json` 与 BO-6/7 原始台账；SB21-4 BO-13 的五项 R1 IMPORTANT 和已登记 R3/R4 IMPORTANT 扩展已按 R8 有限合同原级验收闭环，不重开 BO-13、SB21-3 BO-4 或 SB21-2 BO-10/12。只施工 BO-6 与 BO-7，不启动/并入 BO-8/9，不把 helper/排序组件证据当作 Runner 端到端验收。先读取 AGENTS.md、`$bgi-project-development`、`.agents/knowledge/domains/review-disposition-discipline.md`、`Docs/design/mistletoe-workflow-facilities.md`、`tools/mistletoe/README.md`、`Docs/design/mistletoe-parallel-deliveries.md` 及同名 JSON；开工、自然边界、收口运行 `python -B tools/mistletoe/deliveries.py --root .`，逐项核对并行任务终态、报告、验收与真实提交，仅在既定消费批次接收，不提前集成 R5.6、R6.1 或 R6 diff guard。建立/复核 `_workflow/<本批>/manifest.json` 并运行 workflow evidence、review+verify、closeout+verify；工具只作机械核验。反例先行：先证明当前真实 Runner/loop 对停驻与完成锚冲突的失败或错误成功路径，再最小实现。新增关键断言逐项反向突变；保存 baseline/build/mutant/restored 独立日志/TRX、具名目标失败和源码原始/恢复 SHA-256，确认不是编译失败或更早断言。构建和测试都加 `-p:DeployToBgiTools=false`；按影响跑 BO-6/7 定向、助手适用与全量回归，以同条件基线说明 testId 增删变化。声明面变化时执行 regen/no-env 并确认 SHA 稳定。会诊仅 GPT、每次前读取处置纪律并验证 review 快照、该子批累计最多 8 次（已发失败/超时也计），不得降级 IMPORTANT/MUST；预算用尽仍未闭合则交 owner checkpoint 并保持生产门关闭。保持产品入口、真实 User、R5.8、E3/E4/E5、热键面和 BGI 生产进程关闭，不做实机操作或生产验证。同步 R5.3、计划、handoff、独立会诊台账和 task-relays；只用 `git commit --only -m "<说明>" -- <本批明确文件路径>` 提交，保留所有材料外改动。
完成判据：Runner/loop 当前实现反例先红；实现覆盖冲突、救援后推进、完成节点不重提、停驻义务不丢、多有效停驻全序及同身份删除/插回，不产生虚假成功或流程收尾；所有新增关键断言的目标突变失败且精确恢复后通过；BO-6 R19 MUST、关联 R21 F4 IMPORTANT 与 BO-7 R21 F1/F2 MUST 按原级闭环，否则逐项记录给 owner；助手回归/testId 差集可解释；R5.3、计划、独立台账、handoff、task-relays 与 workflow closeout/verify 完成；提交清单明确、提交后 Git 状态核实且所有生产门仍关闭。
```


## source: MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs L380-L650 SHA256=b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f
Complete RequestRunAction, StopParkedRun, final reread, pre-side-effect guards, write/error/reconcile order and source identity helper.
    public HostActionResult RequestRunAction(string runId, WorkflowRunAction action)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return HostActionResult.Unavailable("运行 runId 为空，未执行动作");

        WorkflowRunRecord? run;
        try { run = _runs.Load(runId); }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable("运行记录读取失败，未执行动作："
                + ex.GetType().Name + "（" + ex.Message + "）");
        }
        if (run is null) return HostActionResult.Unavailable("运行记录不存在：" + runId);
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(run.RunId)
            || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
            return HostActionResult.Unavailable("运行文件名与记录内 runId 不一致，拒绝执行动作");

        if (action == WorkflowRunAction.Stop && run.State == WorkflowRunState.LocalWaitParking)
            return StopParkedRun(runId);

        // Explicit retry path after a previous Stop durably cancelled the run but admission
        // reconciliation could not be confirmed. It is safe for any cancelled run: an empty
        // runBinding lookup is a confirmed no-op, and already-terminal operations are not rewritten.
        if (_admissionWired && action == WorkflowRunAction.Stop && run.State == WorkflowRunState.Cancelled)
            return ReconcileAdmissionTerminalForExplicitStop(runId,
                "运行已终态化，关联受理登记已核对");

        if (action == WorkflowRunAction.Stop && run.State == WorkflowRunState.Paused)
        {
            // 暂停态无驱动（引擎 _controls 已移除）：无在飞作业（暂停节点边界生效），宿主直接终态化
            if (run.CurrentSubmission is { InFlight: true })
                return HostActionResult.Unavailable("存在在飞提交事实（结果不可考），请按幂等键对账后再处置");
            lock (_gate)
            {
                var fresh = _runs.Load(runId);
                if (fresh?.State != WorkflowRunState.Paused)
                    return HostActionResult.Unavailable("运行状态已变化，请刷新后重试");
                fresh.State = WorkflowRunState.Cancelled;
                fresh.Note = (fresh.Note is null ? "" : fresh.Note + " ")
                    + "暂停态显式停止（无在飞作业；不触发收尾）。";
                _runs.Update(fresh);
            }
            NotifyStateChanged();
            return HostActionResult.Effective("已停止（暂停态终态化，未触发收尾）");
        }

        DriveEntry? entry;
        lock (_gate) _drives.TryGetValue(run.WorkflowId, out entry);
        if (entry is null || !entry.Runner.HasActiveControl(runId))
            return HostActionResult.Unavailable(
                $"运行当前不在驱动中（状态 {run.State}）：Interrupted/Paused/LocalWaitParking 请用「恢复」，终态运行无需动作");
        entry.Runner.RequestAction(runId, action);
        return HostActionResult.Registered(action switch
        {
            WorkflowRunAction.Stop => "停止已登记（运行令牌取消，在飞作业将请求远端取消）",
            WorkflowRunAction.SkipCurrent => "跳过当前节点已登记（远端取消确认后推进，未确认则 Unknown 停驻）",
            WorkflowRunAction.Pause => "暂停请求已登记（当前节点结束后生效，≠停止）",
            WorkflowRunAction.ReloadDefinition => "重载修订已登记（下一节点边界生效）",
            _ => "动作已登记",
        });
    }

    /// <summary>停驻运行的显式放弃：先按完整队列绑定取消等待项，再把同一运行记为终态；绝不收尾或发送。</summary>
    private HostActionResult StopParkedRun(string runId)
    {
        lock (_gate)
        {
            WorkflowRunRecord? fresh;
            try { fresh = _runs.Load(runId); }
            catch (Exception ex)
            {
                return HostActionResult.Unavailable("停驻运行记录复核失败，未清理等待项："
                    + ex.GetType().Name + "（" + ex.Message + "）");
            }
            if (fresh is null || string.IsNullOrWhiteSpace(fresh.RunId)
                || !string.Equals(fresh.RunId, runId, StringComparison.Ordinal))
                return HostActionResult.Unavailable("运行文件名与记录内 runId 不一致，拒绝终态化");
            if (fresh?.State != WorkflowRunState.LocalWaitParking)
                return HostActionResult.Unavailable("运行状态已变化，请刷新后重试");
            if (_reservedWorkflows.Contains(fresh.WorkflowId) || _drives.ContainsKey(fresh.WorkflowId))
                return HostActionResult.Unavailable("停驻运行仍处于启动或驱动收敛窗口，请稍后重试");
            if (RunStore.HasUnresolvedExternalFact(fresh))
                return HostActionResult.Unavailable("存在未决发送或收尾事实，必须先按原身份对账；未停止运行、未清理等待项");
            if (HasUnresolvedPrerequisiteResponsibility(fresh))
                return HostActionResult.Unavailable("存在未决前置动作责任，必须按原身份对账；未停止运行、未清理等待项");
            if (!HasValidParkedDecision(fresh, out var binding))
                return HostActionResult.Unavailable("停驻运行的零发送裁定或游标绑定不完整，拒绝终态化");

            var cleanup = LocalWaitBindingCancelResult.Missing;
            if (binding is not null)
            {
                try
                {
                    cleanup = LocalWaitQueue.Cancel(binding, "停驻运行显式停止", DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    return HostActionResult.Unavailable("等待项清理失败，运行保持停驻以便重试："
                        + ex.GetType().Name + "（" + ex.Message + "）");
                }
                if (cleanup == LocalWaitBindingCancelResult.PayloadMismatch)
                    return HostActionResult.Unavailable("等待项身份或载荷已变化，拒绝取消并保留停驻运行");
            }

            fresh.State = WorkflowRunState.Cancelled;
            fresh.Note = (fresh.Note is null ? "" : fresh.Note + " ")
                + (binding is null
                    ? "持久拒登停驻已显式放弃（无等待绑定；零发送；不触发收尾）。"
                    : cleanup is LocalWaitBindingCancelResult.Cancelled or LocalWaitBindingCancelResult.AlreadyCancelled
                        ? "本地等待停驻已显式放弃（等待项已墓碑化；零发送；不触发收尾）。"
                        : "本地等待停驻已显式放弃（绑定等待项已不存在；零发送；不触发收尾）。");
            try
            {
                _runs.Update(fresh);
            }
            catch (Exception ex)
            {
                return HostActionResult.Unavailable("等待项处置已提交，但运行终态落盘失败；保留原运行记录并可重试："
                    + ex.GetType().Name + "（" + ex.Message + "）");
            }
        }

        NotifyStateChanged();
        return ReconcileAdmissionTerminalForExplicitStop(runId,
            "已放弃停驻运行（终态化，未触发收尾）");
    }

    /// <summary>Stop 专用的前置动作责任护栏；恢复扫描的既有状态分类保持不变。</summary>
    private static bool HasUnresolvedPrerequisiteResponsibility(WorkflowRunRecord run)
        => run.PrerequisiteActions?.Any(action => action.State is not (
            PrerequisiteActionState.Succeeded or PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled)) == true;

    private static bool HasValidParkedDecision(WorkflowRunRecord run, out LocalWaitBinding? binding)
    {
        binding = null;
        var decision = run.LocalWaitDecision;
        if (decision is null || !decision.NoSendConfirmed
            || decision.Kind is not (LocalWaitDecisionKind.Wait or LocalWaitDecisionKind.Hold)
            || run.Cursor is not { } cursor
            || string.IsNullOrWhiteSpace(run.RunId)
            || string.IsNullOrWhiteSpace(run.WorkflowId)
            || string.IsNullOrWhiteSpace(run.WorkflowRevision)
            || string.IsNullOrWhiteSpace(cursor.NodeId)
            || cursor.Occurrence < 0 || cursor.Occurrence > 99_999_999
            || cursor.LoopIteration < 0 || cursor.LoopIteration > 99_999_999
            || cursor.Attempt <= 0 || cursor.Attempt > 99_999_999)
            return false;

        var context = decision.Context;
        if (context is null
            || string.IsNullOrWhiteSpace(context.RunId)
            || string.IsNullOrWhiteSpace(context.WorkflowId)
            || string.IsNullOrWhiteSpace(context.WorkflowRevision)
            || string.IsNullOrWhiteSpace(context.NodeId)
            || run.RecordRevision <= 0
            || context.RecordRevision <= 0 || context.RecordRevision > run.RecordRevision
            || context.SequenceIndex < 0 || context.Occurrence < 0
            || context.Occurrence > 99_999_999
            || context.LoopIteration < 0 || context.Attempt <= 0
            || context.LoopIteration > 99_999_999 || context.Attempt > 99_999_999
            || !string.Equals(context.RunId, run.RunId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowId, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowRevision, run.WorkflowRevision, StringComparison.Ordinal)
            || !string.Equals(context.CursorNodeId, cursor.NodeId, StringComparison.Ordinal)
            || context.CursorOccurrence != cursor.Occurrence
            || context.CursorLoopIteration != cursor.LoopIteration
            || !string.Equals(context.NodeId, cursor.NodeId, StringComparison.Ordinal)
            || context.Occurrence != cursor.Occurrence
            || context.LoopIteration != cursor.LoopIteration
            || context.Attempt != cursor.Attempt
            || run.CurrentSubmission is not { } submission
            || submission.Intent != SubmitIntentState.LocalWaitDeferred
            || submission.SendAttempted
            || !string.IsNullOrEmpty(submission.JobId)
            || !string.IsNullOrEmpty(submission.AcceptedSendIdentity)
            || submission.ObservedTerminal is not null
            || !string.Equals(submission.NodeId, context.NodeId, StringComparison.Ordinal)
            || submission.Occurrence != context.Occurrence
            || submission.LoopIteration != context.LoopIteration
            || submission.Attempt != context.Attempt
            || !string.Equals(submission.Key,
                RunStore.DeriveSubmissionKey(run.RunId, context.NodeId, context.Occurrence,
                    context.LoopIteration, context.Attempt), StringComparison.Ordinal))
            return false;

        if (decision.Kind == LocalWaitDecisionKind.Hold)
            return decision.Binding is null;
        if (decision.Binding is not { } waitBinding
            || string.IsNullOrWhiteSpace(waitBinding.ItemId)
            || string.IsNullOrWhiteSpace(waitBinding.StableIdentity)
            || string.IsNullOrWhiteSpace(waitBinding.CandidateId)
            || string.IsNullOrWhiteSpace(waitBinding.AdmissionIdentity)
            || string.IsNullOrWhiteSpace(waitBinding.Namespace)
            || string.IsNullOrWhiteSpace(waitBinding.SourceIdentity)
            || string.IsNullOrWhiteSpace(waitBinding.Scope)
            || string.IsNullOrWhiteSpace(waitBinding.WorkflowRevision)
            || string.IsNullOrWhiteSpace(waitBinding.NodeId)
            || string.IsNullOrWhiteSpace(waitBinding.PrerequisiteReference)
            || waitBinding.RecordRevision <= 0 || waitBinding.RecordRevision > run.RecordRevision
            // The queue binding is immutable across explicit repark: a fresh decision context may
            // be newer, but it must never predate the binding snapshot it is validating.
            || waitBinding.RecordRevision > context.RecordRevision
            || waitBinding.SequenceIndex != context.SequenceIndex
            || !string.Equals(waitBinding.RunId, run.RunId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.WorkflowId, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.Namespace, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.WorkflowRevision, run.WorkflowRevision, StringComparison.Ordinal)
            || !string.Equals(waitBinding.NodeId, cursor.NodeId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.CursorNodeId, cursor.NodeId, StringComparison.Ordinal)
            || waitBinding.CursorOccurrence != cursor.Occurrence
            || waitBinding.CursorLoopIteration != cursor.LoopIteration
            || waitBinding.Occurrence != cursor.Occurrence
            || waitBinding.LoopIteration != cursor.LoopIteration
            || waitBinding.Attempt != cursor.Attempt
            || waitBinding.SourceKind != context.SourceKind
            || !string.Equals(context.SourceIdentity, waitBinding.SourceIdentity, StringComparison.Ordinal)
            || !HasValidParkedSourceIdentity(run, waitBinding)
            || !string.Equals(context.Scope, waitBinding.Scope, StringComparison.Ordinal)
            || !string.Equals(context.CandidateId, waitBinding.CandidateId, StringComparison.Ordinal)
            || !string.Equals(context.AdmissionIdentity, waitBinding.AdmissionIdentity, StringComparison.Ordinal)
            || context.Tier != waitBinding.Tier
            || context.Priority != waitBinding.Priority
            || context.IsHoeingHighest != waitBinding.IsHoeingHighest
            || !context.HasTrustedRankingFacts
            || waitBinding.IsHoeingHighest
            || !Enum.IsDefined(waitBinding.SourceKind)
            || !Enum.IsDefined(waitBinding.Tier)
            || !IsCanonicalAdmissionScope(waitBinding.Scope)
            || !string.Equals(waitBinding.StableIdentity,
                run.RunId + "|" + cursor.NodeId + "|" + cursor.Occurrence + "|" + cursor.LoopIteration,
                StringComparison.Ordinal)
            || !string.Equals(waitBinding.ItemId, LocalWaitQueuePolicy.DeriveItemId(waitBinding.StableIdentity),
                StringComparison.Ordinal)
            || !LocalWaitIdentityTranslation.Translate(waitBinding.ToQueueItem()).Ok)
            return false;

        var candidate = BuildSuccessorIdentityCandidate(waitBinding.Scope, run.WorkflowId, run.RunId,
            cursor.NodeId, cursor.Occurrence, cursor.LoopIteration, cursor.Attempt);
        var expectedIdentity = LocalWaitIdentityTranslation.BuildAdmissionIdentity(candidate);
        if (!string.Equals(waitBinding.AdmissionIdentity, expectedIdentity.AdmissionIdentity, StringComparison.Ordinal)
            || !string.Equals(waitBinding.CandidateId, expectedIdentity.CandidateId, StringComparison.Ordinal))
            return false;

        binding = waitBinding;
        return true;
    }

    private static bool HasValidParkedSourceIdentity(WorkflowRunRecord run, LocalWaitBinding binding)
    {
        // Panel RequestIdentity is captured from the resolved FlowRegistration parent into both
        // decision snapshots; it is not duplicated in RunStore. Stop uses it only as paired
        // snapshot identity and never as submission authority.
        if (binding.SourceKind == LocalWaitSourceKind.PanelFlowRegistration)
            return true;

        // StartupHandoff has an independent durable source in this run record. Do not let a
        // matching context/binding pair substitute another run's handoff identity or scope.
        return binding.SourceKind == LocalWaitSourceKind.StartupHandoff
               && string.Equals(binding.SourceIdentity, run.RunId, StringComparison.Ordinal)
               && IsCanonicalAdmissionScope(run.AdmissionSourceScope)
               && string.Equals(binding.Scope, run.AdmissionSourceScope, StringComparison.Ordinal)
               && (run.Handoffs ?? []).Any(handoff => handoff is not null
                   && handoff.Mode is StartupHandoffModes.Start or StartupHandoffModes.ArmTrigger);
    }

    // ================= R4.9 启动移交受理入口 =================

    /// <summary>本地执行能力守卫（R4.9 §6.2 + ASTRA 二轮 I2：先于恢复屏障与台账写入等一切副作用；绕过 VM 的调用同样被拦；
    /// 提交临界区内复核——能力可能在两次检查间动态变化）。</summary>
    private string? CapabilityBlockReason()
        => _localExecutionCapability is { } cap && !cap()


## source: MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs L1910-L1954 SHA256=f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5
Admission parent resolution and canonical scope/identity.
    ///   ⇒ 返回合成来源身份 `run-source:{runId}` 与该固定 Scope；任一不成立 ⇒ null（不签发、不发送）。
    /// </summary>
    internal static (string RequestIdentity, string Scope)? ResolveAdmissionParent(
        IReadOnlyList<OperationRecord>? sources, WorkflowRunRecord? run, string runId, string workflowId)
    {
        var matches = (sources ?? [])
            .Where(o => o is not null
                        && ArbitrationAdmissionService.IsFlowRegistrationParent(o, runId)
                        && string.Equals(o.Candidate?.WorkflowId, workflowId, StringComparison.Ordinal))
            .ToList();
        // 面板来源**唯一**命中 ⇒ 只认它；内容不完整 ⇒ 不签发（不回落到运行台账、不补造）。
        if (matches is { Count: 1 })
        {
            var op = matches[0];
            var scope0 = op.Candidate?.Scope;
            // [批次四十五 第三轮验证会诊处置] 面板来源的 Scope **同样**必须满足规范形状（`bgi:local:{非空完整 epoch}`）：
            // 否则 `garbage`／`bgi:local:`／其它实例前缀都会被当成权威来源。
            return IsCanonicalAdmissionScope(scope0) && !string.IsNullOrEmpty(op.RequestIdentity)
                ? (op.RequestIdentity, scope0!)
                : null;
        }
        // 面板来源**歧义**（同一 runBinding 多条）⇒ **直接拒绝**，**禁止**借运行台账字段继续签发
        // （[批次四十五 验证会诊处置]：否则来源冲突会被运行来源掩盖，形成越权/误判路径）。
        if (matches is { Count: > 1 }) return null;
        // 零条面板来源 ⇒ 按类别回落：启动移交的来源权威＝**运行台账受理登记事实**（与租约相互独立的存储；
        // 租约未初始化/不可读时同样回落——准入本身仍由门面 fail-closed 把关）。
        if (run is null || !string.Equals(run.WorkflowId, workflowId, StringComparison.Ordinal)) return null;
        // 来源类别证明：该 run 必须**确有启动移交受理事实**（start/armTrigger 绑定），且 Scope 为规范形状。
        if (!(run.Handoffs ?? []).Any(h => h is not null
                                          && h.Mode is StartupHandoffModes.Start or StartupHandoffModes.ArmTrigger))
            return null;
        var fixedScope = run.AdmissionSourceScope;
        if (!IsCanonicalAdmissionScope(fixedScope)) return null;
        return ("run-source:" + runId, fixedScope!);
    }

    /// <summary>准入来源 Scope 规范形状：`bgi:local:{非空完整 epoch}`（实例段固定 `local`；epoch 含冒号不成问题）。</summary>
    internal static bool IsCanonicalAdmissionScope(string? scope)
    {
        if (string.IsNullOrEmpty(scope) || !scope.StartsWith("bgi:local:", StringComparison.Ordinal)) return false;
        return !string.IsNullOrEmpty(scope["bgi:local:".Length..].Trim());
    }

    /// <summary>夹具接缝：按运行反查**准入来源**（身份＋固定 Scope）；生产路径内部同源（`TryGetAdmissionParent`）。</summary>
    internal (string RequestIdentity, string Scope)? AdmissionParentForTest(string runId, string workflowId)


## source: MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs L2580-L2730 SHA256=f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5
Explicit-Stop terminal reconciliation, retry, observability and per-registration isolation.
    }

    private Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalAsync(string? runId)
        => Task.Run(() => ReconcileAdmissionTerminalCoreAsync(runId));

    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreAsync(string? runId)
    {
        try
        {
            return await ReconcileAdmissionTerminalCoreBodyAsync(runId).ConfigureAwait(false);
        }
        finally
        {
            AdmissionTerminalReconciliationCompletedForTest?.Invoke();
        }
    }

    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync(string? runId)
    {
        if (!_admissionWired) return AdmissionTerminalReconciliationOutcome.NotRequired;
        if (string.IsNullOrWhiteSpace(runId))
            return AdmissionTerminalReconciliationOutcome.Failed;

        try { await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            TryLog("[任务中心] 终局回写无法初始化受理存储（保守留待重试）:" + ex.Message);
            return AdmissionTerminalReconciliationOutcome.Failed;
        }
        if (_admission is null || _admissionStore is null)
            return AdmissionTerminalReconciliationOutcome.Failed;

        WorkflowRunRecord? run;
        try { run = _runs.Load(runId); }
        catch (Exception ex)
        {
            TryLog("[任务中心] 终局回写无法读取运行记录（保守留待重试）:" + ex.Message);
            return AdmissionTerminalReconciliationOutcome.Failed;
        }
        if (run is null || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
            return AdmissionTerminalReconciliationOutcome.Failed;
        if (run.State is not (WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled))
            return AdmissionTerminalReconciliationOutcome.NotTerminal;

        var timeout = AdmissionTerminalReconciliationTimeoutForTest ?? TimeSpan.FromSeconds(15);
        var settleClock = System.Diagnostics.Stopwatch.StartNew();
        var readAttempt = 0;
        List<OperationRecord>? accepted = null;
        List<OperationRecord>? current = null;
        while (settleClock.Elapsed <= timeout)
        {
            try
            {
                var injected = AdmissionTerminalReadFaultForTest?.Invoke(++readAttempt);
                if (injected is not null) throw injected;
                var leaseRead = _admissionStore.Read();
                if (leaseRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)
                {
                    await Task.Delay(20).ConfigureAwait(false);
                    continue;
                }
                current = leaseRead.File?.Handoff?.Operations?
                    .Where(op => string.Equals(op.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
            }
            catch (IOException)
            {
                await Task.Delay(20).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                TryLog("[任务中心] 终局回写读取失败（保守留待重试）:" + ex.Message);
                return AdmissionTerminalReconciliationOutcome.Failed;
            }

            if (current.Count == 0) return AdmissionTerminalReconciliationOutcome.NoMapping;
            if (current.All(IsAdmissionTerminalOrClosed)) return AdmissionTerminalReconciliationOutcome.Completed;

            var ready = current.Where(op => op.RequestState == OperationRequestState.Accepted).ToList();
            var transitioning = current.Any(op => op.RequestState is OperationRequestState.Queued
                or OperationRequestState.InRound or OperationRequestState.Granted or OperationRequestState.Sending);
            if (ready.Count > 0 && !transitioning)
            {
                accepted = ready;
                break;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (accepted is null)
        {
            TryLog("[任务中心] 仲裁操作终局回写等待超时/存在未决责任（保守留待显式重试）。");
            return AdmissionTerminalReconciliationOutcome.Pending;
        }

        // A failure on one sibling must not skip writeback attempts for the other Accepted operations.
        foreach (var op in accepted)
        {
            AdmissionResult? result = null;
            Exception? failure = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    failure = AdmissionTerminalWriteFaultForTest?.Invoke(op.RequestIdentity, attempt);
                    if (failure is not null) throw failure;
                    result = AdmissionTerminalResultForTest?.Invoke(op.RequestIdentity)
                        ?? _admission.MarkOperationTerminal(op.RequestIdentity, "runstore:" + run.State);
                    break;
                }
                catch (IOException ex) when (attempt < 5)
                {
                    failure = ex;
                    await Task.Delay(25).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }
            }
            if (failure is not null || result is null || result.Kind == AdmissionResultKind.Error)
                TryLog($"[任务中心] 仲裁操作终局回写被拒（{result?.ReasonCode ?? failure?.GetType().Name ?? "unknown"}）："
                    + (result?.Detail ?? failure?.Message ?? "无结果，保守留待重试"));
        }

        // Confirm every operation bound to this run after independent write attempts.
        try
        {
            var finalReadFault = AdmissionTerminalReadFaultForTest?.Invoke(++readAttempt);
            if (finalReadFault is not null) throw finalReadFault;
            var finalRead = _admissionStore.Read();
            if (finalRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)
                return AdmissionTerminalReconciliationOutcome.Pending;
            current = finalRead.File?.Handoff?.Operations?
                .Where(op => string.Equals(op.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            TryLog("[任务中心] 终局回写后复核失败（保守留待重试）:" + ex.Message);
            return AdmissionTerminalReconciliationOutcome.Pending;
        }

        if (current.Count == 0) return AdmissionTerminalReconciliationOutcome.NoMapping;
        return current.All(IsAdmissionTerminalOrClosed)
            ? AdmissionTerminalReconciliationOutcome.Completed
            : AdmissionTerminalReconciliationOutcome.Pending;
    }

    private static bool IsAdmissionTerminalOrClosed(OperationRecord operation)
        => operation.RequestState is OperationRequestState.TerminalCompleted


## source: MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs L210-L242 SHA256=e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea
External responsibility compatibility and Stop-specific separation.
    /// 是否存在未决外部事实（ASTRA 二轮 I4：恢复扫描与驱动异常收敛统一判定）——主体提交在飞 / 收尾在意或执行中。
    /// 注意（R4.9 二轮处置回退）：前置动作在飞【不计入】——R4.6 已验收合同是「前置在飞 → Interrupted，
    /// 恢复时经 ReconcileAsync 对账」，标 Unknown 会绕过该合同（RecoverOnStart_PrerequisiteInFlight 回归证明）。
    /// </summary>
    public static bool HasUnresolvedExternalFact(WorkflowRunRecord rec)
        => rec.State == WorkflowRunState.Completing
           || rec.PendingCompletion is not null
           || rec.CurrentSubmission is { ObservedTerminal: null } sub
              && (sub.Intent == SubmitIntentState.Accepted
                  || sub.InFlight || sub.SendAttempted
                  || !string.IsNullOrEmpty(sub.JobId)
                  || !string.IsNullOrEmpty(sub.AcceptedSendIdentity));

    /// <summary>推进记录（提交受理/终态/水位/等待/收尾状态更新；记录修订单调递增）。</summary>
    public void Update(WorkflowRunRecord rec) => Persist(rec, rec.RecordRevision);

    /// <summary>
    /// **[P7／§12.2 第 3 项「字段合并」] 按身份字段的选择性更新**：在存储闸门内**重新加载盘上最新记录**，把调用方
    /// 经 <paramref name="applyOwnedFields"/> 声明的**自有字段**应用到**最新记录**上，再以最新修订原子发布。
    /// 与 <see cref="Update"/> 的区别：并发写入者改动的**非自有字段**由此**保留**，不再因修订漂移整笔失败
    /// （也不再允许调用方携带的旧对象整对象覆盖他人改动）。
    /// 返回 `false`＝记录不存在（无副作用）；盘上记录损坏时抛 <see cref="RunRecordConflictException"/>（原件保留、拒绝覆盖）。
    /// **自有字段范围**由调用方声明；本方法不做字段语义校验（身份校验由调用方在其回调内完成）。
    /// </summary>
    /// <param name="applyOwnedFields">
    /// 回调返回 **`false`＝前置条件不成立 ⇒ 不发布、不推进修订**（调用方须据此保守处置并自行回滚内存视图）。
    /// 返回 `true`＝已按**自有字段**更新并原子发布。
    /// </param>
    public bool UpdateMergingIf(string runId, Func<WorkflowRunRecord, bool> applyOwnedFields, out WorkflowRunRecord? latest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(applyOwnedFields);
        lock (_gate)


## source: MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs L360-L410 SHA256=e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea
Persisted LocalWaitParking startup recovery and revision transition.
    public IReadOnlyList<WorkflowRunRecord> RecoverOnStart()
    {
        var recovered = new List<WorkflowRunRecord>();
        foreach (var rec in List())
        {
            if (rec.IsTerminal) continue;
            var hasUnresolvedExternalFact = HasUnresolvedExternalFact(rec);
            // R4.8（宿主夹具连带发现）：Unknown 已是保守收敛终点（结果不确定待对账）——再扫描不改动、不追加笔记、
            // 更不降级 Interrupted（否则 ResumeAsync 的 Unknown 守卫被绕过，前置未知记录场景可未经对账恢复）；
            // 仍返回供 Reconciler/宿主对账决策（幂等保持，CrashWindow2 合同不变）。
            if (rec.State == WorkflowRunState.Unknown)
            {
                recovered.Add(rec);
                continue;
            }
            // Interrupted 通常幂等保持，但必须先检查未决外部事实；旧/冲突记录不得借 Interrupted 绕过对账。
            if (rec.State == WorkflowRunState.Interrupted && !hasUnresolvedExternalFact)
            {
                recovered.Add(rec);
                continue;
            }
            string note;
            // 先核验任何尚未闭合的外部事实；即使 State/LocalWaitDecision 声称本地等待，只要发送/收尾事实
            // 有矛盾或含混，仍必须 Unknown，不能让停驻标签遮住对账义务。
            if (hasUnresolvedExternalFact && (rec.State == WorkflowRunState.Completing || rec.PendingCompletion is not null))
            {
                // B5：收尾意图已落盘但执行结果未知——结果不确定，禁止自动补发收尾
                rec.State = WorkflowRunState.Unknown;
                note = "助手重启：收尾动作在飞（执行结果未证实），标 Unknown，需人工对账，禁止自动补发收尾。";
            }
            else if (hasUnresolvedExternalFact && rec.CurrentSubmission is { } sub)
            {
                rec.State = WorkflowRunState.Unknown;
                note = $"助手重启：提交存在未闭合发送事实（{sub.Key}，节点 {sub.NodeId}），标 Unknown，需按幂等键+job 查询对账，禁止自动重跑。";
            }
            else if (rec.State == WorkflowRunState.LocalWaitParking)
            {
                // [批次 20／Wave3／C11=(a)] 本地等待仅在上方已排除收尾/发送事实后收敛为 Interrupted。
                rec.State = WorkflowRunState.Interrupted;
                note = "助手重启：本地等待停驻运行，标 Interrupted（等待项绑定可由显式恢复重建）。";
            }
            else
            {
                rec.State = WorkflowRunState.Interrupted;
                note = "助手重启：运行被中断，标 Interrupted，恢复需显式决策。";
            }
            rec.Note = AppendNote(rec.Note, note);
            Persist(rec, rec.RecordRevision);
            recovered.Add(rec);
        }
        return recovered;


## source: MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs L288-L345 SHA256=e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b
Full binding match, tombstone idempotence and generation/high-water behavior.
    public LocalWaitBindingCancelResult Cancel(LocalWaitBinding binding, string reason, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.ItemId);
        lock (_sync)
        {
            var snapshot = ReadSnapshotLocked();
            var item = snapshot.Items.FirstOrDefault(i => string.Equals(i.ItemId, binding.ItemId, StringComparison.Ordinal));
            if (item is null) return LocalWaitBindingCancelResult.Missing;
            if (!MatchesBindingPayload(binding, item)) return LocalWaitBindingCancelResult.PayloadMismatch;
            if (item.State == LocalWaitItemState.Cancelled) return LocalWaitBindingCancelResult.AlreadyCancelled;

            item.State = LocalWaitItemState.Cancelled;
            item.Reason = string.IsNullOrWhiteSpace(reason) ? "停驻运行显式停止" : reason;
            item.CancelledAtUtc = nowUtc.ToUniversalTime();
            Persist(snapshot.Items, snapshot.GenerationHighWater);
            return LocalWaitBindingCancelResult.Cancelled;
        }
    }

    private static bool MatchesBindingPayload(LocalWaitBinding binding, LocalWaitItem item)
        => string.Equals(item.ItemId, binding.ItemId, StringComparison.Ordinal)
           && string.Equals(item.StableIdentity, binding.StableIdentity, StringComparison.Ordinal)
           && string.Equals(item.CandidateId, binding.CandidateId, StringComparison.Ordinal)
           && string.Equals(item.AdmissionIdentity, binding.AdmissionIdentity, StringComparison.Ordinal)
           && string.Equals(item.Namespace, binding.Namespace, StringComparison.Ordinal)
           && string.Equals(item.WorkflowId, binding.WorkflowId, StringComparison.Ordinal)
           && item.Tier == binding.Tier
           && item.Priority == binding.Priority
           && item.IsHoeingHighest == binding.IsHoeingHighest
           && item.ScheduledAt == binding.ScheduledAt
           && string.Equals(item.PrerequisiteReference, binding.PrerequisiteReference, StringComparison.Ordinal)
           && item.EnqueuedAtUtc == binding.EnqueuedAtUtc
           && !item.HasTrustedIdentity;

    /// <summary>
    /// 应用失效清理（置 Cancelled 并记录原因/时刻），并按保留期裁剪过期墓碑；有变化才写盘。
    /// 回调在路径锁外对隔离副本运行；提交前在路径锁内核对原始文件快照，冲突时拒绝覆盖并保留并发写入。
    /// 写入失败不会破坏原文件（先写临时文件，成功后才替换）。
    /// </summary>
    public LocalWaitCleanupResult PersistCleanup(Func<LocalWaitItem, string?> invalidationReason, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(invalidationReason);

        QueueSnapshot snapshot;
        lock (_sync) snapshot = ReadSnapshotLocked();

        // 外部回调在路径锁外执行，并且只接触隔离副本。
        var callbackItems = MaterializeAndValidatePayload(snapshot.Items);
        var originalIds = new Dictionary<LocalWaitItem, string>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < callbackItems.Count; index++)
            originalIds.Add(callbackItems[index], snapshot.Items[index].ItemId);

        var decisions = LocalWaitQueuePolicy.Cleanup(callbackItems, invalidationReason, nowUtc);
        foreach (var decision in decisions)
        {
            if (!originalIds.TryGetValue(decision.Item, out var originalItemId))
                throw new LocalWaitQueueCorruptException("清理策略返回了不属于输入快照的等待项：拒绝提交。");


## source: MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs L450-L560 SHA256=781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06
Explicit resume eligibility, responsibility check and local wait transition.
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


## source: MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs L1830-L1920 SHA256=781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06
Binding/context creation and repark revision refresh.
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


## source: MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs L5250-L5300 SHA256=66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29
Admission terminal state check and lease mutation.
    public AdmissionResult MarkOperationTerminal(string requestIdentity, string authoritativeTerminalEvidence)
    {
        if (string.IsNullOrWhiteSpace(authoritativeTerminalEvidence))
            return AdmissionResult.Of(AdmissionResultKind.Error, "evidence_required", "权威终态证据必填（不凭超时/未命中终局）。", requestIdentity);
        _gate.Wait();
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            var lease = read.File.Lease;
            var op = FindOp(read.File, requestIdentity);
            if (op is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
            if (op.RequestState != OperationRequestState.Accepted)
                return AdmissionResult.Of(AdmissionResultKind.Error, "not_accepted", "仅已受理操作可终局完成（其余状态按各自判据）。", requestIdentity);
            // §24.15／§24.1-6（[Batch B 会诊阻断处置]）：外部启动**禁止**借通用终局入口的「台账布尔确认」旁路——
            // 其终局必须经完成结算入口按唯一顺序完成（ExecutionResult＋PendingTerminal → 台账 Terminal → 关闭 → 终局）。
            if (op.OperationType == OperationType.ExternalStart)
                return AdmissionResult.Of(AdmissionResultKind.Error, "external_start_requires_completion_settlement",
                    "外部启动必须经完成结算入口（SettleCompletionAsync）终局，禁止旁路通用终局入口。", requestIdentity);
            // 台账一致交叉确认（关联 job 权威终态；未配置=保守不允许）。
            if (_hooks.TakeoverTerminalConfirmed?.Invoke(op.SubmissionIdentity, op.LastSendSeq) != true)
                return AdmissionResult.Of(AdmissionResultKind.Error, "ledger_not_terminal", "接管台账未确认权威终态（保守不终局）。", requestIdentity);

            var now = _utcNow();
            var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op2 = FindOp(file, requestIdentity);
                if (op2 is null || op2.RequestState != OperationRequestState.Accepted) return "state_changed";
                op2.RequestState = OperationRequestState.TerminalCompleted;
                op2.Zone = OperationZone.TerminalPendingTransfer;
                op2.UpdatedRevision = file.Revision + 1;
                op2.UpdatedAtUtc = now;
                MigrateAndClean(file, now);
                return null;
            });
            return mutate.Success
                ? AdmissionResult.Of(AdmissionResultKind.Accepted, "terminal_completed", "权威终态完成（主槽位经迁移释放）。", requestIdentity)
                : AdmissionResult.Of(AdmissionResultKind.Error, mutate.Reason ?? "invalid_request", "终局落盘失败。", requestIdentity);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>统一关闭接口（§4.2c 两个合法分支共用：当前所有者+当前 revision+目标发送身份匹配；绝不消解 Pending；关闭即同次原子发布更新 Operations 并移除 Submission）。</summary>
    private LeaseMutateResult CloseSubmission(LeaseSegment lease, SubmissionRecord submission, string ownerRequestIdentity,
        Func<LogicalOwnerLeaseFile, string?> applyOutcome,
        bool keepOpenWhenOwnerConflictPending = false,


## source: MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs L785-L850 SHA256=39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8
Cross-process lease lock, latest state, revision publication and failure semantics.
    public LeaseMutateResult MutateHandoffLatest(string leaseId, string ownerEpoch, Func<LogicalOwnerLeaseFile, string?> mutate, bool checkSwitchGate = false)
        => MutateCore(leaseId, ownerEpoch, null, mutate, checkSwitchGate);

    /// <summary>统一锁内核（expectedRevision=null 表示锁内取最新修订号；其余校验两变体完全一致）。</summary>
    private LeaseMutateResult MutateCore(string leaseId, string ownerEpoch, long? expectedRevision, Func<LogicalOwnerLeaseFile, string?> mutate, bool checkSwitchGate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return MutateReject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return MutateReject("unsupported_version");

            var now = _utcNow();
            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || (expectedRevision is { } expected && read.File!.Revision != expected)
                || IsOwnerExpired(lease))
                return MutateReject("lease_stale_generation");

            if (read.File.Diag?.ResidueReconcilePending == true) return MutateReject("residue_reconcile_pending");
            if (checkSwitchGate && read.File.Diag?.SwitchGateActive == true) return MutateReject("switch_gate_active");

            var reason = mutate(read.File);
            if (reason is not null) return MutateReject(reason);

            read.File.Revision += 1;
            lease.HeartbeatSeq += 1;
            lease.LastHeartbeatUtc = now;
            // Validate the exact candidate that would be published. Read-side validation alone is too late:
            // it would let a successful mutation replace a valid lease with a file that every later reader
            // classifies as Corrupt. The in-memory candidate is discarded on rejection, leaving disk intact.
            if (!ValidateLeaseSegment(read.File.Lease, out _)
                || !ValidateHandoffSegment(read.File.Handoff, SupportedVersion, out _))
                return MutateReject("invalid_mutation_state");
            Publish(read.File);
            _lastOwnerWriteMono = _monotonic(); // 一律 Publish 成功后刷新（R5.1 四轮 P1-② 纪律延伸）
            return new LeaseMutateResult { Success = true, Reason = null, File = read.File };
        });
    }

    private static LeaseMutateResult MutateReject(string reason) => new() { Success = false, Reason = reason, File = null };

    // ============================================================

    /// <summary>
    /// **[P50 复核·批次四十九]** 「正式文件已确认不存在」的 Absent 结果（含**诊断性**残件留痕探测）：
    /// 状态**只**由「正式文件不存在」决定（到达此处的必要条件＝读取抛 `FileNotFoundException`／
    /// `DirectoryNotFoundException`，即目录可读或不存在）。
    /// **[第五轮会诊阻断项处置]** 残件探测**失败**（拒绝访问/争用耗尽）**不得**折成「无残件」：
    /// `TryAcquire` 只在 `UncertainResidue == true` 时拒 `residue_uncertain` ⇒ 折成 false 是 **fail-open**
    /// （未能排除崩窗残件却仍可获取新租约，绕过 `QuarantineResidues`）。故此时按**存在未决残件**保守处理
    /// （`UncertainResidue = true` ＋ 明细说明「残件目录不可枚举」），须人工/隔离对账后方可获取。
    /// </summary>
    private LeaseReadResult ReadAbsentWithResidueProbe()
    {
        string? detail = null;
        var residueUnknown = false;
        try
        {
            var residue = Directory.EnumerateFiles(_configDir)
                .Where(p => IsLeaseResidueFileName(Path.GetFileName(p)))
                .ToList();
            if (residue.Count > 0)
                detail = "正式文件不存在；发现残留临时文件（残件按无正式文件处理，不采用）："


## source: MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs L60-L155 SHA256=969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4
Queue identity, generation and high-water policy.
/// <summary>
/// 槲寄生 · R5 批次 6：**本地持久等待**的纯函数部分（登记判定、选择、清理、幂等标识）。
/// 依据 owner 裁决 B.1 与批次 4 的 `RunningOccupancyArbiter`：只有"低优先级本地等待"才入队；
/// 可抢占走抢占流程（不入队）、未知/占用者不可核验保守停驻（不入队、零发送）、空闲走常规准入（不入队）。
/// 纯函数契约：无副作用、不读时钟（时间由调用方传入）、不做 I/O。
/// </summary>
public static class LocalWaitQueuePolicy
{
    /// <summary>按相遇判定决定是否登记等待；等待登记**从不**授予发送权限。</summary>
    public static LocalWaitEnqueueDecision DecideEnqueue(RunningEncounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        return encounter.Verdict switch
        {
            RunningEncounterVerdict.WaitLocally => LocalWaitEnqueueDecision.Wait(
                "低优先级本地持久等待（未获准入前零发送，不进入 BGI 执行队列）"),
            RunningEncounterVerdict.PreemptNow => LocalWaitEnqueueDecision.NotApplicable(
                "可抢占：走抢占流程（入队不适用）"),
            RunningEncounterVerdict.ProceedIdle => LocalWaitEnqueueDecision.NotApplicable(
                "已确认空闲：走常规准入（入队不适用）"),
            _ => LocalWaitEnqueueDecision.NotApplicable(encounter.Reason),
        };
    }

    /// <summary>由稳定身份确定性派生等待项标识（同身份 ⇒ 同标识 ⇒ 幂等登记）。</summary>
    public static string DeriveItemId(string stableIdentity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentity ?? string.Empty));
        return "wait-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 从等待集合里挑下一个执行者：复用批次 4 的选择规则（最高级→级别→优先级→有值时刻先于 null→身份→候选号），
    /// 但**只考虑** <see cref="LocalWaitItemState.Waiting"/> 项。空集/无 Waiting 项 ⇒ null。
    /// 注意：返回值只表示"下一个应重新比较的对象"，**不含**发送许可——仍需走完整准入。
    ///
    /// **[批次 16／D2] 前置就绪不再由过期布尔承载**：就绪**只**由调用方注入的**只读 evaluator** 求得
    /// （见带 <see cref="PrerequisiteEvaluator"/> 的重载）。
    ///
    /// **[批次 16／D2] 旧签名 `SelectNext(items)` 已**废除**，但为满足批约束「冻结合同只允许纯加法」而**保留公开签名**：
    /// 见本类中标注 <c>[Obsolete(error: true)]</c> 的同名重载（调用即抛异常，**不**实现旧语义）。
    /// 保留仅为「签名集合不做减法」；**不**表示 owner 对该重载另有裁决——owner 裁决原文未提及本重载。
    /// **旧语义（缺前置引用 ⇒ 视为已就绪）绝不复活**——它正是 §24.106 C4 的恒 true 硬编码缺陷。
    /// 缺引用／缺求值器**一律不可判定 ⇒ 不参选**；需要选择等待项的调用方**必须**显式注入只读求值器，
    /// 且选择结果仍**只**表示「下一个应重新走完整准入的对象」（不含发送许可）。
    /// </summary>
    /// <remarks>
    /// **[批次 16／D2] 逐步语义（只读）**：
    /// **①保守默认**：<paramref name="evaluator"/> 为 null、或缺引用、或求值器给出未就绪／不可判定
    /// ⇒ 该项不参选（不得当作已就绪，也不得当作「空闲」而抢发）。
    /// **②`ScheduledAt` 仍只参与排序**：未来时刻不阻止参选，也不构成就绪证明。
    /// **③返回值只表示「下一个应重新走完整准入的对象」**，不含发送许可。
    /// **④只读求值视图重载**：<see cref="SelectNextView"/> 与「发送前再次验算」复用**同一**求值结果类型。
    /// </remarks>
    public static LocalWaitItem? SelectNext(IReadOnlyList<LocalWaitItem>? items, PrerequisiteEvaluator? evaluator)
        => SelectNextView(items, ToReadOnlyEvaluator(evaluator));

    /// <summary>
    /// **[批次 16／D2] 旧签名 `SelectNext(items)` 已**废除**，但为满足「冻结合同只允许纯加法」而**保留公开签名**。**
    ///
    /// 保留原因：批约束要求冻结合同**不得做减法**（既有公开签名的删除须 owner 另裁为合同例外）；
    /// 直接删除公开静态方法即构成一次签名减法。故以 <see cref="ObsoleteAttribute"/>（<c>error: true</c>）
    /// **编译期**封死（任何源码调用点立即编译失败），并在**运行期**（反射／旧二进制调用）抛出明确异常。
    ///
    /// **绝不实现旧语义**：旧实现把前置就绪硬编码为 <c>true</c>（§24.106 C4 的恒 true 缺陷）。
    /// 因此本重载**没有任何**「缺引用即视为已就绪」的退回路径——它只会抛异常，要求调用方显式注入只读 evaluator。
    /// </summary>
    [Obsolete("批次 16／D2：`SelectNext(items)` 已废除（前置就绪改由注入的只读 evaluator 求得）。"
        + "请改用 `SelectNext(items, evaluator)`；本重载仅为保留二进制入口而存在，调用即抛异常。", error: true)]
    public static LocalWaitItem? SelectNext(IReadOnlyList<LocalWaitItem>? items)
        => throw new NotSupportedException(
            "批次 16／D2：`SelectNext(items)` 已废除——禁止「缺前置引用 ⇒ 视为已就绪」的旧语义"
            + "（§24.106 C4 恒 true 缺陷）。请改用 `SelectNext(items, evaluator)` 显式注入只读求值器"
            + "（缺引用／缺求值器一律不可判定 ⇒ 不参选）。");

    /// <summary>
    /// **[批次 16／D2] 选择下一个候选（只读求值视图重载）。**
    /// 与 <see cref="SelectNext(IReadOnlyList{LocalWaitItem}, PrerequisiteEvaluator)"/> 同语义；
    /// 提供视图重载以便「发送前再次验算」与选择阶段复用**同一**求值结果类型。
    /// </summary>
    internal static LocalWaitItem? SelectNextView(IReadOnlyList<LocalWaitItem>? items, Func<LocalWaitItem, PrerequisiteReadiness>? evaluator)
    {
        var waiting = (items ?? []).Where(i => i.State == LocalWaitItemState.Waiting).ToList();
        if (waiting.Count == 0) return null;
        var next = RunningOccupancyArbiter.SelectNextFromWaitSet(
            waiting.Select(i => ToWaitingFacts(i, evaluator)).ToList());
        return next is null ? null : waiting.FirstOrDefault(i => i.ItemId == next.CandidateId);
    }

    /// <summary>
    /// **[批次 16／D2] 只读逐项求值入口**（选择阶段与**发送前再次验算**共用）。
    /// **纯函数**：不改动任何等待项、不读时钟、不做 I/O、不产生发送。
    /// 非 <see cref="LocalWaitItemState.Waiting"/> 项**不参与**（取消项不复活）。
    /// </summary>
    public static List<LocalWaitPrerequisiteEvaluation> EvaluatePrerequisites(
        IReadOnlyList<LocalWaitItem>? items, PrerequisiteEvaluator? evaluator)


## source: MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs L55-L145 SHA256=59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0
Canonical successor identity translation and rejection rules.
///
/// **C4②（owner 裁决 D-E3=(c)）**：合同前存量项（缺 <c>AdmissionIdentity</c>/<c>CandidateId</c> 绑定）
/// 在 <see cref="Translate"/> 得到 <see cref="LocalWaitIdentityTranslationResult.LegacyPreContract"/>
/// 显式标注＝**永不参选**；零新通道、零新工具、不可经 Upsert 原地补全。
/// </summary>
public static class LocalWaitIdentityTranslation
{
    /// <summary>
    /// 由出现身份各字段组成**准入面稳定身份与候选号**（登记时点的身份翻译入口）。
    /// **单一权威**：组成与派生只委托 <c>ArbitrationOrdering.BuildStableIdentity</c>／
    /// <c>ArbitrationOrdering.DeriveCandidateId</c>（编码转义、整数越界响亮拒绝等规则全在权威侧，
    /// 本层不复制）。namespace/triggerOccurrenceId 的**取值口径**由 <c>TaskCenterHost.BuildSuccessorIdentityCandidate</c>
    /// （共享权威工厂）定义——successor 提交路径与等待登记共用该工厂，两处同改由锚定夹具
    /// （SuccessorCandidateComposition_AnchoredToSharedFactory）机械保证。
    /// </summary>
    public static (string AdmissionIdentity, string CandidateId) BuildAdmissionIdentity(
        ArbitrationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var stable = ArbitrationOrdering.BuildStableIdentity(candidate);
        return (stable, ArbitrationOrdering.DeriveCandidateId(stable));
    }

    /// <summary>
    /// **队列项身份翻译**（C3 消费侧前置合同；纯函数）：
    /// ①队列本地自洽——<c>ItemId == LocalWaitQueuePolicy.DeriveItemId(StableIdentity)</c>（ordinal）；
    /// ②准入绑定——<c>AdmissionIdentity</c> 非空且 <c>CandidateId == DeriveCandidateId(AdmissionIdentity)</c>；
    /// ③合同前存量（②缺）⇒ <see cref="LocalWaitIdentityTranslationResult.LegacyPreContract"/> 标注（C4②）。
    /// **失败不是异常**：返回不通过结果＋结构化原因（调用方据此不参选/响亮上报）。
    /// </summary>
    public static LocalWaitIdentityTranslationResult Translate(LocalWaitItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // ①队列本地自洽（与 D3 产出侧校验同口径的自洽前置）。
        if (!string.Equals(item.ItemId, LocalWaitQueuePolicy.DeriveItemId(item.StableIdentity), StringComparison.Ordinal))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "队列本地身份自洽破坏：ItemId ≠ DeriveItemId(StableIdentity)（"
                    + "这不是合同前存量，而是载荷被改写或身份漂移——不得参选）。",
            };
        }

        // ②合同前存量（C4②，owner 裁决 D-E3=(c)）：缺准入绑定 ⇒ 永不参选。
        if (string.IsNullOrWhiteSpace(item.AdmissionIdentity))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = true,
                Reason = "C4② 合同前存量：缺准入面身份绑定（版本 1/2 时期载荷）——"
                    + "登记例外＝永不参选（owner 裁决 D-E3=(c)）；不可经 Upsert 原地补全。",
            };
        }

        // ②'形状校验（Wave1 会诊 F4；R6-F4 加严；R8-F1 再加严）：裸 '|' 计数必为 8（编码转义保证
        // 字段载荷内无裸 '|' ⇒ 9 元组恰 8 个分隔符），且整数段（occurrence/loopIteration/attempt，
        // 第 7/8/9 段）必须为权威 EncodeInt 的 D8 定宽十进制——**逐字符全 ASCII 数字**判定
        // （[R8-F1] uint.TryParse 默认 NumberStyles.Integer 容许前导符号/空白，"+0000002"／" 0000002"
        // 会穿透；TryParse 已撤）。⇒「可翻译」即「规范形」，经权威构造的任何重入候选可逐字符对齐
        // （Ok ≠ 可重入的梯度被消除）。非规范形返回不通过结果——不得让 Ok 路径的切分/解析抛异常，
        // 违反「失败不是异常」合同。
        var shapeParts = item.AdmissionIdentity.Split('|');
        static bool IsD8(string s) => s.Length == 8 && s.All(c => c is >= '0' and <= '9');
        var integersCanonical = shapeParts.Length == 9
            && IsD8(shapeParts[6]) && IsD8(shapeParts[7]) && IsD8(shapeParts[8]);
        if (item.AdmissionIdentity.Count(c => c == '|') != 8 || !integersCanonical)
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "准入身份形状非法：裸 '|' 计数 ≠ 8 或整数段非 D8 定宽十进制"
                    + "（非规范 9 元组；编码合同 §2.1）——不参选，不得重入。",
            };
        }

        // ③准入绑定：候选号必须由持久化准入身份经权威派生得出。
        var expectedCandidateId = ArbitrationOrdering.DeriveCandidateId(item.AdmissionIdentity);
        if (!string.Equals(item.CandidateId, expectedCandidateId, StringComparison.Ordinal))
        {
            return new LocalWaitIdentityTranslationResult
            {
                Ok = false,
                LegacyPreContract = false,
                Reason = "准入绑定破坏：CandidateId ≠ DeriveCandidateId(AdmissionIdentity)"
                    + "（绑定缺失或被改写——不得带着坏绑定重入）。",
            };


## source: MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs L1-L65 SHA256=32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e
Run states and terminal state declaration.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>流程运行状态机（R4 分解 D12）。Unknown=结果不确定（需对账，不得当成功）。</summary>
public enum WorkflowRunState
{
    /// <summary>已计划，尚未开始推进。</summary>
    Planned,
    /// <summary>等待触发/条件（不持有叶子执行锁、不提交等待作业）。</summary>
    Waiting,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>已暂停（≠ 停止；暂停期间修订按节点边界生效）。</summary>
    Paused,
    /// <summary>流程成功边界到达（终态）。</summary>
    Succeeded,
    /// <summary>失败（终态；失败不被后续成功覆盖——聚合规则 D6）。</summary>
    Failed,
    /// <summary>被取消（终态）。</summary>
    Cancelled,
    /// <summary>中断（如助手重启；可恢复候选，非终态结论）。</summary>
    Interrupted,
    /// <summary>结果不确定（提交在飞/终态未证实；禁止自动重跑）。</summary>
    Unknown,
    /// <summary>收尾动作执行中（B5：成功边界先落盘收尾意图再执行；恢复扫描见此状态标 Unknown）。尾部追加，不改既有枚举数值。</summary>
    Completing,
    /// <summary>
    /// **[批次 20／Wave3／C11=(a)] 本地等待停驻**（D-E4=(a)：等待停驻**不复用** Waiting/Running）：
    /// 门面已给出确定零发送等待结论、等待项已登记（或登记被拒＝接线缺陷停驻），运行**无活动驱动**、
    /// 游标停在停驻出现、等待项就绪后**可显式重驱**（ResumeAsync 接受本状态）。尾部追加，不改既有枚举数值。
    /// </summary>
    LocalWaitParking,
}

/// <summary>提交意图状态（D11：提交意图先行持久化，回执丢失按账本+job 查询对账）。</summary>
public enum SubmitIntentState
{
    /// <summary>无提交。</summary>
    None,
    /// <summary>提交意图已记录（BGI 提交前）。</summary>
    IntentRecorded,
    /// <summary>已向 BGI 提交，受理回执未确认。</summary>
    Submitted,
    /// <summary>BGI 已受理（拿到 jobId）。</summary>
    Accepted,
    /// <summary>BGI 拒绝（响亮拒绝，带原因）。</summary>
    Rejected,
    /// <summary>门面给出确定零发送等待；本地停驻已原子落盘，可显式重评。</summary>
    LocalWaitDeferred,
}

/// <summary>节点游标：节点出现身份 = nodeId + 出现位置 + 循环轮次 + 尝试（§3.4 身份合同）。</summary>
public sealed class WorkflowNodeCursor
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = "";

    /// <summary>同名/同节点在流程中的出现序号（同名不等于同任务）。</summary>
    [JsonPropertyName("occurrence")]
    public int Occurrence { get; set; }

    /// <summary>循环轮次（结构性循环每轮递增）。</summary>
    [JsonPropertyName("loopIteration")]


## source: MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs L185-L305 SHA256=32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e
Persisted run/cursor/submission/terminal identity fields.
/// 固定幂等键、提交意图、受理 jobId、观察终态、待执行收尾、等待条件与触发身份。
/// </summary>
public sealed class WorkflowRunRecord
{
    /// <summary>运行身份（"run-" + 12 位十六进制，创建即固定）。</summary>
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    /// <summary>稳定流程身份（不靠文件名）。</summary>
    [JsonPropertyName("workflowId")]
    public string WorkflowId { get; set; } = "";

    /// <summary>启动时的流程定义修订号（锚点 2：起步快照拒绝 config_changed，不锁死流程定义）。</summary>
    [JsonPropertyName("workflowRevision")]
    public string WorkflowRevision { get; set; } = "";

    [JsonPropertyName("state")]
    public WorkflowRunState State { get; set; } = WorkflowRunState.Planned;

    /// <summary>当前节点游标（运行水位，C06 入口的落地处）。</summary>
    [JsonPropertyName("cursor")]
    public WorkflowNodeCursor? Cursor { get; set; }

    [JsonPropertyName("boundResource")]
    public BoundResourceRef? BoundResource { get; set; }

    [JsonPropertyName("target")]
    public ExecutionTargetRef? Target { get; set; }

    /// <summary>运行级固定身份键（创建即固定，绝不更换；单次提交键由它+节点出现身份确定性派生，B2）。</summary>
    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = "";

    /// <summary>线协议运行身份（R4.6 B1：BGI 合同要求 Guid——建 run 时生成 Guid "N" 串并持久化；与 RunId 并存，绝不更换）。</summary>
    [JsonPropertyName("wireRunId")]
    public string WireRunId { get; set; } = "";

    /// <summary>移交身份绑定清单（R4.9 §3 + ASTRA 二轮 B1：追加式多绑定——resume/幂等挂载追加新绑定，旧绑定永不替换，
    /// 旧意图的去重依据永存；台账查询键=IntentKey；面板/手工启动为空表）。</summary>
    [JsonPropertyName("handoffs")]
    public List<HandoffIdentity> Handoffs { get; set; } = [];

    /// <summary>
    /// **准入来源固定 Scope（G4a／AMD-1-5 第 2 类来源「启动移交」）**：移交**受理时捕获**的目标实例与完整 epoch
    /// （`bgi:{实例}:{epoch}`），随受理**同一次落盘**写入，随后**只比较、不重写**；后继节点提交据此**继承**来源
    /// （缺省/空 ⇒ 无固定来源 ⇒ 后继准入**不签发、不发送**，不得读当前 epoch 补造）。
    /// **面板启动**不需本字段（其来源权威＝租约中的流程登记操作）；**暂停续行/Interrupted 恢复**继承其原始来源。
    /// </summary>
    [JsonPropertyName("admissionSourceScope")]
    public string? AdmissionSourceScope { get; set; }

    /// <summary>当前提交（B2：一提交一身份——幂等键/意图/jobId/观察终态同属一个提交身份，不跨节点复用残留）。</summary>
    [JsonPropertyName("currentSubmission")]
    public WorkflowSubmission? CurrentSubmission { get; set; }

    /// <summary>本地等待/保持的类型化决定与不可变队列绑定；Hold 可无 binding，且无 binding 不可重建队列。</summary>
    [JsonPropertyName("localWaitDecision")]
    public LocalWaitDecisionRecord? LocalWaitDecision { get; set; }

    /// <summary>链尾已达（B3：游标 null 消歧——false+null=未开始/中断；true+null=全部节点已完成）。</summary>
    [JsonPropertyName("tailReached")]
    public bool TailReached { get; set; }

    /// <summary>顶层触发器已消费（B3：恢复后不重等已触发过的触发器；暂停在触发等待中保持 false 以便恢复重排）。</summary>
    [JsonPropertyName("triggerConsumed")]
    public bool TriggerConsumed { get; set; }

    /// <summary>已完成起点等待的循环轮次（B9：轮次等待统一在新一轮边界执行，恢复后不重等同一轮）。</summary>
    [JsonPropertyName("lastScheduledRoundWait")]
    public int LastScheduledRoundWait { get; set; }

    /// <summary>前置动作记录（R4.6 E2-8'：意图-受理-终态逐策略实例，发送前落盘；事实只信本清单，不信流程文件标记）。</summary>
    [JsonPropertyName("prerequisiteActions")]
    public List<PrerequisiteActionRecord> PrerequisiteActions { get; set; } = [];

    /// <summary>待执行收尾动作（R4.6 E3'/B8 结构化：动作身份+指纹+状态机 pending/submitted/executed/unknown；
    /// D10：失败/取消/未知/手动停止不得触发；已提交未确认事实持久保留，禁止补发）。
    /// 旧 pendingCompletionAction 字符串键退役（落入 ExtensionData 忽略——R4 开发期运行记录无存量包袱）。</summary>
    [JsonPropertyName("pendingCompletion")]
    public PendingCompletionRecord? PendingCompletion { get; set; }

    /// <summary>逐节点结果（追加；聚合判定只看此清单，不信终态单字段）。</summary>
    [JsonPropertyName("nodeOutcomes")]
    public List<WorkflowNodeOutcome> NodeOutcomes { get; set; } = [];
    [JsonPropertyName("wait")]
    public WaitStateRecord? Wait { get; set; }

    /// <summary>记录级单调修订（RunStore 内部乐观并发；每次成功写入 +1）。</summary>
    [JsonPropertyName("recordRevision")]
    public int RecordRevision { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>人类可读备注（恢复原因/拒绝原因等；不含敏感账号字段——脱敏纪律）。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>是否终态（Succeeded/Failed/Cancelled；Interrupted/Unknown 非终态结论）。</summary>
    [JsonIgnore]
    public bool IsTerminal => State is WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled;
}

/// <summary>
/// 单次提交记录（B2 会诊：一提交一身份）。
/// 幂等键按 runId+nodeId+出现序号+循环轮次+attempt 确定性派生（RunStore.DeriveSubmissionKey）：
/// 崩溃恢复后同一出现身份重算同一键（重复投递复用），不同节点/轮次/尝试绝不复用同一键。
/// 意图/jobId/观察终态同属本对象，禁止跨提交残留（旧字段跨节点复用导致恢复误判已废除）。
/// </summary>
public sealed class WorkflowSubmission
{
    /// <summary>确定性派生幂等键（"idem-" + 24 位十六进制）。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";



## source: MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs L365-L405 SHA256=32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e
Prerequisite responsibility enum used by Stop.
/// <summary>前置动作状态机（R4.6 E2-8'）：Intent（意图已落盘）→ Submitted（已受理在飞）→ Succeeded/Failed/Cancelled/Unknown。</summary>
public enum PrerequisiteActionState
{
    Intent,
    Submitted,
    Succeeded,
    Failed,
    Cancelled,
    Unknown,
}

/// <summary>
/// 前置动作记录（R4.6 E2-8'/B2）：键 = runId+nodeId+occurrence+loopIteration+attempt+策略索引+操作类型+账号标识+执行纪元。
/// 发送前落盘意图；恢复时对账（先查远端权威终态）；传输重投同键同载荷（expiresAtUtc 不刷新，I4），重投计数持久化。
/// </summary>
public sealed class PrerequisiteActionRecord
{
    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = "";
    [JsonPropertyName("occurrence")] public int Occurrence { get; set; }
    [JsonPropertyName("loopIteration")] public int LoopIteration { get; set; }
    [JsonPropertyName("attempt")] public int Attempt { get; set; }
    [JsonPropertyName("strategyIndex")] public int StrategyIndex { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>账号标识（SHA256 截断哈希，非可逆掩码——稳定可比对且碰撞隔离，原值不落记录；I3/四轮阻断 3）。</summary>
    [JsonPropertyName("accountKey")] public string? AccountKey { get; set; }

    /// <summary>执行纪元（"processId:startTicksUtc"；跨纪元事实不沿用，入 Unknown 对账——B2 窗口过期语义）。</summary>
    [JsonPropertyName("epoch")] public string? Epoch { get; set; }

    [JsonPropertyName("idempotencyKey")] public string IdempotencyKey { get; set; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";

    /// <summary>I4：重投沿用原载荷（含原 expiresAtUtc），指纹不变。</summary>
    [JsonPropertyName("expiresAtUtc")] public string ExpiresAtUtc { get; set; } = "";

    /// <summary>发送已尝试（发送窗口崩溃/取消时区分「确定未发送」与「可能已发送」；后者禁止当 Cancelled——四轮阻断 1）。</summary>
    [JsonPropertyName("sendAttempted")] public bool SendAttempted { get; set; }

    [JsonPropertyName("jobId")] public string? JobId { get; set; }
    [JsonPropertyName("state")] public PrerequisiteActionState State { get; set; } = PrerequisiteActionState.Intent;


## source: MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs L1-L155 SHA256=f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7
LocalWait context, immutable binding, decision and queue payload fields.
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>同步提交前等待裁定；ContinueAdmission 不是发送许可。</summary>
public enum LocalWaitDecisionKind
{
    ContinueAdmission = 0,
    Wait = 1,
    Hold = 2,
}

/// <summary>来源证明的种类；移交 runId 不伪装成面板 RequestIdentity。</summary>
public enum LocalWaitSourceKind
{
    PanelFlowRegistration = 0,
    StartupHandoff = 1,
}

/// <summary>不可变的同步等待判定输入，锁定一次运行与节点出现。</summary>
public sealed record WaitDecisionRequest
{
    public string RunId { get; init; } = "";
    public string WorkflowId { get; init; } = "";
    public string WorkflowRevision { get; init; } = "";
    public int RecordRevision { get; init; }
    public string? CursorNodeId { get; init; }
    public int CursorOccurrence { get; init; }
    public int CursorLoopIteration { get; init; }
    public string NodeId { get; init; } = "";
    public int SequenceIndex { get; init; }
    public int Occurrence { get; init; }
    public int LoopIteration { get; init; }
    public int Attempt { get; init; }
}

/// <summary>等待裁定观察到的运行、来源与候选身份。缺失来源或排序证明时允许持久化为 Hold。</summary>
public sealed record LocalWaitDecisionContext
{
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("workflowId")] public string WorkflowId { get; init; } = "";
    [JsonPropertyName("workflowRevision")] public string WorkflowRevision { get; init; } = "";
    [JsonPropertyName("recordRevision")] public int RecordRevision { get; init; }
    [JsonPropertyName("cursorNodeId")] public string? CursorNodeId { get; init; }
    [JsonPropertyName("cursorOccurrence")] public int CursorOccurrence { get; init; }
    [JsonPropertyName("cursorLoopIteration")] public int CursorLoopIteration { get; init; }
    [JsonPropertyName("nodeId")] public string NodeId { get; init; } = "";
    [JsonPropertyName("sequenceIndex")] public int SequenceIndex { get; init; }
    [JsonPropertyName("occurrence")] public int Occurrence { get; init; }
    [JsonPropertyName("loopIteration")] public int LoopIteration { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("sourceKind")] public LocalWaitSourceKind? SourceKind { get; init; }
    /// <summary>面板来源为真实 RequestIdentity；移交来源为 RunId，与 RequestIdentity 分列。</summary>
    [JsonPropertyName("sourceIdentity")] public string? SourceIdentity { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("candidateId")] public string? CandidateId { get; init; }
    [JsonPropertyName("admissionIdentity")] public string? AdmissionIdentity { get; init; }
    [JsonPropertyName("tier")] public ArbitrationTier? Tier { get; init; }
    [JsonPropertyName("priority")] public int? Priority { get; init; }
    [JsonPropertyName("isHoeingHighest")] public bool IsHoeingHighest { get; init; }
    [JsonPropertyName("hasTrustedRankingFacts")] public bool HasTrustedRankingFacts { get; init; }
}

/// <summary>等待队列不可变登记载荷＋其运行/来源绑定；可由运行记录重建队列项。</summary>
public sealed record LocalWaitBinding
{
    [JsonPropertyName("itemId")] public string ItemId { get; init; } = "";
    [JsonPropertyName("stableIdentity")] public string StableIdentity { get; init; } = "";
    [JsonPropertyName("candidateId")] public string CandidateId { get; init; } = "";
    [JsonPropertyName("admissionIdentity")] public string AdmissionIdentity { get; init; } = "";
    [JsonPropertyName("namespace")] public string Namespace { get; init; } = "";
    [JsonPropertyName("workflowId")] public string WorkflowId { get; init; } = "";
    [JsonPropertyName("sourceKind")] public LocalWaitSourceKind SourceKind { get; init; }
    [JsonPropertyName("sourceIdentity")] public string SourceIdentity { get; init; } = "";
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("workflowRevision")] public string WorkflowRevision { get; init; } = "";
    [JsonPropertyName("nodeId")] public string NodeId { get; init; } = "";
    [JsonPropertyName("sequenceIndex")] public int SequenceIndex { get; init; }
    [JsonPropertyName("recordRevision")] public int RecordRevision { get; init; }
    [JsonPropertyName("cursorNodeId")] public string? CursorNodeId { get; init; }
    [JsonPropertyName("cursorOccurrence")] public int CursorOccurrence { get; init; }
    [JsonPropertyName("cursorLoopIteration")] public int CursorLoopIteration { get; init; }
    [JsonPropertyName("occurrence")] public int Occurrence { get; init; }
    [JsonPropertyName("loopIteration")] public int LoopIteration { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("tier")] public ArbitrationTier Tier { get; init; }
    [JsonPropertyName("priority")] public int Priority { get; init; }
    [JsonPropertyName("isHoeingHighest")] public bool IsHoeingHighest { get; init; }
    [JsonPropertyName("scheduledAt")] public DateTimeOffset? ScheduledAt { get; init; }
    [JsonPropertyName("prerequisiteReference")] public string PrerequisiteReference { get; init; } = "";
    [JsonPropertyName("enqueuedAtUtc")] public DateTimeOffset EnqueuedAtUtc { get; init; }

    public LocalWaitItem ToQueueItem() => new()
    {
        ItemId = ItemId,
        StableIdentity = StableIdentity,
        CandidateId = CandidateId,
        AdmissionIdentity = AdmissionIdentity,
        Namespace = Namespace,
        WorkflowId = WorkflowId,
        Tier = Tier,
        Priority = Priority,
        IsHoeingHighest = IsHoeingHighest,
        ScheduledAt = ScheduledAt,
        PrerequisiteReference = PrerequisiteReference,
        EnqueuedAtUtc = EnqueuedAtUtc,
        State = LocalWaitItemState.Waiting,
        // Queue membership is not a proof of authority or a send permit.
        HasTrustedIdentity = false,
        Reason = "本地等待（未获准入前零发送）",
    };
}

/// <summary>运行台账中的等待裁定；Hold 可无完整 binding，但无 binding 时不得重建队列。</summary>
public sealed record LocalWaitDecisionRecord
{
    [JsonPropertyName("kind")] public LocalWaitDecisionKind Kind { get; init; }
    [JsonPropertyName("context")] public LocalWaitDecisionContext Context { get; init; } = new();
    [JsonPropertyName("binding")] public LocalWaitBinding? Binding { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("noSendConfirmed")] public bool NoSendConfirmed { get; init; }
}

/// <summary>本地等待项状态（等待不是 BGI 已入队、也不是已受理）。</summary>
public enum LocalWaitItemState
{
    Waiting = 0,
    Cancelled = 1,
}

/// <summary>
/// **本地持久等待项**（R5 批次 6，owner 裁决 B.1："低优先级新任务在调度器本地持久等待，
/// 当前结束后重新比较，等待期间不得抢发，也不得提前放入 BGI 执行队列"）。
/// 只承载调度意愿，**不含任何发送许可**：入队路径恒不产生发送。
/// </summary>
public sealed class LocalWaitItem
{
    /// <summary>幂等标识（由稳定身份确定性派生；同身份重复登记复用同一条）。</summary>
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    /// <summary>候选稳定身份（去重与审计的权威身份；**队列本地身份空间**）。</summary>
    [JsonPropertyName("stableIdentity")] public string StableIdentity { get; set; } = "";
    /// <summary>
    /// **[批次 20／C3] 准入面候选号**（与 <see cref="AdmissionIdentity"/> 绑定：必须等于
    /// <c>ArbitrationOrdering.DeriveCandidateId(AdmissionIdentity)</c>）。
    /// **合同前存量**（v1/v2 时期文件）缺 <see cref="AdmissionIdentity"/> ⇒ 登记例外：永不参选
    /// （**Legacy 判据＝AdmissionIdentity 缺失**（见其注释与 C4②）；本字段在存量上可空可非空——
    /// 绑定校验在 <c>LocalWaitIdentityTranslation.Translate</c>（批次 20／C3 翻译层）执行）。
    /// </summary>
    [JsonPropertyName("candidateId")] public string CandidateId { get; set; } = "";
    /// <summary>
    /// **[批次 20／C3/C4①] 准入面稳定身份（9 元组）**——由**权威组成函数**
    /// <c>ArbitrationOrdering.BuildStableIdentity</c> 在**登记时点**求得并持久化（身份翻译在登记时完成：
    /// 队列本地身份——D1 裸拼 4 段——**不足以**事后恢复准入候选，§24.115 IW-05／§24.117 C3）。
    /// **合同前存量（C4②，owner 裁决 D-E3=(c)）**：版本 1/2 时期文件**缺字段**，或**当前格式（v3/v4）


## source: MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs L365-L425 SHA256=f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1
Admission request states, terminal statuses and identities.
               && string.Equals(operation.TakeoverRef, claim.SubmissionIdentity, StringComparison.Ordinal)
               && (execution.Kind is ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled)
               && execution.ObservedAtUtc != default
               && string.Equals(execution.SubmissionIdentity, claim.SubmissionIdentity, StringComparison.Ordinal)
               && execution.SendSeq == claim.SendSeq
               && (string.IsNullOrEmpty(claim.JobId) || string.Equals(claim.JobId, execution.JobId, StringComparison.Ordinal));
    }
}

/// <summary>请求状态（§3.3 分类表——操作身份与处理状态分离；状态读取/迁移在权威串行边界内完成）。</summary>
public enum OperationRequestState
{
    /// <summary>已登记待裁决（入队快照前）。</summary>
    Queued,
    /// <summary>当轮裁决中。</summary>
    InRound,
    /// <summary>已占位（Submission 落盘、发送责任存续）。</summary>
    Granted,
    /// <summary>当次锁外发送在飞。</summary>
    Sending,
    /// <summary>发送结果未知/待对账（返回对账状态，不重发）。</summary>
    Reconciling,
    /// <summary>已受理（接管台账已持久化，返回既有结果）。</summary>
    Accepted,
    /// <summary>终局完成（关联执行权威终态+台账一致，返回既有结果）。</summary>
    TerminalCompleted,
    /// <summary>可重试拒绝（操作级白名单内，已关闭 Submission；预算/窗口内可由唯一重试者重新 Admit）。</summary>
    RetryableRejected,
    /// <summary>终局拒绝（不得静默返回成功）。</summary>
    TerminalRejected,
    /// <summary>未获选（当轮裁决完成即终局，含胜者引用与压制来源，不悬置不自动进入下一轮）。</summary>
    NotSelected,
}

/// <summary>操作受理结论（§4.1 五轮重要 1：仅两值，不设第三关闭依据；对账结论写入 evidenceSource）。</summary>
public enum OperationOutcome
{
    Accepted,
    Rejected,
}

// ============================================================
// R5.3 §24（B3 外部启动生命周期补全）——加法类型（锚点 6：加类型不改序列化框架）
// ============================================================

/// <summary>
/// **可信持久化操作类型**（§24.17）：由可信适配器在 Operation 创建时提供、与 Operation **同次原子发布**，后续不可改写。
/// 缺失/`Unknown`/与来源记录冲突 ⇒ **fail-closed**（不得按 `ResourceRef`、`RunId` 空值或运行快照猜测）。
/// </summary>
public enum OperationType
{
    /// <summary>流程登记（E1 启动）。</summary>
    FlowRegistration,
    /// <summary>节点执行（首节点/后继节点）。</summary>
    NodeExecution,
    /// <summary>外部启动（E3/E4/E5）。</summary>
    ExternalStart,
    /// <summary>恢复（E2）。</summary>
    Recovery,
    /// <summary>启动移交。</summary>
    Handoff,


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs L415-L510 SHA256=5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563
Same-process repark and restart-recovery/repark/new-run assertions.
    public async Task ResumeThatReparks_RemainsStoppableAndReleasesSameFlowForNewRun()
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost();

        var firstStart = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, firstStart.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var firstRun = Assert.Single(new RunStore(_runsDir).List());
        Assert.Equal(WorkflowRunState.LocalWaitParking, firstRun.State);
        var firstBinding = firstRun.LocalWaitDecision?.Binding;
        var firstContextRevision = firstRun.LocalWaitDecision!.Context.RecordRevision;
        Assert.NotNull(firstBinding);
        var firstQueueItem = Assert.Single(host.LocalWaitQueue.Load());
        Assert.Equal(LocalWaitItemState.Waiting, firstQueueItem.State);
        using var queueBeforeResume = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
        var generationHighWaterBeforeResume = queueBeforeResume.RootElement.GetProperty("generationHighWater").GetInt64();

        var resume = await host.ResumeRunAsync(firstRun.RunId);
        Assert.Equal(HostActionStatus.Registered, resume.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var reparking = new RunStore(_runsDir).Load(firstRun.RunId)!;
        Assert.Equal(WorkflowRunState.LocalWaitParking, reparking.State);
        Assert.Equal(firstRun.Cursor!.NodeId, reparking.Cursor!.NodeId);
        Assert.Equal(firstRun.Cursor.Occurrence, reparking.Cursor.Occurrence);
        Assert.Equal(firstRun.Cursor.LoopIteration, reparking.Cursor.LoopIteration);
        Assert.Equal(firstRun.Cursor.Attempt, reparking.Cursor.Attempt);
        Assert.Equal(firstRun.CurrentSubmission!.Key, reparking.CurrentSubmission!.Key);
        Assert.Equal(firstBinding, reparking.LocalWaitDecision!.Binding);
        Assert.True(reparking.LocalWaitDecision.Context.RecordRevision > firstContextRevision,
            "same-process Resume must refresh the decision snapshot while retaining the original queue binding");
        Assert.True(reparking.LocalWaitDecision.Binding!.RecordRevision <= reparking.LocalWaitDecision.Context.RecordRevision,
            "valid repark keeps the immutable queue binding at or before its refreshed decision snapshot");
        Assert.True(reparking.LocalWaitDecision.Context.RecordRevision < reparking.RecordRevision,
            "valid repark refreshes the decision snapshot before the run record advances again");
        var queueAfterRepark = Assert.Single(host.LocalWaitQueue.Load());
        Assert.Equal(firstBinding!.ItemId, queueAfterRepark.ItemId);
        Assert.Equal(firstQueueItem.Generation, queueAfterRepark.Generation);
        using var queueAfterReparkJson = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
        Assert.Equal(generationHighWaterBeforeResume,
            queueAfterReparkJson.RootElement.GetProperty("generationHighWater").GetInt64());

        var stop = host.RequestRunAction(firstRun.RunId, WorkflowRunAction.Stop);
        Assert.True(stop.Status == HostActionStatus.Effective, stop.Message);
        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(firstRun.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);

        var secondStart = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, secondStart.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var runs = new RunStore(_runsDir).List().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, run => run.RunId != firstRun.RunId
            && run.WorkflowId == workflowId && run.State == WorkflowRunState.LocalWaitParking);
    }

    [Fact]
    public async Task RestartRecoveryThenResumeReparksRetainsQueueGenerationAndAllowsExplicitStop()
    {
        var workflowId = SeedWorkflow();
        var firstHost = MakeWaitParkingHost();
        var firstStart = await firstHost.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, firstStart.Status);
        Assert.True(SpinWait.SpinUntil(() => !firstHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var originalRun = Assert.Single(firstHost.Runs.List());
        Assert.Equal(WorkflowRunState.LocalWaitParking, originalRun.State);
        var originalBinding = originalRun.LocalWaitDecision!.Binding!;
        var originalItem = Assert.Single(firstHost.LocalWaitQueue.Load());
        using var beforeRestartQueue = JsonDocument.Parse(File.ReadAllText(firstHost.LocalWaitQueue.FilePath));
        var originalHighWater = beforeRestartQueue.RootElement.GetProperty("generationHighWater").GetInt64();
        await firstHost.ShutdownAsync();

        var recoveredHost = MakeWaitParkingHost();
        try
        {
            recoveredHost.EnsureRecovered();
            var recovered = recoveredHost.Runs.Load(originalRun.RunId)!;
            Assert.Equal(WorkflowRunState.Interrupted, recovered.State);
            Assert.True(recovered.RecordRevision > originalRun.RecordRevision,
                "startup recovery must persist the LocalWaitParking to Interrupted transition before explicit Resume");
            Assert.Equal(originalBinding, recovered.LocalWaitDecision!.Binding);
            Assert.Equal(originalRun.Cursor!.NodeId, recovered.Cursor!.NodeId);
            Assert.Equal(originalRun.Cursor.Occurrence, recovered.Cursor.Occurrence);
            Assert.Equal(originalRun.Cursor.LoopIteration, recovered.Cursor.LoopIteration);
            Assert.Equal(originalRun.Cursor.Attempt, recovered.Cursor.Attempt);
            Assert.Equal(originalRun.LocalWaitDecision!.Context.RecordRevision,
                recovered.LocalWaitDecision.Context.RecordRevision);

            var resume = await recoveredHost.ResumeRunAsync(originalRun.RunId);
            Assert.Equal(HostActionStatus.Registered, resume.Status);
            Assert.True(SpinWait.SpinUntil(() => !recoveredHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
            var reparking = recoveredHost.Runs.Load(originalRun.RunId)!;
            Assert.Equal(WorkflowRunState.LocalWaitParking, reparking.State);
            Assert.Equal(originalBinding, reparking.LocalWaitDecision!.Binding);
            Assert.True(reparking.LocalWaitDecision.Context.RecordRevision > recovered.LocalWaitDecision!.Context.RecordRevision,
                "explicit Resume must refresh the decision snapshot while retaining the prior immutable binding");


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs L780-L880 SHA256=5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563
StartupHandoff borrowed-identity negative and valid positive byte-preservation tests.
        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json")));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsDecisionNotBoundToDeferredSubmission()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.CurrentSubmission!.Key = "different-submission-key";
        _runs.Update(run);
        var runBytes = File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json"));
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json")));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsMissingCanonicalAdmissionIdentity()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.LocalWaitDecision = run.LocalWaitDecision! with
        {
            Context = run.LocalWaitDecision.Context with { AdmissionIdentity = null },
        };
        _runs.Update(run);
        var runBytes = File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json"));
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json")));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
            createQueueItem: true, sourceKind: LocalWaitSourceKind.StartupHandoff);
        var otherRun = _runs.CreateRun(workflowId, "revision-sb21-4-other");
        var decision = run.LocalWaitDecision!;
        run.LocalWaitDecision = decision with
        {
            Binding = decision.Binding! with { SourceIdentity = otherRun.RunId },
            Context = decision.Context with { SourceIdentity = otherRun.RunId },
        };
        _runs.Update(run);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        Assert.Equal(WorkflowRunState.LocalWaitParking, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
            createQueueItem: true, sourceKind: LocalWaitSourceKind.StartupHandoff);
        Assert.Equal(run.RunId, run.LocalWaitDecision!.Binding!.SourceIdentity);
        Assert.Equal(run.RunId, run.LocalWaitDecision.Context.SourceIdentity);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, result.Status);
        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsCanonicalAdmissionIdentityFromWrongCandidateNamespace()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var decision = run.LocalWaitDecision!;
        var binding = decision.Binding!;
        var wrongCandidate = TaskCenterHost.BuildSuccessorIdentityCandidate(binding.Scope, run.WorkflowId,
            run.RunId, run.Cursor!.NodeId, run.Cursor.Occurrence, run.Cursor.LoopIteration, run.Cursor.Attempt);
        wrongCandidate.Namespace = "manual";
        var (wrongAdmissionIdentity, wrongCandidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(wrongCandidate);


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs L952-L1190 SHA256=5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563
Final-read identity/corrupt/read failures; queue publication and complete run-publish-failure/tombstone-preserving retry fixture.
    public void LocalWaitParkingStop_FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var runA = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runB = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var pathA = Path.Combine(_runsDir, runA.RunId + ".run.json");
        var pathB = Path.Combine(_runsDir, runB.RunId + ".run.json");
        var runABytes = File.ReadAllBytes(pathA);
        var runBBytes = File.ReadAllBytes(pathB);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var loadCount = 0;
        host.Runs.BeforeLoadForTest = id =>
        {
            if (id == runA.RunId && Interlocked.Increment(ref loadCount) == 2)
                File.WriteAllBytes(pathA, runBBytes);
        };

        HostActionResult? action = null;
        Exception? thrown;
        try { thrown = Record.Exception(() => action = host.RequestRunAction(runA.RunId, WorkflowRunAction.Stop)); }
        finally { host.Runs.BeforeLoadForTest = null; }

        Assert.Null(thrown);
        Assert.Equal(2, loadCount);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.NotEqual(runABytes, runBBytes);
        Assert.Equal(runBBytes, File.ReadAllBytes(pathA));
        Assert.Equal(runBBytes, File.ReadAllBytes(pathB));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_FinalReadMalformedRecordIsUnavailableAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(path);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var malformedBytes = System.Text.Encoding.UTF8.GetBytes("{ malformed-final-read");
        var loadCount = 0;
        host.Runs.BeforeLoadForTest = id =>
        {
            if (id == run.RunId && Interlocked.Increment(ref loadCount) == 2)
                File.WriteAllBytes(path, malformedBytes);
        };

        HostActionResult? action = null;
        Exception? thrown;
        try { thrown = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop)); }
        finally { host.Runs.BeforeLoadForTest = null; }

        Assert.Null(thrown);
        Assert.Equal(2, loadCount);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.Contains("停驻运行记录复核失败", action.Message);
        Assert.Equal(malformedBytes, File.ReadAllBytes(path));
        Assert.NotEqual(runBytes, malformedBytes);
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_FinalReadExceptionIsUnavailableAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(path);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var loadCount = 0;
        host.Runs.BeforeLoadForTest = id =>
        {
            if (id == run.RunId && Interlocked.Increment(ref loadCount) == 2)
                throw new IOException("SB21-4 injected final run read failure");
        };

        HostActionResult? action = null;
        Exception? thrown;
        try { thrown = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop)); }
        finally { host.Runs.BeforeLoadForTest = null; }

        Assert.Null(thrown);
        Assert.Equal(2, loadCount);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.Contains("停驻运行记录复核失败", action.Message);
        Assert.Equal(runBytes, File.ReadAllBytes(path));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsDecisionSnapshotOlderThanItsBindingAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var decision = run.LocalWaitDecision!;
        var binding = decision.Binding!;
        var newerBinding = binding with { RecordRevision = binding.RecordRevision + 1 };
        run.LocalWaitDecision = decision with
        {
            Binding = newerBinding,
        };
        host.Runs.Update(run);
        host.LocalWaitQueue.Upsert(newerBinding.ToQueueItem());

        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(path);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(runBytes, File.ReadAllBytes(path));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Theory]
    [InlineData("binding-zero")]
    [InlineData("decision-zero")]
    [InlineData("both-snapshots-future")]
    public void LocalWaitParkingStop_RejectsNonpositiveOrFutureBindingSnapshotsAndPreservesBytes(string invalidRevision)
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var decision = run.LocalWaitDecision!;
        var binding = decision.Binding!;
        switch (invalidRevision)
        {
            case "binding-zero":
                binding = binding with { RecordRevision = 0 };
                decision = decision with { Binding = binding };
                break;
            case "decision-zero":
                binding = binding with { RecordRevision = 0 };
                decision = decision with
                {
                    Binding = binding,
                    Context = decision.Context! with { RecordRevision = 0 },
                };
                break;
            case "both-snapshots-future":
                var futureRevision = run.RecordRevision + 3;
                binding = binding with { RecordRevision = futureRevision };
                decision = decision with
                {
                    Binding = binding,
                    Context = decision.Context! with { RecordRevision = futureRevision },
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidRevision), invalidRevision, null);
        }
        run.LocalWaitDecision = decision;
        host.Runs.Update(run);
        host.LocalWaitQueue.Upsert(binding.ToQueueItem());

        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_QueuePublishFailurePreservesBytesAndCanRetry()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        LocalWaitQueueStore.BeforeTemporaryFileWriteProbe = (tmp, _) =>
        {
            if (string.Equals(Path.GetDirectoryName(tmp), _runsDir, StringComparison.OrdinalIgnoreCase))
                throw new IOException("SB21-4 injected queue publish failure");
        };

        HostActionResult result;
        try { result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop); }
        finally { LocalWaitQueueStore.BeforeTemporaryFileWriteProbe = null; }

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);

        var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
        Assert.Equal(HostActionStatus.Effective, retry.Status);
        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        host.Runs.PublishFaultForTest = updated => updated.State == WorkflowRunState.Cancelled
            ? new IOException("SB21-4 injected run publish failure") : null;

        var failed = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, failed.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
        var tombstoneBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        host.Runs.PublishFaultForTest = null;

        var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, retry.Status);
        Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(tombstoneBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public async Task LocalWaitParkingStop_DuringResumeReservationIsUnavailable()
    {
        var workflowId = SeedWorkflow();
        using var enteredFactory = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var host = MakeWaitParkingHost(runnerFactoryEntered: call =>
        {
            if (call != 2) return;


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs L1180-L1455 SHA256=5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563
Run read failure and admission reconciliation timeout/overlap/retry/sibling evidence.
    }

    [Fact]
    public async Task LocalWaitParkingStop_DuringResumeReservationIsUnavailable()
    {
        var workflowId = SeedWorkflow();
        using var enteredFactory = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var host = MakeWaitParkingHost(runnerFactoryEntered: call =>
        {
            if (call != 2) return;
            enteredFactory.Set();
            if (!releaseFactory.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("resume factory barrier timed out");
        });
        var started = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, started.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var run = Assert.Single(host.Runs.List());

        var resumeTask = Task.Run(() => host.ResumeRunAsync(run.RunId));
        Assert.True(enteredFactory.Wait(TimeSpan.FromSeconds(5)), "resume runner factory did not reach reserved window");
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytesWhileReserved = File.ReadAllBytes(runPath);
        var queueBytesWhileReserved = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var stop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
        Assert.Equal(runBytesWhileReserved, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytesWhileReserved, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        releaseFactory.Set();
        var resume = await resumeTask;

        Assert.Equal(HostActionStatus.Unavailable, stop.Status);
        Assert.Contains("启动或驱动", stop.Message);
        Assert.Equal(HostActionStatus.Registered, resume.Status);
        await host.ShutdownAsync();
    }

    [Fact]
    public void LocalWaitParkingStop_RunReadFailureReturnsStructuredUnavailableAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("{ corrupted-run-record");
        File.WriteAllBytes(runPath, corruptBytes);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        HostActionResult? action = null;
        var exception = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));

        Assert.Null(exception);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.Equal(corruptBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public async Task LocalWaitParkingStop_TerminalizesAdmissionWiredAcceptedRegistration()
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true);
        try
        {
            var start = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(10)));
            var run = Assert.Single(host.Runs.List());
            Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
            var before = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read();
            Assert.Contains(before.File!.Handoff!.Operations!, op => op.RunBinding == run.RunId
                && op.RequestState == OperationRequestState.Accepted);

            var stop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, stop.Status);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try
                {
                    return new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read()
                        .File?.Handoff?.Operations?.Any(op => op.RunBinding == run.RunId
                            && op.RequestState == OperationRequestState.TerminalCompleted) == true;
                }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(5)), "accepted admission registration did not reach terminal state");
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_OverlappingRetriesWriteOneTerminalTransitionAndIsolateOtherRun()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            var otherWorkflowId = SeedWorkflow();
            var otherStart = await host.StartWorkflowAsync(otherWorkflowId);
            Assert.Equal(HostActionStatus.Registered, otherStart.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(otherWorkflowId), TimeSpan.FromSeconds(10)));
            var otherRun = Assert.Single(host.Runs.List().Where(candidate => candidate.WorkflowId == otherWorkflowId));

            var admissionPath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
            var store = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration"));
            var before = store.Read().File!;
            var target = Assert.Single(before.Handoff!.Operations!.Where(op => op.RunBinding == run.RunId));
            Assert.Equal(OperationRequestState.Accepted, target.RequestState);
            var unrelatedBefore = JsonSerializer.SerializeToUtf8Bytes(
                Assert.Single(before.Handoff.Operations, op => op.RunBinding == otherRun.RunId));

            host.AdmissionTerminalWriteFaultForTest = (_, _) => new IOException("seed pending admission retry");
            var initialStop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Unavailable, initialStop.Status);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Equal(OperationRequestState.Accepted,
                Assert.Single(ReadAdmissionOperationsForRun(run.RunId)).RequestState);
            host.AdmissionTerminalWriteFaultForTest = null;
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(50);

            var arrivalCount = 0;
            var reconciliationCompletionCount = 0;
            using var bothReconciliationsCompleted = new ManualResetEventSlim();
            host.AdmissionTerminalReconciliationCompletedForTest = () =>
            {
                if (Interlocked.Increment(ref reconciliationCompletionCount) >= 2)
                    bothReconciliationsCompleted.Set();
            };
            var terminalIdentityCalls = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var bothAtTerminalWrite = new Barrier(2);
            host.AdmissionTerminalResultForTest = identity =>
            {
                terminalIdentityCalls.Enqueue(identity);
                Interlocked.Increment(ref arrivalCount);
                if (!bothAtTerminalWrite.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("overlapping Stop calls did not reach the same terminal-write boundary");
                return null;
            };

            var first = Task.Run(() => host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));
            var timedOut = await first;
            Assert.Equal(HostActionStatus.Unavailable, timedOut.Status);
            Assert.Contains("再次执行 Stop 重试", timedOut.Message);

            var retry = await Task.Run(() => host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            Assert.True(bothReconciliationsCompleted.Wait(TimeSpan.FromSeconds(5)),
                "both the timed-out first worker and the retry reconciliation must exit before inspecting final bytes");
            host.AdmissionTerminalReconciliationCompletedForTest = null;
            Assert.True(reconciliationCompletionCount >= 2);
            var after = store.Read().File!;
            var terminal = Assert.Single(after.Handoff!.Operations!, op => op.RunBinding == run.RunId);
            Assert.Equal(OperationRequestState.TerminalCompleted, terminal.RequestState);
            Assert.Equal(unrelatedBefore, JsonSerializer.SerializeToUtf8Bytes(
                Assert.Single(after.Handoff.Operations, op => op.RunBinding == otherRun.RunId)));
            Assert.All(terminalIdentityCalls, identity => Assert.Equal(target.RequestIdentity, identity));
            Assert.Equal(before.Revision + 1, after.Revision);
            Assert.Equal(after.Revision, terminal.UpdatedRevision);
            Assert.Equal(2, arrivalCount);

            var completedLeaseBytes = File.ReadAllBytes(admissionPath);
            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, repeated.Status);
            Assert.Equal(completedLeaseBytes, File.ReadAllBytes(admissionPath));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_ReadTimeoutIsVisibleAndSameSessionStopRetriesToTerminal()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(60);
            host.AdmissionTerminalReadFaultForTest = _ => new IOException("injected admission read outage");

            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, first.Status);
            Assert.Contains("再次执行 Stop 重试", first.Message);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);

            host.AdmissionTerminalReadFaultForTest = null;
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromSeconds(2);
            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
            var terminalBytes = File.ReadAllBytes(leasePath);
            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, repeated.Status);
            Assert.Equal(terminalBytes, File.ReadAllBytes(leasePath));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_LogicalAdmissionRejectionRemainsVisibleAndCanRetry()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            host.AdmissionTerminalResultForTest = identity => AdmissionResult.Of(
                AdmissionResultKind.Error, "injected_logical_rejection", "terminal write refused", identity);

            var rejected = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, rejected.Status);
            Assert.Contains("再次执行 Stop 重试", rejected.Message);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);

            host.AdmissionTerminalResultForTest = null;
            var retried = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retried.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_WriteExhaustionProcessesSiblingAndRetryDoesNotRewriteTerminalSibling()
    {
        var loggerFailures = 0;
        var (host, _, run) = await StartAdmissionWiredParkedRun(message =>
        {
            if (!message.Contains("终局回写被拒", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref loggerFailures);
            throw new InvalidOperationException("injected terminal reconciliation logger failure");
        });
        try
        {
            // Recovery admission creates a second legitimate operation bound to this same parked run.
            var resumed = await host.ResumeRunAsync(run.RunId);
            Assert.Equal(HostActionStatus.Registered, resumed.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(run.WorkflowId), TimeSpan.FromSeconds(10)),
                "resume fixture must settle back into LocalWaitParking before explicit Stop begins");
            Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
            var accepted = ReadAdmissionOperationsForRun(run.RunId)
                .Where(op => op.RequestState == OperationRequestState.Accepted).OrderBy(op => op.RequestIdentity).ToList();
            Assert.True(accepted.Count >= 2, "the fixture must expose multiple Accepted registrations for one run");
            var failingIdentity = accepted[0].RequestIdentity;
            var writeAttempts = 0;
            host.AdmissionTerminalWriteFaultForTest = (identity, attempt) =>
            {
                if (!string.Equals(identity, failingIdentity, StringComparison.Ordinal)) return null;
                Interlocked.Increment(ref writeAttempts);
                return new IOException("injected sibling terminal write outage " + attempt);
            };

            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, first.Status);
            Assert.Equal(5, writeAttempts);
            Assert.Equal(1, loggerFailures);
            var afterPartial = ReadAdmissionOperationsForRun(run.RunId);
            var terminalSibling = Assert.Single(afterPartial.Where(op => op.RequestIdentity != failingIdentity));
            Assert.Equal(OperationRequestState.TerminalCompleted, terminalSibling.RequestState);
            Assert.Equal(OperationRequestState.Accepted, Assert.Single(afterPartial, op => op.RequestIdentity == failingIdentity).RequestState);
            var terminalRevision = terminalSibling.UpdatedRevision;
            var terminalUpdatedAt = terminalSibling.UpdatedAtUtc;

            host.AdmissionTerminalWriteFaultForTest = null;
            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            var afterRetry = ReadAdmissionOperationsForRun(run.RunId);
            Assert.All(afterRetry, op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            var unchangedSibling = Assert.Single(afterRetry, op => op.RequestIdentity != failingIdentity);
            Assert.Equal(terminalRevision, unchangedSibling.UpdatedRevision);
            Assert.Equal(terminalUpdatedAt, unchangedSibling.UpdatedAtUtc);
            var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
            var completedBytes = File.ReadAllBytes(leasePath);


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs L1460-L1600 SHA256=5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563
Recovery of pending admission terminalization and final-read/identity isolation.
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_PendingAdmissionTerminalizationCanRecoverAfterHostRestart()
    {
        var (firstHost, _, run) = await StartAdmissionWiredParkedRun();
        firstHost.AdmissionTerminalWriteFaultForTest = (_, _) => new IOException("injected persistent write outage");
        var first = firstHost.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
        Assert.Equal(HostActionStatus.Unavailable, first.Status);
        Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);
        await firstHost.ShutdownAsync();

        var restarted = MakeWaitParkingHost(admissionWired: true);
        try
        {
            var recovered = restarted.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, recovered.Status);
            Assert.Equal(WorkflowRunState.Cancelled, restarted.Runs.Load(run.RunId)!.State);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await restarted.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_FinalAdmissionReadFailureIsVisibleAndRetryCompletes()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            host.AdmissionTerminalReadFaultForTest = attempt => attempt == 2
                ? new IOException("injected post-write confirmation failure") : null;

            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, first.Status);
            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.TerminalCompleted);
            host.AdmissionTerminalReadFaultForTest = null;

            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("unsupported")]
    public async Task LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping(string failureKind)
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
        var originalLeaseBytes = File.ReadAllBytes(leasePath);
        byte[] unavailableLeaseBytes;
        if (failureKind == "unsupported")
        {
            var leaseDocument = JsonNode.Parse(originalLeaseBytes)!.AsObject();
            leaseDocument["version"] = ArbitrationLeaseStore.SupportedVersion + 1;
            unavailableLeaseBytes = JsonSerializer.SerializeToUtf8Bytes(leaseDocument);
        }
        else
        {
            unavailableLeaseBytes = System.Text.Encoding.UTF8.GetBytes("{ malformed admission bytes");
        }

        try
        {
            File.WriteAllBytes(leasePath, unavailableLeaseBytes);
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(60);

            var blocked = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, blocked.Status);
            Assert.Contains("再次执行 Stop 重试", blocked.Message);
            Assert.Equal(unavailableLeaseBytes, File.ReadAllBytes(leasePath));
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);

            File.WriteAllBytes(leasePath, originalLeaseBytes);
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromSeconds(2);
            var retried = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retried.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task PersistentHoldStopWithAdmissionWiredConfirmsNoMappingAndReachesCancelled()
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true);
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Hold, createQueueItem: false);

        try
        {
            var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, result.Status);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Empty(ReadAdmissionOperationsForRun(run.RunId));
            Assert.Empty(host.LocalWaitQueue.Load());
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_TerminalizesOnlyTheRegistrationBoundToItsRun()
    {
        var firstWorkflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true);
        try
        {
            var firstStart = await host.StartWorkflowAsync(firstWorkflowId);
            Assert.Equal(HostActionStatus.Registered, firstStart.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(firstWorkflowId), TimeSpan.FromSeconds(10)));
            var firstRun = Assert.Single(host.Runs.List().Where(run => run.WorkflowId == firstWorkflowId));
            var secondWorkflowId = SeedWorkflow();
            var secondStart = await host.StartWorkflowAsync(secondWorkflowId);
            Assert.Equal(HostActionStatus.Registered, secondStart.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(secondWorkflowId), TimeSpan.FromSeconds(10)));
            var secondRun = Assert.Single(host.Runs.List().Where(run => run.WorkflowId == secondWorkflowId));
            var secondOperationBefore = Assert.Single(ReadAdmissionOperationsForRun(secondRun.RunId));
            Assert.Equal(OperationRequestState.Accepted, secondOperationBefore.RequestState);
            var secondOperationBytes = JsonSerializer.SerializeToUtf8Bytes(secondOperationBefore);
            var terminalWriteAttempts = new System.Collections.Concurrent.ConcurrentQueue<string>();
            host.AdmissionTerminalResultForTest = identity =>
            {
                terminalWriteAttempts.Enqueue(identity);
                return null;
            };

            var stopped = host.RequestRunAction(firstRun.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, stopped.Status);
            Assert.All(ReadAdmissionOperationsForRun(firstRun.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            Assert.Equal(ReadAdmissionOperationsForRun(firstRun.RunId).Select(op => op.RequestIdentity), terminalWriteAttempts);


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs L24-L45 SHA256=ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35
ClaimSurface environment variable and guarded declaration vocabulary.
    private const string RegenerateEnvVar = "CLAIM_SURFACE_REGENERATE";

    /// <summary>承载状态／门禁／证据等级的声明关键词（保守枚举；新增需评审）。</summary>
    private const string ClaimPattern =
        "已验收|未验收|已接线|未接线|已交付|未闭合|已冻结|已完成|已闭合|保留门禁|已收口|未收口" +
        "|已闭环|未闭环|待验收|待实机|未执行|已执行|已实施|未实施|不得标记完成";

    private static readonly string[] GuardedDocs =
    {
        Path.Combine("Docs", "design", "onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md"),
        Path.Combine("Docs", "design", "onedragon-r5-3-external-start-lifecycle-2026-09-21.md"),
        Path.Combine("Docs", "design", "onedragon-r5-closure-audit-2026-09-22.md"),
        Path.Combine("Docs", "design", "onedragon-r5-owner-decisions-2026-09-24.md"),
        Path.Combine("Docs", "design", "onedragon-r5-handoff-2026-09-21.md"),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs L109-L143 SHA256=ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35
Regeneration and no-environment guard behavior.
    [Fact]
    public void DesignDocs_ClaimSurface_MatchesReviewedManifest()
    {
        var root = RepoRoot();
        var actual = ExtractClaimSurface(root);
        var manifestPath = ManifestPath(root);

        if (string.Equals(Environment.GetEnvironmentVariable(RegenerateEnvVar), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(manifestPath, Render(actual), new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(manifestPath),
            $"声明面清单缺失：{manifestPath}\\n首次生成：设 {RegenerateEnvVar}=1 运行本夹具，并把生成的清单连同改动一并提交。");

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(manifestPath))
            if (line.Trim().Length > 0) expected[Identity(line)] = line;

        var added = actual.Where(kv => !expected.ContainsKey(kv.Key)).ToList();
        var removed = expected.Where(kv => !actual.ContainsKey(kv.Key)).ToList();

        if (added.Count == 0 && removed.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("声明面发生变化——本改动**不得**援引 §17.4-A 第 1 条「措辞类豁免」，须按语义必改项走处置→回归→复会诊。");
        sb.AppendLine("若确认变化已评审，设 CLAIM_SURFACE_REGENERATE=1 重跑以更新清单，并把清单变更纳入本次提交评审。");
        sb.AppendLine($"新增/变更声明 {added.Count} 行：");
        foreach (var kv in added.Take(20)) sb.AppendLine("  + " + kv.Key + "  :: " + kv.Value);
        if (added.Count > 20) sb.AppendLine($"  …（其余 {added.Count - 20} 行从略）");
        sb.AppendLine($"移除声明 {removed.Count} 行：");
        foreach (var kv in removed.Take(20)) sb.AppendLine("  - " + kv.Key + "  :: " + kv.Value);
        if (removed.Count > 20) sb.AppendLine($"  …（其余 {removed.Count - 20} 行从略）");
        Assert.Fail(sb.ToString());


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt L1-L18 SHA256=bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e
Final generated ClaimSurface manifest and stable declarations.
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md0005270E844D1911F683543481FC16716601671F52809F6E09C64C5F98B9C6841| 任务中心根级触发器（`trigger.time*`） | RunStore 持久化 | `R54MechanismSchemaTests`；**引擎消费未接线**（§19.4） | **未闭合**，保留门禁 |
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md002BA9B048996078CEA46179D65012F64869DA755D64F9CC28347327CBFABEB41**结论（如实）**：R5.6 **仍未收口**；v3 在「普通入口持锁、实例内串行、RollingBack 续做」上有实质改善，但上述反例仍直接破坏原处置目标。组件**未接线**（无生产消费侧 ⇒ 不构成生产风险，也不得据此主张任何一致性/闸门成立）。真实引用/激活编排、助手侧元数据恢复、生产消费必经检查点与实机证据…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md01DB1F7BF526B9AD72ECED9B2A984DE189DFF10C090636AA4BDF99B016CBE8181| B-1 | 新增执行状态的旧消费方兼容未闭合 | §4.0 租约文件 version 升 2（旧消费方按 Unsupported 响亮拒绝，不依赖加法被忽略）；v1 向后读兼容；静止判定扩展=未决 Submission+已受理未终结台账+Pending 任一存在即非静止 |
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md08C42317C2D5FBEEE87B4199D8A3D15ABC14D0D5E8177A0F0B783FA30AF9629C1| **并存风险与未验收范围**的登记 | **并存防护验收**、旧直通路径（`start_bgi(args)` 等）的**覆盖或排除裁决** |
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md099221E11035F55CB9BC194C9CF660AB70A342404819A69C7E765CA9D3F4997F1| 3 | 并发首调者 + 外部进程仍持有租约时，复用检查 `if (_admission is not null)` 只在「接管证据成熟」后执行——先接管者持续心跳使后继者观察永不成形，后继者空转到接管观察预算耗尽后响亮失败 | 阻断（可用性） | 在接管观察等待循环内每轮先检查并复用已完成门面；补「外部持有租约 +…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md0B22C86C87C72CA776CD1EBAC6F5177EE6EF98452F601A8A8BA709114022820C1> - **迟到结果与旧对象回写**：旧轮结果**不得污染**新轮；Runner/sender 对同一 run 的更新须**按字段合并或版本守卫**，**不得**以旧对象整体覆盖已冻结字段与本轮 `jobId`。
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md0C208BE4F208FF007CE82BD0D8F9BCD0FC1BAC366356A029CE3D23A53AFE2B271- **仍欠（登记为启用前置）**：①**外部启动候选未提供可验证的载荷绑定**（宿主构造候选未设 `PayloadFingerprint`）⇒「同身份、不同实际启动参数」的续用**当前无法被拒**（候选指纹同为空的空串比较），且 `ProcessLocalContext`（含执行委托）在重新驱动 Queued 操作时…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md0F090D16F181844B49751EB1A5B164594ED2EB7F50253391B63AB0B9E3DA12D81**依赖**：AMD-1-8（§3.3 白名单补录，与本条**同组生效**——**AMD-1-8 必须实际包含 E1 准入层该条目**，不能仅由本条引用；该条目须识别「**原生占用**」并附**可验证的操作类型与占用分类**，避免把**托管占用、同流程冲突、宿主在飞预留**一并纳入；且 AMD-1-8 的**范围说明须…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md10F948A1FC226A8EF09ED4B4C99EEBD4E253727E13969A06D098CB37BBF4CBAF1按 §17.5：R5.6 **独立会诊序列**（迭代至无必改项）。本阶段**不**执行真实目录切换、**不**开门；R5.5 的动作准入残余与本节 R-8 项继续保留门禁。**时效说明（2026-09-21）**：本条记录 R5.6 组件层收口前的历史会诊归属；其后的剩余实现接线统一按 §17.5 的「R5.8 实现闭…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md1257A8597FD48FC2AAE357BE9D7B14A8A1ECA3144471E0DC9E7C9545058859A61| P28／P55／P57 | 部分处置（P28 正/负向夹具已存在，残余见其行）；P55/P57 未验收 | R5.8 实现闭合序列（施工方：恢复/票据负责人） | 各自门禁（**已接线 E1/E2 路径不受节点改道门保护**） | §17 原行＋§24.41-C#10 |
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md12B4701117DF1794AF5B8A5413FE5FA73F2C3556D22E7A51CCAE0B291DE0B6E01与「获准后不得追加抢占」冲突——冷启动归 R5.3（须带目标身份校验），接线态返回 `cold_start_required` 保守失败（未发送）；④未接线路径保持既有语义（6 次 + 抢占 + 裸拉起回退）。
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md165386521518831243C4F5E7E184B3D84FF5FD1CE9FA32565A15325ECD79D19E1### 21.9 会诊记录（第 4 轮：v4 判「仍有必改项」，R5.6 仍未收口）
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md1673069013C36F38D3B8336782EB9E7C4866C98EE43C3A4AAE192476D2C1A1021| §12 实施要点第 3 步 | `三态双向映射` 及其后 `RecordIntent` 纪律 | 待生效候选条款 | AMD-1-4／§13.10 A3 | 已完成原处去向标注 |
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md168710F0F207949DEEE640ECD68DD19D32303AB78137F948FD7D4BF5C1216E861> **[历史时点提示]** 本节初稿曾写「本节不得被读作依赖组已生效」，那是 §13.9 C 完成收口前的时点状态；**本批已完成依赖组生效登记**。现行状态以本节末「当前生效登记」与 §13.9 C 为准，前文「未生效/待完成」字样仅作历史记录。**`AMD-1-1` 的「错误解释撤回」仍属会诊纠错，单列，不计入规…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md17B9AF62A81034297BBCF12D203AFC45A7DF4FA3CFC015E1AAF2B7A0D28B49F31> 其中 **P1/P2/P3/P5 属设计或批次归属裁决**，须在 R5.8 验收单签署前明确处置（补齐或转为显式验收前置并保留门禁）。
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md18881881C6DFD142CBBCD7DC7DF93AE939C4345678FED3C89129175D9E94A0421| **S8b** | 同方法 ← `ExecuteResumeWithBusyRetryAsync` ← `ApplyPolicyTeardownAsync(Resume)` | 恢复执行副作用（**策略收尾来源**） | **未经恢复准入**（内部链直发，不经过 E2 准入面）——既有**未接入** | 保留既有行…
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md1A3F27F78C46DE6EBCD6E91E6C118F4EC81C0F64946DEC9AA6B88D7BBA25F4ED1> ③**不得**以「已移交 R5.8/R5.5」替代组件验收证据（§8）；④本清单**不是**完成声明——未闭环者一律保持门禁。
Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md1AA58E75FE0D58C0D7D8388252C32305C0D49D1680E6CCDC8DBB03F16A1EC0481**异常边界与回执纪律**：凡「受理已成立」（台账/运行记录原子落盘）之后的任何 await 或派发，异常都不得穿透为对外拒绝——一律保留 `Accepted` 并按可读状态如实描述（执行结果待核实），与 I1「区分已发送/已入队/已执行/终态、未知不得当作成功或空闲」同口径。**该纪律同样覆盖取消回调与日志委托**：…


## source: MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj L1-L32 SHA256=974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44
Assistant production project build configuration.
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Version>0.2.1</Version>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <ApplicationIcon>Assets\Images\logo.ico</ApplicationIcon>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="8.0.0" />
    <PackageReference Include="Hardcodet.NotifyIcon.Wpf" Version="1.1.0" />
    <PackageReference Include="System.Drawing.Common" Version="8.0.0" />
    <!-- 鏇存柊BGI锛氳В鍘?.7z 瀹夎鍖咃紙涓?BGI 涓诲伐绋嬪悓鐗堟湰锛屽叡浜?NuGet 缂撳瓨锛?-->
    <PackageReference Include="SharpCompress" Version="0.38.0" />
  </ItemGroup>

  <!-- 宓屽叆 HarmonyOS Sans SC 瀛椾綋锛堝厤璐瑰晢鐢紝鎺堟潈瑙?Assets/Fonts/HarmonyOS-LICENSE.txt锛夆啋 鍦嗘鼎灞忓箷瀛椾綋 -->
  <ItemGroup>
    <Resource Include="Assets\Fonts\HarmonyOS_Sans_SC_Regular.ttf" />
    <Resource Include="Assets\Fonts\HarmonyOS_Sans_SC_Medium.ttf" />
  </ItemGroup>

  <!-- 宓屽叆鍝佺墝 Logo 鍜屽師绁炰富棰樺浘鐗囩礌鏉愶紙娲捐挋 + 瑙掕壊澶村儚锛屾潵婧?enka.network 鍏紑绱犳潗锛?-->
  <ItemGroup>
    <Resource Include="Assets\Images\logo.png" />


## source: Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj L1-L32 SHA256=e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287
Assistant test project build configuration.
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    [A4.6] 助手侧纯逻辑单测工程（总计划 F10）。
    首批覆盖：BatchReconcileDecider（批次 reconcile 决策纯函数）/ OnlineHoeingBatch。
    引用 WPF 工程仅为取 Services 下的零依赖纯逻辑类；测试自身不碰 UI 线程。
  -->
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- 真实 7z 夹具（py7zr 生成）：4 个条目，含应被排除的 User/Tool 子目录文件 -->
    <None Include="TestData\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>


## current-review-result: _workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md L1-L57 SHA256=dd7c9fd6346e86ac202e29c6d013ddacd6f9fd4b50efcdb8b81b73b4fef2847e

# GPT R8 BO-13 最终复审报告

**模型：** gpt-6-astra  
**强度：** medium  
**模式：** review  
**调用次数：** 1  
**送审快照：** _workflow/sb21-4/review-packet-r8-final-20260928-v1/packet.md  
**工具返回：** reviewed_files 仅含该 packet.md；diff_included=false。该快照由 workflow 工具生成，packet.md 本身含全局 git status、相关未暂存/暂存差异及材料外说明。

## 总体裁决

BO-13 原始 R1 #1–#5 及包内列明的 R3/R4 IMPORTANT 扩展，均可在报告所列有限合同内裁为 CLOSED_AT_IMPORTANT。未发现该范围内新增的 MUST/IMPORTANT 产品缺陷。这是本轮技术裁决，不代表 owner 已接受，也不代表生产验收或 Wave3 残项清零。

仍有两项 SHOULD 文档问题：

1. R7 突变编号仍错序。R5.3 §24.124.3 将 M3/M4 分别描述为“恢复 revision／重启 repark Context”；实际 mutations-r7.json 是 M3＝repark Context，M4＝恢复 revision。应按实验 ID 修正对应关系；“六次实验、五种源码 mutant”的去重结论正确。
2. 当前交接仍混入旧时点表述。handoff“工作区与收口证据”仍写“当前状态仍为 R6 复审前”；R5.3、handoff 又将已有 R8 最终回归和 ClaimSurface 写成待执行。应同步为本次固定证据和最终裁决，防止后续误用。机械守卫通过不能消除这些语义矛盾。

以下仅依据提供的 packet 文本；我没有调用工具、执行命令、修改文件或独立核验 SHA/TRX。

## 原始 R1 项目

| 项目 | 原级裁决 | 支持证据与限定边界 |
|---|---|---|
| R1 #1 前置责任与零发送边界 | CLOSED_AT_IMPORTANT | Stop 专用谓词采用终态白名单：仅 Succeeded/Failed/Cancelled 放行，Intent/Submitted/Unknown 及枚举外值拒绝。IndependentlyRefusesEachUnresolvedPrerequisiteState、UnknownPrerequisiteRemainsUnresolvedForEachPersistedSendFact 比较 run/queue 原字节；已知终态正例及两个 Succeeded 历史 send/job 正例支持兼容行为。HasUnresolvedExternalFact 仍独立检查提交/收尾责任，恢复谓词未因本修复改变。只关闭持久状态分类下的 Stop 合同；不证明 producer 将真实责任正确标为终态，也不声称穷举所有历史字段组合。 |
| R1 #2 决策、身份与 revision 不一致 | CLOSED_AT_IMPORTANT | HasValidParkedDecision 核验 run/workflow/revision、cursor、Deferred submission 及派生 key、Context/binding 配对和规范候选身份；Wait 修订关系为 0 < binding ≤ context ≤ run。非法 revision、旧 Context、错误提交 key、缺失/错误规范身份，以及 StartupHandoff 借用另一 run 的负例有支持；合法 StartupHandoff 正例防止过度拒绝。恢复与同进程 repark 测试证明持久 Interrupted、revision 推进、Context 刷新、旧 binding 和 generation/HWM 保留，并能随后 Stop、新建另一 RunId。Panel 来源仅验证配对快照，不重新鉴权；Hold 允许缺来源/排序证明，但须通过公共 NoSend、cursor/submission 检查且无 binding。不是任意双快照篡改检测或任意流程修订矩阵证明。 |
| R1 #3 请求 runId 与正文错绑 | CLOSED_AT_IMPORTANT | 当前 TaskCenterHost.cs:380–650 已完整展示入口校验及锁内最终复读。最终对象的 body ID、状态、reservation/drive、外部责任、前置责任、decision/binding 校验均先于 LocalWaitQueue.Cancel。FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes 和 R8 M1 支持该关键 guard。字节合同是 Stop 不进一步修改被替换后的 A 文件、B 文件及队列；测试并没有把 A 恢复为替换前内容。关闭限停驻 Stop 路径，不推广为全部动作分支或最终读取后的任意外部写者防护。 |
| R1 #4 Admission 未终局对账 | CLOSED_AT_IMPORTANT | run 成功持久化后，Host 在 _gate 外调用显式终局对账；已经 Cancelled 的再次 Stop 可重试。accepted registration 正例、read timeout、逻辑拒绝、五次写失败、最终确认读失败、损坏/不支持版本和无映射 Hold 用例支持结果传播。允许 run 已 Cancelled 而 admission 暂为 Accepted，此时必须返回可观察的 Unavailable；故障解除后显式 Stop 收敛。永久故障期间不承诺自动成功。 |
| R1 #5 Queue/Run/read/reservation 故障窗口 | CLOSED_AT_IMPORTANT | queue 发布失败、run 发布失败、初读损坏、最终读取错绑/损坏/异常及 Resume reservation 均有展示的行为证据。完整 RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt 明确断言首次 Unavailable、run 原字节及停驻态保留、queue 已 Cancelled；解除故障后再次 Stop 为 Effective、run Cancelled、queue 全文件字节不变。只关闭这些具名故障接缝和确定交错；缺项分支及未展示的独立 queue I/O-read 故障覆盖不能被描述为同等行为验证。 |

完整顺序可以确认：

请求 ID → 初读及正文 ID → 进入 Host gate → 最终复读及正文 ID/状态 → reservation/drive → 外部责任 → 前置责任 → Wait/Hold 与绑定检查 → queue 精确取消 → run 持久化 → 退出 gate → 通知 → admission 对账

其中队列墓碑已经发布而 run 写入失败，不是零副作用失败。返回 Unavailable 并明确提示部分提交、允许重试，符合展示的有限合同。LocalWaitQueueStore.Cancel 先比较载荷，再对已有墓碑直接返回 AlreadyCancelled，因此同一匹配墓碑的重试不会调用 Persist；载荷已变则拒绝。

## R3/R4 IMPORTANT 扩展

| 扩展 | 原级裁决 | 当前对应证据 |
|---|---|---|
| R3 #1 同会话可观察重试、sibling 隔离 | CLOSED_AT_IMPORTANT | ReadTimeoutIsVisibleAndSameSessionStopRetriesToTerminal、LogicalAdmissionRejectionRemainsVisibleAndCanRetry、WriteExhaustionProcessesSiblingAndRetryDoesNotRewriteTerminalSibling。一个登记失败及 logger 抛错不阻断另一登记；重试保留已终局 sibling 的 revision/time；重复 Stop 保留完整 lease bytes。 |
| R3 #2 独立输入及失败分支证据缺口 | CLOSED_AT_IMPORTANT | 独立前置状态输入、最终读取错绑/损坏/异常、reservation-held 字节断言，加上本轮补齐的完整顺序及 run-publish-failure 测试，覆盖包内登记的缺证项。历史突变不重记为 R8 实验。 |
| R4 overlap／终局写幂等性 | CLOSED_AT_IMPORTANT | overlap 测试让超时 worker 与 retry 同时到达写边界，并等待两者 reconciliation 完成，再检查单次 lease revision、对应 UpdatedRevision、另一 run operation 序列化字节和重复 Stop 全 lease bytes。限同 Host、同 admission service 的确定交错。 |
| R4 identity／revision／恢复矩阵 | CLOSED_AT_IMPORTANT | 对应 R1 #2 的非法 revision、合法旧 binding、来源身份正反例及两类 repark 生命周期；不扩展为任意修订、任意外部 writer 或跨进程恢复证明。 |

Admission 的重启用例实际执行的是重建 Host 后显式 Stop；不能据此声称启动扫描已自动完成终局对账。MarkOperationTerminal 展示了服务互斥及最新 lease 内再次检查 Accepted；MutateCore 展示了 revision 更新和发布，但 WithLock 实现未展开，因此不将注释中的跨进程锁声明升级为本轮跨进程验证。

## R8 当前源码反向突变

- M1：testId 14b12c92-31c4-d84d-5129-de01fac6b561 在删除最终 body-ID guard 后，于测试第 977 行出现 Expected Unavailable / Actual Effective，命中目标状态断言。mutant 未执行到后续字节断言；字节保持由 baseline/restored 用例支持。
- M2：testId c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72 在重写已有墓碑后，于第 1179 行最终集合字节断言失败，直接识别了重试改写。两项均报告 baseline/build 成功、mutant build 成功而目标测试失败、恢复 SHA 等于原 SHA 且恢复测试成功。这是包内执行记录，不是我的独立复跑。

## 验证与范围边界

最终验证帧报告 BO-13 49/49、LocalWait 240/240、助手全量 1558 passed／2 skipped／0 failed。首次文档守卫失败被保留并解释。构建发生在最终文档措辞修正前，产品/测试源码 SHA 与当前一致，不能称为“文档修正后重新构建”。ClaimSurface regen/no-env 的稳定 SHA 支持清单一致性；testId 差集支持名称和结果集合比较，不证明测试正文未变。

本结论不证明跨文件原子事务、断电耐久、新 OS 进程、任意外部写者、真实 admission 服务或生产实机行为。材料外变更不随本审查获得验收或合并授权。BO-6/7 保持未启动及原 MUST/IMPORTANT，BO-8/9 保持原级冻结；BO-4、BO-10/12 不重开，所有生产门继续关闭。


## findings-closeout: _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/findings-closeout.md L1-L24 SHA256=93320947227693a9d62075f09e4a4f35c522a1b8f1e54684c6c5f845c36bc4d5

# SB21-4 BO-13 R8 原级处置记录

依据：独立会诊台账 R1–R8（累计 8/8；R2 已发失败请求保留计次）及 `_workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md`。R8 使用 GPT `gpt-6-astra` / medium；报告只复核其快照所含材料。按本 Goal 的用户指令，采纳 R8 对列明范围的技术建议，完成下列 IMPORTANT 原级验收；这不是生产运行验收。

## 五项 R1 IMPORTANT

| 项目 | 原级处置 | 验收证据与边界 |
|---|---|---|
| #1 前置责任和零发送边界 | 按原 IMPORTANT 关闭，限 Stop 专用持久状态分类 | 仅 Succeeded/Failed/Cancelled 放行；Intent/Submitted/Unknown 与未知枚举值拒绝。独立具名测试比较 run/queue 原字节。没有证明 producer 对真实责任的标记正确，也没有穷举全部历史字段。 |
| #2 决策身份、revision、恢复与 repark | 按原 IMPORTANT 关闭，限明确身份/修订矩阵 | 覆盖 run/workflow/revision、cursor、submission/key、Context/binding、规范身份、来源身份，合法 StartupHandoff 正例和借用身份负例；恢复/repark 保留旧 binding、generation/HWM，并允许后续 Stop 和新 RunId。不是任意快照篡改或跨进程恢复证明。 |
| #3 请求 RunId 与最终复读身份错绑 | 按原 IMPORTANT 关闭，限停驻 Stop 最终检查顺序 | 入口和 gate 内复读检查 body ID/state/reservation/drive/外部及前置责任/decision/binding，均先于首次 queue 副作用；目标突变删除最终 ID guard 后命中 Unavailable/Effective 断言。字节测试基线是注入替换后的 A/B/queue；Stop 不进一步修改这些字节，不证明恢复注入前 A 或抵御最终读后的任意 writer。 |
| #4 Admission 终局对账与同会话可观察重试 | 按原 IMPORTANT 关闭，限显式 Stop 及已具名错误/重试路径 | run 持久后显式对账；timeout/rejection/write exhaustion/final-read error 可观察为 Unavailable，故障解除后显式 Stop 重试；同 Host/service 并发重试和 sibling 隔离具名验证。永久故障期间不承诺自动成功；重启证据为重建 Host 后显式 Stop，不是启动扫描。 |
| #5 queue/run/read/reservation 故障窗口 | 按原 IMPORTANT 关闭，限已展示故障接缝 | 包括 queue 发布失败、run 发布失败、损坏/读错绑/最终读失败、reservation-held；run 发布失败保留 queue tombstone，重试令 run 终态化且 queue 全文件字节不变，目标突变命中最终字节断言。没有单独注入 queue 文件读取 I/O 异常，未展示分支不外推。 |

R3/R4 已登记 IMPORTANT 延伸依 R8 报告同样按原级有限合同关闭：R3 #1 同会话可观察重试与 sibling 隔离；R3 #2 独立输入、最终读取及 reservation 证据缺口；R4 overlap/终局写幂等；R4 identity/revision/recovery 矩阵。详细 test ID、具体断言和范围边界见 R8 报告。R8 未发现本范围内新 MUST/IMPORTANT。

## 两项 SHOULD 文档修正

- R7 mutation 映射改为 M3 `r7-m3-repark-refreshes-context`、M4 `r7-m4-recovery-revision-advancement`，与 `_workflow/sb21-4/review/r7-review-20260928-v1/mutations-r7.json` 一致；六项实验、五种源码 mutant 的去重数量不变。
- handoff 和 R5.3 去掉过时时点/待执行表述，改为 R8 最终证据、8/8 累计和当前有限合同闭环；handoff 中当前 HEAD 校正为复审前基线，最终提交由外部 active-ledger 记录。

## 仍冻结的 BO-11 项

BO-6：R19 MUST、R21 F4 IMPORTANT；BO-7：R21 F1/F2 MUST；BO-8：R29 IMPORTANT；BO-9：R34 F5 IMPORTANT。它们均未由本次 BO-13 复审关闭。BO-6/7 未启动，BO-8/9 保持原级冻结并交 owner；详见 `_workflow/sb21-4/scope-and-state-table.md`、R5.3 §24.124.4 与原始 obligation ledger。生产入口、真实 User、R5.8、E3/E4/E5、热键与 BGI 进程保持关闭。


## testid-diff-data: _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-closeout-final.json L1-L206 SHA256=bfa559e0aa33038bd237a93c99a097da4a5e06e8a961d94c705eb938ffd9fb89

{
  "schema_version": 1,
  "generated_from": "tools/mistletoe/workflow.py parse_trx/compare_trx; all execution rows retained",
  "comparisons": [
    {
      "name": "opening_to_final",
      "baseline_path": "_workflow/sb21-4/baseline/full/assistant-full-baseline.trx",
      "final_path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx",
      "baseline": {
        "total": 1511,
        "passed": 1509,
        "failed": 0,
        "skipped": 2
      },
      "final": {
        "total": 1560,
        "passed": 1558,
        "failed": 0,
        "skipped": 2
      },
      "shared": 1511,
      "added": 49,
      "removed": 0,
      "changed": 0,
      "unchanged": 1511,
      "added_ids": [
        "07fc34dd-5e13-1688-f973-796a9039381a",
        "0fe6e276-2320-ac40-58b3-3a84023b9b58",
        "10dfd457-b43a-77c1-5c41-6bea79edd369",
        "14164b6a-cc69-3859-eee8-0630c138ce28",
        "142ef5f1-b79f-c83a-cdce-c15299459e48",
        "14b12c92-31c4-d84d-5129-de01fac6b561",
        "1a9d4afa-5d09-4ef7-d1f7-d1751ee818e5",
        "24a61d65-808c-9cc5-0347-dd0f2c6c6cd7",
        "260d9a6e-4e9e-c464-e723-c4ae8e20eaf4",
        "31533669-b089-5e91-5472-6f0fb4eb5164",
        "366dac3f-517e-9e95-f338-7aa239a4b828",
        "3f66e0ac-bb20-f6f4-36d2-a485bd16d781",
        "48c80e2d-f80f-7938-0b67-bb1f367c0333",
        "4e406f2d-d06c-27d6-a5a7-da653232fd32",
        "55ce4aeb-02f6-5979-b443-9fdf6573f062",
        "5be5c01c-9731-04f1-8bcf-46268ec86057",
        "5c829a04-e5ac-38c9-c8ba-4c8bb35f4eeb",
        "5c9341a4-949f-1933-495a-c43d4ba5c9a2",
        "62d390bb-e91f-98d9-6e78-38ce05d3ea47",
        "63d96ba2-036e-8678-a572-0e466e6d1f6c",
        "6b69710a-6a3d-d19b-f3e0-1076ab15c90d",
        "6f423c16-ea04-52a8-ea4d-024f1cef2f8e",
        "76837a78-5830-d89e-daab-11ea2c45307b",
        "7d8ee91a-7ade-ce53-f3d7-cf37a2109765",
        "7ff2dc0d-a3c3-d14e-a619-fee985a3deca",
        "895b4929-9a8e-afc8-fceb-b7fa9a14a966",
        "8983db62-6f34-8d98-20b8-01879036554a",
        "89e894ed-a5da-b5fc-6d81-e4bc88c6a1e7",
        "8eb055eb-1c6d-c932-2ffc-9ca3c8663273",
        "8fcfdb85-9109-4d02-190c-c72e325cfec1",
        "91b00edf-e1b8-ad0d-32b7-a6c7f9fc52d9",
        "94549705-4351-9051-87ca-29388f0104d4",
        "b8307bf2-0e31-dc11-2b09-40e882aa73c9",
        "ba940fb9-1e4f-4efb-0b9a-d5d378524b13",
        "bd238e74-eb5f-8311-1e3b-10aca683f093",
        "c5402bcc-6cd0-4d51-121e-43538c795310",
        "c703f675-50dc-6a8d-b774-e4b778d39cec",
        "c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72",
        "d2570c26-41b8-dc60-a14d-6941b37e3b7e",
        "d7427258-34d9-44e9-73c5-630308a8b138",
        "de8c793b-100e-3eed-e813-a1cbdccd67ee",
        "e080bf87-a185-708c-b158-83fac382b89c",
        "e3c72cf3-6a64-a2ed-8c8f-69dfa089e589",
        "ea2159f2-e93b-4a94-f8bd-3fd37b9298c5",
        "ea61ca6f-f2dc-6ce2-c488-53992bb56fec",
        "f5a5c886-4f07-a6a9-729d-44ccd2ca053a",
        "f94a18d9-2212-e986-d735-e8d6517f51c7",
        "fab66767-82dc-d7ff-0214-48dc81671e1d",
        "ff22b4ef-785e-c033-fd9d-4429492b3ac6"
      ],
      "removed_ids": [],
      "changed_ids": [],
      "identity_basis": "testId; provider changes require manual reconciliation"
    },
    {
      "name": "r6_to_final",
      "baseline_path": "_workflow/sb21-4/review/r6-review-validation-final/assistant-full/assistant-full-r6.trx",
      "final_path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx",
      "baseline": {
        "total": 1558,
        "passed": 1556,
        "failed": 0,
        "skipped": 2
      },
      "final": {
        "total": 1560,
        "passed": 1558,
        "failed": 0,
        "skipped": 2
      },
      "shared": 1558,
      "added": 2,
      "removed": 0,
      "changed": 0,
      "unchanged": 1558,
      "added_ids": [
        "76837a78-5830-d89e-daab-11ea2c45307b",
        "ea2159f2-e93b-4a94-f8bd-3fd37b9298c5"
      ],
      "removed_ids": [],
      "changed_ids": [],
      "identity_basis": "testId; provider changes require manual reconciliation"
    },
    {
      "name": "r7_to_final",
      "baseline_path": "_workflow/sb21-4/review/r7-review-20260928-v1/postbuild-regression/assistant-full/assistant-full-final.trx",
      "final_path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx",
      "baseline": {
        "total": 1560,
        "passed": 1558,
        "failed": 0,
        "skipped": 2
      },
      "final": {
        "total": 1560,
        "passed": 1558,
        "failed": 0,
        "skipped": 2
      },
      "shared": 1560,
      "added": 0,
      "removed": 0,
      "changed": 0,
      "unchanged": 1560,
      "added_ids": [],
      "removed_ids": [],
      "changed_ids": [],
      "identity_basis": "testId; provider changes require manual reconciliation"
    },
    {
      "name": "r8_pre_to_final",
      "baseline_path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/assistant-full-r8-final/assistant-full-r8-final.trx",
      "final_path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx",
      "baseline": {
        "total": 1560,
        "passed": 1558,
        "failed": 0,
        "skipped": 2
      },
      "final": {
        "total": 1560,
        "passed": 1558,
        "failed": 0,
        "skipped": 2
      },
      "shared": 1560,
      "added": 0,
      "removed": 0,
      "changed": 0,
      "unchanged": 1560,
      "added_ids": [],
      "removed_ids": [],
      "changed_ids": [],
      "identity_basis": "testId; provider changes require manual reconciliation"
    },
    {
      "name": "localwait_exact240_pre_to_final",
      "baseline_path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/localwait-r8-final/localwait-r8-final.trx",
      "final_path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/localwait/localwait.trx",
      "baseline": {
        "total": 240,
        "passed": 240,
        "failed": 0,
        "skipped": 0
      },
      "final": {
        "total": 240,
        "passed": 240,
        "failed": 0,
        "skipped": 0
      },
      "shared": 240,
      "added": 0,
      "removed": 0,
      "changed": 0,
      "unchanged": 240,
      "added_ids": [],
      "removed_ids": [],
      "changed_ids": [],
      "identity_basis": "testId; provider changes require manual reconciliation"
    }
  ],
  "tests_added_since_r6": [
    {
      "test_id": "ea2159f2-e93b-4a94-f8bd-3fd37b9298c5",
      "name": "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes",
      "class": "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests",
      "method": "LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes",
      "outcome": "Passed"
    },
    {
      "test_id": "76837a78-5830-d89e-daab-11ea2c45307b",
      "name": "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests.LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding",
      "class": "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.LocalWaitFinalizationContractTests",
      "method": "LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding",
      "outcome": "Passed"
    }
  ],
  "interpretation": "Opening-to-final adds 49 IDs accumulated over SB21-4. R6-to-final adds the two named StartupHandoff identity tests. R7 and R8 pre-review have no ID/definition/outcome changes. Exact LocalWait remains 240/240. Broad 284-test runs are supplementary and not used for the exact-set delta."
}


## parallel-delivery-status: _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/parallel-task-status-closeout.json L1-L631 SHA256=cca45d814d7a750ce1b1299bda2d98f7db6d0066b1caffc54ccd3dd231ff2faf

{
  "schema_version": 1,
  "checked_at": "2026-09-28 final closeout r4",
  "thread_status_source": "Codex list_threads/read_thread snapshot; notLoaded is not treated as completed or idle",
  "delivery_scan": {
    "path": "_workflow/sb21-4/deliveries/final-closeout-r4.json",
    "exit_code": 0,
    "queue": 12,
    "unregistered_reports": 0,
    "errors": 0,
    "ok": true,
    "task_status_note": "not inferred from Git; executor queried available thread status snapshots"
  },
  "deliveries": [
    {
      "id": "r56-migration-audit",
      "delivery_state": "independent-delivered",
      "integration_state": "pending",
      "target_batch": "R5.6 主线迁移集成批",
      "thread_id": "01a0e060-f824-7bb2-8490-d53b6e6dd88d",
      "thread_snapshot": {
        "status": "notLoaded",
        "latest_turn": "completed",
        "completed_at": 1790475379
      },
      "head": "c11cb45f6de2436b46c58b91d47f2c9768132ef3",
      "commits": [
        "24928d294",
        "97d05b931",
        "ad248944e",
        "c11cb45f6"
      ],
      "report": "_r56_parallel/report.md",
      "files": [
        {
          "path": "_r56_parallel/report.md",
          "sha256": "044A62D06DEE49FCDB8E87FAD6ADB30521E03A276217785FACEB2EF817369D97"
        },
        {
          "path": "_r56_parallel/acceptance.md",
          "sha256": "377FC50C072818764FA178BAFE14CF4F256464368165B41982EE378EB77055AE"
        }
      ],
      "blockers": [
        "真实引用/激活元数据、静止窗口及生产检查点尚未完成；真实 User 门仍关闭"
      ]
    },
    {
      "id": "r61-distribution-candidate",
      "delivery_state": "independent-delivered",
      "integration_state": "blocked",
      "target_batch": "R6.1 正式分发与互用集成批",
      "thread_id": "01a0e0ce-c94e-76f1-9646-7dd9c4772a6f",
      "thread_snapshot": {
        "status": "notLoaded",
        "latest_turn": "interrupted",
        "completed_at": 1790482237,
        "note": "last turn asks about parallel eligibility; registered delivery is blocked and unconsumed"
      },
      "head": "28d8c764f5e1ae6ae0fee5a1d99a2e41d107d964",
      "commits": [
        "252b1258955eda5eebd87d43fa966896f3a2870f",
        "af26127b9",
        "28d8c764f"
      ],
      "report": "_r61_parallel/report.md",
      "files": [
        {
          "path": "_r61_parallel/report.md",
          "sha256": "FE8D4C6C055519E553DF0057C41480A37AC9D7D12AD20F2505ABFC3AA2B7ACD5"
        },
        {
          "path": "_r61_parallel/acceptance.md",
          "sha256": "C22390F22D58294FCF8F7DEED8271489936FEE7A195715034D591E6B335AA98B"
        },
        {
          "path": "_r61_parallel/evidence/validation.md",
          "sha256": "6149CEC2DC0E13179555DC33378E0E9D20DF8BEF3EB7A8C2CEDCCF467F822AA5"
        }
      ],
      "blockers": [
        "资源缺失及过滤语义待决；正式分发/运行/实机门关闭"
      ]
    },
    {
      "id": "r61-integration-validation",
      "delivery_state": "independent-delivered",
      "integration_state": "blocked",
      "target_batch": "R6.1 正式分发与互用集成批",
      "thread_id": "01a0e115-c509-7f00-a69f-79ec0a77c06b",
      "thread_snapshot": {
        "status": "notLoaded",
        "latest_turn": "completed",
        "completed_at": 1790485293
      },
      "head": "111f6abe0c68330b5acdd867a21586071ce4662a",
      "commits": [
        "dfa8dfa79",
        "111f6abe0"
      ],
      "report": "_r61_integration/report.md",
      "files": [
        {
          "path": "_r61_integration/report.md",
          "sha256": "7F2ED213F45E259E43F267982911AEDC941309C8F8F2847549D9D5AAD3B1BB0E"
        },
        {
          "path": "_r61_integration/acceptance.md",
          "sha256": "99E508E72D53DA9B46C63E85B2CCFD70DA52962AA8F9D51AAC2DD8F0DEE4452B"
        },
        {
          "path": "_r61_integration/evidence/validation.md",
          "sha256": "65BB216DEEDF3B474740FDDC07F481C4D4434FE7B3F1A35D1721C8CC60A5B7C1"
        },
        {
          "path": "_r61_integration/source-hashes.json",
          "sha256": "5D8929902C648B26951CBB9FA45EDE0528D89F7B9786D56D94C556EC90F4BC52"
        }
      ],
      "blockers": [
        "缺ScriptGroup/砍树.json",
        "四类脚本目录及12/33路线目录缺失",
        "legacyFiltered及连续计划触发语义待owner裁决",
        "应用加载/保存/互导/真实运行未验证"
      ]
    },
    {
      "id": "r62-bgi-guard",
      "delivery_state": "independent-delivered",
      "integration_state": "pending",
      "target_batch": "R6 diff 收敛/持续守卫集成批",
      "thread_id": "01a0e15e-eff3-7e32-b503-f76035e28cb2",
      "thread_snapshot": {
        "status": "notLoaded",
        "latest_turn": "completed",
        "completed_at": 1790491142,
        "thread_title": "读取 Codex Goal 目标文件",
        "note": "registry thread title differs from delivery name; no terminal status inferred"
      },
      "head": "d50d28e80dc550a4d21ed1ac1f9dd1402d69fd1e",
      "commits": [
        "d50d28e80dc550a4d21ed1ac1f9dd1402d69fd1e"
      ],
      "report": "_r62_bgi_guard/report.md",
      "files": [
        {
          "path": "_r62_bgi_guard/.gitattributes",
          "sha256": "23902EBDBEA58994947466370BC27ADBB904B51E5ADCDD98009D97148E93E15E",
          "bytes": 61
        },
        {
          "path": "_r62_bgi_guard/acceptance.md",
          "sha256": "3CFF1235DBA80B447C421FE5DBED54293AB35F7D71F1121B458B0D57DC65444F",
          "bytes": 5356
        },
        {
          "path": "_r62_bgi_guard/candidate/BgiLegacySchedulerResidueTests.cs",
          "sha256": "2CFDE5B557B34D83171D65A3AE2D53EF3D94FE240B3E3D75034B64B18A52725D",
          "bytes": 13813
        },
        {
          "path": "_r62_bgi_guard/candidate/BgiLegacySchedulerSourceGuard.cs",
          "sha256": "49E983AD648221592C83D35CA36E01FE27F39D4542435016AD9AE3B8B1EB9696",
          "bytes": 11606
        },
        {
          "path": "_r62_bgi_guard/evidence/edit-integrity-fix1.json",
          "sha256": "6DB84E91DAD10586F25ED9FECE42E2937738A7E86A74A3F095B2E4A6DD047E5C",
          "bytes": 581
        },
        {
          "path": "_r62_bgi_guard/evidence/edit-integrity-initial.json",
          "sha256": "A3765B273A9801291CEB9849B667A3CF9836794C189C4CBF9409648D375FEAB5",
          "bytes": 941
        },
        {
          "path": "_r62_bgi_guard/evidence/edit-integrity-substring-boundary.json",
          "sha256": "9A6966E90C5E929D7841D31CA8F34CC5CF2EEB131BC6D510CF77CB71D8C90EC7",
          "bytes": 636
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/mutant-build.log",
          "sha256": "9E82154D5832633DEB33DF4D402C120D4FB619E3B8F09BFC207D2F1830B3000C",
          "bytes": 349
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/mutant-restore.log",
          "sha256": "39BCC562732E5CB67D2DF94C96B30F608ECAF88118AFBC1035621E830DCC1962",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/mutant-run/mutant.trx",
          "sha256": "95AA8299E98F439B8A43749A36853A755D352B456E33A7760488300C526CB604",
          "bytes": 5712
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/mutant-test.log",
          "sha256": "916D53EC9B7EB76BFC9631A67A73081DF76F24819CC1FB4C21E91CF326CB22A6",
          "bytes": 1884
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/mutation-record.json",
          "sha256": "4CA459803CD827312EB98A7BF5DCBB9393B561871F9A9C91965FFBCE06380089",
          "bytes": 1925
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/restored-build.log",
          "sha256": "9D2EF9662B7D7B2D6DD54DC9A823AD9798934DC14F95CE5069AFA8400DCCD985",
          "bytes": 351
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/restored-restore.log",
          "sha256": "E152267E3AF1F5B7AB7CC5B0D6652A2E0564BA197A36D8D7DBAD056942924F55",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/restored-run/restored.trx",
          "sha256": "A23F907B0D2202D6F62EC63C176ADEB3369E20920E9665C0232FEF5043231B63",
          "bytes": 3365
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/empty-source-pass-a686734caea24b30b69e07794b760ab0/restored-test.log",
          "sha256": "E34D1CBD5DC7EE946755AC4900E47A49E0D2857FD261EE9422F9CA987E7225E0",
          "bytes": 754
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/mutant-build.log",
          "sha256": "C29D59311EA51283C7FFE890ED1697C49916F3E54AC8A4D048170D15987E81C8",
          "bytes": 352
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/mutant-restore.log",
          "sha256": "BC49F45726853CCC99C8382DC2EFFEE18D3CDE90F79CDFA190BCE4F4B1E9841B",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/mutant-run/mutant.trx",
          "sha256": "F48A3D1D92485E85D1BF16DA629F32F2E25CA1168602AD88074B9B5CB957FCFE",
          "bytes": 5684
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/mutant-test.log",
          "sha256": "3E37ADE9A7CCBAAAEA38490762883F4DE9805AD6BE516B5677D78AC4E37B0280",
          "bytes": 1875
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/mutation-record.json",
          "sha256": "B286630BB2CCFD1175B4EA37601BCFAB14D519A17438ED786461F2BA73925C33",
          "bytes": 1915
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/restored-build.log",
          "sha256": "9A2CB0AA9BC1B0768AE01AE901A66C7D080CDB3B54F90945454D186CAC554FD0",
          "bytes": 354
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/restored-restore.log",
          "sha256": "C786B62A72DA78E434F35C3980473FACE77A32F5AE03EDF5AE758E6D3A2C7427",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/restored-run/restored.trx",
          "sha256": "DC664AEA99A57D27B9519A93CCD406A36D20639248BB67913FE8E86C9907C5F4",
          "bytes": 3356
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-banned-identifier-8dd36c5b77414306891f90fc119d542d/restored-test.log",
          "sha256": "D1997D7B4EDB5C423D32F6C3186ECD11425A69196B71DD169C70DFC5D6246D20",
          "bytes": 762
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/mutant-build.log",
          "sha256": "37A5B5937ADC384E5661F4D8C7947FABD8AC51C4D5362FA9FAE3B831F1CFB3DE",
          "bytes": 348
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/mutant-restore.log",
          "sha256": "5382CB75EAC4384991FABF0703A349B01622251BE68EAA2A47116862E2FD20A4",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/mutant-run/mutant.trx",
          "sha256": "BC7D0C4CC6BAD35B460E959A00BFEE37A142C956EEF785C351F6FD14AD19DD92",
          "bytes": 5387
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/mutant-test.log",
          "sha256": "9BD758F149038E0F10AD90664248622D01C34B35CAAD7A424EE007C13FD2C18E",
          "bytes": 1738
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/mutation-record.json",
          "sha256": "796B7E6B88058AAD1DAF03E4CB080256E3E8AA66F04E92AA0B08F595F756F722",
          "bytes": 1991
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/restored-build.log",
          "sha256": "F5E945EAD119974B60E85958C70AAAF92634E2EA5AAA2D15164F7EE532EDA5BC",
          "bytes": 350
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/restored-restore.log",
          "sha256": "75E5DC17FA42147B9E0A21B28DB6BA59E89409D88776E7A105087CB1926CC0EA",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/restored-run/restored.trx",
          "sha256": "7C8345058FE7FB3BE7C81542F1CC2C0069B34ECF9D2FC9845EE0B532244F1EBF",
          "bytes": 3501
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/omit-source-file-0c39895d9b9d4fd49ab51b1a9b51260e/restored-test.log",
          "sha256": "97A5FBFE28DEB7A5BD26E0241DA6E24F9DB8730852F6F75A1C8471F2082CFC03",
          "bytes": 752
        },
        {
          "path": "_r62_bgi_guard/evidence/mutations/scanner-original.cs.bin",
          "sha256": "49E983AD648221592C83D35CA36E01FE27F39D4542435016AD9AE3B8B1EB9696",
          "bytes": 11606
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/baseline-final-44b28be43498499fb7e95565fbca68a6/baseline-full.log",
          "sha256": "C0A0C50C36E44B6B1420979094EBF60642D2A00A94674776FA8C7692CC68E991",
          "bytes": 977
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/baseline-final-44b28be43498499fb7e95565fbca68a6/baseline-full.trx",
          "sha256": "C2A9BFA2F618B0A39937B01A410AB726C3F5B867E175624A6A81CCF955A3A366",
          "bytes": 36645
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/baseline-final-44b28be43498499fb7e95565fbca68a6/execution.json",
          "sha256": "D232950C535E151E05060B19B73A9E71AE5DADEA1784D49F9754D61E7A35E29B",
          "bytes": 1057
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/baseline-final-44b28be43498499fb7e95565fbca68a6/restore.log",
          "sha256": "BC49F45726853CCC99C8382DC2EFFEE18D3CDE90F79CDFA190BCE4F4B1E9841B",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/baseline-final-44b28be43498499fb7e95565fbca68a6/source-inventory.json",
          "sha256": "44DAACD36492171EC8D2E0A26F07DF6712EE537D3F15AC66AB9B1E2D814B6728",
          "bytes": 224952
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/baseline-final-44b28be43498499fb7e95565fbca68a6/source-version.json",
          "sha256": "A8CAC9219318F2A86CF00D02FDD40830864818DEC4AE9EA2A61CE9E916C4F297",
          "bytes": 1104
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/final-post-mutations-1716b4af567c4763ab4a8d292b361616/execution.json",
          "sha256": "28C070AA5217F27F10350D77046A47A43D168B45C0C202ABC2A5C2CEB4D366DA",
          "bytes": 1076
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/final-post-mutations-1716b4af567c4763ab4a8d292b361616/final-full.log",
          "sha256": "21A3DBD1C1A4F7636019F2477A3529FF951E64A7CCB995FB7B76D6DFEC89697A",
          "bytes": 988
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/final-post-mutations-1716b4af567c4763ab4a8d292b361616/final-full.trx",
          "sha256": "6CB775DD2459C823C7AFC5671CBD8CC9940B0CC90E1805F958FC33B6BD2467B9",
          "bytes": 36813
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/final-post-mutations-1716b4af567c4763ab4a8d292b361616/restore.log",
          "sha256": "53BDAC594D8D1F61B268C5BDEA4D51A82B8F4379F34FE1B53A8AB07EC9A38CEF",
          "bytes": 214
        },
        {
          "path": "_r62_bgi_guard/evidence/runs/final-post-mutations-1716b4af567c4763ab4a8d292b361616/source-inventory.json",
          "sha256": "44DAACD36492171EC8D2E0A26F07DF6712EE537D3F15AC66AB9B1E2D814B6728",
          "bytes": 224952
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475.json",
          "sha256": "0B180F918B5AA963E82B2BBB6FFF8120629D3C9E4A67993126EE8CDC3ADAB4BA",
          "bytes": 3646
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/BetterGenshinImpact.csproj",
          "sha256": "400B35829B2A391F4048DA02E1108B98DB09431B2947E806A184743BD1B33C3A",
          "bytes": 11
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/User/UserData.cs",
          "sha256": "5680041403C4212F5E3D74B099435421C37310FB5031B57FBCC508D210B7EC45",
          "bytes": 51
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/backup/Copy.bak.cs",
          "sha256": "7C0E1979CB078FFC479F1C3325624481BBBE240B9C82AC2B53EA5718CC6691F3",
          "bytes": 55
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/obj/Generated.cs",
          "sha256": "26D42AC9202FE9D289FDB1505DDB3C02D580E930349B140969FDB22ABF563579",
          "bytes": 61
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-a/Marker01.cs",
          "sha256": "0FA73ED8F4733A8D261DB6595D273A243E5C4B976730C7FA509F5BDEE3165371",
          "bytes": 63
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-a/Marker02.cs",
          "sha256": "B5B5FB17FA6D67FC9AEBBB3D794079D9250D7E69CC7CCE2D5E6037E0DDC8DE88",
          "bytes": 63
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-a/Marker03.cs",
          "sha256": "78406B550E56D213ECD4614A7A9F711194D97361F55E40E7E1FDBE9757A1DE31",
          "bytes": 60
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-a/Marker04.cs",
          "sha256": "7E1ABB4A854AE8E337A6A9AEA3EE4DA8375641540154E8E5E03AC6ADC01762CA",
          "bytes": 60
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-a/Marker05.cs",
          "sha256": "8750B76678C754BB21EE2643B6305C5997149FF230270B8DFE08E789EDF2D436",
          "bytes": 67
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-a/Marker06.cs",
          "sha256": "7AA8D56982490618C94AD63ABE63FEEACAC9C76537F5026B30A4D70E57BEAF99",
          "bytes": 70
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-b/nested/Marker07.cs",
          "sha256": "73234AC36053C38C95BA3A9301516DE3D4E27B2E234D96E34EFF8CE794602140",
          "bytes": 80
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-b/nested/Marker08.cs",
          "sha256": "AB497C5F58EBF1BCF61804ED55B70A1DAD958C46C5A132F529CF9C69229ED92E",
          "bytes": 77
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-b/nested/Marker09.cs",
          "sha256": "68B41F3D74BA1D8B00C1847B52E7847E9BCE2FEBE969CF7A9478F4D20E1A3418",
          "bytes": 62
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-b/nested/Marker10.cs",
          "sha256": "D5F8F0FD02BE0B4B031EFAA094A1CB4D24FAB13612DDD386DB9FDA8594A874A4",
          "bytes": 76
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-b/nested/Marker11.cs",
          "sha256": "8A4815143E0D9F2FC24D76653EF318488CF6353BD2F43AB5530533FA354FADE0",
          "bytes": 57
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/BetterGenshinImpact/src/part-b/nested/Marker12.cs",
          "sha256": "903B443D4DEEC2217DB1D44BDFB0A6DD2728AB276E803D78FF7841C8F46FD069",
          "bytes": 67
        },
        {
          "path": "_r62_bgi_guard/evidence/synthetic/pre-implementation-e5711260149e4f6cb7c7686dc3206475/neighbor/Outside.cs",
          "sha256": "4A506756BA287B98F92250226FDFDD66F6884A2D60994AF7AE403142F3979875",
          "bytes": 59
        },
        {
          "path": "_r62_bgi_guard/evidence/tooling-regression/9aa2f00c961240eea797e3593cc4c0e8/console.log",
          "sha256": "CA703A6CD2D449AD376B9829018AB9897EFA7084C4C938CE258220C264B38B8A",
          "bytes": 5543
        },
        {
          "path": "_r62_bgi_guard/report.md",
          "sha256": "B47A8B97F16DBDD616BF534413B8654552D8C12A98170373466CCA7C4E5F2E0E",
          "bytes": 7360
        },
        {
          "path": "_r62_bgi_guard/runner/LegacySchedulerGuard.Standalone.csproj",
          "sha256": "63C0F78CAA17DE7BB78F85E48DB90D576239AAEF5786445DBAA893CB8C9F0CED",
          "bytes": 936
        },
        {
          "path": "_r62_bgi_guard/delivery-registration.json",
          "sha256": "59729D55E5881256A786A135A238DD3C10EC4CEA4C9D5BD5C289A8A87BDB1F59",
          "bytes": 33426
        }
      ],
      "blockers": [
        "尚未加入现有 BGI 单元测试项目；须主线适配及集成回归",
        "仅覆盖12个C#文本标识符，不证明整体退出或运行安全；生产门保持关闭"
      ]
    },
    {
      "id": "r62-contact-audit",
      "delivery_state": "independent-delivered",
      "integration_state": "pending",
      "target_batch": "R6 接点/diff 收敛安全窗口",
      "thread_id": "01a0e3c6-87dc-7fe1-bbdf-beb6125d1b82",
      "thread_snapshot": {
        "status": "notLoaded",
        "latest_turn": "completed",
        "completed_at": 1790532375
      },
      "head": "8ee3f9df38991fabf49e945b8382128516a77b20",
      "commits": [
        "b73da73cfd52e198c52fe37ebf633e49faa6deaa",
        "0b1c8441d463004786d69cee9904cc362096f65f",
        "8ee3f9df38991fabf49e945b8382128516a77b20"
      ],
      "report": "_r6_contact_audit/report.md",
      "files": [
        {
          "path": "_r6_contact_audit/report.md",
          "sha256": "6074DE54C24008163CFDE4BB2670BE9CC02FE53F3C4F6B7BD91376C8A702B345"
        },
        {
          "path": "_r6_contact_audit/acceptance.md",
          "sha256": "8E71D7655EA78029634A3D893CA43217BD7C2803B60AFCA960B7E03972F6D735"
        }
      ],
      "blockers": [
        "F13：五个地脉结束检测字段及复制/导出映射缺少已确认接收处，须主线逐字段证明映射或形成正式迁移/退役裁决并补定向回归",
        "F08-F10、F12、F16、F18-F23、F27-F28 的运行级行为尚未验证",
        "候选仅为固定提交的离线差异清单和工具，不授权产品接收、删除/退役、集成或生产门开放"
      ]
    },
    {
      "id": "r5-prepared-process-cross-process-2026-09-28",
      "delivery_state": "independent-delivered",
      "integration_state": "pending",
      "target_batch": "R5 D18/D26 受理账/存储验证安全批次",
      "thread_id": "01a0e3f4-e58c-7221-b584-df1ba44f600d",
      "thread_snapshot": {
        "status": "notLoaded",
        "latest_turn": "completed",
        "completed_at": 1790535146
      },
      "head": "912efbeb9b74f8ecad01b1456844227f8ff70228",
      "commits": [
        "912efbeb9b74f8ecad01b1456844227f8ff70228"
      ],
      "report": "_r5_prepared_process/report.md",
      "files": [
        {
          "path": "_r5_prepared_process/report.md",
          "sha256": "44D89448CEDE56880ECFE43D96F0CC5DA583E00B4CB58CDB05B57459ACD09F8E"
        },
        {
          "path": "_r5_prepared_process/acceptance.md",
          "sha256": "511C0BFAE6E7FA777D471F9968C59C2ED1F78B449F95391EE41690DD642F7F58"
        }
      ],
      "blockers": [
        "主线适配、主线回归、BGI/助手/服务端运行、断电耐久均未验证；该交付只含隔离 Prepared 组件证据。",
        "D18/D26 统一受理事务、连续物理槽、退出证明和 R5.8 实机门继续关闭。"
      ]
    },
    {
      "id": "r5-slot-process-cross-process-2026-09-28",
      "delivery_state": "independent-delivered",
      "integration_state": "pending",
      "target_batch": "D14 物理槽底座/生产槽集成安全批",
      "thread_id": "01a0e420-364c-7ad1-af63-fe450cc707a0",
      "thread_snapshot": {
        "status": "notLoaded",
        "latest_turn": "completed",
        "completed_at": 1790539689
      },
      "head": "44576c63d9e45b545943a8c1edef1ee715890c8c",
      "commits": [
        "63fb0aa5c19c71573f28ec7c65e917ca35e064aa",
        "baa31e05d32e3b293f4d671b5843822458d015ef",
        "39657881ec8fbb51b80c564e921c3efeb87938c8",
        "e78499d07d09a0c272e61468bf179408c95f84c4",
        "3f448b2ac34bc7258ae7ce8e30c754464ce3c768",
        "44576c63d9e45b545943a8c1edef1ee715890c8c"
      ],
      "report": "_r5_slot_process/report.md",
      "files": [
        {
          "path": "_r5_slot_process/report.md",
          "sha256": "E9759F47D0B2A7D1912C571E001DCCE9C233E6D702C91BFF911CB0436F354F20"
        },
        {
          "path": "_r5_slot_process/acceptance.md",
          "sha256": "594FB3720D436BFEDF62076DD292772440F12F17093DE045B0FFEC37B9F7AD45"
        }
      ],
      "blockers": [
        "D14 消费批尚未完成主线适配、主线回归及产品入口验收；此独立交付不授权集成或生产运行。",
        "BGI/助手/服务端运行、真实 User、R5.8 与断电耐久仍未验证；生产门保持关闭。"
      ]
    },
    {
      "id": "parallel-delivery-review-requirements-2026-09-28",
      "delivery_state": "supplemental-review-only-pending",
      "integration_state": "pending",
      "target_batch": "各既有候选的计划消费批（仅登记审查要求；非产品功能交付）",
      "thread_id": null,
      "thread_snapshot": {
        "status": "unknown",
        "latest_turn": "not uniquely mapped",
        "note": "thread identity/status unavailable in this snapshot; delivery remains pending"
      },
      "head": "6d23092b98833bd580e6688ab2d5107379ae4cbb",
      "commits": [
        "f3daa3cfbd35d177695d451e93a78757fcc93980",
        "a116c8646829108615c355a031f3205e20d9ff29",
        "89b65bcd7a902269a18ee623cab0420011d5abdd",
        "a072dc7fa10bb1d2cc6268ce04b05594fcb8635c",
        "cc583b0f7de620146d5d338916aa644782921e7f",
        "6d23092b98833bd580e6688ab2d5107379ae4cbb"
      ],
      "report": "_r5_parallel_review/report.md",
      "files": [
        {
          "path": "_r5_parallel_review/report.md",
          "sha256": "BCB9704B74C8066C99F7E06499FF0174C357720025B5811655DD27837669E905"
        },
        {
          "path": "_r5_parallel_review/delivery-registration.json",
          "sha256": "BAD3CF99A6C7102ACAC0BAEFEA540C8041B5A92E8E27887848B9F51C28725CF7"
        }
      ],
      "blockers": [
        "该报告仅评估既有并行候选的接收前复核必要性，不是产品实现、完整交付复验或主线验收。其源 Codex thread 身份/终态未从可用线程快照唯一映射；提交和文件已核实，采取 pending，不据 Git 推断任务终态。",
        "Q-R61-ORDER、Q-R61-DEPENDENCY 与 R6.2 F13 的审查要求须在各自目标批次重新绑定并处理；R5 slot 首次最终交付复核须等源任务 closeout 后。不得提前消费、合并或开放生产门。"
      ]
    }
  ],
  "integration_decision": "No parallel artifact was integrated by SB21-4. R5.6, R6.1 and R6 diff guard stay in their registered consumption batches; BO-6/7 remain unstarted."
}


## parallel-discovery-output: _workflow/sb21-4/deliveries/final-closeout-r4.json L1-L170 SHA256=365b57ca05a624f0b23d1121d1abe8a586d5498ec2dd10832d15eca677e5fbae
Final read-only delivery discovery; independent reports and commits do not imply integration acceptance.
{
  "schema_version": 1,
  "quality_verdict": "NOT PROVIDED",
  "automatic_merge": false,
  "queue": [
    {
      "id": "r56-migration-audit",
      "target_batch": "R5.6 主线迁移集成批",
      "integration_state": "pending",
      "blockers": [
        "真实引用/激活元数据、静止窗口及生产检查点尚未完成；真实 User 门仍关闭"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r56-migration-audit\\better-genshin-impact-LCB\\_r56_parallel\\report.md",
      "head": "c11cb45f6de2436b46c58b91d47f2c9768132ef3",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r61-distribution-candidate",
      "target_batch": "R6.1 正式分发与互用集成批",
      "integration_state": "blocked",
      "blockers": [
        "资源缺失及过滤语义待决；正式分发/运行/实机门关闭"
      ],
      "covered_by": "r61-integration-validation",
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r61-parallel\\better-genshin-impact-LCB\\_r61_parallel\\report.md",
      "head": "28d8c764f5e1ae6ae0fee5a1d99a2e41d107d964",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r61-integration-validation",
      "target_batch": "R6.1 正式分发与互用集成批",
      "integration_state": "blocked",
      "blockers": [
        "缺ScriptGroup/砍树.json",
        "四类脚本目录及12/33路线目录缺失",
        "legacyFiltered及连续计划触发语义待owner裁决",
        "应用加载/保存/互导/真实运行未验证"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r61-integration\\better-genshin-impact-LCB\\_r61_integration\\report.md",
      "head": "111f6abe0c68330b5acdd867a21586071ce4662a",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r62-bgi-guard",
      "target_batch": "R6 diff 收敛/持续守卫集成批",
      "integration_state": "pending",
      "blockers": [
        "尚未加入现有 BGI 单元测试项目；须主线适配及集成回归",
        "仅覆盖12个C#文本标识符，不证明整体退出或运行安全；生产门保持关闭"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r62-bgi-guard\\better-genshin-impact-LCB\\_r62_bgi_guard\\report.md",
      "head": "d50d28e80dc550a4d21ed1ac1f9dd1402d69fd1e",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r62-contact-audit",
      "target_batch": "R6 接点/diff 收敛安全窗口",
      "integration_state": "pending",
      "blockers": [
        "F13：五个地脉结束检测字段及复制/导出映射缺少已确认接收处，须主线逐字段证明映射或形成正式迁移/退役裁决并补定向回归",
        "F08-F10、F12、F16、F18-F23、F27-F28 的运行级行为尚未验证",
        "候选仅为固定提交的离线差异清单和工具，不授权产品接收、删除/退役、集成或生产门开放"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r6-contact-audit\\better-genshin-impact-LCB\\_r6_contact_audit\\report.md",
      "head": "8ee3f9df38991fabf49e945b8382128516a77b20",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r5-prepared-process-cross-process-2026-09-28",
      "target_batch": "R5 D18/D26 受理账/存储验证安全批次",
      "integration_state": "pending",
      "blockers": [
        "主线适配、主线回归、BGI/助手/服务端运行、断电耐久均未验证；该交付只含隔离 Prepared 组件证据。",
        "D18/D26 统一受理事务、连续物理槽、退出证明和 R5.8 实机门继续关闭。"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r5-prepared-process\\better-genshin-impact-LCB\\_r5_prepared_process\\report.md",
      "head": "912efbeb9b74f8ecad01b1456844227f8ff70228",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r5-slot-process-cross-process-2026-09-28",
      "target_batch": "D14 物理槽底座/生产槽集成安全批",
      "integration_state": "pending",
      "blockers": [
        "D14 消费批尚未完成主线适配、主线回归及产品入口验收；此独立交付不授权集成或生产运行。",
        "BGI/助手/服务端运行、真实 User、R5.8 与断电耐久仍未验证；生产门保持关闭。"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r5-slot-process\\better-genshin-impact-LCB\\_r5_slot_process\\report.md",
      "head": "44576c63d9e45b545943a8c1edef1ee715890c8c",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "parallel-delivery-review-requirements-2026-09-28",
      "target_batch": "各既有候选的计划消费批（仅登记审查要求；非产品功能交付）",
      "integration_state": "pending",
      "blockers": [
        "该报告仅评估既有并行候选的接收前复核必要性，不是产品实现、完整交付复验或主线验收。其源 Codex thread 身份/终态未从可用线程快照唯一映射；提交和文件已核实，采取 pending，不据 Git 推断任务终态。",
        "Q-R61-ORDER、Q-R61-DEPENDENCY 与 R6.2 F13 的审查要求须在各自目标批次重新绑定并处理；R5 slot 首次最终交付复核须等源任务 closeout 后。不得提前消费、合并或开放生产门。"
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\parallel-recovery\\better-genshin-impact-LCB\\_r5_parallel_review\\report.md",
      "head": "6d23092b98833bd580e6688ab2d5107379ae4cbb",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r56-activation-prep",
      "target_batch": "R5.6 主线迁移集成批",
      "integration_state": "pending",
      "blockers": [
        "A-F real integration/activation/production consumer checks remain pending; no production, real User or R5.8 acceptance.",
        "Worktree currently contains 11 untracked evidence files; tracked product source is unchanged."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r56-activation-prep\\better-genshin-impact-LCB\\_r56_activation_prep\\report.md",
      "head": "90588159b4769c41284ade475052d8b56f92e2d6",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r57-identity-order",
      "target_batch": "R5.7 正式旧配置身份/顺序兼容消费批",
      "integration_state": "blocked",
      "blockers": [
        "IMPORTANT verification gap: missing/empty TaskDefinitions can rebuild task GUIDs and leave NextTaskId dangling after first save.",
        "Duplicate TaskOrder ID contract remains unresolved.",
        "No production code change or real User access; not integrated."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r57-identity-order\\better-genshin-impact-LCB\\_r57_identity_order\\report.md",
      "head": "2e66f4a2cf98b395be7586550b6d0b13b07618a9",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r57-retirement-prep-2026-09-28",
      "target_batch": "R5.7 正式旧链路退役实现复核与并存防护验收",
      "integration_state": "pending",
      "blockers": [
        "No conclusion that the old scheduler is fully absent or coexistence/no-dual-run is safe.",
        "Important compatibility identity/order case remains unverified.",
        "R5.6 integration, R5.7/R5.8 and §23.4 gates remain open."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r57-retirement-prep\\better-genshin-impact-LCB\\_r57_retirement_prep\\report.md",
      "head": "051852fdfe805fd1e0b807bae23300c923e5fb42",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    },
    {
      "id": "r58-acceptance-prep",
      "target_batch": "R5.8 集成验收准备/收口批",
      "integration_state": "pending",
      "blockers": [
        "Product acceptance not signed; protocol, real entry, hardware, R5.8 and production gates remain open.",
        "No unique thread identity/status was available in current snapshot."
      ],
      "covered_by": null,
      "report": "C:\\Users\\Administrator\\.codex\\worktrees\\r58-acceptance-prep\\better-genshin-impact-LCB\\_r58_acceptance_prep\\report.md",
      "head": "4c1316df1765756d49740c62fdd14c14cbd32e09",
      "next_action": "executor must audit semantic gates and integrate at target batch"
    }
  ],
  "unregistered_reports": [],
  "errors": [],
  "ok": true,
  "task_status": "not inferred from Git; executor must query thread status"
}


## Generated evidence overview (mechanical only)
{
  "tests": {
    "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-regen/claim-regen.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-noenv/claim-noenv.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/bo13/bo13.trx": {
      "total": 49,
      "passed": 49,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/localwait/localwait.trx": {
      "total": 240,
      "passed": 240,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx": {
      "total": 1560,
      "passed": 1558,
      "failed": 0,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/assistant-full/results/assistant-full.trx": {
      "total": 1560,
      "passed": 1557,
      "failed": 1,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m1-startup-source-identity-v2/baseline/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m1-startup-source-identity-v2/mutant/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m1-startup-source-identity-v2/restored/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m2-recovery-persisted-transition/baseline/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m2-recovery-persisted-transition/mutant/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m2-recovery-persisted-transition/restored/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m3-repark-refreshes-context/baseline/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m3-repark-refreshes-context/mutant/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m3-repark-refreshes-context/restored/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m4-recovery-revision-advancement/baseline/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m4-recovery-revision-advancement/mutant/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m4-recovery-revision-advancement/restored/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m5-same-process-context-refresh/baseline/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m5-same-process-context-refresh/mutant/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m5-same-process-context-refresh/restored/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m6-startup-source-valid-positive/baseline/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m6-startup-source-valid-positive/mutant/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r7-repair/mutations/r7-m6-startup-source-valid-positive/restored/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard-run2/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard-run2/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard-run2/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m2-tombstone-retry-idempotence-run2/baseline.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m2-tombstone-retry-idempotence-run2/mutant.trx": {
      "total": 1,
      "passed": 0,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m2-tombstone-retry-idempotence-run2/restored.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    }
  },
  "comparison": null,
  "mutations": [
    {
      "id": "r7-m1-startup-source-identity-v2",
      "mechanical_status": "ok",
      "source_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "r7-m2-recovery-persisted-transition",
      "mechanical_status": "ok",
      "source_sha256": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "r7-m3-repark-refreshes-context",
      "mechanical_status": "ok",
      "source_sha256": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "r7-m4-recovery-revision-advancement",
      "mechanical_status": "ok",
      "source_sha256": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "r7-m5-same-process-context-refresh",
      "mechanical_status": "ok",
      "source_sha256": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "r7-m6-startup-source-valid-positive",
      "mechanical_status": "ok",
      "source_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "r8-m1-final-reread-runid-guard-run2",
      "mechanical_status": "ok",
      "source_sha256": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "r8-m2-tombstone-retry-idempotence-run2",
      "mechanical_status": "ok",
      "source_sha256": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    }
  ],
  "evidence": [
    {
      "id": "assistant-full-r6-baseline",
      "path": "_workflow/sb21-4/review/r6-review-validation-final/assistant-full/assistant-full-r6.trx",
      "purpose": "Historical full-suite baseline used only for testId comparison.",
      "level": "test",
      "conditions": "R6 full suite; historical testId comparison only, not current regression success.",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "18754c107a25cc1e82211a56bd9b4f547faa9f27322065073384c10c600c9ebc",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "00bf85441b415dfc23712ddf992d549d00b73e6d58797057726c841c141b3bd0",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "e6e4e78eec3423907326e52504f3386210cf67dc35491242998a776102ca7a0a"
      },
      "file_sha256": "573dd4b95f3d452530bc816d47021b3ff66f5209ad68d62c695fc33af46ebf6b"
    },
    {
      "id": "r8-assistant-build",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-build.log",
      "purpose": "Final-source assistant/test-project build; exit 0, 1 warning/0 errors.",
      "level": "build",
      "conditions": "dotnet build --no-restore -p:DeployToBgiTools=false -p:UseSharedCompilation=false; see captured command log and exitcode.",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad",
        "_batch21/b21_plan.md": "498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb",
        "_batch21/sb21-4-handoff-2026-09-28.md": "b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a",
        "_workflow/sb21-4/scope-and-state-table.md": "4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6"
      },
      "file_sha256": "1901111c83ebe625f44fbf7b0ed165ed2700a437a0cb86ead43683a3f0e7a48a"
    },
    {
      "id": "r8-test-project-build",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/test-project-build.log",
      "purpose": "Final-source assistant/test-project build; exit 0, 1 warning/0 errors.",
      "level": "build",
      "conditions": "dotnet build --no-restore -p:DeployToBgiTools=false -p:UseSharedCompilation=false; see captured command log and exitcode.",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad",
        "_batch21/b21_plan.md": "498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb",
        "_batch21/sb21-4-handoff-2026-09-28.md": "b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a",
        "_workflow/sb21-4/scope-and-state-table.md": "4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6"
      },
      "file_sha256": "800587240e61cf2baaba5db5f8164711908d3f011b5af5c0119a57120a203dce"
    },
    {
      "id": "r8-claim-surface-regen",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-regen/claim-regen.trx",
      "purpose": "Final declaration-surface regeneration; passed with CLAIM_SURFACE_REGENERATE=1.",
      "level": "test",
      "conditions": "CLAIM_SURFACE_REGENERATE=1; dotnet test --no-build --no-restore -p:DeployToBgiTools=false; manifest SHA stable.",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad",
        "_batch21/b21_plan.md": "498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb",
        "_batch21/sb21-4-handoff-2026-09-28.md": "b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a",
        "_workflow/sb21-4/scope-and-state-table.md": "4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6"
      },
      "file_sha256": "05a1cb1380afef0551befc6995c0a6194b76f25281c9fa7d26b7270d0f385035"
    },
    {
      "id": "r8-claim-surface-noenv",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-noenv/claim-noenv.trx",
      "purpose": "Final declaration-surface guard without regeneration environment; passed and manifest SHA unchanged.",
      "level": "test",
      "conditions": "CLAIM_SURFACE_REGENERATE removed; dotnet test --no-build --no-restore -p:DeployToBgiTools=false; SHA bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e before/after.",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad",
        "_batch21/b21_plan.md": "498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb",
        "_batch21/sb21-4-handoff-2026-09-28.md": "b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a",
        "_workflow/sb21-4/scope-and-state-table.md": "4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6"
      },
      "file_sha256": "1a8168982c64b7e21af48e9729b07a3f91b397a1c1378fbdd06788b51843d3e0"
    },
    {
      "id": "r8-bo13-final",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/bo13/bo13.trx",
      "purpose": "Final-source BO-13 named regression; 49/49 passed.",
      "level": "test",
      "conditions": "dotnet test --no-build --no-restore -p:DeployToBgiTools=false; filter FullyQualifiedName~LocalWaitFinalizationContractTests.",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad",
        "_batch21/b21_plan.md": "498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb",
        "_batch21/sb21-4-handoff-2026-09-28.md": "b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a",
        "_workflow/sb21-4/scope-and-state-table.md": "4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6"
      },
      "file_sha256": "36897d27159cca361a45ddcaff060abab6ebe596def7998d8c648e9fec0dcc24"
    },
    {
      "id": "r8-localwait-final",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/localwait/localwait.trx",
      "purpose": "Final-source exact SB21 LocalWait regression set; 240/240 passed.",
      "level": "test",
      "conditions": "dotnet test --no-build --no-restore -p:DeployToBgiTools=false; exact four-part filter recorded in final-current-source-v4/localwait/localwait.log.",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad",
        "_batch21/b21_plan.md": "498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb",
        "_batch21/sb21-4-handoff-2026-09-28.md": "b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a",
        "_workflow/sb21-4/scope-and-state-table.md": "4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6"
      },
      "file_sha256": "03fc98e7f9bfb59101d33cda7ab4c779194288a4dacea4fb6272ccdeed036d51"
    },
    {
      "id": "r8-assistant-full-final",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx",
      "purpose": "Final-source assistant full suite; 1558 passed/2 skipped/0 failed/1560 total.",
      "level": "test",
      "conditions": "dotnet test --no-build --no-restore -p:DeployToBgiTools=false.",
      "binding": "current",
      "source_sha256": {
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs": "b6646b1a3f0d456e5bf437fcf726efdcc30d3645099f82b9900c4b3458c08d5f",
        "MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs": "f6dcb649da11c0e83271b1049d6d8dc9bb264dc9d6f02bc3b67c9e5d359999a5",
        "MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs": "e3fe7eb41e62d35a4e1cd802e3c1050e581009c703a2bb8d7883685ce42e57ea",
        "MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs": "e08bd825e113b23a7439f8d9769d10569d93008c74d5976a35b9ce728d87fb5b",
        "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "781087da6377f18e53e8e904a8cebc1a904d804b28d9fd2e3a826a7679100d06",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs": "66510f6549eae254f16fe76ee55bdaadd72a72226f3692cfc075909e0795bf29",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs": "39e6f87c6007b110abc622adc7186f7c6e3914e59318cb4fd246b382c7e24df8",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitQueuePolicy.cs": "969b151af9eaecaf67dd54a70397d049b4b1a7aa05107bb9a8f092b6529a30f4",
        "MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitIdentityTranslation.cs": "59216aae21d6d97d9c83ef828176b753d73a83eaac25c7d5e6cd053aae0c1da0",
        "MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs": "32651aa2448bb7e3baa7f9c87f685541fd685f1d65d3f4930c078503a68b337e",
        "MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs": "f7a32330a2c9d873abf4d924c6b3dce9e61d543bf1b16269c174e45e1aeba3a7",
        "MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs": "f08e7bd05c398264b36044ab886073ac45bb5ba63949f2bf351fae1aa211cfd1",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs": "5d207d674227a8d0d9b48684247a1dfee0887a096ad57d50719942c511128563",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt": "bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e",
        "MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj": "974f9fe4c47832d913844704231c731bf9248297a56b6e847dc4e63226b4bf44",
        "Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj": "e6a59beb4466b8b7ea6cb39661e16c216d9d09d6bdfd55a19f71efdacf030287",
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "bce50d952b78ab06f0e5317fd611f5211b73d14a7b2060a9e971c581333c01ad",
        "_batch21/b21_plan.md": "498a91ad6cadd3c45a3d1f0545f2fd2f50bb96083488f3ce83828f5aeafc76eb",
        "_batch21/sb21-4-handoff-2026-09-28.md": "b8a4cc723af10dc15c90f07ac5c5e283821e967e23a5616d0fa1148c2f8f027a",
        "_workflow/sb21-4/scope-and-state-table.md": "4befcdb0058b3f61dc43f01348b6f6e5457fc907d10ac633036dc6e0e8ed4fc6"
      },
      "file_sha256": "86df562d1b5f082621d1d4eb9651c6c53d2762f994d30e3407a4b964929df2f8"
    },
    {
      "id": "r8-assistant-full-initial-failure",
      "path": "_workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/assistant-full/results/assistant-full.trx",
      "purpose": "Preserved first full-suite failure before documentation fixture-name correction.",
      "level": "test",
      "conditions": "Failed only DocsFixtureReferenceGuardTests because the R5.3 terms OPEN_IMPORTANT and CLOSED_AT_IMPORTANT were interpreted as test fixture identifiers; changed wording to Chinese status prose, then ClaimSurface and full suite passed. Retained to explain failure identity, not current green evidence.",
      "binding": "historical",
      "source_sha256": {
        "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md": "359509c583aae4674479cc5b491ff008279e69bfe992b0143157003d8a25f31b",
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/DocsFixtureReferenceGuardTests.cs": "ef19314bd93850d28de5becf0db072b4a20014b806bc8f089c01e86efcab2c35"
      },
      "file_sha256": "6a807ca0793be7d75d6def53656e1451e96157082b85b8a10d84c27594dffef8"
    },
    {
      "id": "r8-deliveries-final-closeout",
      "path": "_workflow/sb21-4/deliveries/final-closeout-r4.json",
      "purpose": "Final read-only parallel-delivery discovery scan; exit 0, queue 12, no unregistered reports/errors; task terminal state not inferred.",
      "level": "document",
      "conditions": "python -B tools/mistletoe/deliveries.py --root .; no automatic merge; task status cross-checked separately.",
      "binding": "historical",
      "source_sha256": {
        "Docs/design/mistletoe-parallel-deliveries.json": "8fdc8f6c981fdc26c04e469eee329c2b8aeec4fd7331ee549254a2aeb1cc85eb"
      },
      "file_sha256": "365b57ca05a624f0b23d1121d1abe8a586d5498ec2dd10832d15eca677e5fbae"
    }
  ],
  "quality_verdict": "NOT PROVIDED"
}

## git status --porcelain (all changes; ownership requires manual classification)
 M Docs/design/mistletoe-session-relay-2026-09-24.md
 M Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
 M Docs/design/unified-job-registry-master-plan.md
 M MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs
 M MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs
 M MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs
 M Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
 M Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs
 M _batch21/b21_plan.md
 M _batch21/sb21-4-handoff-2026-09-28.md
 M 槲寄生调度器总计划.md
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
?? Docs/design/mistletoe-parallel-deliveries.json
?? Docs/design/mistletoe-parallel-deliveries.md
?? Docs/design/mistletoe-parallel-recovery-2026-09-27.md
?? Docs/design/mistletoe-r62-registration-2026-09-27.md
?? Docs/design/mistletoe-workflow-facilities.md
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
?? _aout.txt
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
?? _dpiprobe/
?? _extprobe/
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
?? _probe/
?? _probe_taskline.png
?? _probe_taskline2.png
?? _r17.txt
?? _r5_batch9_assistant_full.log
?? _r5_test_temp/
?? _statusprobe/
?? _styleprobe/
?? _tools/
?? _uidmask_preview.png
?? _workflow/
?? build_final.log
?? build_head_output.txt
?? test_preservation.txt
?? testrun_preservation.log
?? tools/

## scoped unstaged diff
diff --git a/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md b/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
index c23b873b5..2793f384b 100644
--- a/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
+++ b/Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
@@ -4915,36 +4915,31 @@ SB21-3 有独立 GPT 会诊台账 C:/Users/Administrator/.tools/zcode-relay/test
 
 ### §24.124.1 范围与原始等级
 
-本子批只处理 BO-13 会话内 Stop 出口，以及 BO-11 对 BO-6/7/8/9 的逐项登记。BO-13 原等级为 batch20 R47 **重要**；BO-6 原等级为 R19 **必改**、同族 R21 F4 **重要**；BO-7 的 R21 F1、F2 均 **必改**；BO-8 的 R29 与 BO-9 的 R34 F5 均 **重要**。本批不重开 BO-4、BO-10 或 BO-12。原始来源与本批边界见 `_workflow/sb21-4/raw-bo-obligations.json` 和 `_workflow/sb21-4/scope-and-state-table.md`。
+本子批只处理 BO-13 会话内 Stop 出口，以及 BO-11 对 BO-6/7/8/9 的逐项冻结登记。BO-13 原等级为 batch20 R47 **重要**；BO-6 原等级为 R19 **必改**、同族 R21 F4 **重要**；BO-7 的 R21 F1、F2 均 **必改**；BO-8 的 R29 与 BO-9 的 R34 F5 均 **重要**。本批不重开 BO-4、BO-10 或 BO-12。原始来源见 `_workflow/sb21-4/raw-bo-obligations.json` 和独立机器台账；状态与交错矩阵见 `_workflow/sb21-4/scope-and-state-table.md`。
 
 ### §24.124.2 停驻运行终局状态与队列绑定
 
-仅对已持久化 `LocalWaitParking` 记录、明确 NoSend、完整 Wait/Hold 裁定身份、无同流程 drive/reservation 且无未决外部发送/收尾事实及前置动作责任的 run 接受同步 Stop。入口与 gate 内复读都验证请求 runId 与记录体 runId；读取异常返回 `Unavailable`。Wait 清理使用 `LocalWaitBinding` 全部队列可见不可变载荷作锁内比较；只把精确匹配项写为 `Cancelled` 墓碑，不降低 generation/high-water、不改无关项。身份漂移、队列损坏或读写异常返回不可用，保留停驻运行；缺少队列项或已有墓碑不伪造新项；持久 Hold 无 binding 时只终态化 run，不写队列。Wait 决策同时绑定当前 run/workflow/revision、cursor snapshot、同 key 的 `LocalWaitDeferred` submission、完整 context/binding 与共享身份 factory 的规范候选身份；decision/binding revision 可小于后续 run revision，但不能在未来。完成后同一 run 以 `Cancelled` 留在历史并释放 `ActiveStates` 槽位；run、cursor occurrence/loop/attempt、submission key、旧 outcomes、裁定和 binding 保留，不调用 flow 收尾或发送，并在 gate 外回写 admission registration 终态。若队列墓碑已落盘但 run 终态写入失败，结果明确提示可重试；重试识别墓碑后完成 run 终态化且不重复写队列。
+仅对已持久化 `LocalWaitParking` 记录、明确 NoSend、完整 Wait/Hold 裁定身份、无同流程 drive/reservation 且无未决外部发送/收尾事实及前置动作责任的 run 接受同步 Stop。前置动作 enum 为 `Intent/Submitted/Succeeded/Failed/Cancelled/Unknown`：仅 `Succeeded/Failed/Cancelled` 是允许收敛的已知终态；`Intent/Submitted/Unknown` 和未识别数值状态均保持未决。终态记录上残留的 `SendAttempted` 或 `JobId` 不单独阻止 Stop，未决状态则无论这些旁置字段为何都拒绝。该护栏为 Stop 专用，不改变恢复扫描原谓词。入口与 gate 内复读都验证请求 runId 与记录体 runId；读取异常返回 `Unavailable`。Wait 清理按 `LocalWaitBinding` 全部队列可见不可变载荷作锁内比较；只把精确匹配项写为 `Cancelled` 墓碑，不降低 generation/high-water、不改无关项。身份漂移、队列格式损坏及已具名的 run 读取或 queue/run 持久化故障路径返回不可用并保留可重试状态；独立 queue 文件读取 I/O 异常未单独注入，不作为等同已验证分支。最终复读错绑测试比较错绑注入后的 A/B run 文件与队列字节，证明 Stop 不进一步改写当时可见内容，不声称恢复注入前 A 文件；缺少队列项或已有墓碑不伪造新项；持久 Hold 无 binding 时只终态化 run。
+
+Wait 决策必须绑定当前 run/workflow/revision、cursor snapshot、同 key 的 `LocalWaitDeferred` submission、完整 context/binding 与共享 identity factory 的规范候选身份。合法修订顺序是 `0 < binding.RecordRevision <= context.RecordRevision <= run.RecordRevision`：恢复复核可以更新 Context 并保留不可变旧 binding，不要求两者相等，也不允许 Context 早于 binding。来源身份按类别校验：StartupHandoff 的权威身份在同一运行台账中，Stop 要求 `SourceIdentity == run.RunId`、存在 Start/ArmTrigger 受理绑定且 `AdmissionSourceScope` 与 binding scope 一致；PanelFlowRegistration 的权威身份是唯一流程登记父操作，Context 与 binding 保存该 RequestIdentity 的配对快照，Stop 检查两者相等，但不把快照当作发送授权。终态化保留 run、cursor occurrence/loop/attempt、submission key、旧 outcomes、裁定和 binding；不调用 flow 收尾或发送；在 gate 外回写 admission registration。队列墓碑先落盘而 run 终态写入失败时，返回可观察重试结果；后续 Stop 识别墓碑并完成 run 终态化而不重复改队列。`Cancelled` 留在历史并释放 `ActiveStates`；同流程恢复后再次停驻可 Stop，Stop 后新 Start 使用新 RunId。
 
 ### §24.124.3 实现、反例与关键断言判别
-改动包括 `TaskCenterHost.RequestRunAction/StopParkedRun`、`LocalWaitQueueStore.Cancel(LocalWaitBinding, ...)`、新增 `LocalWaitFinalizationContractTests` 17 个 Fact，以及测试互斥元数据。开工 HEAD `5e7e7e22f11daad0c86795368e9a21bb14d79b19` 的首轮 6 项中 3 项因旧 Host 不支持停驻 Stop 而按预期失败；R1 修复前 16 项另有 7 项目标行为失败，定位前置动作责任、cursor/deferred submission/规范准入身份、runId 文件错绑、读取异常和 admission terminal 回写。生产 Host/queue 源码恢复 SHA 与编辑前源字节一致；当前具名 Host 事实 17/17。
 
-最终 23 个独立关键反向突变均通过构建、在指定断言失败、再逐字节恢复并通过具名测试。覆盖终态与精确墓碑、payload/run/cursor/submission/admission identity、前置责任、损坏队列、读/写异常及重试、Resume reservation、admission terminal 回写、HWM/无关项、重复 Stop 与 ActiveStates。一个与其他护栏重叠的旧突变不计独立数，原始尝试保留；全部日志、TRX、testId 和 SHA 在 `_workflow/sb21-4/mutations-review-r1-retry2/`。Host 原/恢复 SHA-256 `E050959933CD28D1CA2F9EDE28AB59B55359F262BBC9A7EAC01ECF69C218B260`；queue 为 `E08BD825E113B23A7439F8D9769D10569D93008C74D5976A35B9CE728D87FB5B`。
+本批改动集中于 `TaskCenterHost.RequestRunAction/StopParkedRun/HasValidParkedDecision`、`TaskCenterHost.Admission` 终局回写、RunStore 和 Host 测试接缝。R6 复审前具名反例覆盖 throwing logger 不阻断 sibling 回写、非正/未来 binding 与 context revision 拒绝且保留 run/queue 原字节、重启恢复后再停驻沿用旧 binding 与 queue generation/HWM、新流程 RunId，以及两个 reconciliation worker 均完成后只发生一次终局 revision 迁移并隔离另一 run。R7 新增 StartupHandoff 借用其他 run ID 的反例与合法 run ID 正例，以及重启恢复态先持久迁移并推进 revision、显式 Resume 后 Context 刷新且 binding/generation/HWM 保留的断言。错借身份夹具在 R6 源码上以 `Expected Unavailable, Actual Effective` 失败，R7 修复后通过；具名证据见 `_workflow/sb21-4/review/r7-repair/identity-red-current-v2/` 与 `source-guard-final-target/`。
+
+R5/R1 历史突变保留原执行证据，不在本轮重绑；R6 五项有效突变与 R7 六次具名反向突变实验均以成功 baseline/build 为前提，mutant 构建成功且具名测试在指定目标断言失败，恢复后源码 SHA 与原字节精确一致并复跑通过。R7 M1–M6 分别命中 StartupHandoff 来源身份拒绝、恢复状态落盘、重启恢复后 Resume/repark 刷新 Context、恢复 revision 递增、同进程 repark 刷新 Context、合法 StartupHandoff run identity 保持放行；M3/M5 是不同测试断言实验但共享同一源码 mutant，六次实验对应五种源码 mutant。每项 baseline/build/mutant/restored 独立日志/TRX、命名失败、源码原始/恢复 SHA 和 mutant SHA 见 `_workflow/sb21-4/review/r7-repair/mutations/`；第一版 M1 只因换行预检未发送修改，单独保留且不计有效突变。R6 M1–M5 的 TryLog、revision 与并发完成 join 证据见 `_workflow/sb21-4/review/r6-repair/mutations/`。R1/R5 历史哈希只沿用当时记录，未知执行哈希继续未知；无效/重叠及本地未发送预检尝试保留并排除。
 
-回归实际发现 `LocalWaitGenerationContractTests` 与本批具名事实会并发改写同一个进程级 `BeforeTemporaryFileWriteProbe`，使目标测试在 fixture seed 的 queue Upsert 阶段被其他测试注入。复现记录保留于 `_workflow/sb21-4/final/final-current/full/assistant-full-current.trx` 与相邻日志。为限定共享可变探针的范围，这两个测试类加入仓库既有 `LocalWaitSnapshotProbe` 禁并行 collection；隔离后具名 17/17 和助手回归全绿。此测试调度修复不改变生产行为。
-### §24.124.4 BO-11 Wave3 残项处置
+并发回写依据也在范围内复核：Host 每实例互斥只串行化该实例的任务；`MarkOperationTerminal` 通过 `MutateHandoffLatest` 在跨进程文件锁中读取最新租约，并在锁内再次要求 request 仍为 `Accepted` 后以同一 revision 发布终态。确定性 Barrier 夹具证明重叠 timeout/retry 只有一次有效转移、重复 Stop 不再写、同一 run 操作的 `UpdatedRevision` 对应发布 revision、另一 run 的 operation JSON 字节不变。该证据限于助手组件内线程交错，不证明跨进程压力、断电耐久或产品进程运行。
 
-BO-6 的现行 helper 在停驻与已完成锚点冲突时返回 `null` 并记录诊断，但 Runner 的 `hadBadOutcome` 不把 `waitLocally` 计作坏结果；当前证据不能证明冲突成为显式失败并阻止聚合成功。原 R19 必改与 R21 F4 重要风险均保留未闭合并交 owner。BO-7 现有 `(LoopIteration, SequenceIndex)` 全序与多有效停驻遍历有组件用例；它们未驱动救援后的真实推进，原 F1/F2 必改随 BO-6 端到端验收未闭合并交 owner。BO-8 源码仍承认恢复点之后推进没有完成过滤，原重要项未闭合并交 owner。BO-9 只有 helper/loop 组件全序证据，没有 Runner 跨轮次恢复推进证明，原重要项未闭合并交 owner。各项证据、风险、尝试与具体验收条件见状态表；方案、注释和组件用例均未被记作端到端闭合。BO-11 本批完成的是残项逐项打包登记，不表示 Wave3 已清零。C12→BO-4 已按 SB21-3 owner 接受路径收口，本条不重开。
+### §24.124.4 BO-11 Wave3 残项冻结处置
+
+BO-6 的现行 helper 在停驻与已完成锚点冲突时返回 `null` 并记录诊断，但 Runner 的 `hadBadOutcome` 不把 `waitLocally` 计作坏结果；真实 Runner 冲突失败/成功聚合语义仍未证明。R19 必改与 R21 F4 重要均原级交 owner。BO-7 已有 `(LoopIteration, SequenceIndex)` 全序与多有效停驻遍历组件用例，但没有救援后的真实 Runner 推进闭环；R21 F1/F2 必改均原级交 owner，且 BO-6/7 本批未启动。BO-8 源码仍承认恢复点之后推进没有完成过滤，R29 重要未闭合。BO-9 只有 helper/loop 全序组件证据，没有 Runner 跨轮次恢复推进证明，R34 F5 重要未闭合。逐项证据、风险、尝试和验收条件见状态表；方案、注释或组件测试不算端到端闭合。BO-11 在本批完成的是冻结残项逐项打包登记，不表示 Wave3 清零。C12→BO-4 已由 SB21-3 按 owner 接受的“历史修复＋本批验证”路径收口，本条不重开。
 
 ### §24.124.5 验证、会诊与门禁
-助手项目与助手测试项目均使用 `dotnet build --no-incremental -p:DeployToBgiTools=false` 并成功，分别 59/80 warning、0 error。最终具名 Host 事实 17/17、curated BO-13/LocalWait/Runner 定向 233/233，扩展 TaskCenter/LocalWait 定向 277/277，助手全量 1526 pass / 2 skip / 0 fail / 1528。与开工工作区 HEAD 下新增本批事实前的 1511 项基线按真实 testId 比较：shared 1511 / added 17 / removed 0 / changed 0 / unchanged 1511；17 项均为 `LocalWaitFinalizationContractTests`，最终差集见 `_workflow/sb21-4/settled-final-r4/testid-diff.md`。
 
-隔离修正前曾有一次定向 232/233 及一次全量 1 fail；失败都在 `SeedParkedRun` 的队列准备阶段，由 `LocalWaitGenerationContractTests` 静态临时写入探针并发拦截，不是 BO-13 目标断言。两测试类加入既有禁并行集合后，curated 定向 233/233、扩展定向 277/277、助手全量 1526/2/0/1528。首跑、失败TRX与修复后成功日志保留在 `_workflow/sb21-4/final/`、`review-prep-r2/` 与 `settled-final-r4/`；测试宿主证据不表示产品真实运行。
+R8 最终文档修订后的助手主项目与测试项目均使用 `-p:DeployToBgiTools=false` 非增量构建通过（分别 58/79 warnings、0 errors）；ClaimSurface regen/no-env 均通过，最终 SHA 与 TRX 见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-evidence.json`。BO-13 定向 49/49、LocalWait 240/240、助手全量 1558 passed / 2 skipped / 0 failed / 1560 total；具名 TRX、完整 TRX、build 日志及差集见同目录。R6→最终全量 testId shared 1558、added 2、removed 0、changed 0、unchanged 1558；开工→最终 shared 1511、added 49、removed 0、changed 0、unchanged 1511；R7→最终 shared 1560、added 0、removed 0、changed 0、unchanged 1560。两个新增 ID 是 StartupHandoff 本 run identity 正例及借用另一 RunId 时 Unavailable/字节保持反例。首次助手全量文档守卫失败及其修正记录仍保留；最终回归通过。上述为源码/助手组件/Host 测试证据，不代表生产实机运行。
 
-GPT 会诊与 owner 检查点：R1 为 GPT `gpt-6-astra` / medium，已发 1 次并提出五项 BO-13 **IMPORTANT**。R2 review 请求于 2026-09-27 20:00 UTC 已发，但会诊执行器返回 `GPT consultation failed for gpt-6-astra (Codex exit code 1). Start a new task and retry.`，未提供 GPT 报告或质量结论；按失败已发送计入累计 2/8。失败记录、R2 送审包与快照见 `_workflow/sb21-4/review/gpt-r2-attempt1-failure.md`、`review-snapshot-r2-retry3/`、`review-snapshot-r2-retry4/`。所以五项重要发现仍全部**未闭合并交 owner**：
+GPT R8（gpt-6-astra / medium，独立台账累计 8/8）对 BO-13 原始 R1 #1–#5 及已登记 R3/R4 IMPORTANT 扩展均建议在明示有限合同内按原 IMPORTANT 等级关闭，未发现新增 MUST/IMPORTANT。依本 Goal 的 owner 指令，本批接受这些有限范围原级裁决；不将其扩大为生产验收或 Wave3 清零。R8 报告及逐项证据边界见 `_workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md`。R8 指出的两项 SHOULD 已修正：R7 M3/M4 映射与实际 mutations-r7.json 对齐；交接/R5.3 当前状态与 R8 最终验证同步。R8 是预算最后一轮，不再增加会诊。BO-6/7 未启动；BO-8/9 仍按原级冻结登记。
 
-| R1 原级与风险 | 本批尝试/证据 | owner 验收条件 |
-|---|---|---|
-| IMPORTANT 1：恢复扫描谓词未覆盖 Intent/Submitted/Unknown、SendAttempted、jobId 等前置责任，误 Stop 会让责任失去原 run 对账身份。 | Stop 专用责任谓词；具名 Unknown/SendAttempted 行为测试、目标突变及保留 run/queue 字节证据。 | 核对外部责任状态枚举/交错，确认所有未决发送或收尾来源均拒绝终态化，零发送责任可安全收敛。 |
-| IMPORTANT 2：Wait/Hold 裁定须绑定 run/cursor/context/submission、规范 successor 与 admission 身份；repark 旧 revision 关系需符合生命周期。 | 17 个 Host facts 和多个身份/提交/游标独立突变、修复前命名失败；允许较早但非未来 revision。 | 核对字段全表及恢复/repark 代际语义；每种身份错配均不能清队列或终态化，规范候选由共享 factory 得出。 |
-| IMPORTANT 3：请求文件 runId 与嵌入记录 runId 不一致会误操作其他运行。 | 入口与 gate 内复读比较，misbound 文件用例和双位置突变；A/B/queue 字节精确保留。 | 验收坏读、空值、A-file/B-record 与并发替换均在入口和最终 gate 拒绝且全文件字节不变。 |
-| IMPORTANT 4：Stop 持久成功后 admission registration 可能仍 Accepted。 | 已关联注册的 Host 事实观察 TerminalCompleted；删除回写调用的突变在目标断言失败。 | 验收成功终态回写的时序、异常/无映射调用者可观测性及重复调用幂等，不出现 run 已 Cancelled 而注册仍活跃。 |
-| IMPORTANT 5：queue/run/read/reservation 故障与墓碑先写窗口缺行为证据。 | 具名 queue 发布失败、run 发布失败可重试、读损坏、Resume reservation 竞争和 23 个目标断言突变。 | 验收每个失败返回、必要身份保留、exact-byte 责任与重试收敛；覆盖 tombstone 后 run 写失败窗口及其他外部责任源。 |
-
-以上均仅有施工方实现/本地证据，因 R2 未返回**不能记为会诊确认或原级闭环**。BO-11 的 BO-6 R19 MUST/R21 F4 IMPORTANT、BO-7 R21 F1/F2 MUST、BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT 仍按 §24.124.4 原级交 owner；组件/helper 不作为 Runner 端到端闭合。
-生产入口、真实 User、R5.8 签署、E3/E4/E5、热键面及 BGI 生产进程门继续关闭。所列是源码、助手构建/测试和组件/Host 接缝证据，不是 BGI 实机、真实 facade 调度或生产 User 验证。BO-6/7/8/9 的重要/必改风险仍未排除；不得据本收口开放其 successor/推进或收尾生产门。
+最终声明面清单在上述文档修正后以 `CLAIM_SURFACE_REGENERATE=1` 再生，再清除环境变量复跑；两次均通过且 SHA 稳定，确切 SHA 与独立 TRX 见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-evidence.json`。R8 两项目构建和定向/助手全量结果见同目录。未验证 BGI 生产进程、真实 User、R5.8、E3/E4/E5、热键面或实机；所有生产入口及运行门保持关闭。
+生产入口门、真实 User 门与 R5.8 签署继续关闭；停驻语义生产不可达（`ShouldRegisterLocalWait` 生产恒 null，`waitLocally` 无生产产出方）。不援引措辞类豁免；声明面清单变更随本批提交评审。R44 起新发现按 owner 止损裁决登记台账残项（BO-14 起），未修复项已在 §24.120.4 与台账逐条登记归属。E3/E4/E5、热键面及 BGI 生产进程也保持关闭。以上均为源码、助手构建和组件/Host 测试接缝证据，不是 BGI 实机、真实 facade 调度或生产 User 验证；BO-6/7/8/9 的重要/必改风险尚未排除，不据此打开推进或收尾生产门。
diff --git a/MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs b/MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs
index 0886db6d0..501c5f421 100644
--- a/MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs
+++ b/MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs
@@ -48,6 +48,9 @@ public sealed class RunStore
     /// </summary>
     internal Func<WorkflowRunRecord, Exception?>? PublishFaultForTest { get; set; }
 
+    /// <summary>测试专用的逐次 Load 前探针，用于固定 Host 两次读取之间的 run 文件身份/损坏交错；生产恒 null。</summary>
+    internal Action<string>? BeforeLoadForTest { get; set; }
+
     public RunStore(string runsDir)
     {
         // R4.8 二轮（重要2）：构造零副作用——目录推迟到首次 Persist 才创建
@@ -294,6 +297,7 @@ public sealed class RunStore
     /// <summary>读取运行记录（不存在返回 null；解析失败抛 JsonException——调用方按隔离处理，不回空）。</summary>
     public WorkflowRunRecord? Load(string runId)
     {
+        BeforeLoadForTest?.Invoke(runId);
         var file = PathFor(runId);
         // [第三轮会诊阻断项处置] **不得用 `File.Exists` 探测**（它会把「拒绝访问」静默折成 false ⇒ 误判为不存在）：
         // 直接读——NotFound ⇒ null（合法「无记录」）；争用 ⇒ 有界重试后原样抛出。
diff --git a/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs b/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs
index 12f089e41..8347cfa79 100644
--- a/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs
+++ b/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs
@@ -80,6 +80,23 @@ internal sealed class TaskCenterAdmissionSeams
 public sealed partial class TaskCenterHost
 {
     private readonly bool _admissionWired;
+
+    // Host-scoped fault seams for BO-13 terminal reconciliation tests; production leaves all null.
+    internal Func<int, Exception?>? AdmissionTerminalReadFaultForTest { get; set; }
+    internal Func<string, int, Exception?>? AdmissionTerminalWriteFaultForTest { get; set; }
+    internal Func<string, AdmissionResult?>? AdmissionTerminalResultForTest { get; set; }
+    internal Action? AdmissionTerminalReconciliationCompletedForTest { get; set; }
+    internal TimeSpan? AdmissionTerminalReconciliationTimeoutForTest { get; set; }
+
+    private enum AdmissionTerminalReconciliationOutcome
+    {
+        NotRequired,
+        NotTerminal,
+        NoMapping,
+        Completed,
+        Pending,
+        Failed,
+    }
     /// <summary>
     /// B2-γ 第 3 步「路径启用」独立门（§12.3 施工阻断：第 3 步尚不得启用相关路径）。
     /// `_admissionWired` 只表示 E1/E2 入口已接线；**节点后继提交改道必须另开此门**——生产构造恒不传
@@ -2514,93 +2531,203 @@ public sealed partial class TaskCenterHost
         return false;
     }
 
-    /// <summary>运行终态→仲裁操作终局回写（按 runBinding 反查 Operations；台账交叉确认经 TakeoverTerminalConfirmed 钩子）。</summary>
+    /// <summary>
+    /// 运行终态→仲裁操作终局回写。Runner finally 保持异步，避免受理管线同线程等待；显式 Stop 则等待有界结果，
+    /// 把未完成回写作为调用者可见的 pending，并允许对已 Cancelled run 再次 Stop 进行同会话/重启后重试。
+    /// </summary>
     private void MarkAdmissionTerminalIfAny(string? runId)
     {
-        if (!_admissionWired || runId is null || _admission is null) return;
-        // 受理管线内竞态（夹具实证）：驱动极快时运行先终态，门面尚在「占位→台账→关闭」途中（op=Granted/Sending）。
-        // 纪律：不得在本调用路径上同步等待受理收敛——entry.Task 已完成时 ObserveDriveAsync 整体内联于门面流水线程
-        // 执行，同步自旋=循环等待（实测 5s 停摆）。整个回写放线程池异步有界重试：管线毫秒级关闭后自会看到 Accepted；
-        // 崩溃/未决（Reconciling 等）=保守不回写（恢复路径按既有合同处置）；进程退出前未完成的回写由重启恢复兜底。
-        _ = Task.Run(async () =>
+        var work = ReconcileAdmissionTerminalAsync(runId);
+        _ = work.ContinueWith(task =>
         {
-            try
+            if (task.IsFaulted)
             {
-                var run = _runs.Load(runId);
-                if (run?.State is not (WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled)) return;
-                // B2-β（恢复接入后）：同一 runBinding 可有多笔操作（启动 op + 恢复 op 共享同一运行事实）——
-                // 运行终态=绑定该运行的全部已受理操作一同终局；有操作仍在过渡态（受理管线未关闭）则等有界窗口。
-                List<OperationRecord>? accepted = null;
-                // **[第六轮会诊重要项处置] 以墙钟界定整个收敛窗口**：`Read()` 在争用下自身最坏消耗 ≈1.2s 预算
-                // （80×15ms）；若仍按「次数」循环（500 次），持续不可读时窗口会被放大到 ≈10 分钟（500×1.2s）
-                // ＝可用性回归。故总墙钟 >15s 即放弃本轮（由下一次触发/恢复扫描再对账，保守方向且留日志）。
-                var settleClock = System.Diagnostics.Stopwatch.StartNew();
-                for (var spin = 0; spin < 500; spin++)
-                {
-                    if (settleClock.Elapsed > TimeSpan.FromSeconds(15)) break;   // 墙钟耗尽 ⇒ 走下方「放弃本轮」分支
-                    List<OperationRecord> current;
-                    try
-                    {
-                        var leaseRead = _admissionStore!.Read();
-                        if (leaseRead.Status == ArbitrationLeaseStatus.Corrupt)
-                        {
-                            // **[P50 复核·批次四十九]** 租约不可确认 ⇒ 按「尚未收敛」重试（有界 spin，
-                            // 且受上方**墙钟预算**约束 —— [第六轮会诊重要项处置]），
-                            // **不得**按「无映射」静默结束本轮回写（不可读 ≠ 确无映射）。
-                            await Task.Delay(20).ConfigureAwait(false);
-                            continue;
-                        }
-                        current = leaseRead.File?.Handoff?.Operations?
-                            .Where(o => string.Equals(o.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
-                    }
-                    catch (IOException)
-                    {
-                        // 锁文件（FileShare.None）瞬时争用：与门面自身写入/其他读取碰撞——按「尚未收敛」重试（跨进程互斥合同的调用方义务）。
-                        await Task.Delay(20).ConfigureAwait(false);
-                        continue;
-                    }
+                TryLog("[任务中心] 仲裁操作终局回写异常（保守留待显式重试）:" + task.Exception?.GetBaseException().Message);
+                return;
+            }
+            if (task.Result is AdmissionTerminalReconciliationOutcome.Pending or AdmissionTerminalReconciliationOutcome.Failed)
+                TryLog("[任务中心] 仲裁操作终局回写未确认（保守留待显式重试）。");
+        }, TaskScheduler.Default);
+    }
 
-                    if (current.Count == 0) return; // 无映射=零副作用
-                    if (current.All(o => o.RequestState is OperationRequestState.Reconciling
-                            or OperationRequestState.TerminalRejected or OperationRequestState.NotSelected
-                            or OperationRequestState.TerminalCompleted)) return; // 确定不会进入 Accepted=无需回写
-                    var ready = current.Where(o => o.RequestState == OperationRequestState.Accepted).ToList();
-                    var transitioning = current.Any(o => o.RequestState is OperationRequestState.Queued
-                        or OperationRequestState.InRound or OperationRequestState.Granted or OperationRequestState.Sending); // RetryableRejected=已定拒绝（不再转入 Accepted），不阻塞同胞回写
-                    if (ready.Count > 0 && !transitioning) { accepted = ready; break; }
-                    await Task.Delay(10).ConfigureAwait(false);
-                }
+    private HostActionResult ReconcileAdmissionTerminalForExplicitStop(string runId, string successMessage)
+    {
+        var work = ReconcileAdmissionTerminalAsync(runId);
+        var timeout = (AdmissionTerminalReconciliationTimeoutForTest ?? TimeSpan.FromSeconds(15))
+            + TimeSpan.FromSeconds(2);
+        AdmissionTerminalReconciliationOutcome outcome;
+        try
+        {
+            outcome = work.WaitAsync(timeout).GetAwaiter().GetResult();
+        }
+        catch (TimeoutException)
+        {
+            return HostActionResult.Unavailable("运行已终态化，但受理登记终局回写仍待确认；恢复存储后再次执行 Stop 重试");
+        }
+        catch (Exception ex)
+        {
+            return HostActionResult.Unavailable("运行已终态化，但受理登记终局回写失败；恢复存储后再次执行 Stop 重试："
+                + ex.GetType().Name + "（" + ex.Message + "）");
+        }
 
-                if (accepted is null)
-                {
-                    _log?.Invoke("[任务中心] 仲裁操作终局回写放弃：受理管线未在有界窗口内收敛（保守留待对账）。");
-                    return;
-                }
+        return outcome switch
+        {
+            AdmissionTerminalReconciliationOutcome.NotRequired or
+            AdmissionTerminalReconciliationOutcome.NoMapping or
+            AdmissionTerminalReconciliationOutcome.Completed => HostActionResult.Effective(successMessage),
+            _ => HostActionResult.Unavailable("运行已终态化，但受理登记终局回写尚未确认；恢复责任后再次执行 Stop 重试"),
+        };
+    }
 
-                foreach (var op in accepted)
-                {
-                    AdmissionResult? r = null;
-                    for (var attempt = 0; attempt < 5; attempt++) // 锁争用 IOException 有界重试（逻辑拒绝不重试）
-                    {
-                        try
-                        {
-                            r = _admission.MarkOperationTerminal(op.RequestIdentity, "runstore:" + run!.State);
-                            break;
-                        }
-                        catch (IOException) when (attempt < 4)
-                        {
-                            await Task.Delay(25).ConfigureAwait(false);
-                        }
-                    }
+    private Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalAsync(string? runId)
+        => Task.Run(() => ReconcileAdmissionTerminalCoreAsync(runId));
+
+    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreAsync(string? runId)
+    {
+        try
+        {
+            return await ReconcileAdmissionTerminalCoreBodyAsync(runId).ConfigureAwait(false);
+        }
+        finally
+        {
+            AdmissionTerminalReconciliationCompletedForTest?.Invoke();
+        }
+    }
+
+    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync(string? runId)
+    {
+        if (!_admissionWired) return AdmissionTerminalReconciliationOutcome.NotRequired;
+        if (string.IsNullOrWhiteSpace(runId))
+            return AdmissionTerminalReconciliationOutcome.Failed;
+
+        try { await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false); }
+        catch (Exception ex)
+        {
+            TryLog("[任务中心] 终局回写无法初始化受理存储（保守留待重试）:" + ex.Message);
+            return AdmissionTerminalReconciliationOutcome.Failed;
+        }
+        if (_admission is null || _admissionStore is null)
+            return AdmissionTerminalReconciliationOutcome.Failed;
 
-                    if (r is null || r.Kind == AdmissionResultKind.Error)
-                        _log?.Invoke($"[任务中心] 仲裁操作终局回写被拒（{r?.ReasonCode ?? "lock_contention"}）：{r?.Detail ?? "锁争用重试耗尽，保守留待对账"}");
+        WorkflowRunRecord? run;
+        try { run = _runs.Load(runId); }
+        catch (Exception ex)
+        {
+            TryLog("[任务中心] 终局回写无法读取运行记录（保守留待重试）:" + ex.Message);
+            return AdmissionTerminalReconciliationOutcome.Failed;
+        }
+        if (run is null || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
+            return AdmissionTerminalReconciliationOutcome.Failed;
+        if (run.State is not (WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled))
+            return AdmissionTerminalReconciliationOutcome.NotTerminal;
+
+        var timeout = AdmissionTerminalReconciliationTimeoutForTest ?? TimeSpan.FromSeconds(15);
+        var settleClock = System.Diagnostics.Stopwatch.StartNew();
+        var readAttempt = 0;
+        List<OperationRecord>? accepted = null;
+        List<OperationRecord>? current = null;
+        while (settleClock.Elapsed <= timeout)
+        {
+            try
+            {
+                var injected = AdmissionTerminalReadFaultForTest?.Invoke(++readAttempt);
+                if (injected is not null) throw injected;
+                var leaseRead = _admissionStore.Read();
+                if (leaseRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)
+                {
+                    await Task.Delay(20).ConfigureAwait(false);
+                    continue;
                 }
+                current = leaseRead.File?.Handoff?.Operations?
+                    .Where(op => string.Equals(op.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
+            }
+            catch (IOException)
+            {
+                await Task.Delay(20).ConfigureAwait(false);
+                continue;
             }
             catch (Exception ex)
             {
-                _log?.Invoke("[任务中心] 仲裁操作终局回写异常（保守留待对账）：" + ex.Message);
+                TryLog("[任务中心] 终局回写读取失败（保守留待重试）:" + ex.Message);
+                return AdmissionTerminalReconciliationOutcome.Failed;
             }
-        });
+
+            if (current.Count == 0) return AdmissionTerminalReconciliationOutcome.NoMapping;
+            if (current.All(IsAdmissionTerminalOrClosed)) return AdmissionTerminalReconciliationOutcome.Completed;
+
+            var ready = current.Where(op => op.RequestState == OperationRequestState.Accepted).ToList();
+            var transitioning = current.Any(op => op.RequestState is OperationRequestState.Queued
+                or OperationRequestState.InRound or OperationRequestState.Granted or OperationRequestState.Sending);
+            if (ready.Count > 0 && !transitioning)
+            {
+                accepted = ready;
+                break;
+            }
+            await Task.Delay(10).ConfigureAwait(false);
+        }
+
+        if (accepted is null)
+        {
+            TryLog("[任务中心] 仲裁操作终局回写等待超时/存在未决责任（保守留待显式重试）。");
+            return AdmissionTerminalReconciliationOutcome.Pending;
+        }
+
+        // A failure on one sibling must not skip writeback attempts for the other Accepted operations.
+        foreach (var op in accepted)
+        {
+            AdmissionResult? result = null;
+            Exception? failure = null;
+            for (var attempt = 1; attempt <= 5; attempt++)
+            {
+                try
+                {
+                    failure = AdmissionTerminalWriteFaultForTest?.Invoke(op.RequestIdentity, attempt);
+                    if (failure is not null) throw failure;
+                    result = AdmissionTerminalResultForTest?.Invoke(op.RequestIdentity)
+                        ?? _admission.MarkOperationTerminal(op.RequestIdentity, "runstore:" + run.State);
+                    break;
+                }
+                catch (IOException ex) when (attempt < 5)
+                {
+                    failure = ex;
+                    await Task.Delay(25).ConfigureAwait(false);
+                }
+                catch (Exception ex)
+                {
+                    failure = ex;
+                    break;
+                }
+            }
+            if (failure is not null || result is null || result.Kind == AdmissionResultKind.Error)
+                TryLog($"[任务中心] 仲裁操作终局回写被拒（{result?.ReasonCode ?? failure?.GetType().Name ?? "unknown"}）："
+                    + (result?.Detail ?? failure?.Message ?? "无结果，保守留待重试"));
+        }
+
+        // Confirm every operation bound to this run after independent write attempts.
+        try
+        {
+            var finalReadFault = AdmissionTerminalReadFaultForTest?.Invoke(++readAttempt);
+            if (finalReadFault is not null) throw finalReadFault;
+            var finalRead = _admissionStore.Read();
+            if (finalRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)
+                return AdmissionTerminalReconciliationOutcome.Pending;
+            current = finalRead.File?.Handoff?.Operations?
+                .Where(op => string.Equals(op.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
+        }
+        catch (Exception ex)
+        {
+            TryLog("[任务中心] 终局回写后复核失败（保守留待重试）:" + ex.Message);
+            return AdmissionTerminalReconciliationOutcome.Pending;
+        }
+
+        if (current.Count == 0) return AdmissionTerminalReconciliationOutcome.NoMapping;
+        return current.All(IsAdmissionTerminalOrClosed)
+            ? AdmissionTerminalReconciliationOutcome.Completed
+            : AdmissionTerminalReconciliationOutcome.Pending;
     }
+
+    private static bool IsAdmissionTerminalOrClosed(OperationRecord operation)
+        => operation.RequestState is OperationRequestState.TerminalCompleted
+            or OperationRequestState.TerminalRejected or OperationRequestState.NotSelected
+            or OperationRequestState.RetryableRejected;
 }
diff --git a/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs b/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs
index 438704503..f3635767a 100644
--- a/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs
+++ b/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs
@@ -379,6 +379,9 @@ public sealed partial class TaskCenterHost
     /// </summary>
     public HostActionResult RequestRunAction(string runId, WorkflowRunAction action)
     {
+        if (string.IsNullOrWhiteSpace(runId))
+            return HostActionResult.Unavailable("运行 runId 为空，未执行动作");
+
         WorkflowRunRecord? run;
         try { run = _runs.Load(runId); }
         catch (Exception ex)
@@ -394,6 +397,13 @@ public sealed partial class TaskCenterHost
         if (action == WorkflowRunAction.Stop && run.State == WorkflowRunState.LocalWaitParking)
             return StopParkedRun(runId);
 
+        // Explicit retry path after a previous Stop durably cancelled the run but admission
+        // reconciliation could not be confirmed. It is safe for any cancelled run: an empty
+        // runBinding lookup is a confirmed no-op, and already-terminal operations are not rewritten.
+        if (_admissionWired && action == WorkflowRunAction.Stop && run.State == WorkflowRunState.Cancelled)
+            return ReconcileAdmissionTerminalForExplicitStop(runId,
+                "运行已终态化，关联受理登记已核对");
+
         if (action == WorkflowRunAction.Stop && run.State == WorkflowRunState.Paused)
         {
             // 暂停态无驱动（引擎 _controls 已移除）：无在飞作业（暂停节点边界生效），宿主直接终态化
@@ -489,17 +499,15 @@ public sealed partial class TaskCenterHost
             }
         }
 
-        MarkAdmissionTerminalIfAny(runId);
         NotifyStateChanged();
-        return HostActionResult.Effective("已放弃停驻运行（终态化，未触发收尾）");
+        return ReconcileAdmissionTerminalForExplicitStop(runId,
+            "已放弃停驻运行（终态化，未触发收尾）");
     }
 
     /// <summary>Stop 专用的前置动作责任护栏；恢复扫描的既有状态分类保持不变。</summary>
     private static bool HasUnresolvedPrerequisiteResponsibility(WorkflowRunRecord run)
-        => run.PrerequisiteActions?.Any(action => action.SendAttempted
-            || !string.IsNullOrWhiteSpace(action.JobId)
-            || action.State is PrerequisiteActionState.Intent or PrerequisiteActionState.Submitted
-                or PrerequisiteActionState.Unknown) == true;
+        => run.PrerequisiteActions?.Any(action => action.State is not (
+            PrerequisiteActionState.Succeeded or PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled)) == true;
 
     private static bool HasValidParkedDecision(WorkflowRunRecord run, out LocalWaitBinding? binding)
     {
@@ -568,6 +576,9 @@ public sealed partial class TaskCenterHost
             || string.IsNullOrWhiteSpace(waitBinding.NodeId)
             || string.IsNullOrWhiteSpace(waitBinding.PrerequisiteReference)
             || waitBinding.RecordRevision <= 0 || waitBinding.RecordRevision > run.RecordRevision
+            // The queue binding is immutable across explicit repark: a fresh decision context may
+            // be newer, but it must never predate the binding snapshot it is validating.
+            || waitBinding.RecordRevision > context.RecordRevision
             || waitBinding.SequenceIndex != context.SequenceIndex
             || !string.Equals(waitBinding.RunId, run.RunId, StringComparison.Ordinal)
             || !string.Equals(waitBinding.WorkflowId, run.WorkflowId, StringComparison.Ordinal)
@@ -582,6 +593,7 @@ public sealed partial class TaskCenterHost
             || waitBinding.Attempt != cursor.Attempt
             || waitBinding.SourceKind != context.SourceKind
             || !string.Equals(context.SourceIdentity, waitBinding.SourceIdentity, StringComparison.Ordinal)
+            || !HasValidParkedSourceIdentity(run, waitBinding)
             || !string.Equals(context.Scope, waitBinding.Scope, StringComparison.Ordinal)
             || !string.Equals(context.CandidateId, waitBinding.CandidateId, StringComparison.Ordinal)
             || !string.Equals(context.AdmissionIdentity, waitBinding.AdmissionIdentity, StringComparison.Ordinal)
@@ -612,6 +624,24 @@ public sealed partial class TaskCenterHost
         return true;
     }
 
+    private static bool HasValidParkedSourceIdentity(WorkflowRunRecord run, LocalWaitBinding binding)
+    {
+        // Panel RequestIdentity is captured from the resolved FlowRegistration parent into both
+        // decision snapshots; it is not duplicated in RunStore. Stop uses it only as paired
+        // snapshot identity and never as submission authority.
+        if (binding.SourceKind == LocalWaitSourceKind.PanelFlowRegistration)
+            return true;
+
+        // StartupHandoff has an independent durable source in this run record. Do not let a
+        // matching context/binding pair substitute another run's handoff identity or scope.
+        return binding.SourceKind == LocalWaitSourceKind.StartupHandoff
+               && string.Equals(binding.SourceIdentity, run.RunId, StringComparison.Ordinal)
+               && IsCanonicalAdmissionScope(run.AdmissionSourceScope)
+               && string.Equals(binding.Scope, run.AdmissionSourceScope, StringComparison.Ordinal)
+               && (run.Handoffs ?? []).Any(handoff => handoff is not null
+                   && handoff.Mode is StartupHandoffModes.Start or StartupHandoffModes.ArmTrigger);
+    }
+
     // ================= R4.9 启动移交受理入口 =================
 
     /// <summary>本地执行能力守卫（R4.9 §6.2 + ASTRA 二轮 I2：先于恢复屏障与台账写入等一切副作用；绕过 VM 的调用同样被拦；
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
index b1b7dc196..84b9e01e7 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
@@ -196,7 +196,6 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md153BAF4EB40A98
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md15D91BEE148A191925D415A38892DD8E96756DA16BF6690DA01094D85B5796C31「**部分已交付·未验收**」——持久化父子绑定 ＋「E1 关闭后首节点另行取许可」夹具**已交付（组件/宿主层）**；
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md1811BBDB0BD9857B6E126A389E53C23CBF23152DCE8A51733072EE9D220565FF1| §24.41-C | 3 | 首节点绑定（§12.3 M1） | 部分已交付·未验收（绑定持久化＋首/后继各自取许可已交付） | 部分证据 | `TaskCenterSuccessorPathGateTests.NodeSubmit_AfterE1Closed_OwnDriveOccupied_PerNodePer…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md181F2B818155E658461261797B5845D921757F8E344ECAE17AE0D692A7654CA51当前前置／收尾执行体先在 `JobRegistry` 写 `Failed/Rejected` 再返回 `false`，共享队列却把 `false` 报成 `completed`。本切片只改**可信入口显式标为 Prerequisite/Terminal**的提交；普通配置组／一条龙的 `Task<bool>` 合同不变…
-Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md187D695213BB4E3813A7CF11A7513FECD593F946482E5ED9D31AD90EF23AE0651GPT 会诊与 owner 检查点：R1 为 GPT `gpt-6-astra` / medium，已发 1 次并提出五项 BO-13 **IMPORTANT**。R2 review 请求于 2026-09-27 20:00 UTC 已发，但会诊执行器返回 `GPT consultation failed for gp…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md18E594D3BC0E15196ADDD8322F9F389B31C728881BD26F7CAEF45C6650EA64FB1> **收口状态＝已收口（限定范围）**：经**四轮会诊**（第 1 轮 1 阻断／4 重要 ⇒ 第 2 轮 1 阻断／1 重要 ⇒
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md1933B1C0BF543F9011E2455E27DD41CBF23C758146DE10D0045E57A1E88FFED21| 等待基础组件（落盘/幂等/选择/清理/裁剪） | ✅ 限定收口（**未接线**：不进生产准入、不入 BGI 队列） |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md1A63877857B553E25B71AB0EDE8EFE5F36D892F92DEA9635568FDED18C2E74DC1| 生产接线 | ❌ 未接线：无事件源订阅、无宿主启动恢复扫描、无安全网调度者；本批只交付组件与注入式判定接缝 |
@@ -316,6 +315,7 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7AF7BD784F9E96
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7B3FCFC847FCF95CB018B0D3FF50DE90720052384ABE433A5F387DD1D8C2A8A21**未闭合**字符串（`OpCode = "abc`）⇒ 同样保守失败。二者均**只会拒绝**，不会静默放行。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7BB5CB289354D9C6BBAE0A8390C18A30F75FAD02E40FDCEB6C9F1275B457B6351### 24.102 落地登记：低优先级本地持久等待（基础组件，未接线）（2026-09-24；B.1）
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7C5A9CEB9294DEE0920954A3A570523CC62124CE34A15BC58AF7594295923A931| 27 | 重要 | 冷启动错误码断言过宽（`cold_start_required` 与 `result_unknown` 二选一） | **已修**：本夹具观察**适配器出口**，精确断言 `result_unknown`（不得泄漏核心层错误码）；并在夹具注释说明 `cold_start_required` 只由…
+Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7DA3F81B26F5CF9E60D8B96DFA645F2059DEFEB5E8AA84900CBE18386D4204D91BO-6 的现行 helper 在停驻与已完成锚点冲突时返回 `null` 并记录诊断，但 Runner 的 `hadBadOutcome` 不把 `waitLocally` 计作坏结果；真实 Runner 冲突失败/成功聚合语义仍未证明。R19 必改与 R21 F4 重要均原级交 owner。BO-7 已有 `(Lo…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7E9073703DAFE8B505321497CA3A37F51A5018869E65DFB4E6DB926DE7BD1A0C1- 裁定：首轮 4 必改＋3 重要；SOL 第 2 轮判 3 项未闭合，第 3 轮判 `state:null` 必改，第 4 轮判全部闭合且无新增必改、同意限定收口。
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7E92B274E07EA8EE4F10F1F63ED2A965727CE5B18E87598D57E148D6025A15EA1> **[更正·2026-09-22 批次五十]** 上句为**当时口径**：P8 的**判据接缝＋夹具**已交付（见 **§24.62**）——
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7F9BAE5F12E995153E0A5C3E7D405BB06F3C31A50B9757457D7033021702D9D11**未验收**，按 §16 收口判据**不得计入「已覆盖」**（§16／§16-A 已同步为 2 项已覆盖／4 项部分）。
@@ -365,7 +365,6 @@ Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdADA1C05E645AC2
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdADF33C7B491E2AAA799144C38F56CB2601E678CF0D4143C3D691828F201BE3891| 79 | 重要 | 交接稿标题仍「截至批次五十」；B′ 待办仍列 #11 | **已修**：标题同步为批次五十一；B′ 从待办移除并标注「已交付（组件/宿主层）」 |
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB10FC181E740EADDED0DDF05A87F11385F20A08DB7A77CFF674068ADF1EA36051**B. 状态**：§24.41-C#3「首节点绑定（§12.3 M1）」由「已移交·未验收」改为
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB29F5DEDA3D3404F8803277406BC0BA47DE2688A3086CB3675699E94479FBA421| §24.41-C | 4 | G4a 启动移交来源登记 | **已交付（组件/宿主层）** | 完成证据 | `TaskCenterSuccessorPathGateTests.NodeSubmit_AfterHandoffStart_InheritsRecordedFixedScope` |
-Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB38719306C1982967916400FC83069C05C1AC63561CA66791FA0E3D12DE679161BO-6 的现行 helper 在停驻与已完成锚点冲突时返回 `null` 并记录诊断，但 Runner 的 `hadBadOutcome` 不把 `waitLocally` 计作坏结果；当前证据不能证明冲突成为显式失败并阻止聚合成功。原 R19 必改与 R21 F4 重要风险均保留未闭合并交 owner。BO-7 现…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB4B61FD63B591E42EDB526212B1DE590782F86992D53E9BDBC0ECB15C637BFD912. **本轮已落地的安全阻断**：`ContinueUseAsync`／`RetryAsync` 对 `PreemptConfirmPending` 返回 `NeedPreemptConfirm`；`ValidateAndOccupy` 在跨进程原子占位事务内再次拒绝该标记，且本轮新发送不再顺手清标记。`Preemp…
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB75E5A2EACE1962029B168FCEDE360E83EB1BA34940BA7F8D7F81A4385C27D611把「逐项校验 `item.State`」改成「只看集合是否非空」之类的突变**尚未执行** ⇒ 该夹具目前**只证明正向行为**，
 Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB7D65826559758869081F647902117C519A91D2B7F609865046DAAB8F17B96CF1**B. 状态（[首轮会诊重要项处置] 不改写整行效力）**：§24.41-C#10 **整行仍记「部分已交付·未验收（未整体验收）」**——
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs
index d40f6e4be..38c530bef 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs
@@ -88,7 +88,8 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
             () => (true, null));
     }
 
-    private TaskCenterHost MakeWaitParkingHost(bool admissionWired = false, Action<int>? runnerFactoryEntered = null)
+    private TaskCenterHost MakeWaitParkingHost(bool admissionWired = false, Action<int>? runnerFactoryEntered = null,
+        Action<string>? log = null)
     {
         TaskCenterHost? host = null;
         var runnerFactoryCalls = 0;
@@ -104,7 +105,7 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
             ? new TaskCenterAdmissionSeams { Epoch = "9:900", Occupied = false, FactsUnknown = false }
             : null;
         host = new TaskCenterHost(_flowsDir, _runsDir, Path.Combine(_root, "catalog-cache.json"),
-            () => null, null,
+            () => null, log,
             (_, workflows, runs) =>
             {
                 runnerFactoryEntered?.Invoke(Interlocked.Increment(ref runnerFactoryCalls));
@@ -120,6 +121,23 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         return host;
     }
 
+    private async Task<(TaskCenterHost Host, string WorkflowId, WorkflowRunRecord Run)> StartAdmissionWiredParkedRun(
+        Action<string>? log = null)
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeWaitParkingHost(admissionWired: true, log: log);
+        var start = await host.StartWorkflowAsync(workflowId);
+        Assert.Equal(HostActionStatus.Registered, start.Status);
+        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(10)));
+        var run = Assert.Single(host.Runs.List());
+        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
+        return (host, workflowId, run);
+    }
+
+    private List<OperationRecord> ReadAdmissionOperationsForRun(string runId)
+        => new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read()
+            .File?.Handoff?.Operations?.Where(op => op.RunBinding == runId).ToList() ?? [];
+
     private static LocalWaitDecisionContext MakeValidContext(WaitDecisionRequest request, string scope, string sourceIdentity)
     {
         var candidate = TaskCenterHost.BuildSuccessorIdentityCandidate(scope, request.WorkflowId, request.RunId,
@@ -152,9 +170,20 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
 
     private WorkflowRunRecord SeedParkedRun(TaskCenterHost host, string workflowId,
         LocalWaitDecisionKind decisionKind, bool createQueueItem, bool unresolvedExternalFact = false,
-        bool driftQueuePayload = false)
+        bool driftQueuePayload = false, LocalWaitSourceKind sourceKind = LocalWaitSourceKind.PanelFlowRegistration)
     {
-        var run = _runs.CreateRun(workflowId, "revision-sb21-4");
+        const string scope = "bgi:local:test-epoch";
+        var handoff = sourceKind == LocalWaitSourceKind.StartupHandoff
+            ? new HandoffIdentity
+            {
+                IntentKey = "sb21-4-startup-handoff-" + Guid.NewGuid().ToString("N"),
+                ExecutionId = "sb21-4-execution",
+                StepId = "sb21-4-step",
+                Mode = StartupHandoffModes.Start,
+            }
+            : null;
+        var run = _runs.CreateRun(workflowId, "revision-sb21-4", handoff: handoff,
+            admissionSourceScope: handoff is null ? null : scope);
         const string nodeId = "n1";
         const int occurrence = 2;
         const int loopIteration = 1;
@@ -162,7 +191,6 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         var stableIdentity = $"{run.RunId}|{nodeId}|{occurrence}|{loopIteration}";
         var itemId = LocalWaitQueuePolicy.DeriveItemId(stableIdentity);
         var queuedAt = DateTimeOffset.UtcNow;
-        const string scope = "bgi:local:test-epoch";
         var candidate = TaskCenterHost.BuildSuccessorIdentityCandidate(scope, workflowId, run.RunId,
             nodeId, occurrence, loopIteration, attempt);
         var (admissionIdentity, candidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(candidate);
@@ -174,8 +202,8 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
             AdmissionIdentity = admissionIdentity,
             Namespace = workflowId,
             WorkflowId = workflowId,
-            SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
-            SourceIdentity = "request-" + run.RunId,
+            SourceKind = sourceKind,
+            SourceIdentity = sourceKind == LocalWaitSourceKind.StartupHandoff ? run.RunId : "request-" + run.RunId,
             RunId = run.RunId,
             Scope = scope,
             WorkflowRevision = run.WorkflowRevision,
@@ -233,7 +261,7 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
             Occurrence = occurrence,
             LoopIteration = loopIteration,
             Attempt = attempt,
-            SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
+            SourceKind = sourceKind,
             SourceIdentity = binding.SourceIdentity,
             Scope = binding.Scope,
             CandidateId = binding.CandidateId,
@@ -395,8 +423,12 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         var firstRun = Assert.Single(new RunStore(_runsDir).List());
         Assert.Equal(WorkflowRunState.LocalWaitParking, firstRun.State);
         var firstBinding = firstRun.LocalWaitDecision?.Binding;
+        var firstContextRevision = firstRun.LocalWaitDecision!.Context.RecordRevision;
         Assert.NotNull(firstBinding);
-        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);
+        var firstQueueItem = Assert.Single(host.LocalWaitQueue.Load());
+        Assert.Equal(LocalWaitItemState.Waiting, firstQueueItem.State);
+        using var queueBeforeResume = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
+        var generationHighWaterBeforeResume = queueBeforeResume.RootElement.GetProperty("generationHighWater").GetInt64();
 
         var resume = await host.ResumeRunAsync(firstRun.RunId);
         Assert.Equal(HostActionStatus.Registered, resume.Status);
@@ -409,7 +441,18 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         Assert.Equal(firstRun.Cursor.Attempt, reparking.Cursor.Attempt);
         Assert.Equal(firstRun.CurrentSubmission!.Key, reparking.CurrentSubmission!.Key);
         Assert.Equal(firstBinding, reparking.LocalWaitDecision!.Binding);
-        Assert.Equal(firstBinding!.ItemId, Assert.Single(host.LocalWaitQueue.Load()).ItemId);
+        Assert.True(reparking.LocalWaitDecision.Context.RecordRevision > firstContextRevision,
+            "same-process Resume must refresh the decision snapshot while retaining the original queue binding");
+        Assert.True(reparking.LocalWaitDecision.Binding!.RecordRevision <= reparking.LocalWaitDecision.Context.RecordRevision,
+            "valid repark keeps the immutable queue binding at or before its refreshed decision snapshot");
+        Assert.True(reparking.LocalWaitDecision.Context.RecordRevision < reparking.RecordRevision,
+            "valid repark refreshes the decision snapshot before the run record advances again");
+        var queueAfterRepark = Assert.Single(host.LocalWaitQueue.Load());
+        Assert.Equal(firstBinding!.ItemId, queueAfterRepark.ItemId);
+        Assert.Equal(firstQueueItem.Generation, queueAfterRepark.Generation);
+        using var queueAfterReparkJson = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
+        Assert.Equal(generationHighWaterBeforeResume,
+            queueAfterReparkJson.RootElement.GetProperty("generationHighWater").GetInt64());
 
         var stop = host.RequestRunAction(firstRun.RunId, WorkflowRunAction.Stop);
         Assert.True(stop.Status == HostActionStatus.Effective, stop.Message);
@@ -425,6 +468,69 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
             && run.WorkflowId == workflowId && run.State == WorkflowRunState.LocalWaitParking);
     }
 
+    [Fact]
+    public async Task RestartRecoveryThenResumeReparksRetainsQueueGenerationAndAllowsExplicitStop()
+    {
+        var workflowId = SeedWorkflow();
+        var firstHost = MakeWaitParkingHost();
+        var firstStart = await firstHost.StartWorkflowAsync(workflowId);
+        Assert.Equal(HostActionStatus.Registered, firstStart.Status);
+        Assert.True(SpinWait.SpinUntil(() => !firstHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
+        var originalRun = Assert.Single(firstHost.Runs.List());
+        Assert.Equal(WorkflowRunState.LocalWaitParking, originalRun.State);
+        var originalBinding = originalRun.LocalWaitDecision!.Binding!;
+        var originalItem = Assert.Single(firstHost.LocalWaitQueue.Load());
+        using var beforeRestartQueue = JsonDocument.Parse(File.ReadAllText(firstHost.LocalWaitQueue.FilePath));
+        var originalHighWater = beforeRestartQueue.RootElement.GetProperty("generationHighWater").GetInt64();
+        await firstHost.ShutdownAsync();
+
+        var recoveredHost = MakeWaitParkingHost();
+        try
+        {
+            recoveredHost.EnsureRecovered();
+            var recovered = recoveredHost.Runs.Load(originalRun.RunId)!;
+            Assert.Equal(WorkflowRunState.Interrupted, recovered.State);
+            Assert.True(recovered.RecordRevision > originalRun.RecordRevision,
+                "startup recovery must persist the LocalWaitParking to Interrupted transition before explicit Resume");
+            Assert.Equal(originalBinding, recovered.LocalWaitDecision!.Binding);
+            Assert.Equal(originalRun.Cursor!.NodeId, recovered.Cursor!.NodeId);
+            Assert.Equal(originalRun.Cursor.Occurrence, recovered.Cursor.Occurrence);
+            Assert.Equal(originalRun.Cursor.LoopIteration, recovered.Cursor.LoopIteration);
+            Assert.Equal(originalRun.Cursor.Attempt, recovered.Cursor.Attempt);
+            Assert.Equal(originalRun.LocalWaitDecision!.Context.RecordRevision,
+                recovered.LocalWaitDecision.Context.RecordRevision);
+
+            var resume = await recoveredHost.ResumeRunAsync(originalRun.RunId);
+            Assert.Equal(HostActionStatus.Registered, resume.Status);
+            Assert.True(SpinWait.SpinUntil(() => !recoveredHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
+            var reparking = recoveredHost.Runs.Load(originalRun.RunId)!;
+            Assert.Equal(WorkflowRunState.LocalWaitParking, reparking.State);
+            Assert.Equal(originalBinding, reparking.LocalWaitDecision!.Binding);
+            Assert.True(reparking.LocalWaitDecision.Context.RecordRevision > recovered.LocalWaitDecision!.Context.RecordRevision,
+                "explicit Resume must refresh the decision snapshot while retaining the prior immutable binding");
+            Assert.True(reparking.LocalWaitDecision.Binding!.RecordRevision <= reparking.LocalWaitDecision.Context.RecordRevision);
+            Assert.True(reparking.LocalWaitDecision.Context.RecordRevision < reparking.RecordRevision);
+            var reparkedItem = Assert.Single(recoveredHost.LocalWaitQueue.Load());
+            Assert.Equal(originalItem.Generation, reparkedItem.Generation);
+            using var afterRestartQueue = JsonDocument.Parse(File.ReadAllText(recoveredHost.LocalWaitQueue.FilePath));
+            Assert.Equal(originalHighWater, afterRestartQueue.RootElement.GetProperty("generationHighWater").GetInt64());
+
+            var stop = recoveredHost.RequestRunAction(originalRun.RunId, WorkflowRunAction.Stop);
+            Assert.Equal(HostActionStatus.Effective, stop.Status);
+            Assert.Equal(WorkflowRunState.Cancelled, recoveredHost.Runs.Load(originalRun.RunId)!.State);
+            Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(recoveredHost.LocalWaitQueue.Load()).State);
+            using var afterStopQueue = JsonDocument.Parse(File.ReadAllText(recoveredHost.LocalWaitQueue.FilePath));
+            Assert.Equal(originalHighWater, afterStopQueue.RootElement.GetProperty("generationHighWater").GetInt64());
+
+            var nextStart = await recoveredHost.StartWorkflowAsync(workflowId);
+            Assert.Equal(HostActionStatus.Registered, nextStart.Status);
+            Assert.True(SpinWait.SpinUntil(() => !recoveredHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
+            Assert.Contains(recoveredHost.Runs.List(), candidate => candidate.RunId != originalRun.RunId
+                && candidate.WorkflowId == workflowId && candidate.State == WorkflowRunState.LocalWaitParking);
+        }
+        finally { await recoveredHost.ShutdownAsync(); }
+    }
+
     [Fact]
     public void LocalWaitHoldStop_TerminatesPersistedRejectedRegistrationWithoutBinding()
     {
@@ -495,6 +601,166 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
     }
 
+    [Theory]
+    [InlineData(PrerequisiteActionState.Intent)]
+    [InlineData(PrerequisiteActionState.Submitted)]
+    [InlineData(PrerequisiteActionState.Unknown)]
+    [InlineData((PrerequisiteActionState)12345)]
+    public void LocalWaitParkingStop_IndependentlyRefusesEachUnresolvedPrerequisiteState(
+        PrerequisiteActionState state)
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
+        {
+            NodeId = run.Cursor!.NodeId,
+            Occurrence = run.Cursor.Occurrence,
+            LoopIteration = run.Cursor.LoopIteration,
+            Attempt = run.Cursor.Attempt,
+            StrategyIndex = 1,
+            Kind = "account.switch",
+            IdempotencyKey = "prereq-state-" + state + "-" + run.RunId,
+            Fingerprint = "fixture",
+            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
+            State = state,
+        });
+        _runs.Update(run);
+        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytes = File.ReadAllBytes(runPath);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Unavailable, result.Status);
+        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+    }
+
+    [Theory]
+    [InlineData(true, null)]
+    [InlineData(false, "known-job-with-unresolved-state")]
+    public void LocalWaitParkingStop_UnknownPrerequisiteRemainsUnresolvedForEachPersistedSendFact(
+        bool sendAttempted, string? jobId)
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
+        {
+            NodeId = run.Cursor!.NodeId,
+            Occurrence = run.Cursor.Occurrence,
+            LoopIteration = run.Cursor.LoopIteration,
+            Attempt = run.Cursor.Attempt,
+            StrategyIndex = 1,
+            Kind = "account.switch",
+            IdempotencyKey = "prereq-unknown-fact-" + run.RunId,
+            Fingerprint = "fixture",
+            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
+            SendAttempted = sendAttempted,
+            JobId = jobId,
+            State = PrerequisiteActionState.Unknown,
+        });
+        _runs.Update(run);
+        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytes = File.ReadAllBytes(runPath);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Unavailable, result.Status);
+        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+    }
+
+    [Theory]
+    [InlineData(PrerequisiteActionState.Succeeded)]
+    [InlineData(PrerequisiteActionState.Failed)]
+    [InlineData(PrerequisiteActionState.Cancelled)]
+    public void LocalWaitParkingStop_AllowsKnownTerminalPrerequisiteResponsibilities(
+        PrerequisiteActionState state)
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
+        {
+            NodeId = run.Cursor!.NodeId,
+            Occurrence = run.Cursor.Occurrence,
+            LoopIteration = run.Cursor.LoopIteration,
+            Attempt = run.Cursor.Attempt,
+            StrategyIndex = 1,
+            Kind = "account.switch",
+            IdempotencyKey = "prereq-terminal-" + state + "-" + run.RunId,
+            Fingerprint = "fixture",
+            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
+            State = state,
+        });
+        _runs.Update(run);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Effective, result.Status);
+        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(run.RunId)!.State);
+        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
+    }
+
+    [Fact]
+    public void LocalWaitParkingStop_DoesNotTreatTerminalSendAttemptAsUnresolvedResponsibility()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
+        {
+            NodeId = run.Cursor!.NodeId,
+            Occurrence = run.Cursor.Occurrence,
+            LoopIteration = run.Cursor.LoopIteration,
+            Attempt = run.Cursor.Attempt,
+            StrategyIndex = 1,
+            Kind = "account.switch",
+            IdempotencyKey = "prereq-complete-send-" + run.RunId,
+            Fingerprint = "fixture",
+            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
+            SendAttempted = true,
+            State = PrerequisiteActionState.Succeeded,
+        });
+        _runs.Update(run);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Effective, result.Status);
+        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
+    }
+
+    [Fact]
+    public void LocalWaitParkingStop_DoesNotTreatTerminalJobIdAsUnresolvedResponsibility()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
+        {
+            NodeId = run.Cursor!.NodeId,
+            Occurrence = run.Cursor.Occurrence,
+            LoopIteration = run.Cursor.LoopIteration,
+            Attempt = run.Cursor.Attempt,
+            StrategyIndex = 1,
+            Kind = "account.switch",
+            IdempotencyKey = "prereq-complete-job-" + run.RunId,
+            Fingerprint = "fixture",
+            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
+            JobId = "known-terminal-job",
+            State = PrerequisiteActionState.Succeeded,
+        });
+        _runs.Update(run);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Effective, result.Status);
+        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
+    }
+
     [Fact]
     public void LocalWaitParkingStop_RejectsDecisionWhoseCursorSnapshotDisagrees()
     {
@@ -555,6 +821,51 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
     }
 
+    [Fact]
+    public void LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
+            createQueueItem: true, sourceKind: LocalWaitSourceKind.StartupHandoff);
+        var otherRun = _runs.CreateRun(workflowId, "revision-sb21-4-other");
+        var decision = run.LocalWaitDecision!;
+        run.LocalWaitDecision = decision with
+        {
+            Binding = decision.Binding! with { SourceIdentity = otherRun.RunId },
+            Context = decision.Context with { SourceIdentity = otherRun.RunId },
+        };
+        _runs.Update(run);
+        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytes = File.ReadAllBytes(runPath);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Unavailable, result.Status);
+        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+        Assert.Equal(WorkflowRunState.LocalWaitParking, new RunStore(_runsDir).Load(run.RunId)!.State);
+        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);
+    }
+
+    [Fact]
+    public void LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
+            createQueueItem: true, sourceKind: LocalWaitSourceKind.StartupHandoff);
+        Assert.Equal(run.RunId, run.LocalWaitDecision!.Binding!.SourceIdentity);
+        Assert.Equal(run.RunId, run.LocalWaitDecision.Context.SourceIdentity);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Effective, result.Status);
+        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(run.RunId)!.State);
+        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
+    }
+
     [Fact]
     public void LocalWaitParkingStop_RejectsCanonicalAdmissionIdentityFromWrongCandidateNamespace()
     {
@@ -625,6 +936,192 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
     }
 
+    [Fact]
+    public void RequestRunAction_RejectsEmptyRunIdBeforeReadingOrTouchingStores()
+    {
+        var host = MakeHost();
+        var result = host.RequestRunAction(" ", WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Unavailable, result.Status);
+        Assert.Contains("runId 为空", result.Message);
+        Assert.Empty(host.Runs.List());
+        Assert.False(File.Exists(host.LocalWaitQueue.FilePath));
+    }
+
+    [Fact]
+    public void LocalWaitParkingStop_FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var runA = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        var runB = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        var pathA = Path.Combine(_runsDir, runA.RunId + ".run.json");
+        var pathB = Path.Combine(_runsDir, runB.RunId + ".run.json");
+        var runABytes = File.ReadAllBytes(pathA);
+        var runBBytes = File.ReadAllBytes(pathB);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+        var loadCount = 0;
+        host.Runs.BeforeLoadForTest = id =>
+        {
+            if (id == runA.RunId && Interlocked.Increment(ref loadCount) == 2)
+                File.WriteAllBytes(pathA, runBBytes);
+        };
+
+        HostActionResult? action = null;
+        Exception? thrown;
+        try { thrown = Record.Exception(() => action = host.RequestRunAction(runA.RunId, WorkflowRunAction.Stop)); }
+        finally { host.Runs.BeforeLoadForTest = null; }
+
+        Assert.Null(thrown);
+        Assert.Equal(2, loadCount);
+        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
+        Assert.NotEqual(runABytes, runBBytes);
+        Assert.Equal(runBBytes, File.ReadAllBytes(pathA));
+        Assert.Equal(runBBytes, File.ReadAllBytes(pathB));
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+    }
+
+    [Fact]
+    public void LocalWaitParkingStop_FinalReadMalformedRecordIsUnavailableAndPreservesBytes()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytes = File.ReadAllBytes(path);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+        var malformedBytes = System.Text.Encoding.UTF8.GetBytes("{ malformed-final-read");
+        var loadCount = 0;
+        host.Runs.BeforeLoadForTest = id =>
+        {
+            if (id == run.RunId && Interlocked.Increment(ref loadCount) == 2)
+                File.WriteAllBytes(path, malformedBytes);
+        };
+
+        HostActionResult? action = null;
+        Exception? thrown;
+        try { thrown = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop)); }
+        finally { host.Runs.BeforeLoadForTest = null; }
+
+        Assert.Null(thrown);
+        Assert.Equal(2, loadCount);
+        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
+        Assert.Contains("停驻运行记录复核失败", action.Message);
+        Assert.Equal(malformedBytes, File.ReadAllBytes(path));
+        Assert.NotEqual(runBytes, malformedBytes);
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+    }
+
+    [Fact]
+    public void LocalWaitParkingStop_FinalReadExceptionIsUnavailableAndPreservesBytes()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytes = File.ReadAllBytes(path);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+        var loadCount = 0;
+        host.Runs.BeforeLoadForTest = id =>
+        {
+            if (id == run.RunId && Interlocked.Increment(ref loadCount) == 2)
+                throw new IOException("SB21-4 injected final run read failure");
+        };
+
+        HostActionResult? action = null;
+        Exception? thrown;
+        try { thrown = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop)); }
+        finally { host.Runs.BeforeLoadForTest = null; }
+
+        Assert.Null(thrown);
+        Assert.Equal(2, loadCount);
+        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
+        Assert.Contains("停驻运行记录复核失败", action.Message);
+        Assert.Equal(runBytes, File.ReadAllBytes(path));
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+    }
+
+    [Fact]
+    public void LocalWaitParkingStop_RejectsDecisionSnapshotOlderThanItsBindingAndPreservesBytes()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        var decision = run.LocalWaitDecision!;
+        var binding = decision.Binding!;
+        var newerBinding = binding with { RecordRevision = binding.RecordRevision + 1 };
+        run.LocalWaitDecision = decision with
+        {
+            Binding = newerBinding,
+        };
+        host.Runs.Update(run);
+        host.LocalWaitQueue.Upsert(newerBinding.ToQueueItem());
+
+        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytes = File.ReadAllBytes(path);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Unavailable, result.Status);
+        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
+        Assert.Equal(runBytes, File.ReadAllBytes(path));
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+    }
+
+    [Theory]
+    [InlineData("binding-zero")]
+    [InlineData("decision-zero")]
+    [InlineData("both-snapshots-future")]
+    public void LocalWaitParkingStop_RejectsNonpositiveOrFutureBindingSnapshotsAndPreservesBytes(string invalidRevision)
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeHost();
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
+        var decision = run.LocalWaitDecision!;
+        var binding = decision.Binding!;
+        switch (invalidRevision)
+        {
+            case "binding-zero":
+                binding = binding with { RecordRevision = 0 };
+                decision = decision with { Binding = binding };
+                break;
+            case "decision-zero":
+                binding = binding with { RecordRevision = 0 };
+                decision = decision with
+                {
+                    Binding = binding,
+                    Context = decision.Context! with { RecordRevision = 0 },
+                };
+                break;
+            case "both-snapshots-future":
+                var futureRevision = run.RecordRevision + 3;
+                binding = binding with { RecordRevision = futureRevision };
+                decision = decision with
+                {
+                    Binding = binding,
+                    Context = decision.Context! with { RecordRevision = futureRevision },
+                };
+                break;
+            default:
+                throw new ArgumentOutOfRangeException(nameof(invalidRevision), invalidRevision, null);
+        }
+        run.LocalWaitDecision = decision;
+        host.Runs.Update(run);
+        host.LocalWaitQueue.Upsert(binding.ToQueueItem());
+
+        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytes = File.ReadAllBytes(runPath);
+        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
+
+        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+        Assert.Equal(HostActionStatus.Unavailable, result.Status);
+        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
+        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
+        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
+    }
+
     [Fact]
     public void LocalWaitParkingStop_QueuePublishFailurePreservesBytesAndCanRetry()
     {
@@ -702,7 +1199,12 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
 
         var resumeTask = Task.Run(() => host.ResumeRunAsync(run.RunId));
         Assert.True(enteredFactory.Wait(TimeSpan.FromSeconds(5)), "resume runner factory did not reach reserved window");
+        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
+        var runBytesWhileReserved = File.ReadAllBytes(runPath);
+        var queueBytesWhileReserved = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
         var stop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+        Assert.Equal(runBytesWhileReserved, File.ReadAllBytes(runPath));
+        Assert.Equal(queueBytesWhileReserved, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
         releaseFactory.Set();
         var resume = await resumeTask;
 
@@ -763,4 +1265,342 @@ public sealed class LocalWaitFinalizationContractTests : IDisposable
         }
         finally { await host.ShutdownAsync(); }
     }
+
+    [Fact]
+    public async Task LocalWaitParkingStop_OverlappingRetriesWriteOneTerminalTransitionAndIsolateOtherRun()
+    {
+        var (host, _, run) = await StartAdmissionWiredParkedRun();
+        try
+        {
+            var otherWorkflowId = SeedWorkflow();
+            var otherStart = await host.StartWorkflowAsync(otherWorkflowId);
+            Assert.Equal(HostActionStatus.Registered, otherStart.Status);
+            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(otherWorkflowId), TimeSpan.FromSeconds(10)));
+            var otherRun = Assert.Single(host.Runs.List().Where(candidate => candidate.WorkflowId == otherWorkflowId));
+
+            var admissionPath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
+            var store = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration"));
+            var before = store.Read().File!;
+            var target = Assert.Single(before.Handoff!.Operations!.Where(op => op.RunBinding == run.RunId));
+            Assert.Equal(OperationRequestState.Accepted, target.RequestState);
+            var unrelatedBefore = JsonSerializer.SerializeToUtf8Bytes(
+                Assert.Single(before.Handoff.Operations, op => op.RunBinding == otherRun.RunId));
+
+            host.AdmissionTerminalWriteFaultForTest = (_, _) => new IOException("seed pending admission retry");
+            var initialStop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+            Assert.Equal(HostActionStatus.Unavailable, initialStop.Status);
+            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
+            Assert.Equal(OperationRequestState.Accepted,
+                Assert.Single(ReadAdmissionOperationsForRun(run.RunId)).RequestState);
+            host.AdmissionTerminalWriteFaultForTest = null;
+            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(50);
+
+            var arrivalCount = 0;
+            var reconciliationCompletionCount = 0;
+            using var bothReconciliationsCompleted = new ManualResetEventSlim();
+            host.AdmissionTerminalReconciliationCompletedForTest = () =>
+            {
+                if (Interlocked.Increment(ref reconciliationCompletionCount) >= 2)
+                    bothReconciliationsCompleted.Set();
+            };
+            var terminalIdentityCalls = new System.Collections.Concurrent.ConcurrentQueue<string>();
+            using var bothAtTerminalWrite = new Barrier(2);
+            host.AdmissionTerminalResultForTest = identity =>
+            {
+                terminalIdentityCalls.Enqueue(identity);
+                Interlocked.Increment(ref arrivalCount);
+                if (!bothAtTerminalWrite.SignalAndWait(TimeSpan.FromSeconds(10)))
+                    throw new TimeoutException("overlapping Stop calls did not reach the same terminal-write boundary");
+                return null;
+            };
+
+            var first = Task.Run(() => host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));
+            var timedOut = await first;
+            Assert.Equal(HostActionStatus.Unavailable, timedOut.Status);
+            Assert.Contains("再次执行 Stop 重试", timedOut.Message);
+
+            var retry = await Task.Run(() => host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));
+
+            Assert.Equal(HostActionStatus.Effective, retry.Status);
+            Assert.True(bothReconciliationsCompleted.Wait(TimeSpan.FromSeconds(5)),
+                "both the timed-out first worker and the retry reconciliation must exit before inspecting final bytes");
+            host.AdmissionTerminalReconciliationCompletedForTest = null;
+            Assert.True(reconciliationCompletionCount >= 2);
+            var after = store.Read().File!;
+            var terminal = Assert.Single(after.Handoff!.Operations!, op => op.RunBinding == run.RunId);
+            Assert.Equal(OperationRequestState.TerminalCompleted, terminal.RequestState);
+            Assert.Equal(unrelatedBefore, JsonSerializer.SerializeToUtf8Bytes(
+                Assert.Single(after.Handoff.Operations, op => op.RunBinding == otherRun.RunId)));
+            Assert.All(terminalIdentityCalls, identity => Assert.Equal(target.RequestIdentity, identity));
+            Assert.Equal(before.Revision + 1, after.Revision);
+            Assert.Equal(after.Revision, terminal.UpdatedRevision);
+            Assert.Equal(2, arrivalCount);
+
+            var completedLeaseBytes = File.ReadAllBytes(admissionPath);
+            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+            Assert.Equal(HostActionStatus.Effective, repeated.Status);
+            Assert.Equal(completedLeaseBytes, File.ReadAllBytes(admissionPath));
+        }
+        finally { await host.ShutdownAsync(); }
+    }
+
+    [Fact]
+    public async Task LocalWaitParkingStop_ReadTimeoutIsVisibleAndSameSessionStopRetriesToTerminal()
+    {
+        var (host, _, run) = await StartAdmissionWiredParkedRun();
+        try
+        {
+            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(60);
+            host.AdmissionTerminalReadFaultForTest = _ => new IOException("injected admission read outage");
+
+            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Unavailable, first.Status);
+            Assert.Contains("再次执行 Stop 重试", first.Message);
+            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
+            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);
+
+            host.AdmissionTerminalReadFaultForTest = null;
+            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromSeconds(2);
+            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, retry.Status);
+            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
+            var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
+            var terminalBytes = File.ReadAllBytes(leasePath);
+            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+            Assert.Equal(HostActionStatus.Effective, repeated.Status);
+            Assert.Equal(terminalBytes, File.ReadAllBytes(leasePath));
+        }
+        finally { await host.ShutdownAsync(); }
+    }
+
+    [Fact]
+    public async Task LocalWaitParkingStop_LogicalAdmissionRejectionRemainsVisibleAndCanRetry()
+    {
+        var (host, _, run) = await StartAdmissionWiredParkedRun();
+        try
+        {
+            host.AdmissionTerminalResultForTest = identity => AdmissionResult.Of(
+                AdmissionResultKind.Error, "injected_logical_rejection", "terminal write refused", identity);
+
+            var rejected = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Unavailable, rejected.Status);
+            Assert.Contains("再次执行 Stop 重试", rejected.Message);
+            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
+            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);
+
+            host.AdmissionTerminalResultForTest = null;
+            var retried = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, retried.Status);
+            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
+        }
+        finally { await host.ShutdownAsync(); }
+    }
+
+    [Fact]
+    public async Task LocalWaitParkingStop_WriteExhaustionProcessesSiblingAndRetryDoesNotRewriteTerminalSibling()
+    {
+        var loggerFailures = 0;
+        var (host, _, run) = await StartAdmissionWiredParkedRun(message =>
+        {
+            if (!message.Contains("终局回写被拒", StringComparison.Ordinal)) return;
+            Interlocked.Increment(ref loggerFailures);
+            throw new InvalidOperationException("injected terminal reconciliation logger failure");
+        });
+        try
+        {
+            // Recovery admission creates a second legitimate operation bound to this same parked run.
+            var resumed = await host.ResumeRunAsync(run.RunId);
+            Assert.Equal(HostActionStatus.Registered, resumed.Status);
+            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(run.WorkflowId), TimeSpan.FromSeconds(10)),
+                "resume fixture must settle back into LocalWaitParking before explicit Stop begins");
+            Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
+            var accepted = ReadAdmissionOperationsForRun(run.RunId)
+                .Where(op => op.RequestState == OperationRequestState.Accepted).OrderBy(op => op.RequestIdentity).ToList();
+            Assert.True(accepted.Count >= 2, "the fixture must expose multiple Accepted registrations for one run");
+            var failingIdentity = accepted[0].RequestIdentity;
+            var writeAttempts = 0;
+            host.AdmissionTerminalWriteFaultForTest = (identity, attempt) =>
+            {
+                if (!string.Equals(identity, failingIdentity, StringComparison.Ordinal)) return null;
+                Interlocked.Increment(ref writeAttempts);
+                return new IOException("injected sibling terminal write outage " + attempt);
+            };
+
+            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Unavailable, first.Status);
+            Assert.Equal(5, writeAttempts);
+            Assert.Equal(1, loggerFailures);
+            var afterPartial = ReadAdmissionOperationsForRun(run.RunId);
+            var terminalSibling = Assert.Single(afterPartial.Where(op => op.RequestIdentity != failingIdentity));
+            Assert.Equal(OperationRequestState.TerminalCompleted, terminalSibling.RequestState);
+            Assert.Equal(OperationRequestState.Accepted, Assert.Single(afterPartial, op => op.RequestIdentity == failingIdentity).RequestState);
+            var terminalRevision = terminalSibling.UpdatedRevision;
+            var terminalUpdatedAt = terminalSibling.UpdatedAtUtc;
+
+            host.AdmissionTerminalWriteFaultForTest = null;
+            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, retry.Status);
+            var afterRetry = ReadAdmissionOperationsForRun(run.RunId);
+            Assert.All(afterRetry, op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
+            var unchangedSibling = Assert.Single(afterRetry, op => op.RequestIdentity != failingIdentity);
+            Assert.Equal(terminalRevision, unchangedSibling.UpdatedRevision);
+            Assert.Equal(terminalUpdatedAt, unchangedSibling.UpdatedAtUtc);
+            var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
+            var completedBytes = File.ReadAllBytes(leasePath);
+
+            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+            Assert.Equal(HostActionStatus.Effective, repeated.Status);
+            Assert.Equal(completedBytes, File.ReadAllBytes(leasePath));
+        }
+        finally { await host.ShutdownAsync(); }
+    }
+
+    [Fact]
+    public async Task LocalWaitParkingStop_PendingAdmissionTerminalizationCanRecoverAfterHostRestart()
+    {
+        var (firstHost, _, run) = await StartAdmissionWiredParkedRun();
+        firstHost.AdmissionTerminalWriteFaultForTest = (_, _) => new IOException("injected persistent write outage");
+        var first = firstHost.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+        Assert.Equal(HostActionStatus.Unavailable, first.Status);
+        Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);
+        await firstHost.ShutdownAsync();
+
+        var restarted = MakeWaitParkingHost(admissionWired: true);
+        try
+        {
+            var recovered = restarted.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, recovered.Status);
+            Assert.Equal(WorkflowRunState.Cancelled, restarted.Runs.Load(run.RunId)!.State);
+            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
+        }
+        finally { await restarted.ShutdownAsync(); }
+    }
+
+    [Fact]
+    public async Task LocalWaitParkingStop_FinalAdmissionReadFailureIsVisibleAndRetryCompletes()
+    {
+        var (host, _, run) = await StartAdmissionWiredParkedRun();
+        try
+        {
+            host.AdmissionTerminalReadFaultForTest = attempt => attempt == 2
+                ? new IOException("injected post-write confirmation failure") : null;
+
+            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Unavailable, first.Status);
+            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.TerminalCompleted);
+            host.AdmissionTerminalReadFaultForTest = null;
+
+            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, retry.Status);
+            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
+        }
+        finally { await host.ShutdownAsync(); }
+    }
+
+    [Theory]
+    [InlineData("corrupt")]
+    [InlineData("unsupported")]
+    public async Task LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping(string failureKind)
+    {
+        var (host, _, run) = await StartAdmissionWiredParkedRun();
+        var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
+        var originalLeaseBytes = File.ReadAllBytes(leasePath);
+        byte[] unavailableLeaseBytes;
+        if (failureKind == "unsupported")
+        {
+            var leaseDocument = JsonNode.Parse(originalLeaseBytes)!.AsObject();
+            leaseDocument["version"] = ArbitrationLeaseStore.SupportedVersion + 1;
+            unavailableLeaseBytes = JsonSerializer.SerializeToUtf8Bytes(leaseDocument);
+        }
+        else
+        {
+            unavailableLeaseBytes = System.Text.Encoding.UTF8.GetBytes("{ malformed admission bytes");
+        }
+
+        try
+        {
+            File.WriteAllBytes(leasePath, unavailableLeaseBytes);
+            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(60);
+
+            var blocked = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Unavailable, blocked.Status);
+            Assert.Contains("再次执行 Stop 重试", blocked.Message);
+            Assert.Equal(unavailableLeaseBytes, File.ReadAllBytes(leasePath));
+            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
+
+            File.WriteAllBytes(leasePath, originalLeaseBytes);
+            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromSeconds(2);
+            var retried = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, retried.Status);
+            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
+        }
+        finally { await host.ShutdownAsync(); }
+    }
+
+    [Fact]
+    public async Task PersistentHoldStopWithAdmissionWiredConfirmsNoMappingAndReachesCancelled()
+    {
+        var workflowId = SeedWorkflow();
+        var host = MakeWaitParkingHost(admissionWired: true);
+        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Hold, createQueueItem: false);
+
+        try
+        {
+            var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, result.Status);
+            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
+            Assert.Empty(ReadAdmissionOperationsForRun(run.RunId));
+            Assert.Empty(host.LocalWaitQueue.Load());
+        }
+        finally { await host.ShutdownAsync(); }
+    }
+
+    [Fact]
+    public async Task LocalWaitParkingStop_TerminalizesOnlyTheRegistrationBoundToItsRun()
+    {
+        var firstWorkflowId = SeedWorkflow();
+        var host = MakeWaitParkingHost(admissionWired: true);
+        try
+        {
+            var firstStart = await host.StartWorkflowAsync(firstWorkflowId);
+            Assert.Equal(HostActionStatus.Registered, firstStart.Status);
+            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(firstWorkflowId), TimeSpan.FromSeconds(10)));
+            var firstRun = Assert.Single(host.Runs.List().Where(run => run.WorkflowId == firstWorkflowId));
+            var secondWorkflowId = SeedWorkflow();
+            var secondStart = await host.StartWorkflowAsync(secondWorkflowId);
+            Assert.Equal(HostActionStatus.Registered, secondStart.Status);
+            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(secondWorkflowId), TimeSpan.FromSeconds(10)));
+            var secondRun = Assert.Single(host.Runs.List().Where(run => run.WorkflowId == secondWorkflowId));
+            var secondOperationBefore = Assert.Single(ReadAdmissionOperationsForRun(secondRun.RunId));
+            Assert.Equal(OperationRequestState.Accepted, secondOperationBefore.RequestState);
+            var secondOperationBytes = JsonSerializer.SerializeToUtf8Bytes(secondOperationBefore);
+            var terminalWriteAttempts = new System.Collections.Concurrent.ConcurrentQueue<string>();
+            host.AdmissionTerminalResultForTest = identity =>
+            {
+                terminalWriteAttempts.Enqueue(identity);
+                return null;
+            };
+
+            var stopped = host.RequestRunAction(firstRun.RunId, WorkflowRunAction.Stop);
+
+            Assert.Equal(HostActionStatus.Effective, stopped.Status);
+            Assert.All(ReadAdmissionOperationsForRun(firstRun.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
+            Assert.Equal(ReadAdmissionOperationsForRun(firstRun.RunId).Select(op => op.RequestIdentity), terminalWriteAttempts);
+            var secondOperationAfter = Assert.Single(ReadAdmissionOperationsForRun(secondRun.RunId));
+            Assert.Equal(secondOperationBytes, JsonSerializer.SerializeToUtf8Bytes(secondOperationAfter));
+        }
+        finally { await host.ShutdownAsync(); }
+    }
 }
diff --git a/_batch21/b21_plan.md b/_batch21/b21_plan.md
index a0aafd0e6..a763ab774 100644
--- a/_batch21/b21_plan.md
+++ b/_batch21/b21_plan.md
@@ -26,7 +26,7 @@ SB21-3 仅处理 BO-4。owner 于 2026-09-27 核实历史源码与测试后，
 - **SB21-2 代际边界（实现、R5 复核与恢复态回归完成）**：只含 BO-10（Remove 后重登的 HWM 单调边界）和 BO-12（Remove/裁剪后不降 HWM、legacy int 隔离、long 回绕/ABA、C5 Store 消费前复核）。GPT 会诊独立台账累计 5/8，R5 未发现新 MUST/IMPORTANT，并认可 R4-1/2 重要级证据闭环；另 2 次本地预检未发送、不计次。助手项目和测试项目分别非增量构建 0 错误/58 警告、0 错误/79 警告；恢复态 generation 定向 45/45、LocalWait 190/190、助手全量 1506/2/0/1508。Remove/prune 重登使用同一重开 Store 读回持久状态，C5 旧/新请求消费计数为过期 1、有效 1；组件无 sender 依赖、无生产调用点，本批发送数为 0，生产门继续关闭。主矩阵 10 个保护点、legacy 强化、R5 C5 代际比较及 long-generation `long.TryParse` 边界均由命名断言反向突变检出并精确恢复。最新 testId 差集：共享 1497、移除 1、新增 11、变化 17、不变 1480，见 `sb21-2-review/assistant-full-test-diff-r5-counter.md/.json`。R5 后复验及声明面最终守卫证据见 R5.3 §24.122 与 `_batch21/sb21-2-review/`。生产入口、真实 User、R5.8 签署、E3/E4/E5 与热键面继续关闭；未做实机验证。
 - **SB21-3 facade 映射（BO-4；owner 按历史修复＋本批验证裁决收口）**：仅本批 BO-4。历史错误映射由提交 47571736f27bfbc1a20e4d7bb67ce6db967cc0b5 改为 WaitWith；历史 4/4 wiring 测试不直接证明 mapper。本批没有生产源码修复；v15 文档定稿后的直接映射与消费验证 14/14、助手全量 1509/2/0/1511、testId shared1508/removed0/added3/changed0。owner 明确接受历史修复＋当前验证，IMPORTANT #1 保持原等级并关闭验收项，不将历史修复记为 SB21-3 生产修复。IMPORTANT #2 经 GPT round4 接受为组件范围证据闭合。round4 SHOULD 已修正 E1/E2 与 successor 节点门表述；会诊累计 4/8，3 次本地预检未发送，不计次。生产入口/真实 User/R5.8/E3/E4/E5/热键与 BO-4 successor 门继续关闭，未做实机验证。BO-10/12 不重开；SB21-4 的 BO-6/8/11/13 按原范围保留。
 - **SB21-4 停驻出口**：BO-13（停驻运行会话内终局处置出口）＋BO-11（Wave3 冻结残项打包：BO-6/7/8/9 处置或显式登记）。
-- **SB21-4 BO-13 / BO-11（2026-09-28 收口登记）**：本批仅实现停驻运行 Host Stop 终局出口和完整 binding 队列墓碑清理；17 个具名 Fact 覆盖 Wait/Hold、前置责任、游标/提交/规范准入身份、runId 文件绑定、队列/RunStore 异常与重试、恢复再停驻/reservation、admission terminal 回写、重复 Stop 与 ActiveStates。开工 HEAD 六项反例三项按预期失败；R1 修复前 16 项七项失败；修复后具名 17/17。两项目非增量构建成功，59/80 warning、0 error；广定向 233/233，助手全量 1526/2/0/1528；testId shared1511/added17/removed0/changed0。23 个独立反向突变各在目标断言失败并精确恢复；材料见 `_workflow/sb21-4/settled-final-r4/` 与 `mutations-review-r1-retry2/`。初始定向与全量发生静态临时写探针并发误注入，堆栈在 fixture seed；两个探针测试类现加入既有禁并行 collection，隔离后适用回归通过。BO-6 R19 MUST/R21 F4 IMPORTANT、BO-7 R21 F1/F2 MUST、BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT 按原级交 owner，方案/helper/组件证据未算端到端闭合。GPT R1 提出五项 BO-13 IMPORTANT；R2 已发但会诊执行器返回 Codex exit code 1、无审查报告，累计已发送 2/8。五项均保持原级未闭合并交 owner；失败与检查点详见 §24.124.5 和 handoff。SB21-3 BO-4 按历史修复＋owner接受的验证路径已结，不重开；SB21-2 BO-10/12 不重开。生产门继续关闭，未做实机验证。并行 R5 Prepared 任务终态已更新为 completed，提交 `912efbeb9b74f8ecad01b1456844227f8ff70228`，仍仅登记到 D18/D26、主线未消费。
+- **SB21-4 BO-13 / BO-11（R8 复审与收口）**：BO-13 的 R1 #1–#5 及 R3/R4 登记的重要扩展经 GPT `gpt-6-astra` / medium 最终复审，均建议按明示有限合同在原 IMPORTANT 等级内关闭，没有新增 MUST/IMPORTANT；R8 使用独立台账第 8/8 次且未重置。R8 指出的 M3/M4 mutation 映射和旧时点交接文字已修正。复审前的最终验证：两项目构建 exit 0（58/79 warnings，DeployToBgiTools=false），BO-13 49/49、LocalWait 240/240、助手全量 1558/2/0/1560；最终文档修订后的修复回归、ClaimSurface 和 testId 证据见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/`。BO-11 按原级逐项登记并冻结：BO-6 R19 MUST/R21 F4 IMPORTANT、BO-7 R21 F1/F2 MUST、BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT；BO-6/7 未启动，组件/方案不冒充端到端闭合。SB21-3 BO-4 按已接受路径不重开；SB21-2 BO-10/12 不重开。生产门关闭，R5.6、R6.1、R6 diff guard 不提前集成。
 - 横向：EV1-R1（RunStore.List 静默跳过修复）随 SB21-1 同批（占用者级别事实源的同族面）。
 
 ## 开工纪律
diff --git a/_batch21/sb21-4-handoff-2026-09-28.md b/_batch21/sb21-4-handoff-2026-09-28.md
index c56b49dd7..f54b9c50b 100644
--- a/_batch21/sb21-4-handoff-2026-09-28.md
+++ b/_batch21/sb21-4-handoff-2026-09-28.md
@@ -1,6 +1,6 @@
 # SB21-4 交接与验收记录
 
-> 状态：BO-13 实现及本地证据完成；BO-11 已按原级逐项登记。GPT R1 五项 IMPORTANT 均未闭合；R2 已发送但执行器 exit code 1、未返回评审，现明确交 owner 检查点。所有生产门继续关闭。
+> 状态：SB21-4 BO-13 的五项 R1 IMPORTANT 与已登记 R3/R4 IMPORTANT 扩展已按 R8 建议和本 Goal 验收指令，在明示有限合同内保持原级关闭。R8 GPT gpt-6-astra/medium 为独立台账第 8/8 次；无新增 MUST/IMPORTANT。R8 提出的两项 SHOULD 文档问题已修正并完成修复后回归。BO-11 仅完成冻结残项打包；BO-6/7 未启动，BO-8/9 按原级交 owner。BO-4、BO-10/12 不重开，所有生产门继续关闭。
 
 ## 本批目标与边界
 
@@ -14,27 +14,27 @@
 可逐状态核对的转移矩阵、字段保留和拒绝条件见 [`_workflow/sb21-4/scope-and-state-table.md`](../_workflow/sb21-4/scope-and-state-table.md)。当前实现的关键规则：
 
 1. Stop 入口与 `_gate` 内复读都比较请求 runId 和记录体 runId；读坏记录或身份不符时返回 `Unavailable`，不改 run/queue 字节。
-2. 仅 `LocalWaitParking`、明确 NoSend、当前有效 Wait/Hold 决策、无 drive/reservation、无未决外部事实和前置动作责任才可终态化。前置动作的 Intent/Submitted/Unknown、SendAttempted 或 jobId 由 Stop 专用护栏拦截；不改变恢复扫描原谓词。
-3. Wait 绑定当前 run/workflow/revision、cursor 及 decision cursor snapshot，当前 submission 必须是同派生 key 的 `LocalWaitDeferred` 且无 send attempt/job/accepted identity/terminal。Context、binding、排序/来源事实与共享身份翻译必须一致；候选/准入身份由共享 factory 复算。允许 context/binding revision 小于后续 run revision，不接受未来 revision。Hold 必须无 binding。
+2. 仅 `LocalWaitParking`、明确 NoSend、当前有效 Wait/Hold 决策、无 drive/reservation、无未决外部事实和前置动作责任才可终态化。前置动作状态枚举为 `Intent/Submitted/Succeeded/Failed/Cancelled/Unknown`；仅 `Succeeded/Failed/Cancelled` 三个已知终态可放行，`Intent/Submitted/Unknown` 及未识别数值状态均由 Stop 专用护栏拦截。已终态记录上残留的 `SendAttempted` 或 `jobId` 本身不阻止 Stop；不改变恢复扫描原谓词。
+3. Wait 绑定当前 run/workflow/revision、cursor 及 decision cursor snapshot，当前 submission 必须是同派生 key 的 `LocalWaitDeferred` 且无 send attempt/job/accepted identity/terminal。Context、binding、排序/来源事实与共享身份翻译必须一致；候选/准入身份由共享 factory 复算。修订关系为 `0 < binding.RecordRevision <= context.RecordRevision <= run.RecordRevision`。StartupHandoff 的来源 ID 必须等于 runId，并由本 run 的受理 scope/handoff 记录佐证；PanelFlowRegistration 的 RequestIdentity 来自唯一父登记，Stop 只核对 Context/binding 配对快照、不将其当发送许可。Hold 必须无 binding。
 4. Wait 仅按完整 `LocalWaitBinding` 在队列锁内比较并写精确墓碑；generation/HWM 和无关项不变。缺项/已有墓碑可收敛；载荷漂移、损坏、读写错误均保留 run 并拒绝误清理。先 queue tombstone 后 run `Cancelled`；run 写失败后重试识别已有墓碑、不重复写队列。
 5. 成功后保留 run、workflow revision、cursor occurrence/loop/attempt、submission key、outcomes、decision 和原 binding；不触发发送/收尾；在 gate 外将关联 admission registration 回写到 terminal。Cancelled 历史记录释放 ActiveStates 同流程槽位；恢复后再次停驻可再 Stop，之后新 Start 生成新 run。
 
 源码涉及 `TaskCenterHost.cs`、`LocalWaitQueueStore.cs`；新增具名事实在 `LocalWaitFinalizationContractTests.cs`。
 
 ## 验证证据
-- 开工分支 `main-OldTeaBag-B168`、HEAD `5e7e7e22f11daad0c86795368e9a21bb14d79b19`；SB21-3 的历史修复/owner 接受路径只作前情，不重开 BO-4。
-- 开工 HEAD 下 BO-13 6 项新增反例三项按旧实现预期失败；R1 修复前另有 16 项中 7 项失败。修复后 Host 事实 17/17；队列/run/cursor/submission/admission 身份、前置动作、读写异常及恢复路径均覆盖。23 个有效反向突变的 build/mutant/restored 日志/TRX、精确 SHA 和具名失败证据见 `_workflow/sb21-4/mutations-review-r1-retry2/`，旧重叠突变不计数。
-- 助手项目/测试项目最终非增量构建分别 0 error（59/80 warning）；具名 17/17、定向广回归 233/233、扩展 277/277、助手全量 1526/2/0/1528。开工 baseline 1511 项；testId shared 1511 / added 17 / removed 0 / changed 0，17 新增均为本批具名事实。当前 build、TRX、声明面与差集见 `_workflow/sb21-4/settled-final-r4/`；R2 未返回评审，没有新增源码调整；终稿文档后的最终 build/定向/助手回归与差集在 settled-final-r4。
-- 定向和全量首次回归各曾复现临时写探针竞争，失败栈在 `SeedParkedRun` 的 fixture queue Upsert，非 BO-13 目标断言。源因是 `LocalWaitGenerationContractTests` 与本批测试并行读写相同进程级 `BeforeTemporaryFileWriteProbe`。两类测试现并入已有 `[Collection("LocalWaitSnapshotProbe")]` 禁并行 collection；隔离后的定向具名事实 17/17、助手全量 1526/2/0/1528。所有失败 TRX/日志保留，不计为通过。
-- ClaimSurface regen/no-env 均 1/1；生成前、regen 后、no-env 前后及当前清单 SHA 均为 `A714AE920BD2928E19D2E4E3319AC413C24C45555718A9C1586E30F6964436AB`。日志/TRX 与 SHA 回执见 `_workflow/sb21-4/settled-final-r4/claims/`。生产相关构建/测试命令均显式 `-p:DeployToBgiTools=false`。
+- SB21-4 原始实现提交为 `afe84f22d0ebc49d7481bb0adf58f2ea6e29ec5b`，分支 `main-OldTeaBag-B168`；R6/R7 的后续生产/测试/文档修订是该提交之后的未提交差异。SB21-3 历史修复/owner 接受路径只作前情，不重开 BO-4。
+- 开工 HEAD 下 BO-13 6 项新增反例三项按旧实现预期失败；R1 修复前另有 16 项中 7 项失败。R1 历史具名 Host 事实 17/17、23 个有效突变位于旧证据路径；R6 增加 logger 异常 sibling、三种非法 revision 输入及重启恢复后 repark Fact，BO-13 定向 47/47、LocalWait 回归 238/238。R6 五项有效突变分别覆盖 TryLog、正 revision、future revision、binding/context 次序及双 reconciliation worker completion join；独立 baseline/build/mutant/restored 日志/TRX 和源码 SHA 见 `_workflow/sb21-4/review/r6-repair/mutations/`。R1/R5 历史哈希仅沿用当时记录，不以当前 SHA 回填；无效/重叠及未发送预检尝试继续保留并排除。
+- R6 文档同步后，两项目非增量构建分别 0 error（58/79 warning）；Host 47/47、LocalWait 238/238；助手全量 1556 passed / 2 skipped / 0 failed / 1558。R7 修订后再次非增量构建通过（58/79 warnings、0 errors），Host 49/49、LocalWait 240/240、助手全量 1558 passed / 2 skipped / 0 failed / 1560。R6→R7 testId shared1558/added2/removed0/changed0；开工→R7 shared1511/added49/removed0/changed0。两个新增 ID 是 StartupHandoff 本 run identity 正例与借用其他 run identity 的拒绝/字节保持反例。R7 最终日志/TRX 位于 `_workflow/sb21-4/review/r7-review-20260928-v1/postbuild-regression/`。DocsFixtureReferenceGuardTests 的旧失败及修正记录仍保留在 `_workflow/sb21-4/review/r6-review-validation/assistant-full.log`。
+- R5 的早期定向与全量首次回归曾复现临时写探针竞争，失败栈在 `SeedParkedRun` fixture queue Upsert，非 BO-13 目标断言；两个测试类已并入 `[Collection("LocalWaitSnapshotProbe")]`。相关失败 TRX/日志保留为 R5 历史，不计为 R6 通过证据。
+- R7 ClaimSurface SHA `466ba5573a9986618e568ebd514f79d55899e596c7c1e46e631e178b65a66982` 是历史证据。R8 完整文档修正后再次 regen/no-env，两次通过且 SHA 稳定；修复后证据、当前清单 SHA 与 TRX 位于 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-evidence.json`。构建和测试命令均显式使用 `-p:DeployToBgiTools=false`。
 - 当前源码和 Host 接缝测试不是 BGI 实机、真实 facade 调度、生产 User 或助手服务端运行验证。没有打开生产门。
 ## 会诊义务与状态
 
-- 独立台账：`C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch21-sb21-4.json`；累计上限 8 次，已发送 2/8（均为 `gpt-6-astra` / medium）；一次本地 preflight 因 sources 覆盖不全被拒、未发送，不计次。
-- GPT R1（`gpt-6-astra` / medium）已发送 1 次，提出五项 BO-13 **IMPORTANT**，原级均保留。R2 于 2026-09-27 20:00 UTC 已发送，但工具报 `GPT consultation failed for gpt-6-astra (Codex exit code 1). Start a new task and retry.`，没有返回 GPT 报告或质量结论；该已发送失败计入累计 2/8。完整失败记录见 `_workflow/sb21-4/review/gpt-r2-attempt1-failure.md`，台账快照与 review packet 分别在 `review/ledger-r2-snapshot.json`、`review-snapshot-r2-retry4/`。
+- 独立台账：`C:/Users/Administrator/.tools/zcode-relay/test/ledger-batch21-sb21-4.json`。现场机器原账保留 R1–R8 八次累计请求，R2 已发送失败仍计次；R8 已返回且是最后一次，不再新增会诊。Goal 摘要中的“2/8”与原件不符，按实际累计记录 8/8，不重置或覆盖历史轮次。
+- GPT R8 已逐项复核 BO-13 原始 R1 #1–#5 与登记的 R3/R4 IMPORTANT 扩展，建议均在明示有限合同内按原 IMPORTANT 等级关闭，未发现新增 MUST/IMPORTANT。本 Goal 要求完成原级验收，故本批接受有限范围裁决；其边界不是生产运行验收。R8 报告 `_workflow/sb21-4/review/r8-review-20260928-v1/gpt-r8-review.md` 明确说明：最终读错绑只证明不进一步修改错绑注入后的 A/B/queue；未单独验证 queue 文件读取 I/O 异常；重启证据为重建 Host 后显式 Stop；并发结论限同 Host/service 确定性交错。两项 SHOULD 文档问题已按 R7 mutation ledger 与最终 R8 状态修正。
 
-- **Owner 检查点（BO-13 R1 五项 IMPORTANT，全部未闭合）**：①未决前置责任 Intent/Submitted/Unknown/SendAttempted/jobId 的完整枚举与零发送终态界限；本批已加 Stop 专用谓词、Unknown/SendAttempted 事实和目标突变，owner 验收外部责任源、恢复扫描兼容及未决责任均被拒绝。②run/cursor/context/submission/successor/admission 身份与 revision 生命周期；本批 17 项事实和身份突变，owner 验收全字段/恢复/repark 矩阵和共享 factory 规范身份。③外部 runId 与嵌入 runId 不一致；本批入口/gate 双检和 A/B/queue 精确字节测试，owner 验收坏读、并发替换与最终 gate 拒绝。④Stop 后 admission registration 回写；本批 wired registration 事实及删除调用突变，owner 验收持久顺序、异常/无映射可观测性和幂等。⑤queue/run/read/reservation 故障及 tombstone 先落盘窗口；本批对应具名失败/重试、保留字节和独立突变，owner 验收各返回值、身份责任和所有失败窗均可安全重试。证据、原文后果与详细条件见 R1 外部台账及 R5.3 §24.124.5。GPT R2 未提供评审，施工方证据不代替 owner/GPT 语义确认。
-- 若后续 GPT 新增 MUST/IMPORTANT，保留原级并按上限继续修复/复核；达到 8 次仍有未闭合项则停在 owner checkpoint，不能以预算或工具报告冒充通过。
+- **R8 复审前待决清单（历史时点）**：当时 R1 IMPORTANT #1–#5 与 R3/R4 延伸均保持原级开放，#3/#5 等待完整 Host 与测试材料。R8 已在补齐材料下逐项给出有限原级关闭建议；最终验收和边界见本节状态及 R5.3 §24.124。
+- R8 于 2026-09-28 返回并计为第 8/8 次。报告对五项 R1 IMPORTANT 和登记的 R3/R4 IMPORTANT 扩展均建议原级有限合同关闭，未留 MUST/IMPORTANT；两项 SHOULD 文档问题已修正。台账不重置、不追加会诊；BO-6/7 不启动，生产门继续关闭。
 
 ## BO-11 owner 检查点（Wave3 未清零）
 
@@ -62,16 +62,17 @@
 R5.6、R6.1 和 R6 diff guard 均未提前集成。R5 Prepared 虽已独立任务完成，仍要等 D18/D26 指定主线消费批次和安全写入窗口。R5 slot 留待 D14 消费批次复核最终任务状态与完整材料；补充评估提出的 Q-R61/Q-R62 要求留给各自消费批次处理。
 ## 工作区与收口证据
 
-- 本批在 `main-OldTeaBag-B168` 开工，HEAD 为 `5e7e7e22f11daad0c86795368e9a21bb14d79b19`。源码、计划、R5.3、交接和声明面按明确清单提交；并行索引、relay、独立台账、日志和历史材料均在提交范围外。
-- 最终 R4 build、ClaimSurface regen/no-env、具名/定向/助手全量回归、testId 差集与 closeout 回执统一保存在 `_workflow/sb21-4/settled-final-r4/` 和 `_workflow/sb21-4/closeout/`；最终数量、SHA、审计状态以这些回执及提交后 Git 核验为准。工作流审计只作机械核验，不代替 owner/GPT 语义裁决。
-- 外部 `ledger-batch21-sb21-4.json` 保留累计两次已发送请求，并将五项 BO-13 IMPORTANT 与 BO-6/7/8/9 原等级完整登记为 owner checkpoint；`active-ledger.json` 指向本批 owner_checkpoint，未启动后续任务。`next-batch-prompt.md` 仅交付未来 BO-6/7 单一完整 Goal。
+- R8 复审与两项 SHOULD 文档修复均已完成；最终验证为两项目非增量构建通过、ClaimSurface regen/no-env 通过、BO-13 49/49、精确 LocalWait 240/240、助手全量 1558/2/0/1560。日志、TRX、清单 SHA 与 testId 差集见 `_workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/`；workflow closeout/verify 快照路径与状态记于 SB21-4 external active-ledger。提交后 SHA/HEAD 记录于上述外部 active-ledger。
+- 本批在 `main-OldTeaBag-B168` 开工，复核 HEAD 为 `afe84f22d0ebc49d7481bb0adf58f2ea6e29ec5b`。该 SHA 是本批复审前基线，不是最终提交；本批文件在收口审计后按明确路径提交，准确提交 SHA/最终 HEAD 记录于 `C:/Users/Administrator/.tools/zcode-relay/task-relays/active-ledger.json`。并行索引、relay、独立台账、日志和历史材料均在 Git 提交范围外。
+- R6/R7 记录保留为历史；R8 修复后回归、差集及 ClaimSurface 证据见 `postreview-closeout/`。R8 GPT 报告属限定范围技术裁决，不证明生产入口、真实 User、BGI 进程或实机行为；BO-11 其余原级义务继续冻结。工作流审计只做机械核验，结论由报告与代码/测试证据支持。
+- 独立台账保留 R1–R8 八次累计记录，R2 已发送失败仍计次，R8 为最后一次；没有重置或新增请求。R1 五项与 R3/R4 扩展按原 IMPORTANT 有限合同关闭；BO-11 BO-6/7/8/9 原级仍为 owner checkpoint。active-ledger 指向本批最终收口状态；next-batch-prompt.md 保存一份未来 BO-6/7 完整 Goal，仅供 owner 后续启动，不表示已经创建/启动任务。
 - 最终提交使用 `git commit --only -m ... -- <逐项显式路径>`。材料外已知保留项包括三份无关 tracked 文档 `Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md`、`槲寄生调度器总计划.md`，untracked parallel registry/reports、各类 `.bak`/`.stale`、历史批次、日志/TestResults、DLL/tool 输出和截图；均不进入提交。
 
 ## 下一批完整 Goal（只作为交接，不在本任务启动）
 
 ```text
 /goal
-结果：在 owner 另行启动的独立 Wave3 子批中，端到端解决 BO-6 的停驻与已完成锚冲突，并只覆盖其直接依赖的 BO-7 R21 F1/F2；选择并实现原台账允许的完成过滤推进或显式失败态，交付真实 Runner/loop 驱动验收证据。
-约束：开工先核对 SB21-4 handoff、R5.3 §24.124、R5.3 §24.120.4 与 BO-6/7 原始 ledger，重审分支、HEAD、工作区和并行成果；使用 bgi-project-development Skill、会诊处置纪律及 workflow/deliveries 设施。只施工 BO-6/BO-7，不重开 BO-13、BO-4、BO-10/12，不顺手纳入 BO-8/9；未闭合原等级 MUST/IMPORTANT 不降级；维持生产入口、真实 User、R5.8、E3/E4/E5、热键与 BGI 生产进程关闭；不提前合并未到消费批次的 R5.6/R6.1/R6 成果。反例先行，关键断言逐项反向突变并精确恢复；构建/测试加 -p:DeployToBgiTools=false；以真实 Runner/loop 证明冲突不会聚合成 Succeeded 或触发收尾、已完成出现不重提、有效停驻义务不丢；记录助手回归和 testId 差集；仅 GPT 会诊且累计最多 8 次，重要/必改闭环或留下 owner checkpoint。读取 `Docs/design/mistletoe-parallel-deliveries.md` 及同名 JSON，自动运行 `python -B tools/mistletoe/deliveries.py --root .` 并在开工、自然边界和收口核对任务状态、报告、acceptance 与提交；只在目标消费批次接收，不提前合并 R5.6、R6.1 或 R6 diff guard 成果。所有源码和文档仅按本批明确路径提交。
-完成判据：当前实现反例先红；BO-6/7 端到端驱动矩阵完成且具名断言通过；新增关键断言的 mutant 在目标断言失败、恢复 SHA 精确一致且恢复测试通过；助手适用回归与差集可解释；R5.3、计划、交接、独立台账、task-relays 和 workflow closeout/verify 同步；逐项提交清单核验且生产门仍关闭。
+结果：在独立 Wave3 子批中，以真实 Runner/loop 端到端关闭 BO-6 停驻与已完成锚冲突语义，并只纳入其直接依赖的 BO-7 R21 F1/F2 救援全序、多有效停驻问题；按原台账选择并实现完成过滤推进或显式失败态。
+约束：这是未来独立子批，只有 owner 启动后才施工。开工先核对本交接 `_batch21/sb21-4-handoff-2026-09-28.md`、R5.3 §24.120.4/§24.124、`_workflow/sb21-4/raw-bo-obligations.json` 与 BO-6/7 原始台账；SB21-4 BO-13 的五项 R1 IMPORTANT 和已登记 R3/R4 IMPORTANT 扩展已按 R8 有限合同原级验收闭环，不重开 BO-13、SB21-3 BO-4 或 SB21-2 BO-10/12。只施工 BO-6 与 BO-7，不启动/并入 BO-8/9，不把 helper/排序组件证据当作 Runner 端到端验收。先读取 AGENTS.md、`$bgi-project-development`、`.agents/knowledge/domains/review-disposition-discipline.md`、`Docs/design/mistletoe-workflow-facilities.md`、`tools/mistletoe/README.md`、`Docs/design/mistletoe-parallel-deliveries.md` 及同名 JSON；开工、自然边界、收口运行 `python -B tools/mistletoe/deliveries.py --root .`，逐项核对并行任务终态、报告、验收与真实提交，仅在既定消费批次接收，不提前集成 R5.6、R6.1 或 R6 diff guard。建立/复核 `_workflow/<本批>/manifest.json` 并运行 workflow evidence、review+verify、closeout+verify；工具只作机械核验。反例先行：先证明当前真实 Runner/loop 对停驻与完成锚冲突的失败或错误成功路径，再最小实现。新增关键断言逐项反向突变；保存 baseline/build/mutant/restored 独立日志/TRX、具名目标失败和源码原始/恢复 SHA-256，确认不是编译失败或更早断言。构建和测试都加 `-p:DeployToBgiTools=false`；按影响跑 BO-6/7 定向、助手适用与全量回归，以同条件基线说明 testId 增删变化。声明面变化时执行 regen/no-env 并确认 SHA 稳定。会诊仅 GPT、每次前读取处置纪律并验证 review 快照、该子批累计最多 8 次（已发失败/超时也计），不得降级 IMPORTANT/MUST；预算用尽仍未闭合则交 owner checkpoint 并保持生产门关闭。保持产品入口、真实 User、R5.8、E3/E4/E5、热键面和 BGI 生产进程关闭，不做实机操作或生产验证。同步 R5.3、计划、handoff、独立会诊台账和 task-relays；只用 `git commit --only -m "<说明>" -- <本批明确文件路径>` 提交，保留所有材料外改动。
+完成判据：Runner/loop 当前实现反例先红；实现覆盖冲突、救援后推进、完成节点不重提、停驻义务不丢、多有效停驻全序及同身份删除/插回，不产生虚假成功或流程收尾；所有新增关键断言的目标突变失败且精确恢复后通过；BO-6 R19 MUST、关联 R21 F4 IMPORTANT 与 BO-7 R21 F1/F2 MUST 按原级闭环，否则逐项记录给 owner；助手回归/testId 差集可解释；R5.3、计划、独立台账、handoff、task-relays 与 workflow closeout/verify 完成；提交清单明确、提交后 Git 状态核实且所有生产门仍关闭。
 ```

## scoped staged diff

## materials outside this batch
本批相关跟踪路径：R5.3 §24.120/§24.124、TaskCenterHost.cs、TaskCenterHost.Admission.cs、RunStore.cs、LocalWaitFinalizationContractTests.cs、ClaimSurfaceManifest.txt、_batch21/b21_plan.md、SB21-4 handoff；LocalWaitQueueStore.cs 与其余合同源码为当前固定证据输入。材料外 tracked changes 为 Docs/design/mistletoe-session-relay-2026-09-24.md、Docs/design/unified-job-registry-master-plan.md、槲寄生调度器总计划.md，精确差异由既有 `_workflow/sb21-4/review/r7-review-20260928-v1/material-out-tracked.diff` 提供，本 Goal 未编辑或暂存它们。材料外 untracked parallel-deliveries.{md,json} 及其 R7 登记差异保留于 `_workflow/sb21-4/deliveries/parallel-index-r7-diff.patch`；本轮读取但未写它们。保留所有 .bak/.stale、logs、TestResults、DLL/tool outputs、历史 _batch*、screenshots、用户配置与所有其他材料外文件；不纳入提交。无 R5.6/R6.1/R6 diff guard 集成。 Final closeout status classifies and preserves the three unrelated tracked changes (Docs/design/mistletoe-session-relay-2026-09-24.md, Docs/design/unified-job-registry-master-plan.md, 槲寄生调度器总计划.md), all unrelated ignored/untracked backups, stale files, logs, User/runtime/configuration data, historical batches and generated outputs. They are not in this batch commit; current full porcelain is captured by closeout audit. The closeout packet omits the older standalone material-out tracked-diff snapshot (kept unchanged on disk); the actual R8 review snapshot already included the then-current material-out tracked diff, while this closeout captures the latest full porcelain and outside-change classification.
