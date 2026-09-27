# 批次 21 施工计划（接线批）v1 — 2026-09-26 勘察定稿

## 当前执行状态（2026-09-27）

SB21-1 已完成生产接线、4/4 定向夹具、当前源码 11/11 反向突变及助手全量回归；全量 1479/2/0/1481，基线 1475/2/0/1477 是新增四个 Batch21 Facts 的逐名投影（无旧基线 TRX）。原预算 R1–R8 共 8 次，owner 批准 1 次 GPT R9 后累计 9 次。R9 快照的两项必改证据缺口已分别通过 11 组突变/恢复 TRX 与 R16 §24.63 U、锚点 blob、104/104 TRX 的本地机械核验补齐，原等级保留；owner 本轮指令据此关闭 SB21-1，不追加会诊，也不声称 R9 顾问复核或接受了后补证。R16 历史独立会诊仍无结果，作为 §24.63 U 所述后续处理债务保留。门禁继续关闭。细节见 R5.3 §24.121.4、暂停交接稿和 `_batch21/sb21-1-r8-review/`。提交只用显式 `git commit --only` 文件清单；材料外历史变更不入库。

## 目标
按 R5.3 §24.120 合同把本地等待/停驻/重评接进生产路径；核销 BO-1~13（接线批归属项）＋批次 14 四项残项＋EV1-R1＋C8 义务。

## 勘察结论（代码缝隙地图，均实测）

1. **生产组合根零接线**：`MainViewModel.BgiExternal.cs:37-43` 构造 TaskCenterHost 时**不传** `admissionWired`/`successorAdmissionWired`（默认 false）⇒ 生产 E1/E2 准入、后继改道、等待判定**全部休眠**。`CreateRunner`（TaskCenterHost.cs:982-997）双门缺一即直通。
2. **登记机制已就绪**：`WorkflowRunner.TryRegisterLocalWait`（:1429 起）含 C4① 全部分支（队列缺失/前置引用缺失/scope 非规范 ⇒ Park=true 零发送停驻）；注入口：构造参数 `localWaitQueue`/`localWaitPrerequisiteReferenceProvider`/`localWaitAdmissionScopeProvider`（:282-294）＋选项 `ShouldRegisterLocalWait`（:159，Wave3 实例级接缝，默认 null⇒false）。
3. **门面消费已闭环**：`MapAdmissionResultToBoundary`（TaskCenterHost.Admission.cs:1636）对 `WaitLocally` 单独分流为确定 Rejected（不 Uncertain、不重试窗、JobId 空）；登记翻译唯一定义点同文件 :1345（C3 身份翻译层）。
4. **批次 14 残项 #1 待解**：WaitLocally 经 Rejected 承载，Runner 侧若按拒绝推进游标即违约 ⇒ 接线必须让"停驻"语义在游标/终态层可区分（Wave3 已交付 LocalWaitParking 状态词与全链，接线时验证路径不被 Rejected 路径吞并）。
5. **BO-1 provider 异常面开放**：TryRegisterLocalWait 内 provider.Invoke / BuildAdmissionIdentity 越界异常收敛义务未闭合（代码注释 ①/①' 明载"provider 分支仍归 BO-1"）；Upsert 冲突分支已在本层折 Park=true。
6. **BO-2 面板 scope 注入源**：`_localWaitAdmissionScopeProvider` 生产无注入方；权威来源＝租约侧 FlowRegistration 反查（移交来源运行＝台账 AdmissionSourceScope 恒取，无需 provider）。
7. **EV1-R1**：`RunStore.List()` 对 JsonException 记录静默跳过（归占用者级别接线批，owner 裁决 a）。
8. **流程性挂账**：批次五十一 R16 验证会诊未取得（§24.63 U）⇒ 并入本批首轮会诊材料（已核实 09-24 后台账零提及）。

## 子批状态与划分

- **SB21-1 等待判定接线（收口完成）**：TaskCenterHost 装配 `LocalWaitQueueStore`＋`WaitDecisionSource` 并由 `CreateRunner` 注入；类型化 pre-intent Wait/Hold/Continue、身份绑定与队列/运行恢复规则按 owner 有限扩展实现。BO-1、BO-2、EV1-R1 与批次 14 残项 #1 有本批代码/测试证据。owner 按 R5.3 §24.121.4 依据本地可复核材料收口：R9 两项必改证据缺口已机械补齐，原等级保留；不声称顾问接受后补证，也不追加 SB21-1 会诊。R16 历史会诊缺失继续作为 §24.63 U 的后续处理债务。现有生产构造的后继门 `_successorAdmissionWired` 仍关闭，未开放等待消费路径或真实 User/R5.8 最终入口。
- **SB21-2 代际边界（实现、R5 复核与恢复态回归完成）**：只含 BO-10（Remove 后重登的 HWM 单调边界）和 BO-12（Remove/裁剪后不降 HWM、legacy int 隔离、long 回绕/ABA、C5 Store 消费前复核）。GPT 会诊独立台账累计 5/8，R5 未发现新 MUST/IMPORTANT，并认可 R4-1/2 重要级证据闭环；另 2 次本地预检未发送、不计次。助手项目和测试项目分别非增量构建 0 错误/58 警告、0 错误/79 警告；恢复态 generation 定向 45/45、LocalWait 190/190、助手全量 1506/2/0/1508。Remove/prune 重登使用同一重开 Store 读回持久状态，C5 旧/新请求消费计数为过期 1、有效 1；组件无 sender 依赖、无生产调用点，本批发送数为 0，生产门继续关闭。主矩阵 10 个保护点、legacy 强化、R5 C5 代际比较及 long-generation `long.TryParse` 边界均由命名断言反向突变检出并精确恢复。最新 testId 差集：共享 1497、移除 1、新增 11、变化 17、不变 1480，见 `sb21-2-review/assistant-full-test-diff-r5-counter.md/.json`。R5 后复验及声明面最终守卫证据见 R5.3 §24.122 与 `_batch21/sb21-2-review/`。生产入口、真实 User、R5.8 签署、E3/E4/E5 与热键面继续关闭；未做实机验证。
- **SB21-3 facade 映射**：BO-4（facade WaitLocally 映射合同＋消费方识别运行验证）。
- **SB21-4 停驻出口**：BO-13（停驻运行会话内终局处置出口）＋BO-11（Wave3 冻结残项打包：BO-6/7/8/9 处置或显式登记）。
- 横向：EV1-R1（RunStore.List 静默跳过修复）随 SB21-1 同批（占用者级别事实源的同族面）。

## 开工纪律
反例先行（每子批先红夹具）；会诊刹车（≤8 轮、严重度地板）；提交 --only；声明面变更再生＋无 env 复跑；每子批收尾刷新接力文件。
