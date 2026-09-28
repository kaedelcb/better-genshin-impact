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
