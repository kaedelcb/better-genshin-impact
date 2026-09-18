# R1 一条龙迁移器（只读 dry-run）

槲寄生总计划 R1 阶段交付：无副作用 JSON DOM 迁移器。读取三种格式的一条龙配置，生成**公版标准配置 + 槲寄生增强流程 + 映射 manifest + dry-run 报告**，输入文件字节不变，迁移结果不激活。

## 运行

```powershell
dotnet run --project Test/OneDragonMigration/OneDragonMigration.csproj
# 退出码 0 = 全部夹具通过；输出写入 Test/OneDragonMigration/_out/candidate-<时间戳>/（gitignore）
```

## 输入格式识别（按 JSON 形状，不用版本号做主判据）

| 格式 | 形状 |
|---|---|
| PublicCurrent | `TaskEnabledList: {id:bool}` + `TaskOrder`/`TaskDefinitions` |
| LegacyNameBool | `TaskEnabledList: {任务名:bool}`，无 definitions/order |
| TeabagTuple | `TaskEnabledList: {数字键:{Item1,Item2}}` + `NextTaskIndex` |

## 产物

- `standard/OneDragon/<名>.json`：公版形状标准配置（字符串稳定 ID 三件套；茶包专有字段移除；`SundayDaySelectedValue`→`SundaySelectedValue`；秘境格事件标记/自定义组回退标准并登记）
- `flows/<计划名>.flow.json`：`mistletoe.workflow` schemaVersion 1——资源节点（`resource.oneDragonConfig` 引用）+ 策略修饰（`condition.weekdays`/`prerequisite.account`/`prerequisite.redeemCode`）+ 触发器/循环/终止/水位（三分法，见总计划 §3.1a 锚点 5）
- `manifest.json`：来源哈希、ID 映射（重复迁移幂等）、删除/上移清单、问题清单、产物哈希、回滚预览
- `report.md`：人类可读 dry-run 报告（退役项/上移项/待确认问题分区）

## 合同回归（Program.cs T01–T27）

三格式识别、枚举顺序=执行顺序（不按数字键排序）、同名重复独立 ID、重复迁移 ID 幂等（prior 仅作只读种子）、输入字节不变、坏文件隔离（语法坏/数组形状/混合形状三类）、NextTaskIndex 映射与失效游标 error 阻断、C12/C13/C14 删除回退、全局调度仅附着旧选中计划流程（含单配置收尾抑制）、账号策略脱敏导出、多入口标记冲突、无茶包字段残留、公版原样保留（含缺 TaskOrder 不改写 ID）、周日字段改名、星期条件本地语义标注（「每日」键优先）、legacyFiltered 过滤标记保留、同名/大小写冲突唯一键、路径越界归一化、manifest/流程结构化 blocked、未识别字段与依赖清单、空 UID 留痕、引用含 configKey+revision、节点 ID 内容哈希派生跨进程稳定。

## 2026-09-18 GPT 会诊加固

12 项发现全部核实处置（路径越界、同名覆盖、公版降级、结构崩溃、阻断缺失、过滤丢失、每日语义、调度扩散、种子累积、身份不稳、黑名单投影、文档漂移），详见槲寄生总计划 §7 进度日志。

## 纪律

- 不实例化产品配置类/VM，不复用旧降级器（`AdaptVersions` 等）。
- 只写 `_out` 自建目录；真实 `User` 配置只在用户授权的正式迁移（R5）中经事务使用。
- 迁移决策（留 11/删 9）见 `Docs/design/onedragon-public-vs-teabag-comparison-2026-09-17.md` §8。
