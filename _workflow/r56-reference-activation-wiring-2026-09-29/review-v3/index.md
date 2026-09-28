# r56-reference-activation-wiring-2026-09-29 evidence index

Mechanical checks only; no quality/reuse verdict.

- baseline-targeted | test | _workflow/r56-reference-activation-wiring-2026-09-29/baseline/targeted-baseline.trx | SHA256=b08d358d451e291b5450029c5adb164657956de9dab0d371a1f5303de2707c2a
  Purpose: 开工同条件定向基线（R56/R58 迁移夹具 70/70）; conditions: 独立 baseline worktree @ 22ccd6ee2；-p:DeployToBgiTools=false
- baseline-assistant-full | test | _workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx | SHA256=e854aa4b960867f4ce514697633c42acff57c9deb9b4eaa6b72fdc1eb3f584ac
  Purpose: 开工同条件助手全量基线（1570/2/0/1572）; conditions: 独立 baseline worktree @ 22ccd6ee2
- targeted-final-frozen | test | _workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final-frozen.trx | SHA256=d46576576ea55e3209d9dfa606a2346e2735dcceae2ba3b3222299e3a57cf3a3
  Purpose: 本批定向回归 119/119（含 49 条新夹具，两轮会诊修复后）; conditions: 主工作区最终源码；-p:DeployToBgiTools=false
- assistant-full-final-frozen | test | _workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx | SHA256=c7b53c34d27b4f2ee3cfb2bb211776c0c8011efd57e5228a487c1c1a34726305
  Purpose: 本批助手全量回归 1619/2/0/1621; conditions: 主工作区最终源码；全量
- subagent-readonly-audit | consult | _workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md | SHA256=e2c5851064c985945b77e8f4243a3ad16381589d10a808fe007b1e9239aeaa99
  Purpose: 固定开工 ref 的只读子 Agent 独立核查; conditions: 只读、零写入；固定 ref 22ccd6ee2
- field-audit | document | _workflow/r56-reference-activation-wiring-2026-09-29/findings.md | SHA256=48ddc0947bc9142687f9b4604a6de291958ce8972b81d7d088d0e47f4200e31b
  Purpose: 现场审计 + 会诊发现处置 + 边界（F-1..F-12）; conditions: 开工只读审计与会诊后修订
- claims-regeneration | component | _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff.txt | SHA256=be653bae6e2fced7f6e4cae5314e542a538a7274c3fd054a19cf5366818423bb
  Purpose: 第 1 轮声明面再生差异（+4/-0）; conditions: CLAIM_SURFACE_REGENERATE=1 再生后清除变量复跑守卫通过
- claims-regeneration-r2 | component | _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r2.txt | SHA256=83348a4ea8a58395d314aa715293e6fce647e675252e27e148bfbd0a7af89fb1
  Purpose: 第 2 轮声明面再生差异（+2/-0，627→629）; conditions: 同上
- mutations-summary | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/summary.md | SHA256=958ce01ef06f5515fc9085d18b396ecf0e3ff3f29638bc4850e84c877556fe35
  Purpose: 28 项反向突变逐项补丁与三段判定（供审查者核对判别力）; conditions: 主工作区；逐项含独立日志与 TRX
- testid-comparison | component | _workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json | SHA256=e033dcd640b801ea615db0d4b7b010cdf84bb3bc1dbfc911677f0c06907fa257
  Purpose: 逐 testId 差集 added=49/removed=0/changed=0/unchanged=1572; conditions: 主工作区最终源码
- deploy-target-post | component | _workflow/r56-reference-activation-wiring-2026-09-29/final/deploy-target-post.json | SHA256=490edb152357b7e99f6a33a38f7313830c03cbd148c8cc951e6e54776158d26b
  Purpose: 部署目标事后清单（1158 文件；最新写入早于本批）; conditions: Get-FileHash 清单；全部构建带 -p:DeployToBgiTools=false
- review-round1-report | consult | _workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md | SHA256=fc65079cf5684447a1fde127ef9570c54111faa9544bba228f2c1ddcdbf70545
  Purpose: 第 1 轮会诊报告原文（5 MUST + 4 IMPORTANT + 1 建议级）; conditions: gpt-6-astra / medium；只读；attempts=1（子批计数 1/8）
- review-round2-report | consult | _workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round2-report.md | SHA256=59a4e893d7273d50843cb6881dc3352a1ff7c8abbf708f305b66df9e98818021
  Purpose: 第 2 轮（验证轮）会诊报告原文（已闭环 4 / 未闭环 5 + 新报 MUST 4 + IMPORTANT 4）; conditions: gpt-6-astra / medium；只读；attempts=1（子批计数 2/8）
- claims-regeneration-r3 | component | _workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r3.txt | SHA256=03ec3a5f5b246779d6b92ab09f17ee71c7273247a4b5ed7bed1a2ace0519f81a
  Purpose: 第 3 轮声明面再生差异（+3/-0，629→632）; conditions: 同上
- mut-M1-no-readback-confirmation | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M1-no-readback-confirmation/record.json | SHA256=82f7d17e14cd84d5c2a2621ee2007cd38a5cf2f654c3888fd897947471123f8b
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M2-no-cross-check-with-change-registry | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M2-no-cross-check-with-change-registry/record.json | SHA256=a771468a8da2c83a543c1626e858e6e28e1db0747290910229776ad615c9c9ec
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M3-no-outside-write-detection | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M3-no-outside-write-detection/record.json | SHA256=675571a50cae775454ad04366269cef703e0b890bfec46a257a59eb1e2ff32a8
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M4-no-activation-target-membership | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M4-no-activation-target-membership/record.json | SHA256=31f402ae686f5997460af5867a887f84d797917affa34e87fe29992b3f5afa8e
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M5-no-commit-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M5-no-commit-recheck/record.json | SHA256=48a1462edf3015c713144d75e45cf373ddaef375ec6d5a904b4da89a6684e529
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M6-no-stage-mark-gate | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M6-no-stage-mark-gate/record.json | SHA256=1d0a52e6c2ac5c3ad0b59c75e7e89379e18303ffe91f354f0793f3744ebc01a3
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M7-no-activation-writeset-hash-sync | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M7-no-activation-writeset-hash-sync/record.json | SHA256=fe0a87f220523a0c29b090ddfd975e11436fa33fa528871cc92b92d6c6399f53
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M9-fail-open-on-non-success | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M9-fail-open-on-non-success/record.json | SHA256=269a845cde2b1e34bcb8e547cec444df5a4958d4540d9bfa9a66f9924e25eb80
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M10-no-production-commit-gate | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M10-no-production-commit-gate/record.json | SHA256=f408ee6ef613a944081c2bd77fc29cf23bbe3aeee2c7b9612ad7ca85eaf4f02a
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M11-no-lock-guard-on-real-effects | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M11-no-lock-guard-on-real-effects/record.json | SHA256=502776e7405515199b84c9b522f4d93f9f0882962a95f590d19c54d738117996
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M12-stage-persisted-before-effect | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M12-stage-persisted-before-effect/record.json | SHA256=35299192439c802f4836242548296087276f9dd7107d966f50aa811090fe2f19
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M13-no-activation-readback | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M13-no-activation-readback/record.json | SHA256=c244a172c504f22e55c786b6daf164177aee2fc7d78ee2c2adbb60beb3e2a3df
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M14-no-real-evidence-invariants | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M14-no-real-evidence-invariants/record.json | SHA256=e81ddd5dbfa71904c846801bc8616316b72c766abd3f0e274398d219d615f19a
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M15-no-reference-idempotent-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M15-no-reference-idempotent-recheck/record.json | SHA256=269b85dadef14c0e9d697c9ca667f17bbaa7e431c5a58d2b86bef6fdac709ab6
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M16-no-activation-idempotent-recheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M16-no-activation-idempotent-recheck/record.json | SHA256=32a45793c9f1d036500a3195b51060adbd560cbd0c18d4b16e5cce746f1146fe
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M17-writeset-evidence-not-persisted | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M17-writeset-evidence-not-persisted/record.json | SHA256=967205b2af3208bd0aaf2e013e69b25ad278ad590687c725ba58cb02cfca5b9b
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M18-no-activation-undo | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M18-no-activation-undo/record.json | SHA256=7b851f3f755b45a6ccf2034086dfa271366502258c7b8350aeb5b5a3bc7e7236
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M19-no-activation-pre-drift-check | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M19-no-activation-pre-drift-check/record.json | SHA256=403119ac3bdb9372a79b9becb272ee3b2db159beee48efc0bb8e2443dd14a040
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M20-no-new-file-detection | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M20-no-new-file-detection/record.json | SHA256=c0f3a6001ea3f017ae563e813edeef6f1c5aa480f44e0f8cb2b7e89eb53d0184
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M21-no-full-baseline-verification-on-rollback | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M21-no-full-baseline-verification-on-rollback/record.json | SHA256=dac07f0c8fa437d232403edcf0f3ba414211754b4f741049815aa4f9b80dad63
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M23-commit-gate-not-bound-to-instance | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M23-commit-gate-not-bound-to-instance/record.json | SHA256=7337ff5a22f82b64621efcb1014db344871d70ec79691ec502324fa1255a21e3
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M24-no-change-registry-freeze | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M24-no-change-registry-freeze/record.json | SHA256=b34da4028b844fd0300d72abbdd482230d11caa509dc4c26f361282cd2969277
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M25-no-port-exception-containment | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M25-no-port-exception-containment/record.json | SHA256=025365a4f68b7c0b68d666d6b01b7ad347628ba93b6202afe85fa7e812ff0e24
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M26-no-activation-status-precheck | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M26-no-activation-status-precheck/record.json | SHA256=4c9610dd55c2726ef7603b6ec36c8c5876b80394d98a35db4db3fb6752684db1
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M27-no-d13-transition-restriction | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M27-no-d13-transition-restriction/record.json | SHA256=f012a3d9ac851f45c5f2a2a47ff4d9795f4781da825361e653801ec39f860173
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M28-added-target-overwrite-allowed | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M28-added-target-overwrite-allowed/record.json | SHA256=3eff314c5afc305e8c5086c1f1a9366ce7675e8da34fdf6e9f19818ed6020c94
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M8-no-activation-stage-gate | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M8-no-activation-stage-gate/record.json | SHA256=c3736039922135df45e63c924ff0d3a2a6b415472eb5e83197352323f7e35e63
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M29-no-ownership-precheck-before-write | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M29-no-ownership-precheck-before-write/record.json | SHA256=aafbbe8591bb1862e1349af014857a64619250aa3bbf79ee96d3317b2b2f8356
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M30-no-addition-ownership-enforcement | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M30-no-addition-ownership-enforcement/record.json | SHA256=3e7ccd3f795cb08b4342376e982656da27af0c176e6cc0856f2b110b944d82ed
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M31-no-activation-content-binding | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M31-no-activation-content-binding/record.json | SHA256=ec82e8398409d2bc915edcc731e63a3be4bbe2c2f576e2680d7741ad2c040472
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M32-no-missing-baseline-half | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M32-no-missing-baseline-half/record.json | SHA256=a186088937d99e5c335db5d79d14feaade50b868e5ea6ba3c38332f4ccec9a6e
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M33-no-reentrancy-guard-on-rollback | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M33-no-reentrancy-guard-on-rollback/record.json | SHA256=5f6e1c766371d3666f12a8339228bbaaef3f6be051ecb129b905fc953e0f04dd
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
- mut-M34-no-duplicate-change-identity-check | component | _workflow/r56-reference-activation-wiring-2026-09-29/mutations/M34-no-duplicate-change-identity-check/record.json | SHA256=abd8e785ad96a8caa22027de9bfa150c276afd40d0bf38c2417bfae8124ca0d3
  Purpose: 突变记录：补丁原文 + baseline Passed / mutant Failed / restored Passed 且源码逐字节恢复; conditions: 三段独立日志与 TRX，绑定源文件哈希
