# SB21-2 最终提交范围清单

生成时间：2026-09-27（提交前快照）。本清单界定 SB21-2（BO-10/BO-12）显式提交范围。

## 当前边界

- 分支 main-OldTeaBag-B168，审计 HEAD 8a4b8d988e2f0ff3ade35538bffa2de7a93c9da2；暂存区原为空。
- 本批 tracked 改动为核心实现、定向夹具、声明面、R5.3 与计划；另有三份 tracked 材料外文档变更：Docs/design/mistletoe-session-relay-2026-09-24.md、Docs/design/unified-job-registry-master-plan.md、槲寄生调度器总计划.md。三者及全部其他材料外文件不进入本清单。
- .bak、.stale、运行产物、旧批次材料、用户材料均保持现状，不删除、不回退。
- 独立会诊台账 consultation-ledger.json：GPT 5/8 已发送，2 次本地预检拒绝未发送；父 ledger-batch21.json 的 9 次记录属于 SB21-1，不改写。指定外部 review 纪律路径不存在；项目内 .agents/knowledge/domains/review-disposition-discipline.md 存在且已读取。
- task-relays 两外部文件已更新，其 SHA-256 与更新前副本核对记录在交接稿；外部文件不属于 Git 提交。
- 所有构建/测试使用 -p:DeployToBgiTools=false；未运行实机/真实 User/生产入口；相关生产门保持关闭。

## 本批核心与收口文件

- _batch21/b21_plan.md
- _batch21/sb21-2-handoff-2026-09-27.md
- Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
- MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs
- MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitReevaluationConsumer.cs
- MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/LocalWaitReevaluationTrigger.cs
- MultiplayerHoeingAssistant/Services/TaskCenter/LocalWaitQueueStore.cs
- Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
- Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitGenerationContractTests.cs
- Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs

## 会诊记录

- _batch21/sb21-2-review/contract.md
- _batch21/sb21-2-review/source-bo-records.md
- _batch21/sb21-2-review/production-path-boundary.md
- _batch21/sb21-2-review/gpt-r1-review.md
- _batch21/sb21-2-review/gpt-r2-review.md
- _batch21/sb21-2-review/gpt-r3-review.md
- _batch21/sb21-2-review/gpt-r4-review.md
- _batch21/sb21-2-review/gpt-r5-review.md
- _batch21/sb21-2-review/review-r4-context.md
- _batch21/sb21-2-review/review-r5-context.md
- _batch21/sb21-2-review/review-r4-preflight-rejection.md
- _batch21/sb21-2-review/consultation-ledger.json
- _batch21/sb21-2-review/ledger-batch21-sb21-2-pre-r5-dispatch.json
- _batch21/sb21-2-review/ledger-external-before-r4-record.json
- _batch21/sb21-2-review/audit-status-initial.txt
- _batch21/sb21-2-review/audit-scoped-unstaged-diff-initial.txt
- _batch21/sb21-2-review/review-r1-status.txt
- _batch21/sb21-2-review/review-r1-related-diff.patch
- _batch21/sb21-2-review/review-r2-status.txt
- _batch21/sb21-2-review/review-r2-related-diff.patch
- _batch21/sb21-2-review/review-r3-status.txt
- _batch21/sb21-2-review/review-r3-staged-in-scope.diff
- _batch21/sb21-2-review/review-r3-related-unstaged-in-scope.diff
- _batch21/sb21-2-review/review-r3-material-out-status.txt
- _batch21/sb21-2-review/review-r3-material-out-unstaged.diff
- _batch21/sb21-2-review/review-r4-status.txt
- _batch21/sb21-2-review/review-r4-staged-in-scope.diff
- _batch21/sb21-2-review/review-r4-related-unstaged-in-scope.diff
- _batch21/sb21-2-review/review-r4-material-out-status.txt
- _batch21/sb21-2-review/review-r4-material-out-unstaged.diff
- _batch21/sb21-2-review/review-r5-status.txt
- _batch21/sb21-2-review/review-r5-staged-in-scope.diff
- _batch21/sb21-2-review/review-r5-related-unstaged-in-scope.diff
- _batch21/sb21-2-review/review-r5-material-out-status.txt
- _batch21/sb21-2-review/review-r5-material-out-unstaged.diff
- _batch21/sb21-2-review/pre-r5-closeout/active-ledger-before-refresh.json
- _batch21/sb21-2-review/pre-r5-closeout/next-batch-prompt-before-refresh.md

## 构建、测试、突变与差集证据

- _batch21/sb21-2-review/red-results-summary.md
- _batch21/sb21-2-review/reverse-mutations.md
- _batch21/sb21-2-review/assistant-full-test-diff-r5-counter.md
- _batch21/sb21-2-review/assistant-full-test-diff-r5-counter.json
- _batch21/sb21-2-review/assistant-build-r5-original-parser.log
- _batch21/sb21-2-review/test-project-build-r5-final-nonincr.log
- _batch21/sb21-2-review/targeted-r5-restored-final.log
- _batch21/sb21-2-review/targeted-r5-restored-final/sb21-2-generation-restored-final.trx
- _batch21/sb21-2-review/localwait-r5-restored-final.log
- _batch21/sb21-2-review/localwait-r5-restored-final/sb21-2-localwait-restored-final.trx
- _batch21/sb21-2-review/assistant-full-r5-restored-final.log
- _batch21/sb21-2-review/assistant-full-r5-restored-final/sb21-2-assistant-full-restored-final.trx
- _batch21/sb21-2-review/assistant-full-red-baseline-final/sb21-2-assistant-full-red-baseline-final.trx
- _batch21/sb21-2-review/claim-r5-closeout-final-regen.log
- _batch21/sb21-2-review/claim-r5-closeout-final-regen/sb21-2-claim-r5-closeout-final-regen.trx
- _batch21/sb21-2-review/claim-r5-final-verified-noenv.log
- _batch21/sb21-2-review/claim-r5-final-verified-noenv/sb21-2-claim-r5-final-verified-noenv.trx
- _batch21/sb21-2-review/claim-r5-post-pathrefs-noenv.log
- _batch21/sb21-2-review/claim-r5-post-pathrefs-noenv/sb21-2-claim-r5-post-pathrefs-noenv.trx
- _batch21/sb21-2-review/claim-r5-exact-final-noenv.log
- _batch21/sb21-2-review/claim-manifest-before-r5-regen.txt
- _batch21/sb21-2-review/mutations-r3/results.json
- _batch21/sb21-2-review/mutations-r3/remove-highwater/mutant.log
- _batch21/sb21-2-review/mutations-r3/remove-highwater/remove-highwater.trx
- _batch21/sb21-2-review/mutations-r3/prune-highwater/mutant.log
- _batch21/sb21-2-review/mutations-r3/prune-highwater/prune-highwater.trx
- _batch21/sb21-2-review/mutations-r3/legacy-reservation-v2/mutant.log
- _batch21/sb21-2-review/mutations-r3/legacy-reservation-v2/legacy-reservation-v2.trx
- _batch21/sb21-2-review/mutations-r3/v4-highwater-required/mutant.log
- _batch21/sb21-2-review/mutations-r3/v4-highwater-required/v4-highwater-required.trx
- _batch21/sb21-2-review/mutations-r3/cleanup-byte-conflict/mutant.log
- _batch21/sb21-2-review/mutations-r3/cleanup-byte-conflict/cleanup-byte-conflict.trx
- _batch21/sb21-2-review/mutations-r3/c5-generation-check/mutant.log
- _batch21/sb21-2-review/mutations-r3/c5-generation-check/c5-generation-check.trx
- _batch21/sb21-2-review/mutations-r3/max-allocation-guard/mutant.log
- _batch21/sb21-2-review/mutations-r3/max-allocation-guard/max-allocation-guard.trx
- _batch21/sb21-2-review/mutations-r3/caller-generation-guard/mutant.log
- _batch21/sb21-2-review/mutations-r3/caller-generation-guard/caller-generation-guard.trx
- _batch21/sb21-2-review/mutations-r3/trigger-long-prune/mutant.log
- _batch21/sb21-2-review/mutations-r3/trigger-long-prune/trigger-long-prune.trx
- _batch21/sb21-2-review/mutations-r4/item-generation-overflow-original-parser/evidence.json
- _batch21/sb21-2-review/mutations-r4/item-generation-overflow-original-parser/LocalWaitQueueStore.pre-mutation.cs
- _batch21/sb21-2-review/mutations-r4/item-generation-overflow-original-parser/mutant-nonincr-build.log
- _batch21/sb21-2-review/mutations-r4/item-generation-overflow-original-parser/mutant.log
- _batch21/sb21-2-review/mutations-r4/item-generation-overflow-original-parser/test-results/sb21-2-item-overflow-original-parser-mutant.trx
- _batch21/sb21-2-review/mutations-r5/c5-consume-generation-valid/evidence.json
- _batch21/sb21-2-review/mutations-r5/c5-consume-generation-valid/LocalWaitReevaluationConsumer.pre-mutation.cs
- _batch21/sb21-2-review/mutations-r5/c5-consume-generation-valid/mutant-build.log
- _batch21/sb21-2-review/mutations-r5/c5-consume-generation-valid/mutant-test.log
- _batch21/sb21-2-review/mutations-r5/c5-consume-generation-valid/result/sb21-2-r5-c5-generation-valid-mutant.trx
- _batch21/sb21-2-review/mutations-r5-note-fixes/trigger-long-generation/evidence.json
- _batch21/sb21-2-review/mutations-r5-note-fixes/trigger-long-generation/LocalWaitReevaluationTrigger.original.cs
- _batch21/sb21-2-review/mutations-r5-note-fixes/trigger-long-generation/mutant-build.log
- _batch21/sb21-2-review/mutations-r5-note-fixes/trigger-long-generation/mutant-test.log
- _batch21/sb21-2-review/mutations-r5-note-fixes/trigger-long-generation/test-results/long-generation-intparse-mutant.trx
- _batch21/sb21-2-review/item-generation-overflow-original-source-probe/evidence.json

## 本次提交前快照文件

- _batch21/sb21-2-review/final-closeout/final-closeout-file-scope.md
- _batch21/sb21-2-review/final-closeout/intended-paths.txt
- _batch21/sb21-2-review/final-closeout/status-before-commit.txt
- _batch21/sb21-2-review/final-closeout/staged-diff-before-commit.patch
- _batch21/sb21-2-review/final-closeout/in-scope-unstaged-before-commit.patch
- _batch21/sb21-2-review/final-closeout/material-out-status-before-commit.txt
- _batch21/sb21-2-review/final-closeout/material-out-unstaged-before-commit.patch

## 提交规则

仅提交上述路径，使用 git commit --only -m "SB21-2 固化代际单调性与消费复核" -- <明确文件路径>。清单之外的修改、未跟踪材料与材料外变更不得进入提交。
