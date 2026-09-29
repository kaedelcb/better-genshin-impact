# R7 集中处置与最新模型政策

状态：候选修复完成，等待 R8 独立复核，未宣称闭合、未部署主线。

- R7-1 MUST：execution_evidence 保存实际输入字节，规范化 argv/工具链/平台/环境值哈希/条件/产物名；B/M/B 必须同条件，patch 必须等于实际输入的 canonical unified diff。参数/环境变化及假 patch 均有真实执行反例，关系守卫有反向突变。
- R7-2 IMPORTANT：extra_files 作为稳定合同的内容身份纳入许可；源码可实施变化，合同变化须重审，合同守卫有反向突变。
- R7-3 IMPORTANT：audit 绑定 manifest 路径、原字节和 canonical digest，dispatch 验证实际消费身份，implementation pass 与当前 manifest 一致；测试含五类字段改动及同批 A/B audit 借用拒绝。完整 begin/注册/前审/实施/真实执行/audit/实现审查/closeout 测试不 mock 门禁；模型报告是明确的 transport fixture，不能冒充真实模型质量审查。
- R7-4 MUST：Windows Job Object 在 helper 启动任何后代前完成归属；保存 PID 创建时间和 Job identity；BaseException/超时保留恢复标记、inflight 与锁，只终止本次树。真实 KeyboardInterrupt 与 RuntimeError 注入在后代 heartbeat 已运行后发生，验证清理 active=0、heartbeat 停止、锁保留。守卫做反向突变。当前版本仅 Windows 支持，其他平台明确阻断。
- R7-5 IMPORTANT：build 前只展开 run argv，build 完成后解析/hash 新 exe，测试真实生成新解释器 exe/DLL 后直接运行；不是产品 C# 构建验证。
- R7-6 MUST：comparison baseline/final 两端都必须有认证执行来源、正确用途、同条件及先后顺序；历史来源不冒充当前绿灯。真实旧版本与新版本比较、缺 baseline 来源、历史充当当前绿灯反例均覆盖，历史绿灯守卫突变验证。

用户最新明确覆盖固定 Astra 方案：所有会诊逐次智能判断，默认 gpt-6-sol/medium；复杂、高风险或风险未排除选 gpt-6-astra/medium。首轮通常较复杂，但不按轮次强制。执行者评估八维风险和当前证据，assessment 绑定当前文件、manifest 和请求历史；工具检查完整性和一致性，不自称理解风险。辅助渠道也要求 assessment，原质量门和同批预算不减。

本次 R8 选择 Astra/medium：进程后代生命周期、跨工作区锁、执行来源与审批身份的交互复杂，原六项含 MUST 正确性风险；Sol 默认适合边界已明确的普通请求，本次不适用。不是因为第八轮而选 Astra。

补充修复：快照保留全局原始 Git status，但并发稳定性比较排除本次 review-process 输出目录；HEAD/分支/scoped diff/源字节及输出树外状态仍须一致。已跟踪工作流目录下创建新快照有回归。

边界：规则和工具不能防止有写权限的执行者主动绕过所有门或伪造全部账本；不保证零 BUG。无产品运行、无 hooks、无后台任务、无新会话，R5.6 产品改动未碰。R7真实本地模型已自主发现导航外 storage.py 丢更新，原始报告/事件保留。本次真实模型复核使用新增 Job runner，不用旧 subprocess.run 冒充验证。
