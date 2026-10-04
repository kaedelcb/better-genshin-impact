# 类型化父来源待实施细化（不授予许可/不记闭合）

同共享包的既有明确定向修复，承接 G4a/G2(e) 与交错责任；不重开预算/前审。当前源码仍为旧二元组/Any/合成 run-source，本文仅设计。

受理点保留两种权威：PanelFlowRegistration 来自同一租约事务的唯一 FlowRegistration；StartupHandoff 来自 CreateRun 与原首个 HandoffIdentity 同次持久发布。移交增加 nullable、旧空值省略的类型化原来源，固定版本、run/workflow/完整 Scope、原 intentKey/executionId/stepId/triggerKind/mode。空可选执行/步骤字段保持原合同，不自行新增业务必填条件。旧记录只有 Scope 和 Handoffs 不回填来源；恢复/幂等附加绑定不改变首来源。

RunStore 所有普通/合并写者保持原类型化来源和 Scope 逐字相等，原 HandoffIdentity 逐项保留；旧缺来源 null 不能由普通写者补造。实际原绑定被删/重复/改 mode/内容不一致、run/workflow 不符、未知版本/畸形 Scope 全部拒绝。出现面板父与移交原来源冲突不得择一。不得凭 List.First/Any、裸 runId 或当前流程签发。

宿主来源解析返回 Kind 与精确原引用；本地等待按 Kind 分派而不是识别 run-source 字符串，既有队列 SourceIdentity 的运行引用不能独立成为授权。节点提交将预期原来源随不可变上下文带入；门面登记在同一 Lease mutation 中核面板唯一父或读取不可变移交原来源，再把类型化 ParentSource 与 ParentRequestIdentity 原子绑定。移交不另造租约父操作；其父引用绑定真实原 intentKey 与完整原 HandoffIdentity，而非合成 run-source。

门面读取移交原来源的 hook 仅读 RunStore，不回读 Lease、不 await、不向外部端口调用。占位/重驱/重试在原锁内复核当前权威与已存 ParentSource/Scope；删除、歧义、变化或旧 successor 缺字段都不能新发送。自有占用豁免对两类来源均核实际原绑定及无全局未决/其他在飞节点；不能把 caller 自报/当前 run 当授权。通用组件夹具的手工节点不代表产品路径；生产后继 Namespace=successor 必须具备此来源链。

红例先行：旧裸 Scope+列表、错 run 别名、普通 Scope 重写、删除/替换原绑定，随后增加受理/重开/多绑定/冲突/登记后删除/原子父绑定的真实 Host/RunStore/LeaseStore 反例与正例。更新旧夹具只补其明确模拟的受理输入，不改旧核心预期、不把无法恢复的旧记录改当现代凭据。关键来源守卫 P/F/P，Rebuild/影响与完整 testId 对照，稳定共享链后统一认证和综合后审；生产门及总交付判据保持。
