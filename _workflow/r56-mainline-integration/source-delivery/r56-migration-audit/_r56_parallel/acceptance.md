# R5.6 Migration Transaction and Rollback Component — Acceptance

## Goal and scope

Complete an independent verification of the R5.6 migration transaction and rollback components, repair only proven defects within scope, run the required regression and reverse-mutation checks, and deliver a reviewable report and commit. This is a component batch only; it does not complete all of R5.6 or R5.

Baseline captured 2026-09-27 01:02 UTC:
- Original workspace: `E:\Program Files\better-genshin-impact-LCB`, branch `main-OldTeaBag-B168`, HEAD `8a4b8d988e2f0ff3ade35538bffa2de7a93c9da2`; 0 staged, 3 unstaged, 168 untracked entries (171 total). It remains read-only.
- Isolated managed worktree: `C:\Users\Administrator\.codex\worktrees\r56-migration-audit\better-genshin-impact-LCB`, detached at the same HEAD; clean before this file was created.
- Original-workspace-only inputs read directly and not assumed copied: `AGENTS.md`, `.agents/skills/bgi-project-development/SKILL.md`, and `_batch21/b21_plan.md`. The first two are ignored local materials and are absent from the isolated checkout; the Skill was read from the original workspace. No SB21 work is performed here.

## Authoritative inputs

Use the task's AGENTS.md and bgi-project-development Skill from the original workspace, plus:
- `槲寄生调度器总计划.md`
- `Docs/design/onedragon-r5-design-review-and-breakdown-2026-09-19.md`
- `Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md`, especially §21 and §23
- `Docs/design/onedragon-r5-handoff-2026-09-21.md`
- `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`, especially §24.96
- `_batch21/b21_plan.md` only to confirm SB21's in-flight boundary

Read only relevant excerpts. Validate legacy completion claims against current code and evidence.

## Allowed file changes

- `MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`
- `MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs`
- `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs`
- `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R58MigrationRehearsalTests.cs`
- Additional migration-only test files if a specific defect or evidence gap requires them.
- This `_r56_parallel/` directory for acceptance, inventory, logs, consultation ledger (only if consultation is used), report, and evidence.

Do not modify TaskCenterHost, WorkflowRunner, RunStore, MainViewModel, wait queues/consumers, shared models, build configuration, shared plans/handoff, ClaimSurfaceManifest, task-relays ledgers, or any SB21-2/3/4 implementation. Do not do production wiring, real reference/activation orchestration, UI work, or whole-R5.8 acceptance. Record out-of-scope findings with concrete entry point, counterexample, severity, impact, and dependency; preserve blockers that need owner/product decisions or shared-module work.

## Required coverage matrix

For every row, report: requirement; actual consumer/entry point; existing fixture; evidence; gap; status as implemented, stub-tested, unwired, unverified, or blocked.

1. Complete configuration snapshot: exact file set, bytes, SHA-256 manifest integrity.
2. Unique commit marker, durable stage persistence/read-back, and no execution before commit.
3. Quiescence bound to instance session and generation; release, reacquire, and restart invalidate old eligibility.
4. Failure/recovery at snapshot, reference update, activation, commit, and rollback; partial state fails closed.
5. Rollback restores changed/deleted files, removes only files created by this transaction, preserves unrelated files, and resumes idempotently.
6. Assistant-side workflow and activation metadata restoration: distinguish actual implementation, delegated interface/stub, unwired, and unverified.
7. Rehearsal root isolation from real User root, path identity, and rejection of exceptional/ambiguous paths.

Do not claim whole-machine/whole-old-ledger manual recovery, independent Windows service, TPM, or network witness; these are out of scope under §24.96. Component tests do not prove production entry wiring. Exception injection is not a child-process crash. Distinguish delegated stubs, persistence read-back, instance recreation, and actual process-crash evidence.

## Repair and validation

- Inventory source encoding/BOM/newlines and size/hash before edits; read back after writes; review `git diff --stat` and stop if its scope differs.
- Add only diagnostic fixtures for a specific defect/evidence gap. For each in-scope defect, add a red counterexample first, then make the smallest repair. For key assertions, perform a reverse mutation, record the named assertion failing, restore source exactly, and show it green after restoration.
- Build/test only against this isolated worktree and a self-created independent config root; never touch real User, APPDATA configuration, runtime ledger, or user process.
- Before building, inspect the existing `DeployToBgiTools=false` condition and use it. Run migration-targeted tests first; after repairs run the full assistant suite and compare failures by identity to a same-condition baseline. Do not remove, exclude, or weaken tests. Record exact commands, exit codes, source version, and existing log/TRX paths and hashes.
- If consultation is needed, first read the review-disposition discipline and GPT workspace-executor Skill, inspect history/counters, use GPT only, count every dispatched request (including failure/timeout) toward a maximum of 8 new requests for this Goal, preserve original severity, and attach full porcelain plus relevant unstaged diff per request. Keep its ledger under this directory and do not change SB21 ledgers.

## Evidence and close gates

Deliver `_r56_parallel/report.md` with Goal/baseline/scope; the full coverage matrix; defect/repair and consultation disposition; targeted, reverse-mutation, full-assistant, and same-condition baseline-difference results; actual evidence paths/hashes; exact commit and file list; remaining worktree state and baseline-drift limits; and integration regressions required after mainline integration. Keep “implemented”, “stub-tested”, “unwired”, “unverified”, and “blocked” distinct.

All production gates stay closed: no production entry wiring, downstream consumer, real User path, deployment/release, push, or R5.8 sign-off. Do not cherry-pick or merge to the in-flight main workspace. Commit only with `git commit --only -m "<message>" -- <explicit-file-list>`; add new files individually and never use `git add -A` or `git add .`.

Completion requires all necessary in-scope correctness repairs and required review to be closed, complete evidence, and no unexplained new regression failures. If an in-scope important/required item remains open, leave the Goal incomplete, preserve closed gates, and deliver an owner blocking checkpoint. Never claim whole R5.6 or R5 completion.
