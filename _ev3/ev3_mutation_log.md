ev3 反向突变日志（R5 判别力自检；目标 JobRegistry.cs（M1/M2）＋ExecutionScope.cs（M3）；
施加方式＝Read/Edit 工具逐行编辑，无 heredoc；每编辑前已读文件）

环境：dotnet test Test/BetterGenshinImpact.UnitTest -p:DeployToBgiTools=false
     --filter "FullyQualifiedName~JobTreeEvictionAndSampling"（定向 3 例）
编号对照（FIX-n → 测试方法，与夹具文件头注一致）：
FIX-1＝JobRegistry_TerminalCapacityEviction_IsFifo_KeepsNewest64；
FIX-2＝ExitReceipt_RealCapacityEvictionOfTerminalAncestor_IsUnknownNotZero；
FIX-3＝JobTreeSnapshot_UnderConcurrentSubmitAndTerminalChurn_HoldsSamplingInvariants。
口径：每组突变＝apply（单锚点编辑）→ 重建跑定向（红帧留档）→ revert →
     对被突文件 `git update-index --refresh` 后 `git diff` 0 差异核验
     （M1/M2 对 JobRegistry.cs、M3 对 ExecutionScope.cs，三者均 0 差异）；
     三组全部还原后统一定向终帧复绿（_ev3_targeted_green_final.trx，3/3），
     并跑 TaskTakeoverIncident 集合级定向（_ev3_collection_green.trx，59/59）。
     施突前核验：两生产文件 git status 干净（porcelain 无输出）。

| 组 | 突变内容（生产锚点，单行/单块） | 预期守护 | 实际红帧 | 归因核对 |
|---|---|---|---|---|
| M1 | JobRegistry.cs `TryMarkTerminal` 淘汰循环 `while (_terminalOrder.Count > TerminalCapacity)` → `> int.MaxValue`（终态 FIFO 容量淘汰永不触发） | FIX-1 容量守卫＋FIFO；FIX-2 真实淘汰前提；FIX-3 容量不变量 | 3 败/0 过（_ev3_mutM1_red.trx）：FIX-1（Assert.Null fillers[0] 失败＝未被淘汰）、FIX-2（Assert.Null Query(A) 失败）、FIX-3（Assert.Empty errors 失败＝终态数超容量） | ✅ 全部为容量/淘汰依赖断言红；方向与预期一致 |
| M2 | JobRegistry.cs `JobTreeSnapshot` 锁内复制拆除（`lock (_gate)` 块移除，无锁枚举可变字典） | FIX-3 采样不变量（并发枚举异常通道） | 1 败/2 过（_ev3_mutM2_red.trx）：FIX-3 | ✅ 唯一红＝并发采样夹具；FIX-1/FIX-2 无并发，保持绿（与预期一致，说明 FIX-3 的红来自并发通道而非容量通道） |
| M3 | ExecutionScope.cs `CollectOutstandingRegisteredDescendants` 链完整性早退 `if (!present.Contains(parent)) return (false, ...)` → `continue`（父缺失不再降级为不可判定） | FIX-2 的 null（不可判定）断言 | 1 败/2 过（_ev3_mutM3_red.trx）：FIX-2 | ✅ 唯一红＝真实淘汰链断夹具（AtExit 被报为 0 而非 null）；FIX-1 与 FIX-3 不经过该分支，保持绿 |

**第 1 轮会诊处置后的形态重做（2026-09-26，ev2「当轮夹具形态重做」口径）**：
会诊第 1 轮重要项 #2 处置给三组夹具补 try/finally 兜底、FIX-3 增 Thread.Yield 应力与 mineAll 登记、
头注补 M1-M3 守护映射（断言语义零变化）。突变按新形态全部重做：
M1＝3 败/0 过（_ev3_mutM1_red2.trx）、M2＝1 败/2 过（_ev3_mutM2_red2.trx）、
M3＝1 败/2 过（_ev3_mutM3_red2.trx）——红面与第一轮形态完全一致；
还原后 `git update-index --refresh`＋git diff 0 差异双核验（JR=0/ES=0），
终帧复绿 _ev3_targeted_green_final2.trx（3/3），全量帧 _ev3_bgi_full_final2.trx
（1048 过/14 败/1062 总，与基线逐名差集空，见 ev3_full_diff_baseline.md）。

如实边界（R3/R5）：
- FIX-3 的"快照内重复 JobId"断言无对应突变（Dictionary<Guid,…> 结构上不可能产生重复键），
  如实登记为未突变验证的廉价不变量，不主张其判别力。
- FIX-3 的"终局不可逆"断言在 M2 下未触发独立红信号（状态单调性使无锁读也不产生
  终态回退）——其红信号经由"快照抛异常"通道（M2 实测红）；该断言对"状态寄存器回写"
  类假想突变有判别力但本批未构造该突变，如实登记为未突变验证。
- FIX-2 的 StillOpenNow 不可判定断言在 M3 下未获得独立红信号（AtExit 断言在前先抛，
  循环短路）——其稳态判别力平凡成立（生产报 0 则 Assert.Null 必红），独立突变验证
  未构造，如实登记（会诊第 2 轮建议 #2② 补记）。
- FIX-1 的"#2..#65 在场且 IsTerminal=true"循环断言在 M1 下红信号被首个
  Assert.Null(fillers[0]) 截获，循环体未独立执行——判别力未突变验证（性质平凡：
  Query 非 null 即红），如实登记（会诊第 2 轮建议 #3 补记）。
- FIX-2 的两条 ContainsKey 断言（会诊第 2 轮建议 #2① 采纳后新增）**实测反例成立后撤回**：
  采纳即跑定向（_ev3_targeted_green_final3.trx 首帧）＝Assert.True(ContainsKey) 红——
  实证该投影经 InstanceIpcProtocol.cs:132 的 NullValueHandling.Ignore 序列化，null 字段
  整条不上线，「键缺失」即不可判定的线上形态，建议前提（键存在但值为 null 的形态）在
  本生产形状中不存在；原 Assert.Null 形态恢复为绿（_ev3_targeted_green_final3.trx 重跑帧）。
  该建议守护的真实漂移形态是「报 0 代替 null」，已由 M3 突变证红。反例尝试记录如上（R3）。
- 初版夹具缺陷（跨读者共享已见终态集的假阳性）见 ev3_objective.txt 末节，
  红帧 _ev3_targeted_green1.trx 留档，修正后 6 连绿（green2..green7）。

复绿口径：逐突变组 revert 后 git diff 0 差异；终帧＝_ev3_targeted_green_final.trx
（3/3，规则式引用帧名）；全量帧 _ev3_bgi_full_final.trx（1048/14/1062，差集空，
见 ev3_full_diff_baseline.md）。
