# 同共享链旧历史运行封印定向修复

继承原 Goal/opening/预算/所有 important implementation open，生产门关闭。当前红例 legacy-red.trx：12 Failed/4 Passed，全部失败为应拒绝却实际生成 TerminalReleaseSeal；这不是实机验收。

根因：BodySettled 对非 Observed 且缺 Accepted 的关联不检查；RunSettled 在 Accepted/Permit 均缺失时跳过原 outcome 集合，且不校验孤立关联。统一修复 BodySettled 的已存关联验证、RunSettled 的全关联及旧格式 outcome 集合。无 outcome 的旧关联验证改为原主体退出事实检查，避免 ValidRecoveryAssociation→RunSettled→关联校验递归；不修改原历史/结果，不补造身份/Permit。

矩阵：身份 Accepted/Permit 均缺 vs 当前完整；关联无/唯一合法/重复/错 job/epoch/index/hash/sendSeq/孤立；outcome 无/唯一合法/重复/业务结果冲突/raw 冲突；新封印/已有封印读回/重开/重复停止；当前 Observed 原退出关联与严格结清保持原夹具。旧 no-outcome 只在 Cancelled+StopRequested 且原主体有完整退出事实时可绑定。关键反向突变覆盖旧关联完整性、旧 outcome 集合与原终态一致性，finally 原子恢复 SHA。

串行 Rebuild 助手/测试及 ControlledWriterProbe，定向原历史/封印/Host影响回归、全 TaskCenter testId 差集及六原失败身份保留、声明面。本修复依 delivery-first 原定向许可，不增加第三轮纯前审，不按小函数派新整包审查；仍欠认证、原预算全集核对、统一 sol/high 全源域综合后审与全部功能运行/停止/重启/数据保留。
