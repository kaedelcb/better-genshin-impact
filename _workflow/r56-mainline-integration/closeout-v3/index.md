# r56-mainline-integration-2026-09-28 evidence index

Mechanical checks only; no quality/reuse verdict.

- opening-snapshot | document | _workflow/r56-mainline-integration/opening.json | SHA256=c083c11ee6003b079b9772779ba040b7258883db6e13fa5412ef25e5fa1e705a
  Purpose: 本批开工快照（分支 main-OldTeaBag-B168、HEAD 8a3ee6c4c、工作区状态、三个源的开工哈希、7 行原始风险矩阵）; conditions: python -B tools/mistletoe/workflow.py begin --manifest …；无产品副作用；此快照早于本批导入
- intake-equivalence | document | _workflow/r56-mainline-integration/intake-equivalence.json | SHA256=f31d99f31bd3329986ad20a3f7d590bd686e7392775cbada1927719039c60424
  Purpose: 接收等价性：唯一测试增量 48/0、blob 相同、工作区 4C101FF3…；25 个材料文件的暂存内容与来源 blob 逐项相等; conditions: git checkout <来源提交> -- <路径> 导入后逐文件哈希比对
- intake-line-endings | document | _workflow/r56-mainline-integration/source-delivery-line-endings.json | SHA256=c4f555487d3bc15ffbef633bca0b074faaa7c9911f905f47137e54ab5126a244
  Purpose: 摄入材料的行尾形态与来源 worktree 字节对照（25/25 等于来源现行字节；4 个 JSON 在来源侧为 CRLF 工作区形态）; conditions: 逐文件 SHA-256 与 git blob 比对
- intake-note | document | _workflow/r56-mainline-integration/source-delivery/INTAKE-NOTE.md | SHA256=2ac97afb83684a6bdcfe042c42d9745b15da24a01552ab643853b13f2ab3a8ba
  Purpose: 摄入说明：来源 worktree/HEAD、路径映射（为何收纳到 _workflow 下）、逐字节结论、刻意未收录清单、边界; conditions: 人工编写；映射与哈希由 intake-equivalence.json 支持
- src-report-audit | document | _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/report.md | SHA256=044a62d06dee49fcdb8e87fad6adb30521e03a276217785faceb2ef817369d97
  Purpose: 来源交付 A 报告（含 7 行覆盖矩阵、回归/突变证据、提交清单、主线集成要求）；来源 SHA-256 044A62D0…9D97; conditions: 逐字节摄入来源提交 c11cb45f6 的该文件
- src-acceptance-audit | document | _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/acceptance.md | SHA256=377fc50c072818764fa178bafe14cf4f256464368165b41982ee378eb77055ae
  Purpose: 来源交付 A 验收单（范围、允许改动、覆盖矩阵要求、回归/突变要求、关闭门禁）; conditions: 逐字节摄入来源提交 c11cb45f6 的该文件
- src-mutation-results | document | _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/followup/results.json | SHA256=e6f2af18317deddf9b5565e2ee67deed42559312463b4784912b61fd5835020e
  Purpose: 来源 4 项断言突变的记录（原始/突变/恢复哈希、命中行、退出码），本批据此做 mutant 哈希交叉核对; conditions: 逐字节摄入来源提交 c11cb45f6 的该文件
- src-mutation-source-hashes | document | _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/source-hashes.txt | SHA256=bc9fc94da99150496824507ec5d19ac9ff96e8a99d5a48e5e81e44ef3d4847bd
  Purpose: 来源 extra 突变的 before/mutant/restored 哈希（未记录精确 needle）; conditions: 逐字节摄入来源提交 c11cb45f6 的该文件
- src-prep-report | document | _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/report.md | SHA256=bee488672fa4649817333cdd99ee1e9d99a979bcd48caaf6d8386442e5ff4fed
  Purpose: 来源交付 B 报告：固定基线审计结论、去重核销、正式消费批最小实施顺序、未决事项；来源 SHA-256 BEE48867…4FED; conditions: 逐字节摄入来源提交 90588159b 的该文件
- src-prep-acceptance-matrix | document | _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance-matrix.md | SHA256=0a94a0c4ff4288852a7d8fc763adcdccfd6af1658291f3aa2b5d23bda7b65103
  Purpose: 来源交付 B 逐状态验收矩阵（A–F 前置 × 状态/证据/余证/门禁）; conditions: 逐字节摄入来源提交 90588159b 的该文件
- src-prep-acceptance | document | _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance.md | SHA256=12a65a86e1284890b9ee81ff617d8edde711fe8dd4c19c01877c0c7c8c887fc3
  Purpose: 来源交付 B 验收单（准备包与未来正式集成的分层判据）; conditions: 逐字节摄入来源提交 90588159b 的该文件
- src-prep-contact-inventory | document | _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/contact-inventory.md | SHA256=1b82287e3d0565e43840e1601f96b1c2c749ce34032359909d1d150830c0b349
  Purpose: 来源交付 B 六项接点清单（条款、定义与调用者、状态、证据能与不能证明的范围）; conditions: 逐字节摄入来源提交 90588159b 的该文件
- src-prep-search-audit | document | _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/search-consumer-audit.md | SHA256=8baccc2e2deff1cb783caa5c7ec75e9954ead87e64b75f3dfe1bb3646baa1f53
  Purpose: 来源交付 B 有界搜索记录（生产消费者命中/未命中范围与命令）; conditions: 逐字节摄入来源提交 90588159b 的该文件
- src-prep-registration | document | _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/delivery-registration.json | SHA256=d8aae0c50c0235fc607fabb66fbb32c0000222e4797de88fc48df1a772a090bf
  Purpose: 来源交付 B 登记侧车（唯一交付 ID、目标批次、A–F 门禁、主线接管说明）；来源 SHA-256 D8AAE0C5…90BF; conditions: 逐字节摄入来源提交 90588159b 的该文件
- src-prep-manifest | document | _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_workflow/manifest.json | SHA256=556ead0ced17150d951a7ce179d098a9759adf9fe47fd253d05fce892b3f3f53
  Purpose: 来源交付 B 自己的 v2 manifest（范围、来源、证据、修正历史）; conditions: 逐字节摄入来源提交 90588159b 的该文件
- baseline-build-assistant | build | _workflow/r56-mainline-integration/baseline/assistant-build.log | SHA256=2e97f49de2cf518858dc9b055cad19705c898ad6a6fe809b25fff6308bf9a62c
  Purpose: 导入前助手项目 Rebuild（-p:DeployToBgiTools=false，exit 0，59 警告/0 错误）; conditions: dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false
- baseline-build-testproject | build | _workflow/r56-mainline-integration/baseline/testproject-build.log | SHA256=422a5a928cbc440f71843a4021eabc2059c90216fd07048306b572d492b8aaf8
  Purpose: 导入前测试项目 Rebuild（-p:DeployToBgiTools=false，exit 0，84 警告/0 错误）; conditions: dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false
- baseline-targeted | test | _workflow/r56-mainline-integration/baseline/targeted-baseline.trx | SHA256=70112377e235f27312e7b0f0d1b210d956b323f89c2b5c6dfec103ec293c6368
  Purpose: 导入前同条件基线：R56/R58 迁移定向三类 78/78（未导入 theory）; conditions: dotnet test … --no-build --filter R56MigrationSwitchTransactionTests|R58MigrationRehearsalTests|R58MigrationRehearsalHostTests
- baseline-full | test | _workflow/r56-mainline-integration/baseline/assistant-full-baseline.trx | SHA256=f99d1b76f3488f3c0bd1471f9ccf098de0f7d10203a57293c8196f7c9d7acb0a
  Purpose: 导入前同条件基线：助手全量 1567 passed/2 skipped/0 failed/1569; conditions: dotnet test … --no-build（全部用例）
- final-build-testproject | build | _workflow/r56-mainline-integration/final/testproject-build.log | SHA256=9996a02ecae226d7a01f4342df43076596dcd34c427fc112edf4e34914aa0733
  Purpose: 导入后测试项目 Rebuild（-p:DeployToBgiTools=false，exit 0，84 警告/0 错误）; conditions: dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false
- targeted-final | test | _workflow/r56-mainline-integration/final/targeted-final.trx | SHA256=573275689055e2de45836bb297d651f3a5a83bad6cfb427f7eeeb49c6b613039
  Purpose: 集成后定向三类 81/81：三行新增 theory（extra/missing/changed）全部 Passed；风险矩阵 S1/C2/F1/F2 的反例证据; conditions: dotnet test … --no-build --filter 三类迁移夹具；exit 0；81 passed
- full-final | test | _workflow/r56-mainline-integration/final/assistant-full-final.trx | SHA256=d41e8a5caa4152ac96065c8fd5d44e5cf0bf5aa7e566fc1b16c5298edf684d27
  Purpose: 集成后助手全量 1570 passed/2 skipped/0 failed/1572（含声明面守卫与全部既有夹具）; conditions: dotnet test … --no-build；exit 0
- testid-comparison | document | _workflow/r56-mainline-integration/testid-comparison.json | SHA256=8fc962e8f5942652164f9cda4246f54a42f7301e658c147f95fc65ae52eda8d4
  Purpose: testId 差集：定向 78→81（added=3/removed=0/changed=0/unchanged=78）；全量 1569→1572（added=3/removed=0/changed=0/unchanged=1569）; conditions: workflow.compare_trx 逐 testId 对照（同名不折叠）
- deploy-target-before | document | _workflow/r56-mainline-integration/deploy-target-before.json | SHA256=fa2fdda663db7a8719bae60ec50d897451350fd1dac23ebf38e1a0f9b5b9e974
  Purpose: 构建/测试前部署目标目录清单（1158 个文件 + 逐文件 SHA-256 + 清单摘要）; conditions: 本批全部构建/测试开始前采集
- deploy-target-after | document | _workflow/r56-mainline-integration/deploy-target-after.json | SHA256=6ef3e2f8230e06acbd54222bd3cc68464059665f927af4eb98cba5dc693c7a3b
  Purpose: 构建/测试后部署目标目录清单（与前后对比使用）; conditions: 本批全部构建/测试之后采集
- deploy-target-comparison | document | _workflow/r56-mainline-integration/deploy-target-comparison.json | SHA256=797c454e0f6f146961697d03f428f87c0dd388fc3614524933f3c12dc094435e
  Purpose: 部署目标前后清单逐项相同（identical=true，1158→1158）; conditions: 前后清单摘要对比
- claim-manifest-before | document | _workflow/r56-mainline-integration/claims/manifest-before.txt | SHA256=1f2b674365c9f150a0c4e898a54e586177283e07fa5bfb83bd2f4cbb786a5ccc
  Purpose: 声明面再生前清单（618 行），用于评审清单差异; conditions: R5.3 §24.128 追加后、再生前
- claim-preguard | test | _workflow/r56-mainline-integration/claims/claim-preguard.trx | SHA256=3ed9bd4ab09856f67fd4af4a18176f1d9af2c549906afb5999e0fca54643be38
  Purpose: 声明面守卫在追加 §24.128 后按预期失败（新增/变更 1 行、移除 0 行）——证明本批触及声明面、不得援引措辞类豁免; conditions: dotnet test … --filter FullyQualifiedName~ClaimSurfaceGuardTests（不带 CLAIM_SURFACE_REGENERATE）；exit 1
- claim-regen | test | _workflow/r56-mainline-integration/claims/claim-regen.trx | SHA256=229274fa674a819cc7be6ffdecab344e06d078396ab345783bb6577aa379f2c4
  Purpose: 带 CLAIM_SURFACE_REGENERATE=1 再生清单并守卫通过（618→619 行，+1/-0）; conditions: dotnet test … --filter ClaimSurfaceGuardTests（带再生变量）；exit 0
- claim-noenv | test | _workflow/r56-mainline-integration/claims/claim-noenv.trx | SHA256=17daa0d26217b1ac8ce7fb988ea97cad72cd1e2559ca300c4508a850dd618336
  Purpose: 清除 CLAIM_SURFACE_REGENERATE 后复跑守卫通过（清单 SHA 28567E75…FF52，619 行）; conditions: dotnet test … --filter ClaimSurfaceGuardTests（已清除变量）；exit 0；绑定 §24.128.1–.5 的文档状态（§24.128.6 追加后将再次再生复跑）
- claims-diff | document | _workflow/r56-mainline-integration/claims/claims-diff.json | SHA256=07e2ebf5419bfe2dc4a11e7074e8ba0baeb5ad2d0cde4e32e9c7c622bab0aa38
  Purpose: 声明面清单差异摘要（新增/移除行数与清单 SHA 前后值）; conditions: man anifest-before/after 逐行对照
- mutation-records | document | _workflow/r56-mainline-integration/mutations/mutation-records.json | SHA256=ad6417197e7c7896d1135b3a98828a03336475efbc0e39c83ee75c6dc1c6de7c
  Purpose: 本批 5 项反向突变的机读记录（原/突变/恢复哈希、三段 TRX、具名 testId、命中断言行、退出码）; conditions: 由 mutations/run-mutations.py 依来源突变定义重跑并落盘
- mutation-crosscheck | document | _workflow/r56-mainline-integration/mutations/mutation-hash-crosscheck.json | SHA256=ad2fdde105f107a13d6a2328df55b81557878bfed0112800bbb030de038913af
  Purpose: 与来源 records.json/source-hashes.txt 的 mutant 哈希交叉核对（4/5 CRLF 形态完全相同；extra 为等价独立突变）; conditions: 逐突变 CRLF/LF 形态哈希比对
- mutation-runner | document | _workflow/r56-mainline-integration/mutations/run-mutations.py | SHA256=596da1317409956a16a3c4dc35fc5fb82867e24fe59da195d8aa9e3781bd630d
  Purpose: 本批突变执行脚本（needle/anchor/断言行/恢复校验），供复查复现; conditions: Python；不改产品源码之外的任何内容，逐次 finally 恢复
- mut-extra | test | _workflow\r56-mainline-integration\mutations\extra\mutant.trx | SHA256=d09afb4ed83de425fba4018eb31f5403789da697d4542ab151b02b0511d285a7
  Purpose: 反向突变 extra 的 mutant TRX：目标 theory 行 Failed（命中 :line 188）; conditions: dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1
- restored-extra | test | _workflow\r56-mainline-integration\mutations\extra\restored.trx | SHA256=c418c574f682d201758ceef7c6cdab57adafd0d3129729ce67f464ad54ba260e
  Purpose: 反向突变 extra 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed; conditions: 同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…
- mut-extra-log | document | _workflow\r56-mainline-integration\mutations\extra\mutant.log | SHA256=7f340dd8d976a78e3a5a928d8ecdc5006723c383872f9d6e838e3aee4701334d
  Purpose: 反向突变 extra 的执行日志（构建与测试输出、退出码）; conditions: dotnet test 全量输出重定向
- mut-missing | test | _workflow\r56-mainline-integration\mutations\missing\mutant.trx | SHA256=df99b2e324c35898e22a36b9dd1fa58240bd66f8dbd3c832d024a1bc61dbbfcb
  Purpose: 反向突变 missing 的 mutant TRX：目标 theory 行 Failed（命中 :line 188）; conditions: dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1
- restored-missing | test | _workflow\r56-mainline-integration\mutations\missing\restored.trx | SHA256=8da6955f0042b4dbf4a01f6ff3133d56acfba8ae3d4f5b45cfe03e03c5f6fca5
  Purpose: 反向突变 missing 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed; conditions: 同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…
- mut-missing-log | document | _workflow\r56-mainline-integration\mutations\missing\mutant.log | SHA256=980376147e322446ef955a2beef13b92ff0f3eabf766d8b9271ada438f93d483
  Purpose: 反向突变 missing 的执行日志（构建与测试输出、退出码）; conditions: dotnet test 全量输出重定向
- mut-changed | test | _workflow\r56-mainline-integration\mutations\changed\mutant.trx | SHA256=b88e5b70e27ed1f193cebc9545b3f81fc6d998fd1961dd829066ed3d6b68c1fa
  Purpose: 反向突变 changed 的 mutant TRX：目标 theory 行 Failed（命中 :line 188）; conditions: dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1
- restored-changed | test | _workflow\r56-mainline-integration\mutations\changed\restored.trx | SHA256=505f451598bc1033fab42b5e57500467b081c73a7ada3ec82d5e5de0874ff2e4
  Purpose: 反向突变 changed 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed; conditions: 同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…
- mut-changed-log | document | _workflow\r56-mainline-integration\mutations\changed\mutant.log | SHA256=3e44e497de2b97984239a7698b566f0a3d7f219828499ec4c0ea0425e2c31b70
  Purpose: 反向突变 changed 的执行日志（构建与测试输出、退出码）; conditions: dotnet test 全量输出重定向
- mut-commit-refusal | test | _workflow\r56-mainline-integration\mutations\commit-refusal\mutant.trx | SHA256=b48d090c49ce6ba5da9d6bd3783761fb699cc87033622f97bf527be56fc583bc
  Purpose: 反向突变 commit-refusal 的 mutant TRX：目标 theory 行 Failed（命中 :line 190）; conditions: dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1
- restored-commit-refusal | test | _workflow\r56-mainline-integration\mutations\commit-refusal\restored.trx | SHA256=b9b1963a26a6d9232d09a8a789ecdaea868e48bde2dfc4f4cdf59c22fe9f12e0
  Purpose: 反向突变 commit-refusal 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed; conditions: 同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…
- mut-commit-refusal-log | document | _workflow\r56-mainline-integration\mutations\commit-refusal\mutant.log | SHA256=790f6ca5f454650613fbcc29996e8d7aab53c978a00988b17098a1ceb1c759c9
  Purpose: 反向突变 commit-refusal 的执行日志（构建与测试输出、退出码）; conditions: dotnet test 全量输出重定向
- mut-production-gate | test | _workflow\r56-mainline-integration\mutations\production-gate\mutant.trx | SHA256=2fbf51a4d512fb1810f1ad3c0423198f273413077440a795df99c5ee789dbeec
  Purpose: 反向突变 production-gate 的 mutant TRX：目标 theory 行 Failed（命中 :line 196）; conditions: dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1
- restored-production-gate | test | _workflow\r56-mainline-integration\mutations\production-gate\restored.trx | SHA256=4c9989b71a3dc807bf5d8978201eadd78bc477274a2211b04885742d77f35f10
  Purpose: 反向突变 production-gate 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed; conditions: 同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…
- mut-production-gate-log | document | _workflow\r56-mainline-integration\mutations\production-gate\mutant.log | SHA256=5bdfaa415d81b4d139e8097eff1b04722f2d8a97add37534e8044a032ba325f0
  Purpose: 反向突变 production-gate 的执行日志（构建与测试输出、退出码）; conditions: dotnet test 全量输出重定向
- subagent-readonly-audit | document | _workflow/r56-mainline-integration/subagent/readonly-audit-report.md | SHA256=9f600a90d2e309fdd5d9847aa159d3e74b9cec819684406b63575bf847d53e0f
  Purpose: 固定开工 ref 的只读子 Agent 报告：核对交付 A 覆盖矩阵 7 行与主线实现一致、TryRunProduction 无外部调用方、A–F 无一项已接线，并列出导入 theory 的 6 处断言强度边界与 1 处待判定授权/快照交错（F-4）、1 处行号漂移（F-5）; conditions: 子 Agent 只读执行（未构建/未测试/未写文件），固定 ref = 开工 HEAD 8a3ee6c4c，与 opening.json 源哈希一致；主执行者已按最终源码复核 F-4/F-5 关键行
- targeted-final-v2 | test | _workflow/r56-mainline-integration/final/targeted-final-v2.trx | SHA256=cbe4a4a4eedc64f120025244f4034e517c9eb47642357cd18347d6cbde7e9538
  Purpose: 会诊修复后的集成定向三类 81/81（最终文档版本；风险矩阵 S1/C2/F1/F2 的反例证据）; conditions: dotnet test … --no-build --filter 三类迁移夹具；exit 0；81 passed
- full-final-v2 | test | _workflow/r56-mainline-integration/final/assistant-full-final-v2.trx | SHA256=02f60577caae3ac007ea7fc3b1eb2662ca57ad48e17ee1a4690505e77d83c1f4
  Purpose: 会诊修复后的助手全量 1570 passed/2 skipped/0 failed/1572（含声明面守卫与全部既有夹具）; conditions: dotnet test … --no-build；exit 0
- claim-postconsult-preguard | test | _workflow/r56-mainline-integration/claims/claim-postconsult-preguard.trx | SHA256=0808b1d2808b381c8f735050e6a575bc1d3f3f9a063e371619d1eb7f20d9a431
  Purpose: 追加 §24.128.6 与其计数更正后，声明面守卫按预期失败（新增/变更 2 行、移除 0 行）——本批再次触及声明面; conditions: dotnet test … --filter ClaimSurfaceGuardTests（不带再生变量）；exit 1
- claim-postconsult-regen | test | _workflow/r56-mainline-integration/claims/claim-postconsult-regen.trx | SHA256=ba71a6323ef64a92f8385256102a2bc957c4a7e82d15521d2423d943ee0ab07d
  Purpose: 带 CLAIM_SURFACE_REGENERATE=1 再生（619→621 行，+2/-0）; conditions: dotnet test … --filter ClaimSurfaceGuardTests（带再生变量）；exit 0
- claim-postconsult-noenv | test | _workflow/r56-mainline-integration/claims/claim-postconsult-noenv.trx | SHA256=2c7203ab7ba7dd770bf5fd16d7c0c77db71a95327e0ff1a6a6323eac8d9dc2ae
  Purpose: 清除再生变量后复跑守卫通过（清单 621 行、SHA-256 543A5687…EE37）——最终权威清单; conditions: dotnet test … --filter ClaimSurfaceGuardTests（已清除变量）；exit 0
- claim-final-preguard | test | _workflow/r56-mainline-integration/claims/claim-final-preguard.trx | SHA256=d0c0a4a779794407575a3d454572444d5fd55791a14a001bd3794ea520dab4e9
  Purpose: 追加 §24.128.7 后声明面守卫按预期失败（新增/变更 2 行、移除 0 行）——第三次触及声明面，仍不得援引措辞类豁免; conditions: dotnet test … --filter ClaimSurfaceGuardTests（不带再生变量）；exit 1
- claim-final-regen | test | _workflow/r56-mainline-integration/claims/claim-final-regen.trx | SHA256=f24f5a3e0dc0ef1803a915c7cf120002a0432f7fdcde538e28719553af06a62c
  Purpose: 带 CLAIM_SURFACE_REGENERATE=1 最终再生（621→623 行，+2/-0）; conditions: dotnet test … --filter ClaimSurfaceGuardTests（带再生变量）；exit 0
- claim-final-noenv | test | _workflow/r56-mainline-integration/claims/claim-final-noenv.trx | SHA256=5b5e88c874a35905ac79d93536f96ef598883c47aec652948293c18cc29f4c88
  Purpose: 清除再生变量后复跑守卫通过——最终权威清单 623 行、SHA-256 05EB52DE…E725，绑定含 §24.128.7 的最终文档状态; conditions: dotnet test … --filter ClaimSurfaceGuardTests（已清除变量）；exit 0
- claim-manifest-final | document | _workflow/r56-mainline-integration/claims/manifest-final.txt | SHA256=543a5687b9b75c263d2dcdab21bfd0fc24ac41c36c0f24499a09614d1c54ee37
  Purpose: 本批最终声明面清单副本（623 行，SHA-256 05EB52DE…E725）; conditions: 最终再生并复跑通过后的清单副本
- full-final-v3 | test | _workflow/r56-mainline-integration/final/assistant-full-final-v3.trx | SHA256=80d80db2762b435f77ec3ae6b98749adaea3788c61967f350d6967cab1029343
  Purpose: 最终文档状态下的助手全量 1570 passed/2 skipped/0 failed/1572（权威集成回归证据）; conditions: dotnet test … --no-build；exit 0
