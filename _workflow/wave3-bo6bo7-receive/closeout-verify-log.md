# 接收批审验链记录（`wave3-bo6bo7-receive`）

`tools/mistletoe/workflow.py` 各步均以 `PYTHONIOENCODING=utf-8` 运行；`quality_verdict` 一律为
`NOT PROVIDED`（工具只做机械核验，不批准质量或收口）。

| 步骤 | 命令 | 时点 HEAD | 结果 |
|---|---|---|---|
| 开工快照 | `begin --manifest _workflow/wave3-bo6bo7-receive/manifest.json` | `6fd6207e5`（开工前） | ok，9 行风险矩阵冻结，4 个源文件哈希为开工前形态 |
| 送审快照 v1 | `audit --stage review --out …/review-intake-20260928-v1` | `6fd6207e5` | ok（packet 504,184 B）；`verify` ok |
| 送审快照 v2 | `…-v2` | `6fd6207e5` | ok（packet 508,322 B）；`verify` ok（第 1 轮会诊送审材料） |
| 送审快照 v3 | `…-v3` | `6fd6207e5` | ok（packet 523,573 B）；**未运行 verify**：为给后续编辑留出本地 packet 余量而做了合规去重，manifest 随之改变，v3 被 v4 取代 |
| 送审快照 v4 | `…-v4` | `6fd6207e5` | ok（packet 515,669 B）；`verify` ok（第 2 轮验证轮送审材料） |
| 送审快照 v5 | `…-v5` | `6fd6207e5` | ok（packet 516,670 B）；`verify` ok（两轮会诊结果回填后的冻结材料） |
| 主线接收提交 | — | `1945913a4` | 8 个来源文件 + 5 份审验快照 + 本批全部证据（223 文件） |
| 台账回填提交 | — | `8a014cf78` | 并行成果台账/索引标记 `verified` 并附收据 |
| 收口快照 v6 | `audit --stage closeout --out …/closeout-receive-20260928-v6` | `8a014cf78` | ok（packet 456,823 B）；`verify` ok |
| 收口复验 v7 | 同上，`…-v7` | 最终文档提交之后 | 见本批最终报告；该目录为本地证据，不进提交 |

## 说明

- v1、v2、v4、v5 四个送审快照均在 `6fd6207e5`（提交前工作区状态）上 `audit` + `verify` 通过；v3 只做了 `audit`，随后被取代，**不主张其已通过 verify**。
- 收口快照在提交后重跑时 `scoped-staged/unstaged` 为空（导入已入库），因此体积小于送审快照；导入增量本身仍作为受索引证据保存在 `verification/import-delta-8files.diff`（接收后改动见 `verification/post-import-delta.diff`）。
- 本地 packet 上限为 524288 字节（不可调高）。为在后续编辑中保持该上限内，本批做了一次**合规去重**（见 `manifest.json` 的 `packet_pruning_note`）：移除两份被取代的再生前回归记录（文件保留在磁盘、结果已在 `findings.md` 复述）、三份内容与来源检查点重复的来源文档、被 `verification/targeted-93-ids.json` 取代的定向身份文件，以及被重述到构建条件与 `findings.md` 的部署目标抓取。未删除任何必需原始证据，未为适配上限裁剪任何摘录。
- 全部构建/测试使用 `-p:DeployToBgiTools=false`；部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant`（及非 x64 同名目录）在整批构建与测试前后 LastWriteTimeUtc 与文件数均未变化（1158 文件，09/26/2026 21:41:14 UTC）。
- 工具退出码 0 只表示声明范围内的机械核验成功；语义完整性、会诊裁决与生产门由本批材料与最终报告承担。
