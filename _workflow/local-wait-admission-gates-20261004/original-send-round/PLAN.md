# G7 原发送轮次与历史恢复候选

同原 local-wait-admission-gates-20261004 共享包，继承全部 opening、请求、预算、important/implementation/open；已有 blocked 明确定向修复许可，不新增纯前审。G4 nonce 消费、G7 轮次/历史/结清共同决定是否允许认领，统一后审。本目录是依赖阶段证据，不是独立新批或验收。

先保持行为抽取原宿主解析器接缝并执行红反例，再集中修复：现代 consumed 许可原 identity 与 acceptedIdentity 一致，按完整 run/node/occ/loop/attempt/key/epoch 匹配热与归档唯一操作，精确解析原 seq，不择最新。每轮预观察和此前所有轮次未发送/类型化拒绝必须有耐久依据；冲突/旧缺锚保持未知。服务器规范原 payload 继续独立核对。旧无锚不得根据流程重建。

|维度|反例/正例与判据|
|---|---|
|身份|permit 原 identity、acceptedIdentity、op seq 互冲突、旧无锚、零seq：不返回身份/不补记|
|范围|归档唯一原操作可读；热+归档重复/多操作同key：拒绝歧义|
|多轮|原permit旧轮而op最新：不能选最新；缺/冲突前轮拒绝：未知；合法完整前轮证据保留正例|
|历史|原index/hash/outcome/job/epoch不符、已封印：零改旧原件；合法关联持久读回可幂等|
|恢复|三参数Runner/停止入口仍查原payload；空解析/发布或settle失败不返回成功；active非终局|
|故障|查询三重epoch变化、readback失败、迟到受理冲突：责任保留、零重发|
|G4影响|已消费nonce不可重用；合法新轮须专用准备及前轮严格清偿，不普通写者补造|

先红与针对性回归，再串行 Rebuild -t:Rebuild -p:DeployToBgiTools=false、同DLL影响/全量及关键P/F/P源码恢复SHA、声明面。认证来源与统一独立综合实现后审仍欠，所有原级义务与真实入口/停止重启数据保留门保留。源码持续变更阶段不派读码子Agent，稳定后核全部累计预算再正式独立后审；不扩建工具。

当前基线 HEAD c4fe12c822f41ca8852c1c4eb5c202383163963c（产品3cac686），材料外R56/csproj/工具/文档、User/第三方JS/.kiro/旧D盘保持。普通TRX不冒充认证receipt；旧18失败须精确身份对照，不豁免。并行bundle36169fb核验成立，r61报告缺失仍未知。候选验证与恢复终态后明确文件 git commit --only，不push/发布/部署。

## 当前候选与未闭合判据

具体行为、全部过程失败、真正红例、九项P/F/P、最终1906/18/2去重结果与局限见 CURRENT-HANDOFF 最新原轮段及 candidate-observation.json。仍欠真实历史宿主/停止重启/全部约定功能与认证综合后审；原级义务不关闭。原 prepare 不限制首次规范授权seq为1，新轮续办/恢复必须保留完整前轮证明。原 manifest 工具 evidence audit blocked，不伪造通过。
