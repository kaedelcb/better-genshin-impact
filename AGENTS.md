本项目使用了 WPF-UI、 CommunityToolkit.Mvvm、Microsoft.Xaml.Behaviors.Wpf 来实现 MVVM 架构。在编写代码的时候请注意：

### 主要依赖框架
#### UI 框架
- **WPF-UI (4.0.2)** - 现代化 WPF UI 框架
- **gong-wpf-dragdrop(3.2.1)** - 拖拽框架

#### MVVM 框架
- **CommunityToolkit.Mvvm (8.2.2)** - 微软官方 MVVM 工具包
  - 所有 ViewModel 必须继承自 `ObservableObject`
  - 使用 `[ObservableProperty]` 特性自动生成属性
  - 使用 `[RelayCommand]` 特性自动生成命令
- **Microsoft.Xaml.Behaviors.Wpf(1.1.122)** - WPF 行为扩展库
  - 请尽量使用 Behaviors 库来实现交互，避免不符合 MVVM 规范的交互事件触发方式。

### 其他框架使用要求
1. 请优先使用 Newtonsoft.Json 作为json序列化工具，但是如果这个模型已经被System.Text.Json序列化过了，那么就直接使用System.Text.Json反序列化。
2. 所有简单的对话框弹出需求优先使用 ThemedMessageBox 弹出。而不是 WPF 自带的 MessageBox。

## MVVM 架构规则

### 基础架构

### ViewModel 编写规范

1. **继承规则**
   ```csharp
   public partial class ExampleViewModel : ViewModel
   {
       [ObservableProperty]
       private string _title = "";
       
       [RelayCommand]
       private void DoSomething()
       {
           // 实现逻辑
       }
   }
   ```

2. **属性命名**
   - 私有字段使用下划线前缀: `_fieldName`
   - 公共属性使用 PascalCase: `PropertyName`
   - 使用 `[ObservableProperty]` 自动生成属性

3. **命令实现**
   - 使用 `[RelayCommand]` 特性
   - 异步命令使用 `[RelayCommand]` + `async Task`

### View 编写规范

1. **代码后置**
   ```csharp
   public partial class ExamplePage : UserControl
   {
       public ExampleViewModel ViewModel { get; }
       
       public ExamplePage(ExampleViewModel viewModel)
       {
           ViewModel = viewModel;
           DataContext = this;
           InitializeComponent();
       }
   }
   ```

2. **XAML 绑定**
   - 使用 `{Binding}` 语法绑定 ViewModel 属性
   - 命令绑定: `Command="{Binding ExampleCommand}"`
   - 避免在 XAML 中编写复杂逻辑

### 依赖注入规范

1. **服务注册**
   ```csharp
   // 在 App.xaml.cs 中注册
   services.AddView<ExamplePage, ExampleViewModel>();
   services.AddSingleton<IExampleService, ExampleService>();
   ```

最后，程序能够编译就认为成功，无需实际运行程序。

编译指令参考，如果出现程序占用场景，直接放弃编译验证即可
```
dotnet build BetterGenshinImpact.sln -c Debug
```

---

# KIRO 经验记忆（从 KIRO 移植，永久规则）

以下规则继承自本项目此前在 KIRO 中长期开发积累的经验记忆。完整原文存档在 `.agents/rules/`（12 份），历史任务档案索引在 `.agents/memory/kiro-task-index.md`（223 条任务记录），历史规格文档在 `.kiro/specs/`。KIRO 原档仍保留在 `C:\Users\Administrator\.kiro\`（两处冲突时以较新者为准，不确定就询问用户）。

⚠️ **KIRO 原档是只读的**：`C:\Users\Administrator\.kiro\` 和工作区 `.kiro\` 是 KIRO 正在使用的活数据（用户仍会回去用 KIRO）。**禁止修改、移动、删除其中的任何文件**，读取时也只做只读操作。本环境的一切工作只在本环境的副本（`.agents\`）中进行；若 KIRO 更新了其规则/specs，本环境副本按需重新复制同步（原档始终只读）。

## 1. 受保护路径（绝对禁止删除）★最高优先级

`BetterGenshinImpact\bin\x64\Debug\net8.0-windows10.0.22621.0\User`（以及所有上层目录：`bin`、`bin\x64`、`bin\x64\Debug`、`bin\x64\Debug\net8.0-windows10.0.22621.0`、项目根目录）包含用户长期积累且**不在 git 跟踪**的运行时数据（截图、录像、KeyMouse 宏、运行时 config.json、自定义寻路 JSON、JS 脚本本地修改、调试数据、文档）。

- 任何理由（清缓存、重置项目、清理临时文件）都**禁止**递归删除这些路径。
- 确需删除时必须连续 3 次获得用户明确同意，模糊回答（"嗯"/"ok"/"继续"）不算数。
- 清理编译缓存只允许删 `obj` 目录或单个 .pdb/.dll 文件；**禁止**对任何 `bin` 下目录使用 `Remove-Item -Recurse`。
- 历史事故：2026-05-26 曾因 `Remove-Item -Recurse -Force obj bin` 永久删除了用户 10 年积累的数据。绝不允许重演。

## 2. 输出语言

面向用户的所有输出（回复、提问、总结、方案、表格、注释中给用户看的说明）一律简体中文；内部思考可用英文。

## 2.5 用户截图读取路径（全局记忆 — 所有会话必须遵守）

用户要求记住：**以后用户说"看这张图/看图"时，优先去这个目录找最新的 QQ 截图缓存，所有会话都生效。**

- **QQ 截图缓存目录：** `C:\Users\Administrator\AppData\Local\Temp\`
- **文件名规律：** `QQ_<毫秒时间戳>.png`（毫秒级 Unix 时间戳，**数字越大 = 截图越新**）
- **读取方法：** `list_directory` 该目录，找文件名时间戳**最大**的 `QQ_*.png`（**排除 `_thumb.png` 缩略图**），这就是用户刚截的图；然后用 MCP 图片分析读取。
- **验证通过：** 2026-08-19 用户截图后，用该方法成功读到最新 `QQ_1787129592988.png`。
- **注意：**
  - 用户 Ctrl+V 粘贴到 Kiro 聊天框的截图，**作为消息附件无法被 Agent 直接读取**——必须走上述 Temp 目录缓存文件。
  - PowerShell 的 `Get-ChildItem` 可能被 PreToolUse 钩子误拦截，读这个目录优先用 `list_directory` 工具，不必走 shell。
  - 桌面被 360 重定向到 `E:\360MoveData\Users\Administrator\Desktop`，若要从桌面找截图文件用此路径。

## 3. 始终生效的核心纪律（详情见 `.agents/rules/` 对应文件）

### 3.1 改动流程：三阶段自审（`pre-submit-review.md`）
任何改动：需求分析（画影响半径三问 → 风险分级 → 改动点清单）→ 设计（边界/依赖/兼容性审视；首选"可选参数+安全默认值 / 新增分支 / 抽纯函数"，最后才考虑原地改语义；显式枚举不可破坏清单）→ 执行后三层验证（编译 0 error → 静态 grep 确认无辜调用方未变、旧符号零命中 → 行为层测试）。禁止：跳过分析直接动手、改完不查就提交、连续失败后继续"试修复"。

### 3.2 诊断纪律（`debugging-reasoning-discipline.md`）
diagnose-first 而非 fix-first；连续失败 2 次后禁止再改修复代码，只能做诊断动作（日志/标记/二分/打印中间状态）；先测最便宜的承重假设（"我改的代码真的在用户跑的程序里吗"）；用户报告"零变化"是诊断金矿；连续失败 3 次触发战略暂停；**绝不谎报成功**——无法亲眼确认的结果措辞只能是"请你确认"；归因顺序：我的假设 > 我的实现 > 环境 > 用户操作。

### 3.3 防回归与设计原则（`regression-safe-change-discipline.md` + `software-design-principles.md`）
改动落地前三问：改的符号被谁引用 / 哪些调用方该变、哪些必须不变 / 改法能否做到"目标变、无辜不变"。共享代码默认"加法、默认值、门控"，禁止为一个调用方改共享函数的无条件行为。先用 SRP/CQS/机制策略分离/依赖倒置/KISS 等公认原则审方案，不现攒规则。每个改动列出"必须逐字节不变"清单并有守护手段（PBT/单测/显式论证）。

### 3.3.1 跨 bug 回归预防（`bug-cross-hopping-prevention.md`，治"来回修"）
核心痛点：修 A 时动了共享代码 → B 修好了 → A 又回来，来来回回没完没了。根因是**每次只按症状堵出口，不问两个 bug 是否共享同一个根因**（`regression-safe-change-discipline.md` §八 trace-to-root）。三个拦截点：①需求/方案提交时做影响半径调研；②实施前 PreToolUse HOOK 强制 trace-to-root 三问（改的符号被谁引用 / 有没有其他已知 bug 共享这符号 / 无辜调用方会不会受波及）；③收尾时 PostToolUse HOOK 强制跨 bug 回归扫描。**额外硬阻断**：`gate-trace-hardblock.json`（command 类型，exit 2）在触及已知高风险共享根因（`MoveForwardTask.MoveForwardAsync`、`TpTaskFastDrag.Tp`、`AutoHoeingConfig`、`HandleTaskStart` 等 15 个符号）时直接阻止工具执行，防止"走过场自审后照样修补"。trace-to-root 相关 hook 文件：`.kiro/hooks/pre-edit-review.json`（agent 软约束）+ `post-edit-check.json`（PostToolUse 轻量校验 + 跨 bug 回归）+ `gate-trace-hardblock.json`（command 硬阻断）e 收尾校验）+ `gate-trace-hardblock.json`（command 硬阻断）。

### 3.4 UI 布局（`ui-layout-debugging-discipline.md`）
"多次修改后画面零变化"= 立即停止猜测（改动没生效或改错层级）；推理与现实矛盾时错的一定是自己的前提；上可见证据（涂色分层/打印 ActualWidth/唯一标记验证编译生效）；从外往内查容器链；记住 StackPanel 不拉伸子元素、Grid 默认拉伸、Stretch+显式 Width 会居中；换方案前撤干净上一版实验代码。

### 3.5 任务执行与委派（`task-execution-discipline.md` + `parallel-task-execution.md` + `spec-quality-checklist.md`）
- 任务拆解用 todo_list 跟踪（同时只允许一个 in_progress）；可并行批次要求：文件不相交、无数据依赖、prompt 自包含、验证隔离，否则串行；编译/测试/全局扫描必须串行。
- 委派 subagent 的 prompt 必含：任务编号+参考文档路径、目标文件+行号+当前内容、改动前→改动后片段、明确"不要碰"清单、验证步骤。subagent 失败立即停止并报告，同一 prompt 失败 2 次先改 prompt。
- 静默 catch 是禁忌（要带 LogWarning 和注释说明理由）。
- 不夸大报告：编译过=如实说编译范围；测试没跑过不写"全部通过"。
- 本项目 BGI 联机代码模式（决策纯函数、SignalR 先订阅后 invoke、多世界轮换防误终止双标志、AutoHoeingConfig 字段三处对称、协议字段新旧兼容）见 `bgi-implementation-patterns-v2.md`（新经验默认）与 `bgi-implementation-patterns.md`（旧，已冻结为历史存档）；写 spec 时按 `spec-quality-checklist.md` 逐项过。

### 3.6 测试项目阻塞隔离（`task-execution-discipline.md` §六）
仓库测试项目经常被其他进行中任务的未实现类型阻塞编译。处理：先判定归属（grep 确认报错类型在生产代码零命中）→ 主项目 `dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug` 验证本任务生产代码 → 本任务测试文件用编译诊断验证 → **不擅自删他人测试文件** → 报告事实而不阻塞推进。

### 3.7 记忆沉淀（`memory-sedimentation-discipline.md`）
每个非平凡任务收尾时必须做一次记忆回顾（不靠用户提醒）：本次有没有"项目特有、可复用、不沉淀就要重复付代价"的事实/模式？有 → 立即写入对应记忆文件。**写入前必须 readFile 目标文件确认当前结构，而不是凭记忆盲写。**（代码模式→`bgi-implementation-patterns-v2.md`，任务经验→`project-experience.md`，新纪律→`.agents/rules/` + 本文件 §3 索引）；没有 → 必须说清为什么没有。该沉淀=项目特有事实/重复模式/高代价雷区；不沉淀=公认原则重复/任务特定细节/未验证推测。

### 3.8 回合内连续执行（`round-continuity-discipline.md`，治"写一段就停"）
写多章节文档 / 大文件 / 一组相关改动时，**必须在一个回合内连续发完全部工具调用**（`fs_write` 头部 + 多个 `fs_append` 各章节连续追加），不要"写一段→回一句→等下一轮"把交付物切碎。补充：`large-file-write-strategy.md` 的"分段写入"指分段**追加**（意义单元），不是分**回合**。
**HOOK 自动响应（不减弱任何 HOOK 约束）**：每轮注入的 HOOK（统一质量审核/收尾校验）只是指令文本，不是回合终点，也不禁止模型在同一回合继续调工具。模型必须"**内部判断 + 直接继续**"：判断阶段放内部推理，判定"中途/可跳过"时**不输出任何用户可见文字**，直接在同一回合内继续发下一个工具调用；**禁止输出"我判断是中途、跳过 HOOK、继续执行"这类自我叙述**——一旦输出即截断本可连续的后续工具调用（回合终结器）。只有真正收尾或真正提交需求/设计时才需要输出文字。

### 3.9 回合执行与文件编码纪律（`prompt-response-and-encoding-discipline.md`，防"假成功/空转/乱码"）
**假成功/空转**：声称"已改/已落地"前必须用真实字节校验（read_file/grep/byte），工具说不出来≠已落地；确认要改的事务必须在**同一回合连发完**（替换+校验），不中途停下或输出大段文字截断后续调用；连续失败 2 次换维度不重复试。**编码**：`.cs`（尤其 `AutoFightOfficial/*.cs`）是 **UTF-8 带 BOM**，写入必须保编码；**禁止 `Encoding.Default`(GBK) 覆盖写**（会把中文全打乱码，本仓库 2026-08-24 同型事故）；优先 `str_replace`（字节安全），必须用 PowerShell 时显式 UTF-8 无 BOM 化读写+保留 BOM；发现乱码/中文截断立即 `git restore` 回滚，绝不续编辑。结束前至少做一次 diff/grep/编译自检。

## 4. 工具映射（操作意图 → 本环境真实工具名）

> ⚠️ **最高优先级（工具异常处理纪律）**：工具报错/不存在时怎么救，见 `.agents/rules/tool-availability.md`（异常处理流程 + 真实工具清单 + execute_pwsh 可靠写法）。
> 关键事实：**本环境没有 `fs_write` / `fs_append` / `str_replace` / `edit` 等任何专用文件编辑工具**，写文件/改文件只能靠 `execute_pwsh`（PowerShell）。
> 规则文档里这些名字都是历史/抽象别名。模型在工具列表里找不到时**不要停下清点**——按"操作意图"找等价工具：读→read_file / 搜→grep_search / 执行→execute_pwsh；写文件直接用 execute_pwsh 兜底。

| 操作意图 | 历史/别名（KIRO） | 本环境真实做法 |
|---------|------------------|----------------|
| 读文件 | readFile | `read_file` / `read_files` |
| 创建/覆盖文件 | fsWrite / write | **无专用工具 → execute_pwsh**（WriteAllLines） |
| 追加文件 | fsAppend | **无专用工具 → execute_pwsh**（Add-Content） |
| 替换文本 | strReplace / edit | **无专用工具 → execute_pwsh**（ReadAllText+Replace+WriteAllText） |
| 搜索文件内容 | grepSearch / grep | `grep_search` |
| 列目录 | listDirectory | `list_directory` |
| 执行命令/编译/测试 | pwsh / bash（getDiagnostics） | `execute_pwsh` |
| 委派子代理 | subagent | `invoke_sub_agent` |
| 任务状态机 | todo_write | `todo_list` |

**工具异常处理纪律 + execute_pwsh 可靠写法见 `.agents/rules/tool-availability.md`**（所有会话注入）。

## 5. 历史经验导航（动手前先查）

### 5.1 数据源索引
- 任务索引：`.agents/memory/kiro-task-index.md`（223 条任务：最后活动时间、任务数、执行状态、是否有 spec 档案）
- 项目经验沉淀：`.agents/memory/project-experience.md`（公版赶路优选、联机锄地血条阈值、公版战斗UI对齐、公版规范化状态、记忆沉淀覆盖缺口等）
- 工程实现模式：`.agents/rules/bgi-implementation-patterns-v2.md`（新经验默认优先；历史既有模式见 `.agents/rules/bgi-implementation-patterns.md`，已冻结为历史存档）
- 完整设计文档：`.kiro/specs/<任务名>/`（requirements / design / tasks / bugfix）
- 任务执行细节：`C:\Users\Administrator\.kiro\tasks\ada854181d8b03f7\<任务名>.meta.json`

### 5.2 自定义代理
- 公版合并助手（`public-merge-assistant`）：将上游公版改动安全合并到当前分支，遵循三阶段自审与防回归纪律。配置在 `.kiro/agents/public-merge-assistant.json`
- 项目知识检索（`project-knowledge-retriever`）：只读检索 BGI 项目历史经验、规则、spec、记忆档案与代码结构。配置在 `.kiro/agents/project-knowledge-retriever.json`

### 5.3 使用建议
新任务开始前，先调 `project-knowledge-retriever` 子代理检索相关经验，再 grep 历史索引和 specs，看是否做过类似功能、当时的决策和踩坑记录，避免重蹈覆辙。
