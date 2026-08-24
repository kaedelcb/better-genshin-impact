# 项目经验记忆

> 每次完成任务后，自动记录关键经验和模式，供后续任务复用。
> 格式：`- [日期] 场景：经验要点`

> ⚠️ 2026-08-24 重建说明：本文件原版因**编码损坏（GBK 被误读为 UTF-8 后二次保存）**导致大量中文转为 U+FFFD 无法恢复。
> 已重建为精简、准确版本。系统性工程模式仍以 `.agents/rules/bgi-implementation-patterns.md` 为准（此处不重复）。
> 原损坏文件备份保留在 `project-experience.md.bak`。

## 一、工程实现模式（索引到 bgi-implementation-patterns.md）

以下主题的**细节**全部沉淀在 `.agents/rules/bgi-implementation-patterns.md`，本文件只留索引，不重复：

| 主题 | 关键要点（详见规则文档） |
|------|--------------------------|
| 决策函数纯化 | 判定逻辑抽 static pure function，PBT 友好 |
| SignalR subscribe-before-action | 先订阅事件再 invoke，防竞态 |
| 多世界轮换防误终止双标志 | 自触发关房标志 + WorldStateMonitor 轮换 |
| 联机锄地引擎路由 | 联机恒走茶包版，`OfficialAutoFightRouter.UseOfficial` |
| 共享函数按单机/联机分流 | 可选参数+默认值=旧行为，纯函数给决策值 |
| 传送系统生态位 | 缩放/大地图匹配/拖动安全区/loading检测 四环节 |
| 联机同步机制生态位 | FastSync/SyncBarrier/RouteSync/SyncPointResolver |
| IPC 系统 | 命名管道 + InstanceIpcEnvelope 协议 |
| "从此处开始执行" | 配置组 NSFT+SkipFlag vs 一条龙 NextTaskIndex |
| 端到端部署 | WEB 端三处模型同步、startFromIndex 语义等 |

## 二、任务级经验记录（历史）

### 公版优选流程（2026-08-16 起）
- 公版赶路文件：`GameTask/AutoPathing/Handler/SkillBoostHelper.cs`（公版）vs `TeapotHurryOnHelper.cs`（茶包版），路由分叉在 `PathExecutor.cs` 的 `UseNewHurrySystem`。
- 公版源文件通常 UTF-16 LE（BOM: FF FE），优选时需转 UTF-8 无 BOM 再写入。
- 易错：`using AutoFightOfficial.Model` 与 `using AutoFight.Model` 会产生 `Avatar` 歧义 → 用完全限定名；`ESkillCdTracker.ApplyFallback` 签名差异（公版有 `log` 参数）。
- 字段名差异：公版 `MwkJumpFlyDistance` vs 茶包版 `MwkFlyJumpDistance`（发音不同：Fly vs JumpFly），改前先确认走哪份。

### 联机锄地血条阈值（2026-08-16，已并入 bgi-patterns §4.2）
- `MoveForwardTask.MoveForwardAsync` 被单机+联机共用（4 处调用）。改联机行为 = 加可选参数（默认=单机旧值 6）+ `AutoFightSeekDecisions.GetNearHeightThreshold(isMultiplayerHoeing)` 纯函数（联机 8/单机 6）+ 调用点传联机信号。

### 公版战斗 UI 与上游对齐（2026-08-17，commit 4a2710c19）
- 两套独立配置类（AutoFightOfficialConfig vs AutoFightConfig），UI 面板靠 `UseOfficialAutoFight` + DataTrigger 互斥。
- 对齐时**不要只对比配置项数量**，需逐行对比顺序/结构/文案/Visibility/控件类型。详见 bgi-patterns §7。

### 记忆沉淀覆盖缺口（2026-08-17 调研）
- kiro-task-index 223 条历史任务中仅约 10 条有 spec 设计文档，约 200+ 条"快速修复"模式无文档沉淀。
- 高频重复主题（teleport-* 20+ 条、sync-* 50+ 条）需沉淀通用架构知识——后来已沉淀进 bgi-patterns §8/§9。

### EBUSY 文件锁静默失败（2026-08-17）
- `fs_append`/`fs_write` 返回成功 ≠ 文件确实写入。当目标是 `inclusion: always` 全局注入文件（被 IDE 频繁打开）时，写入后必须用 `read_file` 或 shell 验证文件末尾是否真的追加成功。
- 验证方法：`[System.IO.File]::ReadAllLines("path") | Select-Object -Last 5`。
## 三、编码乱码修复经验（2026-08-24，本次重建起因）

### 现象
`.md` / 记忆文件打开后中文全乱码（`项目经验记忆` → `椤圭洰缁忛獙璁板繂`），且**多次读取测试后仍乱码**。

### 根因
文件最初是 **GBK 编码**写的中文，某环节被**当作 UTF-8 误读后再二次保存**，导致：
- 文件带 UTF-8 BOM（EF BB BF），但字节序列是"GBK 中文字节被 UTF-8 重编码"的产物；
- 大部分汉字可逆，但**行尾/句尾全角标点（`？` `）` `。` `：`）第二个字节在误读时被替换成 U+FFFD**，一旦保存**永久不可逆**。

### 诊断方法（可复现）
1. 看文件头字节：`[System.IO.File]::ReadAllBytes(path)` 前 16 字节，确认是否 UTF-8 BOM。
2. 试标准 mojibake 逆转看恢复比例：
   ```powershell
   $mojibake = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($path))
   $restored = [System.Text.Encoding]::UTF8.GetString([System.Text.Encoding]::GetEncoding(936).GetBytes($mojibake))
   ```
3. 统计 U+FFFD 数量判断损坏深度：若每行多个 U+FFFD（>70% 行受影响）→ **不可恢复到可依赖程度**，直接重建。

### 关键结论
- **git 历史也不可靠**：若文件 commit 时已带乱码，git 里存的就是乱码（本案例即如此，所有历史版本同病）。不要指望从 git 找回干净副本。
- **受保护路径外**：`project-experience.md` 等记忆文件若损坏，正确做法是**备份 .bak 后重建**，而非在残缺内容上"猜着补全"——残缺技术经验比没有更危险（关键数字/动词丢失会误导后续代码改动）。

### 防再犯（必做）
- **写入一律 UTF-8 无 BOM**（Kiro 的 fs_write/fs_append 默认如此，但用 PowerShell 写文件时显式指定不含 BOM 的 UTF-8 编码）。
- 若曾用某脚本/工具写 `.md`，确认该工具不强制 GBK/系统 ANSI 编码。
- 追加记忆后**用 `read_file` 或 `Get-Content -Encoding UTF8` 验证末尾**，确认新内容正常（呼应 EBUSY 经验：写入 ≠ 生效）。
### 公版规范化状态问题（2026-08-17）
- `bgi-upstream-pick-workflow.md` 声称 `TpTaskOfficial.cs` 已规范化（基线 commit: 9f82e8234），但当前工作副本（`main-OldTeaBag-B127`）**实际没有 `#region TeaBag Originals / TeaBag Extensions` 代码块**，只有类头部注释描述该约定。
- 下次优选此文件时，需先做真正的规范化（加 #region 包裹公版原始代码和茶包扩展代码），否则会像普通文件一样全量冲突。

### 公版/茶包共享文件关系图谱（2026-08-17）
- **同一文件 #region 隔离**（适合规范化）：仅 `TpTaskOfficial.cs` 一个。
- **不同文件隔离**（无需 #region）：`SkillBoostHelper.cs`（公版赶路）vs `TeapotHurryOnHelper.cs`（茶包赶路）；`AutoFightOfficial/` 整套 vs `AutoFight/` 整套。
- **分发器/路由文件**：`TpTask.cs`（传送分发器 `UseOfficialTeleport`）、`PathExecutor.cs`（赶路路由 `UseNewHurrySystem`）、`OfficialAutoFightRouter.cs`（战斗路由 `UseOfficialAutoFight`）。
- **候选优先做基线标记**：`SkillBoostHelper.cs` 与公版上游有直接继承关系，最适合做下一个基线标记。

### 自定义代理创建（2026-08-17）
- 两个自定义代理在 `.kiro/agents/`：`public-merge-assistant.json`（公版合并助手，负责公版优选/合并的基线 commit 标记、diff 计算、冲突分析）、`project-knowledge-retriever.json`（项目知识检索，只读检索 BGI 历史经验、规则、spec、记忆档案）。
- **重要**：自定义代理（及 Kiro Hook）需**下一个会话才会被 Kiro 识别加载**，当前会话创建后不立即生效，不要误以为配置无效。

### 联机锄地远程控制助手设计讨论（2026-08-17）
- 背景：讨论做独立于 BGI 的联机锄地助手，通过联机 SignalR 通道远程控制 4 台机器的 BGI。
- 关键决策：助手独立于 BGI 运行（BGI 挂了也能响应），用命名管道 IPC 控制 BGI；停止 BGI 先 IPC 优雅停止、超时再杀进程；操作权限所有成员可操作不依赖房主身份；配置组/一条龙名称各机本地独立、房主发名称；房间轮换不影响助手。
- 协议设计原则：远程控制命令协议**客户端无关**，服务端只认身份不认客户端类型。
- 状态：当时为设计讨论阶段。后续已实现为 `MultiplayerHoeingAssistant`（见 bgi-patterns §10/§11）。

### ABGI 远程控制 BGI 方式参考（2026-08-17）
- ABGI（autoBGI）是 Go + Vue 的 BGI 辅助管理工具，通过 Web 界面远程控制 BGI。
- 核心方式：**杀进程 + 重启带命令行参数**（无 IPC）：停止 `taskkill /F /IM BetterGI.exe`；启动配置组 `--startGroups 组名`；启动一条龙 `--startOneDragon 配置名`；模拟 `CancelTaskHotkey()` 再杀进程。
- BGI 已有命令行参数（`CommandLineOptions.cs`）：`--startGroups`、`--startOneDragon`、`--instance`、`--restart-from-pid`。
- 对比：BGI 已有命名管道 IPC（比杀进程重启优雅），ABGI 的"杀进程+重启带参数"是**不依赖 IPC 时最简单的兜底方案**。

### 联机锄地助手配置陷阱（2026-08-18）
- **`serverUrl` 不要带 `/hub` 路径**：SignalR 客户端内部自动拼接 `/hub`，填 `http://xxx/hub` 会变成 `/hub/hub` 连接失败。正确写法 `http://localhost:5000` 或 `http://xxx:8080`。
- **助手 `serverUrl` 与 BGI 联机锄地配置地址不同**：BGI `CoordinatorClient.ConnectAsync` 直接传 serverUrl 不加 `/hub`（[CoordinatorClient.cs:166]）；助手 `SignalRClient.ConnectAsync` 手动拼 `$"{serverUrl}/hub"`。BGI 配置里的完整 Hub 地址不能直接复制给助手的 serverUrl。
- **`teamUids` 必须填完整 4 个 UID**：房间码 = SHA256(4 个 UID 排序后逗号拼接) 前 6 位 hex，只填 1/2 个会导致房间码与队友不一致。
- **配置文件大小写坑（等号 bug 已修）**：`assistant-config.json` 用户手写小写属性名（`serverUrl`/`teamUids`），`AssistConfig` 模型类是大写属性（`ServerUrl`/`TeamUids`）。`System.Text.Json` 反序列化默认大小写敏感 → 全部失配读默认值 → `Save()` 把空配置写回，用户配置在设置密码后被自动清空覆盖。修复：`Load()` 显式 `PropertyNameCaseInsensitive = true`。
- **spec 实现完成后必须逐条需求对照审查**：MultiplayerHoeingAssistant 实现后没对照 requirements.md 逐条验证，导致 FR-7/FR-8（从此处开始执行）、FR-4/FR-15（配置列表选择）、FR-13（离线命令缓存）、FR-1c（UID 白名单）未实现却以为完成，用户发现后信任崩塌。tasks.md 的 `[x]` 标记必须在任务完成后立即更新。

### 联机锄地助手完整重写教训（2026-08-18）
- 背景：第一次实现因反复修补代码质量崩溃，用户要求完全重走 SPEC 流程、删旧代码从头重写。
- 教训 1：spec 实现后必须逐条对照 requirements.md 做需求审查，不能仅按 tasks 标记判完成。
- 教训 2：tasks.md 完成标记（`[x]`）必须任务完成后立即更新。
- 教训 3：IPC 扩展操作码编译验证——`task.start` 依赖 `ScriptService.RunMulti` 和 `OneDragonFlowViewModel.OnOneKeyExecute`，这些依赖 `App.ServiceProvider.GetService<T>()` 和 `Application.Current.Dispatcher.Invoke`，编译时易报 CS0234/CS4008。
- 关键事实：房间码 `SHA256(4个UID排序后逗号拼接)` 前 6 位 hex；控制房间前缀 `CTRL_`；密码哈希 `SHA256(roomCode + ":" + password)` 服务端内存存储、重启丢失；IPC 内联启动（BGI 无 `--startFrom`，通过 `task.start` 写 `AllConfig.NextScheduledTask` 或 `OneDragonFlowConfig.NextTaskIndex`）。
- 三项目独立编译：BgiCoordinatorServer / BetterGenshinImpact / MultiplayerHoeingAssistant。
### IPC 协议格式不匹配修复（2026-08-18）
- 场景：助手 `IpcClient.SendCommandAsync` 发 `IpcRequest`（OpCode/Payload）到 BGI，但 BGI `InstanceRequestHandler` 期望 `InstanceIpcEnvelope`（operation/data/requestId/version），弹"JSON 值无法转换为 System.String"。
- 根因 1：帧格式 `[4字节 payload length][1字节 payload type (Utf8Json=1)][JSON]`，旧客户端忽略 1 字节 type 头，读 JSON 多一个 `\x01`。
- 根因 2：请求体必须转 `{version=2, requestId, operation, data}` 格式。
- 根因 3：响应是 `InstanceIpcEnvelope`（success/errorMessage/data），需先 `JsonDocument.Parse` 再取字段。
- 教训：BGI IPC 帧格式**带 1 字节 payload type**，任何新增 IPC 客户端必须用与 `InstanceIpcProtocol` 一致的帧格式。

### WPF DataContext 双重实例化（2026-08-18）
- 场景：`MainWindow.xaml` 的 `<Window.DataContext><vm:MainViewModel/></Window.DataContext>` 与 `App.OnStartup` 的 `new MainWindow(viewModel)` 各建一个实例 → 两个实例，一个被初始化、一个绑到 UI（未初始化）→ 窗口正常但所有绑定为空、无报错。
- 根因：XAML `<Window.DataContext>` 优先级高于构造函数代码设置。
- 修复：删 XAML 里的 `<Window.DataContext>` 定义，只靠构造函数代码设置。
- **教训**：WPF 代码后置注入 ViewModel 时**绝不能**同用 XAML `DataContext`，否则两个实例且 XAML 实例优先。

### IPC 响应帧读取长度修复（2026-08-18）
- 场景：`IpcClient.SendCommandAsync` 报 `Expected depth to be zero at the end of the JSON payload`。
- 根因：帧 `length` = JSON 字节长（不含 type 字节），但读取时跳过 1 字节 type 后只读 length 字节 → JSON 末尾被截断。
- 正确读法：流中实际 `length + 1` 字节（1 字节 type + length 字节 JSON），分配 `length+1` 数组，跳过第 1 字节取后面 length 字节为 JSON。

### 助手远程 start 与 BGI 单任务锁抢占（2026-08-17）
- 场景：成员 BGI 正跑本地任务时，助手下发 start，BGI 报"当前存在正在运行中的独立任务"。
- 根因：`HandleTaskStart`（InstanceRequestHandler.cs）固定 `Delay(1000)` 就启动新任务，但 BGI 单任务锁 `TaskControl.TaskSemaphore(new SemaphoreSlim(1,1))` 旧任务清理常 >1s，信号量未释放 → 抢锁失败。
- 修复：改为轮询 `TaskControl.TaskSemaphore.CurrentCount` 回到 1 再启动（200ms 轮询、5s 兜底），只读 CurrentCount 不抢锁不死锁。
- 关键定位事实：BGI 单任务锁 = `BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore`；`TaskRunner.RunCurrentAsync/RunThreadAsync` 用它 `WaitAsync(0)` 抢门；`ScriptService.RunMulti` 内部 new TaskRunner 也抢同一把锁。
- 方案取舍：优于"杀 BGI 重启"——不丢状态不重连游戏；只有旧任务 >15s 停不下的卡死才考虑"超时强制杀进程"。

### 联机助手"锄地中"一直显示的根因（2026-08-18，WPF 局部值 vs Style Setter）
- 场景：BGI 未锄地，助手 UI 从打开就显示"锄地中"一直不消失。
- 排查：按 IPC 链路 BGI→助手→服务端→UI 逐层加日志探针打 `autoHoeingRunning`，确认 BGI/服务端/助手都收到 False，数据链路全对。
- 根因：MainWindow.xaml 成员卡片 `<TextBlock Text="锄地中" ...>` 的**局部值**覆盖 Style Setter 的 `Text=""`（WPF 属性优先级：局部值 > Style Setter）→ 无条件显示。
- 修复：去掉 TextBlock 局部 `Text`/`Foreground`，全交给 Style 控制（默认空，DataTrigger `AutoHoeingRunning=True` 时显示）。
- 教训：UI 显示与数据不符时，先怀疑"局部值覆盖 Style Setter"，链路诊断用打印日志探针逐层定位，别在数据链路盲改。

### WEB 控制端部署经验（2026-08-18）
- 坑 1：signalR JS 必须本地化，不能引 cdnjs `signalr.min.js`（大陆被墙 `signalR` 未定义→点进入静默失败）。下载 `signalr.min.js` 到 `wwwroot/` 本地引用。
- 坑 2：根路径 `/` 被 `app.MapGet("/", ...)` 健康检查占用 → 访问 `/` 看到 JSON。把页面命名 `index.html`（ASP.NET Core 默认文档）让根路径返回页面。健康检查移到 `/health`。
- 坑 3：「`dotnet run` 后台启动后杀 terminal 不会杀 exe 子进程，旧进程仍占端口」——验证"改代码行为不变"时第一嫌疑是残留旧进程占端口，不是改动没编译。诊断用 `Get-NetTCPConnection -LocalPort <port>` + OwningProcess + StartTime 与 LastWriteTime 比对。
- 坑 4：NPM 反代必须配 Websocket 且端口 8080（SignalR 长连接需要）；健康检查 URL 已移至 `/health`。
- 纠错：WEB 一条龙"不执行"的根因不是 IPC 内联，是曾误改 PC 端正常逻辑；已恢复 `CommandExecutor.StartOneClickAsync` 为"IPC 内联 task.start，失败才杀进程重启"。WEB 端命令不执行的真正根因链都在 WEB/服务端（makeCmd 缺 roomCode / 服务端拒绝 web_ 发送者 / startFromIndex 写死 0），不在 PC 端 IPC。

### 环境全局事实（本机机器级，所有会话通用，2026-08-18）
- **桌面路径**：偏离默认，被 360 安全卫士搬家重定向到 `E:\360MoveData\Users\Administrator\Desktop`（不是 `C:\Users\Administrator\Desktop`）。找桌面文件直接用它。
- **QQ 截图缓存**：`C:\Users\Administrator\AppData\Local\Temp\`，按"最新修改时间"找 png/jpg（文件名不一定带 `QQ_` 前缀，不用文件名模式匹配）。已在 AGENTS.md §2.5 全局记忆。
### 需求分析自检流程改进（2026-08-18）
- 用户指出缺少科学的需求分析方法和验证闭环。改进方案（不增用户负担，AI 自行执行）：
  - 需求分析阶段自检三步：两端对比表（WEB/PC/BGI 三端对照）、数据流链路反向推、边界条件清单（空数据/离线/旧版本兼容/用户手误）。
  - 测试落地：不因"IPC 通信不好测"跳过测试，至少为 IPC 处理方法写 mock 测试；纯逻辑/决策函数必须写 PBT。
  - 交付前自检：对照最初需求逐条确认"做了没有"；两端功能对比确保无遗漏。

### 编译输出目录 vs 运行目录不一致（2026-08-19）
- 场景：`dotnet build -p:Platform=x64` 输出到 `bin\x64\Debug\...\`，但用户从 `bin\Debug\...\`（无 x64）运行，导致 `MultiplayerHoeingAssistant.exe` 是旧的。
- 诊断方法：`Get-ChildItem -Recurse -Filter "*.exe"` 对比修改时间和文件大小。
- 重要教训：当用户说"打开的不是根目录的 exe"时，先确认**根目录下的 exe 是否最新编译版本**，而不是怀疑路径代码。`dotnet build -p:Platform=x64` 输出到 `bin\x64\Debug\`，用户可能从 `bin\Debug\` 运行。
- 复制命令：`Copy-Item "MultiplayerHoeingAssistant\bin\Debug\net8.0-windows\MultiplayerHoeingAssistant.exe" "BetterGenshinImpact\bin\Debug\net8.0-windows10.0.22621.0\MultiplayerHoeingAssistant.exe" -Force`，以及同路径 `x64` 版本。

### 状态残留修复：WEB 端已用 taskRunning 门控（2026-08-19）
- 场景：BGI 任务中途停止后，WEB/PC 端状态显示停留在上次的任务名。
- 根因：BGI `HandleTaskStatus` 中 `taskName` 来自 `RunnerContext.Instance.taskProgress.CurrentScriptGroupProjectInfo?.Name`，停止后不立即清空，有残留值。
- 修复：PC 助手 `MainViewModel.ReportStatusAsync` 中 `currentTaskName` 只在 `bgiRunning=true` 时才读 IPC 响应，任务停止后跳过读取，保持 `null`。
- 关键发现：WEB 端 `control-room.js` 的 `renderMembers` **已经**用 `taskRunning` 门控状态显示，所以 WEB 端不需要额外修改——只保证 PC 端上报 `CurrentTaskName` 在 `TaskRunning=false` 时为 `null`，WEB 端自动正确。
- 教训：遇到状态显示问题，先确认链路中哪一环在门控，不要默认两端都要改。

### 编译输出目录诊断顺序强化（2026-08-19）
- 再次踩坑：用户报"任务停止后状态仍残留"（修复代码正确），但花多个来回才意识到 `bin\Debug\...\MultiplayerHoeingAssistant.exe` 没更新。
- 诊断顺序当用户报"改动无效"（代码已改、编译通过）：① 确认用户运行的 exe 是否包含最新代码（对比修改时间）→ ② 复制最新 exe 到用户实际运行目录 → ③ 只有确认 exe 是最新版后才去怀疑代码逻辑。

### 子代理委派后必须验证代码是否真的写入（2026-08-19）
- 场景：委派 subagent 执行两个任务，subagent 报完成，标记 completed，但实际代码没写入。用户测试两轮后用日志探针才发现。
- 根因：没有验证代码是否真的写入了文件，只相信了 subagent 的完成报告。
- 教训：标记 subagent 任务为 completed 之前，必须：① `read_file` 或 `grep` 确认目标文件包含预期改动内容；② 编译验证 `dotnet build` 确认 0 error。只有两步都通过才标记 completed。

### WPF 托盘图标实现（2026-08-19）
- 推荐方案：`Hardcodet.NotifyIcon.Wpf` NuGet 包（纯 WPF，不依赖 WinForms），避免 `UseWindowsForms=true` 导致的命名冲突。
- 关键 API：`TaskbarIcon` 双击事件是 `TrayMouseDoubleClick`（不是 `DoubleClick`），右键菜单用 `ContextMenu` 属性。
- 图标来源：`System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location)` 从 exe 自身提取。
- NuGet 包：`Hardcodet.NotifyIcon.Wpf` 1.1.0 + `System.Drawing.Common` 8.0.0。

### 恢复机制修正链接（2026-08-24）
- 联机锄地恢复机制（SuspendedTaskContext 的保存/恢复、F11 停止 vs IPC task.suspend 两条独立通道、边沿检测死代码修复、索引偏移修复）——详情见 `fix-multiplayer-rerun-sync-issue.md`。
### 全局知识库文档管理（2026-08-24）
- **背景**：用户要求把微软官方文档（SignalR/WPF/MVVM 等）下载到本地，形成跨会话可复用的知识库。
- **方案**：方案 2（裁剪版）= 结论索引（`~/.kiro/steering/global-knowledge-index.md`）+ 官方文档全文（`~/.kiro/docs/`）。索引自动加载（`inclusion: always`），全文按需读取。
- **存放位置**：
  - 用户级 steering（全局，跨项目）：`~/.kiro/steering/global-knowledge-index.md`
  - 官方文档全文（不自动加载）：`~/.kiro/docs/`
- **已下载的 P0 文档**：SignalR .NET Client、SignalR 配置、SignalR Hubs
- **后续会话**：新会话中 steering 文件自动加载，AI 能直接引用本地文档知识，不需要重新学习。
- **其他**：`AGENTS.md` §3 索引已更新指向此知识库。
### task.status 状态便签展示的数据能力边界（2026-08-24 调研）
- 场景：用户想让"任务状态便签"不只显示任务名，还显示"一条龙/配置组 + 任务名 + 当前执行线路"。
- 现状数据链路：BGI `HandleTaskStatus`（`Service/Instance/MessageHandlers/InstanceRequestHandler.cs:676`）→ IPC `task.status` → 助手 `MainViewModel.ReportStatusAsync` 轮询 → 服务端透传 → 遥控器/WEB 显示 `CurrentTaskName`。
- 能拿到的：`groupName`（配置组名，来自 `RunnerContext.Instance.taskProgress.CurrentScriptGroupName`）、`taskName`（当前脚本项目名，`CurrentScriptGroupProjectInfo?.Name`）、`autoHoeingProgress`（联机锄地线路进度文本：第 X/Y 条线路 + 线路文件名 + 预计用时，见 `AutoHoeingProgress.cs`）。
- **关键缺口**：`task.status` 返回的 `groupName` **无法区分这条龙还是配置组**（BGI 端执行时未记录来源目录标识，OneDragon 走 `User/OneDragon`、配置组走 `User/ScriptGroup`）。要显示"一条龙/配置组"前缀，需 BGI 端在执行入口记录来源标识并上报，或遥控器端用配置名去已上报的 `OneClickConfigs`/`ConfigGroups` 两个列表撞名匹配（后者低成本但重名有歧义）。
- 游戏内遮罩 `MaskWindow` 的 `StatusList` 显示的是**固定功能开关状态**（拾取/剧情/邀约/钓鱼/传送，`MaskWindowViewModel.InitializeStatusList`），**不是当前任务名**，与"显示当前任务"是两套独立 UI，别混淆。
- 结论：改动范围 = BGI 主项目 1-3 文件（task.status 加一条龙标识）+ 三端对称 Model（ControlStatus/ControlRoomPlayer 双份 + MainViewModel）+ 展示层，中低风险（全新增字段+兼容默认值，不破坏协议）。
### 成员卡片状态标签的确切实现（2026-08-24 补充，接上一条）
- **位置**：`MultiplayerHoeingAssistant/Views/MainWindow.xaml:747-762`，成员卡片顶部那个 `TextBlock`（配 `DataTrigger` 多个状态分支）。
- **状态机**（WPF DataTrigger 优先级从下往上覆盖）：默认 `离线`(Pyro) → `Online=True` 显示 `BGI已启动: 空闲`(Orange) → `BgiStatus="stopped"` 显示 `BGI 未运行`(Geo) → `BgiStatus="observer"` 显示 `遥控器`(#5B8DEF) → `TaskRunning=True` 显示 `{Binding CurrentTaskName}`(Electro)。
- **数据源**：`MemberViewModel`（`MainViewModel.cs:4136-4148`）的 `Online`/`BgiStatus`/`TaskRunning`/`CurrentTaskName`；`BgiStatus` 只表示"BGI 进程是否运行"（self 上报：observer/running/stopped，见 `MainViewModel.cs:477`），与"是否有任务在执行"（`TaskRunning`）是**两个独立标志**。
- **注意**：`TaskRunning=True` 分支的 `CurrentTaskName` 是 `task.status` 的 `taskName`（当前脚本项目名），**不含一条龙/配置组前缀，也不含线路**。要扩展这两类信息，低成本方案 = 遥控器端用 `CurrentTaskName` 去 `MemberViewModel.ConfigGroups`/`OneClickConfigs` 撞名匹配；线路信息需先在 `ControlStatus`/`ControlRoomPlayer`(双份)/`MemberViewModel` 加 `AutoHoeingProgress` 字段（数据已在 `MainViewModel.ReportStatusAsync` 解析出 `autoHoeingProgress`，只是没下沉到 MemberViewModel）。
### HandleTaskStart 幂等保护误拦多配置组修复（2026-08-24）

**现象**：`OnAllReadyConfirmedInternal` 依次执行绑定的多个配置组（如"联机-传奇-锄地"→"联机-精英-锄地"），第一个正常执行，第二个被跳过，直接恢复原任务。

**日志证据**：
```
[IPC task.start] RunMulti 完成, group="联机-传奇-锄地", wasCancelled=False
[IPC task.start] generation=1 已执行过，跳过重复执行  ← 第二个被误拦
[IPC task.resume] 开始恢复任务: Type="group", Group="单机-小怪-锄地", Index=1
```

**根因**：`HandleTaskStart` 的幂等保护用 `_lastExecutedTaskGeneration`（单个 int）只记录 generation，不记录配置组名。`OnAllReadyConfirmedInternal` 循环中所有配置组共享同一个 `generation`，第一个执行后 `_lastExecutedTaskGeneration = generation`，第二个检查 `generation <= _lastExecutedTaskGeneration` 成立 → 被跳过。

**修复**：`_lastExecutedTaskGeneration` 改为 `(int generation, string? name) _lastExecutedTask` 元组，幂等检查改为同时匹配 `generation` 和 `groupName/configName`。同一 generation + 不同配置组名不拦截，同一 generation + 同一配置组名才拦截（防重复广播）。

**涉及文件**：`BetterGenshinImpact/Service/Instance/MessageHandlers/InstanceRequestHandler.cs`（`HandleTaskStart` 第 506-520 行）

**教训**：`OnAllReadyConfirmedInternal` 的循环共享 generation 是合法行为（同一轮 AllReady 启动多个配置组），幂等保护的粒度应该是 `(generation, 配置组名)` 而不是 `(generation)` 全局唯一。
### 状态标签扩展实现完成（2026-08-24）
- **需求**：成员卡片状态标签 \TaskRunning=True\ 时只显示子任务名，改为直接拼接 \groupName · taskName · 线路\（用户要求"有什么显示什么"，不做一条龙/配置组分类前缀）。
- **关键纠偏**：\	ask.status\ 的 \	askName\ 是 \CurrentScriptGroupProjectInfo?.Name\（**子任务名**，如"每日委托"），不是配置组名！要拿配置组/一条龙名必须读 \groupName\（\CurrentScriptGroupName\，一行龙执行时 OneDragonFlowViewModel.cs:2539 显式设 Progress.CurrentScriptGroupName = group.Name）。独立任务时 groupName=null。
- **方案 B（结构化单字段）**：BGI HandleTaskStatus 额外返回 currentRouteDisplay（第X/Y条线路：文件名，从 AutoHoeingProgress 静态类现成字段拼），遥控端单字段透传，不解析 autoHoeingProgress 中文文本（避免强耦合 BGI 文案）。
- **改动链（已编译 0 error）**：5 处核心改动（BGI HandleTaskStatus + 双份 ControlStatus/RoomPlayer + MainViewModel 解析/透传/MemberViewModel 字段+TaskDisplayText + MainWindow.xaml 绑定）。
- **注意**：改 ControlStatus/ControlRoomPlayer 必须双份（PC端+服务端）同步，否则 CS0117。TaskDisplayText 需在 TaskRunning/CurrentTaskGroupName/CurrentTaskName/CurrentRouteDisplay 的 setter 里触发 OnPropertyChanged。
### 联机助手成员卡片操作弹窗+广播模式（2026-08-24）
- 需求：给"停止BGI"/"启动BGI"按键加确认弹窗 + "发送给所有人执行"选项，参考"清除上线"的实现。
- 模式：`ShowXxxConfirmDialog` 返回 `(confirmed, bool broadcastToAll)` tuple → broadcastToAll 时本机执行 + 构造 `RemoteCommand { Target = ["*"] }` 广播 → 不广播时按现有单发逻辑。
- 关键：`Target=["*"]` 广播无人需要额外协议改动（服务端 `RoomManager.ResolveTargets` 已支持）；`stop`/`start_bgi` 远端执行在 `CommandExecutor.ExecuteAsync` 中已实现。
- 风险：广播 `*` 包含发送者自己，需注意 §18 ack 循环防御（已在 `OnRemoteCommand` 入口拦 `Cmd=="ack"`）。
- 参考文件：`MainViewModel.cs` 的 `ShowClearOnlineConfirmDialog` ~line 989, `OnClearOnline` ~line 920, `OnStop` ~line 670, `OnStartBgi` ~line 699。
### 离线成员提醒模式（WarnOfflineMembers，2026-08-24）
- 场景：广播命令（`Target=["*"]`）前，需检查是否有成员不在线——离线成员收不到命令，其缓存数据可能过时。
- 模式：`WarnOfflineMembers()` 统计 `Members.Where(m => !m.Online).Select(m => m.PlayerName)`，弹出 MessageBox 列出离线成员名单 + 提醒"离线成员收不到命令"。返回 `Members.Any(m => m.Online)` 供调用方判断是否要继续广播（全离线则阻止）。
- 关键：`MessageBox.Show` 是 `MultiplayerHoeingAssistant` 项目既有惯例（`OnCloseGame`、`ExecuteQuickCommandAsync` 都在用）。本项目无 ThemedMessageBox。
- 参考文件：`MainViewModel.cs` 的 `WarnOfflineMembers` 方法，`OnStop`/`OnStartBgi` 的广播分支调用。
### C# 字符串插值中中文引号导致的编译错误（2026-08-24）
- 场景：写 `MainViewModel.ShowQuickCommandBindForMembersDialog` 弹窗标题时用了 `$"...成员名称旁的"选择"按钮..."`，两个中文双引号 `"选择"` 被 C# 编译器误认为字符串结束标记 → `CS1003 应输入 ","` 编译失败。
- 根因：C# 只认 ASCII 双引号 `"` 作为字符串定界符。中文/全角引号 `"` `"` 在转义上**不会被 C# 视为字符串内容**——`$"..."` 内部的中文引号必须在代码里替换为转义 `\"选择\"` 或改用单引号/中文书名号，不能原样放全角引号。
- 修复：`Text = $"..., 点击成员名称旁的\"选择\"按钮进行绑定："`（用 `\"` 转义）。
- 教训：在 C# 字符串字面量（含字符串插值 `$"..."`）里给用户看的文案**不要直接用全角中文引号 `"` `"`**，会破坏字符串定界；要么转义 `\"`，要么换用「」中文书名号。交付前用 `dotnet build 0 error 0 fail` 验证可兜底这类书写问题。
### 删除探针脚本误删文件教训（2026-08-24）
- **场景**：用 PowerShell 行操作脚本删除 `[探针]` 日志行时，`$skipProbe=$true` 跳过逻辑没有精确匹配探针块结束，导致文件从 4038 行被截断到 339 行（`MainViewModel.cs`），以及从 1272 行截断到 753 行（`InstanceRequestHandler.cs`）。
- **根因**：删除脚本使用"匹配注释行后跳过 N 行"的模糊逻辑，而不是精确匹配要删除的代码块。`$skipProbe=$true` 后没有在探针代码块末尾精确停止，而是一直跳过到文件末尾，导致大段代码丢失。
- **恢复**：两次都用 `git checkout` 恢复原始文件（回到 git HEAD 版本），然后重新用 `str_replace` 逐个应用本次会话的改动。**关键风险**：`git checkout` 会丢失未 commit 的本地改动。
- **纪律补充**：
  1. **禁止**用 PowerShell 行操作脚本做"跳过 N 行"的删除。必须用 `str_replace` 精确匹配 oldStr/newStr，或至少用 `ReadAllText` + `.Replace(old, new)` 的精确字符串替换。
  2. 每次写操作后必须用 `grep` 或行数统计验证文件完整性（`$lines.Count` 接近预期值，不是在 300-400 行被截断）。
  3. 如果文件之前有未 commit 的改动，先 `git stash` 再 `git checkout` 恢复，然后用 `git stash pop` 还原。
  4. `str_replace` 虽然也被 PreToolUse hook 拦截，但对比 PowerShell 批量操作，它不会误删范围外代码，更安全。
### 被 AGENT 误删代码的审核恢复模式（2026-08-24）

**场景**：本会话实现了 7 个文件约 14 个改动点（一条龙三层恢复、幂等修复、互斥锁、绑定支持一条龙等），随后被其他 AGENT 误删了 BGI 端的 3 个核心逻辑改动（`HandleTaskSuspend` onedragon 分支、`HandleTaskResume` onedragon 分支、`HandleTaskStart` 幂等修复）和助手端的 3 个改动（`_isAllReadyProcessing` 互斥锁、`OnAllReadyConfirmedInternal` 支持一条龙、`OnBindHoeingGroup` 绑定弹窗支持一条龙）。数据模型字段（`SuspendedTaskContext.OneDragonTaskIndex`/`SubTaskGroupName`、`AssistConfig.OnlineHoeingGroupTypes`）和 `CommandExecutor` generation 透传幸免。

**审核方法**：逐条列出本次会话所有改动点，用 grep 确认每个关键符号是否存在。6 个被删、6 个幸存。

**幸存清单**（grep 确认存在）：`SuspendedTaskContext.OneDragonTaskIndex`/`SubTaskGroupName` 字段、`OneDragonFlowViewModel` 配置组传 `taskProgress`、`HandleTaskResume` group 分支 `resumeIndex = TaskIndex + 1`、`AssistConfig.OnlineHoeingGroupTypes` 字段、`CommandExecutor` generation 透传、`OnAllReadyConfirmedInternal` 方法体（但回退到原始版本）。

**被删清单**（grep 确认不存在）：`HandleTaskSuspend` onedragon 分支三层上下文保存、`HandleTaskResume` onedragon 分支三层恢复、`HandleTaskStart` 幂等修复（`_lastExecutedTask` 元组）、`MainViewModel._isAllReadyProcessing` 互斥锁、`OnAllReadyConfirmedInternal` 使用 `OnlineHoeingGroupTypes` 发 `start_oneclick`、`OnBindHoeingGroup` 绑定弹窗支持一条龙。

**教训**：一个会话实现的核心逻辑改动，因其他 AGENT 的清理操作被回退。数据模型（字段/Schema）改动通常幸存（被配置文件引用），逻辑层（方法体/分支）容易被回退。恢复时应优先保障逻辑层，数据模型可以作为恢复锚点——从字段的 json 属性名反查逻辑层是否被删。
### 快捷指令绑定后"执行无反应/重开不生效"根因（2026-08-24，快捷指令 spec）

- 场景：传奇/次数盾等公共快捷指令，弹窗绑定配置组后点"确认执行"没反应，重新打开弹窗之前绑定的没生效。
- 根因：`BindQuickCommandAsync` 保存绑定只写 `_config.QuickCommands[key]`（本机配置）或推送 `set_quick_command`，**没有同步更新 `MemberViewModel.QuickCommands` 属性**。而分成员列弹窗 `ShowQuickCommandBindForMembersDialog` 的"确认执行"循环读的是 `member.QuickCommands?.GetValueOrDefault(key)`（成员模型属性，来自服务端广播），绑定后该属性未更新 → `string.IsNullOrEmpty(binding)` 为 true → `continue` 跳过 → 不执行。重开弹窗读的仍是旧广播值 → 不生效。
- 修复：`BindQuickCommandAsync` 保存成功后同步更新对应成员属性——
  - 绑自己：`var selfMember = Members.FirstOrDefault(m => m.PlayerUid == _config?.PlayerUid); if (selfMember != null) selfMember.QuickCommands[key] = boundValue;`
  - 绑别人：`targetMember.QuickCommands[key] = boundValue;`（本地弹窗立即反映，不依赖下次 `OnPlayersUpdated` 广播）
- 教训：**涉及"绑定/配置 + 分成员弹窗 + 执行下发"的三段式交互时，绑定保存必须同时更新弹窗/执行要读的成员模型属性**，否则执行端读的是旧数据。UI 弹窗里读 `MemberViewModel.X`（来自服务端广播，刷新慢）而非本机 `_config.X` 是这类"改了不生效"的高频根因。
### 遥控端快捷指令"确认执行"静默跳过（2026-08-24，与上一条同 spec）

- 场景：遥控端（ObserverMode）打开快捷指令弹窗，选择绑定后点"确认执行"，没有反应。执行端正常。
- 根因：`ShowQuickCommandBindForMembersDialog` 的 `executeBtn.Click` 中，对"本机成员"（`member.PlayerUid == _config?.PlayerUid`）的执行分支只做了 `if (_commandExecutor != null) { 本地IPC }`。但遥控端模式下 `ApplyModeRuntime` 已将 `_commandExecutor = null`（第2920行），导致该分支被跳过。且因为 `member.PlayerUid == _config?.PlayerUid` 为 true，也不走 `else { 远程下发 }` ——**什么都不执行，静默失败**。
- 修复：在 `if (_commandExecutor != null)` 后加 `else if (_config?.ObserverMode == true)` 分支，遥控端对本机成员走 `SendQuickStartAsync(key, isOneClick, value, [member.PlayerUid])` 通过 SignalR 下发到执行端。
- 教训：**凡涉及"本机直接执行"的分支（`if (_commandExecutor != null)`），都必须同步考虑遥控端模式下的兜底（`else if (ObserverMode)` 走 SignalR 下发）**。`_commandExecutor = null` 在遥控端是预期行为，但"预期"不等于"什么都不做"——执行端上 `_commandExecutor` 不为 null 的那台机器才会真正执行。`ApplyModeRuntime` 是唯一的分界点，改了它，所有依赖 `_commandExecutor != null` 的分支都需要同步检查遥控端路径。
### 遥控端快捷指令"绑自己改绑不生效/执行旧值"三连坑（2026-08-24，快捷指令 spec 收尾）

遥控端快捷指令经历了三轮修复，三次都是不同的坑，合成一条完整链路记录：

1. **绑定后"执行无反应/重开不生效"**（见上一条）：`BindQuickCommandAsync` 保存绑定只写 `_config.QuickCommands`/推送 `set_quick_command`，没同步 `MemberViewModel.QuickCommands`。弹窗/执行读成员属性读不到新值 → 执行被 `continue` 跳过。
2. **遥控端"确认执行"静默跳过**（见上一条）：`ShowQuickCommandBindForMembersDialog` 的"本机成员"分支只做 `if (_commandExecutor != null)` 本地 IPC；遥控端 `ApplyModeRuntime` 把它置 null → 跳过且不走 else 远程下发 → 什么都不做。
3. **遥控端"绑自己"改绑，执行端执行旧值**（本次）：遥控端在"绑自己"分支（`targetMember.PlayerUid == _config?.PlayerUid`）只保存了**遥控端本机** `_config.QuickCommands[key]` 和 `selfMember.QuickCommands[key]`，**没有推送 `set_quick_command` 给执行端**。执行端收到 `SendQuickStartAsync` 下发的命令后查**自己本机** `QuickCommands[key]`，仍读到旧值 → 执行 A 而不是新绑的 C。
   - 修复：`BindQuickCommandAsync`"绑自己"分支的 `AddLog` 后加 `if (_config?.ObserverMode == true && _signalRClient != null)` → 构造 `Cmd="set_quick_command"`、`Target=[selfMember?.PlayerUid ?? _config?.PlayerUid ?? ""]`、`Params={key,boundValue,isOneClick}` 推送给执行端。

**完整教训**：遥控端模式（ObserverMode）下，`_config.QuickCommands`（遥控端本机配置）与执行端 BGI 上实际生效的 `QuickCommands` 是**两份独立数据**。遥控端一切"绑定/执行"都要想清楚：
- 绑定必须既能更新遥控端本地显示（`MemberViewModel.QuickCommands`），又能通过 `set_quick_command` 推送到执行端（真正生效的那台）；
- 执行必须通过 SignalR 下发（`_commandExecutor` 在遥控端恒为 null）；
- 弹窗显示读 `MemberViewModel.QuickCommands`（来自服务端广播），执行读执行端本机配置——两处都要对得上，缺一处就是"改了不生效/执行旧值"。