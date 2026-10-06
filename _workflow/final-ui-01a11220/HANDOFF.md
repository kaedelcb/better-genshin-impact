# 独立数据根与真实拖动入口修复后的实机接续

标记 `OWN-ROOT-DRAG-ACCEPT-20261007-FROM-01a11220`。来源聊天 `01a11220-852f-7e42-b4c9-5bbcedbbd87d`。总产品未完成。

本批已完成独立验收数据根、同一完整产物启动和实际 UI 定位，以及真实拖动缺口的红反例与源码修复回归。下一独立阶段是更新修复模块后集中补全真实交互及完整功能矩阵。当前上下文同时包含两次中断恢复、双物理根与多个产物版本，必须从此明确边界接续，避免把源码候选/旧产物/实际验收混为同一状态；不是按时间或工具数强制交接。

## 用户保护边界与现场

用户明确要求：外部恢复成功后继续原总目标，**不要再从 Codex 内移动真实用户数据目录**。外部恢复 r2/external-restored.json 已读：27 常规 Roaming 原件回确切物理位置，MSIX 116 与常规 27 均逐 SHA 同，实际句柄准确。默认 AppData 仍受 MSIX 混合重定向，改变 APPDATA 无效；不可重新使用旧 data_guard.py prepare/restore，也不重试目录更名。

源码候选 dffbf9e54 已接入 `AssistantDataDirectory`：NEXUSBGI_DATA_ROOT 显式进程根统一 13 个配置/流程/运行/缓存/水位入口，未指定保持原路径，非法相对根拒绝，显式根禁旧 exe 配置迁入。独立目录 `_workflow/final-ui-01a11220/own-runtime/assistant-data` 实际运行文件句柄全部落 E: 正确根，原 MSIX 116 运行中定向核 SHA 同。完整原常规根以外部恢复证据为基线，Codex 联合视图不能用路径标签冒充物理全量 readback。

同一完整产物仍 `_workflow/runtime-unified-01a10e1b/product`。own-runtime/refresh/result.json：五助手模块更新为 DLL `450e3600dfafad85f85e525f01f64038e599499ab7cde391d481624885f863bf`；BGI DLL `0720fe79e71f1069c51378cc8a0e54e621dd03ea900fca052915c77f9a9147c7`，1351 BGI 输入同，9500 product/User 前后同。此 DLL 尚不含最新 OLE 拖动修复，禁止当作已实机通过的修复版本。

## 真实运行证据

own-runtime/first：自有 BGI PID22108、助手12644，Session1，实际路径均在同一 product；未操作用户其它会话程序。正常退出确认通过 Computer Use，两个应用 exit0，apps-tree-terminal active0，父控制器 exit0/预算终态；协议恢复，product/User changed 空。无自有应用、构建、测试、突变或预算锁在途，交接前再核。

实际正式 WPF 页面：普通与最大化自适应；新建 `wf-59fb6c1b`，连接实时 61 资源；三步选「整龙·垫底占位」→23:50/主车道→确认。保存修订 `f4e64385…`，实际 JSON 节点 `n-090b94d1`、schedule.time=23:50/mode=sequence，原件在 own-runtime/assistant-data/flows。**未启动流程/游戏**，未取得等待停止或重启证据。实际普通点击保持23:50；明确物理拖动到不同位置后仍23:50，登记本版功能阻断，不能说拖动已通过。

## 拖动根因与当前源码候选

`NodeDrag` 源 OLE 声明 Move；旧 `TimelineDragOver` 对所有类型恒 Copy，实际放置被拒绝。drag-effects/red：真实 WPF Canvas RaiseEvent，节点 Move/Copy-only/Move|Copy 以及资源不兼容源 4 Fail，未知载荷 1 Pass；Rebuild0，source_drift 空，Job 各 active0，父 exit0。原等级/其它发现不改。

已定向修复 TimelineDragOver：节点请求 Move，资源/判断预设 Copy，均与 e.AllowedEffects 相交，未知或无草稿 None。drag-effects/green：Rebuild0，正式 UI/路径/编辑/独立根与新 OLE 回归 **53 Passed/0 Failed/0 Skip**，source_drift 空，build/test Job active0，父 exit0，源码无突变。新测试 FormalDragEffectsTests.cs 5 项，原 WPF 源 35078→35164 字节，BOM/LF保持。本次新反例由旧实现自然变红，未再突变 live 源。

最新源码提交以初始消息/git读回为准；此 OLE 修复尚未更新 product 或实机复验。唯一下一项：运行已经调整的 `runtime_own.py refresh-drag`（输出必须不存在，保全 450e 旧模块、核 308 输入/1351 BGI/User），然后 `runtime_own.py second` 以同一 own-runtime/assistant-data 启动，通过 Computer Use 集中复验节点移动/阈值/跨道覆盖/线路/双窗撤销/弹窗/三步添加/参数/互导/候选保护/运行操作；补未来等待停止、保存重启、数据保留及总计划全功能矩阵，修本版现实缺口并交付完整版本/步骤。运行前核 native Goal、当前源码/产物和自有进程；不启动第二写者。

## 验证局限、工序及保留项

isolated-data/green 编译失败为新增测试误 namespace，原件保留；green2 在 mutant 构建收口时句柄丢失，helper 一度仍 mutant，三 Job/父17052/后代终态核验后原子恢复并以 failed 结算原预约，见 isolation-recovered.json。green3 对旧 mutant 产物实际根断言 1 Failed（写前拒绝），再恢复源码 Rebuild及48/48绿色；旧构建仍 interrupted，不称完整认证PFP。当前两次错误/中断不伪改成功。

原 bf733 blocked/98 原级责任、extra2/2余额0、本聊天新增会诊0、无独立综合pass/receipt；不能跨渠道超额/重置。原全量/286失败、ProductionCtor_DoesNotEnableSuccessorPathGate 等保留，未改断言。bundle drift、r61-distribution-candidate缺report只读已核，不扩工具。最新用户功能优先及完整产品/UI定义保持，普通不妨碍使用BUG/无本版现实影响历史后跟踪，延期不是 closed。

政策：mistletoe-release-first-20261005-v2、mistletoe-complete-usable-delivery-20261006-v1、mistletoe-storage-limits-20261005-v1、auto-local-checkpoint-commit-20261004-v1；存储实际单次1.5GiB、累计17GiB、余量8GiB，新材料经Session/process_runner，不是OS硬配额。全域预算扫描慢但不绕额度。材料外csproj、MigrationReferenceActivation/MigrationSwitchTransaction、旧文档/账本与大量_workflow变化保护，只明确文件 commit --only，不发布。

前序功能合同/完整范围/源码与证据入口均沿 FINAL-UI-ACCEPT-20261007-FROM-01a10f9b：原 HANDOFF/RUNTIME-MATRIX、path-runtime-01a10f14、总计划、UI预研（E:\Program Files\mistletoe-ui-design、v4跨列聚类）、DELIVERY-COVERAGE/POLICY、工具设施/v3/并行成果索引。车道是流程路径，任一路径到同一节点即执行、重复由再次到达/循环决定，无每日一次或OR/AND门槛。保留 C01/C02/C04-C11/C17/C20、公版与八原生单项、组/JS/宏混排，第三方JS一字不改。游戏/UID/账号/兑换码/树脂/关机/队友动作待用户主动反馈，不代做、不据此停独立开发侧交付。

Computer Use继续当前skill及@oai/sky；精准返回窗口选择、每动作新观察，不混PowerShell UIA。原生退出确认的 UIA关联索引不可用时，刷新后同工具实际截图坐标正常确认（非改通道）；一次 no-monitor 状态通过重新选择同返回窗口恢复。首次截图前台曾为Codex，未进行输入，activate真实助手后重新取得正确截图。不得把这些瞬时截图/缓存误当界面状态。若工具明确用户停止，仅停当轮，不reset/换通道绕过。

交接继承来源当前实际gpt-6.1-sol/medium，最近turn_context2026-10-06T17:57:48.34Z与settings一致。旧原Goal须原生paused确认后唯一同项目local接班，新聊天实际模型及自身完整Goal active读回再施工，不恢复旧Goal，不重开opening/包/额度。
