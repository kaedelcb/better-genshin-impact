ev2 反向突变日志（R5 判别力自检；脚本 _ev2/_apply_mutation.py，目标
BgiTaskCoordinator.cs（M1-M5/M7）＋JobRegistry.cs（M6））

环境：dotnet test Test/BetterGenshinImpact.UnitTest -p:DeployToBgiTools=false
     --filter "FullyQualifiedName~TerminalSplitCharacterization"（定向 10 例）
编号对照（FIX-n → 测试方法，与夹具文件头注一致）：
FIX-1＝Split_…PreWritesFailure…（Theory 两例）；FIX-2＝Split_…PreWritesRejected_ReturnsTrue…；
FIX-3＝SameSource_…NoPreWrittenTerminal…；FIX-4＝SameSource_…PreWritesSucceeded…；
FIX-5＝SameSource_…ExecutorCancelled…；FIX-6＝Fallback_ExecutorThrows…；
FIX-7＝Fallback_QueueCancelWhilePending…；FIX-8＝Fallback_SlotWaitTimeout…；
FIX-9＝Fallback_QueueFull…。
口径：每组突变＝apply（脚本断言模式唯一）→ 重建跑定向（红帧留档）→ revert →
     对被突文件（BgiTaskCoordinator.cs／JobRegistry.cs，按脚本 TARGETS 表）各自
     `git diff` 0 差异＋git status 干净双核验（第 7 轮建议 2：M6 目标为 JobRegistry.cs，
     原口径只点名协调器文件属空真，已修正）。
     复绿口径＝逐突变组以 git diff 0 差异保证实现回到原形后，统一定向终帧复绿
     （终帧＝_build_evidence.py FRAMES 中标注「终帧」的定向绿帧，**当前＝green6**；
     按规则引用而非硬编码帧名，避免帧演进时散落指针漏改——第 6 轮建议 2）。
     不逐组单跑复绿帧（如实登记，非 16 帧口径）。
     〔更正史〕本句原写 green2（第 4 轮建议 4 更正为 green5），第 5 轮帧演进后 green5
     又漏改——两次复发后改为上述规则式引用。
     初版 M1 曾以 --no-build 跑过一次（用的是未突变旧程序集，显示 10/10 绿）——该帧无效已弃用，
     重跑一律带重建（此教训与本行如实登记）。
     〔第 2 轮前更正〕M1 行原写「预期守护：分裂/同源四夹具」，实际红帧 6 例——「四」为笔误已更正；
     编号对照表原缺失已补（第 1 轮建议 4 处置）。

| 组 | 突变内容（生产锚点，单行） | 预期守护 | 实际红帧 | 归因核对 |
|---|---|---|---|---|
| M1 | :652 `RecordTerminal(item.TaskHandle, "completed", cancelled: cancelled);` → `"failed"`（普通路径队列状态改写） | 分裂/同源五夹具的队列侧 completed 断言 | 6 败/4 过（_ev2_mutM1_red.trx）：FIX-1×2（Failed/Rejected 两例）、FIX-2、FIX-3、FIX-4、FIX-5 | ✅ 全部为队列侧 completed 断言红；注册表侧断言不红（M1 不触碰注册表路径） |
| M2 | :271 `TryRegistryTerminal(taskHandle, state, code, message, cancelled);` → `_ = (state, code);`（注册表兜底写入拆除＝Running 僵尸） | 兜底面与同源面的注册表侧断言 | 5 败/5 过（_ev2_mutM2_red.trx）：FIX-6、FIX-7、FIX-8、FIX-3、FIX-5 | ✅ 关键方向验证：FIX-1×2 与 FIX-2 保持绿——执行体先终态已落注册表，协调器兜底拆除不影响其断言（先终态者赢方向正确性的对照证据） |
| M3 | :267 `"completed" => cancelled ? (Cancelled,cancelled_user) : (Succeeded,null)` → `(Succeeded,null)`（丢弃取消区分） | FIX-5 注册表 Cancelled 映射 | 1 败/9 过（_ev2_mutM3_red.trx）：FIX-5 | ✅ 唯一红＝取消映射夹具 |
| M4 | :269 `_ => (Cancelled, cancelled_user)` → `(Succeeded, null)`（queueCancelled 兜底改写为成功） | FIX-7 注册表 queueCancelled 兜底 | 1 败/9 过（_ev2_mutM4_red.trx）：FIX-7 | ✅ 唯一红＝排队取消兜底夹具 |
| M5 | :268 `"failed" => (Failed, errorCode ?? task_start_failed)` → `(Failed, task_start_failed)`（丢弃真实错误码） | FIX-8 注册表 task_busy 归因 | 1 败/9 过（_ev2_mutM5_red.trx）：FIX-8 | ✅ 唯一红＝等槽超时夹具（其注册表错误码归因 task_busy 被抹平） |
| M6 | JobRegistry.cs `TryMarkTerminal` 摘除已终态拒绝守卫（`‖ job.IsTerminal`，第 4 轮建议 5 采纳＝方向反转突变） | FIX-1×2 与 FIX-2 的注册表侧断言（直接守护先终态者赢方向） | 3 败/7 过（_ev2_mutM6_red.trx）：FIX-1×2、FIX-2 | ✅ 覆盖可被写 ⇒ 执行体终态被协调器映射值改写（FIX-1 两例被 Succeeded 覆盖、FIX-2 被 Cancelled 覆盖）⇒ 注册表侧断言红；其余夹具（无先写终态，方向不敏感）保持绿。M6 对 JobRegistryTests 的波及已实跑佐证（见下行佐证帧），不再是无尝试记录的推断 |
| M7 | :397-398 Submit 队列满路径注册表写入拆除（TryRegistrySubmitQueued＋TryRegistryTerminal(Rejected,queue_full)；第 5 轮建议 2 采纳） | FIX-9（队列满 ⇒ Rejected(queue_full) 只存在于注册表） | 1 败/9 过（_ev2_mutM7_red.trx）：FIX-9 | ✅ 唯一红＝队列满夹具。如实登记：同款调用的 Channel 物理满兜底路径（:407-408，极端竞态、夹具不可确定构造）不在本突变与夹具范围，属未钉边界 |

**M6 波及佐证帧（第 5 轮建议 4：把「JobRegistryTests 会红」从静态推断升级为实跑留档）**：
M6 下实跑 `--filter FullyQualifiedName~JobRegistryTests`＝7 总/**2 红**/5 过
（_ev2_mutM6_jobregistry_red.trx）。两个红的逐例因果（第 6 轮建议 4 补）：
① `TryMarkTerminal_FirstWriterWins_SecondMarkIsNoop`——夹具主体即先终态者赢合同，
二次 TryMarkTerminal 被守卫拒绝返回 false；M6 摘除守卫后二次写成功 ⇒ 断言红。
② `Submit_WithParentJobId_ChildrenLinkToDragonParent`——主体是父子链合同，但尾部
两行显式断言同一合同（`Assert.True(父首次终态)`＋`Assert.False(父二次终态写)`）；
M6 下二次写返回 true ⇒ 尾部 Assert.False 红。两例均为先终态者赢合同断言，
非环境性/无关失败。

**FIX-4 注册表侧断言的覆盖边界（如实）**：FIX-4（先写 Succeeded 保留）的注册表侧值
与协调器映射值相同（Succeeded），任何「可覆盖」突变（M6）下被覆盖为同值 ⇒ 断言仍绿，
不可与 FIX-1×2 区分；其方向安全由 M6（FIX-1×2 红）间接守护，值等价性不可判别属
结构性边界，如实登记为未突变验证。

还原核验：每组 revert 后 `git diff` 0 差异（M1 施突/还原各一次的 racy-stat 假 M 经
update-index --refresh＋git diff 0 差异双重核验为内容一致，同 ev1 第 1 轮口径）。

终帧演进（如实）：green2（第 1 轮突变还原后）→ green3（第 1 轮加固后）→
green4（第 2 轮修复后）→ green5（第 3 轮修复后）→ **green6（第 5 轮修复后终帧，10/10）**；
突变红帧按「当轮修复后夹具形态」多次重做（M1-M5 红数 6/5/1/1/1 各轮一致、M6 红 3、
M7 红 1＝第 5 轮终形态）。
全量：final2（第 1 轮加固后）→ final3（第 2 轮修复后）→ final4（第 3/4 轮形态）→
**final5（最终帧）**，各帧均 1059/1045/14 且失败身份与 r58_bgi_full_20260924.trx 既有
14 项基线逐名差集为空（新增 0/消失 0）；最终差集核验产物 _ev2/ev2_full_diff_baseline.md
（指向 final5）；本批 10 例全部入帧。帧身份标签以 _build_evidence.py 显式映射为准。
