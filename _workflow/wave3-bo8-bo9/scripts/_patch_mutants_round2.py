import io, sys
p = r"_workflow/wave3-bo8-bo9/run-mutants.ps1"
t = io.open(p, encoding="utf-8").read()

# output root / records file for the post-review-1 re-run
t = t.replace("$outputRoot = '_workflow/wave3-bo8-bo9/mutations-final'",
              "$outputRoot = '_workflow/wave3-bo8-bo9/mutations-round2'")
t = t.replace("mutation-records-final.json", "mutation-records-round2.json")

# reentrancy helper renamed -> rebind the two re-entry patterns
old_re = "$reentryOld = 'if (!enteredAtTail && TryRelocateToLivePark(run, plan, out var livePark))'"
new_re = "$reentryOld = 'if (!enteredAtTail && TryRelocateToOutstandingObligation(run, plan, out var obligation))'"
assert t.count(old_re) == 1
t = t.replace(old_re, new_re)
t = t.replace("New = 'if (false && TryRelocateToLivePark(run, plan, out var livePark))'",
              "New = 'if (false && TryRelocateToOutstandingObligation(run, plan, out var obligation))'")
t = t.replace("New = 'if (TryRelocateToLivePark(run, plan, out var livePark))'",
              "New = 'if (TryRelocateToOutstandingObligation(run, plan, out var obligation))'")

# plan-order selection inside the rebuilt helper: variable renamed occ -> candidate
old_order = "Old = '|| occ.LoopIteration < earliest.LoopIteration'"
new_order = "Old = '|| candidate.LoopIteration < earliest.LoopIteration'"
assert t.count(old_order) == 1
t = t.replace(old_order, new_order)

# new fixture name + mutation entry (parks-only re-entry)
anchor = "$failMarker = 'Assert.Equal() Failure'"
assert t.count(anchor) == 1
t = t.replace(anchor, "$preInsertName = 'MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.WorkflowRunnerTests.Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail'\n" + anchor)

entry_anchor = "    @{ Id = 'bo9-persisted-tail-reopen-v5';"
assert t.count(entry_anchor) == 1
new_entry = ("    @{ Id = 'bo9-tail-reentry-parks-only-v5'; Mode = 'replace'; TestName = $preInsertName; "
             "Old = 'probe is not null && probe.SequenceIndex < parkOcc.SequenceIndex;'; New = 'false;'; "
             "Purpose = 'BO-9 (review-1 IMPORTANT-1): restore tail re-entry to parks only, dropping the same-round pre-insert unexecuted-occurrence reconstruction so the selected rescue obligation is silently dropped and the run falsely succeeds.' },\n"
             + entry_anchor)
t = t.replace(entry_anchor, new_entry)
io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("mutation script updated for round-2 rebinding + new parks-only mutant")
