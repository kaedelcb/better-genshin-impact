# 槲寄生并行成果登记与主线回收

owner 于 2026-09-27 明确要求：成果由执行者登记，后续按计划自行接收，不让 owner 记住、提醒或搬运。
入口：总计划 → 设施接入计划 → 本索引与 [机器台账](mistletoe-parallel-deliveries.json)。
登记是可追溯快照；当前线程状态、源码和依赖必须在消费时重核。
原索引中 R6.1“仍进行中/报告不存在”已被下列实际交付替代，不能继续当作现状。

## 已交付与等待消费

| ID / 任务 | 独立交付 | 主线消费批次 | 当前集成状态 |
|---|---|---|---|
| wave3-bo6-bo7-2026-09-28 / 完成 BO-6 与 BO-7 子批 | 真实 Runner/loop 修复、93/93 定向、助手全量 1562 通过/2 跳过、8 项反向突变；四项原级义务经追加两次独立会诊 closed | 下一 R5 Wave3 主线安全写入批（先接收 BO-6/7，再处理依赖它们的 BO-8/9） | **已接收并经集成验证（2026-09-28）**：主线接收提交 `1945913a4`；导入增量与来源 `e2613a851..e009068e2` 增量逐字节相同（SHA-256 均为 `0642971729f3eff09bde3bf4a437865710ac4966ffd814cb42e36d55d910b3b4`）；集成版本定向 93/93（93 个 testId/名称/结果与来源最终 TRX 完全相同）、助手全量 1562 通过/2 未执行/0 失败/1564（接收前同条件基线 1558/2/0/1560）、testId 1556 unchanged/8 added/4 removed/0 changed；8 项反向突变在集成字节上重跑为 Passed/Failed/Passed 且 mutant SHA-256 与来源记录完全相同；声明面再生（+3/-0）后清除变量复跑通过；本批子批会诊 2/8，验证轮裁定无未闭合 MUST/IMPORTANT。BO-8/BO-9 仍未并入未闭合；BO-6/7-D1 仍为建议级；生产门与实机门继续关闭。〔2026-09-28 更新：BO-8 R29 与 BO-9 R34 F5 已由主线子批 `wave3-bo8-bo9-2026-09-28` 施工，并分别于该批第 1／3 轮会诊按原级登记为 closed（第 1 轮 BO-8 closed；第 3 轮 BO-9 closed；BO-9 经第 1 轮 IMPORTANT-1 与第 2 轮 IMPORTANT-2 两轮原级修复后闭合，收尾原文「本批是否仍有未闭合的 MUST/IMPORTANT：否」，本子批会诊计 3/8）；BO-6/7-D1 已在该批内随 `WorkflowRunner.cs` 哈希重绑定修正；新增建议级残项 **BO-9-D1**（4 处注释陈旧）留待下一次重绑定该文件哈希的批次订正。见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。〕 |
| r56-migration-audit / 核验 R5.6 迁移回滚组件 | 组件核验及补证完成 | R5.6 主线迁移集成 | **已接收（2026-09-28）**：产品/测试增量 +48 行逐字节导入主线（blob 相同、工作区 `4C101FF3…`）；主线集成回归定向 81/81、助手全量 1570/2/0/1572（同条件基线 78/78 与 1567/2/0/1569；testId added=3/removed=0/changed=0）；5 项反向突变在集成字节上重跑并逐字节恢复（4/5 mutant 哈希与来源记录完全相同）；部署目标未留下可观察变化。生产接线/真实静止与激活恢复仍欠 |
| r61-distribution-candidate / 标准化 R6.1 分发候选包 | 候选与离线合同完成 | R6.1 正式分发/互用 | 未合入；后续增量核验见下一行，避免重复导入 |
| r61-integration-validation / 完成 R6.1 分发集成验证 | 集成候选与合同增量核验完成 | R6.1 正式分发/互用 | 资源/过滤语义阻断运行；未合入 |
| r62-bgi-guard / R6.2 BGI旧调度标识符持续守卫 | 候选、独立测试及反向突变完成 | R6 diff收敛/持续守卫集成 | 已登记、待主线适配和集成回归；未合入 |
| r62-contact-audit / 整理 R6.2 公版接点清单 | 固定双端差异清单与离线版本核验器完成 | R6 接点/diff 收敛安全窗口 | 已补登记、待消费；F13 阻断保留；未合入 |
| r5-prepared-process-cross-process-2026-09-28 / 核验 Prepared 身份账跨进程争用 | 真实子进程场景 4/4、固定源码最终 16/16、2 项反向突变恢复 | R5 D18/D26 受理账/存储验证安全批次 | 已登记、独立任务已完成；主线未消费/未验收 |
| r5-slot-process-cross-process-2026-09-28 / 核验 R5 物理槽跨进程交接 | 固定源码 17/17、8 个有效反向突变、1096 项哈希核对 0 缺失/不符；不改产品源 | D14 物理槽底座/生产槽集成安全批 | 独立任务已 completed，最终 HEAD `44576c63d9e45b545943a8c1edef1ee715890c8c`；报告/验收 SHA 已复核；仍 pending、不消费。
| parallel-delivery-review-requirements-2026-09-28 / 并行成果接收前复核要求评估 | 既有候选的验证缺口和接收时机评估；不构成功能交付或验收 | 各既有候选的对应消费批 | 仅补充审查要求；源任务身份/终态未核实，HEAD 已复核至 `6d23092b98833bd580e6688ab2d5107379ae4cbb`，pending |
| r56-activation-prep / 完成 R5.6 迁移接点审计 | A–F 迁移/激活/生产消费者准备矩阵 | R5.6 主线迁移集成批 | **已接收（材料，2026-09-28）**：17 个 `_r56_activation_prep/**` ＋2 个 `_workflow/r56-activation-prep/**` ＋6 个 `_r56_parallel/**` 共 25 个文件按批次证据目录 `_workflow/r56-mainline-integration/source-delivery/` 收纳（暂存内容 25/25 等于来源 blob）；报告 `BEE488…4FED`、HEAD `90588159…e2d6`；**A–F 六项仍为生产接线前置，未闭合** |
| r57-identity-order / 核验 R5.7 旧配置身份与顺序 | TaskDefinitions 缺失/空对象、TaskOrder、NextTaskId 兼容反例 | R5.7 正式旧配置身份/顺序兼容消费批 | 最新任务 turn completed；重要身份/顺序验证缺口仍 blocked；报告 `409A60…8E0`、HEAD `2e66f4a2…18a9` |
| r57-retirement-prep-2026-09-28 / 准备 R5.7 旧链路退役复核包 | 五类旧补丁实现层静态复核 | R5.7 正式旧链路退役实现复核与并存防护验收 | 最新任务 turn completed；不证明旧调度器完全消失或无双跑；报告 `085914…E85`、HEAD `051852fd…fb42`，pending |
| r58-acceptance-prep / 槲寄生 R5.8 验收缺证核销准备包 | 入口/场景/P 编号验收矩阵准备 | R5.8 集成验收准备/收口批 | 报告 `222644…E1E1`、HEAD `4c1316df…e09`；未签产品验收；来源任务身份和状态未能唯一匹配，保留 unknown，pending |

具体任务 ID、绝对 worktree、基线、最终 HEAD、提交、报告/验收哈希、证据摘要和阻断记录在机器台账。
BO-6/7 的权威检查点为 `C:/Users/Administrator/.codex/worktrees/wave3-bo6-bo7/better-genshin-impact-LCB/_workflow/wave3-bo6-bo7/owner-checkpoint.md`（SHA-256 `A4AB77476298FD7AB3847930C0A92BA72F234424EC6A4D3B1706AB17809C75B1`）。主线接收时重核源 HEAD、跟踪与未跟踪工件、提交差异和会诊报告；只在安全写入窗口最小集成并跑集成版本回归。BO-6/7-D1 是注释陈旧的建议级残项，推荐在下次重新绑定 `WorkflowRunner.cs` 哈希的改动中修正并按该批质量门验证；不把它误判为四项原级义务未闭合。
已核对既有交付的跟踪材料及版本；未跟踪证据/缓存保持原样，未清理。R6.2 的长路径 scratch 未作全量清洁声明。

**BO-6/7 主线接收已完成（2026-09-28）**：接收批 `wave3-bo6bo7-receive` 在主线 HEAD `6fd6207e5` 上用 `git checkout e009068e2 -- <路径>` 逐字节导入该子批相对 `e2613a851` 的 8 个非 `_workflow` 文件，并收录少量权威证据到 `_workflow/wave3-bo6-bo7/`（**部分收录**，边界与未收录清单见该目录 `INTAKE-NOTE.md`）；未合并隔离分支，未搬入 6 份 closeout 快照的 report.json/packet.md 与逐突变 TRX 日志包。来源 worktree 与未跟踪证据保留至本次接收验证完成（本条即该验证）。
集成后在主线重跑（不复用来源数字）：定向三类 93/93；助手全量 1562 通过/2 未执行/0 失败/1564；接收前同条件基线 1558/2/0/1560；testId 差集 1556 unchanged/8 added/4 removed/0 changed；8 项反向突变重新执行（baseline Passed/mutant Failed/restored Passed，命中同一批具名断言，源码逐字节恢复）。构建均带 `-p:DeployToBgiTools=false`，部署目标未被写入。声明面按 §17.4-A 第 1 条再生（599→602 行，+3/-0）并清除变量复跑通过。
会诊：本批子批两轮，计次 2/8（第 1 轮 2 项 IMPORTANT + 1 建议 → 按同一修复批归集修复证据 → 第 2 轮验证轮裁定两项 IMPORTANT 按原级闭合、无新增 MUST/IMPORTANT）。来源 BO-6/BO-7 子批的 8/8 + owner 批准追加 2/2 不被重置、不被本批消耗。
证据入口：`_workflow/wave3-bo6bo7-receive/`（manifest、风险矩阵、基线/集成 TRX 与日志、8 项突变材料、验证包、会诊请求与结果、5 份 audit/verify 快照）、R5.3 §24.126、主线接收提交 `1945913a4`。
另记一处接收发现：来源散文把声明面清单 SHA 记为 `9EAA1A7F…`，而随交付提交的清单实为 `84E1D93D…`（原因是其后新增的 §24.125.5 再次改变声明面后执行了再生却未回改散文引用）；已在 R5.3 §24.126.4 登记权威口径，不改写已与登记哈希绑定的来源工件。
**剩余**：BO-8/BO-9 仍未并入、仍未闭合；BO-6/7-D1（建议级注释陈旧，范围含 `WorkflowRunner.cs:1400–1403、1486、1535–1537`）留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次；R5.6、R6.1 与 R6 diff guard 未提前集成；BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭。
旧版本测试不能作为未来集成版本通过；任何“独立交付”不等于“正式集成/验收”。

上述 R5.6 activation-prep、两项 R5.7 prep 与 R5.8 prep 在 2026-09-28 自然边界由 deliveries 扫描发现并补入机器台账；均按其目标消费批登记，没有执行集成、合并或产品验收。R5.8 sidecar 未含可唯一映射的 thread ID，当前任务快照亦未发现唯一对应项，因此其任务终态仍为 unknown。报告 SHA、登记 sidecar、固定基线、worktree HEAD、实际提交及当前未跟踪状态以机器 JSON 为准。

### R5 Prepared 身份账跨进程核验

权威 worktree：`C:/Users/Administrator/.codex/worktrees/r5-prepared-process/better-genshin-impact-LCB`。
固定基线 `5e7e7e22f11daad0c86795368e9a21bb14d79b19`，提交/HEAD `912efbeb9b74f8ecad01b1456844227f8ff70228`；提交只含 `_r5_prepared_process/`，报告 SHA-256 `44D89448CEDE56880ECFE43D96F0CC5DA583E00B4CB58CDB05B57459ACD09F8E`，acceptance SHA-256 `511C0BFAE6E7FA777D471F9968C59C2ED1F78B449F95391EE41690DD642F7F58`。
`核验 Prepared 身份账跨进程争用` 任务于 2026-09-27 18:52:26 UTC 快照为 idle、最新 turn completed。固定源码既有基线 12/12、真实子进程场景 4/4、隔离回归 16/16；2 项关键反向突变恢复；无修复候选。只证明隔离组件，不证明主线适配/回归、BGI/助手/服务端运行或断电耐久。
登记仍为 pending，目标 D18/D26；主线未消费/未验收。本批不接收，生产门维持关闭。

### R5 物理槽跨进程核验

来源任务：`核验 R5 物理槽跨进程交接`，thread `01a0e420-364c-7ad1-af63-fe450cc707a0`；最新 `read_thread` 快照为 idle，latest turn completed。托管 worktree `C:/Users/Administrator/.codex/worktrees/r5-slot-process/better-genshin-impact-LCB`，固定基线 `5e7e7e22f11daad0c86795368e9a21bb14d79b19`，最终 detached HEAD `44576c63d9e45b545943a8c1edef1ee715890c8c`。

实际提交：`63fb0aa5c19c71573f28ec7c65e917ca35e064aa`、`baa31e05d32e3b293f4d671b5843822458d015ef`、`39657881ec8fbb51b80c564e921c3efeb87938c8`、`e78499d07d09a0c272e61468bf179408c95f84c4`、`3f448b2ac34bc7258ae7ce8e30c754464ce3c768`、`44576c63d9e45b545943a8c1edef1ee715890c8c`。最终 report SHA-256 `E9759F47D0B2A7D1912C571E001DCCE9C233E6D702C91BFF911CB0436F354F20`，acceptance SHA-256 `594FB3720D436BFEDF62076DD292772440F12F17093DE045B0FFEC37B9F7AD45`，registration SHA-256 `6C017F4F394B3D091EC6C227993FBF0D6053CE388940E22838BA1B39267AB08D`。

固定源码隔离套件 17/17；双组件进程排他及正常/异常交接使用实际 PID/启动身份、generation/nonce；8 个反向突变有效（基础 3 + 场景敏感 5），恢复后同条件通过；1096 项文件清单 missing=0/mismatch=0；最终无残留组件宿主进程，worktree clean。没有修改产品源码。锁交接证据不代表 BGI/助手/服务端运行、真实 User、断电耐久或所有入口无双跑。

最终只读 `deliveries.py` 扫描记录公共行仍指向旧 HEAD `baa31e05d32e3b293f4d671b5843822458d015ef`，当时交付 HEAD 为 `e78499d07d09a0c272e61468bf179408c95f84c4`，因此返回 exit 2/HEAD drift。扫描后又产生两个报告/登记元数据提交，来源任务随后完成；本批已只更新公共登记到最终 HEAD 与报告/验收 SHA，`integration_state=pending`、`automatic_acceptance=false`。这只是补账，不是 D14 接收、集成或验收；D14 仍须重核最终源任务状态、文件和主线适配回归。

### 并行成果接收前复核要求评估（仅登记）

来源 worktree `C:/Users/Administrator/.codex/worktrees/parallel-recovery/better-genshin-impact-LCB`，commit/HEAD `18e186f897b1d0691faa72837dc897c50d017623`；报告 SHA-256 `BCB9704B74C8066C99F7E06499FF0174C357720025B5811655DD27837669E905`，registration SHA-256 `BAD3CF99A6C7102ACAC0BAEFEA540C8041B5A92E8E27887848B9F51C28725CF7`。报告已提交且绑定文件工作区干净；该工作区另有未跟踪 `_workflow/p28/`。当前可用 Codex 线程快照未能唯一匹配此来源任务身份/终态，故登记为 pending/unknown，不根据 Git 提交推断任务完成。
该材料是既有并行候选的复核必要性评估，不是产品功能或验收。新增要求：R6.1 顺序断言需扰动实际候选生成/输出，依赖检查需注入真实扫描漏报；R6.2 F13 接收时证明五个字段的映射或形成正式迁移/退役裁决并做相关回归；R5 slot 须等源任务最终 closeout 后首次完整复核。各项按原计划消费批次办理，不改变 SB21-4 范围，也不提前合并 R5.6、R6.1 或 R6 diff guard。
公开索引将该报告登记为 `supplemental-review-only-pending`，并逐项绑定现有候选；未消费，未提供质量通过结论，所有生产门保持关闭。

### R5.6

权威 worktree：`C:/Users/Administrator/.codex/worktrees/r56-migration-audit/better-genshin-impact-LCB`。
报告 `_r56_parallel/report.md`，HEAD `c11cb45f6de2436b46c58b91d47f2c9768132ef3`。
提交 `24928d294`、`97d05b931`、`ad248944e`、`c11cb45f6`。
定向81/81，助手1482通过/2跳过/0失败，真实子进程组件恢复及断言反向突变见报告。
尚欠真实引用/激活编排、助手激活元数据恢复、生产检查点消费、真实静止窗口及真实环境验证。
可集成测试补强，不因这些测试通过而自动打开 User 或生产切换门。

### R6.1 上批候选

权威 worktree：`C:/Users/Administrator/.codex/worktrees/r61-parallel/better-genshin-impact-LCB`。
报告 `_r61_parallel/report.md`，HEAD `28d8c764f5e1ae6ae0fee5a1d99a2e41d107d964`。
提交 `252b1258`、`af26127b`、`28d8c764`；候选26/26、迁移合同27/27。
标准JSON与独立脱敏流程已生成，但不是运行就绪包。增量验证已由下批完成，相同候选不重复导入。

### R6.1 集成候选增量核验

权威 worktree：`C:/Users/Administrator/.codex/worktrees/r61-integration/better-genshin-impact-LCB`。
报告 `_r61_integration/report.md`，HEAD `111f6abe0c68330b5acdd867a21586071ce4662a`。
提交 `dfa8dfa79`、`111f6abe0`；合同27/27、反向突变指定断言失败26/27、精确恢复27/27。
已明确：缺“砍树”组、四类脚本目录、12/33路线目录；legacyFiltered与连续计划触发语义待裁决；
应用加载/保存/互导/真实运行未验证。此批没有替换正式分发、没有主线合入、没有启用或发布。
后续以本批作为R6.1接收入口，回溯上批来源；不让owner再找两份材料。

### R6.2 BGI旧调度标识符持续守卫

权威 worktree：`C:/Users/Administrator/.codex/worktrees/r62-bgi-guard/better-genshin-impact-LCB`。
报告 `_r62_bgi_guard/report.md`，HEAD `d50d28e80dc550a4d21ed1ac1f9dd1402d69fd1e`，基线 `1b80154c55b5ccb928870a7c44088be8d5f341e5`。
对应会话原名“读取 Codex Goal 目标文件”，任务ID见机器台账；不要凭会话标题漏掉该交付。
独立21/21、隔离扫描1175个C#且0命中、三类有效反向突变及精确恢复；68/68交付文件哈希和提交blob一致。
候选仅检查12个历史C#文本标识符，不证明改名/非C#调度退出或实际运行安全。
2026-09-27实核后已从观察项转为正式登记；原报告中“待主线登记”是交付时状态，当前登记以本索引为准。
尚未加入现有BGI测试项目，产品构建/UI/实机未验证。主线进入R6 diff收敛/持续守卫集成批时自行接收、最小适配并跑集成回归；不让owner再次转发或搬运。
本次仅补登记，不合入产品、不改变活动SB21批次或生产门。审计见 [R6.2登记复核](mistletoe-r62-registration-2026-09-27.md)。

### R6.2 公版接点审计候选

权威 worktree：`C:/Users/Administrator/.codex/worktrees/r6-contact-audit/better-genshin-impact-LCB`。
本次核实任务“整理 R6.2 公版接点清单”状态为 idle（仅状态快照）；隔离 worktree 无未提交差异。
候选提交 `b73da73cfd52e198c52fe37ebf633e49faa6deaa`，其后收口提交 `0b1c8441d463004786d69cee9904cc362096f65f`、最终核验提交/当前 HEAD `8ee3f9df38991fabf49e945b8382128516a77b20`。
报告 `_r6_contact_audit/report.md` SHA-256 `6074DE54C24008163CFDE4BB2670BE9CC02FE53F3C4F6B7BD91376C8A702B345`；`acceptance.md` SHA-256 `8E71D7655EA78029634A3D893CA43217BD7C2803B60AFCA960B7E03972F6D735`。
登记为 `pending`，目标批次是 R6 接点/diff 收敛安全窗口；686 条路径与 251 个 hunk 的固定输入机械覆盖已复核，但 F13 五个地脉结束检测字段缺少已确认接收处，仍是正确性阻断；多项运行语义未验证。只在目标批次重新核双端提交及依赖、处理 F13 与相应回归，不在 SB21-4 接收/合并此成果。

## 无需 owner 提醒的执行闭环

1. 主线每次开工、自然批次边界及收口交接，都先读本索引并执行发现检查：
   `python -B tools/mistletoe/deliveries.py --root <实际原工作区> --registry <权威机器台账>`。
   工具只读台账和Git托管worktree中的 `_r*/report.md`，输出待消费队列、漂移和漏登记报告，不运行产品、不合并。
   出现漏登记、材料缺失/变化时退出2；执行者自行审计并补登记，不把“查不到”当成“没有成果”。
   工具无法使用则按同等路径人工核对并记录原因，不把操作交给owner。
2. 执行者定向核实对应任务状态、报告、HEAD、提交、证据、范围与依赖。工具没有任务状态API，
   `idle`不是完成证明。来源工作区未跟踪资料不能被删掉或假称已版本管理。
3. 当前批次不相关的成果只登记待消费；到其目标集成批时，将接收、最小适配、集成回归及收口写入该批完整Goal，
   由主线唯一写者在安全窗口自行执行既有授权的源码/测试/文档集成，不再请owner搬文件或确认“要不要收取”。
   不抢在正在进行的快照、测试、提交或安全状态转换中改变HEAD；不能趁回收扩大活动Goal。
4. 业务语义/资源/实机授权未满足时，保留具体阻断、责任和解锁条件；可消费部分与阻断部分分开登记。
   阻断只阻止对应集成/启用，不掩盖独立测试或报告已交付。不得因“自动”擅自裁决过滤语义、真实User、部署或发布。
5. 实际集成后回填主线提交、集成版本和证据；只有适用回归/审查满足才记verified。仍有失效证据或重要项就不标完成。
   下一次完整Goal/交接必须带本索引、机器台账、待消费ID和阻断，不只靠聊天记忆；一次只启动一个后续任务。
6. 独立任务收口必须登记其结果。若主线唯一写者忙，先在自己的report/交接中给出登记信息，由主线自然边界发现补账；
   不让owner再补写，不并发修改主线索引。原worktree和证据保留到接收验证完成。

## 完成核查与能力边界

- 每次发现检查必须处理所有漏登记/漂移；不得静默删记录以变绿。覆盖关系保留来源，不能当作已合入主线。
- 工具通过仅证明材料发现/版本绑定；`quality_verdict=NOT PROVIDED`，不证明产品正确性、集成收据真实性或生产可运行。
- 本机制是后续执行者自动完成的计划工序，不是后台定时自动merge。没有向活动会话发消息、没有新建会话或hooks/心跳。
- 当前活动Goal不会热加载所有新增规则，生效检查点是其下一次读取AGENTS/Skill/总计划的自然接力；不能声称当前会话已执行回收。
- 新worktree缺失本地入口时，执行者从上述权威原工作区获取索引、台账和工具，核对哈希，不让owner安装或搬运。

### R5.6 主线迁移集成批接收收据（2026-09-28）

- **集成批次**：`r56-mainline-integration-2026-09-28`；集成前 HEAD `8a3ee6c4c98e845b2988774fe9c3ab65343ce33e`（分支 `main-OldTeaBag-B168`）。**主线接收提交**：`e093f0073403d6cf877064596de6ed1f72d728d2`（`git commit --only`，152 个明确路径：产品/测试增量 2 个、本批证据 145 个、状态文档 5 个）。
- **接收方式**：逐字节导入（`git checkout <来源提交> -- <路径>`），**未**合并隔离分支；产品/测试增量只有 `R56MigrationSwitchTransactionTests.cs`（`git diff --numstat 8a3ee6c4c c11cb45f6 -- <path>` = `48 0`），材料 25 个文件按批次证据目录收纳并逐文件哈希核对。
- **集成版本证据**：两项目 Rebuild 均带 `-p:DeployToBgiTools=false`（0 错误）；同条件基线定向 78/78、助手全量 1567/2/0/1569；集成后定向 81/81、助手全量 1570/2/0/1572；testId added=3/removed=0/changed=0；5 项反向突变 P/F/P 且源码逐字节恢复；声明面两轮再生（618→619→621 行）后清除变量复跑通过（最终清单 SHA-256 `543A5687…EE37`）。
- **会诊**：本子批独立计数 1/8（`gpt-6-astra`/medium 只读）：MUST 0、IMPORTANT 0；4 项建议级全部采纳（含 F-4 判为设计边界并登记 `R56-D1`、材料计数笔误 18→17、`VerifySnapshot` 调用点漏记 `RecoverOnStart:698`、措辞收窄）。
- **遗留阻断**：A–F 六项生产接线前置未闭合；`R56-D1`（提交后快照损坏+直接授权交错无覆盖）与 `F-5`（交付 B 行号漂移）留待下一次重绑定 `MigrationSwitchTransaction.cs` 哈希的批次；BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭。
- **证据入口**：`_workflow/r56-mainline-integration/`（manifest、风险矩阵、opening、基线/最终 TRX 与日志、5 项突变材料、两轮声明面证据、会诊请求/预检/结论、只读子 Agent 报告、review/closeout audit+verify 快照）与 R5.3 §24.128。
