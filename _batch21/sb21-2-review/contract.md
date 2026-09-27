# SB21-2 代际与消费规则

## 审计边界与历史等级

- 批次计划将 BO-10 与 BO-12 独立划给 SB21-2；BO-4 仍属 SB21-3，BO-6/8/11/13 仍属 SB21-4。
- SB21-1 的 `ledger-batch21.json` 原累计 8 次、owner 明确追加 R9 后累计 9 次；保持原记录不变。SB21-2 使用独立台账；GPT R1-R4 已发送并返回 4 次，另有 2 次本地预检未发送，计数 4/8。
- BO-10 原始 R37 重要-2：旧处置认为 Remove 终结代际后重登 gen0 的有限后果是旧请求额外走一次完整准入。BO-12 原始 R46 重要-2：明确 Remove/裁剪后同身份回 gen0 会使旧 gen0 请求通过 C5，并把彻底修复归入后续 C5 合同面。历史等级、证据和处置不改写；SB21-2 选择更强的无 ABA 合同，不能称为历史顾问对新方案背书。

## 当前实现状态（修复前）

`LocalWaitQueueStore.Upsert` 对保留的取消墓碑做 `Generation + 1`；新登记分支直接采用来件代际（生产入口通常为 0）。`Remove` 物理删除，`PersistCleanup` 可裁剪过期墓碑，两者不保存代际高水位。`LocalWaitReevaluationConsumer.Consume(request, currentItem)` 是纯校验函数，调用方传入其所持对象；仓库内未找到生产消费调用点。Remove/裁剪后同身份重登 gen0 时，旧请求可通过 ItemId、StableIdentity、Waiting、Generation 校验。不同 loopIteration 由 StableIdentity 隔离。请求恒要求完整准入，结构上没有发送许可。

## 状态与转移规则

代际分配采用**单个等待队列文件内的全局单调高水位**。代际代表此队列文件中一次登记生命周期的分配序号，不承诺每个不同身份都从 0 开始，也不承诺某项重激活恰为该项旧代际加一。此规则保留对 StableIdentity 的身份隔离；唯一的 Store 分配点保证已分配代际在本文件生命周期内不复用。

文件根新增必需整数 `generationHighWater`：`-1` 唯一表示该 v4 文件尚未分配任何代际，且只允许与空 `items` 同现；非负值表示该文件已经分配过的最大代际。每个 v4 项都必须有规范非负整数 `generation`，且不得大于根高水位。缺失、null、非整数、越界、H 小于任一项代际，或 `H=-1` 但 items 非空，均响亮拒绝，不做降级推导。

| 当前状态 | 动作 | 结果 |
|---|---|---|
| 新 v4 文件，`H=-1, items=[]` | 第一次新登记 | Store 分配 gen0，原子写入 `Waiting(0), H=0` |
| 任意 H，`Waiting(g)` | 同 ItemId、同登记载荷重复登记 | `Waiting(g)` 幂等复用，不分配新代际 |
| 任意 H，`Waiting(g)` | 同 ItemId、不同登记载荷 | 响亮冲突，文件不变 |
| 任意 H，`Cancelled(g)` | 同 ItemId、同载荷重登记请求（来件 Generation=0） | Store 分配 `H+1`，写 `Waiting(H+1)` 并推进 H |
| 已分配 H，空项或其他项存在 | 新 ItemId 登记请求（来件 Generation=0） | Store 分配 `H+1`，不能自报分配结果 |
| 任意记录 | Cancel / 清理取消 | 标为 `Cancelled(g)`，H 不变 |
| 任意记录 | Remove 终局删除 | 记录删除，H 原样保留 |
| 到期 `Cancelled(g)` | 裁剪墓碑 | 记录删除，H 原样保留 |
| `H=long.MaxValue-1` | 新登记或重激活 | 可分配 `long.MaxValue` 并写入 |
| `H=long.MaxValue` | 需要新代际 | 加法与落盘前响亮拒绝；原文件逐字节不变。幂等 Waiting 登记、取消、Remove、裁剪仍可用 |

同一 JSON 原子替换同时写 `items` 和 H。v1-v3 不具备可恢复的删除历史；旧格式中代际的类型为 int（缺字段等价 0），因此迁移统一以 `int.MaxValue` 为下界：`H = max(int.MaxValue, 所有现存项代际)`。这使所有合法旧请求（代际至多 int.MaxValue）在切换后无法匹配任何新分配代际，包括旧文件曾删除过的更高旧代际；不依赖“新建 Store 实例等于宿主重启”的假设。现存项仍保留原代际，可继续处理其旧请求。首次写入将旧文件转成 v4 并保留迁移 H；仅 Load 不改写文件。v4 后再由 Store API 删除或裁剪会继续保留 H。

显式边界：迁移只覆盖合法 v1-v3 请求所携带的 int 代际；Store API 之外整体删除/回滚队列文件、跨进程并发写入仍不由此保证。路径锁键为 `Path.GetFullPath` 后的路径字符串且忽略大小写，不解析 junction/symlink 等物理别名；并发 Store 只有在规范路径锁键相同的情况下才共享锁。生产接线目前仅在 `MainViewModel.TaskCenterHost` 单例中从 `RunStore.DefaultRunsDir()` 构造一个 LocalWaitQueueStore；其它多 Store 写者必须传入相同规范路径表示或自行保证单写者。证据定位：`MainViewModel.BgiExternal.cs:37`、`TaskCenterHost.cs:119`、`RunStore.cs:59`；无其他生产构造点。原子替换与部分临时写失败夹具仅证明当前进程/Windows 文件操作条件下原件保留，不延伸为断电持久性证明。

`PersistCleanup` 的策略回调可能重入 Store。不得在回调执行时持有读改写锁并让旧快照覆盖新写：先在锁内读取并解析快照，同时保留“文件是否存在”和完整原始字节；释放锁后仅对隔离副本执行清理回调；再重新取得同一条规范化路径锁，并在持锁状态下将当前文件存在性与原始字节和起始快照比较。比较与原子替换之间持续持锁；任一不同则响亮抛并拒绝外层旧快照写回。清理决策用稳定的原始项关联应用到原始快照，回调对隔离副本的任何字段改写都不能污染待提交快照。冲突时不回滚内层提交、不静默重跑回调。初始“不存在”和零字节文件严格区分。

## C5 消费前复核

公开入口只接受 `Consume(request, store)`，每次调用内部从 Store 加载当前快照，再按读取时点检查请求与现存项：存在、ItemId 相同、StableIdentity 相同、状态为 Waiting，且请求代际为规范非负 long 十进制并等于当前项代际。不得公开接受调用方缓存 `LocalWaitItem` 的重载。Remove 后仅持缓存对象不能通过公开入口；重登后旧代际仍须过期。

复核合同在线性化读取时点成立。Load 返回后若并发 Remove，已返回的 Valid 不保证到函数返回时或后续准入时仍有效；连续两次消费同一请求都可能 Valid，本批不声称至多一次消费或请求领取去重。有效仍只表达“必须重新走一次完整准入”；`RequiresFullAdmission` 恒 true，且请求无发送许可字段。队列损坏继续响亮失败，不按空队列放行。助手中没有生产消费调用点，因此不作发送路径或实机验证声明。

## R1 重要发现处置与夹具判据

1. Cleanup 回调重入：按上节释放锁执行回调并在提交前比较原始文件快照；增加跨线程同步夹具，确认另一 Store 写入可在回调返回前完成，外层随后冲突失败；核对 A 仍 Waiting、B/H 完整保留，最终字节等于内层已提交文件。
2. legacy 删除历史不可恢复：迁移 H 下界提升到 int.MaxValue，验证“曾有 A(int.MaxValue)，删 A 后只剩较小 B”的合法旧文件迁移后，A 新代际严格大于所有 legacy int 请求。
3. v4 不变量及分配权：严格校验根 H、逐项上界和唯一未分配表示；所有 Upsert 登记请求的 Generation 必须为 0（负数与非零均拒绝），实际代际由 Store 在私有物化副本分配；保留负数拒绝。
4. C5 夹具拆开：单独建立“旧快照只经历 Remove，缓存对象消费仍被旧 API 接受”的红证据；Remove/裁剪 ABA 夹具只证明代际复用，避免前置代际断言遮蔽后续消费断言。修复后 Store-backed 消费重新验证过期，并反射确认无公开快照重载。
5. long 全链路：验证 2147483648 落盘、Store 重建读回、Trigger 产请求、请求消费、同实例 Trigger 同身份旧键修剪；并覆盖 long.MaxValue 分配耗尽边界。

红夹具通过条件为每个缺陷有一个无前置失败遮蔽的单独反例；完成后对关键保护分别做反向突变验证，并恢复原实现。

## R3 后补强验收矩阵

- 旧 v3 中只剩较小代际项时，新登记分配大于 `int.MaxValue`；显式构造已删除身份的 `R_A(int.MaxValue)`，经 Store 重建后同身份新代际请求被 C5 判为过期。
- 非空 v1/v2 缺 generation 的存量项仍以 gen0 读取、迁移前 Load 不改字节，旧 gen0 请求继续有效；第一次新生命周期登记分配 `int.MaxValue+1`，重建后读回不变。
- v4 非法输入矩阵覆盖 H 缺失/null/错误类型/负数/小数/溢出、`H=-1` 非空、H 低于取消项代际，以及项 generation 缺失/null/错误类型/负数/小数/溢出；所有失败均核验原始文件字节不变。
- 项 generation 溢出反例必须用合法根 H=`long.MaxValue`、item generation=`9223372036854775808`，独立命中项解析；不允许根 H 同时溢出而遮蔽项解析。保存的原 Store 源码经非增量重建后，v4 根/项溢出测试均通过；随后只把项解析失败分支改成饱和接受，该独立断言失败且源码按字节恢复。最终不需要改变 `TryGetValue<long>` 生产守卫。夹具、原实现确认及当前保护反向突变证据见 `item-generation-overflow-original-source-probe/`、`mutations-r4/item-generation-overflow-original-parser/`；早期突变后未重建的运行仅保留追溯。
- H=`long.MaxValue` 时 Cleanup 取消与墓碑裁剪仍可用且不降低 H；Store-backed Consume 遇损坏文件响亮失败；部分临时写入失败与目标替换失败均核验原件保留及临时文件清理。
- Cleanup 的原始字节比较用“仅追加空白、JSON 语义不变”的并发外改写夹具独立证明；跨线程屏障证明同锁键下回调不持路径锁、内层写先提交、外层冲突失败并保留内层字节。
- 当前分支关键反向突变清单及精确恢复哈希见 `_batch21/sb21-2-review/mutations-r3/results.json`；过往 M51/M63 对现 API 的判别力需按 R3 报告重释，不沿用为单分支独立证据。
