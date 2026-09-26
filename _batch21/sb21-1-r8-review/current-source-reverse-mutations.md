# SB21-1 current-source reverse mutations (post-R8)

Scope: exact current guards/policies named in GPT R8 plus the accepted-without-job recovery edge. For each valid row, the named Fact failed under the mutant, source bytes were restored to the exact listed SHA-256, and the same Fact passed on the restored source. TRX/log paths are relative to this directory.

| Mutation | Named Fact | Mutant | Source SHA-256 before = restored | Restored baseline |
|---|---|---|---|---|
| BO-1 reference-provider catch disabled | `RegistrationFailures_ParkZeroSend_NotUnknownConvergence` | named test Failed | `781087DA6377F18E53E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06` | Pass |
| BO-1 scope-provider catch disabled (balanced catch replacement) | `ScopeAndQueueFailures_ParkZeroSend_NotUnknownConvergence` | named test Failed | `781087DA6377F18E53E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06` | Pass |
| BO-1 identity-construction catch disabled (balanced catch replacement) | `RegistrationFailures_ParkZeroSend_NotUnknownConvergence` | named test Failed | `781087DA6377F18E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06` | Pass |
| EV1-R1 integrity guard bypassed | `OccupantLevels_CorruptRunRecord_KeepsUnknownAndLogs` | named test Failed | `1CEBB2D3FCBA2BF594B8A78DCAB0E036B767ECD816F1E9AA859994347210AF7C` | Pass |
| EV1-R1 two scans restored | `OccupantLevels_CorruptRunRecord_KeepsUnknownAndLogs` | named test Failed (enumeration interleaving assertion) | `1CEBB2D3FCBA2BF594B8A78DCAB0E036B767ECD816F1E9AA859994347210AF7C` | Pass |
| Resume control check moved after run write | `RegistrationFailures_ParkZeroSend_NotUnknownConvergence` | named test Failed (state/revision changed before rejection) | `781087DA6377F18E53E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06` | Pass |
| shared unresolved-fact predicate omits `SendAttempted` | `OccupantLevels_CorruptRunRecord_KeepsUnknownAndLogs` | named test Failed (expected Unknown, got Interrupted) | `F7DE1021D384872E99A6C05C42F2D35BA5FD86515880C2F67354A3F087CE612C` | Pass |
| boundary Hold precedence disabled | `RegistrationFailures_ParkZeroSend_NotUnknownConvergence` | named test Failed (Hold reason lost) | `781087DA6377F18E53E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06` | Pass |
| wait-reason sanitization disabled | `RegistrationFailures_ParkZeroSend_NotUnknownConvergence` | named test Failed (raw UID persisted) | `781087DA6377F18E53E8E904A8CEBC1A904D804B28D9FD2E3A826A7679100D06` | Pass |
| successor Plan/0 mapping changed to Plan/1 | `DefaultConstructedHost_WiresLocalWaitStack` | named test Failed (sync/async candidate mismatch) | `1CEBB2D3FCBA2BF594B8A78DCAB0E036B767ECD816F1E9AA859994347210AF7C` | Pass |
| accepted in-flight predicate removed (accepted-only/no-job case) | `OccupantLevels_CorruptRunRecord_KeepsUnknownAndLogs` | named test Failed (expected Unknown, got Interrupted) | `F7DE1021D384872E99A6C05C42F2D35BA5FD86515880C2F67354A3F087CE612C` | Pass |

Evidence directories retain each mutant/restored TRX and build log: `current-mutant-results/`, `repaired-mutant-results/`, `accepted-no-job-inflight-mutant2/`, and `send-attempted-current-mutant/`.

The first R8 harness run produced four compile-error/no-TRX attempts (scope catch, identity catch, two-scan, ranking). They are explicitly excluded as mutation detections; each source was restored exactly. The four behaviors were rerun with balanced/minimal mutants above and produced named assertion failures plus green restored baselines.

One intermediate attempt removed only the explicit `Intent == Accepted` clause and remained green because the canonical `WorkflowSubmission.InFlight` predicate also covers Accepted with no observed terminal. That redundant-clause mutation is not a detection claim; the valid accepted-without-job mutant removed both terms, produced the named Unknown-vs-Interrupted failure, and restored green.

Total valid current-source mutants in this file: **11/11 detected**, exact source restoration confirmed for each, restored targeted baseline passed for each. Earlier BO-1/EV1 historical mutations remain separately recorded in `previous-reverse-mutants.md` and `reverse-mutation-evidence.md`.
