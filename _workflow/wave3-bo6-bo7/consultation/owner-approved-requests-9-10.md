# Wave3 BO-6/BO-7 owner 批准追加会诊（请求 9–10）处置记录

## 授权与计数

- owner 于 2026-09-28 明确批准本子批**额外最多 2 次**独立会诊；原 8/8 保留、不重置、不滚动追加。
- 第 1 次只审 BO-6 R19 MUST 与 R21 F4 IMPORTANT；第 2 次只审 BO-7 R21 F1/F2 MUST。
- 两次均已实际发送并成功返回：**工具调用/请求 9 与 10，各计 1 次；追加额度 2/2 用尽，余额 0**；原 8/8 不变。
- 两次都是 exit code 0、模型 `gpt-6-astra`、强度 `medium`（由本地 rollout 记录的 model slug 与 reasoning_effort 证实），read-only 沙箱。
- 未出现失败或超时；没有发生模型替换、强度降低或改回原工具通道。

## 通道与预检

- 通道选择：独立本地 Codex CLI（`codex exec`），默认运行时 / aiotto provider。包装脚本 `ask_codex.ps1` 因存在 `~/.codex-deepseek/config.toml` 会自动选择 DeepSeek 运行时（deepseek-flash）并**拒绝 `medium`**，无法表达 owner 指定的 gpt-6-astra / medium，故改用直接调用；未修改任何本地配置。
- 预检（全部本地、未派发模型请求）：见 `consultation/owner-approved-cli-preflight.json`。要点：目录含 `gpt-6-astra`（窗口 272,000、有效 95% ≈ 258,400、默认 medium）；`codex doctor` 报 auth 已配置（chatgpt tokens，有效期约 8.7 天）、provider 端点 HTTP 可达；`-s read-only` 可用且工作区受信任；HEAD 固定 `0f46128fbba75ea346a9e05b94ec12f55f062b3e`，6 个 manifest 源文件哈希全部匹配；两范围证据路径齐备；完整批次 diff 已生成。
- 干扰控制：`-c features.hooks=false`（已用 `codex features list` 验证 hooks 生效状态为 false）与 `-c notify=[]`。
- 容量：整读全部长格式原件约 45.3 万 token，会超有效窗口；因此提供紧凑判定包并限制审查者总阅读量约 12 万 token，长格式原件按需分片读取。两次实际用量见各自 `run-metadata.json`。

## 逐项原级裁决

| 义务 | 原级 | 裁决 | 报告 | 关键证据 |
|---|---|---|---|---|
| BO-6 R19 | **MUST** | **closed** | 请求 9 | `WorkflowRunner.cs:568–571` 显式 Resume 重算；`:1503–1516` 取计划全序最早有效停驻；`:655–662` 停驻恢复推进过滤已完成身份（`:1575–1579` 用 NodeId+Occurrence+LoopIteration）；`:745–759` 未清偿停驻聚合为持久 Failed。Runner fact：`WorkflowRunnerTests.cs:1005–1016`（恢复提交恰为 P1,Q,R,stop，lead/A/B 各一次，无收尾、非 Succeeded）、`:1131–1133`、`:1173–1175`。4 份 BO-6 反向突变逐项核验通过。 |
| BO-6 R21 F4 | **IMPORTANT** | **closed** | 请求 9 | `WorkflowRunner.cs:745–759` 冲突不再收敛为普通链尾：State=Failed、Note 明示未清偿停驻、保留历史义务、不建 PendingCompletion、不执行收尾；测试 `WorkflowRunnerTests.cs:1071–1086` 同时断言返回对象与重新加载的 RunStore。 |
| BO-7 R21 F1 | **MUST** | **closed** | 请求 10 | `WorkflowRunner.cs:1460–1467` candidate/rescue 用 `(LoopIteration, SequenceIndex)` 字典序（rescue 不再无条件覆盖）；`:1541–1546` 探针下界同用字典序。双向真实 Runner 证据：`WorkflowRunnerTests.cs:1090–1135`（candidate 较早）与 `:1138–1175`（rescue 较早）。 |
| BO-7 R21 F2 | **MUST** | **closed** | 请求 10 | `WorkflowRunner.cs:1411–1426` 遍历全部停驻标记、无"最新安全即 break"；`:1503–1515` 取全部仍有效标记中计划全序最早者；`:1575–1579` 完成身份不含可变 SequenceIndex。连续修订反例 `WorkflowRunnerTests.cs:941`＋断言 `:1008–1016`、`:1023–1029`。4 份 BO-7 反向突变逐项核验通过。 |

两份报告的收尾行均为"本范围仍有未闭合的 MUST/IMPORTANT：否"。

## 报告新发现与处置

| 编号 | 来源 | 等级 | 内容 | 处置 |
|---|---|---|---|---|
| S1 | 请求 9 | 建议 | `review-r9/raw-mutation-bundles/bo6-tail-persisted-failure-v4.txt` 内嵌 experiment.json 快照的**全路径** `mutation.patch` 哈希为旧值 `d7dfbe59…`，当前原件为 `fd6da338…`。 | **采纳（以说明注明，不改写证据）**：该汇编包是修正前冻结快照；权威记录已核验为当前值（`experiment.json` 的 11 项 artifact 哈希与 `owner-checkpoint-recheck.json` 全部匹配实际文件）。已加 `review-r9/raw-mutation-bundles/METADATA-CURRENCY-NOTE.md` 说明版本差异；同包的 patch 本体、TRX 与日志证据一致，不受影响。 |
| S2 | 请求 9 与 10（两位审查者独立指出同一处） | 建议 | `WorkflowRunner.cs:1486` 注释仍写"取最后一条可定位停驻标记为基准"，与 `:1503–1515` 的全序最早选择矛盾；`:1535–1537` 注释仍以"驱动循环按线性推进（DriveAsync/Relocate 无完成跳过）"为下界约束理由，与 `:655–662` 的停驻恢复完成过滤不符。 | **接受观察，但本批不改动源码，登记为具名文档残项 BO-6/7-D1**（理由见下）。两份报告均明确该问题不改变已核验行为、不阻断裁决。 |

### BO-6/7-D1 登记（注释陈旧）与不改动理由

- 位置与更正文本（下一步修复时直接采用）：
  - `WorkflowRunner.cs:1486`：应为"取**计划全序最早**的有效、未完成停驻标记为基准"。
  - `WorkflowRunner.cs:1535–1537`：下界理由应改为"探针结果不得早于锚候选（锚可定位路径的语义下界）"，删除"DriveAsync/Relocate 无完成跳过"这一已被停车恢复过滤取代的表述。
- 本批不改动的理由（三条，均为可复核事实）：
  1. 会诊裁决明确绑定当前字节：两份报告都记录了当时源码 SHA（LF `181aa93e…76f14c9`、CRLF 形态 `5470cfcb…1f190f96`）并据此核验突变绑定。改动源码会使已获得的原级裁决不再覆盖交付字节，而追加额度已用尽，无法再取得同等级复审。
  2. 改动会作废既定证据链：manifest 的 `current_source_hashes`、8 项突变的 `original_sha256/restored_sha256`、两份送审汇编包内嵌的源码哈希与多份文档引用都绑定该字节；保持一致性需重跑 8 项突变（约 24 次构建/测试）并重新生成审查者已核验的汇编包。
  3. 两位审查者均评定为**建议级**且明确"未据此发现运行正确性缺陷、不阻断裁决"；按处置纪律，建议级可书面说明并登记理由。
- 修复条件：在下一次**会重新绑定 `WorkflowRunner.cs` 哈希**的改动中一并修正注释，并按该次改动自身的验证要求重跑反向突变与受影响回归。若 owner 要求立即修正，可另立小额批次/额度，本批不自行扩张。

## 结论

- 四项原级义务（BO-6 R19 MUST、BO-6 R21 F4 IMPORTANT、BO-7 R21 F1/F2 MUST）均已由独立本地只读会诊按**原等级**裁定 closed，且两次报告都未提出新的 MUST/IMPORTANT。
- 本批未重开 BO-13、SB21-3 BO-4、SB21-2 BO-10/12；未纳入 BO-8/9；未提前集成 R5.6、R6.1、R6 diff guard。
- BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程继续关闭；会诊结论只覆盖助手侧源码与已保存的真实 Runner/驱动夹具证据，外部执行边界是 fake，不构成实机或生产验收。
