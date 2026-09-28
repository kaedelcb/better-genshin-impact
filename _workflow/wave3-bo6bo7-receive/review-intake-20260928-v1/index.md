# wave3-bo6bo7-receive evidence index

Mechanical checks only; no quality/reuse verdict.

- receive-assistant-project-rebuild | build | _workflow/wave3-bo6bo7-receive/regression/final/assistant-project-rebuild.log | SHA256=3022f7b91c48ef0839afb3b51636546f84d3fb4257e68576ff874845e9654429
  Purpose: Integrated mainline rebuild of the assistant product project with deployment disabled.; conditions: dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0; deployment target not written (see receive-deploy-target-before).
- receive-testproject-rebuild | build | _workflow/wave3-bo6bo7-receive/regression/final/testproject-rebuild.log | SHA256=13c57566d740d4f2ac1afd4558f4fb90510af0bef3d191eb2c545840c140851f
  Purpose: Integrated mainline rebuild of the assistant unit-test project.; conditions: dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0.
- receive-targeted-93 | test | _workflow/wave3-bo6bo7-receive/regression/final/targeted-93.trx | SHA256=3769703968a4bad368d1be4a57a9994ccfd7e794820a39d9f9fe991650406fbe
  Purpose: Integrated-version targeted runner/local-wait suite (3 classes); the counterexample evidence for the behaviour risk rows.; conditions: dotnet test --no-build --filter <LocalWaitIdentityTranslationTests|LocalWaitParkingStateContractTests|WorkflowRunnerTests>; 93/93 passed; executed after the final claim-surface regeneration so the binding is exact.
- receive-full-1564 | test | _workflow/wave3-bo6bo7-receive/regression/final/assistant-full-1564.trx | SHA256=2881fcb25d1ffe7c590783ae8a07436d213ef928e72b895ebbd8debd10353f92
  Purpose: Integrated-version full assistant regression.; conditions: dotnet test --no-build (no filter); 1562 passed / 2 skipped / 0 failed / 1564; executed after the final claim-surface regeneration.
- receive-claim-surface-regen | test | _workflow/wave3-bo6bo7-receive/regression/claim-surface/regen/claim-surface-regen.trx | SHA256=f4d6d70716f74af1a231005c768c72bb543e72409a3acd0f29701fbbd20be0d6
  Purpose: Declaration-surface regeneration after the R5.3 24.126 registration was added.; conditions: CLAIM_SURFACE_REGENERATE=1; ClaimSurfaceGuardTests 1/1 passed; manifest 599 -> 602 lines (+3/-0).
- receive-claim-surface-no-env | test | _workflow/wave3-bo6bo7-receive/regression/claim-surface/no-env/claim-surface-no-env.trx | SHA256=b7eb70498b9d44d549fabeb837307823a72c45e235e9d92cb0f1809c3d394187
  Purpose: Declaration-surface guard re-run with the regeneration variable cleared.; conditions: CLAIM_SURFACE_REGENERATE unset; ClaimSurfaceGuardTests 1/1 passed; manifest SHA stable at 5D914611... across regen and no-env.
- receive-fact-testids | document | _workflow/wave3-bo6bo7-receive/regression/final/targeted-fact-testids.json | SHA256=35af90f926c81123b98e9602b10b3229f1e56b58b7da61b0175e958e2f87dcaa
  Purpose: testIds of the four new real-Runner facts and the six relocated/changed helper fixtures.; conditions: Extracted from receive-targeted-93.trx by testMethod name.
- receive-testid-comparison | document | _workflow/wave3-bo6bo7-receive/regression/testid-comparison-receive.json | SHA256=cb0167a82ce4a9487c1def98df2e6dc03ac54d55cf86d61423bb3d87ca62e9e4
  Purpose: testId-level structural comparison of the opening baseline full TRX against the integrated full TRX.; conditions: tools/mistletoe/workflow.py trx --baseline <opening full> --final <integrated full>; 1556 unchanged / 8 added / 4 removed / 0 changed; added and removed id sets identical to the source delivery.
- receive-targeted-identity | document | _workflow/wave3-bo6bo7-receive/regression/targeted-testid-identity.json | SHA256=3b6c4690ac9025eaeb946f5446503385d916c99cd9646ae47fe588dfa5d02cff
  Purpose: Proof that the integrated targeted suite has exactly the source delivery final targeted testId set.; conditions: 93 testIds, names and outcomes identical to the source worktree final targeted TRX.
- receive-mutation-records | document | _workflow/wave3-bo6bo7-receive/mutation-records.json | SHA256=f82e8c08c31b28a93765274fe86789268391b33fbeb2fdc5f9d0c158bfb2e66d
  Purpose: Machine-readable records of the eight reverse mutations re-executed on the integrated bytes.; conditions: Produced by _workflow/wave3-bo6bo7-receive/run-receive-mutants.ps1 under pwsh 7; per-mutation build/test logs and TRX under _workflow/wave3-bo6bo7-receive/mutations/<id>/.
- receive-manifest-diff | document | _workflow/wave3-bo6bo7-receive/regression/claim-surface/manifest-diff-summary.json | SHA256=b35e2e81462f0cadc7120e08719ff89684efb10011648c4d7cc532e7355e4f6f
  Purpose: Reviewed diff of the declaration-surface regeneration.; conditions: Exactly 3 added lines (all from the new R5.3 24.126 text), 0 removed, no pre-existing claim line altered.
- receive-deploy-target-before | document | _workflow/wave3-bo6bo7-receive/deploy-target-before.txt | SHA256=40eac866780d6f45b9a7d1d7f66e2dbc334140a92e4164a2be314f289ef780b4
  Purpose: Deployment-target state captured before the first build; the same LastWriteTimeUtc/file count holds afterwards.; conditions: Path BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant; 1158 files; 09/26/2026 21:41:14 UTC unchanged after all builds/tests.
- receive-intake-note | document | _workflow/wave3-bo6-bo7/INTAKE-NOTE.md | SHA256=5c6a4869b94779e4bfea567dbd19f531341bdd16455b14da214cbc117c552c70
  Purpose: Boundary note for the partially imported source evidence directory.; conditions: States what is imported and what remains only in the retained source worktree.
- receive-findings | document | _workflow/wave3-bo6bo7-receive/findings.md | SHA256=88a43a86bb0e27df2eec6f853bcf554ae7e8385e2eb415038169a6520d69ddea
  Purpose: Batch findings, mutation summary, regressions, and residual items.; conditions: Packet findings role.
- receive-budget | document | _workflow/wave3-bo6bo7-receive/budget.md | SHA256=f075d156ad76edd0be9a0bd943acc9c68257dc7919bbff0f2505672866a13b41
  Purpose: Consultation budget, fixed model/strength, original severities, unclosed items.; conditions: Packet budget role.
- receive-context | document | _workflow/wave3-bo6bo7-receive/context.md | SHA256=68953edeb4e0ae10235dcd5d08915b276ee9d0da344131ae3a2477f70286f283
  Purpose: Batch objective and completion criteria.; conditions: Packet objective role.
- source-owner-checkpoint | document | _workflow/wave3-bo6-bo7/owner-checkpoint.md | SHA256=c365370e4d963fe5d66d755120e81202bf5f0bc3cfd822e47e68ab8833f611e9
  Purpose: Authoritative source checkpoint; SHA-256 A4AB77476298FD7AB3847930C0A92BA72F234424EC6A4D3B1706AB17809C75B1 verified at intake.; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-risk-matrix | document | _workflow/wave3-bo6-bo7/risk-matrix.json | SHA256=1c9843aaf1d29fa5ba3fe5706322c2730bc603ba60e858c43123ac35d089d4aa
  Purpose: Source batch risk matrix and its coverage mapping.; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-mutation-index | document | _workflow/wave3-bo6-bo7/mutations/raw-mutation-evidence-index.md | SHA256=04159b526f8ad77365044f3f885bd7b1b7d4a1ef74bfeedf17314d6955895594
  Purpose: Source batch mutation evidence index.; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-ledger-reconciliation | document | _workflow/wave3-bo6-bo7/consultation/current-ledger-reconciliation.json | SHA256=15313b6fbb1efa5edbca3f6630119170d9f24854e28f53b2112900a3b1224829
  Purpose: Source consultation ledger reconciliation (8/8 conservative count, balance 0).; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-disposition-9-10 | document | _workflow/wave3-bo6-bo7/consultation/owner-approved-requests-9-10.md | SHA256=6b6aea8e75c20bdd9719a1660c7661789b59aea0353a9ffab4430084402c3734
  Purpose: Item-by-item disposition of the two owner-approved additional consultations.; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-testid-comparison | document | _workflow/wave3-bo6-bo7/regression/final/testid-comparison-final.json | SHA256=9325a28b118604ad588a727a4bc310f55785226054c2d7ed5e23b5062c230e65
  Purpose: Source delivery testId comparison used for the independent cross-check.; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-context | document | _workflow/wave3-bo6-bo7/context.md | SHA256=55be4367678e8881b33fdea147a4241759da94b230f4dbba0f8a28a73a599585
  Purpose: Source batch objective (context role).; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-findings | document | _workflow/wave3-bo6-bo7/findings.md | SHA256=41af621a8d1def2b741cc7edf181d29e796b201e34b20bea1da427d37b45419d
  Purpose: Source batch findings (contrast material).; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-budget | document | _workflow/wave3-bo6-bo7/budget.md | SHA256=577e5dd2e5b903e71589508fedd3bc7ac049445cad59c8c4cb194735dac51ed7
  Purpose: Source batch budget ledger (contrast material).; conditions: Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.
- source-review-a-bo6 | consult | _workflow/wave3-bo6-bo7/review-cli-a-bo6/run/review.md | SHA256=4d0a8cd77fc4c39d3841d83e6ab9cc2f6f3640721ae3f238c92acadeea0fdc8e
  Purpose: Independent read-only review report closing BO-6 R19 MUST and BO-6 R21 F4 IMPORTANT at the delivered bytes.; conditions: Local Codex CLI read-only, gpt-6-astra / medium, exit 0, 182.8 s, fixed snapshot HEAD 0f46128f; report SHA-256 de8a8176...b4e8bc.
- source-review-b-bo7 | consult | _workflow/wave3-bo6-bo7/review-cli-b-bo7/run/review.md | SHA256=b385e7732c83b51e7b0e9a256efd1c13e3e252912f9fd8d142bceecc37bd59e9
  Purpose: Independent read-only review report closing BO-7 R21 F1/F2 MUST at the delivered bytes.; conditions: Local Codex CLI read-only, gpt-6-astra / medium, exit 0, 183.0 s, fixed snapshot HEAD 0f46128f; report SHA-256 5b886136...438479.
- source-review-a-meta | document | _workflow/wave3-bo6-bo7/review-cli-a-bo6/run-metadata.json | SHA256=6fd057a5100a7429c8304662cb4007052af74b1623af0debf518c481e01ff817
  Purpose: Run metadata (model, strength, read-only, exit code, usage) for the BO-6 review.; conditions: Request 9; model verified from the local rollout record.
- source-review-b-meta | document | _workflow/wave3-bo6-bo7/review-cli-b-bo7/run-metadata.json | SHA256=992eb9090ce17609063ca028a942a350870cfc92854f61b97eb9b3f63f688296
  Purpose: Run metadata (model, strength, read-only, exit code, usage) for the BO-7 review.; conditions: Request 10; model verified from the local rollout record.
- opening-baseline-build | build | _workflow/wave3-bo6bo7-receive/baseline/testproject-build.log | SHA256=8b25fb731dbf94734dbac492165f96ae280f7b09446ac160cad1129df9d081f2
  Purpose: Pre-intake mainline baseline build (opening HEAD 6fd6207e5).; conditions: dotnet build -t:Rebuild -p:DeployToBgiTools=false; exit 0; executed before any file was imported.
- opening-baseline-targeted | test | _workflow/wave3-bo6bo7-receive/baseline/targeted-baseline.trx | SHA256=4762e8edbadce100cdfef099b1e1474bf46a9fb36411ea0d7f6a18f4d5faf283
  Purpose: Pre-intake targeted three-class baseline (89 tests) for the testId differential.; conditions: Same filter as receive-targeted-93; 89/89 passed; 4 helper tests carry their pre-intake names.
- opening-baseline-full | test | _workflow/wave3-bo6bo7-receive/baseline/assistant-full-baseline.trx | SHA256=b39b4db49ca099364f8dfc83dbbfdc4490ec2e730972187a6914e99dab4548c7
  Purpose: Pre-intake full assistant baseline; same-condition reference for the comparison.; conditions: 1558 passed / 2 skipped / 0 failed / 1560; matches the source delivery opening baseline.
- receive-preRegen-targeted | test | _workflow/wave3-bo6bo7-receive/regression/targeted-integrated-93.trx | SHA256=f9ff310392b9bf46382164425f2a3630a5f11b2bd587b1f208d6bfbb855829ec
  Purpose: Intermediate integrated targeted run executed before the declaration-surface regeneration.; conditions: 93/93; superseded by receive-targeted-93 after ClaimSurfaceManifest.txt changed, kept for traceability (same outcome, not used as current green evidence).
- receive-preRegen-full | test | _workflow/wave3-bo6bo7-receive/regression/assistant-full-integrated.trx | SHA256=ce938dbbc4fe434e15d4d6135e1318efc45bc671eb35e11049ee511fef14c56c
  Purpose: Intermediate integrated full run executed before the declaration-surface regeneration.; conditions: 1562/2/0/1564; superseded by receive-full-1564 for the same reason; kept for traceability.
