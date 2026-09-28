# SB21-4 BO-13 R8 final review of five R1 IMPORTANT findings; BO-11 frozen inventory only; BO-6/7 not started evidence index

Mechanical checks only; no quality/reuse verdict.

- assistant-full-r6-baseline | test | _workflow/sb21-4/review/r6-review-validation-final/assistant-full/assistant-full-r6.trx | SHA256=573dd4b95f3d452530bc816d47021b3ff66f5209ad68d62c695fc33af46ebf6b
  Purpose: Historical full-suite baseline used only for testId comparison.; conditions: R6 full suite; historical testId comparison only, not current regression success.
- r8-assistant-build | build | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-build.log | SHA256=1901111c83ebe625f44fbf7b0ed165ed2700a437a0cb86ead43683a3f0e7a48a
  Purpose: Final-source assistant/test-project build; exit 0, 1 warning/0 errors.; conditions: dotnet build --no-restore -p:DeployToBgiTools=false -p:UseSharedCompilation=false; see captured command log and exitcode.
- r8-test-project-build | build | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/test-project-build.log | SHA256=800587240e61cf2baaba5db5f8164711908d3f011b5af5c0119a57120a203dce
  Purpose: Final-source assistant/test-project build; exit 0, 1 warning/0 errors.; conditions: dotnet build --no-restore -p:DeployToBgiTools=false -p:UseSharedCompilation=false; see captured command log and exitcode.
- r8-claim-surface-regen | test | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-regen/claim-regen.trx | SHA256=05a1cb1380afef0551befc6995c0a6194b76f25281c9fa7d26b7270d0f385035
  Purpose: Final declaration-surface regeneration; passed with CLAIM_SURFACE_REGENERATE=1.; conditions: CLAIM_SURFACE_REGENERATE=1; dotnet test --no-build --no-restore -p:DeployToBgiTools=false; manifest SHA stable.
- r8-claim-surface-noenv | test | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/claim-noenv/claim-noenv.trx | SHA256=1a8168982c64b7e21af48e9729b07a3f91b397a1c1378fbdd06788b51843d3e0
  Purpose: Final declaration-surface guard without regeneration environment; passed and manifest SHA unchanged.; conditions: CLAIM_SURFACE_REGENERATE removed; dotnet test --no-build --no-restore -p:DeployToBgiTools=false; SHA bd03b0a75b6895d51aab717e1d11ec8b7072b7ac7a697d888f65dc704e15ac3e before/after.
- r8-bo13-final | test | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/bo13/bo13.trx | SHA256=36897d27159cca361a45ddcaff060abab6ebe596def7998d8c648e9fec0dcc24
  Purpose: Final-source BO-13 named regression; 49/49 passed.; conditions: dotnet test --no-build --no-restore -p:DeployToBgiTools=false; filter FullyQualifiedName~LocalWaitFinalizationContractTests.
- r8-localwait-final | test | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/localwait/localwait.trx | SHA256=03fc98e7f9bfb59101d33cda7ab4c779194288a4dacea4fb6272ccdeed036d51
  Purpose: Final-source exact SB21 LocalWait regression set; 240/240 passed.; conditions: dotnet test --no-build --no-restore -p:DeployToBgiTools=false; exact four-part filter recorded in final-current-source-v4/localwait/localwait.log.
- r8-assistant-full-final | test | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/assistant-full/assistant-full.trx | SHA256=86df562d1b5f082621d1d4eb9651c6c53d2762f994d30e3407a4b964929df2f8
  Purpose: Final-source assistant full suite; 1558 passed/2 skipped/0 failed/1560 total.; conditions: dotnet test --no-build --no-restore -p:DeployToBgiTools=false.
- r8-assistant-full-initial-failure | test | _workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/assistant-full/results/assistant-full.trx | SHA256=6a807ca0793be7d75d6def53656e1451e96157082b85b8a10d84c27594dffef8
  Purpose: Preserved first full-suite failure before documentation fixture-name correction.; conditions: Failed only DocsFixtureReferenceGuardTests because the R5.3 terms OPEN_IMPORTANT and CLOSED_AT_IMPORTANT were interpreted as test fixture identifiers; changed wording to Chinese status prose, then ClaimSurface and full suite passed. Retained to explain failure identity, not current green evidence.
- r8-deliveries-final-closeout | document | _workflow/sb21-4/deliveries/final-closeout-r4.json | SHA256=365b57ca05a624f0b23d1121d1abe8a586d5498ec2dd10832d15eca677e5fbae
  Purpose: Final read-only parallel-delivery discovery scan; exit 0, queue 12, no unregistered reports/errors; task terminal state not inferred.; conditions: python -B tools/mistletoe/deliveries.py --root .; no automatic merge; task status cross-checked separately.
