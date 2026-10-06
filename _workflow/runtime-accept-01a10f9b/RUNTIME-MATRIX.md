# 同一完整版本的实际验收

交接 RUNTIME-ACCEPT-20261006-FROM-01a10f14；本聊天完整原生 Goal active，真实模型 gpt-6.1-sol/medium。采用 mistletoe-release-first-20261005-v2 与 mistletoe-storage-limits-20261005-v1。总交付尚未完成；历史报告、原级 open、opening 与额度不变。本组会诊请求 0。

当前完整目录：`_workflow/runtime-unified-01a10e1b/product`。源码及模块身份见前继 runtime-refresh/result.json；BGI 的该 SHA 指 BetterGI.dll，exe 独立核验，不混用两者。

| 事项 | 当前实际证据 | 判定/下一核验 |
|---|---|---|
| 同一版本启动 | BGI PID24528 / 助手PID11160，Session1、E:product 实际路径；主窗口已观测 | 已启动；用户Session2旧程序未操作 |
| 正式页面与真实资源 | 槲寄生任务中心正式WPF调度页；连接当前BGI61资源；资源编辑打开对应“垫底占位”一条龙配置 | 入口实际可达；不代表资源游戏内执行 |
| 新建/三步引导/保存 | UI新建wf-22f5a13e，真实目录选择→23:50→确认，保存39a2183f；JSON读回n-031e6979及schedule.time/sequence | 该流程保存成立 |
| 未来等待/停止 | run-b6b38d7cb5a7进入2026-10-06 23:50等待；UI停止为Cancelled，state6/stopRequested=true；submissionHistory/nodeOutcomes/completionHistory全0 | 等待停止成立，未执行游戏资源/收尾 |
| 保存重启 | 助手正常退出、PID16080重启；原流程仍可选；run仍state6/revision11/stopRequested=true/0提交 | 此停止运行未复活 |
| 独立窗口/共享草稿 | 真窗口“槲寄生·调度列表”；缩放/资源目录/检查器/回主窗 | 同一草稿可达；发现下述两问题，修复后须实机复验 |
| 点击时间漂移 | 单击23:50卡片，草稿变23:55，未保存 | 本版具体风险：最小拖拽阈值修复候选已写，未宣称通过 |
| 双窗撤销 | 恢复时间23:50时弹IndexOutOfRange；真实WPF专用红反例同异常，堆栈Draw→NodesChanged→RestoreUndo的Lanes.Clear | 恢复期间不绘制、结束通知双窗刷新候选；PFP与回归待终态 |
| 单流程/整表互导、路径/车道/参数/运行操作 | 前继有效组件证据保留 | 统一修复产物实际矩阵继续；不以旧截图或组件数字当整软件验收 |
| 原助手数据 | 原116文件更名保全；旧测试档58文件原位；新命名空间禁自启/服务器；自有软件已正常退出 | 本轮结束须原件逐SHA恢复并保存测试档案 |
| 用户真实运行数据/JS | 本组未授权改原数据或第三方JS；只操作自有运行目录/测试助手命名空间 | 收口定向哈希核验；不恢复旧备份掩盖已有漂移 |
| 游戏/账号/树脂/兑换码/关机/队友 | 未代执行 | 按用户要求等主动反馈，开发侧独立工作继续 |
| 综合独立复核/工具历史 | bundle drift、r61-distribution-candidate缺报告、旧bf733 blocked及原级责任/追加2/2余额0 | 原样保留；用户最新功能优先要求的后续跟踪，不伪pass或重置额度 |

旧候选引导弹窗使用默认浅色控件，暗底上的步骤说明对比度较低，但实际三步可完成；登记普通显示问题，不扩大当前修复。

## 当前恢复检查点（Esc 后续）

源码检查点为026777e78（点击阈值/共享撤销）及a5f33a12b（手势期间延迟重绘、稳定节点身份）。gesture-green：Rebuild退出0；真实TRX42执行/42通过/0失败/0跳过；305源码输入当前无漂移，构建与测试Job均active_processes=0。不是全量绿或整版验收。

undo-causal原baseline1通过、negative1失败，但执行句柄在negative证据收口时消失，未生成正常最终收据。原四Job及PID已核不存在；源码从自有mutant逐字节恢复；原预约2c11fc9ee13248ea89b93caaa16ad915按failed恢复，身份/额度/roots保持，见interrupted-recovery.json、interrupted-reservation-before.json及interrupted-owned-writer.lock.json。不把缺终态的序列伪称完整PFP。recovered-green和gesture-green为随后真实恢复回归，原件保留。

runtime-refresh-gesture/result.json：同一完整product更新五助手模块，助手DLL SHA debda20c1527ece19fdebe025af2a7e45cc5755684decf8b13789a6fbf6f3b08；BGI DLL SHA 0720fe79e71f1069c51378cc8a0e54e621dd03ea900fca052915c77f9a9147c7；1351个BGI输入不变，9500个product/User更新前后逐SHA相同。

最新模块之前的实机普通点击已保持23:50；明确拖动未移动，随后补手势期间不重绘的候选a5f33a12b。最新模块曾启动，但Computer Use返回用户physical Escape停止，未取得最后拖放/双窗撤销实机结论。用户停止后未改用另一界面通道绕过；后续已只读确认BGI/助手进程不存在。当前仍缺开发侧全功能矩阵和完整版本交付，Goal保持active；不能以42项回归或模块更新宣称完成。

ui-data-restored.json证明：原助手116文件已逐SHA恢复，协议恢复，旧NexusBGI-tested-01a10e1b档案保持；本批测试数据保存在MSIX命名空间NexusBGI-tested-01a10f9b。后续不能盲跑旧prepare/restore或直接用恢复的原配置启动验收，先核进程/原数据并准备新的自有测试命名空间及独立记录；不得冒充真实旧安装归属或丢弃本批结果。

唯一下一项：取得恢复Computer Use的用户意图后，用同一最新完整产物复验真实拖放与双窗撤销，再补互导/参数/运行操作等开发侧矩阵，交付版本及启动步骤。普通显示问题、无关历史/工具及原级open保持；用户游戏/账号/树脂/兑换码/关机/队友行为仍未代执行。
