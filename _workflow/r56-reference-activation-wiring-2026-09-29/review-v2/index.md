# r56-reference-activation-wiring-2026-09-29 evidence index

Mechanical checks only; no quality/reuse verdict.

- baseline-targeted | test | _workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx | SHA256=b08d358d451e291b5450029c5adb164657956de9dab0d371a1f5303de2707c2a
  Purpose: 开工同条件定向基线（R56/R58 迁移夹具 70/70）; conditions: 独立 baseline worktree @ 22ccd6ee2；-p:DeployToBgiTools=false
- baseline-assistant-full | test | _workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx | SHA256=e854aa4b960867f4ce514697633c42acff57c9deb9b4eaa6b72fdc1eb3f584ac
  Purpose: 开工同条件助手全量基线（1570/2/0/1572）; conditions: 独立 baseline worktree @ 22ccd6ee2
- targeted-final-frozen | test | _workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx | SHA256=e49a8c39a4b4becc6629891558b0f3d515a60b4f93cfb91eaa3ce803f19f7c29
  Purpose: 本批定向回归 111/111（第 1 轮会诊修复后）; conditions: 主工作区最终源码；-p:DeployToBgiTools=false
- assistant-full-final-frozen | test | _workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx | SHA256=656ba5b71b24201f1cfe777f350fa4142f2390e9388a24c9c5ee1daf622ccfe1
  Purpose: 本批助手全量回归 1611/2/0/1613; conditions: 主工作区最终源码；全量
- subagent-readonly-audit | consult | _workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md | SHA256=e2c5851064c985945b77e8f4243a3ad16381589d10a808fe007b1e9239aeaa99
  Purpose: 固定开工 ref 的只读子 Agent 独立核查; conditions: 只读、零写入；固定 ref 22ccd6ee2
- field-audit | document | _workflow/r56-reference-activation-wiring-2026-09-29/findings.md | SHA256=48ddc0947bc9142687f9b4604a6de291958ce8972b81d7d088d0e47f4200e31b
  Purpose: 现场审计 + 会诊发现处置 + 边界（F-1..F-12）; conditions: 开工只读审计与会诊后修订
- claims-regeneration | component | _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt | SHA256=be653bae6e2fced7f6e4cae5314e542a538a7274c3fd054a19cf5366818423bb
  Purpose: 第 1 轮声明面再生差异（+4/-0）; conditions: CLAIM_SURFACE_REGENERATE=1 再生后清除变量复跑守卫通过
- claims-regeneration-r2 | component | _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r2.txt | SHA256=83348a4ea8a58395d314aa715293e6fce647e675252e27e148bfbd0a7af89fb1
  Purpose: 第 2 轮声明面再生差异（+2/-0，627→629）; conditions: 同上
- mutations-summary | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/summary.md | SHA256=862f963462e9ee639a313c2603caed044e4e1d6a93387419de8cb09d12c6b715
  Purpose: 28 项反向突变逐项补丁与三段判定（供审查者核对判别力）; conditions: 主工作区；逐项含独立日志与 TRX
- testid-comparison | component | _workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json | SHA256=c91926b7b73a8abc833dc17fa240f1673de54808e40cb239e336a0750ed0d4ed
  Purpose: 逐 testId 差集 added=41/removed=0/changed=0/unchanged=1572; conditions: 主工作区最终源码
- deploy-target-post | component | _workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json | SHA256=490edb152357b7e99f6a33a38f7313830c03cbd148c8cc951e6e54776158d26b
  Purpose: 部署目标事后清单（1158 文件；最新写入早于本批）; conditions: Get-FileHash 清单；全部构建带 -p:DeployToBgiTools=false
- review-round1-report | consult | _workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md | SHA256=fc65079cf5684447a1fde127ef9570c54111faa9544bba228f2c1ddcdbf70545
  Purpose: 第 1 轮会诊报告原文（5 MUST + 4 IMPORTANT + 1 建议级）; conditions: gpt-6-astra / medium；只读；attempts=1（子批计数 1/8）
- mut-M1-no-readback-confirmation | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M1-no-readback-confirmation/record.json | SHA256=a6e3f684a53621c596483084654ab0c2c2bf8e3b8d0560c1c4257ab6569fd106
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M2-no-cross-check-with-change-registry | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M2-no-cross-check-with-change-registry/record.json | SHA256=9c1d0b59678df7a26b014361e131701bc6eaf213a563821345f6a2170a74d11a
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M3-no-outside-write-detection | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M3-no-outside-write-detection/record.json | SHA256=e7dce2d5c285fe9e8ce3c6a16b82b9b505337e90c4ae5ee87b8b60c141e5e7d9
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M4-no-activation-target-membership | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M4-no-activation-target-membership/record.json | SHA256=09886fa55ca87f1ae8d086e0c872d46a55802ff84bcb427ab05b3836e34a6d15
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M5-no-commit-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M5-no-commit-recheck/record.json | SHA256=cb673e9b70204e33c6b57726fa304bbb4c3eaceff829a6348140aeb92911c656
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M6-no-stage-mark-gate | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M6-no-stage-mark-gate/record.json | SHA256=3ccfce4151cc09cbe1240d0a3b93d550707cd568f8213a9f9f53232a225b6155
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M7-no-activation-writeset-hash-sync | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M7-no-activation-writeset-hash-sync/record.json | SHA256=9a5beb7b454afc35e9aabf1077080514d20bc7da0744dd6627fe83bfe69b6804
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M9-fail-open-on-non-success | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M9-fail-open-on-non-success/record.json | SHA256=3840e38224363d7d0e082fa4643febfd0a5e83842582023f5c38ae2795015e4e
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M10-no-production-commit-gate | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M10-no-production-commit-gate/record.json | SHA256=efbe4366b2617cce6cc6e092c422acb778974a0cab6560e69f0d92664ec79a32
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M11-no-lock-guard-on-real-effects | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M11-no-lock-guard-on-real-effects/record.json | SHA256=602852b9270dd4d46cd125515c293c7765d829cabc332deeb292503959016e78
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M12-stage-persisted-before-effect | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M12-stage-persisted-before-effect/record.json | SHA256=45a2c6e8c68bf6c54a5e9beef6e387bb4ac477d5f239540f4785cddfaffaf383
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M13-no-activation-readback | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M13-no-activation-readback/record.json | SHA256=2ae986c23d46df58aef35145674ad88868406b24561230cc320e7505c8cdc487
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M14-no-real-evidence-invariants | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M14-no-real-evidence-invariants/record.json | SHA256=7b2a48d206f78062f5f221eb3ad9b7f3c21d9517189c7ab61799920ab0e875bd
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M15-no-reference-idempotent-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M15-no-reference-idempotent-recheck/record.json | SHA256=af2dd9495cbd862a016da39aea76010af40ca9554ef62f3bd098b6555f44d2b7
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M16-no-activation-idempotent-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M16-no-activation-idempotent-recheck/record.json | SHA256=85daa13cf4d0c2db89f54be1024b828e17d7bd0acfd81175195968868189a3bc
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M17-writeset-evidence-not-persisted | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M17-writeset-evidence-not-persisted/record.json | SHA256=0fb93c26c8592dc7bd32b7e3927b51668b71c077dab0dce36862d6fac2ddcf0e
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M18-no-activation-undo | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M18-no-activation-undo/record.json | SHA256=e495263835c2a58004c9326c453c9f066d25d35392bee3aaf3dc5c55caeb93f2
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M19-no-activation-pre-drift-check | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M19-no-activation-pre-drift-check/record.json | SHA256=cb8cfbb9ec5ecc4a9b82126f5c3ee9a5767c24a6163dcc3e59515be43501e3dc
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M20-no-new-file-detection | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M20-no-new-file-detection/record.json | SHA256=3bb1277b7cae306818657c6f077e6c6ad10dbf6c34bfa2243d27e8a230161c30
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M21-no-full-baseline-verification-on-rollback | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M21-no-full-baseline-verification-on-rollback/record.json | SHA256=966334ff05b7e3d021d98d3005885a7f8a43455f675d779d80657cdf91483f21
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M22-no-addition-ownership-evidence | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M22-no-addition-ownership-evidence/record.json | SHA256=9d3ca7065fceada3928a3a2c44eb94efc6585f1247c142d37cecc04b44d870ff
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M23-commit-gate-not-bound-to-instance | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M23-commit-gate-not-bound-to-instance/record.json | SHA256=bd2362b3796d4ed20361d0027e2e8d7b15564d20f0df26bf4435c78f1335ad03
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M24-no-change-registry-freeze | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M24-no-change-registry-freeze/record.json | SHA256=7e86c37d48a61e6719995b1d383af63be2ec511fa0803e70642923755b06154f
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M25-no-port-exception-containment | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M25-no-port-exception-containment/record.json | SHA256=b39ad5dc3b7f4e9ec1f6ef6d5de515c6ee727ffe089186174cf1ee92dc0bf78b
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M26-no-activation-status-precheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M26-no-activation-status-precheck/record.json | SHA256=da40c8c1332cfb5c21855153c4b18317c5237e5453a1d5425a779a3d1b7c2855
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M27-no-d13-transition-restriction | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M27-no-d13-transition-restriction/record.json | SHA256=f9d2534ad53a4860d799a2594106475ee6eb600ba059f8037387e4b1b5848435
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M28-added-target-overwrite-allowed | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M28-added-target-overwrite-allowed/record.json | SHA256=e744857067ceb7297afe0bdc4c0182a68c520e8926ef189d137677fc330e7e53
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
- mut-M8-no-reference-gate-for-activation | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M8-no-reference-gate-for-activation/record.json | SHA256=daa9afda0b6e3d52ac934ae725b35a9ed24689826a29db4ecd99252f68ef63be
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段（baseline - mutant - restored）独立日志与 TRX，绑定源文件哈希
