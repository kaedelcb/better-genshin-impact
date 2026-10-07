# 末次修复复核的来源边界

原 v3 manifest、opening、native/review config 与 owner-policy 未重写。2026-10-08 实际调用 workflow.py audit --manifest _workflow/local-wait-admission-gates-20261004/manifest.json --out 本目录/review-audit --stage review，退出码1，原输出 mechanical_status=blocked、quality_verdict=NOT PROVIDED、error=review bundle drift。Session 记录 failed / 新增0字节，锁已自然释放。没有审核快照可交给原 verify，也不能把输出目录不存在叫 audit 通过。

随后实际调用同目录 verify，错误原件在本线程 rollout；它不能核验缺失的 audit snapshot。工具漂移保持原级，没有修改 bundle、owner-policy、receipt、opening 或旧 manifest 以变绿。

依当前完整可用/交付优先政策，沿第1次授权综合复核的等价普通来源方法继续：当前所有源码、必要合同/原报告与实际 Git 固定到新不可变文本快照，二进制/产物另绑定实际身份；实际受控 Rebuild/TRX、源前后 SHA、两根因有限 P/F/P 和精确恢复、同一 product 模块/实际 Windows Session/原生 UI 来源/正常进程退出/User保留逐项可查。不称旧认证 pass/permit。

绿色及恢复阶段同一305条执行身份为303 Passed、原 corrupt/unsupported 两 LocalWait Failed、0 Other；编译输入全部逐字节相同，突变只改变各自声明目标，恢复后的当前源码与实际产品分别读回。现有回归/旧基线/SDK/工具未知继续保持其原结论，等价来源没有认证全部依赖。

最后一次 Sol/high 由独立原生子 Agent 自主只读固定全项目及调用链，判断新两重要问题、原级相关义务与本版现实风险。准备不计派发，持久 slot2 dispatch_intent 后失败/未知占最终一次。原 allowance.json 初始0/2、第1次used1/remaining1、旧extra2/2和原报告blocked均保留，不重置。
