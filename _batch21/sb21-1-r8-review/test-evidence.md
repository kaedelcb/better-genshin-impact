# SB21-1 test evidence (post-R8)

- `Batch21WiringRedTests`: **4 passed / 0 failed / 4 total**, TRX `final-targeted/batch21-wiring-4of4-final.trx`.
- Assistant full regression after final production/test-source edits and again after claim regeneration: both runs **1479 passed / 2 skipped / 0 failed / 1481 total**. Final TRX: `final-full-after-claim/sb21-1-assistant-full-final-after-claim.trx`; pre-claim frame: `final-full/sb21-1-assistant-full-final-after-r8.trx`.
- Skips are the two existing opt-in controlled-load diagnostics: `P50_LoadRepro_WholeClass_UnderControlledLoad` and `P50_DiagnosticRepeat_OptIn`.
- Full-run name delta against the requested `1475/2/0/1477` baseline projection: **added exactly these four fully qualified Facts; removed/renamed 0**:
  - `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.Batch21WiringRedTests.RegistrationFailures_ParkZeroSend_NotUnknownConvergence`
  - `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.Batch21WiringRedTests.ScopeAndQueueFailures_ParkZeroSend_NotUnknownConvergence`
  - `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.Batch21WiringRedTests.DefaultConstructedHost_WiresLocalWaitStack`
  - `MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.Batch21WiringRedTests.OccupantLevels_CorruptRunRecord_KeepsUnknownAndLogs`
- Name-delta method: inspected tracked test-source changes versus HEAD; the only modified tracked assistant test file changes assertions only, with existing Fact names unchanged. The only new Fact declarations are the four above. Thus subtracting those four passing Facts yields 1475/2/0/1477. No prior 1477-case baseline TRX was present, so this is an exact test-name/source projection, not a newly executed old-HEAD baseline.
- ClaimSurfaceGuardTests regen and no-environment verification TRXs are in `claim-surface-regen/`, `claim-surface-verify/`, and final-document no-env verification in `claim-surface-final-docs2/`. `ClaimSurfaceManifest.txt` remained 590 lines with SHA-256 `1F29901BF9C2C816BA167AE9E36CB9D03F64F33A6791CDB84ADA3DCD9E7DCAD6`.
- `git diff --check` passed before documentation closeout; rerun after final writes.

## Batch 51 R16 evidence follow-up (mechanical; prepared after original eight-request cap)

- Round 2 of the SB21-1 ledger raised a must-fix evidence objection: the then-submitted R16 closure lacked the historical source anchor, object hash, and a locatable 104/104 run. Preserve that finding's level; this entry supplies the missing factual evidence and does not claim a retroactive R16 consultant result.
- §24.63 U is recorded in `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`. The final post-R15 source anchor is commit `029cbff66068bb53418727706356c620bc810bc1`. `SubmissionPointInventoryTests.cs` has no later history entry; its current Git blob `fe254d499b9f43590d1112b657000679a3e4ecdf` exactly matches that commit's blob. Current SHA-256: `B8093E25BC9DF7486EEB7ECD9F6AB2B00AC1FBBE2085F216499D0BC3FEB697F4`.
- A fresh targeted rerun after the eight-request cap, with `DeployToBgiTools=false`, passed **104/104, 0 failed**. TRX: `r16-followup/sb21-1-r16-followup-104.trx` (SHA-256 `DED9D8AD0350DC9A3D2965F8A1C48B5419029F55E650CBC55E160EF8B4BD2E3C`). This verifies the current anchored test source mechanically; the historical full-run 1068/2/0/1070 remains a historical claim and was not rerun as part of this focused check.
- At the time this follow-up was recorded, the original 8/8 request cap was exhausted and no ninth request had yet been sent. The owner later explicitly authorized one narrowly scoped GPT closeout review; its result and subsequent local reconciliation are recorded below. No historical R16 consultant acceptance is claimed.

## Owner-authorized R9 closeout review and local reconciliation

- After R1–R8 (8 requests), the owner approved exactly one GPT-only read-only closeout request, limited to the five original R8 findings and the R2 R16 evidence finding. The successful request was GPT `gpt-6-astra`, medium, tool-reported `attempts=1`; it is R9 and brings the cumulative count to 9 with a one-time owner-authorized extension. One earlier allowlist typo was rejected locally before dispatch and is not a sent request. No follow-up consultation was sent.
- At the submitted R9 snapshot, GPT returned **2 必改 evidence gaps** and supported the recorded code/test dispositions for the other **4 重要** R8 findings. Exact report: `post-cap-gpt-review/gpt-r9-closeout-review.md`. Original levels are preserved.
- **R8 必改 evidence correction:** the source hash for the balanced identity-catch mutation was mistyped in the first table. It is corrected to `781087DA6377F18E53E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06`, matching the current source. New `current-source-reverse-mutation-audit.md` maps all 11 valid mutant/restored runs to their named single-TestResult TRXs, outcomes, failure messages, and SHA-256 values. Every mutant TRX is Failed on its named Fact; every restored TRX is Passed; current source hashes match the recorded restored hashes. The initial four compile-error/no-TRX attempts remain excluded. Raw TRXs remain at the referenced paths. This is a post-review local evidence repair, not a claim that GPT re-reviewed it.
- **R2 R16 evidence correction:** the first R9 packet excerpt omitted §24.63 U. The full source subsection is now included in `post-cap-gpt-review/batch51-r16-u-excerpt.md`. After R9, `git rev-parse` confirmed the current and anchor source blobs both equal `fe254d499b9f43590d1112b657000679a3e4ecdf`; `git ls-tree` at the anchor agrees; current source SHA-256 is `B8093E25BC9DF7486EEB7ECD9F6AB2B00AC1FBBE2085F216499D0BC3FEB697F4`. Parsing the existing targeted TRX confirmed 104 Passed / 0 Failed and its recorded SHA-256. The historical §24.63 U statement that R16 obtained no consultation result remains intact.
- Post-reconciliation targeted regression: `Batch21WiringRedTests` passed **4/4, 0 failed** at `post-r9-targeted/batch21-wiring-post-r9-4of4.trx` (SHA-256 `BBB810EF64311CB13D69426274FF8B2FFB0FFFE283201DD3BBFC43218FD2E17A`).
- Because §24.121 wording was appended, `ClaimSurfaceGuardTests` was regenerated with `CLAIM_SURFACE_REGENERATE=1` and rerun without the variable; both passed 1/1. TRXs: `post-r9-claim-regen/claim-surface-post-r9-regen.trx` (SHA-256 `B406F9092F15974ED65F17B315456FC5228496CB5C1D5AF1A6E0FCDECFD30790`) and `post-r9-claim-verify/claim-surface-post-r9-no-env.trx` (SHA-256 `0F4589F03E81ECBDA393BB91AE2F3DE9620E69DBE75D55A22EB920FADFE2C256`). Manifest remained 590 lines, SHA-256 `1F29901BF9C2C816BA167AE9E36CB9D03F64F33A6791CDB84ADA3DCD9E7DCAD6`.
- The changes above repair the evidence omissions mechanically. There has been no second GPT acceptance of those additions, and no claim of such acceptance. The full assistant regression remains the previously recorded 1479/2/0/1481 run plus exact four-Fact source-name projection; the historical 1477-case baseline was not rerun. Production gates remain closed.

## Owner 收口裁决（2026-09-27）

owner 指令据本地机械证据关闭 SB21-1。R9 两项原级必改证据缺口均已补齐并可复核：11 组有效反向突变/恢复 TRX 与各自命名结果、哈希对照；R16 完整 §24.63 U、锚点 blob、当前源码哈希与 104/104 TRX。R9 未复核后补材料，不声称顾问接受；没有追加 SB21-1 会诊。原 R16 独立会诊历史上未取得的事实仍保留为后续处理债务。生产入口及真实 User 等门继续关闭。

## Owner closeout claim-surface verification (2026-09-27)

After adding the closeout statements to the guarded R5.3 and R5 handoff documents, `ClaimSurfaceGuardTests` passed 1/1 with `CLAIM_SURFACE_REGENERATE=1` and passed 1/1 again without the variable. Both used `-p:DeployToBgiTools=false`. TRXs: `_batch21/sb21-1-closeout/claim-regen/sb21-1-owner-closeout-claim-regen.trx` and `_batch21/sb21-1-closeout/claim-verify/sb21-1-owner-closeout-claim-verify.trx`. Current `ClaimSurfaceManifest.txt` is 591 lines, SHA-256 `C49D950F90FB51ED91ED46FB697F9AA49D541E650A67A714DCB31767942F7D29`; the prior R9 snapshot remains documented as 590 lines.

## Current checkout audit and regressions (2026-09-27)

These fresh regressions were run against branch main-OldTeaBag-B168, HEAD 14e22f2d578e2c7b005e3d2ef5b0e07a4ae4a987. All three target source/test files matched that HEAD byte-for-byte; recorded SHA-256 values are listed in R5.3 §24.121.5. Test runs used DeployToBgiTools=false and exited successfully.

- Targeted Batch21WiringRedTests: 4 passed / 0 failed / 4 total. TRX _batch21/current-audit-20260927/targeted/sb21-1-current-targeted-20260927.trx; SHA-256 C3925EBCEC3B3B628A94499C12CB9FF32BCA55D8D047169F0D03C34FE070696C.
- Adjacent LocalWait|Batch21|OccupantLevel|StartupHandoff|Resume: 278 passed / 0 failed / 278 total. TRX _batch21/current-audit-20260927/adjacent/sb21-1-current-adjacent-20260927.trx; SHA-256 74166DF9D48DACFB08F3F786278E95739344709F124DA76350C5EFA2F807634C.
- Full assistant test project: 1479 passed / 2 not executed / 0 failed / 1481 total. The two not-executed cases are TaskCenterSuccessorPathGateTests.P50_DiagnosticRepeat_OptIn and TaskCenterSuccessorPathGateTests.P50_LoadRepro_WholeClass_UnderControlledLoad. TRX _batch21/current-audit-20260927/full/sb21-1-current-assistant-full-20260927.trx; SHA-256 F080B2312DE81A1AD10B5AF2E9E6A6A476116CEB5E669CFA784586196291F4D3.

No product source or fixture changed during this current-checkout audit. The historical R16 consultation result remains unavailable; §24.63 U retains that process debt. No additional consultation was sent after R9.

## Claim-surface guard for current audit (2026-09-27)

After adding the current audit record to R5.3, ClaimSurfaceGuardTests.DesignDocs_ClaimSurface_MatchesReviewedManifest passed 1/1 with CLAIM_SURFACE_REGENERATE=1 and passed 1/1 again with the variable absent; both runs used DeployToBgiTools=false. The manifest gained one reviewed record for the new R5.3 claim line and is now 592 lines, SHA-256 50100BE5E54BF65122AD203BB1803DD67E84BFA50305E4FC13C61D6468865ECD. Regen TRX _batch21/current-audit-20260927/claim-regen/sb21-1-current-claim-regen.trx (SHA-256 B37B5542C536EF1FA71E0EE585802B0C3DC2846C489A7C2CCEC32FCCA753C17); no-env TRX _batch21/current-audit-20260927/claim-verify/sb21-1-current-claim-verify.trx (SHA-256 D3054A0F91C821D81FF73B70D4134E8D452CD53AE8FEAB74FFD66B87075784BE).
