# IPC 09:00 事故回归

运行：

```powershell
dotnet run --project Test/IpcIncidentAudit/IpcIncidentAudit.csproj
```

需要 .NET 8 SDK。直接链接当前生产源，Newtonsoft.Json 使用该 SDK 自带程序集；无产品引用、无自动部署、不启动游戏。

当前 Program.cs 是正确行为回归：35 项通过，直接链接 10 份生产源。PASS 表示这些受控边界上的断言成立，不代表游戏/真实 IPC 已验收。

BoundaryStubs 仅替代 UI、游戏、DI 和事件边界。覆盖父子执行权、空闲历史不恢复、票据隔离、取消/失败汇总、手动停止、强身份去重和未知结果不重放。没有加载真实 WPF 一条龙 VM 或真实命名管道。

新增边界回归：25 轮、每轮 32 个并发根流程竞争只能准入一个；租约过期触发取消且原根退出前不得重叠准入；丢失应答按原请求键有限重试，耗尽后报告结果未知。租约测试只修改本测试进程的内存时间字段，等待其定时器触发，不修改系统时间。

隔离范围：这是同机独立代码测试进程，不是虚拟机，也不启动另一套 BGI。UI、输入、游戏与传输替身不执行外部操作；不连接、重启或部署前台 BGI/助手，不读写其运行配置。2026-09-17 使用 `dotnet run --project Test/IpcIncidentAudit/IpcIncidentAudit.csproj --no-restore -p:DeployToBgiTools=false` 验证，退出码 0，35/35 通过。真实管道 ACL、真实游戏及多端联调仍未由该测试验证。

修复前的 18 项刻画源码另存于 BeforeRepairProgram.cs.txt，保留其“PASS = 复现缺陷/对照”的历史含义，不混入当前回归。

真实生产 handler 的 7 项测试在 ../BetterGenshinImpact.UnitTest/ServiceTests/Instance/TaskTakeoverIncidentTests.cs，使用内存配置，不启动游戏或写用户配置。完整服务层测试总数为 71。

本地 baseline/ 是 HEAD 的只读导出构建快照；*-output/、test-results/ 和日志是本机验证产物，均被本目录 .gitignore 排除。未部署运行目录。

完整修复合同、证据范围及待验收矩阵见 [事故审查](../../Docs/design/ipc-0900-incident-audit-and-repair-2026-09-17.md)。
