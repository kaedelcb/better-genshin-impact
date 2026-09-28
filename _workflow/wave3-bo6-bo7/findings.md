# Wave3 BO-6/BO-7 子批发现与证据（当前复核状态）

## 范围与工作区

- 子批固定开工 HEAD `e2613a851bd45c28fdd56b84dfc10784e1d9c9b8`，当前仍在 managed worktree；原工作区 2 项 tracked 修改与 457 项 untracked 未复制或触碰。BO-6 与直接依赖 BO-7 R21 F1/F2 外不扩范围；未重开 BO-13、SB21-3 BO-4、SB21-2 BO-10/12，未纳入 BO-8/9。
- 开工与自然边界 deliveries 检查通过；未提前集成 R5.6、R6.1、R6 diff guard。只读 Agent 基于固定 opening SHA 只读，无写入，未替代 Runner 验收。
- BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键与生产进程关闭；不做实机或生产验证。

## 修复前真实 Runner/loop 反例

- `red-runner/current-cursor/current-cursor-before-fix.trx` 保留仍有效 R cursor；原实现实际只提交 `R, stop`，遗漏更早 P1/Q，具名断言失败。`red-runner/mixed-active-parks/mixed-active-parks-before-fix.trx` 显示停驻/完成锚冲突触发错误收尾。反例由真实 Runner/loop 执行，不是 helper 或排序组件替代。

## 当前实现与端到端证据

- 选用台账允许的“恢复推进时完成过滤”：有持久 `waitLocally` 历史的显式 Resume 在覆盖当前修订前重算所有仍有效停驻义务；即使保存 cursor 仍可定位也不跳过救援。candidate/rescue 和多个停驻以 `(LoopIteration, SequenceIndex)` 全序选择最早点。恢复后只对已完成的稳定 node/occurrence/loop 身份过滤，未完成 occurrence 继续执行；不含停驻历史的普通修订路径不扩到 BO-8。持久 `TailReached` 与可定位未完成停驻并存时聚合为 `Failed`，不创建 PendingCompletion、不调用终态。
- 四条 Runner facts 4/4，覆盖 cursor 冲突、救援后推进、完成项不重提、多停驻全序、candidate/rescue 两个跨轮方向及相同身份删除/插回。独立 TailReached fixture 在 Resume 返回后重新读取 RunStore，断言盘上 Failed、TailReached、P1/Q 义务保留、PendingCompletion 为空，终态调用为空。

## 构建、回归与 testId

- 助手主项目 Rebuild exit 0（59 warnings）；测试项目 Rebuild exit 0；命令均含 `-p:DeployToBgiTools=false`。最新助手全量 `regression/final/assistant-full-final.trx`：1562 passed / 2 skipped / 0 failed / 1564，进程退出码 0。最终等范围定向 `regression/final/runner-localwait-final.trx`：93/93。
- 开工 HEAD 全量基线为 1558 passed / 2 skipped / 0 failed / 1560。testId 集合为 1556 unchanged、8 added、4 removed、0 changed：4 个新 Runner facts + 4 个更新名称 helper facts；4 个旧 helper ID 对应 4 个新 ID。`0 changed` 只表示 testId/name 级差集，两个仍保留原 ID 的 helper 断言语义也改变：`RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate` 从 null 改为 n2，`RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed` 从 null 改为 P1。完整基线/最终对比见 `regression/final/testid-comparison-final.json`。
- 旧 92/92 和中间 88/88 的差异已核实：中间 review3 filter 只选 `WorkflowRunnerTests` 与 `LocalWaitIdentityTranslationTests`，漏掉 5 个仍相关 `LocalWaitParkingStateContractTests`。最终 filter 恢复这些项；开工 92 个 testId 全部保留，并增加独立 TailReached Runner fact，当前 93/93。逐项证据见 `regression/final/runner-localwait-comparison.json`。
- R5.3 状态/预算对账更新改变了声明面；随后 ClaimSurface regen/no-env 均通过，清单 SHA 稳定为 `9EAA1A7F6EFAECCB763DA8EE10520E01B128F3129E6DBFB831295EA58CD083DC`。对应独立日志/TRX 与最终全量报告见 `regression/owner-checkpoint/claim-surface-post-correction/`。

## 反向突变与原始材料

- 7 个 v3 Runner source mutations 各自保存 baseline/mutant/restored build/test logs 与 TRX、source original、实验元数据及实际 patch；original/restored Runner SHA 均为 `5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`，v3 执行时测试源 SHA 为 `c1197ed5b6f79029104b7949247ac19d9e224fe72dde4c4a23eef0c24e680cd9`。七个 mutant 均 build 成功、在登记的目标 Runner 断言失败；baseline 与精确恢复通过。
- 第 8 个 v4 独立突变只移除未清偿停驻链尾聚合分支的 `_runs.Update(run)`。baseline/restored Passed，mutant build 成功且目标 fact 在落盘状态断言失败：内存对象为 Failed，重新 Load 是 Running。Runner original/restored SHA 保持相同；新测试源 SHA `b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711`。
- 精简索引 `_workflow/wave3-bo6-bo7/mutations/raw-mutation-evidence-index.md` 列出 8 项每个原始 experiment、实际 mutation diff、三个阶段 TRX/build/test 日志路径，并记录散列核验；每项原始文件均保留于对应 mutation 子目录。具体具名失败见各 experiment JSON。

## 矩阵措辞说明

开工冻结的 C1 `expected` 对“更早 candidate/rescue”的文字存在歧义。该字段与 `opening.json` 均保留不动；`review-r9/current-clarifications.md` 解释本合同按 `(LoopIteration, SequenceIndex)` 取字典序最小者，SequenceIndex 较小但 LoopIteration 较晚的 candidate 不会越过更早 loop 的 rescue。两条真实 Runner tests 分别证明 candidate@0 优先于 rescue@1，以及 rescue@0 优先于 candidate@1。

## 会诊等级与当前状态（owner 批准追加会诊后）

owner 批准额外最多 2 次独立会诊后，两次请求均通过独立本地 Codex CLI 只读通道发出并成功回收（gpt-6-astra / medium）：

- 请求 9（仅 BO-6 R19 MUST、R21 F4 IMPORTANT）：exit 0，182.8 秒 → **R19 closed（MUST）、F4 closed（IMPORTANT）**
- 请求 10（仅 BO-7 R21 F1/F2 MUST）：exit 0，183.0 秒 → **F1 closed（MUST）、F2 closed（MUST）**

四项原级义务均按原等级闭环，两份报告都未提出新的 MUST/IMPORTANT。追加额度 2/2 用尽、余额 0，原 8/8 不变。两位审查者独立指出同一处源码注释陈旧（`WorkflowRunner.cs:1486`、`:1535–1537`），均评为建议级且明确未据此发现运行正确性缺陷；本批登记为文档残项 `BO-6/7-D1` 并给出三选一处置，未改动源码（理由见 owner-checkpoint.md）。其一会诊还指出送审汇编包内嵌 experiment.json 快照的旧 `mutation.patch` 哈希，已以版本差异说明处置（`review-r9/raw-mutation-bundles/METADATA-CURRENCY-NOTE.md`）。

逐项处置与证据映射见 `consultation/owner-approved-requests-9-10.md`；通道与预检见 `consultation/owner-approved-cli-preflight.json`；两次运行元数据见 `review-cli-a-bo6/run-metadata.json` 与 `review-cli-b-bo7/run-metadata.json`。

会诊预算保守计为 8/8：工具调用数 6，服务端工具错误字符串报告的 Attempts 合计 8；内部 retry 的底层传输 dispatch 数没有独立遥测，故不得据此恢复余额。第 5、6 次工具调用均为 BO-6 R19/F4 同范围的 Codex exit code 1 / Attempts: 2 无报告；附件包低于文件数、单文件和总量限制，具体原因未知。当前 main-workspace 新分流规则还要求核对完整 token 估算和有效上下文阈值；发送前材料未保存该估算，不能声称达到本地 CLI 回退条件。没有发出 GPT 或本地 CLI 新会诊请求。

外部 task-relay 曾落后于实际请求，错误标记 6/8、request 7 未发送；现已按原始 request7 元数据、原始错误和工具返回更正为 8/8，余额 0。调用数、工具报告尝试数、旧快照和当前结果逐项见 consultation/current-ledger-reconciliation.json 与 consultation/task-relay-ledger-owner-checkpoint.json。生产门继续关闭。
