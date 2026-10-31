# 自动回收与旧产物清理结果

2026-10-31。用户要求实现自动清理、清掉核定无用旧材料，保留必要记录和用户数据，不反复扩容。

- 已实现：隔离验证入口 `tools/mistletoe/validate_command.py`；Session 的 `temporary_output()` 自动收尾回收；开始新 Session 前重试已登记回收项。保留真实失败状态、日志、测试结果、源码、配置、User/data 与未知终态资料。普通 `track()` 不授权删除。
- 配额仍为现有 25 GiB；没有提高额度、重置账本、创建后台服务或终止其他任务。重复/父子目录只计量扫描一次；Windows 已退出但仍有句柄的进程不再误持锁，活跃/未知进程仍阻止回收。
- 定向检查：`python -B -m unittest discover -s tools/mistletoe -p 'test_storage*.py' -q`，49 项通过。包括实际 Windows Job Object 成功/失败进程的自动回收、失败日志及退出码保留、User/源码/受跟踪文件保护、哈希变化、链接、未知终态、配额前回收和遗留进程句柄。
- 原先测试中缺新增模块、旧突变替换表达式失效、验证入口恢复目录不正确的失败已修复；未将其他任务的失败改成成功。其他任务中断留下的恢复标记及输出未纳入本次历史清理。

## 实际清理

清单及逐文件 SHA256/删除结果：`.git/mistletoe-storage-control/reclamation/30e3056a5b5e4936b8031a330a59813a.json`。取得共享锁后执行，状态 complete，无待删除项。

仅清理下列历史任务 `artifacts` 中核定可再生成的 `.dll`、`.pdb`、`.exe`：

- `Build/startup-recovery-20261025-01a172f9/artifacts`
- `Build/resource-task-reconnect-20261025-01a172e6/artifacts`
- `Build/member-offline-status-20261025-01a171db/artifacts`
- `Build/startup-session-20261025-01a16f83/artifacts`
- `Build/startup-plan-recovery-20261026-01a1771b/artifacts`

删除 1,879 个文件，移除独立文件字节 3,126,021,412（约 2.91 GiB；非 NTFS 簇统计）。受管去重文件计量从 27,827,344,393 降到 24,701,322,981 字节（约 25.92 → 23.00 GiB）。没有清空任务目录。保留的 8,159 个文件前后 SHA256 一致，包含输出中的非候选文件及任务根的结论/记录。未触及产品运行目录与真实 User。

工具本地提交：`babfc72b5`、`07515f298`、`b9929f9c1`；没有 push。取码基线前值为 `4d054f5e3dc3b2bed6fd857c7ef174e288b7f227`，最终向本次记录提交前移，仅表示源码接续检查点。
