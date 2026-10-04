# G4/G7 共享发送与恢复工作包：当前补充方案

沿原 opening、预算、报告和全部 important/implementation/open 义务继续，不建立新子批。服务器原请求证据、助手冻结比对、游标消费、防重放与恢复结算共享同一发送身份，统一后审；以下阶段是依赖顺序，不单独宣布义务关闭。

1. 在真实 BGI 接纳路径把服务器现有 ExecutionRequestContract.Fingerprint(request) 规范全 SHA256 与 operation/version 冻结到注册表作业，查询加法投影。原去重计算与冲突拒绝不改；旧作业缺证据保持缺失，认领既有作业不得重写原证据。
2. 助手在准备 CAS 保存同规范版本的原载荷依据及 taskId/configRevision，独立计算并对比查询。当前 24hex fingerprint 保留原含义，不重解释；历史缺冻结依据保持 Unknown。规范算法必须用跨端向量证明相同（日期、转义、排序、null、嵌套与 operation）。
3. 当前/历史恢复统一验证 epoch 三重一致、key 唯一、完整出现/task/config/载荷，绑定唯一原 submissionIdentity+sendSeq、ArchivedOperations 及前轮拒绝依据；专用关联发布/readback 后严格结算，不改历史/封印。
4. G4 权威游标准备/许可消费/发送边界使用同事务或明确保留期间的写者约束，补旧 CursorRevision 混存、Note、不同 request、迁区/重启重放；不以追加 Load 代替事务。

| 状态/故障 | 预期及针对性证据 |
|---|---|
| 新合法接纳/重放 | 只保存一次服务器计算证据，认领后原证据不漂移 |
| 旧端/旧历史缺字段 | Unknown，零补造、零重发 |
| operation/payload/task/config/epoch/key 冲突 | Unknown 或受理前冲突拒绝；零错误关联 |
| 受理后本地落盘前崩溃 | 原服务器查询 + 原本地冻结证据 + 唯一轮次后可恢复 |
| 多 sendSeq/迟到旧轮/ArchivedOperations | 前轮未发送/拒绝证据不足保持冲突，不自动择最新 |
| 关联发布/读回/结算中断 | 幂等续办、原件不变；active 不作终局清偿 |
| 游标检查后推进/停止/Note 并发 | 零错误发送，保留并发记录；重启迁区同游标不重复消费 |

先红反例、集中实现、串行 Rebuild DeployToBgiTools=false、跨端定向/影响回归、关键 P/F/P、声明面与认证来源，稳定版本统一独立综合实现后审。已有独立 blocked 报告授予定向修复方向，按 delivery-first 不第三轮纯前审。送审前核全历史累计预算，失败计次。服务器投影单独落地只是候选依赖进度，G4/G7 及其他原级义务不关闭；最终全部功能实机运行/停止/重启/数据保留和正式可运行交付仍必需。

旧证据仅对逐字节相同源码/合同/依赖/运行条件复用；任一相关变化即重新绑定和验证。保护 User、第三方 JS、.kiro、材料外源码工具文档和暂存。无生产、发布或部署许可。
