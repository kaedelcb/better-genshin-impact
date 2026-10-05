# 正常迁移候选准备与流程消费者：未完成整体迁移

采用mistletoe-release-first-20261005-v2/c0f40993b/mistletoe-storage-limits-20261005-v1；沿原Goal/opening/预算，新增独立会诊0。

用户新增可用行为仍是源码候选：页面「准备旧数据迁移」只读选择的旧User/OneDragon及config.json，复用R1原映射源码生成标准配置/流程/报告/manifest；导入candidate-ready流程，先持久来源/产物哈希与稳定流程身份索引，重复准备复用同一候选/映射，不覆盖已有流程或原件。坏/同名冲突不导入；源或产物变化拒绝复用，不重建损坏/缺失索引身份。MHA.csproj链接Test/OneDragonMigration/Core两文件，同一源码；引擎仅新增using System.IO适应WPF编译，原算法未改。

「激活迁移」只接受已导入的candidate-ready，逐节点从在线BGI读取资源修订，只有同字节（允许SHA大小写归一化）才进入既有MigrationSwitchTransaction：实际Store写锁静止窗口、快照、声明与版本绑定替换、真实引用读回、实际激活读回、回退演练、提交。保持workflowId/nodeId/顺序/legacyFiltered/once及未知字段。WorkflowStore.LoadSnapshot核本机事务提交，active标签不能绕过未提交/损坏记录；所有经Store读取的执行者消费同一门。Host拒绝关联运行/未决责任存在时切换或回退。分享副本移除本机事务依赖，原本地文档保留。

**仍缺完整正常迁移的一环**：R1产物的标准配置尚未正式安装到BGI实际消费根，当前资源修订不符会明确拒绝激活；页面没有该标准安装/回退消费者。不要把准备、导入或流程7项测试当完整迁移交付。下一项沿真实BGI写方/本版完整运行产物接标准配置安装与可恢复回退，保全原User/源数据/宏脚本；不能自动把标准目录手工复制进真实User。真实生产迁移前仍先准备具体可运行产物/可恢复材料，必要环境动作仅提出唯一操作。

验证：migration-consumer-r1 Rebuild0、7项中1失败（目标复制漏ExpectedContentHash，拒绝而零覆盖）；补同调用链字段后r2 7/7。修改分享副本后r3 7/7；migration-consumer-negative同一变体移除Store提交拒绝与替换输入hash守卫，指定两个关键断言失败、其余5过；finally原子恢复两文件同SHA。migration-consumer-restored以同字节r3不可变DLL/源码复验7/7，原mutation及观察保存。随后准备入口两个新样例加入；migration-preparation-r1/r2为WPF缺System.IO编译失败，已加显式using；r3 Rebuild0、9/9（含旧tuple重复任务/顺序/NextTaskId/once/源字节保持/重复准备同一身份/同名冲突不覆盖，及前述消费者7项）。不重跑未变化BGI/C17或全历史；没有实机/独立后审/认证receipt。

来源为同预算域storage_limits.Session/process_runner、普通argv/真实退出/TRX/产物SHA/源码前后SHA；所有失败原件与现有材料外保护。tools模块/bundle未修改，verify-bundle drift原样保留。新增可恢复版本绑定替换仅走prepared端口，ExpectedContentHash缺省null不写JSON，保留原rename/Added合同；克隆入口显式传递新字段。实际旧现代/legacy事务全量及本版综合回归/后审仍待统一候选，不能伪闭合原98finding/36unknown或026c0ee0新增/unknown。

待做仍包括标准安装/迁移完整回退、nextDay重启错过、LoopDeadline/LocalWait恢复停止收尾正常组合、统一完整BGI+助手运行资源/身份、最后最多一次Sol/high综合实现复核、同一新产物全部功能实际运行/停止/重启/责任与数据保持/真实C17耐久和版本交付。游戏已观察为Session1；未操控游戏/启动BGI或助手/修改真实User。相关当前源码条件改变才复验影响部分。
