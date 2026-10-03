# 控制链代码修复收口与实机验收交接（2026-10-04）

总目标未完成，原 usable-delivery-control-recovery-20261003 未收口。沿原两次 blocked 前审、CONTROL MUST/全部原级义务、opening/history/累计请求身份，采用交付优先、工作包合批、自动本地提交、自动交接。未做综合实现后审、当前认证、发布或新产物实际运行/停止/重启/数据保留验收。

## 本聊天实际增量及证据

3 笔明确文件提交，均经 Rebuild（DeployToBgiTools=false）+ 影响链定向回归 + 全量回归：

1. **52d806cc1 类型化拒绝合并修复**（红例→修复→突变红三段因果验证）：真实仲裁 Sender 的类型化拒绝清偿事实（serverRejectionEvidence/observedTerminal/executionExitConfirmed/executionExitDisposition/effectState）能合并回 Runner，不再被并发守卫误判为并发修改而保守 Unknown。NormalizeVolatile 白名单+MergeBackAuthoritativeSubmission 回拷同组字段；并发守卫对照（非发送段并发修改）仍拒绝。全量 2129/原六/2 失败身份不变。
2. **32f8f292c Completing 面板停止**：收尾执行期（Completing+PendingCompletion）可从面板停止，保留收尾责任。面板 15/15。
3. **7bcb545e9 Unknown 面板停止**：Unknown 可从面板停止；BGI 离线时对账不可考，保持 Unknown 且停止意图耐久登记；在线时走原身份只读对账（ReconcileSubmissionAsync/ConfirmCancellationAsync），全部清偿转 Cancelled，未清偿保持 Unknown。面板+宿主 25/25。

## 唯一下一项：实机运行/停止/重启/数据保留验收

新产物 `_workflow/usable-delivery-20261003/adapter-exit-20261004/products/MultiplayerHoeingAssistant.dll|exe`（当前源码 Rebuild）。需真实运行，核对 PID 创建身份/路径/Session，验证运行/停止/重启/数据保留。Source 多已有 run/在飞停止交错、直接 Runner raw 补记的原身份/退出/效果资格、旧配置/epoch/兼容均有现有测试覆盖（LocalWaitFinalizationContractTests/TerminalReleaseSealTests/R56Migration*/TaskCenterHostRecoveryAdmissionTests），语义完整。

整包稳定后统一当前来源认证、规定全量/关键因果及独立综合实现后审，按原级成批闭环。gpt-6.1-sol 默认 medium、复杂/高风险 high，原预算/历史保持。

保护材料外/原账、真实 User/配置/宏/脚本/截图、第三方 JS、.kiro 及旧 D:/DOWN，未操作生产程序、未清盘。RunStore 同实例门不当跨进程排他。实机前重新核对 PID 创建身份/路径/Session，先完整新产物，确需切换才给唯一必要动作。

## 交接判断

选择新会话：控制链代码修复形成可复核的集中候选边界；下一项实机验收涉及真实进程操作，需要在干净上下文中进行；本上下文累积约 850K tokens 且经历过执行故障（重复读取同段代码的死循环）。安全操作已终态后，先自动提交、保存现场，再原生暂停本人 Goal 并读回 paused，自动创建恰好一个同项目 local 接班；实际 cwd 及完整 Goal active 读回前接班不写产品。不按时间/工具数强制交接，不把总目标标 complete。

总目标继续覆盖原公版共有、C01/C02/C04–C11/C17 共 11 增强及 C20、八类原生单项入口/结果、priority/fixed/flexible 调度、legacyFiltered/空 weekdays/水位 once、跨天截止、管理导入导出/跳转、迁移激活/回退/原六失败/兼容/正式分发和新产物真实运行停止重启数据保留。权威入口保持总计划、2026-09-17 公版比较/兼容审计、DELIVERY-COVERAGE/assistant-entry-audit/feature-integration-audit-original 及 control-plan-v2/fact-table/manifest/native-config/opening/history。
