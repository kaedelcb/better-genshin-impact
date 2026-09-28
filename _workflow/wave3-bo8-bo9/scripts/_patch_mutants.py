import io
p = r"_workflow/wave3-bo8-bo9/run-mutants.ps1"
t = io.open(p, encoding="utf-8").read()

# 1) per-mutation failure marker
old = """        $assertion = [regex]::Match($stack, 'WorkflowRunnerTests\\.cs:line \\d+').Value
        if (-not $message.Contains($failMarker) -or -not $assertion -or -not $stack.Contains($mutation.TestName)) {
"""
new = """        $assertion = [regex]::Match($stack, 'WorkflowRunnerTests\\.cs:line \\d+').Value
        $marker = if ($mutation.FailMarker) { $mutation.FailMarker } else { $failMarker }
        if (-not $message.Contains($marker) -or -not $assertion -or -not $stack.Contains($mutation.TestName)) {
"""
assert t.count(old) == 1
t = t.replace(old, new)

old = """            failure_contains = $failMarker
"""
new = """            failure_contains = $marker
"""
assert t.count(old) == 1
t = t.replace(old, new)

# 2) declare the marker for the persisted-tail-reopen mutant
old = """Purpose = 'BO-9: drop the entered-at-tail guard so a persisted tail record with live parks is reopened and driven.' }"""
new = """FailMarker = 'Assert.Empty() Failure'; Purpose = 'BO-9: drop the entered-at-tail guard so a persisted tail record with live parks is reopened and driven.' }"""
assert t.count(old) == 1
t = t.replace(old, new)

# 3) write records incrementally so an interrupt cannot lose verified results
old = """        [void]$records.Add($record)
        Write-Output ("PASS {0} target={1} outcome=P/F/P assertion={2}" -f $mutation.Id, $record.target_test_id, $assertion)
"""
new = """        [void]$records.Add($record)
        [IO.File]::WriteAllText((Join-Path $root '_workflow/wave3-bo8-bo9/mutation-records-final.json'), ((ConvertTo-Json -InputObject @($records) -Depth 20) + [Environment]::NewLine), $utf8NoBom)
        Write-Output ("PASS {0} target={1} outcome=P/F/P assertion={2} marker={3}" -f $mutation.Id, $record.target_test_id, $assertion, $marker)
"""
assert t.count(old) == 1
t = t.replace(old, new)
io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("script patched")
