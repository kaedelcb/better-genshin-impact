# 暂停运行停止后的终局回写

当前完整版本工作包内发现 PAUSED-STOP-TERMINAL-RECONCILIATION-1，原级 important/open，未独立闭合。实际入口为 TaskCenter 冷启后对原 Paused 运行 Stop：run-80de123ba00e 已 Cancelled/StopRequested，但原仲裁操作 d3023733d80d442da42bf668e46ba81f 仍 Accepted，terminalRelease 为 null。原运行、实际反例和仲裁原件保留，不能称封印已完成。

准入绑定：本版正常暂停、冷恢复和停止，用户看到 Effective 后关联受理责任未终局写回；只将 TaskCenterHost 的暂停态 Stop 返回接到已有 ReconcileAdmissionTerminalForExplicitStopAsync，与停驻/未知停止的核对及失败重试合同一致。不改 owner、Unknown、epoch、停止意图和原身份保护，不触发收尾或资源提交。完成后回到同套产物组合重演及有界综合收口，未新增审查请求、opening 或预算域。

两个生产 Runner 夹具从未来节点等待暂停、退出旧 Host、冷建 Host 后停止：正常路径要求有效 runstore seal 与原操作 TerminalCompleted；发布故障要求 Unavailable、Cancelled/耐久 Stop、保留 Accepted，清故障显式重试成功且重复 Stop 不改记录。LocalPort 禁止提交/任务查询/取消游戏任务。首次夹具漏传保存期望修订，导致无关 WorkflowRevisionConflictException，失败保留，不能当目标红例。retry1 修正夹具后，两条红例分别命中缺 seal、Unavailable/Effective 差异。

retry1 的绿色和 finally 恢复阶段均344项、342通过、原2 LocalWait失败。反向突变只移除本次暂停 Stop 的终局核对调用，两个关键断言准确重新变红；源码同目录 tmp+fsync+os.replace 精确恢复。各阶段完整日志/TRX/source-hashes/进程来源/Job0保留，旧失败 ID 7ff2dc0d-a3c3-d14e-a619-fee985a3deca、62d390bb-e91f-98d9-6e78-38ce05d3ea47 及 Cancelled/LocalWaitParking 原值保持。

源候选验证完成；当前产物刷新和实际重演、独立综合处置仍未完成。原 PATH-ARRIVAL-MODE-DOWNGRADE-1 important/open、既有101/39历史及原报告/额度0保留。完整产品未交付，不按此夹具结果关闭原级问题。存储本次256MiB操作预约，原1.5GiB最大、20GiB总额、8GiB余量及同域账本保持。
