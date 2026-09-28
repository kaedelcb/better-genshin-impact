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
