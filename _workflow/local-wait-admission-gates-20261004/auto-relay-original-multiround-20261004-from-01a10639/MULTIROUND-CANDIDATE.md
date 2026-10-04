# 原Host显式多轮发送候选（原共享包）

沿原opening、全部请求/预算及G2(e)/G4/G4a/G7/G8/G10/⑤⑥原级important implementation open。生产门关闭；本文件不授予认证、独立综合后审或实机验收。

修复两条实际Host链：同门面实例显式RetryAsync沿用首次可信适配器冻结的节点请求上下文和原调用令牌，缓存绑定原LeaseId/OwnerEpoch、完整候选身份/载荷、run/key/游标；原缓存不能被后续请求替换，不序列化，也不从当前流程定义重建。新实例无此上下文仍响亮拒绝。没有增加自动重试、UI入口或改变一次普通提交合同。

首轮拒绝返回给Runner前，另一显式重试可能已受理。Host现在只在同原请求、原父来源、完整前轮nonce/证明/拒绝/预观察、当前许可、真实受理回执及耐久接管关闭共同成立时采用该轮结果；缺失/冲突/关闭失败返回Unknown并保留事实，不能用初轮旧拒绝覆盖第三轮受理。执行端口受控，观察屏障安排显式重试交错；不是实机IPC/User或新增公开重试流程。

16场景涵盖两种原父来源各自合法1→2→3、原nonce/证明/载荷破坏、拒绝历史删除/重复写入被不可变存储拒绝、第三轮可能受理异常、真实受理后接管关闭异常。两种异常经旧Host Shutdown后新Host/new Runner/new stores按原第三轮恢复/真实退出观察/Stop和重复Stop收敛；原前两轮许可及证明逐字相同，原payload/expires保持、发送总数仍3。拒绝历史场景证明普通写者不能删除/重复原记录，不冒充损坏文件Host重试。

最初两个真实红例证明Retry丢原上下文；上下文修复后两个Succeeded→Unknown红例证明旧结果返回交错。编译限定名/插入位置/构造参数错误、端口配置每次重置故障及Error无定位seq的夹具修正均保留，不当产品语义红。`prior-faults-red.trx`中6个invalid_mutation_state属于故障注入方式被存储守卫拒绝，不当产品红。改原始损坏文件的14场景整体中止，原TRX明确其中2项已Passed各约51s，慢在夹具等待有效台账；这不证明产品死锁。其他未完成项未计Passed，记录见interrupted-run-clarification.json。合法迟到受理的完整生产来源链仍须追查，不能把非法Node accepted_receipt形状的Corrupt拒绝当该链已验。

最终有效证据只用final-r2。Rebuild exit0；TaskCenter {'Passed': 1997, 'Failed': 18, 'NotExecuted': 2}，exit1；相邻 {'Passed': 441, 'Failed': 0, 'NotExecuted': 2}，exit0；精确合集 {'Passed': 2007, 'Failed': 18, 'NotExecuted': 2}，相对前序新增16/删除0/共有变化0，18失败testId集合一致，不豁免。17输入前后字节一致；声明面再生前后同SHA 80f0eac71016793501c7baa2cf7ab32544c7a3a6fa412729670cbcaf7cecc4f5 且清除变量重跑通过。三个关键P/F/P（原上下文、当前原轮读回、前轮nonce绑定）指定断言红、编译成功、逐项恢复SHA绑定final-r2输入。第一次final包装240s超时、全部原进程随后终态，不算有效回归；原全量TRX证明同条件约4m33s，因此final-r2改600s并完成，原过程未编辑。

机械audit仍exit2缺原native request 9f85a4b85f46400dbff20b97ebec4fda receipt。未改旧manifest/policy/工具/冻结请求，未伪receipt。当前来源新增独立请求0。已读本包4次渠道失败及G10独立blocked源，control-native两份方案请求也已定位；这些并不自动证明所有继承G/control账核清，新增前继续核原账，不宣称剩余额度。

下一共享依赖：合法旧轮迟到受理与已归档原身份责任、全部history/outcome/严格结清中断/封印矩阵；沿实际ExternalStart逐轮台账和Host/BGI生产来源核查，不能篡改Node OperationType去绕过ExternalStart专属守卫。保存现有多轮候选，补齐全账及完整源域认证/统一sol high综合后审并成批原级闭环。全部原约定功能新产物实际运行/停止/重启/数据保留和可运行版本仍欠，不缩总Goal。

保护User/第三方JS/.kiro/旧D盘、材料外R56/csproj/工具/文档/暂存。构建测试与突变已终态；只保存明确本地候选，不push/部署。所有过程原件及source-before、inherited-evidence-read、final-r2原TRX/log、impact-comparison、mutation-observations/post-mutation-byte-observation为权威。
