# 原所有者能力与存储基础候选（未接入实际Host写者）

沿原共享包local-wait-admission-gates-20261004、全部opening/历史/原级义务和完整总Goal。当前Goal实际active、源码单写者、实际sol/medium握手已保全；来源Goal paused。原综合后审026c0ee0...blocked，98finding/36unknown和新增五项原级保持；本聊天新增独立审查请求0，owner额外implementation2已用1、余最多1，plan0。不把绿色回归或本地候选当独立闭合/生产许可/实机交付。

第一检查点e8605c7579c8e729591f44ed407594d7931681d0：跨进程CAS发布候选，75个明确文件（3个源码/测试，其余证据/元数据），不是75个源码。两个真实隔离进程同RecordRevision在实际publish点停住，旧代码双发布红；固定永不替换的.runstore.lock覆盖读取/保护/备份/替换，20/20定向绿和指定覆盖断言P/F/P。后续RunStore又增加fence，因此最终版本已重做该CAS突变，见crossprocess-current-pfp-observation.json，不把旧dd31版本证据重绑当前。

当前基础候选写集为ArbitrationAdmissionService、ArbitrationLeaseStore、新LeaseOwnerCapability、RunStore、两个所有权/进程测试及既有准入测试底座和Probe Program共8个源码/测试。成功TryAcquire才产生不可变进程内能力；普通Read不能授予。门面12个公开责任入口在等待前捕获能力，33处底层变更统一比较原能力；队列沿已合法登记项的原能力分组，实例重新取得也不升级旧等待任务。显式同根委托必须携带实际取得的opaque能力，另一根拒绝，保留构造零副作用。租约store同时绑定自身实际取得身份，防止用后继较长TTL借旧单调基线。已有夹具只改变取得/显式同owner委托的接线，不改变原行为断言；全部原testId保持。

确定因果：Release清空旧store单调基线，初始两个分支原本通过（owner-red-r2）；合法TTL观察接管、后继TTL60时旧A新Submit和Mark都Accepted（owner-red-r3两红两绿）。同实例终局等待时释放/重新取得，新明确任务合法、旧任务拒绝，动态借当前能力的PFP命中Expected Error/Actual Accepted。新拒绝曾使已知发送完成回执的Pending变None，owner-pending-red保留；已按原完整发送身份及已观察取消保留Pending/身份/seq/结果/原词，零旧写。首轮枚举TerminalCompleted编译错误、所有编译失败原件保留。

RunStore提供显式BindOwner基础：固定原能力不可升级；同租约物理锁内验证原身份/TTL并包围run读检写，发布前再次验证。在受管目录原子保留.owner-fence策略，未持能力的直接RunStore写者/恢复拒绝；只读加载继续可用。新进程合法B可绑定并正常更新。真实进程红例让A释放后，B合法取得并发布，再让A读取最新修订尝试迟到写，证明CAS本身不能挡住旧责任；当前fence拒绝A且B字节保持，未持能力的直接store亦拒绝。publication-owner-fence PFP去掉原代际/TTL资格判定后命中实际迟到发布断言，恢复通过。它证明隔离存储基础；不是10s实际Host退出全链、发送许可/全部history/所有生产写者证明。

**实际Host接线尚未完成。** 首次把requireOwnership与BindOwner前置到RecoverScan的补丁导致无来源/Unknown等拒绝入口出现租约副作用、共享恢复屏障改变并发首调观察，以及旧夹具旁路写者被拒。外部停止观察还出现None回执；当前独立输入反例已修新拒绝分支，但首次失败不被覆盖。完整首轮回归没有终态，原日志保留；核唯一自有vstest→console→testhost→conhost创建身份后仅结束该树，exit-1，不声称该轮完整TRX。首次两dotnet匹配时安全检查拒绝、没有终止；随后精确root才执行。没有杀用户程序。首次补丁保存host-integration-first-attempt.diff，并精确恢复本聊天新增的两个Host文件改动，diff为0，保留原2d0162dd候选。没有恢复整文件或删既有成果。

唯一下一共享转换：正确将能力/fence接入Host真实写者、恢复扫描、初始Planned、面板/移交/Runner及全部直接调用方，保持F11与来源缺失等零租约副作用拒绝；不能照搬首次前置补丁，也不能改旧断言/禁止合法B功能来变绿。原F11既有夹具还要求预建运行终态清理，此与未取得写者资格的Planned创建必须在真实权威层统一，不能只第二次Load或口头部署前提解决。随后同包集中处理共享完整关闭Task、锁外取消异常/阻塞、启动预留/完整观察、10s/15s/TTL、MainViewModel端口关闭顺序，以及可信完整存储+原映射的NoMapping/Absent/residue停止。保留原M1/M2/M3与重跑正例，补实际旧A/新B/合法恢复全链，所有must/important仍open。

最终core-owner-fence-final-r2：助手/测试/Probe串行Rebuild退出0，普通fresh回归退出0，2107Passed/0Failed/2原NotExecuted=2109；相对原07125认证TRX2100新增9、删除0、共有变化0，声明面守卫Passed。277声明输入执行前后同字节，当前PFP恢复后再全部核同SHA，详core-final-observation/core-testid-comparison/core-source-final-observation。该回归只有普通输入/产物观察，不是execution_evidence认证、完整SDK/compiler/task/package/native闭包、独立综合pass或IPC/游戏/User实机验收。旧07125收据因源码变化失效，保留历史不改绑。两新PFP及当前CAS PFP均普通执行，不冒称认证mutation；publication baseline后direct write曾Errno22失败、finally恢复同SHA，原记录保留，仅补同版本剩余负例/恢复，详owner-pfp-first-error及first-attempt-observation。

bundle实际36169fbf...已核，并行只读发现r61原报告missing/未知/未消费，不阻基础修复。原manifest/opening/policy及9f85缺receipt的机械阻断保持，未伪造permit/pass或重开账。下一源码稳定后才完整真实来源/依赖认证，再用余最多一次Sol/high统一全源域综合复核、全部原级闭环和完整约定功能新产物实际运行/停止/重启/数据保留与可运行版本。总目标未完成，生产门关闭，保护User/配置宏脚本截图/第三方JS/.kiro/旧D盘/材料外和暂存，没有push/部署/发布。
