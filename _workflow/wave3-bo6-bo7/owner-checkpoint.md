# Wave3 BO-6/BO-7 owner 检查点

## 裁决状态

owner 已依据本检查点批准**额外最多 2 次**独立会诊（原 8/8 保留、不重置、不滚动追加，优先使用独立本地 GPT/Codex CLI 只读通道，固定 gpt-6-astra / medium）。两次请求均已实际发送并成功返回：

- 请求 9（仅 BO-6 R19 MUST、R21 F4 IMPORTANT）：exit 0，182.8 秒 → **R19 closed（MUST）、F4 closed（IMPORTANT）**
- 请求 10（仅 BO-7 R21 F1/F2 MUST）：exit 0，183.0 秒 → **F1 closed（MUST）、F2 closed（MUST）**

四位原级义务均已按**原等级**闭环，两份报告都未提出新的 MUST/IMPORTANT。追加额度 **2/2 用尽，余额 0**；原 8/8 计数不变。唯一残项是两位审查者独立指出、均评为**建议级**的一处源码注释陈旧，登记为 `BO-6/7-D1`（见下）。相关生产门保持关闭。

范围仅为 BO-6 与直接依赖它的 BO-7 R21 F1/F2。未重开 BO-13、SB21-3 BO-4、SB21-2 BO-10/12；未纳入 BO-8/9；未提前集成 R5.6、R6.1、R6 diff guard。无真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键或实机验证。

## 已独立核验的修复与证据

- 修复前真实 Runner/loop 反例证明：停驻义务与已完成锚冲突会走错误成功/终态路径；保留仍有效的 R 游标时只提交 `R, stop`，漏掉更早的 P1/Q。证据见 `red-runner/mixed-active-parks/` 与 `red-runner/current-cursor/`。
- 当前 Runner 对持有 `waitLocally` 历史的显式 Resume 重算所有仍有效停驻义务；candidate/rescue 与多停驻按 `(LoopIteration, SequenceIndex)` 全序择早；恢复推进按稳定出现身份跳过已完成 occurrence、继续未完成项；未清偿停驻到达 `TailReached` 时持久化 `Failed`，保留义务且不创建 `PendingCompletion`、不执行终态动作。
- 最新最终源码构建：助手单测项目 `dotnet build --no-incremental -p:DeployToBgiTools=false`，exit 0；日志 `regression/owner-checkpoint/claim-surface-post-correction/unit-build.log`。
- 声明面因 R5.3 状态更新而变更：`CLAIM_SURFACE_REGENERATE=1` 再生和清除此环境变量后的守卫均为 1/1 通过；清单 SHA-256 在再生后与 no-env 复跑后均为 `9EAA1A7F6EFAECCB763DA8EE10520E01B128F3129E6DBFB831295EA58CD083DC`。独立日志、TRX 与前后散列见 `regression/owner-checkpoint/claim-surface-post-correction/`。
- 同步 R5.3、handoff 与计划文本后又执行一次不带环境变量的最终守卫，1/1 通过，清单 SHA 未变；最终文档快照证据见 `regression/owner-checkpoint/final-document-snapshot/`。
- 最终等范围 Runner/LocalWait/Parking 定向回归 93/93；助手全量 1562 passed、2 skipped、0 failed / 1564。构建、独立日志与 TRX 位于同目录。
- 同条件开工 HEAD 全量基线为 1558 passed、2 skipped、0 failed / 1560。最终 testId 差集为 1556 unchanged、8 added、4 removed、0 changed；4 个新增是真实 Runner facts，另 4 个新增 helper ID 对应更名后的 4 个旧 helper ID。两个保留 testId 的 helper 断言语义也有变化，因此 `0 changed` 只代表 ID/name 对照。机器差集见 `regression/owner-checkpoint/claim-surface-post-correction/testid-comparison.json`；逐项映射见 `regression/final/testid-comparison-final.json`。
- 8 项反向突变的 baseline/build/mutant/restored 日志、TRX、目标失败与源码 SHA 已逐项复核，8/8 通过；包括 v4 移除 `_runs.Update(run)` 时目标测试证明落盘状态错误，源码精确恢复后通过。证据索引 `mutations/raw-mutation-evidence-index.md`，复核清单 `mutations/owner-checkpoint-recheck.json`。

- 行尾规范化：反向突变实验执行时受影响文件为 CRLF，之后按仓库提交形态统一为 LF；内容逐字节相同，实验期与规范化后的 SHA-256 映射见 manifest 的 `line_ending_normalization` 与 `mutations/raw-mutation-evidence-index.md`。manifest 中的源码绑定散列与当前文件一致，改造后已重新执行 93 项定向与助手全量回归。

上述证据只证明本地修复与夹具能区分目标回退，不替代独立审查报告；独立审查结论见下节。

## 原级义务裁决（owner 批准追加会诊后）

| 义务 | 原级 | 裁决 | 报告与关键证据 |
|---|---|---|---|
| BO-6 R19 | **MUST** | **closed** | 请求 9。`WorkflowRunner.cs:568–571`／`:1503–1516`／`:655–662`／`:1575–1579`／`:745–759`；真实 Runner facts `WorkflowRunnerTests.cs:1005–1016`、`:1131–1133`、`:1173–1175`；4 份 BO-6 反向突变逐项通过。 |
| BO-6 R21 F4 | **IMPORTANT** | **closed** | 请求 9。冲突不再收敛为普通链尾：`:745–759` 持久 `Failed`＋Note 明示未清偿停驻＋保留义务＋无 `PendingCompletion`＋不收尾；`:1071–1086` 同时断言返回对象与重新加载的 RunStore。 |
| BO-7 R21 F1 | **MUST** | **closed** | 请求 10。`:1460–1467` candidate/rescue 用 `(LoopIteration, SequenceIndex)` 字典序；`:1541–1546` 下界同用字典序；双向真实 Runner 证据 `:1090–1135`、`:1138–1175`。 |
| BO-7 R21 F2 | **MUST** | **closed** | 请求 10。`:1411–1426` 遍历全部停驻标记、无"最新安全即 break"；`:1503–1515` 取计划全序最早者；`:1575–1579` 完成身份不含可变 SequenceIndex；连续修订反例 `:941`／`:1008–1016`／`:1023–1029`。 |

两份报告的收尾结论均为「本范围仍有未闭合的 MUST/IMPORTANT：否」。裁决只覆盖助手侧源码与已保存的真实 Runner/驱动夹具证据（外部执行边界为 fake），不构成实机或生产验收。

## 会诊账与两次无报告调用

- 固定会诊配置为 `gpt-6-astra / medium`。台账记 **6 次 GPT 工具调用、8 次工具错误/返回报告的服务尝试**；按已发送失败规则保守计入 8/8，余额 0。保存的工具返回没有逐次底层 dispatch/接受遥测，因此实际底层送达数不可独立确认；该不确定性不恢复预算。明细见 `consultation/current-ledger-reconciliation.json`、`consultation/attempts.md` 和原始返回。
- 工具调用 #5（请求 5/6，BO-6 R19/F4，同一范围）返回 `Codex exit code 1`、`Attempts: 2`，无报告；精确 14 文件包为 1,850,827 bytes。原始材料：`consultation/gpt-r5-failure.json`、`consultation/gpt-r5-meta.json`。
- 工具调用 #6（请求 7/8，BO-6 R19/F4，同一范围）返回相同错误与 `Attempts: 2`，无报告；最终 24 文件包为 1,728,816 bytes，最大文件 228,706 bytes，低于已知 30 文件、524,288 bytes/文件、2,097,152 bytes 总量限制。原始材料：`consultation/gpt-request7-8-failure.json`、`consultation/gpt-request7-meta.json`。
- 两次均不是本地文件数或字节超限拒绝。可能原因只能表述为 GPT/Codex 审查执行器或 worker 异常退出；具体根因未知。没有证据证明是上下文过大、网络、认证、额度或模型不可用；request 7/8 的完整 token 估算与有效上下文测量未保存，不能证明达到新分流规则的三分之一回退阈值。
- 更早的第 1、2 次已发送调用分别在本地保存返回时遇到 `TextEncoder is not defined` 和 `btoa` 不可用，报告未回收，按已发送计入预算且不产生处置结论。
- task-relay 镜像现已更正为 `blocked_owner_checkpoint`、8/8、余额 0、6 次工具调用/8 次工具报告尝试；此前 6/8、request 7 未发送是过期快照。当前镜像路径、时间和 SHA 见对账 JSON。
- 本检查点形成时未再发 GPT 请求；owner 批准后，追加的请求 9、10 均通过独立本地 Codex CLI 只读通道发出（不占用原 8/8），两次都成功回收报告。
- 请求 9／10 的原始事件流、退出码、耗时、用量与报告哈希见 `review-cli-a-bo6/run-metadata.json` 与 `review-cli-b-bo7/run-metadata.json`；模型与强度由本地 rollout 记录证实为 `gpt-6-astra` / `medium`。逐项处置见 `consultation/owner-approved-requests-9-10.md`；通道与预检见 `consultation/owner-approved-cli-preflight.json`。

## 送审准备度与追加通道执行（已完成）

- owner 批准前：最终修订 `a0cfce8c7` 上执行 review 阶段 `workflow.py audit` 与 `verify`，均机械核验通过（30 份测试报告、8 项突变，packet 448,029 字节）；快照 `review-owner-ready-20260928-v1`；两范围精确清单见 `consultation/owner-ready-preflight.json`。
- owner 批准后：先做**不消耗会诊次数**的本地预检（模型可用性、登录、只读权限、源码版本、证据路径、完整 diff、上下文容量），全部通过；预检记录 `consultation/owner-approved-cli-preflight.json`。
- 通道：本地包装脚本 `ask_codex.ps1` 因 DeepSeek 配置会自动切到 deepseek-flash 且拒绝 `medium`，无法表达 owner 指定的 gpt-6-astra / medium，故改用直接调用 `codex exec -m gpt-6-astra -c model_reasoning_effort="medium" -s read-only`；未修改任何本地配置，运行期关闭本地 hooks 与桌面通知。
- 容量：整读全部长格式原件约 45.3 万 token 会超 gpt-6-astra 有效窗口（≈258,400），因此提供紧凑判定包并限制审查者总阅读量约 12 万 token，长格式原件按需分片读取。
- 两次实际用量：请求 9 输入 1,158,122 token（缓存 1,021,824）、输出 6,472；请求 10 见其 `run-metadata.json`。

## owner 待决残项

**BO-6/7-D1（建议级，注释陈旧）**：`WorkflowRunner.cs:1486` 仍写"取最后一条可定位停驻标记为基准"（与 `:1503–1515` 的全序最早选择矛盾）；`:1535–1537` 仍以"DriveAsync/Relocate 无完成跳过"作为下界理由（与 `:655–662` 的停驻恢复完成过滤不符）。请求 9 与请求 10 独立指出同一处，均评为建议级，并明确未据此发现运行正确性缺陷。

本批**未改动源码**，理由：①会诊裁决绑定当前字节（两份报告均记录当时源码 SHA 并据此核验突变绑定），追加额度已用尽、无法再取得同等级复审；②改动会作废 manifest 源哈希、8 项突变绑定与送审汇编包内嵌哈希，需重跑约 24 次构建/测试并重新生成审查者已核验的汇编包；③两位审查者均评为建议级。

owner 三选一：①另批小额修正（含该次自身的反向突变与回归）；②等下一次重新绑定 `WorkflowRunner.cs` 哈希的改动一并修正；③仅接受为文档说明、不修正。更正文本已在 `consultation/owner-approved-requests-9-10.md` 中给出。

另：请求 9 指出送审汇编包 `bo6-tail-persisted-failure-v4.txt` 内嵌 experiment.json 快照的旧 `mutation.patch` 哈希，已按"注明版本差异"处置（该目录 `METADATA-CURRENCY-NOTE.md`），权威记录已核验为当前值。

closeout audit/verify 只核验材料与快照，不改变上述会诊裁决，也不授权打开任何生产门。 核验修订记录：v8 于 HEAD `400a73d04`、v9 于 HEAD `0658b03af`、v10 于 HEAD `0373ef161` 均 audit+verify 通过；各快照提交位于 `_workflow/wave3-bo6-bo7/closeout-owner-20260928-v8|v9|v10/`。`0658b03af..0373ef161` 之间产品路径仅有 R5.3 的 2 行文档登记，源码与测试字节未变，因此全量回归结论仍绑定同一代码修订。 本批提交：代码与文档 `10675950a`，证据与收口材料 `600f9ef8c`；提交后收口快照见 `_workflow/wave3-bo6-bo7/closeout-owner-20260928-v8/`。
