# 桌面监控低频控制

范围：2026-11-12 用户授权简化方案；助手执行输入，不修改 BetterGenshinImpact，不自动暂停、停止或恢复 BGI，不捕获控制端实体键盘。主屏左右单击/双击、固定键盘按钮（包含用户追加的 Win+D 显示桌面），每个被控端两秒一次，沿用每次进入监控三分钟。不开控制时保留原有监控、放大和历史查看。

## 既有 BGI 快捷功能入口

用户追加：把耕地机页面“选择快捷键 → 执行”的入口复制到桌面监控页，保留原入口。PC 复用 MainViewModel.ExecuteHotkeyCommand / ShowHotkeySelectDialog，网页复用 showHotkeySelect(uid, memberName)，继续发送原有 hotkey_execute，而非模拟对应按键。只作用于当前选择的单个成员，列表来自该成员上报。此入口独立于截图输入模式及其新鲜度，用于先手动暂停/停止 BGI；仍遵守原命令的在线、身份与执行规则，不自行新增自动暂停/恢复行为。

## 数据与通信

公共 C# DTO 在 `BgiCoordinatorServer/Models/DesktopControlContracts.cs`，由助手 csproj 链接编译。JSON camelCase。

- `DesktopCaptureGeometry`：left/top/width/height 是主屏物理像素，displayId/layoutId 标识显示布局。截图已缩小的 width/height 仍表示 JPEG 尺寸，另增 geometry。
- `ScreenFrame` 增加 `FrameId` 和 `Geometry`；远程 `MemberScreenshotFrame` 增加 `Geometry` 和 `ControlAllowed`。远程的 frameId 使用已有 RequestId (`screen:{sessionGuidN}:{frameGuidN}`)。本机帧用独立 Guid N。
- 目标助手默认 `AllowDesktopControl=false`；明确开启才接受输入。截图回复同时携带 `geometry` 和 `controlAllowed`。监控页需有该权限开关，不能将只读共享自动视为控制授权。
- `DesktopControlCommand`：requestId=Guid N、targetUid、frameId、action=left/right/double/key、x/y 是实际图像内部 0..1 的相对位置、key 仅允许 DTO 中白名单。非有限坐标/留白/未知操作拒绝。
- 网关 command `desktop.control.request`：`{roomCode, command}`。返回 accepted ACK，不是执行成功。
- 目标事件 `desktop.control.requested`：DTO DesktopControlRequest (`command,roomCode,requesterConnectionId,expiresAtUtc,frameAgeMilliseconds,lifetimeMilliseconds`)。`expiresAtUtc` 仅诊断；目标以本机登记的该帧实际单调捕获时间计算 `frameAgeMilliseconds + lifetimeMilliseconds - 本机捕获帧龄` 的剩余预算，再扣本机处理耗时，不直接比较两机 UTC。服务器帧龄从收到图片时开始，晚于目标捕获，因此该算法保守计入上传、传输与排队延迟，不会因机器时钟偏差放行迟到输入。
- 目标应答 command `desktop.control.respond`：`{roomCode,result}`，DTO DesktopControlResult。观看端事件 `desktop.control.result`：result 本体。
- 取消 command `desktop.control.cancel`：`{roomCode,sessionId,targetUid?}`，sessionId 是观看会话 Guid N。服务端取消匹配在途指令，向目标发 `desktop.control.cancelled`：`{requestId}`。取消仅阻止尚未执行的动作，不能撤回已送入操作系统的输入。
- 服务器仅接受该观看连接实际收到的最新截图（同房、目标执行连接、会话未到三分钟、帧未超过15秒、geometry有效且controlAllowed）；每帧最多使用一次，防止多次操作旧画面。每目标所有观看者合计两秒一次。指令寿命3秒、不排队、不自动重试、重复编号不得再次转发，断线/目标退出清理在途状态。
- 目标助手独立验证：权限仍开启、帧在本机登记且15秒内、当前屏幕布局与帧相同、命令未过期、请求未重复、两秒间隔、执行闸门立即获取（不能等待排队）。输入先做完整校验，再执行一次并在 finally 释放本次合成的键。执行结果只表示系统是否接受输入，游戏效果以刷新截图观察。

## 助手接口（并行实现约定）

由原生/服务实现者提供：

- `ScreenshotService.CaptureDesktopFrame(int targetWidth, long quality)` 返回 `ScreenFrame?`，生成本机 FrameId 并登记 geometry/单调捕获时间；原 Capture/CaptureJpeg 保持原调用语义。
- `ScreenshotService.RememberControlFrame(string frameId, DesktopCaptureGeometry geometry)` 供远程 relay 登记与应答关联的帧，不能刷新同 ID 捕获起点。`TryGetControlFrame` 与当前 geometry 查询供输入服务验证（内部 API 由服务实现者自定）。
- `DesktopControlService(ScreenshotService, DodocoSettingsService, Func<SignalRClient?>)`；`EnsureHooked()` / `Dispose()`；`Task<DesktopControlResult> RequestAsync(DesktopControlCommand,CancellationToken)` 观看端等待执行回执；`Task<DesktopControlResult> ExecuteLocalAsync(DesktopControlCommand,DateTimeOffset expiresAtUtc,CancellationToken)` 供 PC 本机和本机网页桥调用；`Task CancelAsync(string sessionId,string? targetUid=null)` 取消未决远程操作。
- SignalRClient 提供 OnDesktopControlRequested / OnDesktopControlResult / OnDesktopControlCancelled 事件及 RequestDesktopControlAsync(command)、ReportDesktopControlAsync(result)、CancelDesktopControlAsync(sessionId,targetUid) 方法。
- ScreenshotViewModel 在现有 timeProvider 后增加可选 desktopControl 参数；DodocoViewModel 创建并注入服务，在 FlushPending EnsureHooked、Dispose 释放。服务不会启动 BGI 或发任何 BGI 任务命令。
- 本机网页 `desktop.control.input` operation args 是 command（targetUid 固定 local，并附同机浏览器点击时的 `expiresAtUtc`；HTTP 处理不得重新开始三秒），DodocoLocalService 直接调用输入服务（不走嘟嘟可20秒数据队列）；本机截图增加 frameId/geometry/controlAllowed。`desktop.control.cancel` operation args 为 `{requestId}`，直接调用服务 `CancelLocal(requestId)`，保留取消墓碑以拒绝先取消后到达的 input。HttpListener 上 fetch abort 本身不取消已执行处理，所以网页取消时要另发该写类操作。桥已有身份配对沿用，不扩展匿名入口。

## PC / 网页行为

- 当前单画面查看且新鲜最新帧才允许开启控制；全员、历史帧和放大窗口不控制。控制模式默认关闭，选择成员、离开页面、到期、断线均关闭并取消未决请求；重复激活/重绘不能续期。
- 控制模式下单击按选定鼠标方式执行一次（左键/右键/双击下拉选择），原始浏览器右键菜单不当作指令。界面显示白名单键盘按钮，通过鼠标点击发送；不监听物理键盘。
- 使用真实图像内容区计算归一化坐标，排除 WPF Uniform/CSS contain 留白；无需乘控制端 DPI/devicePixelRatio。放大只看图。
- 操作中暂停该观看端自动截图以免替换操作依据，不接受第二次输入；收到 executed 回执后等待至少1100ms再请求新截图，给游戏响应及截图限频留出余量。失败/超时显示真实结果，不重发输入；拿到新帧后才重新允许输入。
- 提示“输入作用于对方当前桌面；BGI 或本机鼠标同时操作可能干扰，可手动暂停 BGI；不支持锁屏/UAC安全桌面”。

## 验证结束判据

新增测试验证默认拒绝、授权撤销、跨观看者/跨房伪造、两秒全局限频、三分钟截止、过期/重复/取消不执行、过期帧/布局变化拒绝、DPI及留白映射、合成键释放和真实监控入口模式切换；均用隔离数据、假输入发送器或专用测试窗口，不能点击用户桌面或启动用户 BGI。复用现有监控时限/日志/网页协议回归。不把测试输入已发送说成游戏已解卡。

## 本次实现与验证记录（2026-11-12）

- 实现基线：`78df8ecbdd894ad6a106912daecfc0cb6fc30a61`。未修改 `BetterGenshinImpact/`，未自动暂停/恢复 BGI，未覆盖运行目录或真实 User。代码提交后仍需更新助手与协调服务器；本次没有推送或部署。
- 使用：被控助手在桌面监控页开启“允许控制本机桌面”；观看端选单个成员、刷新最新图后开启 PC“控制模式”或网页“启用本页控制”。鼠标方式选择左键/右键/双击；固定键包含 Win+D。BGI 快捷功能按钮复用原选择器，原耕地机入口保留。
- [集中隔离验证记录](../../Build/validation-runs/5dde2c02868349efa7e8c2715f39dfae/records/result.json)：助手 **107/107**，服务端 **43/43**；`DeployToBgiTools=false`，助手使用独立 `NEXUSBGI_DATA_ROOT`，作业终态 `active_processes=0`。
- 网页 Node 测试 **42/42**：`desktop-control.test.mjs`、`desktop-monitor-hotkey.test.mjs`、`desktop-monitor-timeout.test.mjs`、`dodoco-protocol.test.mjs`。包含真实脚本事件绑定、本机身份三项映射、Win+D、留白与缩放、截止、取消、旧结果隔离。
- 真实 WPF 页面验证原 BGI 快捷键弹窗可打开并取消；无物理鼠标的生产事件/helper 验证拖出按钮松开不发送。Windows 原生 DPI API 验证专用线程 PMv2 上下文及恢复；坐标比例用注入几何覆盖。未切换用户系统缩放，未向真实桌面发送 SendInput，未做真实队友/游戏内效果验收。
- [隔离网页预览](../../Test/DesktopControlPreview/index.html) 加载正式脚本和样式，截图/成员/回执均为假数据。浏览器验证 Win+D、中心点击、快捷功能入口和 375px 换行，无横向溢出；本机预览服务器已停止。
- 独立复核来源：`wf_0db7c8c6-732` 两位只读审查者与 `wf_7f2dd3a6-b0b` 定向复验。四项问题涉及本机 UID、请求缓存终身耗尽、取消点击、跨机器墙钟。随后消除了执行端对服务器 UTC 的依赖，并补充目标时钟 ±20 秒、网络迟到、上传/传输/执行预算、本机 HTTP 点击截止等回归。保留原报告当时的 remaining 结论，不改写成历史 pass；当前修正依据为最终代码与集中回归结果。
- 首轮作业 `13f27e17232c41cda390856ef2747c7d` 保留失败状态：一个 PC 提示断言已修复；原有 `LocalFilesAreOpaqueSessionBoundAndRejectReplacement` 在 TRX 中为 Passed，但其保留的目录链接触发存储保护。已在共享锁内只移除该隔离链接，目标夹具和原失败/TRX 保留；后续复验复用该未改动安全用例的有效证据，不重复创建存储域禁止的链接。二进制/资源由回收入口处理，未清真实用户数据或原结论。

## 手机横屏与全屏远控（2026-11-12）

- 手机启用本页控制后自动进入独立大画面，也可点击“全屏远控”。桌面浏览器继续保留原内联操作，并提供同一显式入口；原“放大”仍为只读。
- 全屏工具条保留鼠标方式、刷新、按键抽屉、工具及退出。“工具”提供横屏布局、放大/缩小/适应和原 BGI 快捷功能。画面放大后触屏拖动查看，点按才执行一次输入，不增加远程拖拽或长按指令。
- 优先尝试浏览器 Fullscreen / Screen Orientation API；缺失或被拒绝时使用网页可用视口及 CSS 90 度横屏布局。网页无法移除宿主 App 的外部原生工具栏。真实横屏时取消 CSS 旋转，比例、滚动和旋转后的点击仍映射到原图 0..1 坐标，不乘观看端 DPI。
- 全屏不显示历史缩略图和原始时间长串；只操作已经解码完成的当前最新帧。换帧、旋转、滚动、长按、双指和取消手势不发输入；无 PointerEvent 的触摸回退还屏蔽兼容鼠标事件链，防止长按拒绝后再次被合成为点击。
- 退出、浏览器退出系统全屏、断线、切成员/历史或观看到期时关闭控制并取消未决输入。全屏、旋转及缩放不重置原三分钟观看计时；到期仍需退出桌面监控再进入。保持原两秒限频、权限和执行回执规则，不改 BGI 或控制协议。
- 验证：相关 Node 回归 **67/67**；375×812 的旋转回退及 812×375 横屏浏览器实测使用假执行器。拖动/双指产生零输入，中心点映射为 `(0.5,0.5)`，放大和滚动后的双击坐标与可视位置一致；Win+D、BGI 弹窗、44px 工具按钮和退出可用，无横向溢出。未向真实桌面发输入，未使用实机手机验证；原生全屏/方向成功与拒绝、迟到 Promise 等分支由假 API 测试覆盖。
- 独立复核 `wf_bae3ad70-209` 发现的兼容鼠标误触与隐藏失败反馈已修复，`wf_27a0bba6-dae` 定向复验确认两项 fixed；随后补测了 BGI 命令异步失败反馈及旧视图结果隔离。
- 更新服务器网页资源后刷新手机页面生效；使用助手内嵌本机网页时需随助手重新打包这些现有嵌入资源。本轮未推送或部署。
