# 下一共享依赖定位（不是通过或施工许可替代）

当前修复只补真实 Host ExternalStart 迟到原轮完成归档后的合法重放，不能把它外推为全部 G7/最终验收。

当前源码 `TaskCenterHost.Admission.cs` 的 `TakeoverLedgerScan` 从原 ExternalStartLedgerEntry 投影为 TakeoverLedgerFact 时保留 identity/seq/job/run/OperationType/回执与终态来源时间，却不投影 CandidateId/ResourceRef/ActionId/TargetBgiEpoch。`SettledArchivedTerminalReplayMatches` 能核原身份、终态和审计，但不能从此投影单独证明原台账与原操作的上述因果字段一致。此为待反例验证的精确缺口，不是已证实可错误清偿；其他层可能拒绝，须沿实际完整链核查。

后续集中矩阵须沿 Host 实际 ExternalStartLedger 产生/恢复路径补：不同/缺 epoch、candidate/resource/action、原 job 和每轮 identity/seq/原预观察；热区/归档/新 Host/未完成/严格结清前后交错；同时保留 Node 当前原 payload/job/epoch/key/run/node/occ/loop/attempt/task/config/version/operation/许可nonce 的全部因果约束。禁止改 Node OperationType 绕过专属守卫，禁止由当前定义重建原事实。原 SenderOverride 第二轮属于受控端口，原第一轮迟到回执及完成观察是实际 Host 生产路径，不是全部 ExternalStart 原适配器跨进程多轮证明。

原 `OriginalHost_RunnerRecoveryUsesOriginalRoundAndStrictFacadeClosure` 已有 history-accepted/history-publish/history-settle/history-multiple/history-archive/history-archive-conflict；其 history 原件模拟应结合真实生产 RunStore/Lease/Host 查询链补完整多 history/outcome、严格结清中断、封印幂等与原任务 active→退出。保留各历史模拟的局限，不能把可解析/Load/新存储读回当完整 IPC/User验收。

本包五笔已发来源（9f85 原请求的3原生路由失败+1CLI不支持+G10实质 blocked）已经读取原失败与实际独立来源。control-native 的 d25e7c 与 f82d49 两份 plan request/report/receipt 可定位，但属原 control 包，历史 G 与 control/R56 所有责任和预算必须继续逐项核账；不得将原空 history 或此文件当总数归零/剩余额度证明。本聊天新增独立请求0。统一综合后审须覆盖原完整源域及原 prior，不仅本次44行源码；认证/机械audit 原阻断保持。

原完整功能/实机/数据保留交付判据不变，全部原级责任 open、生产门关闭。
