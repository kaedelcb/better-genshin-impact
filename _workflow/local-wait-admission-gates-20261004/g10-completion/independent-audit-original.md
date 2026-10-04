结论：**blocked，保留全部原级 open**。这是当前源码审查与修复方案，不是前审补签、认证 receipt、测试通过或产品验收。允许按交付优先政策继续定向修复；G4/G7 仍阻断节点准入生产开门。

核对的 HEAD：`436028e3a0beef2b4bbdc12e3c93fcf7631cd351`。审查期间主执行者修改 G10，三个文件发生漂移：

| 文件 | 首次读取 SHA256 | 末次读取 SHA256 |
|---|---|---|
| `TaskCenterHost.Admission.cs` | `1FC9941380483727FFD29EFD6332E580B74448EC291186BC19FD036A7E26E0B4` | `CF6900C90FBF0122538AE9E739A8AD7A9481386EBA21983FCA7C7F921E5AAF69` |
| `ArbitrationAdmissionService.cs` | `D179B3C5C2C8486D90E21CE5F3AD59FF6FBF97CE4F966695F498DE4DD8450D2B` | `2BA09FF8713DE77CFEBFB9461DA292B21C5CEF898675D405DB76F650D81242CC` |
| `ArbitrationModels.cs` | `F628F0C566F307D190C0D7FCF43A0B116AD3B13528DF4FE40009B2B4BD4C404E` | `E890395018F245219083ADCDE887AAF72BEA757110D0967338A79756EE76C379` |

下述 G4/G7 根因所在 `BgiWorkflowExecutionBoundary.cs`、RunStore、Runner 与查询模型读取前后未变。材料外修改保持只读；未写文件、构建、运行测试或操作 Goal。

**重要发现与精确修复方向**

1. **G4-residual：W2 尚未覆盖冻结 CAS 窗口。**

   [TaskCenterHost.Admission.cs:2421](</E:/Program Files/better-genshin-impact-LCB/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs:2421>) 检查的是此前 Load 得到的 Cursor。随后 [BgiWorkflowExecutionBoundary.cs:283](</E:/Program Files/better-genshin-impact-LCB/MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs:283>) 的 `UpdateMergingIf` 只验证提交身份与停止权威，未验证 `latest.Cursor`；发送前亦未核验 Cursor。

   反例：宿主检查通过 → 并发写者推进 Cursor、保留提交键/attempt → 准备 CAS 成功 → 旧节点进入端口发送。现 W2 夹具把变化放在宿主检查之前，不能排除此窗口。

   修复：把冻结 Cursor 身份与逻辑消费代次纳入准备 CAS；同时规定冻结许可存续期间谁能推进游标，或在许可消费处实施同一权威检查。单纯再 Load 一次仍会留下窗口。新增“检查通过后、冻结 CAS 前”屏障反例，断言端口调用为零、并发 Cursor 未覆盖、责任保守保留。

2. **G4-residual：RecordRevision 仍充当逻辑消费代次。**

   `TaskCenterHost.Admission.cs:1446/1456` 仍使用记录修订；门面唯一消费比较仍要求 `other.CursorRevision == request.CursorRevision`。任何无关记录更新都能改变该值，因此 W2 的字段比较没有解决唯一消费身份问题。

   修复：明确采用持久逻辑游标代次，或经合同确认采用稳定的出现身份作为消费键；记录修订继续只用于记录版本校验。必须覆盖“游标未变、Note/停止观察等记录变化、不同请求身份重新准入”反例，以及迁区、重启后的再次消费。不得通过放宽重试资格解决。

3. **G7-residual：当前恢复命中没有补齐发送身份，也未完成门面结清。**

   [BgiWorkflowExecutionBoundary.cs:496](</E:/Program Files/better-genshin-impact-LCB/MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs:496>) 查询并核验原 epoch、key、run/node/occurrence/loop/attempt，CAS 只写 `Intent/JobId`，不写 `AcceptedSendIdentity`。当前停止对账调用此边界；节点恢复链未把该结果转换为 `SettleReconciledAsync` 的原轮次结算。

   因而“远端受理 → Sender 尚未落盘即崩溃”后，RunStore 可以取得 jobId，但严格 `TakeoverPersist` 仍拒绝缺完整发送身份。保守停驻方向正确，闭环尚缺。

4. **G7/G8：补当前提交不足以修复历史结果，普通写入不能改历史。**

   [RunStoreEvidenceGuard.cs](</E:/Program Files/better-genshin-impact-LCB/MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs>) 对既有 `SubmissionHistory` 要求序列化字节等价，对运行/节点封印要求事实哈希保持。`NodeHash` 又要求按完整发送身份定位提交及 outcome。

   反例：历史提交/outcome 缺身份，事后仅补 `CurrentSubmission.AcceptedSendIdentity`，旧节点仍无法形成自己的有效封印。直接修改历史会被现守卫拒绝；放宽守卫会破坏原历史与封印保护。

5. **G10-residual：dc25a5e38 只完成部分链路。**

   该提交发送拒绝分支新增明细，但准备拒绝、异常、未知耐久诊断、镜像复制与续用回读未全部贯通。末次读取已看到主执行者补充准备拒绝、`CloneOperationResult`、拒绝回读及诊断专用 `SendUncertainty`；这些是当前候选修复，不能据此关闭义务。

   仍见准备异常日志后抛出，门面发送 catch 仅保留异常类型，`ex.Message` 未进入结构化诊断。应保留类型、原明细、证据来源，并绑定原 `submissionIdentity+sendSeq`，同时保持 Unknown/责任 Pending；诊断字段不得参与受理、拒绝或释放判定。

**G7 可执行安全方案**

1. 增加宿主内部“原节点责任对账”入口。先从权威 Operations、当前 Submission、PreObservation 获取原请求身份、完整 `submissionIdentity+sendSeq`、原目标 epoch、WireSubmitKey、RunBinding、候选四段出现身份及持久冻结依据。缺失、冲突、重复映射立即保守停驻；不重新签发发送许可。
2. 按原 epoch 查询真实 BGI。查询前连接 epoch、响应 epoch、查询后 epoch 必须一致；同 key 必须唯一命中，run/node/occurrence/loop/attempt 必须完整匹配，并验证 taskId/configRevision及实际载荷证据。不得从当前流程定义重建“原载荷”。
3. 当前查询接口尚不能证明完整载荷：BGI `SerializeJob` 与 `BgiJobInfo` 没有完整请求或请求指纹；本地准入指纹与线上载荷指纹还使用不同算法。缺证据时保持 Unknown。需要合法原接纳回执，或明确允许的附加查询投影；不能把本地指纹自述当远端载荷证据。
4. 查询同一 job 的实际原始终态、退出确认、退出处置与效果。仅命中 active job 可以确认接管，不能证明终局清偿；not_found、取消 RPC 成功、超时均不是退出证据。
5. 用专用 RunStore 操作一次持久化完整关联及合法增强事实，并读回验证。当前无封印提交可以同 CAS 补齐缺失身份及精确对应结果；已有不可变历史应保留原件，追加恢复关联记录，绑定原历史 hash/index、原发送身份及证据。让终态判据读取这一关联，不宽化普通历史写入权限。
6. RunStore 读回成立后，调用门面 `SettleReconciledAsync`，沿用原身份/原序号；门面仍调用现有严格 `TakeoverPersist`。随后由既有节点/运行封印路径结清终局责任。跨文件期间崩溃可幂等续办，不能因 RunStore 已写就直接删门面责任。
7. 对同 key 多 sendSeq，必须唯一证明命中属于当前未决轮次，并保留前轮拒绝/未发送依据；存在矛盾受理、另一 `AcceptedSendIdentity` 或无法唯一关联时保留冲突，禁止补记和重发。

关键反例至少包括：受理后落盘前崩溃；补记后关闭前崩溃；原 epoch 改变；同 key 多命中；错 occurrence/attempt/taskId/configRevision/载荷；不同 jobId；旧 sendSeq 迟到；历史 outcome 缺身份；已封印记录；受理仍 active；终态存在但 exit 未确认；落盘失败与取消同时到达。

**现有实现应复用，不能按旧文档推断缺失**

- 已有 `OperationRecord.ParentRequestIdentity`，节点登记时原子解析并写入。宿主当前来源解析按唯一 E1 来源判定，并非旧注释的“最早更新时间”。W3 应补受理时固定、可区分 E1/Handoff 的精确来源关联及删除/歧义处理，不能从零重复添加父字段。
- `NodeSubmit_33NodeFlow_NoCapacityExhaustion` 已存在且为普通 `[Fact]`。其断言覆盖 33 次发送及唯一操作身份；源码仍明确“逐次 AdmissionResult 容量原因码取证欠缺”。原 G8/交错⑤应按当前版本证据补审，不能继续写成“无夹具”。
- `SweepTerminalNodeOperations → MarkOperationTerminal → _gate.Wait()` 同步等待仍在。必须证明调用与回调不会持门面锁反向等待 Runner，或改成异步终局入口；本次没有运行证据，不能断言已死锁或已排除。

原账本逐项处置：`G2(e)`、`G4-residual`、`G4a-residual`、`G7-residual`、`G8-residual`、`G10-residual`、`interleaving-5`、`interleaving-6` 均 **important / implementation / retained_open**。前四类交错已有相关夹具源码，但本次未执行，不能重新认证；⑤需逐次原因码与当前容量证据，⑥需四类真实入口、停止/重启与数据保留证据。

技术上可自行决定：诊断字段、证据绑定载体、CAS 位置、严格关联与故障反例。需先解决的范围问题是“完整远端载荷证据”与当前 plan“不改 BGI 外部接口协议”的冲突；这只阻断依赖完整证据的 G7 放行，不阻止框架、反例和其它定向修复。

<oai-mem-citation>
<citation_entries>
MEMORY.md:300-301|note=[historical local wait navigation followed by current source audit]
MEMORY.md:353-355|note=[historical admission contract context only]
</citation_entries>
<rollout_ids>
01a0d227-6b21-7ad2-b587-679bc7f1bdcc
01a0d37f-dc49-7781-bcde-ae9e072f2cad
</rollout_ids>
</oai-mem-citation>