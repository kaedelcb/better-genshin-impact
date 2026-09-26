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

## Batch 51 R16 evidence follow-up (mechanical; no ninth consultation)

- Round 2 of the SB21-1 ledger raised a must-fix evidence objection: the then-submitted R16 closure lacked the historical source anchor, object hash, and a locatable 104/104 run. Preserve that finding's level; this entry supplies the missing factual evidence and does not claim a retroactive R16 consultant result.
- §24.63 U is recorded in `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`. The final post-R15 source anchor is commit `029cbff66068bb53418727706356c620bc810bc1`. `SubmissionPointInventoryTests.cs` has no later history entry; its current Git blob `fe254d499b9f43590d1112b657000679a3e4ecdf` exactly matches that commit's blob. Current SHA-256: `B8093E25BC9DF7486EEB7ECD9F6AB2B00AC1FBBE2085F216499D0BC3FEB697F4`.
- A fresh targeted rerun after the eight-request cap, with `DeployToBgiTools=false`, passed **104/104, 0 failed**. TRX: `r16-followup/sb21-1-r16-followup-104.trx` (SHA-256 `DED9D8AD0350DC9A3D2965F8A1C48B5419029F55E650CBC55E160EF8B4BD2E3C`). This verifies the current anchored test source mechanically; the historical full-run 1068/2/0/1070 remains a historical claim and was not rerun as part of this focused check.
- SB21-1 is at 8/8 requests. No ninth consultation was sent. Consultant acceptance of this post-cap evidence is therefore not claimed; the remaining distinction is review acceptance, not an identified product-correctness defect.
