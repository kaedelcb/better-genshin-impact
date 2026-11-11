# 联机锄地续跑：放宽「计划不能有任何修订」的检查

日期：2026-11-11
状态：问题已确认、方案已提出，**尚未施工**（不改代码，等待开工指令）
范围：`MultiplayerHoeingAssistant` 任务中心联机锄地接续（原计划被锄地打断后的续跑）；与任务中心跳转无关，单独施工、单独提交。

## 一、问题

任务被联机锄地挂起、等待续跑期间，只要计划被保存过任何修改，续跑就被拒绝：运行停在「联机停驻」，随后变成结果未确认，需要用户手动「重置状态」或删除后重新启动。改计划名称、改别的行的日期、调整后面行的顺序、加跳转，都会触发。

这与任务中心「运行中可增删、调序，实时生效」的原则冲突（计划表底部提示也是这样写的）。

实测：在未改动的代码上，锄地挂起期间只改计划名称，`PreparePlanResumeAsync` 返回 null（2026-11-11，跳转第 1 阶段施工中发现，测试 `ChangingJumpTargetDuringOnlineHoeingIsRejectedLikeAnyPlanEdit` 锁定了现状）。

## 二、来由

`dffc98568`（2026-10-13「联机锄地后接续任务中心剩余计划表」）加入。检查位置：

- `Services/TaskCenter/TaskCenterHost.OnlineHoeing.cs` 约 1260 行：`PreparePlanResumeAsync`，`snapshot.Revision != run.WorkflowRevision` 时返回 null；
- 约 1414 行：恢复作业终态观察，修订不同时返回「锄地期间计划修订或节点路径发生变化」；
- 约 1543 行：同上（接管恢复路径）；
- 约 1081 行：接管记录补写取消结果时，同样要求修订一致。

目的：防止续跑接回「已经变了的任务」，例如锄地期间被打断的任务被删除或换成了另一个配置。

## 三、为什么过宽

续跑真正需要确认的只有两件事，现有代码已分别覆盖：

| 需要确认 | 现有检查 |
|---|---|
| 被打断的任务在计划里还在 | 同一处紧接着按任务 Id + 出现序号 `TryLocate`，找不到即拒绝 |
| 该任务对应的 BGI 配置内容没变 | BGI 续跑前逐个核对配置文件哈希，修改或删除即拒绝（`BetterGenshinImpact/Service/Instance/MessageHandlers/InstanceRequestHandler.cs` 约 2013 行，`configuration_changed`） |

「整个计划有没有任何修订」是两者之外多加的一道，拦住的范围远大于需要拦的范围。

## 四、建议的改法

去掉「修订必须一致」，改为只检查：

1. 被打断的任务按 Id + 出现序号在新修订里仍能找到；
2. 该任务仍是同一类型、指向同一配置（`Ref.Config` / `ConfigKey` / `TaskId` 不变）；被换成别的配置时拒绝（多数情况 BGI 的配置哈希检查也会拦住，这里是计划侧的双保险）；
3. 车道布局检查（`ValidatePathCursor`）保留，防止车道结构变化后走错路线。

续跑完成后按新修订继续往下走，与普通运行「修改在下一个任务边界生效」一致。运行记录的修订号在续跑接回后更新为新修订（与 `ProcessBoundaryActions` 重载时的做法一致）。

## 五、改动量与风险

- 改动：上述四处检查，加对应测试。
- 风险中等：这是联机锄地接续的核心路径。

需要补的测试：

- 锄地挂起期间改计划名称、改别的行、加或改跳转后，能正常续跑并按新计划往下走；
- 被打断的任务被删除，或被换成别的配置时，仍然拒绝；
- 车道结构变化（路线成员变了）时仍然拒绝；
- 锄地期间重启助手后续跑；
- 现有联机锄地测试（`CompositionOnlineRecoveryTests`、`OnlineHoeingContinuationTests`、`OnlineHoeingAtomicSuspendTests`、`FailedOnlineHoeingSettlementTests` 等）全部回归，并把 `ChangingJumpTargetDuringOnlineHoeingIsRejectedLikeAnyPlanEdit` 改为期望能续跑。

## 六、施工顺序

在任务中心跳转第 1 阶段提交之后单独施工、单独提交，出问题容易定位。
