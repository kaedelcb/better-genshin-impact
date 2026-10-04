# 异步节点终局与面板停止候选（未审/未验收）

同原共享包，原 opening、请求/预算和 G2(e)/G4/G4a/G7/G8/G10/交错⑤⑥ important implementation open 保持。新增独立请求0。总交付未完成，生产门关闭。

生产 Host 节点终局扫描与统一终局回写使用 MarkOperationTerminalAsync，WaitAsync 获得门后进入同一锁内核心，原身份、权威终态/封印、迁区和失败分类不改。取消等待保留 Accepted 原发送身份/seq/Zone，不写终局，原身份可显式重试。同步组件 API 保留兼容。

面板 Stop/Skip/Pause/Reload await RequestRunActionAsync；Unknown 原身份停止及停驻回写按原链异步等待。同步宿主 API 保留兼容，生产面板唯一调用已切到异步；不声称所有同步磁盘工作消失。显式 Stop 对账保留独立 Task.Run 调度，使超时在同步前置工作外生效；超时仍未清偿。曾移除调度导致旧并发重试用例退化，恢复后原断言通过，未修改旧预期。

真实 Host 争用夹具的扫描阻塞与实际 Panel Stop 命令阻塞分别在指定断言红；后者使用真实组件与受控本地 Accepted 父责任、受控执行端口，不是外部 job 事实或真实 IPC/游戏/User 验收。取消责任保留新增测试通过。两项当前关键突变：lock-mutation-r4（扫描退回同步）、panel-mutation-r2（面板退回同步），指定阻塞断言 P/F/P，源码 SHA 恢复。

最终 final-r2：Rebuild exit0；TaskCenter 1939 Passed /18 Failed /2 NotExecuted=1959，exit1；定向/声明面/互导16/16，exit0。精确合集1949/18/2=1969，对前序1966新增3、删除0、共有变化0，18失败 testId 一致；旧失败不是永久豁免。声明面再生成前后 SHA 一致，清除环境后的再次守卫通过。六源码/测试输入在执行前后字节一致。

首个完整回归 1938/19/2：LocalWaitParkingStop_OverlappingRetriesWriteOneTerminalTransitionAndIsolateOtherRun 新退化由任务调度改变导致，已恢复调度并定向4/4后重跑全量。初版扫描夹具误用旧 null 封印做新封印预期、取消夹具 API 拼错导致编译错误、面板夹具漏 CandidateId 导致结构拒绝、突变脚本语法错误均为保留过程，不能当语义红。原 full/lock-mutation 等失败原件不覆盖。

证据是普通进程/TRX/源码字节观察，非认证 receipt、独立综合实现 pass 或产品验收。manifest evidence audit缺列表、policy=false/native prepare专属授权/implgate原planpass限制保持；不翻授权、不倒签、不扩工具、不伪receipt。同步API剩余兼容与受控夹具局限如实保留。

下一共享依赖是类型化唯一原父来源跨存储绑定、33节点逐次原因/迁区/归档/原身份重开、四真实入口因果矩阵及原G4/G7链。PARENT-SOURCE-DESIGN.md仅设计，TypedAdmissionParentTests.pending未进入测试项目、尚未运行，不记功能或测试已实现。稳定完整共享链后的认证真实来源、独立综合后审/原级成批闭环和所有约定功能实际运行/停止/重启/数据保留仍必需。

保护 User/JS/.kiro/旧D盘及材料外 R56/csproj/工具/文档/暂存，无 push/发布/部署。交接理由是已验证异步停止转换到下一类型化来源权威模型的自然边界；不按时间/工具数强制交接。
