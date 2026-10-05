# C17 接续施工（同一原包，不重开预算）

2026-10-05 已读回 mistletoe-release-first-20261005-v2；自身 Goal active、cwd正确、rollout Sol/high/settings一致、来源 Goal paused。HEAD 98ec78c7为交接文档提交，源码基点40c5ef7cb保持，无在途产品写者。

准入：C17软件自退出/游戏与软件退出/关机缺独立且耐久的实际结果；真实 ExternalInterfacePrerequisitePlane → BgiWorkflowTerminalExecutor 在BGI退出后失去IPC，正常支持路径可达，用户无法正常结束收尾责任。最小修复为同用户本地动作进度日志、原进程句柄独立观察、关机后系统日志只读核验及RunStore原身份CAS读回；完成后转管理引用/正常迁移及统一新产物实际验收。

共享链整体修复，最后一次综合后审留统一候选，沿用原限定修复授权，不开局部整包前审/不重置opening/预算。当前链需要跨文件一致性，暂不派额外只读子Agent；独立综合复核仍保留。原级历史/blocked及未知保全，不标pass/closed。deliveries发现r61原报告missing，未消费且不阻独立C17。

| 状态/故障 | 必须成立的事实/结果 |
|---|---|
| 发送前停止/无原epoch句柄 | 零动作；无dispatching新事实 |
| BGI落盘动作进度失败 | 不关闭软件/不发关机；保留失败/未知 |
| closeSoftware | 原载荷/随机标记/job/epoch一致；软件退出请求进度 + 预先打开的原Process句柄HasExited/ExitCode=0，才发布独立效果和执行体退出 |
| closeGameAndSoftware | 在同Session游戏白名单独立确认退出后才请求软件退出；另外满足软件退出事实 |
| shutdown | 同请求标记的真实shutdown.exe /s /t 60 /c进度只表示发起；之后只读System日志匹配User32 1074请求、6006正常结束、Kernel-General 12下一OS启动，拒绝中途其他请求/异常关机，才确认；运行期间仍未知不补发 |
| 掉回执/助手重启 | 同原标记日志可找原job；只能读取与原完整载荷hash相同的事实；无原进程独立观察/无系统事实则未知，无自动补发 |
| RunStore并发/身份变化 | 原完整Binding CAS，事实持久读回再释放；不取消另一epoch |
| 日志损坏/缺失/权限失败 | 未知，保留原件责任，不猜成功、不凭空重建 |

动作进度日志与观察结果位于同用户LocalApplicationData/NexusBGI/terminal-effects；只写新日志，不修改已有配置/User/JS。受控测试注入独立临时根，不触真实目录/真实关机或用户进程。关键断言红→绿→反向突变→同SHA恢复，串行Rebuild DeployToBgiTools=false，输出复用本聊天独立产品目录，不复制整树。系统事件参考Microsoft事件排障文档 https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-unexpected-reboots-system-event-logs ，当前机器1074 XML观察param6为comment。

这里是实现计划及候选验证要求，不是实机验收；正常关机最终效果只能在实际关机/再开机后核验。全约定功能统一产物、运行/停止/重启/数据保留与余一次Sol/high综合后审仍欠。
