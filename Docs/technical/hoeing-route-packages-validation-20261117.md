# 联机锄地线路整包：开发验证记录（2026-11-17）

## 当前结论

三端实现、正式入口替换与旧工具分发拦截已落盘。服务端整套 679/679、客户端定向 107/107、网页 25/25、分发拦截 3/3 通过。**主要真实浏览器/API 路径已验证；截图工具两次超时，指定独立审查工具不可用；未做生产部署、真实 User 迁移或游戏内实跑，不能标记为整版交付。**

源码根为本仓库；验证基线 HEAD 为 `3788073b6bebf559f15ff8f32bc9e78cb39c3825`，测试针对该基线上的本次工作区实现，不等于只测试 HEAD。本次未将用户既有 `BetterGenshinImpact/Properties/launchSettings.json` 修改纳入实现。

## 自动回归与证据

验证由 `tools/mistletoe/validate_command.py` 分配独占 `{artifacts}`，使用已有 `DeployToBgiTools=false`，bin/obj、TEMP、TRX 与线路夹具均隔离。有效证据保留命令、退出码、日志和 TRX，可再生成二进制由 Session 收尾回收。

| 范围 | 实际结果 | 本仓库内证据 |
|---|---|---|
| 源码取入预检 | complete，issues 为空，当前 HEAD 在基线中 | 本次控制台；按分支外入口执行 |
| 旧工具分发拦截 | 3 通过；拒绝大小写/扩展名变体，保留本体及助手更新器，不自动删残留 | `Build/validation-runs/72c4ff87a8054cb1a7ed40d7a7f9ac62/records/` |
| BGI/助手构建与客户端定向测试 | 107/107 通过，无跳过 | `Build/validation-runs/061487b806ab453e9195b3f6f66b7789/artifacts/results/client/client-routes.trx` |
| 网页 Node 测试 | 25/25 通过，无失败/跳过/取消 | 同一次验证的 `records/validation-stdout.log` |
| 服务端整套回归（修复后） | 679/679 通过，0 失败、0 跳过，受控命令 exit 0 | `Build/validation-runs/5bc70e4a97084704a8b9a298e4653569/artifacts/results/server-routes.trx`、`records/result.json` |
| 差异与启动脚本静态检查 | `git diff --check`、PowerShell Parser 通过 | 本次控制台 |

客户端定向范围为 `AutoHoeingRoute`、`WorldPlanMemberNegotiationTests`、`WorldPlanStartBoundaryTests`、`WorldPlanPreviewTests`、`HoeingMissingResource`、`RouteDirectoryScanner`、`RoutePackage`。它覆盖原生安装、pin/封存、失败恢复、来源隔离与相关多世界边界，不代表全部 WPF/游戏行为。

存在既有编译/依赖警告；没有为了本任务修改无关依赖或删除测试求绿。

### 保留的真实失败

1. 首次集中验证 `061487b806ab453e9195b3f6f66b7789`：服务端 678 通过、1 失败；客户端和网页阶段 exit 0。外层存储结算另以 `storage retained+reserved budget exceeded` 失败。二者分别记账，不能只归因为存储，也不能把原记录改成成功。
2. 唯一服务端失败为 `ControlHoeingChecksReportedBgiVersionRatherThanAssistantHelloVersion`。根因：`RoomManager.GetCoordinatedStartPlayers` 的快照未复制 `BgiVersion`，正式批次准入恒读到空版本。已补字段并增加旧快照不变、新快照反映最新上报、旧版本明确拒绝及正确 BGI 版本可受理的断言；保留助手 hello 版本与 BGI 版本分离，不放宽精确版本策略。修复后整套 679 项通过。
3. 旧验证门闩命令有意终止，记录位于 `Build/validation-runs/97adceb0afa2462fa448fd22b5ba0e3f/records/`，原 exit 1 保留；不是产品回归失败。

## 真实浏览器/API 验证

使用 `.claude/launch.json` 的 `hoeing-route-packages-isolated` 与 `tools/hoeing-routes/preview-launch.ps1`，仅监听 `127.0.0.1:8951`，配置公开固定的本机测试凭据，线路库、房间日志、Cookie key、TEMP 和构建产物均指向当次隔离目录。不得用于生产或真实 User。

### 启动身份校正（保留无效试运行）

首次预览 `2a9b7e566c8a4ed993895d047ffa17e9` 使用 `.NET SDK 8.0.406` 的 `dotnet run --artifacts-path`，实际编译到隔离输出却执行默认 bin 的旧服务。实际请求中 `/health` 返回 200、新线路接口返回 404，且源 HTML 仍可直接访问；日志显示默认 DataProtection key 根，证明未执行本次新 Program 接线。**这些响应不是新版功能的验证证据，也不应据此修改新版 API。**

已通过该试运行自身 stop 标记停止其拥有的进程树；未强杀用户程序、未读取或清理默认密钥目录。启动脚本改为显式隔离 `dotnet build` 后 `dotnet <当次绝对 DLL 路径>`，缺失即拒绝启动，并在状态文件记录程序集路径。不再用 `dotnet run` 的默认启动解析。新版预览必须重新核验接口、鉴权、静态源 HTML 屏蔽和数据隔离，不能复用该旧程序结果。

### 已完成的实际入口断言

有效隔离试运行为 `c2d28923924d4cbfbd490a20270f76aa`，状态文件确认执行当次 `artifacts/server-build/bin/BgiCoordinatorServer/debug/BgiCoordinatorServer.dll`，而非默认 bin。日志确认认证密钥写入该次 `route-preview-data/authentication-keys`。测试版本 `0.0.0+route-preview` 不是生产版本。

- 首包未初始化 current 返回 503；policy 返回上述精确版本；匿名管理接口 401；`/admin-routes.html` 与 `/route-upload.html` 源 HTML 直访 404。
- 实际登录 → `/admin/hoeing` → 导航至 `/admin/routes`。原监控页面及管理员房间 API 正常，隔离库为零房间；未据此声称真实历史日志下载或有成员房间全部实测。
- 通过 File / DataTransfer 合成六文件 FileList，触发真实目录输入事件、真实 Worker 打包和单 ZIP 上传。包含中文、A 变体、变体说明、平铺 pathing 和跨目录 control 引用；选择和打包阶段未上传。未测试操作系统原生目录选择对话框。
- 上传令牌创建 V1 草稿，上传后输入框清空，localStorage/sessionStorage 均为空；草稿匿名读取及下载 404；令牌不能读房间/管理目录或发布（401）。管理员 Cookie 缺 CSRF 发布返回 403。管理员也通过页面上传 V2 草稿。
- UI 发布 V1 → revision 1；V2 差异为新增 1、修改 2、移除 1；UI 发布 V2 → revision 2，完整清单含新线、不含旧线；UI 回滚 V1 → revision 3，V2 为 archived 且仍可匿名下载。
- V1 单 ZIP 下载 1079 字节，浏览器 SHA-256 为 `7456f64ac96b9a04654c3155542169890b8fe099cb4a4fea03853c6cace45fef`，与服务端 descriptor 一致。Range `bytes=0-9` 返回 206/10 字节，HEAD 200，ETag 为整包 hash。再次读取下载 ZIP，逐项核六个文件 hash 与完整清单一致；两个 control.json5 与初始 UTF-8 内容字节 hash 一致，注释和换行未被改写。
- UI 从 V1 恢复一个文件到 V2，生成新草稿，只撤销所选文件改动；随后从 V1 恢复 `builtin/中文线路` 整目录到原 V2，生成另一新草稿，恢复旧线并移除该目录内 V2 独有新线，目录外 pathing 仍为 V2。原 V2 hash 不变，两次恢复均未自动发布。
- UI 删除单文件恢复草稿后，该草稿 404，当前仍 revision 3，目录恢复草稿与原 V2 保留；审计包含 upload/publish/restore/rollback/delete 的真实操作者、原因及 revision。
- 真实 API 过期 revision 回滚返回 409，四字节坏 ZIP 上传返回 400，均不改变 current。UI 打包后立即取消，迟到 Worker 消息未恢复上传能力；UI 上传在等待 CSRF、尚未发送 ZIP 时取消，显示“上传已中断”，无新草稿、current 不变。未把这一取消窗口夸大为网络中途取消或响应丢失实测；后两者及 UI 冲突不盲重试分支主要由自动测试覆盖。
- 桌面 DOM/样式核验：双栏外框 208px + 内容列；375px 手机和 768px 平板检查无横向溢出，上传/版本区变单列；手机回滚对话框在视口内且可取消，未执行额外回滚。已清除工具设置的视口模拟。
- console 中出现的 503/401/404/403/409/400 对应主动边界测试，无额外 JavaScript 未捕获异常；服务端日志未检出 fail/Unhandled/Exception。网络列表还保留启动前 connection refused，以及部分 HEAD/204 无正文请求被浏览器工具标作 ERR_ABORTED；以实际响应、读回状态及审计分别判定，不据此伪报失败或笼统称网络零错误。
- 截图工具在滚回顶部后重试仍超时，共两次，未获得截图，不声称有视觉截图证据。文字快照、计算样式及真实网络响应已验证，不能替代像素级视觉检查。

### 隔离库样本身份

| 样本 | 包 ID | 最终状态 |
|---|---|---|
| V1 | `0c2b6dbbfc6749b3a1a7c84add4d275f` | 当前发布，revision 3 |
| V2 | `b49e0558104542c5b17c7b30f6703ac3` | 历史已发布；SHA-256 `99eab8ffa648b1fd715b34c58486703b3e7a0f7bdf7acdd16276788ee082ec2c` |
| 单文件恢复副本 | `ef199c4564374155a79b1f0dd0979ef2` | 经 UI 删除，保留删除审计 |
| 目录恢复副本 | `b1ad3758a69644b3a4429acce7942b66` | 未发布草稿 |

以上仅为本机隔离夹具，不是正式可用线路或生产发布记录。测试后已写入当次 stop 标记，`route-preview-completed.json` 确认 `state:stopped`、`stoppedByControl:true`；外层受控命令 exit 0，预览工具确认已退出、无运行中的服务器。最终账本为 retained，回收记录 `f16344c854c24b6cbeb2d39fa186a2c8` 已回收 `3,833,968` logical unique bytes，无 cleanup_pending_reason；日志、状态和隔离数据保留，不把该数字称为 NTFS 实际簇释放量。

## 正式入口替换核对（源码范围，不冒充 WPF 实跑）

| 入口 | 当前落点与约束 |
|---|---|
| 锄地一条龙启动前 | `AutoHoeingTask.Start` 在选线/世界预判前调用 `RunAutoUpdateRoutesAsync`；房主确定包，成员预取不擅自绑定 current |
| 房主首次配置、成员补更新 | `AutoHoeingTask.cs` 首次发配置前确认 pin；成员接收房主配置后调用 `ApplyPinnedHostConfigAsync` / `RunAutoUpdateRoutesAsync` |
| 一致性失败修复 | 原修复分支调用同一安装器的 `forceRepair:true`，不改变既有 pin，不调用旧工具 |
| 多世界、迟到成员、守护续跑 | `WorldPlan` / `WorldPlanMember` 继承房主 pin；`CreateGuardRestartTask` 显式继承 `_routePackageId`；旧任务 finally 释放租约后才续跑 |
| 独立任务、配置组间接调用 | `SoloTaskRegistry` 的“更新联机锄地线路”映射到 `AutoHoeingRouteUpdateTask` 并等待 `EnsureAsync`，保留已保存名称 |
| UI 目录扫描、变体及角色脚本关联 | 共同使用明确官方/本地来源，不将旧本地同名目录作为官方包回退；包更新后重建对象、失效相关缓存 |
| csproj、CI、本地发行 | 移除旧复制，CI 和本地完整组包后 fail-closed 拦截 `AutoHoeingUpdater` / `AutoHoeingUpdater.*`；保留 BGI 本体及联机助手更新器 |

最终定向搜索客户端 `*.cs` / `*.csproj` / `*.xaml` 未发现 `AutoHoeingUpdater` 调用或复制。用户手写 Shell/JS/批处理不自动改写；普通单机、自定义和本地导入仍为独立来源，不把保持任务名误写成兼容旧更新器。

## 独立审查与未验证范围

已尝试项目指定的独立审查入口：`CODEXHOST_CLI_PATH` 未设置，`codexhost` 不存在（exit 127），`codex` 也不可用。尚未实际启动指定 `gpt-6.1-sol` / medium 独立复核；未安装工具、未改模型、未访问凭据或把本任务内容发送给外部服务。诊断与夹具准备 workflow 不是该独立复核，不能标记其通过，也未自动追加审查次数。

尚未证明真实 WPF 整条执行、第二进程运行中的客户端安装互斥、游戏内效果、Docker 镜像、完整发行安装/覆盖升级或生产 HTTPS/代理/持久卷部署。生产正式版本必须取自实际发布客户端，测试版本值不能照搬。

## 存储与保护边界

首次超限后的只读现存计量为 `24,956,867,576` 字节（约 23.24 GiB），策略 retained 上限仍为 25 GiB，磁盘底线仍为 8 GiB；不是用历史账本写入量冒充现存占用。后续服务端回归避免重复大型客户端构建，未提高额度、重置账本或删除其他任务材料。

首次集中验证已自动回收 `2,290,768,028` logical unique bytes，并保留原失败、TRX、日志和结论；后续回收以实际工具终态记录为准，不宣称该数值是 NTFS 实际簇释放量。不清理真实运行目录、User、第三方输入、源码或来源不明的旧材料。

旧更新器停止调用/分发不等于清空旧安装目录。本次保留来源不明或用户修改的旧文件，不编写兼容启动器，不运行旧工具，不自动改写用户 Shell/JS/批处理。运维边界见 [线路整包运维说明](hoeing-route-packages-operations-20261117.md)。
