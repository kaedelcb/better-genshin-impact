# C17 完整共享链候选检查点（未实机、未综合后审）

已采用 mistletoe-release-first-20261005-v2。源码候选：BGI在动作前记录同用户LocalApplicationData/NexusBGI/terminal-effects下的原token/job/epoch/action/完整合同指纹进度；组合退出先独立确认同Session游戏退出。助手发送前持有原Process句柄，独立确认正常退出后持久化观察并CAS原RunStore。关机命令退出仅记进度，下次启动只读原标记1074、正常6006与下次OS启动12，拒绝中途其他关机/异常事件；RunStore恢复扫描消费同原请求事实，主体完成且全部责任清偿才归成功。缺事实、权限/损坏、异常退出、未持有原句柄或原绑定冲突均保留真实责任，不补发、不猜成功。

实际修复了IPC日期解析导致的两端指纹不同，BGI夹具经真实InstanceIpcProtocol.ReadJson验证；保全该次失败和已按准确路径终止的本聊天testhost观察，不涉及用户进程。旧客户端没有新token仍保持旧动作/Unknown合同；新客户端无新能力/原进程句柄时不发送。正常旧数据不重建无事实的身份。

验证：`-t:Rebuild -p:DeployToBgiTools=false -p:RestoreLockedMode=true`双端成功；当前恢复后助手457/457（restored-linked-r1）、完整BGI输出72/72（restored-original-bgi-r1）。底层产品沿本聊天E:/BgiVersionCheck-01a10954/c17-products；助手测试载体使用只读期间的同字节二进制硬链接（不是源码或User），用于满足现有源码清单测试的AppContext路径要求。原目录缺仓库根导致的5失败及不完整链接视图缺Assets/GameTask导致的1失败均保留，未修改/排除测试；完整输出重测通过。当前source/硬链接/原产品hash读回一致，见input-comparison.json。测试含实际受控子进程正常/异常退出与重启观察，不是BetterGI软件/游戏/OS实机效果。

关键因果：原纪元绑定变体指定断言1Failed；同时撤掉观察器与独立proof中的非零退出守卫后，真实受控子进程异常退出断言1Failed；finally恢复同SHA。单独去掉观察器ExitCode守卫未检出（proof守卫仍拒绝），单独去掉SaveTerminalEffect最新Binding守卫未检出（RunStoreEvidenceGuard先阻止冻结请求改变）；这两项如实非有效突变，不判产品仍坏或伪通过。FrozenOriginalBinding夹具验证现有原冻结保护，不宣称已独立证明内层CAS。原level/历史未决仍保留，未消费独立综合后审预算。

余项：真实BGI closeSoftware/closeGameAndSoftware/shutdown和重启后耐久确认，管理资源引用跳转/编辑实际入口，正常迁移激活/回退消费者、固定时刻重启nextDay与LoopDeadline/真实LocalWait收尾组合；同一新产物所有约定功能真实运行/停止/重启/数据保留，余一次Sol/high综合实现复核，最终产物/版本/启动与恢复步骤。没有全功能可运行版本交付、没有生产/实机pass、没有原级closed或新receipt。原CLI9/native22及implementation额外2用1余1、plan0保持；本聊天新增独立请求0。

下一项转向管理/正常迁移实际消费者与统一产物准备；C17共同链已集中保存，下一项属不同入口与数据合同，选择自然交接以保持审计清晰，不按时间或工具数切换。旧Goal须完成安全终态后原生paused确认，接班同项目local沿原工作区、继承实际Sol/high并读自身完整activeGoal后才写入。总目标仍未完成。
