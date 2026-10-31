# 验证产物自动回收

2026-10-31 实现。与旧存储说明冲突时，本文件和 AGENTS 的回收规则优先。累计配额和磁盘余量底线不变。

## 默认验证入口

隔离构建和测试使用以下入口，命令中的 `{artifacts}` 会替换成当前验证独占的输出目录：

```powershell
python -B tools/mistletoe/validate_command.py --root . -- dotnet test Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj --artifacts-path "{artifacts}" -p:DeployToBgiTools=false
```

入口调用已有 Windows Job Object 执行器，保留命令、退出码、标准输出、标准错误和进程终态。非零退出码仍是失败，不因清理成功变成通过。验证的整个多命令流程也可在一个 `Session` 中使用 `guard.temporary_output()`，待后续检查全部完成后再退出 Session。

正常终止（包括已确认进程结束的失败）后，Session 自动回收上述独占输出中的 `.dll`、`.pdb`、`.exe`；另外回收 MSBuild `*.FileListAbsolute.txt` 明确登记且位于输出 bin/obj 下的模型、编译资源和复制的图片/本机库等资源。生成清单保留并绑定 SHA256；`.deps.json` 中 runtimeTargets/native 明确列出的本机库和跨平台包也可回收。未列入这些清单的图片、模型或压缩包不自动删除。日志、JSON、TRX、源码、截图、配置、User/data 子目录和受 Git 跟踪文件保留。进程终态未知，或恢复标记缺少同一 Job 的零进程终态证明时不清理。同一验证请求和 Job 已有 active_processes=0 的清理证明时，可以回收明确登记的产物，但原异常、恢复标记和失败状态完整保留，不授权恢复执行或改成成功。文件锁阻挡的项保留待回收清单；后续 Session 在计算配额前自动重试已登记清单。异常退出但已由原锁恢复逻辑确认终态的临时输出，也会在下一 Session 补做回收。

这不是任意 bin/obj 的通用清空器。普通 `track()`、产品运行目录、源码快照和收据绑定输出不自动降级为临时目录。已有脚本要使用 `temporary_output()` 或上述入口才有收尾回收；无法安全分类的旧资料不会仅因过期被删除。

## 历史产物一次性登记与清理

先核对任务结论、目录来源、当前进程及引用，再指定 Build 下具体的生成输出目录，或 Test/<项目>/bin|obj 下的具体配置目录；不能传整个 Build、任务根或产品运行目录。Test 的资源生成清单可来自同项目 obj；分别指定的 bin/obj 输出也会共享清单核对。工具在共享锁内冻结精确文件清单及 SHA256，随后删除符合条件的二进制及有生成清单佐证的资源副本，保留目录和其他内容：

```powershell
python -B tools/mistletoe/storage_limits.py --root . --reclaim-output "绝对路径/Build/任务/artifacts" --reason "已完成任务的可重新生成隔离构建输出；结论保留于具体记录路径"
python -B tools/mistletoe/storage_limits.py --root . --reclaim-pending
```

回收记录存于 Git common-dir 的 `mistletoe-storage-control/reclamation`，记录路径、原文件大小、SHA256、删除结果及原因，不改历史失败状态，不清账本。重试时文件身份或内容改变则保留；拒绝链接/重解析路径，不修改硬链接的内容或属性；Windows 共享只读副本使用带映像占用检查的原生删除接口，仅移除核定的旧路径，失败则保留待处理。独立只读生成副本允许解除自身只读属性后删除。分别记录移除路径的逻辑字节和最后一个硬链接被移除的文件字节，后者仍不是 NTFS 实际簇释放量。

没有后台任务、轮询服务或扩容弹窗。容量不足时先处理已登记的回收项；仍不足则报告真实不足，不能悄悄提高上限或删除未登记的用户资料。

2026-11-01 补齐：不再遗漏 MSBuild 生成清单中的资源副本和旧测试配置输出。只读硬链接删除标志参考 [Microsoft 文件处置说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex)，本机测试确认保留链接的内容及属性不变。
