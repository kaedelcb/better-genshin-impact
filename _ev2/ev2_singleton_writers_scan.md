# ev2 单例写者全集与并行配置核实 v2（第 3 轮重要项 2 处置；2026-09-25）

〔v2 更正〕v1 的写者全集扫描用「`JobRegistry.Instance` / `new BgiTaskCoordinator` /
`BgiTaskCoordinator.Instance`」两条 pattern——漏掉了 **target-typed `new(...)`** 形态
（`CoordinatedTaskQueueTests.Create() => new(...)` 不含 `new BgiTaskCoordinator` 子串），
且只覆盖直接引用与经协调器写入，未覆盖经其他生产中介（QueryPlane/InstanceRequestHandler/
TaskRunner/TaskTriggerDispatcher）到达单例的测试。本 v2 以裸类型名＋中介符号全集重扫。

## 扫描命令与输出（仓库根执行）

```
$ grep -rln "InstanceRequestHandler|ExternalInterfaceQueryPlane|ExternalInterfaceCommandPlane|TaskRunner|TaskTriggerDispatcher|BgiTaskCoordinator|JobRegistry.Instance" Test/BetterGenshinImpact.UnitTest --include="*.cs"
ServiceTests\Instance\BgiTaskCoordinatorTerminalSplitCharacterizationTests.cs
ServiceTests\Instance\BgiTaskCoordinatorTests.cs
ServiceTests\Instance\TaskSuspendOnlineSignalTests.cs
ServiceTests\Instance\CoordinatedTaskQueueTests.cs
ServiceTests\Instance\TaskTakeoverIncidentTests.cs
```

逐文件定性：

| 文件 | 触及符号 | 是否单例写者 | 处置 |
|---|---|---|---|
| BgiTaskCoordinatorTerminalSplitCharacterizationTests | BgiTaskCoordinator＋JobRegistry.Instance | 是（本批被测写者） | [Collection("TaskTakeoverIncident")]（已挂） |
| BgiTaskCoordinatorTests | BgiTaskCoordinator＋JobRegistry.Instance＋QueryPlane.TryDispatch | 是（第 1 轮已核实的间接写先例） | [Collection("TaskTakeoverIncident")]（第 1 轮已挂） |
| TaskTakeoverIncidentTests | BgiTaskCoordinator | 是 | [Collection("TaskTakeoverIncident")]（既有） |
| CoordinatedTaskQueueTests | BgiTaskCoordinator（**target-typed new**）＋.Submit | **是（v1 漏判，v2 补正）** | [Collection("TaskTakeoverIncident")]（**第 3 轮新挂**） |
| TaskSuspendOnlineSignalTests | InstanceRequestHandler.IsOnlineSignalTask（**静态纯名称分类**，无实例无注册表） | 否 | 不挂（逐一核查依据：该文件全部 5 处调用均为静态分类断言，见源文件 19-40 行） |

## 事实①：集合定义（定义层成立，R4）

```
$ grep -n "CollectionDefinition|Collection(" Test/.../TaskTakeoverIncidentTests.cs
15:[CollectionDefinition("TaskTakeoverIncident", DisableParallelization = true)]
20:[Collection("TaskTakeoverIncident")]
```

定义全文摘录＋SHA-256/git blob 双哈希锚见 `ev2_collection_definition_excerpt.md`。

## 并行配置事实

```
$ find Test/BetterGenshinImpact.UnitTest -maxdepth 2 -name "xunit.runner.json"   → 无
$ grep -rn "CollectionBehavior|DisableTestParallelization" --include="*.cs"      → 无程序集级禁并行
```

⇒ xunit 2.5.3 默认跨集合并行；约束机制＝四个单例写者类的共同集合成员资格
（DisableParallelization=true 在定义层生效 ⇒ 四类两两串行）。
JobRegistryTests 等自建实例的测试类与本集合并行，不共享状态，无淘汰/污染交错面。
