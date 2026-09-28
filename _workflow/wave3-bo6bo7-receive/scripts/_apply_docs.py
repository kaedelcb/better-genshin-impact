import io

def read(p):
    b = open(p,'rb').read()
    nl = '\r\n' if b.count(b'\r\n') > 0 else '\n'
    return b.decode('utf-8'), nl

def write(p, text, nl):
    data = text.replace('\r\n','\n').replace('\n', nl).encode('utf-8')
    open(p,'wb').write(data)
    print('wrote %-70s %d bytes' % (p, len(data)))

R53 = 'Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md'
t, nl = read(R53)
add = '''

## §24.126 Wave3 BO-6/BO-7 主线接收与集成验证（2026-09-28）

### §24.126.1 接收对象与来源绑定

本批是**接收批**：把已独立交付、并按原等级闭环的 BO-6/BO-7 修复接入茶包主线。不施工 BO-8/BO-9，不提前集成 R5.6、R6.1 或 R6 diff guard，也不重开已闭合的 BO-13、SB21-3 BO-4、SB21-2 BO-10/12。

- 来源交付 ID `wave3-bo6-bo7-2026-09-28`；隔离 worktree `C:\\Users\\Administrator\\.codex\\worktrees\\wave3-bo6-bo7\\better-genshin-impact-LCB`，交付 HEAD `e009068e22b18d89f4eb9f8947fa937dfa8c925c`，开工基线 `e2613a851bd45c28fdd56b84dfc10784e1d9c9b8`。
- 权威检查点 `_workflow/wave3-bo6-bo7/owner-checkpoint.md`；登记 SHA-256 `A4AB77476298FD7AB3847930C0A92BA72F234424EC6A4D3B1706AB17809C75B1`，接收时逐字节复核一致。
- 交付提交链：`10675950a`（代码与状态文档）→ `600f9ef8c`（workflow 证据与反向突变原始材料）→ `fb691f56a`（owner 批准追加会诊按原级闭环）→ `e009068e2`（最终 HEAD 收口快照 v13）。
- 四项原级义务在来源侧由 owner 批准的两次追加独立会诊按**原等级**裁定 closed：BO-6 R19 **MUST**、BO-6 R21 F4 **IMPORTANT**、BO-7 R21 F1 **MUST**、BO-7 R21 F2 **MUST**；两份报告的收尾结论均为「本范围仍有未闭合的 MUST/IMPORTANT：否」。报告原件随本批收录于 `_workflow/wave3-bo6-bo7/review-cli-a-bo6/`、`_workflow/wave3-bo6-bo7/review-cli-b-bo7/`；逐项处置见 `_workflow/wave3-bo6-bo7/consultation/owner-approved-requests-9-10.md`。

### §24.126.2 接收方式（最小且逐字节可核）

只接收交付相对基线的**产品／测试／状态文档增量**，不合并整个隔离分支，也不搬入重复的大型中间证据（6 份 closeout 快照的 report.json／packet.md 与逐突变 TRX 日志包仍只保留在来源 worktree，并保留到本次接收验证完成）。

- 逐字节导入的产品与夹具文件：`MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`、`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs`、`.../LocalWaitIdentityTranslationTests.cs`、`.../ClaimSurfaceManifest.txt`。
- 逐字节导入的状态文档：本文件 R5.3、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、`槲寄生调度器总计划.md`。
- 逐字节导入的少量权威证据（`_workflow/wave3-bo6-bo7/` 下的检查点、上下文／发现／预算、风险矩阵、两次追加会诊报告与运行元数据、会诊台账对账、突变证据索引、testId 对照）；该目录在主线上属**部分收录**，边界与未收录清单见 `_workflow/wave3-bo6-bo7/INTAKE-NOTE.md`。
- 导入以 `git checkout e009068e2 -- <路径>` 完成：导入后 8 个产品／文档文件在主线的 `git diff HEAD` 与来源 `git diff e2613a851 e009068e2` 逐字节相同，未做任何改写或适配。
- 主线工作区按 `core.autocrlf=true` 为 CRLF 检出，仓库 blob 为 LF；两种形态内容逐字节相同（仅行尾差异）。因此本节后续记录的主线 SHA-256 为工作区 CRLF 形态，来源清单记录的是仓库 LF 形态。

### §24.126.3 集成版本证据（在主线上重跑，不复用来源数字）

- 构建：`dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false` 与测试项目同参数 Rebuild 均 exit 0。部署目标（`BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant`）在全部构建与测试前后未被写入。
- 同条件主线基线（接收前 HEAD `6fd6207e5`）：定向三类 89/89；助手全量 **1558 通过／2 跳过／0 失败／1560**，与来源开工基线 1558/2/0/1560 同值。
- 集成后定向三类（`LocalWaitIdentityTranslationTests`＋`LocalWaitParkingStateContractTests`＋`WorkflowRunnerTests`）**93/93**；其 93 个 testId、测试名与结果与来源交付最终定向 TRX **完全相同**。助手全量 **1562 通过／2 跳过／0 失败／1564**。
- testId 差集（接收前基线 → 集成后）：**1556 unchanged／8 added／4 removed／0 changed**，与来源交付逐 ID 相同：4 个新增为真实 Runner facts，4 个 removed 与 4 个 renamed helper 一一对应。两个保留 testId 的 helper 断言语义已变更，故 `0 changed` 只表示 testId／名称／结果层面的成员一致，不表示既有测试语义未变。
- 8 项反向突变在**集成后的字节上重新执行**（不复制来源记录）：全部 baseline Passed／mutant Failed／restored Passed，构建三阶段 exit 0，命中同一批具名目标与断言行（`WorkflowRunnerTests.cs:1008／1071／1080／1131／1173`），源码逐字节精确恢复；8 个 mutant SHA-256 与来源交付记录**完全相同**，证明是同一批突变作用于同一份内容。

### §24.126.4 声明面处置与两处版本关系

追加本节即改变声明面，按 §17.4-A 第 1 条本批**不得**援引措辞类豁免：先再生 `ClaimSurfaceManifest.txt`，评审清单差异，再清除 `CLAIM_SURFACE_REGENERATE` 复跑守卫，再生结果随本批提交并作为本批源文件纳入受审范围。

接收时发现一处**声明面版本引用陈旧**（建议级材料问题）：§24.125.3、`_workflow/wave3-bo6-bo7/owner-checkpoint.md`、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md` 与总计划均把清单 SHA 记为 `9EAA1A7F6EFAECCB763DA8EE10520E01B128F3129E6DBFB831295EA58CD083DC`，而随交付提交的清单实际为 `84E1D93DCC4D8B4B13EEB82C8CD9C25163FF9C18E59E3F772184997528B6D10D`。原因已核清：`9EAA1A7F…` 是「修正 §24.125 状态／预算声明」那一步的中间值；其后 §24.125.5 的 owner 批准追加会诊闭环文本再次改变声明面，交付侧随后执行了 `regression/claim-surface-post-consultation/` 的再生（`manifest-before`=`9EAA1A7F…` → 再生后 `84E1D93D…`）却未回改前述散文引用。**处置**：本批不改写已审并与登记哈希绑定的来源工件（改写会使检查点登记哈希失效），改为在本节登记权威口径——**最终权威值以随提交的清单文件为准**；`9EAA1A7F…` 只作该步中间值，不得再被引用为最终值。另核：交付清单记录 `original_sha256`/`restored_sha256` 为 LF 形态 `181aa93e…`，而 8 个 `mutant_sha256` 为 CRLF 形态，属同一内容的不同行尾形态；本批记录全部统一为工作区 CRLF 形态，内部自洽。

### §24.126.5 剩余项与边界

- **BO-6/7-D1（建议级，注释陈旧）**：状态不变。两位来源审查者独立指出 `WorkflowRunner.cs` 中两处注释与实现不一致（`:1486`、`:1535–1537`），均评为建议级且明确未据此发现运行正确性缺陷。本批不为它单独改写已审源码；按既定处置留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并修正，并按该批质量门验证。
- **BO-8 R29 IMPORTANT、BO-9 R34 F5 IMPORTANT** 仍未并入、仍未闭合；R5.6、R6.1、R6 diff guard 未提前集成。
- BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭；本批只给助手侧源码、真实 Runner 驱动夹具与主线集成回归证据，外部执行边界仍为 fake，不声称实机或生产验收。
'''
write(R53, t + add, nl)

P = '_batch21/b21_plan.md'
t, nl = read(P)
add = '- **BO-6/7 主线接收（2026-09-28）**：由主线唯一写者按最小方式接收独立 Wave3 子批交付——逐字节导入 8 个产品/测试/状态文档文件（`WorkflowRunner.cs`、3 个 TaskCenter 夹具/清单、R5.3、本计划、sb21-4 交接稿、总计划），未合并隔离分支、未搬入重复的大型中间证据。集成后主线 HEAD 重跑：定向三类 93/93（93 个 testId 与来源最终 TRX 完全相同）、助手全量 1562/2/0/1564（接收前同条件基线 1558/2/0/1560；testId 1556 unchanged/8 added/4 removed/0 changed），8 项反向突变重新执行为 P/F/P 且 mutant SHA 与来源记录完全相同；部署目标未被写入，声明面再生后无变量复跑通过。剩余 BO-8/BO-9 未并入、BO-6/7-D1（建议级注释陈旧）留待下一次绑定 `WorkflowRunner.cs` 的批次修正；生产门与实机门继续关闭。详见 R5.3 §24.126。\n'
write(P, t + add, nl)

P = '_batch21/sb21-4-handoff-2026-09-28.md'
t, nl = read(P)
add = '''
### 主线接收（2026-09-28）

BO-6/7 独立子批的交付已由主线唯一写者在茶包主线按最小方式接收：逐字节导入该子批的产品、夹具与状态文档增量（`e009068e2` 相对 `e2613a851` 的 8 个非 `_workflow` 文件），不合并隔离分支、不搬入重复的大型中间证据。集成后主线 HEAD 重跑同条件基线（1558/2/0/1560）与集成回归（定向三类 93/93、助手全量 1562/2/0/1564、testId 1556/8/4/0），并重新执行 8 项反向突变（全部 P/F/P，mutant SHA 与来源记录完全相同）；部署目标未被写入。R5.3 追加 §24.126 登记接收口径，并纠正该子批散文中把中间值 `9EAA1A7F…` 当作最终清单 SHA 的引用（最终权威值以随提交的 `ClaimSurfaceManifest.txt` 为准）。BO-8/BO-9 仍未并入，BO-6/7-D1（建议级注释陈旧）仍按既定处置留待下一次绑定 `WorkflowRunner.cs` 的批次；生产门、真实 User、R5.8 与实机门继续关闭。来源 worktree 与未跟踪证据保留至本次接收验证完成。
'''
write(P, t + add, nl)

P = '槲寄生调度器总计划.md'
t, nl = read(P)
add = '- **主线接收（2026-09-28）**：BO-6/BO-7 独立子批交付已按最小方式接入茶包主线（逐字节导入 8 个产品/测试/状态文档文件，未合并隔离分支、未搬入重复的大型中间证据）。集成后主线 HEAD 重跑：定向三类 93/93（testId 与来源最终 TRX 完全相同）、助手全量 1562/2/0/1564（接收前同条件基线 1558/2/0/1560；testId 1556/8/4/0）、8 项反向突变重新执行为 P/F/P 且 mutant SHA 与来源记录完全相同；部署目标未被写入，声明面再生后无变量复跑通过。剩余 BO-8/BO-9 未并入、BO-6/7-D1（建议级注释陈旧）保留待下一次绑定 `WorkflowRunner.cs` 的批次修正；生产门与实机门继续关闭。详见 R5.3 §24.126。\n'
write(P, t + add, nl)
