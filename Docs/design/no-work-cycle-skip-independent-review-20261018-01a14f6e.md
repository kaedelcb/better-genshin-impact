独立只读方案审核结论：**需修订后可实施。可以修正周期跳过误报，同时保留现有联机接管、停止、恢复和防空跑保护；没有理由删除 `no_work` 或改执行权机制。**

审核基线：`main-OldTeaBag-B180`，实际 HEAD `e6bce62bba2207a23b2cffe15e1958df92635bcb`。开始时相关 tracked 工作区干净；结束时另一写者修改了 `BetterGenshinImpact.csproj`，本次审查相关源码无差异。我未写文件、未操作 Goal、未运行产品或真实 User。已按独立审查技能和项目审查流程核查；历史门沿用主端结果，没有扫描旧根。

### 原问题确认

日志 `D:\DOWN\better-genshin-impact20261014.log:1626-1641` 明确记录：C07 当前周期 `2 != 3` 正常跳过，随后配置组因零完成被登记为 `Failed`。

当前 [ScriptService.cs](/E:/Program%20Files/better-genshin-impact-LCB-source/BetterGenshinImpact/Service/ScriptService.cs:503) 的零完成判断没有区分“全部正常周期跳过”和“没有可解释的实际执行”。`d3af610cca` 首次引入该计数保护，原审计文档第 80 行也允许“明确合法跳过”，并要求空配置继续失败。这是判定缺口，不是取消整个保护的依据。已核公版 `origin-lcb/main=b3f90e70494320639eeccc8c559612d02d2d9ef0`；周期配置类无本地差异。

### 实施前必须落实的修订

1. **周期跳过必须有专门原因，不能复用 `ShouldSkipTask()==true`。**

   [ScriptService.cs:63](/E:/Program%20Files/better-genshin-impact-LCB-source/BetterGenshinImpact/Service/ScriptService.cs:63) 的同一布尔值还包括禁止时段、日上限、执行记录以及 `PathingTask` 构造为空（102-105 行）。其中只有 87-90 行能直接证明本次要求的周期不匹配。

   最小修订是保留原有跳过行为，增加可判别的周期原因；零完成转 `Skipped` 只能消费该明确原因。无效周期参数仍按现有规则继续尝试执行，不能分类成正常跳过。

2. **先定义本次有效待执行集合，不能用“周期跳过数大于零”代替完整证明。**

   当前顺序是恢复游标（319-327 行）→ `SkipFlag`（329-332 行）→ `ShouldSkipTask` → 禁用判断（341-345 行）。因此，禁用项目也可能先产生周期跳过日志。

   判定应基于本次恢复起点之后、实际启用且有正执行次数的项目，要求集合非空，并且全部都有周期跳过依据。恢复前缀、禁用项不能冒领周期原因；全禁用、空组、无有效执行次数、未知类型被重载过滤等情况不能因旁边有周期跳过而获准正常结束。优先插入项目也必须纳入本次实际结果，真实失败不能被普通组的周期跳过覆盖。

   `ReloadScriptProjects`（586-624 行）会忽略未知类型，计数覆盖范围需要显式核对；不能只以重载后的非空列表推定原请求已全部解释。

3. **跳过必须由本次 TaskRunner 的叶结果消费，不能借根状态或共享通道推定。**

   [TaskRunner.cs:228](/E:/Program%20Files/better-genshin-impact-LCB-source/BetterGenshinImpact/GameTask/TaskRunner.cs:228) 目前读取环境中的 `OneDragonItemResultChannel`，在终态前决定 `JobState.Skipped`。仅在 `RunMulti` 返回前改成 `Skipped`，注册表已经写完，无法修正终态。

   应为本次组执行提供明确、隔离的结果来源，并在零完成且全部周期跳过成立后上报。不要令 TaskRunner 根据 `scope.Result==Skipped` 判定当前叶子跳过：根作用域会记住前面子项的跳过，随后真实执行的组或收尾可能被一起标成跳过。若复用现有通道，也要防止组内子任务的上报替整个组作结论。

   失败、取消、失去所有权及清理失败继续走现有终态优先级。本次不宜顺手调整两层既有优先级：`ExecutionScope.Observe` 和 TaskRunner 的终态表达式并非完全相同的排序。

4. **IPC 必须保留跳过身份；不能把 `Skipped` 并入布尔 `false`。这是当前方案最具体的跨端风险。**

   [InstanceRequestHandler.cs:1014](/E:/Program%20Files/better-genshin-impact-LCB-source/BetterGenshinImpact/Service/Instance/MessageHandlers/InstanceRequestHandler.cs:1014) 只有 `Ran` 返回 `false`，取消返回 `true`。如果为消除失败直接把 `Skipped` 也返回 `false`：

   - `BgiTaskCoordinator.cs:712-729` 会登记 `completed`，并可能调用 `RegisterExecuted`；
   - v2 handler 的 861-862 行会登记“已执行”并返回 `started`；
   - `CommandExecutor.cs:1672-1673` 会把普通队列 `completed` 映射为成功；v2 分支 1072-1086 行也只特殊识别取消。

   最小语义修订是让内部执行结果保留 `TaskRunResult` 或等价明确载体，分别映射 `Ran / Skipped / Cancelled / Failed / Preempted`。跳过不得登记成“实际已执行”，事件与轮询必须表达同一结果。助手对应消费面须明确识别正常跳过；未支持该结果的消费面保持保守，不能默认成功。只增加一个可被忽略的 `skipped` 字段也不够。

   **新版联机并非必然因此误恢复。** 当前 `MainViewModel.CoordinatedBatch.cs:79` 开启实际证据检查；434-438 行配合 `CoordinatedHoeingEvidence.cs:38` 会拒绝 `completed + JobResult=Skipped`。因此直接折叠的当前可达后果首先是证据不匹配、停驻，不能宣称已经证明会触发错误恢复。这个既有保护应保留，正确修复是让跳过终态与证据相符。

### 可以保持的现有规则

- [ExecutionScope.cs:206](/E:/Program%20Files/better-genshin-impact-LCB-source/BetterGenshinImpact/Service/Execution/ExecutionScope.cs:206) 的所有权、停止版本、取消与让位检查，以及接管票据、恢复身份、退出证据，不需要为此次修复改变。
- `OneDragonRootResultRule.cs:13-20` 已允许合法跳过后继续普通条目和收尾，整龙尾部完成后根结果为 `Ran`。这是现有整龙合同，可保持；不能把根成功解释成每个叶子都实际执行。
- `ScriptControlViewModel.StartGroups` 的 2998 行目前只接受 `Ran`，应允许明确 `Skipped` 继续后续组；`ScriptService.cs:536-542` 也要为正常跳过提供独立收尾文案。
- **不要给所有联机绑定新增 `Completed>0` 要求。** 绑定允许普通组和整龙。原生锄地 `SoloTask` 在 `ShouldSkipTask` 的 65-68 行就返回“不跳过”，不会被此次周期分类直接放行成未执行的原生锄地完成。没有证据要求扩大修改原生完成判据。
- 未找到 `BatchReconcileDecider` 的生产调用，不能据其定义或测试断言新版现网会误恢复。

### 必需的定向验证

以下验证足以判断本修复，不要求开启整版审查：

- 非空有效项目全部周期不匹配：执行次数为零，TaskRunner 返回及注册表均为 `Skipped`，不产生 `no_work`；混合实际完成与周期跳过仍按原成功结果。
- 空组、全禁用、无有效执行次数、未知类型、非周期跳过不能借周期计数获准；恢复前缀不参与本次待执行计数，恢复后的有效项目全周期跳过可以正确分类。
- 周期跳过与真实失败、取消、让位、清理失败组合时保持既有结果；跳过后下一条真实执行、下一组和一条龙收尾不被通道污染。
- 本地连续组及一条龙能继续；叶子保留跳过身份，整龙按既有尾部完成规则汇总。
- IPC 的事件、轮询、v2 返回和助手消费一致识别跳过；不登记成实际执行、不生成原生完成计数、不触发以实际锄地成功为条件的动作。新版联机证据检查保持开启，正常跳过不能落入证据不匹配等待。
- 复用并运行受影响的停止、接管、恢复定向用例，确认未修改原请求身份、停止版本、票据及恢复位置判断。

当前仍缺的是修订后的实际差异，以及上述定向运行结果。此次静态审查不能确认补丁已修好，也不能代替隔离入口验证。

**人话结论：能保留原功能修好。正确做法是补上“本次全部明确周期跳过”的独立结果，并把它传到真正消费终态的地方；不能删除空跑保护，也不能把“正常没运行”改写成“已经执行成功”。无需用户操作。**

<oai-mem-citation>
<citation_entries>
MEMORY.md:930-941|note=[用于定位并重新核对接管失败与未知结果保护边界]
</citation_entries>
<rollout_ids>
01a133a2-6c3f-7462-850c-491ca64e2e28
</rollout_ids>
</oai-mem-citation>