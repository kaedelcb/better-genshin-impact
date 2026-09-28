# wave3-bo8-bo9-2026-09-28 evidence index

Mechanical checks only; no quality/reuse verdict.

- opening-snapshot | document | _workflow/wave3-bo8-bo9/opening.json | SHA256=3e9bbbc3778adf9557411e819335e2117c3d41e817d2d0f1e7ec9bb64da1745a
  Purpose: 本批开工快照（HEAD、分支、工作区状态、两个源的开工哈希与原始风险矩阵行）; conditions: python -B tools/mistletoe/workflow.py begin（无产品副作用）
- baseline-build | build | _workflow/wave3-bo8-bo9/baseline-build.log | SHA256=849b8e45045f11eb1e345483a122fb5862ed643510e91e60b68f9c93f37e04e4
  Purpose: 开工字节测试项目 Rebuild（同条件基线前提）; conditions: dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0；80 warnings／0 errors
- baseline-targeted | test | _workflow/wave3-bo8-bo9/baseline/targeted-baseline.trx | SHA256=89f0530dd94ebf2ad6df1d1ade51eafe2061c819a446d06c7ade6096a93cb64e
  Purpose: 同条件开工基线定向三类 93/93（对照用）; conditions: dotnet test … --no-build --filter 三类；93 passed
- baseline-full | test | _workflow/wave3-bo8-bo9/baseline/assistant-full-baseline.trx | SHA256=ca6b7344c2daf72c34542ae1dadfe391eb86cdf1671994bfc6c35cf7cc0f69e5
  Purpose: 同条件开工基线助手全量 1562 passed／2 skipped／0 failed／1564（testId 对照基线）; conditions: dotnet test … --no-build；exit 0
- red-prefix-source | document | _workflow/wave3-bo8-bo9/red-final/prefix-source.cs | SHA256=5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96
  Purpose: 红运行使用的开工字节固定件（SHA-256 等于 opening.json 的 WorkflowRunner.cs 开工哈希）; conditions: git show HEAD:<path> 的 LF blob 转工作区 CRLF；运行后逐字节恢复
- red-final-build | build | _workflow/wave3-bo8-bo9/red-final/testproject-build.log | SHA256=765812cfebab52fc314997b0a954bdc54a1ba52641d6ec7d1298f8fdb0d384fd
  Purpose: 红运行前测试项目 Rebuild; conditions: dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0
- red-final | test | _workflow/wave3-bo8-bo9/red-final/bo8-bo9-red-final.trx | SHA256=6b2770cb938833591ac5b2e520a9cdb6ffcd3298f296c1db1b5a51d94768b805
  Purpose: 反例先行红证据（开工字节 + 本批夹具）：3 failed／0 passed／3 total; conditions: dotnet test … --filter 当时三个夹具；exit 1；BO-8 提交 [n3,n2,X,n3,Y]；BO-9 只提交 [(A,1)]
- red-final-exits | document | _workflow/wave3-bo8-bo9/red-final/red-final-exits.json | SHA256=afb2f907a922177e967381a220c746aa91412155f1abe1cdbfbbd889e9b91398
  Purpose: 红运行退出码与源码恢复核对记录; conditions: run-red-final.ps1 落盘；prefix_build_exit=0、prefix_test_exit=1
- red-v1 | test | _workflow/wave3-bo8-bo9/red/bo8-bo9-red.trx | SHA256=289c0ee5b64ee261542bc0a86a0b54d876ee59826194749a6c0786c6500efd26
  Purpose: 早期红运行（BO-9 夹具为 2 停驻版本；仅留存历史）; conditions: dotnet test …；3 failed
- red-v1-build | build | _workflow/wave3-bo8-bo9/red/testproject-build.log | SHA256=13f33d01db5e813a6900e4a13f449fb70ea63eb65ef867e88e3551a87092d2c3
  Purpose: 早期红运行的测试项目 Rebuild; conditions: dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0
- round1-review-packet | consult | _workflow/wave3-bo8-bo9/review-v1/packet.md | SHA256=8d4d6550739b379c3e8a1b5de8958022e3391986c3a7c4a952a45ff2a6783122
  Purpose: 第 1 轮送审材料（audit review 生成，403,789 字节）; conditions: workflow.py audit --stage review；快照 review-v1 经 verify 通过
- round2-review-packet | consult | _workflow/wave3-bo8-bo9/review-v2/packet.md | SHA256=4bd1b440d8f11c0ad8d3bcb192568809e0c0a478125b399b713177a1eb4fdfd4
  Purpose: 第 2 轮送审材料（433,123 字节）; conditions: workflow.py audit --stage review；快照 review-v2 经 verify 通过
- consult-preflight-v1 | document | _workflow/wave3-bo8-bo9/consultation/preflight-v1.json | SHA256=772d157b133455dabe59067795bc190ab690df90b65963545d0961fb675a1136
  Purpose: 第 1 轮渠道容量预检与材料清单; conditions: 本地估算；未派发请求
- consult-preflight-v2 | document | _workflow/wave3-bo8-bo9/consultation/preflight-v2.json | SHA256=26f1caf68f39758d6b2f58d487318fb62c5416006a52cfb813dbb7a736ac9320
  Purpose: 第 2 轮渠道容量预检与材料清单（26 件、487,548 字节）; conditions: 本地估算
- consult-request-v1 | consult | _workflow/wave3-bo8-bo9/consultation/review-request-v1.md | SHA256=c27458b6b4f680bda651280f358195ad6694cd7cea410694cc0ceac33d20edb5
  Purpose: 第 1 轮会诊请求文本; conditions: 发往既有 GPT 会诊工具
- consult-outcome-v1 | consult | _workflow/wave3-bo8-bo9/consultation/review-outcome-v1.md | SHA256=6ae9ecc657b07681548a996259a747de2d0ae0113ed231b06ac4143b8052908b
  Purpose: 第 1 轮结论与逐项处置（1 IMPORTANT + 1 建议级）; conditions: gpt-6-astra / medium，attempts=1，read-only；原文要点逐项抄录
- consult-request-v2 | consult | _workflow/wave3-bo8-bo9/consultation/review-request-v2.md | SHA256=bc7268a23b08b75fac4fd13af88e72fc5a460c343b7b7f60e8aa7a6a7849bd80
  Purpose: 第 2 轮验证请求文本（严格限定两问）; conditions: 同渠道
- consult-outcome-v2 | consult | _workflow/wave3-bo8-bo9/consultation/review-outcome-v2.md | SHA256=96e21eb65cb705d862605a1a77f3c401ee1d11912fb3c18845819db935a41922
  Purpose: 第 2 轮结论与逐项处置（IMPORTANT-1 确认闭合；新增 IMPORTANT-2）; conditions: gpt-6-astra / medium，attempts=1，read-only；原文要点逐项抄录
- fixed-v5-build | build | _workflow/wave3-bo8-bo9/fixed-v4-build.log | SHA256=c36a86bff65c210f88f2d860e9e344944ddc0a94da0cb4020f0f6d7e0818db17
  Purpose: 第 1 轮修复后的测试项目 Rebuild; conditions: dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0
- fixed-v5-targeted | test | _workflow/wave3-bo8-bo9/final-v9/targeted-final.trx | SHA256=7af8a22cf4a40091d5b0b62ee1d6bd6ea61a2f9af97b6640d1c421081b7c2a30
  Purpose: 第 1 轮修复后的定向 97/97（历史中间版本）; conditions: dotnet test …；97 passed
- fixed-v5-full | test | _workflow/wave3-bo8-bo9/fixed-v5/assistant-full-fixed-v5.trx | SHA256=c620ba5f643b60126215109e419fa15fb6969cb33b18ef9f0700138e3acae951
  Purpose: 第 1 轮修复后的助手全量 1566/2/0/1568（历史中间版本）; conditions: dotnet test …；exit 0
- fixed-v6-build | build | _workflow/wave3-bo8-bo9/fixed-v6-build.log | SHA256=2dd5e1bcf22ee4bd8b1f1743e20a7b2dd43f302acd9783db3c3a8dc43502c5e1
  Purpose: 第 2 轮修复后的测试项目 Rebuild; conditions: dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0
- fixed-v6-targeted | test | _workflow/wave3-bo8-bo9/final-v9/targeted-final.trx | SHA256=7af8a22cf4a40091d5b0b62ee1d6bd6ea61a2f9af97b6640d1c421081b7c2a30
  Purpose: 第 2 轮修复后的定向 98/98; conditions: dotnet test …；98 passed
- fixed-v6-full | test | _workflow/wave3-bo8-bo9/fixed-v5/assistant-full-v6.trx | SHA256=cce8c6b83c3c6a9c838c1c865b495dfddb28c6bf109b7ce1016ab0ab6f1a40ee
  Purpose: 第 2 轮修复后的助手全量 1567/2/0/1569; conditions: dotnet test …；exit 0
- final-build | build | _workflow/wave3-bo8-bo9/final-v9/testproject-build.log | SHA256=304347f36175a98ffa35d72db59e10c9391ca1ba15711dd76413e9851397df69
  Purpose: 最终版本（含 R5.3 §24.127 与声明面再生）测试项目 Rebuild; conditions: dotnet build … -t:Rebuild -p:DeployToBgiTools=false；exit 0；0 error
- final-targeted | test | _workflow/wave3-bo8-bo9/final-v9/targeted-final.trx | SHA256=7af8a22cf4a40091d5b0b62ee1d6bd6ea61a2f9af97b6640d1c421081b7c2a30
  Purpose: 最终定向三类 98/98（风险矩阵与关键断言的行级 test 证据）; conditions: dotnet test … --filter 三类；exit 0
- final-full | test | _workflow/wave3-bo8-bo9/final-v9/assistant-full-final.trx | SHA256=2c00d3205fee33cd6d21fadad01b2c477e29aa3492fe3c6bd3fd3b2eb7ccec0d
  Purpose: 最终助手全量 1567 passed／2 skipped／0 failed／1569; conditions: dotnet test … --no-build；exit 0；含 ClaimSurfaceGuardTests
- final-testid-comparison | component | _workflow/wave3-bo8-bo9/final-v9/testid-comparison.json | SHA256=482c0eb7798ae06ae0a66047b9a76346901fee079a8d4b7a11f67c53ef6a0779
  Purpose: 基线→最终逐名 testId 对照：1564 unchanged／5 added／0 removed／0 changed; conditions: 由基线 TRX 与 final-v8 TRX 解析生成
- mutation-records | component | _workflow/wave3-bo8-bo9/mutation-records-round3.json | SHA256=4df7221daadb6edc520f59efcad5de3f2a92a7b6250279e72043a25f32a3410b
  Purpose: 14 项反向突变的机读记录（三阶段退出码、目标 testId/名称、断言行、TRX 路径、源码哈希）; conditions: 由 run-mutants.ps1 生成
- mutation-runner | document | _workflow/wave3-bo8-bo9/run-mutants.ps1 | SHA256=f5d09047c2cdd83467a3cf4db98f520c540a3885b9a9378898ae2b33652ef49f
  Purpose: 反向突变执行脚本（逐项独立构建/测试、源码逐字节恢复、断言行与失败标记提取）; conditions: pwsh -NoProfile -File run-mutants.ps1；ALL MUTATIONS COMPLETE count=14
- mutation-run-log | document | _workflow/wave3-bo8-bo9/mutations-run-round3.log | SHA256=1fd411bdfc7864140bcadd114536f3981b5426a85274f9408c107f640274ac2d
  Purpose: 突变运行日志（逐项 PASS 行与最终 original 哈希）; conditions: 同一脚本 stdout；original=b0b3579b…
- claims-regen | test | _workflow/wave3-bo8-bo9/claims-v4/claim-regen.trx | SHA256=47825fb4434df48efd2ccc2d6eaabcb4be43300a4aa62f6ce37dc58bdc556178
  Purpose: 声明面再生（CLAIM_SURFACE_REGENERATE=1）后守卫通过; conditions: dotnet test … --filter ClaimSurfaceGuardTests（带环境变量）；1 passed
- claims-noenv | test | _workflow/wave3-bo8-bo9/claims-v4/claim-noenv.trx | SHA256=0ac9dd693ac7db1cdcca3cfcd3173daf2a80bbe3212c098c965bdc9660a82005
  Purpose: 清除环境变量后复跑守卫通过（哈希稳定）; conditions: dotnet test …（无环境变量）；1 passed；清单 SHA 前后一致
- claims-before-hash | document | _workflow/wave3-bo8-bo9/claims-v4/manifest-before.sha256 | SHA256=b2b4c09a7ee42c63a132b7da4b2cc7a91128cee146e09d407d58450e0029a48c
  Purpose: 末次再生前清单哈希（617 行）; conditions: daa2639f36ba35ae9370a759fe84e11afe49cb8d210d3892fa03e246520fb684
- claims-after-hash | document | _workflow/wave3-bo8-bo9/claims-v4/manifest-after.sha256 | SHA256=0722627afa4f2e7f24db9849fc603df7d73f8260f3e8ede517843794baa7d88c
  Purpose: 末次再生后并清除变量后清单哈希（618 行）; conditions: 1f2b674365c9f150a0c4e898a54e586177283e07fa5bfb83bd2f4cbb786a5ccc
- claims-diff-summary | document | _workflow/wave3-bo8-bo9/claims-v4/manifest-diff-summary.json | SHA256=26b2812cefaed1adbc02c03ce711b1549614a5ed51f1f10195bfe93ace035713
  Purpose: 清单差异摘要：+1 行／-0 行（BO-9-D1 登记句）; conditions: 逐行身份比对生成
- deploy-before | document | _workflow/wave3-bo8-bo9/deploy-target-before.txt | SHA256=ad1e645e8ca568797a9c00eb8e864d68721bb33a01e95337595caafbd0580a16
  Purpose: 开工时部署目标读数; conditions: Get-ChildItem -Recurse -File 计数与目录 LastWriteTimeUtc
- deploy-final | document | _workflow/wave3-bo8-bo9/deploy-target-final.txt | SHA256=fa12acb69d37af33d193173111146abc6303a12f8c3656fb35d2383e82ed9517
  Purpose: 全部构建与测试后部署目标读数（与开工逐项一致）; conditions: 同上口径；两次读数一致
- findings | document | _workflow/wave3-bo8-bo9/findings.md | SHA256=a1ff598a59e9e4d8fb1323f3aef53cd73e4831f59bd65c015531f7272127e8bb
  Purpose: 本批发现、实现、证据、会诊处置与边界; conditions: 本批 findings
- context | document | _workflow/wave3-bo8-bo9/context.md | SHA256=33c790c1fcc7fcf46cdb30aa1ee5d00ea0af8bd54889b04cd682c09e9af60207
  Purpose: 本批目标、范围、依赖顺序、开工状态与子 Agent 评估; conditions: 本批 objective
- budget | document | _workflow/wave3-bo8-bo9/budget.md | SHA256=ac4af8d986985c219fed86c1872b5e9d3c8b617263a38b86748bc0b855a8a8de
  Purpose: 本批会诊预算、计数与处置纪律; conditions: 本批 budget
- consult-outcome-v3 | consult | _workflow/wave3-bo8-bo9/consultation/review-outcome-v3.md | SHA256=a68fc03f819aef1ea80944d205a58af3d142bbf1c2bebbf976bf64d91f058a15
  Purpose: 第 3 轮验证结论与逐项处置（IMPORTANT-2 原级闭合；原 IMPORTANT-1 未回退；无新增 MUST/IMPORTANT；建议级 BO-9-D1 登记）; conditions: gpt-6-astra / medium，attempts=1，read-only；原文要点逐项抄录
- round3-review-packet | consult | _workflow/wave3-bo8-bo9/review-v3/packet.md | SHA256=c7b2907a0ee5980e309e2ed4126a21ab7daf44b618f9911ec79ed65bbe3cf600
  Purpose: 第 3 轮送审材料（449,331 字节）; conditions: workflow.py audit --stage review；快照 review-v3 经 verify 通过
- consult-preflight-v3 | document | _workflow/wave3-bo8-bo9/consultation/preflight-v3.json | SHA256=d7531921b581770bad02566e26212555e4ded283b5b576783630a022cd769075
  Purpose: 第 3 轮渠道容量预检与材料清单（27 件、502,436 字节）; conditions: 本地估算
