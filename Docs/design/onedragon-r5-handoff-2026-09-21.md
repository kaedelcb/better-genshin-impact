# 槲寄生调度器 R5 交接稿（2026-09-21）

## 当前权威状态

- 分支：`main-OldTeaBag-B168`
- 最新提交：`4618ee002`（R5.8 生产外部启动接线复核：ASTRA 判 7 项阻断，接线撤回并保持生产门关闭）
- 未提交草稿：`Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md` 末尾 **§24 B3 外部启动生命周期补全设计**（尚未冻结、尚未实现）
- 工作区未跟踪文件：既有 `.bak`／`.stale`／`TestResults` 等，**禁止删除、移动或提交**

## owner 已决定

- 选择 **A：B3 架构整改纳入 R5，现在做**。
- 不强制旧用户切换；生产外部启动入口在新设计实现并通过验收前保持关闭。

## 已完成

- R5.0–R5.5：组件层完成。
- R5.6：事务组件层完成；迁移演练入口与路径隔离续修已完成。
- R5.7：旧补丁退出收口（静态清单 + 文本守卫）。
- R5.8：迁移演练隔离、路径身份、严格 BGP 进程枚举、宿主零演练产物快照等批次已收口。
- 最新全量回归：**775 通过 / 1 跳过 / 776**（跳过项为既有 P50 严格用例，未放宽）。

## 未完成，按顺序

1. 冻结 §24 B3 外部启动生命周期设计。必须解决 GPT-5.6 sol 复核提出的问题：
   - 租约格式 version：新增责任字段需升 version 3，旧消费者 fail-closed；
   - 唯一终态事务/恢复状态表；
   - 预观察记录（句柄产生前崩溃恢复）；
   - “Operation 已登记、尚未占位”的取消竞态；
   - `_gate` 两段事务：锁内占位发布责任 → 锁外启动/取证 → 重新串行校验结算；
   - 持久化可信 `OperationType`，未知类型 fail-closed；
   - `unknown` 与权威终态分离；
   - 取消/Unknown 允许长期保守停驻，不得用超时强制终局。
2. 实现 B3 生命周期改造：`CommandExecutor`、`ArbitrationAdmissionService`、`TaskCenterHost.Admission`、`ExternalStartLedger`、`ExternalStartAdmission`、`CommandResult` 及相关模型。
3. 补生产组合根闭环夹具，恢复 `MainViewModel → CommandExecutor → TaskCenterHost → Core` 生产接线；默认门在验收前保持关闭。
4. B2-γ 第 3 步接线复核：宿主节点分派 + 装饰器接线 + 端到端夹具。
5. B4：提交点清单 + 并发屏障收口 + 全量回归 + 结束会诊。
6. R5.3 / R5.5 / R5.8 最终收口与「无双跑」验收单签署。

## 会诊与执行纪律

- 会诊模型：**GPT-5.6 sol，effort=medium**；限流再降级 deepseek-flash（只读）。
- 实现类改动：会诊 → 处置 → 回归 → 验证会诊 → 无必改项后再提交。
- 文档类改动：攒 3–5 项一次会诊；不追求零发现，但不能自相矛盾、不能引用不存在锚点、效力状态要正确。
- 构建/测试：`dotnet test ... -p:DeployToBgiTools=false`。
- 提交：`git commit --only -m "<msg>" -- <files>`；禁止 `git add -A`／`git add .`。

## 新会话开场建议

> 继续 BGI 槲寄生调度器 R5（接管与切换）。先读本交接稿与设计稿 §24；owner 已选 A，B3 架构整改纳入 R5。先冻结 §24（GPT-5.6 sol medium 会诊至无必改项），再实现、回归、补生产组合根夹具。目标：R5.1–R5.8 全部收口，R5.8「无双跑」验收单签署。不要向我索取「继续」确认。
