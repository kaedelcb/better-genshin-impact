# SB21-1 owner-authorized GPT closeout review — R9

- Provider/model: GPT `gpt-6-astra`, `gpt_review`, medium effort.
- Tool-reported attempts: 1. Result returned successfully. This was the single owner-approved request after the original eight; no further consultation was sent.
- Scope: five GPT R8 findings plus the original GPT R2 Batch 51 R16 evidence finding.
- Snapshot under review: HEAD `5e8f72e9ea0948b660b0098857250424ab356769`, with the exact packet recorded beside this file.

## Reviewer result at the submitted snapshot

1. **R8 reverse-mutation evidence — 必改, open at the submitted snapshot.** The reviewer identified a malformed identity-mutation source hash in `current-source-reverse-mutations.md` and said the packet supplied directory pointers but not per-mutant/restored TRX contents or exact patch artifacts. It recognized that the repaired Resume fixture now uses a real workflow and checks unchanged state/revision. Risk was evidence confidence, not an observed remaining production defect. It requested reconciliation of the hash and a per-mutation map of changed behavior, named failure, restored hash, and restored-green result.
2. **R2 Batch 51 R16 evidence — 必改, partially open at the submitted snapshot.** The reviewer found the supplied §24.63 excerpt ended inside C and omitted U, explicitly named by R2. It accepted the supplied commit/blob/run values as materially improved but had not independently verified Git objects/hashes. It requested the complete U text and a clear distinction between the new targeted run and historical claims.
3. **R8 unresolved external-send facts — 重要, code/test disposition supported.** Source and the named recovery fixture support shared checks for Accepted/InFlight, SendAttempted, job ID, accepted identity, and PendingCompletion before startup/Resume parking writes.
4. **R8 successor ranking — 重要, code/test disposition supported.** The common candidate builder explicitly maps Plan/0; the reviewer noted the Plan/1 mutation is detected by the explicit expected-zero assertion, while the earlier “sync/async mismatch” description was imprecise.
5. **R8 boundary Hold precedence — 重要, code/test disposition supported.** The tested Continue→boundary Hold→later Wait path preserves the Hold reason and does not publish a wait binding.
6. **R8 UID diagnostic sanitization — 重要, code/test disposition supported.** Source sanitizes before persistence and Resume note; the named test verifies the persisted redacted value. The reviewer noted the UID assertion directly exercises initial parking, while Resume-note protection is source-inspection evidence.

The reviewer also separated material-out relay-document diffs and historical untracked artifacts from the committed SB21-1 scope, and noted there was no BGI runtime verification.

## Post-review local reconciliation (not a second GPT review)

- Corrected the identity source-hash transcription to the actual SHA-256 `781087DA6377F18E53E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06`.
- Added `current-source-reverse-mutation-audit.md`: all 11 valid mutant/restored TRX pairs are individually indexed with parsed named result, one-test outcome, artifact SHA-256, and source-hash match. The raw TRX files are retained at the linked paths. Four exploratory compile-error/no-TRX attempts remain explicitly excluded.
- Added the complete §24.63 U excerpt and independently rechecked the R16 source blob at both the anchor commit and current HEAD, the current source SHA-256, and the 104/104 TRX result/hash.
- Re-ran the targeted Batch21 fixture after reconciliation: 4/4 passed, 0 failed; raw TRX is `post-r9-targeted/batch21-wiring-post-r9-4of4.trx`.

These corrections and their mechanical checks were performed locally after R9. The reviewer did not inspect or accept these later additions; no such acceptance is claimed. No production gates were changed.
