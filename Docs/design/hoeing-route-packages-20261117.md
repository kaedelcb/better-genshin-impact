# 联机锄地线路整包管理与更新（2026-11-17）

状态：三端实现与自动回归已完成，主要真实浏览器/API 路径已验证；截图工具超时，指定独立审查工具不可用，尚未部署，不能据此宣称整版交付。服务端 679/679、客户端定向 107/107、网页 25/25、分发拦截 3/3 通过；历史失败保留，验证范围与未完成项见[开发验证记录](../technical/hoeing-route-packages-validation-20261117.md)。用户授权：全服监控新增线路管理；选择文件夹后在浏览器本地统一打包；上传/下载均为单 ZIP；完整替换官方受管线路；版本发布/回滚；替换全部第三方更新器入口和分发。强制新版，不做旧工具兼容或回退。不得覆盖用户数据、自定义线路或第三方 JS。

## 并行写入所有权

- 服务端实施者：`BgiCoordinatorServer/RoutePackages/**`（可链接编译的纯 .NET 协议/ZIP 校验源）、`BgiCoordinatorServer/Services/RoutePackage*`、`Program.cs`、`Services/AdminMonitorAccess.cs`、`Services/RoomOperations*.cs` 中新功能的最小接线、服务端 `Models/RoomConfig.cs`、部署文件、服务端新 C# 测试。不得编辑 wwwroot 或 BGI 客户端。
- 客户端实施者：`BetterGenshinImpact/**` 中线路更新、路由解析及任务接线，含 csproj；`Test/BetterGenshinImpact.UnitTest/**` 中相关测试。不得编辑已有用户 `Properties/launchSettings.json`。不得新建根 Assets，不改第三方 JS。
- 网页实施者：`BgiCoordinatorServer/wwwroot/**` 中线路页/打包 Worker/导航；`BgiCoordinatorServer.Tests/Web/**` 中新网页验证及预览入口。不得编辑服务端 C# 或现有监控脚本业务。
- 分发实施者：`.github/workflows/publish.yml`、`tools/local-build/build.py`、当前使用教程和新的运维文档。不得删除真实用户安装目录或第三方输入。
- 主线程集中构建与综合验证；各写者不得同时运行构建、生成快照、创建 worktree、部署、提交或推送。

## 共享协议（camelCase JSON，schemaVersion=1）

纯共享源码存于 `BgiCoordinatorServer/RoutePackages/`，命名空间 `NexusBGI.RoutePackages`。只依赖 .NET 8 BCL。客户端 csproj 将该目录 `*.cs` 链接编译；服务端保持本目录独立 Docker 构建能力。不要把 ASP.NET/服务端类放进这个共享目录。

固定类型：

- `RoutePackageFile`：`string Path`（正斜线相对路径）、`long SizeBytes`、`string Sha256`。
- `RoutePackageDescriptor`：`int SchemaVersion=1`、`string Id`（32 位小写 hex GUID）、`string Sha256`（64 位小写 hex）、`long SizeBytes`、`DateTimeOffset CreatedAtUtc`、`string Description`、`string RequiredClientVersion`、`int FileCount`、`int RouteCount`、`List<RoutePackageFile> Files`、`string DownloadUrl`、`string State`（draft/published/archived）。
- `RoutePackagePublication`：`long Revision`（每次发布/回滚单调递增）、`RoutePackageDescriptor Package`。
- 可扩展额外字段，但不能改固定字段语义；上传者不指定散列/可信元数据，由服务端重算。

共享 API：`RoutePackageArchive.ValidateAsync(string zipPath, CancellationToken ct=default)` 返回 `Task<List<RoutePackageFile>>`；`RoutePackageArchive.ExtractAsync(string zipPath,string destination,CancellationToken ct=default)` 返回 Task，目标须是新建独占目录；`RoutePackageArchive.IsValidId(string?)` 返回 bool。同时暴露 `MaxArchiveBytes`、`MaxExpandedBytes`、`MaxFiles` 常量，初始分别 128 MiB/512 MiB/10000。校验实际读取字节，不只信 ZIP 头。客户端必须核服务端完整清单，不能只信本地版本标签。

ZIP 布局：`builtin/<线路目录名>/**` 必需，包含至少一个有效线路 JSON；可选 `pathing/**`（普通选线用官方线路）；允许线路目录下 `control.json5`，只允许包内相对 ref，检查循环；禁止可执行文件、JS、任意用户配置/账户数据。JSON 元数据不放在被扫描的线路目录中。保留中文、变体目录和原文件名。ZIP 允许 UTF-8 STORE/DEFLATE，统一拒绝越界/绝对路径/反斜线/盘符/ADS/保留名/大小写碰撞/链接/加密和炸弹；解析 JSON 时有限深度，检查非空 positions 及基本类型，检查重复同步点；校验控制合并与变体依赖。不要把线路校验当作游戏实跑验证。

## HTTP API

公共（HTTPS；loopback 开发除外），均有界：

- GET `/api/hoeing-routes/current` -> RoutePackagePublication；未初始化 503。清单 no-store。
- GET `/api/hoeing-routes/packages/{id}` -> RoutePackageDescriptor（已发布/历史可用版本；草稿不能匿名读取）。
- GET|HEAD `/api/hoeing-routes/packages/{id}/download` -> 不可变 ZIP，支持 Range；校验 id，禁止任意路径。
- GET `/api/hoeing-routes/policy` -> `{schemaVersion:1,requiredClientVersion:string}`。

管理员页 `/admin/routes`，上传者页 `/routes/upload`。管理员沿用现有 cookie，不向上传者发管理员 cookie。上传者用独立 `ROUTE_UPLOAD_TOKEN`（请求头 `X-Route-Upload-Token`），仅允许上传草稿，不能读房间/日志、发布或回滚；首版可单独撤销更换 token，不要求完整用户中心。token 页面不得外链脚本/存储 token 到 localStorage。

管理 API：

- GET `/api/admin/routes` -> `{current:RoutePackagePublication|null, packages:RoutePackageDescriptor[], requiredClientVersion:string, storageBytes:long, maxStorageBytes:long}`。
- GET `/api/admin/routes/csrf` -> `{token:string}`；写请求携带 `X-CSRF-Token`（绑定当前 cookie，有效期/重启失效）。上传 token 路径无 cookie 权限，不允许靠 cookie 绕过 CSRF。
- POST `/api/admin/routes/upload?description=...`，Content-Type application/zip，body 为一个完整 ZIP（不用 multipart），管理员带 CSRF，上传者带独立 token；返回 RoutePackageDescriptor（草稿）。
- POST `/api/admin/routes/{id}/publish`，body `{expectedRevision:long,reason:string}` -> 新 publication。
- POST `/api/admin/routes/{id}/rollback`，body 同上；只允许历史已发布包 -> 新 publication。CAS 防并发覆盖，冲突 409。
- GET `/api/admin/routes/{id}` -> `{package:RoutePackageDescriptor,added:string[],modified:string[],removed:string[]}`（相对当前版）。
- GET `/api/admin/routes/{id}/download` -> 管理员下载草稿/历史包。
- POST `/api/admin/routes/{id}/restore`，body `{sourceId:string,paths:string[],expectedRevision:long,reason:string}` -> 从 sourceId 恢复所选文件/目录到 id 草稿的新副本，重打单 ZIP 并重新校验，原包不变；返回新草稿 descriptor。允许新草稿中删除路径的等价操作，但不能在线改已发布 ZIP。
- DELETE `/api/admin/routes/{id}`：只允许未发布且未引用草稿；历史已发布版采用显式归档/保留，不硬删正在使用/可能恢复的唯一版本。
- GET `/api/admin/routes/audit` -> 有界操作记录（不含密码/token）。

发布状态、元数据、审计和 ZIP 需持久化；同步序列化的 publication/catalog 替换作为提交点，崩溃不能误指向未完成包。独立 `ROUTE_PACKAGE_ROOT` 挂载，不放 wwwroot；`ROUTE_PACKAGE_MAX_BYTES` 限总存储。请求取消后临时包回收。长 IO 不占房间锁，不把 ZIP 载入 SignalR。

## 新版门槛及轮次锁定

`HOEING_REQUIRED_CLIENT_VERSION` 指定生产允许的精确完整 BGI 版本。生产 DI 始终注入版本/包策略；未配置时相关新联机入口 fail-closed，不能只比较队员间版本相同。版本为空、旧版、alpha 通配均不能绕过。现有其它房间/控制/日志业务不扩大拦截。独立新功能测试可构造有明确策略的 RoomOperations；原有无此依赖的纯业务测试不凭空等于生产接线。

客户端和服务端 RoomConfig 增加 `string RoutePackageId`（默认空），生产开锄新路径必须有可用且匹配的发布过的包。首次启动选择 current；随后同一轮/多世界/守护续跑沿用 pin，不因新发布追最新。房主在选线与预判前确定包，成员取得房主 pin 后即便已预更新也要确保该 pin。校验失败修复只重装该 pin。服务器拒绝在已冻结轮次中改 pin；重连/开锄要再验版本，不只入房验。

客户端已保存独立任务名称及配置键维持，不保留第三方更新器进程/日志协议、旧下载源、同路径兼容启动器或自动旧线路源回退。更新失败保全原完整目录，但不得假称目标包安装成功或把未知当作空世界。

## 客户端数据与运行边界

官方受管根建议 `Global.UserRoot/AutoHoeingRoutes`；正式内容 `current`（含 builtin/pathing），安装状态、备份、事务记录在其兄弟目录而非线路扫描目录。更新时下载/解压验证后才移动旧目录到 backup，再把新目录切入；两次 rename 不是单一原子操作，必须有持久化恢复记录。跨进程锁；正在执行线路时禁止替换。验证目标不含链接，拒绝路径逃逸；只清理自己创建且已确认归属的 staging/backup。

线路解析对联机官方线路使用受管目录；普通单机行为、自定义绝对路径、本地导入线路保留；不要把旧 Assets 中同名官方包静默当成新包。设置 UI 扫描/打开目录/变体扫描和执行路径必须一致；明确区分官方/本地来源。不写 records/settings/JS assets。已有缺脚本资源判定要与实际依赖匹配，不能让本来可用功能意外缺资源，不能缺必要元数据时假装正常。

## 验证与上线

代码验证统一由主线程经 validate_command.py/{artifacts} 或 Session.temporary_output 执行，执行前确认现有 DeployToBgiTools=false。网页预览必须使用 preview_start 等工具；隔离数据、loopback，不用真实 User 或生产管理密码，不部署公网。验证整包安装/修复/回滚、失败保全、取消、并发、路径安全、准入门槛、单机/自定义与现有管理监控回归。实际游戏效果不冒充已验证。

HTTPS 与新正式版本配置、持久卷、首个正常线路包为上线前置；本次不擅自部署、替换正在运行的产品、推送或开放公网。
