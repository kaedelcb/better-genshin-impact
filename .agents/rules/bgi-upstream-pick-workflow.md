# 公版优选工作流（自动执行）

## 触发条件

用户说"优选公版提交"、"优选公版"或类似含义时，**自动执行**本流程，无需用户额外指示。

## 前置检查

1. 定位目标文件：公版提交 diff 涉及的文件列表
2. 对每个文件，检查头部是否有以下标记：
   - `TeaBag Originals`（#region 标注的公版原始代码区）
   - `TeaBag Extensions`（#region 标注的茶包本地扩展区）
   - 基线 commit 注释（`基线上游 commit:`）

## 第一步（必须先做）：判断该文件走"整文件覆盖"还是"region-diff"

**通用原则：一个文件/一套功能"本来就是纯公版/纯上游、本地只做共存接入"时，优选/合并 = 整文件覆盖成目标上游版本 + 最小共存接入，绝不做逐方法 diff 适配。**

判断标准只有一个——**本地（茶包）代码是否混在同一个文件里**：

| 情况 | 本地代码位置 | 优选方式 |
|------|-------------|----------|
| **纯公版**：上游文件本身是纯的，本地共存代码在**独立文件/独立目录** | `TpTaskFastDrag.cs`、`TeapotHurryOnHelper.cs`、`AutoFightOfficial/` 整目录 | **整文件覆盖**（见下方"整文件覆盖流程"） |
| **混入茶包**：本地扩展代码**和公版代码在同一个文件**里（需要 #region 区分） | `SkillBoostHelper.cs`（若未分目录）、或确实同文件的场景 | region-diff（见下方） |

**判断方法**：先看本地是否有"与之配对的独立文件/目录"存着茶包实现；有 → 上游文件必是纯的 → 覆盖。只有当茶包代码确实挤在同一文件里、且历史靠 #region 隔离时，才用 region-diff。

> **经验教训（2026-08-31）**：`TpTaskOfficial.cs` 本是纯公版，其茶包版在独立文件 `TpTaskFastDrag.cs`。此前错把它当"已规范化需 region-diff"逐方法适配，绕了大圈。正确做法是整文件覆盖成目标公版 commit + 最小共存接入。**下次遇到"公版/茶包分文件共存"的功能（传送、赶路、战斗 AutoFightOfficial 等），一律先按此判断走覆盖。**

### 整文件覆盖流程（纯公版文件专用）

1. 确认目标公版 commit（用户要优选的提交）相对**当前文件实际对齐的上游版本**的净差异，用 `git show <commit>:<path>` 字节精确取目标版本。
2. **先回退当前文件到干净状态**（`git checkout -- <file>`），避免残留半成品。
3. 用目标公版版本**整体覆盖**该文件（逻辑一行不改）。
4. 只做**最小共存接入**（不改逻辑）：
   - 类名/构造名改名：若本工程已用 `TpTask` 等名字被分发器占用，把上游类名 `TpTask`→`TpTaskOfficial`、构造名、内部 `new TpTask(` 同步改。
   - 去重：若上游文件里定义了本工程**已独立成文件**的类型（如 `MapPositionNotRecognizedException`），删掉类定义（保留 throw/catch 用法，引用独立文件）。
   - 删死方法：上游新版本删掉的方法若被共存层（分发器 `TpTask.cs`）引用，先 `grepSearch` 确认**无外部调用方**后，从分发器删除这些死方法。
5. **不碰** 与之配对的本地文件（茶包版 `TpTaskFastDrag.cs` 等）一个字节。
6. 编译验证 + grepScan（见下方）。


## 流程

### 如果文件**未规范化**（没有 region 标记）

1. **先做规范化**：
   - 在文件头部添加注释块，标注基线 commit
   - 将茶包新增的方法/字段用 `#region TeaBag Extensions` 包裹
   - 将公版原始方法保持为 `#region TeaBag Originals`
   - **不改变任何逻辑行为**
2. 再执行优选（见下方）

### 如果文件**已规范化**（有 region 标记）

1. 读取文件头标注的基线 commit
2. `git diff <基线commit> <新commit> -- <文件>` 拿到公版自己的 diff
3. 将 diff 应用到 `#region TeaBag Originals` 区域
4. `#region TeaBag Extensions` 区域**不动**
5. 如果有共享配置/UI/I18n 文件的冲突，按需解决

### 编译验证

- `dotnet build BetterGenshinImpact/BetterGenshinImpact.csproj -c Debug`
- 0 error 为通过

### 提交

用户说"优选"时自动提交，格式：
```
fix: 优选公版<提交描述>(优选公版)
```

## 文件分类（公版/茶包共存，判断覆盖 vs region-diff）

| 文件/功能 | 茶包实现位置 | 类型 | 优选方式 | 备注 |
|-----------|-------------|------|----------|------|
| `TpTaskOfficial.cs`（公版传送） | `TpTaskFastDrag.cs`（独立文件） | 纯公版 | **整文件覆盖** | 2026-08-31 已用覆盖成功优选 #3488（commit 2cf96784） |
| `AutoFightOfficial/`（公版战斗，整目录） | `AutoFight/`（独立目录） | 纯公版 | **整文件覆盖** | — |
| `TeapotHurryOnHelper.cs`（茶包赶路） | 独立文件 | 茶包自身，公版是 `SkillBoostHelper.cs` | 公用 `SkillBoostHelper.cs` 走覆盖 | — |
| `SkillBoostHelper.cs` | 若茶包赶路逻辑在此文件内（未彻底分目录） | 可能混入 | 先确认是否可分目录再定 | 若茶包代码在内部 → region-diff |