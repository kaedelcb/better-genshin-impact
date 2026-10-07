# 等待后到达边界的修订接续修复候选

继承原完整Goal与OWN-ROOT-NONGAME-COLD-INSERT-20261008-FROM-01a1176e；本线程01a11808-ac6c-7132-8e5d-391a499678cf，实际gpt-6.1-sol/ultra，main-OldTeaBag-B168，开工HEAD b3aab71f428b12fd990e380c1b6d060d0e32b705。采用mistletoe-release-first-20261005-v2、mistletoe-complete-usable-delivery-20261006-v1和mistletoe-storage-limits-20261005-v1；仍属原完整产品批次，不另立Goal/opening或重置任何审查预算。

新增PATH-ARRIVAL-REVISION-INSERT-1保持important：正式UI在gate等待期间插入判断、连接gate→新增节点→end、保存新修订并登记边界重载，原run-8386f7c03944却只有gate/end结果，新增节点漏到达。原真实失败及当时源码/产物、修订、UI、RunStore均在../own-runtime/cold-insert-01a11808/，该失败原件不改成成功。同一BGI进程下的助手固定时刻冷恢复已单独实际读回次日同刻并停止，零节点/提交、三个进程正常退出、真实User与产品User不变；它不代表游戏效果或插入通过。

根因：DriveAsync在AwaitNodeScheduleAsync之前对账，等待醒来后先以旧node/plan求条件并持久选择旧后继；后续ProcessBoundaryActions/RecomputeSuccessor按路径合同保留已选游标，正确保护了已完成条件，却使等待期间新保存的未执行节点路径晚一拍生效。修复仅在成功等待、原截止/过期检查之后增加到达边界对账；若新计划生效就回到统一驱动边界，重新以稳定身份核节点、时间和停止，不直接套用旧node对象。已完成条件不重新求值、已选后继不扫描未选车道；原失败/Unknown/布局冲突、停止权威、截止与真实资源责任保护保持。

写集仅WorkflowRunner.cs六行和新PathArrivalRevisionTests.cs。Runner原176358字节/2724行→176800字节/2730行，无BOM且原CRLF保持；修复SHA e9afdaa655f3ccfcd7190d7bb19799544141d95eea6652b9b510cfa84807b700。无大幅缩水。材料外csproj、MigrationReferenceActivation.cs、MigrationSwitchTransaction.cs及其他成果不改或归本批提交。

有限矩阵与来源：

- 等待期间插入并改变尚未求值条件的后继：显式重载/自动修订接续都须gate、inserted、end各一次，未选节点零到达、零资源提交，修订和终局记录持久。
- 已完成条件之后的选中资源等待：修改原条件不重新求值，仍只执行原选中资源。
- 新定义删除正在等待的路径节点：保留该次原到达，不能猜测跳到另一入口。
- 等待期间停止：零节点/资源动作；等待截止与新修订并存：截止照常阻止全部节点动作。

red：Rebuild exit0，六例4 Passed/2 Failed，两失败与实际一样Expected gate/inserted/end、Actual gate/end。green与restored：Rebuild exit0，311例309 Passed/原2 LocalWait Failed/0 Other，新六例全部Passed，313个编译输入无漂移。原失败集合与path-cutoff-01a1176e/restored/result.json完全相同，原Expected Cancelled/Actual LocalWaitParking未改。

negative：仅去掉新增的到达边界三条执行语句，六例重新4 Passed/2 Failed；finally经同目录tmp、flush/fsync、os.replace恢复，字节与修复SHA一致。restored再次重建及同范围回归，变体不用于产物。所有阶段保留原build/test argv、日志、TRX、进程与Job树终态；未改测试断言掩盖失败，未删除/排除原两失败。

首次task controller以普通管道启动，stdin已关闭，EOF exit1，未开始构建/测试或变更源码；确认锁自然释放后按同目录原before/opening恢复TTY，controller-stdin-closed.json保留原错误。不改工具bundle或设施，只修当前任务控制入口。新证据/构建经既有Session/process_runner、old_roots仅去除重复路径且集合一致，当前1.5GiB单次/20GiB累计/8GiB余量政策不改，非OS硬配额。

审查边界：原最后Sol/high源码pass与101/39原对象保持原身份；固定grant已used2/remaining0，未派模型、Agent会诊或换渠道。新增修复尚未独立复核，important责任保持；本记录和回归不伪称综合pass。新同套产物实际UI重演尚待完成，版本与整版Goal未complete。八原生/组/JS/宏、资源Skip、账号兑换码准备队友及游戏/组合退出/关机继续等待主动反馈，不催、不补空脚本或高速循环。

继续当前会话：失败、节点到达边界修复、因果突变与精确恢复属于连续一致性工作，当前可安全接续同一产物实际重演，无需按时间/数量强制换聊天；不恢复来源paused Goal。
