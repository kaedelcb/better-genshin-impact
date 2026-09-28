# r56-reference-activation-wiring-2026-09-29 evidence index

Mechanical checks only; no quality/reuse verdict.

- baseline-targeted | test | _workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx | SHA256=b08d358d451e291b5450029c5adb164657956de9dab0d371a1f5303de2707c2a
  Purpose: 开工同条件定向基线（R56/R58 迁移夹具 70/70）; conditions: 独立 baseline worktree @ 22ccd6ee2；R56MigrationSwitchTransactionTests|R58MigrationRehearsalTests；-p:DeployToBgiTools=false
- baseline-assistant-full | test | _workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx | SHA256=e854aa4b960867f4ce514697633c42acff57c9deb9b4eaa6b72fdc1eb3f584ac
  Purpose: 开工同条件助手全量基线（1570/2/0/1572）; conditions: 独立 baseline worktree @ 22ccd6ee2；全量；-p:DeployToBgiTools=false
- targeted-final-frozen | test | _workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx | SHA256=9b27e2bd79f49b436d527b2f2efdd4c8dd8d4ea8b5ff38d58370233bfa10ed73
  Purpose: 本批定向回归 99/99（含 29 条新夹具）；矩阵各行反例证据; conditions: 主工作区最终源码（文档更新后复跑）；R56|R58|R56ReferenceActivationWiring；-p:DeployToBgiTools=false
- assistant-full-final-frozen | test | _workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx | SHA256=b596b9c85a91f068fb72ab4921af191e33e25abccab7bf7f7286c3382f69b6b7
  Purpose: 本批助手全量回归 1599/2/0/1601; conditions: 主工作区最终源码（文档更新后复跑）；全量；-p:DeployToBgiTools=false
- subagent-readonly-audit | consult | _workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md | SHA256=e2c5851064c985945b77e8f4243a3ad16381589d10a808fe007b1e9239aeaa99
  Purpose: 固定开工 ref 的只读子 Agent 独立核查（跨程序集可达性/写方消费方/写集与回滚归属）; conditions: 只读、零写入；固定 ref 22ccd6ee2；主执行者按最终源码复核
- field-audit | document | _workflow/r56-reference-activation-wiring-2026-09-29/findings.md | SHA256=48ddc0947bc9142687f9b4604a6de291958ce8972b81d7d088d0e47f4200e31b
  Purpose: 现场审计（旧阶段标记语义、跨程序集可达性、写方清单）; conditions: 开工只读审计，绑定开工 HEAD
- claims-regeneration | component | _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt | SHA256=be653bae6e2fced7f6e4cae5314e542a538a7274c3fd054a19cf5366818423bb
  Purpose: 声明面再生差异（+4 / -0，623→627 行）; conditions: CLAIM_SURFACE_REGENERATE=1 再生后清除变量复跑守卫通过
- testid-comparison | component | _workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json | SHA256=416ae2288e8dd1eba5b55f32665e031204d3d3b4bbc97f7a121e481d2cf54c62
  Purpose: 逐 testId 差集 added=29/removed=0/changed=0/unchanged=1572; conditions: 主工作区最终源码；TRX 解析
- deploy-target-post | component | _workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json | SHA256=490edb152357b7e99f6a33a38f7313830c03cbd148c8cc951e6e54776158d26b
  Purpose: 部署目标事后清单（1158 文件；最新写入时间 2026-09-27 早于本批）; conditions: Get-FileHash 清单；本批全部构建/测试带 -p:DeployToBgiTools=false
- mut-M1-no-readback-confirmation | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M1-no-readback-confirmation/record.json | SHA256=3bc15cfc72d7d57569c0e21639ef6d1e60c9623541e8f45c9cda160c21fd9c9a
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M2-no-cross-check-with-change-registry | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M2-no-cross-check-with-change-registry/record.json | SHA256=2ca554cc9d6de4f50d632513271fd3d4734b3a64350a59d1718d821ec7a4a83e
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M3-no-outside-write-detection | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M3-no-outside-write-detection/record.json | SHA256=cb0407492acbe44b8abb62e030d13b8b4aba33ebbee5df5dea5b46eabe3f7dac
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M4-no-activation-target-membership | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M4-no-activation-target-membership/record.json | SHA256=2f2eea34e6674e9716afee438218303577072f8d777dd9838f91d9b6b1d738ad
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M5-no-commit-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M5-no-commit-recheck/record.json | SHA256=8af3736c9e739537fb67e4926ec2321fe1b83b4e4d772fed6b6cb465c1752d7a
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M6-no-stage-mark-gate | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M6-no-stage-mark-gate/record.json | SHA256=a5a4fffd509f3be492449c31dbbdb6a7b443e8c61b53f5d866b945ebc6e2fd5e
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M7-no-activation-writeset-hash-sync | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M7-no-activation-writeset-hash-sync/record.json | SHA256=cb9f3c6b0f0888381bc7121a82e832a404e11ccf566e18611e9cd6d432fca621
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M8-no-reference-gate-for-activation | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M8-no-reference-gate-for-activation/record.json | SHA256=477eefca9a28844a539be8e110e5907bb77ea723fd3d19c19ffd9004a7253c16
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M9-fail-open-on-non-success | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M9-fail-open-on-non-success/record.json | SHA256=bb83051887ef7ac31867d3af97037cabfb959103c79ebcb07dfb4e7305848e6e
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M10-no-production-commit-gate | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M10-no-production-commit-gate/record.json | SHA256=4ebde33905c8b21f2ad0e91c364493751ca90a13ead739d0cee3160a4932eb65
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M11-no-lock-guard-on-real-effects | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M11-no-lock-guard-on-real-effects/record.json | SHA256=20d6983f95bd542f0a73cd680e06f0148fcaec658103fb30510d2f09c8a86872
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M12-stage-persisted-before-effect | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M12-stage-persisted-before-effect/record.json | SHA256=53417556c739b6e0459dd719af7e40ad805937e6d15142299e0c57148acbb9c0
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M13-no-activation-readback | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M13-no-activation-readback/record.json | SHA256=21be251ce47eb311f0b2ef0e58215e74640add68ad8000f68c22a6ad84520055
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M14-no-real-evidence-invariants | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M14-no-real-evidence-invariants/record.json | SHA256=2271f3150941bde24362e93741a7194521dc3237cbd753244c4a0bd479f9d8b2
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M15-no-reference-idempotent-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M15-no-reference-idempotent-recheck/record.json | SHA256=f430ccadabda7d2cc30d51372cfad0173d934783e252ae12e5969a9986732088
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M16-no-activation-idempotent-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M16-no-activation-idempotent-recheck/record.json | SHA256=3d2dbf3c288ae6491712117ef7cb0bb2d7a24d41f203c211cc8edbb6ebfa9cd4
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
- mut-M17-writeset-evidence-not-persisted | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M17-writeset-evidence-not-persisted/record.json | SHA256=4693977b409990151b4820069fde1dd43d3b9f23f374a01f2725c82c24468728
  Purpose: 突变记录：baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 主工作区；三段（build/baseline - mutant - restored）独立日志与 TRX
