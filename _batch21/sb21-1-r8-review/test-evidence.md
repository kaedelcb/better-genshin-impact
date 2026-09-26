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
