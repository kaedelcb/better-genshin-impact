# 新Host／四入口与待迁移原身份验证

仍同原共享包，原opening、预算、G2(e)/G4/G4a/G7/G8/G10/⑤⑥ important implementation open保持。delivery-first限定修复方向有效，不倒签前审。源码稳定后统一认证/sol high综合后审；当前不新增独立请求。

依赖与矩阵：
1. state：面板start与移交start均取得真实原父引用；32待迁移/256未到期墓碑饱和时拒绝新登记，全原Operation字段保持；成熟后真正迁移归档、重放保守拒绝。
2. state：真实首节点接受并终态、Pause到节点边界后停Paused，新Host/new Runner/new RunStore/new LeaseStore显式Resume；不重发已完节点，下一节点仍唯一原父来源，原history/outcome/seal/sendSeq保持。
3. state：模拟崩溃的耐久Running现场经新Host实际RecoverOnStart成Interrupted；只模拟崩溃状态，不宣称真进程重启；恢复不改原身份、不重发原已完节点。
4. concurrency：暂停请求先于首节点终态，受控端口异步闸门保证因果；旧Host shutdown读回后才创建新写者；无双驱动。
5. fault：上述来源变更/歧义/缺锚后新Host恢复明确拒绝且零新发送，原数据不补造；源写入由拥有者与专用RunStore约束保留。
6. fault：恢复、停止、封印异常既有真实来源测试继续回归；未枚举的G4/G7多轮迟到冲突保持open，不以新增绿测试宣称覆盖。

红反例先保存原执行，随后集中修复。编辑前后SHA/大小/行数/BOM/换行检查；Rebuild DeployToBgiTools=false与测试串行，复用g10产品；关键PFP突变用finally原子恢复与SHA读回。普通TRX不是认证receipt、IPC游戏User验收或独立pass。保护全部User/JS/.kiro/旧D盘、材料外R56/csproj/工具/文档/暂存，生产门关闭。边界、结论、实际修改和剩余转换保存后明确范围候选本地提交。

此阶段不派并行只读Agent：生命周期接缝/红反例与被测版本尚在连续修改，固定版本时统一独立后审，避免消费漂移报告。r61发现仍missing report，不当已消费，不阻本转换。
