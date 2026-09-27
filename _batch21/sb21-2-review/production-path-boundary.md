# SB21-2 LocalWaitQueueStore 生产路径锁边界证据

本批未修改生产构造链，只核对当前调用面以界定物理路径别名锁支持范围。

## 源码路径

- `MultiplayerHoeingAssistant/App.xaml.cs:98`：应用启动创建唯一 `_mainViewModel = new MainViewModel()`。
- `MultiplayerHoeingAssistant/ViewModels/MainViewModel.BgiExternal.cs:37`：MainViewModel 内通过 `_taskCenterHost ??= new TaskCenterHost(...)` 缓存宿主，并传入 `RunStore.DefaultRunsDir()`。
- `MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs:119`：宿主在同一 `runsDir` 构造一个 `LocalWaitQueueStore`。
- `MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs:59-60`：`DefaultRunsDir()` 为 `%APPDATA%/NexusBGI/runs`。

## 仓库调用点搜索

命令：`rg -n --glob '!Test/**' "new LocalWaitQueueStore|LocalWaitQueueStore\(" MultiplayerHoeingAssistant`

结果只有 Store 构造声明与 `TaskCenterHost.cs:119` 生产构造点；无第二处助手生产构造。MainViewModel 是 App 持有的唯一实例，TaskCenterHost 由其属性惰性缓存。

## 支持范围

Store 的进程锁键为 `Path.GetFullPath` 字符串并忽略大小写；没有解析目录 junction/symlink/其他物理别名。当前生产路径只有一个 Store 写者，因此队列内部的同进程 HWM 分配与 Cleanup 并发提交使用同一路径锁。未来增加第二个 Store 写者时须传入相同规范路径表示或保证单写者；物理路径别名不在并发保证内。跨进程写者仍按既有单写者合同处理。
