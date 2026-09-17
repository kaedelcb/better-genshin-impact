# 槲寄生 IPC 通道合同审查

## 修复后状态

用户授权实施后的最终集合 **52/52，连续 4 个新进程复跑通过**。末尾的 21 项 14/7 表格是修复前基线，不是当前结果。新增 T22–T52 覆盖在途合并、失败/未知回执、4096 条背压、期限、身份贯通、临时配置 CAS/单项选择/权限、SDK 异常帧，以及自建管道 ACL/双向传输。T12 明确使用受理前 `queue_full` 拒绝；执行失败不可重放由 T23 覆盖。

另有真实生产网页回执与目标解析函数的隔离测试 **10/10**（含全员广播展开为实际在线 UID）：

```powershell
node --check BgiCoordinatorServer/wwwroot/control-room.js
node Test/IpcChannelContractAudit/web-config-receipts.test.cjs
```

运行（需要 Windows、.NET 8 SDK）：

```powershell
dotnet run --project Test/IpcChannelContractAudit/IpcChannelContractAudit.csproj -p:RestoreSources= -p:NuGetAudit=false -p:DeployToBgiTools=false
```

后续可加 `--no-restore`。无 NuGet 包依赖；Newtonsoft.Json 引用 SDK 自带程序集。

## 安全与证据范围

本工程直接链接 15 份生产源码，包含帧协议/管道工厂、ext 协议/会话/控制/配置/查询、协调器、作业模型/注册表/请求账本、配置合同、抢占门及助手 SDK。没有产品 ProjectReference、部署目标或 WPF。

BoundaryStubs 替换执行 handler、DI、事件发送、执行槽，产品启动只增加计数，关闭游戏/产品配置/UI 方法直接拒绝。新配置测试把真实 helper 指向 `%TEMP%/bgi-channel-contract-<GUID>`，只读写并删除测试自建文件。多数帧测试使用 MemoryStream；T48 使用生产管道工厂创建 `Codex.IpcChannelAudit.<GUID>`，仅连接它，直接调用 SDK 编解码而不调用 StartAsync/SendCommandAsync。绝不读取运行 User、发现/连接 BGI、发键鼠或启动产品。

因此：

- T09–T11 测试真实会话层对任意写操作的幂等处理；不是在测试中真的修改配置或停止游戏。
- T16 测试真实协调器的等待超时清理。
- T18 测试真实会话/控制面在入队前的旧纪元校验，实际执行段为计数替身。真实 handler 的纪元检查范围另由源码审查确认。
- T19 是计划要求的作业身份结构合同，不是已有功能回归。
- T21 测试真实会话缓存、协调器和注册表组合；模拟已完成请求响应遗失、缓存压力后重试，执行仍是内存计数。
- T32–T42/T51 验证真实 helper 的临时文件写盘；T48 验证同用户管道 ACL/网络拒绝规则和真实双端帧。不验证跨用户会话矩阵、真实事件路由、WPF 执行器整链或游戏任务本体。独立测试不是安全沙箱，外部动作边界已显式限定为自建夹具。

## 2026-09-17 修复前基线（历史）

21 项：14 通过，7 失败。完整集合在三个独立进程运行中结果一致；每次审查程序退出码为 1。失败断言保留正确合同，没有改成“复现缺陷即通过”，也没有排除测试。

| 用例 | 结果 |
|---|---|
| T01–T08 | 通过：帧格式、UTF-8、分片、相邻帧、截断/超长/未知类型拒绝、v2 握手与纪元、跨会话顺序去重 |
| T09 | 失败：同 key 并发写请求实际调用两次写入边界 |
| T10 | 失败：相同 key 的不同请求参数收到旧成功响应 |
| T11 | 失败：相同 key 跨操作复用时，stop 收到旧配置写成功响应，未到 stop 边界 |
| T12–T15 | 通过：失败不缓存为成功、事件不投递仍可查终态、未知句柄、队列背压与排队取消 |
| T16 | 失败：等待槽位超时，终态项仍占队列容量 |
| T17 | 通过：在途启动请求按明确 key 采用同一队列项 |
| T18 | 失败：声明旧 bgiEpoch 的启动请求仍入队，模拟执行次数为 1 |
| T19 | 失败：BgiJob 缺 WorkflowRunId / NodeId / Iteration；查询面也未输出对应身份 |
| T20 | 通过：入队到作业快照保持明确 request key |
| T21 | 失败：130 个无副作用写请求使缓存淘汰后，原已完成请求再次模拟执行，总次数 2 |

已有 `../IpcIncidentAudit` 本轮另行复跑 35/35 通过；两个集合覆盖范围不同，不能互相替代。

完整原因、静态发现、修复顺序见 [通道合同专项审查](../../Docs/design/mistletoe-ipc-channel-contract-audit-2026-09-17.md)。
