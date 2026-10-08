# 槲寄生施工辅助工具

2026-10-08 按用户要求精简默认工序。产品任务先按[交付流程](../../_workflow/usable-delivery-20261003/DELIVERY-FIRST-POLICY.md)选择功能/使用故障及结束条件，再按需要选工具；以下工具不再是每次修改的必经流水线。

## 保留用途

| 工具 | 需要时用于 | 不能证明 |
|---|---|---|
| storage_limits.py | 新快照/取证/审查材料的共享预算、写入检查和受控 scratch | 系统硬配额或任意外部写入已纳管 |
| deliveries.py | 相关正式交付登记与漂移检查；preparations 另按 JSON 定向核 | 独立成果已集成或已验收 |
| native_review.py / review_process.py | 选用旧协议时固定版本、记录请求、原始独立报告和有限授权 | 工具绿等于语义正确或产品交付 |
| execution_evidence.py / process_runner.py | 选用既有 recipe 时记录实际命令、版本、退出码和自有进程终态 | 构建/单测等于真实 UI/游戏效果 |
| workflow.py / snapshot_reader.py | 选用既有 manifest 时检查声明材料、漂移和读取固定输入 | 矩阵已穷尽、范围正确或全项目应停工 |

普通读取、rg、文档校对直接完成，不为它们生成快照、构建、TRX、反向突变或独立产品审核。Python 工具按实际参数和协议使用，不能仅修改文件名字伪造认证。

## 存储和数据

生成材料经现有 storage_limits.Session，根策略以 `_workflow/storage-policy.json` 为准。普通 Session 默认预约 4 MiB（不超过根单次上限），已知规模用 `Session(root, purpose, reserve_bytes=2*1024**2)` 明确预约。`operation_bytes` 是上限，不能提高上限或新建预算域绕限制；旧的进入前降低该字段的写法继续兼容。可选完整快照/执行捕获入口显式保留原额度，嵌套调用沿用外层额度；普通备份不触发完整协议。复用已有产物，不复制整树。

同 Git common-dir 共享账本与锁；不按 PID/时间抢锁，未知进程或残锁先核真实身份/终态。保护 User、第三方 JS、失败、已引用证据和唯一成果。共享锁及 Job 监测不是 OS 硬配额；任意外部写入仍有能力边界。

Serena 未纳管缓存入口保持原限制，普通文件/rg是适用替代。[存储限制](../../Docs/design/mistletoe-storage-limits.md)用于确实生成材料时的协议说明，不是每次读文档的前置任务。

## 独立审查

[简化审查流程](../../Docs/design/mistletoe-review-process.md)与[独立审查技能](skills/mistletoe-independent-review/SKILL.md)规定当前适用范围。稳定完整版本按真实授权集中复核，不默认每个代码批双审、每个关键断言突变、全历史逐 key 重述、全依赖认证或新增八维表单。

当前在途请求、原 opening、原报告/等级、失败和授权次数保持。失败/超时不释放次数，不重置、不自动加轮；历史记录保留不等于全部成为当前发版前置。

保存真正独立报告及版本/实际来源。可选 native 协议需相应 request/snapshot/config/schema/capture；可选 v3 协议需对应 manifest/opening/audit/verify。它们是选用该协议时的要求，不是任何产品工作都必须调用。

旧工具若因版本或历史政策字段拒绝，原错误和旧身份保持；可用当前适用的普通日志或独立原生来源完成必要核验，明确其范围与未认证状态。不修改 owner_policy、旧报告、receipt 或哈希来假通过，也不另开无关工具整改项目。

## 可选命令导航

从实际仓库根目录执行。下面只展示查阅入口；是否调用及参数由当前任务决定。

```powershell
python -B tools/mistletoe/native_review.py --help
python -B tools/mistletoe/review_process.py --help
python -B tools/mistletoe/workflow.py --help
python -B tools/mistletoe/execution_evidence.py --help
python -B tools/mistletoe/deliveries.py --help
```

需要旧认证协议的显式任务，先核其 `review-bundle.json` 和实际工具版本，再依原命令协议准备/捕获；不能把不满足旧协议的普通来源叫认证收据。旧 `templates/review-plan.json` 等模板是该协议的格式资料，不是新任务清单。

2026-10-08 的规则精简先改正文；随后本次修复 Session 默认预约并为原有可选完整捕获入口明确额度。旧 bundle 哈希未重算，旧版本材料、请求和原冻结副本仍按原身份保留；版本漂移据实报告，不倒签工具重新审查。

## 结束条件

工具结果只用于当前产品决策。必要检查完成就转向功能/版本交付，不为了机械绿、全格覆盖或证明完整继续扩张。文档/技能维护只检查内容、引用、格式和改动范围，不运行产品构建或完整工具回归。
