# Prepared Stop / terminal seal 接续

沿用 usable-delivery-control-recovery-20261003 和原 opening / blocked 前审 / 原级义务。采用 delivery-first、auto-local-checkpoint-commit、handoff-auto-create 政策。此目录仅记录接续证据，不另建包或改历史。

当前聚焦转换：冻结提交 → 同 prepared 单次消费 → Stop 阻止本端口调用 → 同 RunStore 门内发布本地零调用证明。原 SendAttempted 保持 true，rawTerminal / exit 不伪造，类型与远端拒绝分开。

| 类别 | 场景 | 要求 |
|---|---|---|
| 状态 | 准备后耐久 Stop，同冻结身份 | 零端口调用、证据耐久、恢复不留虚假在飞 |
| 状态 | 权威未知/记录缺失 | Unknown，不能发布 Stop 零调用证明 |
| 并发 | 已消费/重包装同 submission | 不再发送，不另产证明 |
| 并发 | 已有 job / acceptedSendIdentity / rawTerminal / exit / 远端拒绝 | 禁止本地证明，保留责任 |
| 并发 | 指纹、payload、原 epoch / wire / 出现 / StopAuthority 冲突 | 禁止证明，Unknown |
| 故障 | 发布失败 | 不释放，仍零调用，盘上原责任保留 |
| 恢复 | 重新加载证明后 Stop | 同资格清偿，不对原键重复发/查询 |
| 单调 | 证明被删/替换/同键追加矛盾受理 | RunStore 拒绝 |

依赖后续：全 run 不可逆封印及完整三段资格 → Host 锁外释放/回写/同封印重试 → 等待/暂停/LocalWait、面板对账、Source 交错 → 原功能/迁移/分发/实机。生产门关闭，原级问题 open。

子 Agent 决策：本段共享可变身份/RunStore/Runner连续修复由唯一写者串行推进，当前固定只读分工净收益不足。稳定整包的独立综合后审仍按 Skill 使用独立审查者。
