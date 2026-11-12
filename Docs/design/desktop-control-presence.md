# 被控端远程控制提示

用户要求：PC 监控画面不再显示整幅图的悬停提示；执行端右下角明确显示“正在被远程控制”，不抢焦点、整窗鼠标穿透；退出控制后自动消失。控制模式持续开启期间一直显示，而不是只在按键时闪一下。PC 和网页的四个方向按钮按用户要求替换为 W、S、A、D，点击后分别发送对应按键，不捕获控制端实体键盘。保留 BGI 不改动、接收控制默认关闭、三分钟与两秒限制。

## 统一协议

共享 DTO 在 `BgiCoordinatorServer/Models/DesktopControlContracts.cs`；现有 DesktopControlCommand 新增 `ControlId`（每次真正开启控制生成新的 Guid N，输入必须带相同 ID）。控制 ID 与观看 session ID 不同：同一次观看可退出后重新开启控制。

`DesktopControlPresenceRequest` 字段 requestId/controlId/targetUid/frameId/action (`start`、`renew`、`end`)、requesterConnectionId/roomCode/lifetimeMilliseconds/frameAgeMilliseconds；后四项由服务端填写，不相信发送端。远端使用截图相对帧龄计算传输预算，避免依赖跨机器墙钟同步；本机网页另带同机 `expiresAtUtc`，排队不能重新开始确认时限。`DesktopControlPresenceResult` 为 requestId/controlId/targetUid/status (`active`、`ended`、`denied`)、message。帧和命令大小、ID 验证与既有控制协议一致。

- command `desktop.control.presence`，payload `{roomCode,presence}`；服务端绑定真实观看连接、房间、目标 UID/当前执行连接及 frameId 中的观看 session。start 必须关联此观看者收到的最新授权帧，session 未超过三分钟；renew 必须对应已确认、未过期的同一控制 ID；end 即使先于 start 到达也记录短期结束墓碑。单播目标事件 `desktop.control.presence.requested`。
- start/renew 返回转发 ACK 不代表目标已亮提示。目标先完成授权、来源及窗口显示校验，报告 command `desktop.control.presenceResult` payload `{roomCode,result}`；服务端验证目标连接和待决请求，viewer 收到事件 `desktop.control.presence.result`。仅确认 active 后才允许发输入；目标显示失败或超时则关闭控制并提示原因。end 可立即回复转发 ACK，同时目标清理后不需要额外确认。
- 观看端每四秒请求续期，服务端和被控端的租约最长十二秒，且不得超过原观看三分钟。server 输入必须匹配已确认、未过期的 `(观看连接,目标,session,controlId)`；目标在每个输入包前独立检查对应 owner+controlId 当前仍有效。过期、撤权或断连后拒绝新输入；已经交给系统的输入无法撤回，但须完成释放按键。
- 每次 end、连接/房间变化或权限关闭立即结束对应租约并发送目标结束事件；多观看者独立，一个退出不清掉其他人的提示。取消输入按观看会话、目标及控制 ID 限定；具体输入取消还带请求编号，避免迟到的旧取消影响新控制。对乱序 end→start 保存有界三分钟墓碑；断线/服务端崩溃/浏览器卸载不能保证收到 end，目标以自身单调时钟十二秒过期兜底，绝不永久遗留。发送端仅在控制模式开启且页面有效时续期；续期失效关闭控制，不自动重发输入。
- 本机网页经已配对桥 `desktop.control.presence`（args 为同一请求 DTO，targetUid=local，start/renew 附同机点击时的 `expiresAtUtc`），仅操作自己的浏览器 session；PC 直接本机控制使用 owner `local-pc`。本机桥退出/释放 session 清自己的 owner，断线不影响其他观看者。

## 助手 API

`MultiplayerHoeingAssistant/Services/IRemoteControlPresence.cs` 是唯一提示/租约抽象：

- `Task<bool> ActivateAsync(string owner,string controlId,TimeSpan lifetime,bool renewOnly,CancellationToken cancellationToken=default)`；首次 activate 必须完成实际窗口显示后返回 true，renewOnly 不得复活已关闭/过期租约。异步入 UI 队列后再次检查取消和 tombstone。
- `Task DeactivateAsync(string owner,string controlId)` 在调用时同步登记 tombstone，再安排 UI 消失；旧 end 不得关闭新 controlId。`Task ClearOwnerPrefixAsync(string prefix)` 只清匹配 owner；`Task ClearAsync()` 用于接收授权撤销/整体释放。`bool IsActive(string owner,string controlId)` 使用单调时间检查；`Dispose` 退订所有窗口和计时器资源。
- 实现 `RemoteControlPresence`，在 UI Dispatcher 管理一个右下角状态窗。窗口文本“正在被远程控制”，简洁静态，不播放声音/动画，不展示个人信息。只要任一租约有效就显示，空集合时关闭；不抢焦点、不出现在任务栏、不接收鼠标（WPF IsHitTestVisible=false + Win32 WS_EX_TRANSPARENT/WS_EX_NOACTIVATE，处理 WM_NCHITTEST 和 WM_MOUSEACTIVATE）。跟随主屏工作区/DPI 与显示设置变化定位，合理留白。无须控制 BGI。
- `DesktopControlService` 构造函数最后增加可选 `IRemoteControlPresence? presence=null`，生产默认 UI 实例（构造发生在 Dispatcher UI 线程），测试注入 fake；Dispose 同时释放。PC 调 `StartPresenceAsync(targetUid,frameId,controlId,ct)`、`RenewPresenceAsync(...)` 和 `EndPresenceAsync(targetUid,frameId,controlId)`。local-PC owner 为 `local-pc`；被控远端 owner 为 `remote:{目标连接代次}:{请求者连接ID}:`，本机网页 owner 为 `browser:{浏览器会话ID}:`。远端输入按服务端事件 requesterConnectionId 构造同一 owner；本机网页输入采用专用内部方法传入可信 browser owner；不能由不可信 JSON 自选 owner。`SignalRClient` 接入 presence 请求、结果事件及发送/报告方法。
- `DodocoLocalService` 实现桥操作 `desktop.control.presence`，接收与上面同形 args，直接调用 DesktopControlService 的可信本机会话方法，按 session ID 清理；不能走原有日志 20 秒队列。继续复用本机桥身份/源验证。
- `ScreenshotViewModel` 仅在已确认提示显示后允许输入；开启请求期间明确“等待对方确认”、可取消；退出/切源/到期/断线立即异步发送 end 并取消等待。每四秒续期，失败/超时关闭模式；不要因点击或刷新重置三分钟。输入 DTO 填当前 controlId。本机/远端身份和生命周期与上面一致。
- 网页同样使用唯一 controlId、3 秒确认超时与四秒续期；移动端必须在用户点击的同步事件里仍能启动全屏（等待确认时禁止输入，成功后才解锁）；未确认/断线/到期/切源/退出时停止续期、发送 end 并关闭控制。pagehide 尽力发 end；目标十二秒单调租约兜底。`desktop.control.input` 填 controlId；原只读放大与 BGI 快捷功能不依赖此状态。

## 测试判据

用隔离根、fake 时钟/网关/输入验证：启动只能在目标窗口显示确认后发送输入；单看画面不亮；两个观看者各有租约、一个退出仍亮、全部退出立即消失；end 先于 start、迟到确认、重复包不能复亮或误发送；断线、页面卸载、目标授权撤销和进程/房间代次变化均清理；丢包时租约十二秒过期，窗口在 UI 线程可响应后的计时检查中消失；旧 controlId 输入、无提示输入被拒绝；三分钟不续期。真实 WPF 小窗测试不得移动或点击用户鼠标，检查窗口样式、无激活、鼠标穿透命中和关闭；不得启动真实 BGI。原 tooltip 不再绑定到 PC 画面；不能将服务端 ACK 说成目标确认。

本次受控隔离验证记录为 `Build/validation-runs/884aa117c2ca4bdeb58d697402502a69/records`：服务器 49 项、助手 97 项、真实监控页面 2 项、独立提示窗 1 项通过；相关网页回归 80 项通过。构建跳过部署，未进行真实游戏内验证。受存储重解析保护影响的既有 `LocalFilesAreOpaqueSessionBoundAndRejectReplacement` 用例沿用此前单项 Passed 证据，未纳入本次隔离批次；其旧作业失败状态保留。
