# ev2 集合定义逐字摘录（第 2 轮重要项 2 处置、第 3 轮重要项 1 补机械核验锚；2026-09-25）

源文件：`Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/TaskTakeoverIncidentTests.cs`
（1373 行；本批未修改、git status 干净；以下内容与工作区文件逐字节一致）

## 机械核验锚（任何审计者可就地复核）

| 项 | 值 |
|---|---|
| 字节数 | 73558 |
| SHA-256 | `5c68165da915c5816f4d98dd73b89582d26f598080c60c334004f0cc6a81d883` |
| git blob（`git hash-object <file>`） | `0c8f67889cab30d59fca608554ff909ae36ccb42` |
| 与 HEAD blob 一致 | 是（`git rev-parse HEAD:<path>` 同值；文件无修改条目） |
| 定义行号 | 15（CollectionDefinition） |

〔如实登记〕第 2 轮处置原文写「定义全文随材料快照送审」，实际第 3 轮材料只送了摘录——
偏差原因是全文快照使材料达 292K 字符、kimi 上游连续 504/EOF；本文件补哈希锚后改为
「摘录＋机械核验锚」口径：审计者用上表任一哈希对工作区文件复核即等价于见到定义全文。
（若第 4 轮材料体量允许，可再行全文快照。）

```csharp
[CollectionDefinition("TaskTakeoverIncident", DisableParallelization = true)]
public sealed class TaskTakeoverIncidentCollection;

// Real handlers, gate and lifecycle; only in-memory config is substituted.
// No application dispatcher, named pipe, screenshots, game or user-directory writes.
[Collection("TaskTakeoverIncident")]
public sealed class TaskTakeoverIncidentTests : IDisposable
```

⇒ `DisableParallelization = true` 在**定义层**成立（xunit 2.5.3 语义：该集合内所有类两两串行）。
挂入该集合的四个类＝JobRegistry.Instance 单例写者全集（第 3 轮重要项 2 的 v2 扫描补入
CoordinatedTaskQueueTests；见 ev2_singleton_writers_scan.md 的逐文件定性表）。
