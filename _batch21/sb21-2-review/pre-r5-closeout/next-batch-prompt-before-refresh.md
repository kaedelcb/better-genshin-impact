【槲寄生证据族清理阶段·续行提示词（ZCode 链，copy-paste 即用）】

【owner 裁决（2026-09-25 交接会话采纳施工方建议，复制本提示词即视为确认；如需更改请在开工前说明）】
裁决1（ev1 例外收口）：**授权**——机械 gate≠0 记 owner 授权例外（台账与文档如实记载，不写「无必改」），随后提交暂存区 9 文件并完成登记。
裁决2（EV1-R1 三选一）：**(a) 归占用者级别接线批**——EV1-R1（RunStore.List 静默跳过坏记录掩盖 WireRunId 唯一性歧义）挂接「接线前必办清单」，与批次 14 五项、批次 17 进程隔离残项同清单管理；修复与占用者级别来源接线（§24.99 残余⑥）同批设计。

【先建本批 Goal（结果＋约束＋完成判据三要素），再开工；Goal 未建立前不得修改任何文件。】

你在 ZCode 承接「槲寄生证据族清理」阶段续行（上一会话 sess_902414dd 于 2026-09-25 冻结）。工作目录
E:\Program Files\better-genshin-impact-LCB，分支 main-OldTeaBag-B168。

## 零、owner 裁决执行（见顶部裁决原文）
1. 按裁决1 完成 ev1 提交：显式列暂存区 9 文件逐一提交（禁全量暂存类命令），提交信息注明「owner 授权例外收口（gate≠0 记例外）」；
   台账补记例外授权；把 §24.99 残余表「宿主分支测试证据」行改 ✅（附 gate 例外与残项去向）并同步交接稿与 R5.3。
2. 按裁决2 把 EV1-R1 登记进「接线前必办清单」（与批次 14 五项、批次 17 进程隔离残项同点），注明 owner 裁决日期与选项。
3. semantic_handoff 任务状态从 needs_user 翻转（ev1 完成后按工具要求登记）。

## 一、ev1 冻结现状（裁决后即可提交）
- 暂存区已就绪 9 文件：MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs（两处真缺陷修复 +5/−1）、
  Test/.../TaskCenterHostOccupantLevelResolutionTests.cs（15 例新夹具）、_ev1/ 证据 7 件（突变日志/目标书/逐名差集/
  TRX 摘录/SHA 清单/施突脚本/TRX 归档 zip）。
- 台账：C:\Users\Administrator\.tools\zcode-relay\test\ledger-ev1.json（15 轮全程可审计）；
  冻结 manifest：C:\Users\Administrator\.tools\zcode-relay\handoffs\evidence-stage-20260925-manifest.json（逐文件 SHA-256）；
  交接稿：handoffs\evidence-stage-20260925-handoff.md。
- 提交方式：显式列上述文件路径逐一提交（禁全量暂存类命令；管线 hash-object+update-index 可用）。
- 写权：task-relays/mistletoe-zcode-20260925--evidence-stage.ownership.json（owner sess_902414dd…）；
  本会话请用 relay_ownership.py claim --transfer mistletoe-zcode-20260925--evidence-stage-2 --kind taken 重新登记。

## 二、待办批次（顺序执行，每批 Goal→红→绿→会诊闭环→提交→登记）
- ev2：RecordTerminal 两侧分裂表征夹具（BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs:259 起；
  队列 completed 与 JobRegistry 终态分裂的确定性表征＋观测边界固化；§24.83-84）。
- ev3：执行树退出观测并发采样夹具（BetterGenshinImpact/Service/Execution/JobRegistry.cs JobTreeSnapshot＋
  ExecutionScope.Dispose 派生收集；§24.103）。
- ev4：顾问件登记批——D-family 复审（IW-01~09/SW-01~03，.tools/zcode-relay/docs/dfamily-interaction-review-20260925.md）
  与证据审计（0ea1ba6a7→82522dae5 等 7 条，docs/evidence-audit-20260925/report.md）按残项流程登记（文档批）。
- ev5：等待队列接线前合同草案收录评审（§24.106 C1-C10＋§24.99 六项＋D-family sharpening；不接线）＋owner 待决表汇总交付。

## 三、纪律（原样生效）
- 每批必读 .agents/knowledge/domains/review-disposition-discipline.md；R1-R6；防重犯五条；生产门/真实 User 门/R5.8 继续关闭。
- 会诊工具 C:\Users\Administrator\.tools\zcode-relay\（consult.py/consult_run.py；--model auto）；
  端点 http://127.0.0.1:10531/health（不通跑 C:\Users\Administrator\.tools\aiproxy\start.ps1）。
- 已知工具缺陷：prepare 不清理输出目录（每轮先清空材料目录再 prepare）；请求体上限约 950KB（TRX 大帧勿入 scope）；
  --extra 单文件；mistletoe-session-relay 与 unified-job-registry 两文档不纳入提交；全量暂存类命令被安全钩子拦截属预期；
  会话内批量文本替换用 Write/Read+Edit，勿用 heredoc 内嵌长字符串（双层转义两次损坏脚本的前科）。
- 批次 18（D5）提示词已备份于 task-relays\batch18-d5-prompt-preserved.md，证据阶段收尾后恢复其指针地位。
