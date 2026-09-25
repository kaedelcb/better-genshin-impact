# ev-1 反向突变验证日志（MUT-EV1-1..11、12、13、14；计数时钟终版＝15 夹具）

- 批次：证据族 ev-1（`TaskCenterHost.ResolveOccupantLevels` 宿主分支夹具）
- 夹具：`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterHostOccupantLevelResolutionTests.cs`（**15 例**，终版＝13 例＋2 例有效租约/台账钩子版早出）
- 生产形态：含本批两处同族真缺陷修复（修复①＝未组装判定先于台账读取；修复②＝台账读取后移到非 Valid 判定之后）。
- **唯一有效证据**：下表红帧全部在「修复①②后、13 例夹具」形态重做（磁盘 TRX mtime 05:09–05:12，total=13；
  `_ev1_mut12_red`/`_ev1_mut13_red` 亦为 13 例帧）。**更早形态的任何帧（11 例/12 例、旧顺序）一律作废，不得引用**；
  两份反例先行帧 `_ev1_notassembled_red.trx`（12 例期，恰 1 红）与 `_ev1_nonvalid_red.trx`（13 例期，恰 1 红）按其生成时点如实标注。
- 施突/还原脚本：`_ev1/_apply_mutation.py`（apply/revert 双向、唯一性断言）；每次还原后 `git diff` 该文件仅含两处修复本身。
- 现行生产顺序（决定探针归因）：守卫（State/身份）→ **租约 Read** → 未组装判定 → 非 Valid 判定 → **运行台账 List** → 解析 → 返回。

## 终版突变表（红名单逐条来自终版 TRX）

| # | 突变内容 | 验证的判据（现行顺序归因） | 实测红 | 实测绿 | TRX |
|---|---|---|---|---|---|
| MUT-EV1-1 | 方法头部插入 `return occupant;`（解析体短路） | 各分支日志判据＋正向 Tier/Priority | **9 红**（4×非Valid＋NonValid_WithFaultyLedger＋RunNotFound＋OpNotFound＋RunListThrows＋Resolved） | 4 绿（2×NotAssembled＋2×早出） | `_ev1_mut1_red.trx` |
| MUT-EV1-2 | 非 Valid 分支改伪造级别（System/MaxValue/true） | 非 Valid 四态＋第 13 例的级别保持判据 | **5 红**（4×非Valid＋NonValid_WithFaultyLedger） | 8 绿 | `_ev1_mut2_red.trx` |
| MUT-EV1-3 | catch 改伪造级别 | 异常分支级别保持判据 | **1 红**（RunListThrows） | 12 绿 | `_ev1_mut3_red.trx` |
| MUT-EV1-4 | 早出守卫改 `if (false)`（组合守卫整体移除） | 四条早出夹具全捕：计数时钟版 2 例（租约读取 ⇒ 计数 >0 ⇒ `Assert.Equal(0, reads())` 红）、台账钩子版 2 例（List 抛非争用异常 ⇒ catch 留痕 ⇒ 日志红） | **4 红**（Idle/Untrusted × 计数版/钩子版） | 11 绿 | `_ev1_mut4_red.trx` |
| MUT-EV1-14 | 组合守卫改 `if (!occupant.HasTrustedIdentity) return occupant;`（**仅删 State 检查**） | State 守卫的独立判别力：Idle 夹具为可信身份输入 ⇒ 仅删 State 检查时两版 Idle 夹具均失去保护（计数版经计数 >0、钩子版经台账钩子留痕）而红；两版 Untrusted 仍被身份检查保护而绿 | **2 红**（IdleOccupant_EarlyOut_NoLeaseRead＋NoLedgerRead） | 13 绿（含 2×Untrusted） | `_ev1_mut14_red.trx` |
| MUT-EV1-5 | 未组装分支改伪造级别 | 接缝态级别保持判据（不触台账/租约 ⇒ 快速红） | **2 红**（2×NotAssembled） | 11 绿 | `_ev1_mut5_red.trx` |
| MUT-EV1-6 | `List()` 后立即 `return occupant;`（解析路径静默多读台账后原样返回） | **解析判据**（第 9 轮会诊必改 2 归因更正）：红由解析原因日志缺失（RunNotFound/OpNotFound）与级别未填充（Resolved）检出——**非**台账故障钩子（该钩子仅 RunListThrows 夹具注入，mut6 下该夹具仍在 List 处抛异常、由原 catch 留痕而保持绿） | **3 红**（RunNotFound/OpNotFound/Resolved） | 10 绿（非 Valid 四态在 List 之前返回，不受影响——与现行顺序一致） | `_ev1_mut6_red.trx` |
| MUT-EV1-7 | 正向返回改丢字段手工构造 | 全字段保留判据 | **3 红**（Resolved/RunNotFound/OpNotFound） | 10 绿 | `_ev1_mut7_red.trx` |
| MUT-EV1-8 | 正向 `WithLevelFacts` 第三参恒 true | `HighestClass` 保持判据 | **3 红**（Resolved/RunNotFound/OpNotFound） | 10 绿 | `_ev1_mut8_red.trx` |
| MUT-EV1-10 | 守卫改为 `_admissionStore?.Read(); return occupant;`（仅静默读租约后原样返回） | **计数时钟判据**（第 11 轮会诊方案）：静默读租约必使 _utcNow 计数 >0 ⇒ 两例计数版早出夹具 `Assert.Equal(0, reads())` 红——**零痕静默读取的确定性判别就此闭合**；其余 9 例经日志/值判据红 | **11 红**（含两例计数版早出） | 4 绿（2×NotAssembled——store 为 null 时 `?.` 短路确实未读；2×NoLedgerRead——本突变不触台账，其判据属 mut6/钩子域） | `_ev1_mut10_red.trx` |
| MUT-EV1-11 | catch 改局部丢字段构造 | 异常分支全字段保持＋预填级别不覆盖 | **1 红**（RunListThrows） | 12 绿 | `_ev1_mut11_red.trx` |
| MUT-EV1-12 | **修复①②整体回退**（恢复原始顺序：`List()` 最先） | 两份反例先行夹具的合同 | **2 红**（NotAssembled_WithFaultyLedger＋LeaseNonValid_WithFaultyLedger） | 11 绿 | `_ev1_mut12_red.trx` |
| MUT-EV1-13 | **修复②单独回退**（`List()` 移回非 Valid 判定之前；未组装判定仍前置） | 第 13 例夹具单独归因 | **1 红**（恰 LeaseNonValid_WithFaultyLedger，3ms 快速红） | 12 绿 | `_ev1_mut13_red.trx` |

## 反例先行帧（真缺陷修复的固有红，按生成时点如实标注）

- `_ev1_notassembled_red.trx`（**12 例期**帧）：第 12 例夹具对修复①前代码天然红（恰 1 红）。
- `_ev1_nonvalid_red.trx`（13 例期帧）：第 13 例夹具对修复②前代码天然红（恰 1 红，5ms）。

## 探针体系与现行顺序的对应（第 8 轮会诊重要项归因更正）

- **计数时钟探针**（第 11 轮方案）：租约库注入计数时钟，任何租约读取（含零痕静默）必使计数 >0 ⇒ 早出「不读租约」的
  确定性判别（mut4/mut10/mut14 均由此红）。墙钟计时断言已按第 10 轮裁定移除（调度暂停可假红）。
- **State 守卫隔离**（第 9 轮会诊必改 1）：Idle 夹具改用可信身份输入后，mut14（仅删 State 检查）单独变红，
  证明「非占用 ⇒ 早出」不依赖身份不可信这一并列条件。
- **台账故障钩子**（RunStore 故障注入）：由 RunListThrows 夹具（异常分支合同，mut1/mut3/mut11 命中）与两例 NoLedgerRead
  早出夹具（第 10 轮必改 1）使用；mut6 的解析路径静默多读由**解析判据**检出（见上表更正），与钩子无关。
- 修复①②的反例归因由 mut12（整体回退，两反例夹具同红）与 mut13（仅②回退，第 13 例红）精确分层。

## 复绿与全量

- 定向 15/15 绿（`_ev1_targeted_green.trx`）；全量 **1393/2/0/1395**（`_ev1_full_final.trx`）＝批次 17 基线 1378＋15 条、
  移除 0（`_ev1/ev1_full_pernames.txt`）。

## 已知边界的演进（如实）

- 「早出路径零痕静默读租约」曾于第 10 轮登记为已知边界（墙钟计时不可靠、存储层无读计数接缝）；**第 11 轮会诊给出
  确定性方案＝构造注入计数时钟**（_utcNow 在 ReadCore 开头必被调用），已在终版夹具落地并经 mut10 实证（两例计数版
  早出夹具红）——该边界**已闭合**，无残余。墙钟计时断言按第 10 轮裁定保持移除。

## 覆盖对账（逐判据，全部有终版突变命中）

未组装值/无痕＝mut5；未组装＋台账故障＝mut12；非 Valid 值＝mut2；非 Valid 日志＝mut1；非 Valid＋台账故障＝mut13；
未命中日志＝mut1、字段保持＝mut7；正向 Tier/Priority＝mut1、字段保留＝mut7、HighestClass＝mut8；
异常日志＝mut1、级别保持＝mut3、全字段保持＝mut11；早出不读租约（含零痕静默）＝计数时钟（mut4/mut10/mut14）、
State 守卫隔离＝mut14（Untrusted 由 mut4 整体移除覆盖）；早出不读台账＝台账钩子（mut4/mut14）；解析路径静默多读台账＝mut6（解析判据）。

## 过程记录（工具脚本缺陷两次＋材料目录残留一次，均已修正且未污染终版证据）

1. 还原脚本「删行」缺陷（对替换型突变会删掉原语句）→ 双向替换脚本重做。
2. heredoc 转义层级错误致脚本语法损坏，9 次施突未生效（跑的是未突变代码）→ Write 工具重写＋语法自检后全部重做。
3. **prepare 材料目录不清理**：早前轮次的旧快照（含 11 例期的 TRX 帧与旧版测试文件）残留在 material 目录内被会诊方读到
   （第 8 轮 #1 的「11 例原件」即源于此）→ 第 9 轮起材料目录每次清空重建；此为会诊基建「prepare 不清理输出目录」缺陷，
   与 dfamily 复审登记的 400KB 静默跳过同族，登记为工具残项。
