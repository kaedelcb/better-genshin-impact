# 本轮纠正的证据边界

来源聊天：01a104a0-a853-70e1-82a6-4d89ce3a5bbc。沿原 local-wait-admission-gates-20261004 批次、opening、全部历史请求及原级重要义务继续。未增加独立审查请求。产品 Goal 未完成，生产门关闭。

此前六笔提交是真实候选进度，但“G4/G7 核心链完整”“同游标重放已排除”“历史关联机制不破坏所有旧封印”等结论没有对应完整证据。本轮先红反例，证明并纠正：

- 普通 Update 可以追加恢复关联；新记录创建还可以绕过已有记录的 guard。现只允许专用 RunStore 事务追加一个被授权的关联，普通更新和新记录创建均拒绝，旧关联不能删除/修改。
- 关联只绑定 Key，错误 job、epoch、sendSeq、发送身份也能写入。现要求唯一原历史 index/hash、原 outcome index/hash、同原 job/epoch、合法原发送序号格式；无匹配、多个匹配或冲突零发布。正例证明原 SubmissionHistory 与 NodeOutcomes 的序列化原件不改，合法关联能形成自己的节点封印并重开验证。
- 空 recoveryAssociations 字段改变运行封印 hash。现保留两种已存在格式：字段尚未引入的旧 hash，以及六笔候选期间包含空列表的过渡 hash；非空关联仍绑定到新 hash。物理旧记录重开与普通事实变化拒绝由夹具证明。
- 原三参数 ReconcileSubmissionAsync 被改成四参数可选方法后，没有实现 IWorkflowExecutionBoundary 的三参数成员，Runner/仲裁包装器会调用默认 Unknown 而根本不查询。现恢复精确三参数成员，并由真实接口夹具证明查询次数。
- 受理落盘失败的内存回滚漏 AcceptedSendIdentity，现与 Intent/JobId 一并恢复。
- 门面结清失败仅打印日志、历史查询未知仅 continue，仍可能继续返回停止成功。现缺门面、结清失败、历史身份无法唯一解析或无唯一查询命中都返回不可用；真实宿主交错仍须后续完整验证，不能以这段源码宣称全部停止场景闭合。

验证文件均为真实普通进程/TRX 观察，不冒称认证 current_regression receipt 或独立实现 pass。red.trx、red2.trx、creation-red.trx 保留原红例。mutation-baseline.trx 对应初始十二个目标；五项突变各自指定断言在 M1–M5 的 TRX 中失败，源码字节恢复记录见 restoration-readback.json、M5-baseline-subject.json 和当前源码观察。最终恢复产物的全量结果与精确 testId 差集另存观察 JSON。

最终串行 Rebuild exit 0、0 错误；current2-taskcenter.trx 为 1833 Passed / 18 Failed / 2 NotExecuted = 1853、测试进程 exit 1。impact-comparison.json 对旧 taskcenter-after-g7wiring.trx 的 1840 项精确 testId 比较：新增 13、删除 0、共有结果变化 0、Failed 身份集合完全相同。最终源码八文件哈希与 current-source-observation.json 一致，全部突变已恢复；不是全量绿色，也不授权生产或产品验收。

首轮全量 current-taskcenter.trx 多出一项 R56 PersistentModernHistory 测试，错误 commit_authority_and_artifacts；其单独复跑 48/48 与后续 Rebuild 重叠，后者明确 MSB3021/MSB3027 文件被该 testhost 锁定。因此该复跑只作诊断，错误日志保留；等待两者终态后重新串行 Rebuild/红例/修复/全量。不能隐藏首轮十九失败，不能把“名称属于 R56”解释成精确身份集合相同。

## 尚未完成的原责任

G4 发送许可消费前后仍缺完整原子游标证据。旧记录 CursorRevision=RecordRevision 与新 LoopIteration 的混存兼容、同游标不同请求/迁区/重启的重放尚需真实反例，不能当成已排除。

G7 当前/历史查询缺完整服务器原载荷指纹、taskId/configRevision 比对；新增历史 hash 关联不代替远端受理证据。下一包保留 BGI ExecutionRequestContract.Fingerprint(request) 的服务器独立计算和去重/冲突拒绝，只在真实接纳点冻结原请求证据并加法查询投影。**不采用此前“信任调用方 payloadFingerprint”建议**。还须核 ArchivedOperations、原多 sendSeq 唯一性与此前拒绝依据、迟到受理、原父来源类型、完整停止/重启入口。

原 G2(e)/G4/G4a/G7/G8/G10/交错⑤⑥均保留 important/implementation/open。原方案 blocked、不倒签前审；按 delivery-first 继续明确定向修复，不新增第三轮纯前审。综合后审前核全部原账及预算，当前可定位五次请求不构成全历史完整核账；未知历史不能自称剩三次。认证执行、声明面、独立综合后审、所有约定功能真实运行/停止/重启/数据保留及正式交付均仍欠。

原 User、第三方 JS、.kiro、材料外 R56/工具/文档与既存未提交成果保持。此次仅本地候选检查点，不 push、合并、发布或部署。下一工作包仍继承原全部最终功能范围，不缩小为 G4/G7 单项。
