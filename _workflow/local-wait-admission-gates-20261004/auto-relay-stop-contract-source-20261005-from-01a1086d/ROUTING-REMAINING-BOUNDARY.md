# 历史路由候选仍开放的合同边界

当前新字段只证明每个首次发送意图的边界模式；本轮具名新夹具是单次发送，不能推及同 submission 多轮发送/恢复重新准备。需要沿原 PreviousSendRounds、PreparedSendPermit、原恢复关联及 Runner 的相同 key 重用追查：原直通 false 的出现，在后续合法恢复或边界切换后是否可能经节点仲裁发送；若能，首次 false 不能免除后轮节点责任。缺当前凭据但仍有旧轮次/许可时须逐轮核证。涉及跨代际/错误释放，按 important / implementation / open 保留（HISTORICAL-ROUTE-ROUND-1），本轮没有关闭证据。

没有路由字段、没有原节点凭据而只剩流程登记的真正老直通与原节点记录丢失，当前可读事实可能不足以区分。不得从 null、当前开关或空节点集合推出直通；也不能将永久 Unavailable/Pending 当完整功能可用。需要可核验原关系恢复或明确独立综合合同裁决；本轮未取得老直通所有缺证场景的可用性证明，原 HOST-ABSENT-MAPPING-1 / 老格式未知仍 open。

原 CORRUPT-STOP-OWNER-CONTRACT-1 两个 Cancelled 期望与 owner fence 冲突、完整 stop/permit/history/terminal 双次序、真实端口及 10s/15s/TTL 余矩阵、SDK/MSBuild/task/compiler/package/native capture-before-build 来源闭包、独立综合后审与全部实际产品交付全部保持。本轮没有独立请求，不消费余最多一次 Sol/high 实现复核；不把此候选或窄测试当最终收口。
