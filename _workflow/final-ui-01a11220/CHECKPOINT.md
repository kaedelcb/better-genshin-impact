# 正式 UI 验收的数据保护检查点

交接标记 FINAL-UI-ACCEPT-20261007-FROM-01a10f9b；本聊天 01a11220-852f-7e42-b4c9-5bbcedbbd87d。

原生完整 Goal 已建立并读回 active；来源 paused Goal 不恢复。本聊天最近 rollout 的 turn_context 2026-10-06T16:51:28.816Z 为 gpt-6.1-sol/medium，effort 与 collaboration_mode.settings 一致。实际 cwd E:\Program Files\better-genshin-impact-LCB；开工分支 main-OldTeaBag-B168、HEAD 7971fa1b96774943e60175c4eddaca1b099f135f。

采用 mistletoe-release-first-20261005-v2、mistletoe-complete-usable-delivery-20261006-v1、mistletoe-storage-limits-20261005-v1。完整总目标未完成；独立综合报告/原级 open、opening、额外 2/2 余额 0 保持。本聊天会诊请求 0。bundle drift 和 r61-distribution-candidate 缺 report 只读核验一致，不开工具修复工程。并行成果发现已运行，无漏登记报告，原已消费/待消费边界保持。真实鼠标和跨软件验收需顺序执行，当前不派子 Agent，不消耗复核额度。

## 产品身份与证据复用

同一产物 _workflow/runtime-unified-01a10e1b/product。polish-green 的 305 个源码输入逐 SHA 当前无漂移；runtime-refresh-polish 的五个助手模块逐 SHA 当前无漂移。助手 DLL ab35fc7464a5543620d72719a6c2bd4a89dfc035366d273daac8b9085564ab74；BGI DLL 0720fe79e71f1069c51378cc8a0e54e621dd03ea900fca052915c77f9a9147c7；BGI EXE 42aa9e13aa823a2796fd96b666360dff29890ed1f4d73798f837fa740ac70795。本聊天没有重构建、刷新模块或启动 BGI/助手/游戏。Computer Use 仅只读 list_windows，返回 Codex、Edge；全 Session 进程查询未见 BGI/助手/游戏。

## 数据保护操作及实际阻断

普通 Roaming 枚举存在 MSIX 联合视图，不能据目录标签或扩展路径认定物理根。文件句柄确认配置、缓存和部分 runs 实际在 MSIX；流程等其他文件原在常规 Roaming。扩展路径与 handle-relative NtCreateFile 同样重定向，不作为绕过路径。未建立测试配置、未执行 UI 编辑。

首次 prepare：先将 MSIX 更名后，常规视图也出现同名保全目录，目标存在断言拒绝；恢复 MSIX，116 原件逐 SHA 相同。原证据 private/msix-before.json、private/regular-before.json 保留，预约按 failed，不重跑。

第二次 prepare（r2）：使用不同物理根名称，仍发现常规 Move-Item 的目标被重定向到 MSIX；保存位置实际句柄校验拒绝。自动回退把 27 个常规原件集合放入 MSIX 当前目录。随后 recover-overlay 核两集合逐 SHA，独立保全 27 文件，再恢复 MSIX 原件 116 文件。结果 r2/overlay-recovery.json；两份原件清单 r2/private/*-before.json 为恢复基线，私有不提交。所有原件字节仍保存，常规原物理位置尚未证实恢复，不能声称两根已恢复。

常规原件现保存在 C:\Users\Administrator\AppData\Local\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Roaming\NexusBGI-regular-recovery-01a11220-r2；MSIX 原件已回 NexusBGI。旧测试档案和第三方 JS 未编辑。新操作均进入原 storage_limits.Session，额度单次 1.5 GiB、累计 17 GiB、余量 8 GiB，不是 OS 硬配额。

## 唯一必要外部操作

在用户从开始菜单打开、非 Codex 内置的 PowerShell 中，用 D:\Program Files\Python\python.exe -B 加本脚本绝对路径及 external-restore 参数运行，脚本自行定位仓库。该阶段只恢复/核验原件，不启动软件：已存在且逐 SHA 同的物理常规根只核验；不同内容拒绝覆盖；缺失时仅将上述 27 文件保全目录移回原物理路径，核实际打开句柄及两集合 SHA。成功写 r2/external-restored.json；不接受逻辑路径或仅自述为成功。

完成该恢复后，本聊天继续先设计可证实的双物理根保护窗口，再用同一产物集中实测拖放/阈值/跨道/线路/双窗撤销/三步添加/实际参数/互导/候选保护/等待停止/保存重启/数据保留。原全部功能与正式 UI、必要综合复核及版本步骤判据保持；用户游戏待测不代执行。当前外部阻断仅限物理数据恢复和依赖它的实机写入，产品未交付，Goal 不 complete。

## 2026-10-07 外部恢复后续（以上保留为历史）

用户明确反馈外部恢复成功，并要求继续原 Goal、禁止再从 Codex 内移动真实用户数据目录。已读取 r2/external-restored.json：27 个常规原件已移回确切物理根，MSIX 116 与常规 27 均逐 SHA 相同、实际文件句柄路径准确；未启动应用、未验收。原 Goal 已读回 active。当前 HEAD 38d92e39c（外部恢复脚本改用系统 PowerShell）为既有修复，不覆盖。

默认程序集不支持独立数据根；实测仅修改 APPDATA 不影响 .NET GetFolderPath。为继续不碰原件的同一软件实机验收，按 isolated-data/begin/admission.json 准入最小修复：AssistantDataDirectory 固定读取显式 NEXUSBGI_DATA_ROOT，统一 13 处存储入口，无指定保持原路径，非法相对根拒绝，显式根禁旧 exe 配置自动迁入。非通用存储改造，不改运行/流程合同、第三方 JS、User 或旧目录。13 个原源码保存在本批 begin，编码/换行及大小核对通过；材料外 csproj/迁移事务仍保护。

isolated-data/green 为测试误引用 Services.TaskCenter 命名空间导致的真实编译失败，build Job 树 0、source_drift 空；失败原件保留。命名空间已修正。green2 集中执行 Rebuild、正式 UI/编辑/独立数据根回归及错根反向突变，finally 写回并核逐字节恢复，再验证恢复版本。结果未终态前不刷新或启动产品、不能称绿色。

runtime_own.py 是本批自有产品测试执行器，不移动真实目录。计划只刷新同一 product 五助手模块，保全旧模块、核 User 前后；通过既有 Session/process_runner Job 启动 BGI 与助手，给助手传本批 E: 下自有数据根；由 Computer Use 真实交互及正常退出。游戏/账号/关机动作仍不代执行。未决综合复核及原额外额度 0 保持，不开新请求。

### 独立根修复验证安全终态

green2 执行句柄在负向构建证据收口时消失，未跑负向测试或恢复回归；负向构建 exit0，原正常树终态缺失。源文件曾保持 mutant，三原 Job/父17052及后代已核不存在或 active0后，recover_isolation.py 从 negative-original.cs 原子恢复并逐字节核 SHA，将原 nonce 锁保存、原预约标 failed，根/额度/身份保持。见 isolation-recovered.json；不称完整认证 PFP。

green3 不再改坏 live 源码：先核现存 mutant 产物 SHA，再实际运行单项根断言，1 Failed/0 Passed/0 Skip，错误包含 unexpected 且在任何配置写入前失败；旧产物原构建继续标 interrupted。随后对恢复的当前源码 Rebuild exit0，真实 interaction.trx 48 Passed/0 Failed/0 NotExecuted，307 输入逐 SHA source_drift 空；build/test 及负向测试各 Job active0。green3/result.json 绑定助手 DLL 450e3600dfafad85f85e525f01f64038e599499ab7cde391d481624885f863bf。finish_safe.py 返回 exit0，预算操作终态；这是源码/组件候选，不是整软件实机或独立综合 pass。
