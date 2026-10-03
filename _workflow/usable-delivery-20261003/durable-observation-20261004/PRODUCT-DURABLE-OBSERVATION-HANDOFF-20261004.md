# 耐久观察、单调事实与停止回写：产品接续现场

总产品目标未完成，原控制包未收口。当前为候选源码/普通受控证据；没有发布、启动新产物或实际运行/停止/重启/数据保留验收，没有独立综合实现后审。原四 CONTROL MUST、全部原级义务与新增重要候选保持 open。沿用 `usable-delivery-control-recovery-20261003`、原 opening / 历史 / 请求计数 / 两次 blocked 方案前审，不第三次纯方案审查。

本聊天 thread=01a1031a-4344-75b2-a519-309d0153981a，接收 AUTO-PRODUCT-RELAY-20261004-FROM-01a102c5。原生 Goal 使用3475字符完整范围摘要＋原交接全文约束（工具上限4000），真实读回在上一段 adapter-exit-20261004/auto-relay-20261004/new-goal-readback.json。后续暂停/接班状态以本目录 auto-relay 的真实原生读回为准，不凭本文件证明 Goal 状态。

## 候选实际增量

1. 共同观察器在完整冻结 epoch / job / key / wireRunId / node / occurrence / iteration / attempt 核验后立即发布观察事实，再进入下一查询/延时。实际终态与退出/效果分开；后来活动态、查询OCE、delay取消、实际cleanup预算到期不洗掉已读事实。
2. 前置/收尾无job原键唯一命中先耐久绑定，再核对最新冻结载荷/句柄并取消。修订变化合并保留他方 Note/StopRequested；身份、指纹或既有句柄冲突、落盘失败拒绝取消/补发。旧形状接口复用一次 ListWithIntegrity 找唯一完整所属，坏记录不当空。生产 Runner 的恢复/停止入口明确传所属 run。
3. RunStore 原门内保护完整已发送身份、已读合法终态/退出、既有句柄及前置/收尾冻结票据/指纹/有效期；完成历史完整记录不可删或替换。允许同一原子事务首次发布清偿证据并移入历史。尚未实现 terminal-release 封印，不能据这些守卫当作整个终局资格已闭合。
4. Runner 在主体观察/备注后以及每个前置结果后发布，防后一个适配器 rebase 洗掉前一个未发布的归一化结果；多前置实际反例及因果成立。

12个源码/测试文件（含3个新增）的改前字节在 before/，大小/BOM/换行/hash在 current-source-integrity.json，精确本聊天增量在 this-chat-only.diff。既有原行换行保持，没有整文件Git恢复或大幅缩水。配套导航登记在 control-native-config.write_paths 和 control-manifest.continuation_sources；原 manifest.sources / opening 未倒改，整包认证阶段须按真实影响域整理当前完整来源。

按自动本地提交政策保存候选检查点，明确19文件范围见 commit-scope.json，实际commit/HEAD/工作区以 COMMIT-RESULT-20261004.json、git-status-post-commit.txt 为准。开工HEAD3719aa2b72067fbe871f37ae31c70f07f3bddda7仅为本段历史。原8e37f68b控制修复和3719aa2b政策提交保留；材料外成果和原账不清理、不整库提交。提交不授予审查通过或生产许可。

## 证据与局限

- 最后源码恢复后 Rebuild 退出0，DeployToBgiTools=false；restored-impact 709/709，无跳过，覆盖适配器/主体边界/Runner/RunStore/Host/LocalWait/来源/停止权威/声明面与发送点。pre-test源域242项与两程序集hash在 restored-impact/source-input-observation.json，收尾源码无漂移。
- 六项普通 P/F/P（观察落盘、取消前绑定、单调事实、历史不可删、多前置结果发布、单次所属快照）指定testId/断言与源码恢复在 causal-* / record.json；causal-current-readback.json原/恢复/current SHA一致。编译失败/中止不算突变；旧四项之后测试/调用形状有增量，保留版本局限，不当当前整冻结域认证。
- full-r2 为最终单次所属快照修正前普通全量：2070 Passed、原六 Failed、2 NotExecuted，共2078；相对上交接新增39、删除0、既有outcome变化0，失败testId完全相同。这不是最后读取修正后全量或认证receipt；最后局部修正已按影响运行709/709，整包稳定后仍须当前全量/认证/独立综合实现后审。原六迁移失败未修、未豁免。
- red-r2的10旧实现红例、monotonic-red的16旧存储红例、多前置red、owner-snapshot的1红1绿，首次中止、编译/锚点错误和中间回归失败均保留，详情CANDIDATE-NOTES.md。没有把普通日志/TRX改成认证收据。
- 没有BGI源码增量，未为交接重复BGI构建/105例旧测试；旧BGI普通证据和版本局限保持。真实收尾result_unknown与实际兼容验收均未解决。

## 唯一下一项及依赖链

先沿 control-plan-v2.md 补红例并实现同 prepared 对象准备后Stop的**耐久零调用清偿**。当前 SendPreparedAsync 的 beforeSend StopRequested/权威不可确认分支仍返回Unknown，发送事实保留；不能改SendAttempted=false猜未发。必须同prepared消费身份、完整冻结载荷/原epoch/wire/key/fingerprint/出现、明确未调用本端口及无矛盾受理事实，在原RunStore门内发布类型化本地证明；与远端拒绝证明分开，冲突/未知保留责任。

随后同控制/恢复链集中完成 RunStore不可逆终局封印及完整原身份/三段退出/实际效果资格，封印后全部新增/回退/改写事实拒绝；Host节点和运行全部释放入口消费同封印、锁外回写与同封印失败重试。TaskCenterHost.Admission.cs节点MarkOperationTerminal约192、运行约2696仍只作导航，追全调用链；本段没有修这些入口。等待/暂停/LocalWait停止复验撤销、Completing/Unknown实际面板停止/原身份只读对账、多已有run/在飞Source停止交错仍须反例。旧自动配置缺基线及更换BGI epoch实际兼容待验收。

完整产品范围、保护和工序不变：原公版共有＋C01/C02/C04–C11/C17共11增强及C20、八类单项、priority/fixed/flexible、legacyFiltered/空weekdays/水位once、跨日截止、管理导入导出/跳转、C17实际效果耐久结算、迁移六失败及兼容分发。总计划/原用户决定、DELIVERY-COVERAGE和原审计继续权威。缺功能/误执行/数据风险/停止未确认/错误释放/未知假成功不得延期。

并行发现本次ok=true/errors=[]，上一段 auto-relay/delivery-discovery.json保留；R56同HEAD/hash搬迁已补账不重复集成，R61资源仍缺。后续读取索引/JSON并运行既有发现，按依赖消费；不让owner搬运。全局/项目AGENTS第0/4.1节、项目Skill、独立审查Skill/合批、review-process、facilities、tools README继续适用。gpt-6.1-sol默认medium，复杂/高风险high；整个控制包稳定后统一规定认证与独立综合后审。

保护真实User/配置/宏/截图、第三方JS、.kiro、旧D:/DOWN安装；不清盘、不杀用户程序、不复制历史产物。专属products沿用adapter-exit原目录；仅早期按明确创建身份停止本次私有测试testhost。实际验收前重新核对PID/创建身份/路径/Session，不复用旧Session6或放宽守卫。

交接判断：耐久观察与原身份事实已形成可复核候选边界；下一段prepared证明、封印与Host锁序需要连续追查，当前上下文包含多版普通全量/定向/因果以及最后局部读取修正，切分能保留证据等级和版本边界。不是按时间/工具数强制切换。安全操作与所有突变先终态/核验，按常驻授权暂停本聊天Goal并真实读回，再自动创建唯一同项目local接班、完整一次初始提示词并核验新Goal active；旧聊天停工，总目标仍未完成。
