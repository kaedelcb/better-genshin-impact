# R5.6 引用写入/激活接线批 — 只读独立核查报告（子 Agent 交付原文）

- 工作目录：`E:\Program Files\better-genshin-impact-LCB`
- 分支：`main-OldTeaBag-B168`；固定 ref = HEAD = `22ccd6ee2721abb1642535253997c57dbf9020d6`（复核命令 `git rev-parse HEAD`，开工与收尾两次一致）
- 只读手段：`git show <ref>:<path>`、`git grep <ref>`、`Select-String`、`Get-Content`；未运行构建/测试/dotnet/git 写操作，未改动任何文件（收尾 `git status --porcelain --untracked-files=no` 仍只有开工时既存的两个 Docs 修改）

---

## Q1（跨程序集可达性）

**结论：助手代码无法直接调用 `OneDragonConfigReferenceService.RenameGroupReferences/DeleteGroupReferences`——两条独立理由都成立：(a) 助手程序集/工程对 `BetterGenshinImpact`（AssemblyName = `BetterGI`）没有任何程序集引用或 ProjectReference；(b) 该服务在固定 ref 上声明为 `internal static class`，且 BGI 的 `InternalsVisibleTo` 只授予 `BetterGenshinImpact.UnitTest`。已核实。**

证据：

1) 助手源码 `using BetterGenshinImpact` 命中数为 0

```
git grep -c "using BetterGenshinImpact" -- "MultiplayerHoeingAssistant/*.cs" "MultiplayerHoeingAssistant/**/*.cs"
→ 无输出（git grep 退出码非 0 = 无匹配文件）
git grep -c "BetterGenshinImpact\." -- "MultiplayerHoeingAssistant/*.cs" "MultiplayerHoeingAssistant/**/*.cs"
→ 无输出（同上；即连全限定写法也没有）
```

2) 助手工程文件不含对 BGI 的程序集/项目引用

```
git show 22ccd6ee2721abb1642535253997c57dbf9020d6:MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj
→ <OutputType>WinExe</OutputType> / <TargetFramework>net8.0-windows</TargetFramework>
→ 仅 4 个 <PackageReference>（SignalR.Client / Hardcodet.NotifyIcon.Wpf / System.Drawing.Common / SharpCompress）
→ 无 <ProjectReference>，无 <Reference Include=...>，无 <Import>
```

该 csproj 中唯一出现 `BetterGenshinImpact` 的两处是部署复制目标路径，不是引用：

```
MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj:67:  DestinationFiles="@(AssistOut->'$(MSBuildProjectDirectory)\..\BetterGenshinImpact\bin\$(Configuration)\net8.0-windows10.0.22621.0\Tools\MultiplayerHoeingAssistant\...')"
MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj:71:  DestinationFiles="@(AssistOut->'$(MSBuildProjectDirectory)\..\BetterGenshinImpact\bin\x64\$(Configuration)\net8.0-windows10.0.22621.0\Tools\MultiplayerHoeingAssistant\...')"
（所在 Target：DeployAssistantToBgiTools AfterTargets="Build"）
```

补充核查：仓库根无 `Directory.Build.props/.targets` 被跟踪，故不存在通过 props 隐式注入 BGI 引用的路径。助手单测工程也只引用助手自身：

```
Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj
→ <ProjectReference Include="..\..\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.csproj" />
```

3) 服务可见性 = internal

```
git show 22ccd6ee...:BetterGenshinImpact/Service/OneDragon/OneDragonConfigReferenceService.cs
10: namespace BetterGenshinImpact.Service.OneDragon;
26: internal static class OneDragonConfigReferenceService
67:     public static ChangeReport RenameGroupReferences(string oneDragonDirectory, string oldName, string newName)
69:     public static ChangeReport DeleteGroupReferences(string oneDragonDirectory, string groupName)
```

4) BGI 侧 InternalsVisibleTo 不含助手

```
git grep -n "InternalsVisibleTo" 22ccd6ee... -- "BetterGenshinImpact/**"
→ BetterGenshinImpact/AssemblyInfo.cs:10/12  [assembly: InternalsVisibleTo("BetterGenshinImpact.UnitTest")]
git show 22ccd6ee...:BetterGenshinImpact/BetterGenshinImpact.csproj | Select-String "AssemblyName"
→ <AssemblyName>BetterGI</AssemblyName>
```

据此判断（由已核实的两条事实推出）：助手编译期根本没有 BGI 类型可用（无引用）；即使补上引用，`internal` + 无 IVT 也会在编译期拒绝。

---

## Q2（写方与消费方）

### (a) 真实写入 `User/OneDragon/*.json` 与 `*.flow.json` 的生产代码位置

**结论：`User\OneDragon\*.json` 有 3 组生产写方（BGI 侧：`OneDragonFlowViewModel` 全量重写/删除、`TaskConfigurationContract.ApplyEnabledAsync` 经外部接口、`OneDragonConfigReferenceService` 引用改写）；`*.flow.json` 的生产写方只有助手 `WorkflowStore.Save`，且其目录是 `%APPDATA%/NexusBGI/flows`，不是 `User/OneDragon`。已核实。**

(a-1) BGI 一条龙配置目录常量与全量重写

```
BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs:38  public static readonly string OneDragonFlowConfigFolder = Global.Absolute(@"User\OneDragon");
:539 public bool WriteConfig(...) → :559 File.WriteAllText(filePath, json)
:465 SaveConfig() → :482 WriteConfig(SelectedConfig)
:1159 DeleteConfig() → :1193 File.Delete(configFile)
:1237 RenameConfig() → :1293 WriteConfig + :1310 File.Delete(oldConfigFile)
View/Pages/OneDragonFlowPage.xaml.cs:129 ViewModel.SaveConfig();
```

(a-2) 外部接口/单测共享的配置写回合同（可写 `User/OneDragon`）

```
BetterGenshinImpact/Service/Execution/TaskConfigurationContract.cs:29 Path.Combine(userDirectory, oneDragon ? "OneDragon" : "ScriptGroup", name + ".json")
:63 ApplyEnabledAsync → :104 File.WriteAllTextAsync(temporary, ...) + :111 File.Move(temporary, path, true)
生产调用点：BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceConfigurationPlane.cs:60 store.ApplyEnabledAsync(...)
（对 oneDragon=true 的文件若判型非 PublicCurrent 会以 configuration_pending_migration 拒写）
```

(a-3) 引用改写服务（批量改写 `User/OneDragon/*.json` 中的组名引用）

```
BetterGenshinImpact/Service/OneDragon/OneDragonConfigReferenceService.cs:67/69 Rename/DeleteGroupReferences
:145 temporary = file + ".refsvc-" + Guid + ".tmp"; :148 File.WriteAllText(temporary, ...); :149 File.Move(temporary, file, true)
生产调用点：BetterGenshinImpact/ViewModel/Pages/ScriptControlViewModel.cs:796-797（重命名）、:875-876（删除）
```

(a-4) 助手流程定义 `*.flow.json`

```
WorkflowStore.cs:78-79 DefaultFlowsDir() = %APPDATA%/NexusBGI/flows；:88 PathFor；:129 Save；:165 File.WriteAllBytes(tmp)；:168 File.Move(tmp, file, true)
调用链：TaskCenterHost.cs:117 new WorkflowStore(flowsDir) → :189-202 SaveFlow ← TaskCenterPanelViewModel.cs:109/207
目录来源：MainViewModel.BgiExternal.cs:38 WorkflowStore.DefaultFlowsDir()（与 User\OneDragon 无关）
```

(a-5) 演练/工具类写入（写独立临时根或候选输出目录，非生产）

```
MigrationRehearsal.cs:108/110/141（独立根内置基线文件与提交后变更）
Test/OneDragonMigration/Core/OneDragonMigrationEngine.cs:556/562（候选输出目录）
```

### (b) 读取或判定 `activation.status` / `candidate-ready` 的消费点

**结论：生产消费点共 7 处（全部为“读取判定 + 拒绝/降级”，无一处赋值）；测试消费点集中在 4 个测试文件。已核实。**

| 位置 | 作用 |
|---|---|
| `WorkflowStore.cs:257`（配合 `:25 string? ActivationStatus`） | 判型时读出 `doc.Activation?.Status` 填入目录条目 |
| `TaskCenterHost.cs:195-198`（`SaveFlow`） | candidate-ready ⇒ 抛异常禁写 |
| `TaskCenterHost.cs:248-249`（启动） | candidate-ready ⇒ `Unavailable` 禁启动 |
| `TaskCenterHost.cs:907-909`（移交启动） | candidate-ready ⇒ `Rejected(FlowUnavailable)` |
| `TaskCenterHost.cs:1068-1070`（移交恢复） | candidate-ready ⇒ `Rejected(FlowUnavailable)` |
| `WorkflowPlanner.cs:115-116` | candidate-ready ⇒ 加入 `blocking` 理由 |
| `TaskCenterPanelViewModel.cs:175-177`、`:407`（`IsCandidate`）、`EditParts.cs:491-492` | UI 只读预览/禁编辑 |

```
TaskCenterHost.cs:249      return HostActionResult.Unavailable("candidate-ready 候选流程为只读预览，禁止启动（激活归 R5 专用入口）");
WorkflowPlanner.cs:116      blocking.Add("迁移候选（candidate-ready）只可预览，正式激活由 R5 事务迁移完成（D13）");
TaskCenterPanelViewModel.cs:407  public bool IsCandidate => string.Equals(_entry.ActivationStatus, "candidate-ready", StringComparison.Ordinal);
```

测试/工具代码：`TaskCenterPanelViewModelTests.cs:118,133,330,348,426-440`；`WorkflowStoreTests.cs:71,102`；`WorkflowPlannerTests.cs:205,209`；`StartupHandoffHostTests.cs:524`；`Test/OneDragonMigration/Core/OneDragonMigrationEngine.cs:551-553,595-598`（候选取值侧，写 `candidate-ready`）。

### (c) 是否存在把 `activation.status` 从 `candidate-ready` 改写成 `active` 的生产路径

**结论：在所查范围内未发现任何此类生产代码路径。已核实执行过的查询如下；生产代码甚至从不构造 `WorkflowActivation` 对象。**

```
① git grep -n '"active"' 22ccd6ee... -- '*.cs' → 生产侧仅作业状态串、推送 level 与 UI 展示串，其余全部在 Test/
② git grep -n 'Activation\s*=\|Activation?\.Status\s*=' 22ccd6ee... -- '*.cs' → 赋值语句全部位于 Test/ 下；生产目录 0 命中
③ git grep -n 'new WorkflowActivation' 22ccd6ee... -- '*.cs' → 排除 Test/ 后 0 行输出（生产代码从不实例化 activation）
④ git grep -n '"status"\s*:\s*"active"' → 仅测试夹具 JSON 与批次台账
⑤ git grep -n '"candidate-ready"|"blocked"' -- 助手/BGI 生产源码 → 8 处全为读取判定或 UI 文案，无写入
⑥ git grep -n '\.Import\(\|_workflows\.\|SaveFlow' → *.flow.json 的唯一写入口仍是 WorkflowStore.Save
```

需要如实标注的一点（已核实、非“存在提升路径”）：`WorkflowStore.Save` 会把传入文档**原样序列化**，因此若调用方给出的文档带 `activation.status = "active"`，盘上就会是 `active`（`Import` 亦同）；但**没有任何生产代码给 `Activation` 赋值**（见 ②③），故不存在从 `candidate-ready` 改写为 `active` 的生产路径。

---

## Q3（精确写集与回滚归属）

**结论：`ChangedFiles` 在回滚路径中只驱动“删除本事务登记为 Added 的文件”（并参与演练范围哈希与演练后校验）；恢复 Modified/Deleted 的旧字节并不按 `ChangedFiles` 逐条进行，而是整份基线快照覆盖（`m.FileHashes`）。事务类内部没有任何把新内容写进 configRoot 的步骤。已核实。**

```
:26-27   public enum ChangeKind { Added = 0, Modified = 1, Deleted = 2 }   // 变更归属（回滚据此判定「本事务新增」）
:418-493 RecordChanges(...)  // 基线校验 + 拓扑约束后写 m.ChangedFiles
:607     Rollback() → :635-639 先落 RollingBack/清标记 → :641 VerifySnapshot → :642 CompleteRollback
:646     CompleteRollback → :653 RestoreFromSnapshot(m, _configRoot) → :654 DeleteRecordedAdditions(>0 ⇒ rollback_cleanup_incomplete)
:915     RestoreFromSnapshot → :917 foreach (var rel in m.FileHashes.Keys)（**迭代基线快照清单，不是 ChangedFiles**）
:935     DeleteRecordedAdditions → :938 foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))（唯一使用 ChangedFiles 的回滚动作）
:874-880 RehearsalScopeOf（范围哈希）、:884 ApplyRepresentativeChanges（写演练副本 root）
```

事务类全部文件写动作：`:389 TakeSnapshot`（configRoot → 快照副本）、`:910/:911 WriteManifest`、`:924 RestoreFromSnapshot`（快照旧字节 → targetRoot）、`:944 DeleteRecordedAdditions`、`:891/:894/:897 ApplyRepresentativeChanges`（**只写演练副本**，唯一调用点 `:547`，root = `_transactionRoot/rehearsal-<session>`）。**在所查范围内未发现事务内写新内容到 configRoot 的步骤。**

事务类全部实例化点：`MigrationRehearsal.cs:114`（演练/独立根）、`MainViewModel.BgiExternal.cs:147,153`（仅静态路径校验）、`R56MigrationSwitchTransactionTests.cs`（夹具）。“提交后制造真实变更”的新内容写入发生在夹具/演练（`MigrationRehearsal.cs:141`），不在事务内部。

---

## 不确定项与查询边界

1. 核查对象是固定 ref 的跟踪内容；工作区**未跟踪**的 `MigrationReferenceActivation.cs`（当时为 183 字节开工占位）与 `_workflow/r56-reference-activation-wiring-2026-09-29/` 等未跟踪物**不在固定 ref 内**，未纳入 Q1–Q3 结论。
2. Q2(a) 基于路径常量与文件写 API 的关键字检索；不排除经反射、动态委托或未覆盖关键字外的第三方库间接写盘——**在所查范围内未发现**（未验证“不可能存在”）。
3. Q2(a) 中 `TaskConfigurationContract.ApplyEnabledAsync` 的 `oneDragon=true` 分支存在生产可达调用，但本轮只核对调用点与写入代码，**未运行**该路径。
4. Q2(b)(c) 的 `activation` 检索限定于助手与 BGI 的 `*.cs`；若 BGI 侧存在以字符串字面量拼装 JSON 的其他消费点，本轮未发现。
5. Q3 的“事务内无新内容写入”限定于 `MigrationSwitchTransaction.cs` 这一个类的代码路径。
6. 未执行任何构建、测试、运行验证；上述结论均为静态只读代码证据，不含运行时/实机证据。
