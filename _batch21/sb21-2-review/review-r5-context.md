# SB21-2 GPT R5 复核材料说明

## 本轮问题与边界

只复核 SB21-2 的 BO-10（Remove 后新登记代际边界）与 BO-12（Remove/裁剪后重登记的代际回绕、ABA 和 C5 消费前复核）。请重点确认 R4 保留的两项 IMPORTANT 证据缺口是否由本轮材料闭合，并检查当前实现/夹具/全量 TRX 差集是否引入新的 MUST 或 IMPORTANT。不得扩展到 BO-4、BO-6/8/11/13，不授权开启任何生产门。

本轮为 GPT R5，模型 `gpt-6-astra`、medium；发出后计为 SB21-2 第 5/8 次会诊。会诊前重新读取了 review-disposition-discipline.md、独立 `ledger-batch21-sb21-2.json` 与父台账；独立台账在 R5 发出前为 4/8，父台账 SB21-1 维持原累计 9 次，未修改。完整台账在 allowlist 中。

## 当前状态规则

- generation 是单队列文件内 Store 分配的生命周期序号。新 v4 文件第一次分配 gen0；相同 Waiting 载荷幂等复用当前代际；Cancelled 重激活或新身份登记分配 `H+1`。Remove 和到期墓碑裁剪不降低持久 `generationHighWater`。
- v1-v3 只表达 Int32 历史代际，无法恢复删除项的历史值；迁移保留现存项原值并将高水位至少提升到 `int.MaxValue`，Load 不改旧文件，首次写入再持久化 v4。新生命周期与任一旧 int 请求隔离。
- 新生命周期分配达到 `long.MaxValue` 后 fail-closed，文件字节不变；幂等读写、Cancel、Remove、Cleanup/裁剪不需要新代际时仍可进行。所有 Upsert 来件 generation 必须为 0，由 Store 分配。
- C5 消费 API 只接受 Store，读取 Store 快照后检查 ItemId、Waiting 状态及规范非负 long 代际。Remove 后缓存对象不能通过复核；其线性化点是 Load 快照，不承诺 at-most-once，也不直接授权发送。
- Cleanup 回调在隔离副本执行，提交前在同一路径锁下比较原文件存在性和原始字节。保障限于相同规范路径锁键的进程内 Store；符号链接/物理别名及跨进程写者不在本批保障。

## R4 两项 IMPORTANT 的复核材料

1. **R4-1 item generation overflow 反例曾被根 H 溢出遮蔽。** 现在有独立事实 `Load_V4ItemGenerationOverflowWithValidHighWater_FailsClosedWithoutMutation`：根 H=`long.MaxValue` 合法，item generation 为 `9223372036854775808`；断言检查 item-generation 错误且原文件字节不变。将原 item `TryGetValue<long>` 失败分支突变为接受并饱和到 `long.MaxValue` 后，该具名断言因没有异常而失败。原/突变/恢复源 SHA 为 `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289`、`9726E89B312EFB9722EE44C386C9008550A25DD95C1E88A0682866D09BE418C4`、原 SHA；权威突变 JSON、日志和 TRX 在 `mutations-r4/item-generation-overflow-original-parser/`。最终测试项目非增量构建与定向/全量 TRX 是该轮新鲜编译产物；当前 Store 源 SHA 为原实现 SHA。
2. **R4-2 助手全量 TRX 差集原先按显示名计数不守恒。** 当前差集按唯一 `testId` 比较：基线 1498、最终 1508、共享 1497、移除 1、新增 11、共享结果变化 17、共享且结果不变 1480；算术 `1497+1=1498`、`1497+11=1508`。被移除的旧 C5 用例名由语义更准确的新测试名替代；无材料外测试用例消失或改名。详见 `assistant-full-test-diff.md/.json`。

**历史证据限制需保留**：item overflow 的早期失败跑不能区分被测二进制身份，具体原因未定；不作为原生产实现缺陷证据。旧 `item-generation-overflow-original-source-probe/evidence.json` 记录 originalSourceSha256 与 strictSourceBeforeSha256/restoredSourceSha256 不同（`731E…` 对 `8C8D…`），不能证明当前源码恢复到 originalSource；现只作为有歧义的历史记录。权威精确突变记录是 `mutations-r4/item-generation-overflow-original-parser/evidence.json`（原/恢复均 `731E…`），由最终原源码非增量构建、当前源 hash 和最终 TRX交叉确认。

## 最终助手侧证据

- 助手项目非增量构建：0 错误、58 警告；助手单测项目在 C5 计数断言修改后的非增量构建：0 错误、79 警告。日志分别在 `assistant-build-r5-original-parser.log`、`test-project-build-r5-counter-nonincr.log`；命令均传 `-p:DeployToBgiTools=false`。
- R5 文证修订后，测试项目恢复态非增量构建 0 错误/79 警告；`LocalWaitGenerationContractTests` 45/45、全部 `FullyQualifiedName~LocalWait` 190/190、助手全量 1506 通过/2 跳过/0 失败/1508。最终 TRX 在 `targeted-r5-restored-final/`、`localwait-r5-restored-final/`、`assistant-full-r5-restored-final/`；助手全量按 testId 差集仍为 shared 1497 / removed 1 / added 11 / changed 17 / unchanged 1480，详见 `assistant-full-test-diff-r5-counter.md/.json`。
- Remove 和裁剪后重登记夹具从同一个重新打开的 Store 读回持久代际，再各消费旧/新请求，断言 C5 结果计数为旧请求过期 1、新请求有效 1；Trigger 的批次产物数由 `Requests.Count` / `Single` / `Empty` 断言覆盖。关闭 C5 代际比较后，具名 Remove 重登夹具在 `Assert.False` 处失败；源码精确恢复 SHA `CCACFF30CB464409C323F1C7ABAD3AEA828BFB0887C6BFB74316DA1EEDA9D324`。Consumer 无 sender 依赖、助手仓库无生产调用点，因此本批发送数是 0（代码面没有发送入口），不把夹具模拟成真实发送，也不宣称真实发送/消费发生。权威突变证据在 `mutations-r5/c5-consume-generation-valid/`。
- 一次无效的 C5 突变首跑把 `--no-incremental` 传给 `dotnet test`，MSB1001 拒绝该开关；失败不是测试断言证据，`mutations-r5/c5-consume-generation/evidence.json` 已标记无效。唯一有效突变证据是先非增量 build、再 `dotnet test --no-build` 的上段记录。
- 主矩阵 10 个关键保护点均由命名断言检出，另有 legacy 已删除高代际身份强化突变；R5 针对 C5 消费代际比较、文证修订后的 long-generation Trigger 溢出边界再做两项反向突变并精确恢复。item overflow 权威突变在原生产解析实现上完成。`reverse-mutations.md` 与对应 evidence.json 记录范围与 SHA。
- ClaimSurfaceGuardTests 最终文档 regen/no-env 各 1/1；manifest 593 行，SHA-256 `A857167570E7E433E1795FCF2718F760E7877C1A79397EB4A3B899BEB3C566A0`，相对 HEAD 只增加 §24.122 一行。
- 未运行 BGI 实机、真实 User 或生产入口；生产入口、R5.8 签署、E3/E4/E5、热键继续关闭。

## Git 状态和差异分层

- 分支 `main-OldTeaBag-B168`，HEAD `8a4b8d988e2f0ff3ade35538bffa2de7a93c9da2`；暂存区为空。完整原样 `git status --porcelain=v1` 在 `review-r5-status.txt`；本批暂存 diff 在 `review-r5-staged-in-scope.diff`，相关未暂存 diff 在 `review-r5-related-unstaged-in-scope.diff`。
- 本批 tracked scope：4 个 LocalWait 实现文件、2 个定向测试文件、R5.3、声明面 manifest 与 `_batch21/b21_plan.md`；本批新增的交接、规则、回归摘要、差集、反突变和审阅证据在 `_batch21/sb21-2-review/` 与 `_batch21/sb21-2-handoff-2026-09-27.md`。
- 材料外 tracked 变更是 `Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md`、`槲寄生调度器总计划.md`，未列入 SB21-2 提交候选；其状态和独立 diff 分别在 `review-r5-material-out-status.txt`、`review-r5-material-out-staged.diff`、`review-r5-material-out-unstaged.diff`。
- 其余 porcelain 中既有 `.bak/.stale`、日志、测试产物、DLL、历史批次目录及其他材料外条目均原样保留，不纳入本批提交；完整列表仍以状态快照为准。

## R5 返回结论与非阻断修订（GPT 已返回，累计 5/8）

- GPT `gpt-6-astra`／medium 未发现新的 MUST/IMPORTANT；R5 认可 R4-1（独立、合法根 H 下的 item overflow 反例及原源码突变恢复）和 R4-2（TRX `testId` 差集守恒）证据可闭环。其结论基于随审材料；GPT 未运行本机工具，也未独立重算原始 TRX 或哈希。R4 原始等级保留，闭环记录见 `gpt-r5-review.md` 与 R5.3 §24.122.7。
- R5 要求收窄早期 probe 的因果表述：该 probe 的 `originalSourceSha256` 与 `strictSourceBeforeSha256`/`restoredSourceSha256` 不同，二进制身份有歧义；现已明确不以此 probe 推断早期失败成因，也不把它当成原实现缺陷证据。精确恢复只由 `mutations-r4/item-generation-overflow-original-parser/evidence.json` 支持。
- R5 指出 long-generation 夹具把 gen0 的 `other` 与 2147483648 代际项放进同一显式代际批次。现已把两项拆为各自代际的独立 Trigger 调用；`other` 的键保留断言也改用其真实 gen0。
- R5 指出最新 testId 差集 Markdown 的 `$finalPath` 未替换；现已改为最终助手全量 TRX 的实际路径。
- 其他证据边界：消费 API 复核不等于 at-most-once 或真实发送；Mutation 的具体断言位置以目标测试/TRX 结果为据，不能只凭不含异常文本的 mutant 日志；Cleanup 文件若变成不可解析形状可能先抛 Corrupt 异常，当前保证是失败时不覆盖，而不是所有外部变化都必然抛 ConcurrentUpdate 异常。
- 最终 R5.3 文本 `ClaimSurfaceGuardTests` regen 与 no-env 各 1/1；manifest 仍 593 行、SHA-256 `A857167570E7E433E1795FCF2718F760E7877C1A79397EB4A3B899BEB3C566A0`。regen TRX 在 `claim-r5-closeout-final-regen/`；最终路径引用调整后又运行 no-env 1/1，TRX 在 `claim-r5-final-verified-noenv/`。中间一次无变量调用拼错项目路径而未启动测试，保留于 `claim-r5-exact-final-noenv/` 日志。

## 送审准备中的证据目录误覆盖记录

一次最终 ClaimSurfaceGuardTests 复跑误复用了已存在的 `claim-r5-final-regen/` 与 `claim-r5-final-noenv/` 结果目录，VSTest 报告覆盖既有 TRX。覆盖后的两份 TRX 均为本次 1/1 通过结果；较早完成且未被覆盖的独立 `claim-r5-closeout-regen/` 与 `claim-r5-closeout-noenv/` 两份 TRX 也均为 1/1，可用于本批验收。原 `claim-r5-final-*` TRX 的精确字节版本没有事先副本，本批不声称已恢复其原始字节；后续只使用新建结果目录。该误覆盖只影响冗余守卫 TRX 文件，不涉及源码、用户数据、`.bak/.stale` 或材料外文件。
