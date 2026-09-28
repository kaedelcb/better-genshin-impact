import io
p = r"_workflow/wave3-bo8-bo9/run-mutants.ps1"
t = io.open(p, encoding="utf-8").read()
t = t.replace("$outputRoot = '_workflow/wave3-bo8-bo9/mutations-round2'", "$outputRoot = '_workflow/wave3-bo8-bo9/mutations-round3'")
t = t.replace("mutation-records-round2.json", "mutation-records-round3.json")
old = "$preInsertName = 'MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.WorkflowRunnerTests.Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail'"
assert t.count(old) == 1
t = t.replace(old, old + "\n$settledParkName = 'MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.WorkflowRunnerTests.Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode'")
anchor = "    @{ Id = 'bo9-tail-reentry-parks-only-v5';"
assert t.count(anchor) == 1
t = t.replace(anchor,
    "    @{ Id = 'bo9-settled-park-probe-v5'; Mode = 'replace'; TestName = $settledParkName; "
    "Old = 'if (HasCompletedOutcome(run, parkOcc)) continue;'; New = 'if (false) continue;'; "
    "Purpose = 'BO-9 (round-2 review IMPORTANT-2): stop excluding settled park markers so a stale marker again spawns a same-round pre-insert obligation and the newly inserted node is executed an extra time.' },\n"
    + anchor)
io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("round-3 mutation set prepared (14 entries)")
