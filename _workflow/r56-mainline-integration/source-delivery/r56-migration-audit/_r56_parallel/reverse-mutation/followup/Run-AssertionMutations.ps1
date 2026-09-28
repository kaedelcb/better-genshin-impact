$ErrorActionPreference = 'Stop'
$source = Join-Path (Get-Location) 'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'
$project = 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'
$filter = 'FullyQualifiedName~SnapshotIntegrity_CompleteFileSetAndBytes_AreVerifiedAndCommitFailsClosed'
$dir = Join-Path (Get-Location) '_r56_parallel/reverse-mutation/followup'
$baseHash = '87905209E73AE3AE819D4382AA2AF643A942A2C4D2A687270C68D19670DB1ABA'
$mutants = @(
    [pscustomobject]@{ Name='missing'; Needle='if (!File.Exists(target)) return "snapshot_file_missing:" + p.Key;'; Replacement='if (false) return "snapshot_file_missing:" + p.Key;'; ExpectedLine=188 },
    [pscustomobject]@{ Name='changed'; Needle='if (!string.Equals(hash, p.Value, StringComparison.Ordinal)) return "snapshot_hash_mismatch:" + p.Key;'; Replacement='if (false) return "snapshot_hash_mismatch:" + p.Key;'; ExpectedLine=188 },
    [pscustomobject]@{ Name='commit-refusal'; Needle='if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);'; Replacement='if (string.IsNullOrEmpty(VerifySnapshot())) return MigrationResult.Fail("snapshot_invalid:" + VerifySnapshot(), m.Stage);'; ExpectedLine=190 },
    [pscustomobject]@{ Name='production-gate'; Needle='if (!auth.Success) return auth;'; Replacement='if (!auth.Success) { action(); return auth; }'; ExpectedLine=196 }
)
$records = [System.Collections.Generic.List[object]]::new()
$priorResultsPath = Join-Path $dir 'results.json'
if (Test-Path -LiteralPath $priorResultsPath) {
    foreach ($prior in @(Get-Content -LiteralPath $priorResultsPath -Raw | ConvertFrom-Json)) { $records.Add($prior) }
}
foreach ($mutation in $mutants) {
    if (@($records | Where-Object { $_.mutation -eq $mutation.Name }).Count -gt 0) { continue }
    $currentHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
    if ($currentHash -ne $baseHash) { throw "Unexpected source before $($mutation.Name): $currentHash" }
    $backup = Join-Path $dir "$($mutation.Name)-source-before-resume-$((Get-Date).ToString('HHmmssfff')).cs"
    [IO.File]::Copy($source, $backup, $false)
    $backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backup).Hash
    if ($backupHash -ne $baseHash) { throw "Backup hash mismatch for $($mutation.Name): $backupHash" }
    $text = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
    if ($mutation.Name -eq 'commit-refusal') {
        $methodStart = $text.IndexOf('public MigrationResult Commit()', [StringComparison]::Ordinal)
        $methodEnd = $text.IndexOf('public MigrationResult Rollback()', $methodStart, [StringComparison]::Ordinal)
        if ($methodStart -lt 0 -or $methodEnd -lt 0) { throw 'Could not anchor the Commit method.' }
        $at = $text.IndexOf($mutation.Needle, $methodStart, $methodEnd - $methodStart, [StringComparison]::Ordinal)
        if ($at -lt 0 -or $text.IndexOf($mutation.Needle, $at + 1, $methodEnd - $at - 1, [StringComparison]::Ordinal) -ge 0) {
            throw 'Commit guard must occur exactly once inside Commit().'
        }
    } else {
        $at = $text.IndexOf($mutation.Needle, [StringComparison]::Ordinal)
        if ($at -lt 0 -or $text.IndexOf($mutation.Needle, $at + 1, [StringComparison]::Ordinal) -ge 0) {
            throw "Mutation needle must occur exactly once: $($mutation.Name)"
        }
    }
    $mutated = $text.Substring(0, $at) + $mutation.Replacement + $text.Substring($at + $mutation.Needle.Length)
    [IO.File]::WriteAllText($source, $mutated, [Text.UTF8Encoding]::new($false))
    $mutantHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
    if ($mutantHash -eq $baseHash) { throw "Mutation did not change source: $($mutation.Name)" }
    $mutantLog = Join-Path $dir "$($mutation.Name)-mutant.log"
    $mutantTrx = "$($mutation.Name)-mutant.trx"
    $mutantArgs = @('test', $project, '-p:DeployToBgiTools=false', '--no-restore', '--filter', $filter,
        '--logger', "trx;LogFileName=$mutantTrx", '--results-directory', $dir)
    $mutantExit = -999
    $mutantRows = @()
    $mutantFailure = $null
    $restoreExit = -999
    $restoreRows = @()
    $restoreHash = ''
    try {
        & dotnet @mutantArgs *> $mutantLog
        $mutantExit = $LASTEXITCODE
        $trxPath = Join-Path $dir $mutantTrx
        if (-not (Test-Path -LiteralPath $trxPath)) { throw "Missing mutant TRX: $trxPath" }
        [xml]$trx = [IO.File]::ReadAllText($trxPath)
        $mutantRows = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like '*SnapshotIntegrity_CompleteFileSetAndBytes*' })
        if ($mutantRows.Count -ne 3) { throw "Expected 3 executed theory rows for $($mutation.Name), found $($mutantRows.Count)" }
        $targetName = if ($mutation.Name -eq 'commit-refusal') { 'missing' } elseif ($mutation.Name -eq 'production-gate') { 'changed' } else { $mutation.Name }
        $targetPattern = 'mutation: "' + $targetName + '"'
        $target = @($mutantRows | Where-Object { $_.testName.Contains($targetPattern) })
        if ($target.Count -ne 1 -or $target[0].outcome -ne 'Failed') {
            throw "Targeted theory row did not fail for $($mutation.Name)"
        }
        $mutantFailure = $target[0].Output.ErrorInfo.Message + [Environment]::NewLine + $target[0].Output.ErrorInfo.StackTrace
        if ($target[0].Output.ErrorInfo.StackTrace -notmatch (":line " + $mutation.ExpectedLine + "\b")) {
            throw "Wrong assertion failed for $($mutation.Name); expected test line $($mutation.ExpectedLine)"
        }
        if ($mutantExit -eq 0) { throw "Mutant test process unexpectedly exited 0: $($mutation.Name)" }
    }
    finally {
        [IO.File]::WriteAllBytes($source, [IO.File]::ReadAllBytes($backup))
        $restoreHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
        if ($restoreHash -ne $baseHash) { throw "Exact source restore failed for $($mutation.Name): $restoreHash" }
        $restoreLog = Join-Path $dir "$($mutation.Name)-restored.log"
        $restoreTrx = "$($mutation.Name)-restored.trx"
        $restoreArgs = @('test', $project, '-p:DeployToBgiTools=false', '--no-restore', '--filter', $filter,
            '--logger', "trx;LogFileName=$restoreTrx", '--results-directory', $dir)
        & dotnet @restoreArgs *> $restoreLog
        $restoreExit = $LASTEXITCODE
        $restoreTrxPath = Join-Path $dir $restoreTrx
        if (-not (Test-Path -LiteralPath $restoreTrxPath)) { throw "Missing restored TRX for $($mutation.Name)" }
        [xml]$restored = [IO.File]::ReadAllText($restoreTrxPath)
        $restoreRows = @($restored.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like '*SnapshotIntegrity_CompleteFileSetAndBytes*' })
        if ($restoreExit -ne 0 -or $restoreRows.Count -ne 3 -or @($restoreRows | Where-Object outcome -ne 'Passed').Count -ne 0) {
            throw "Restored theory did not pass 3/3 for $($mutation.Name)"
        }
    }
    $records.Add([pscustomobject]@{
        mutation = $mutation.Name
        sourceBytesBefore = (Get-Item -LiteralPath $backup).Length
        sourceHashBefore = $backupHash
        mutantHash = $mutantHash
        mutantExitCode = $mutantExit
        mutantTestCount = $mutantRows.Count
        mutantFailedCount = @($mutantRows | Where-Object outcome -eq 'Failed').Count
        namedFailure = $target[0].testName
        assertionLine = $mutation.ExpectedLine
        failure = $mutantFailure
        restoredHash = $restoreHash
        restoredExact = ($restoreHash -eq $baseHash)
        restoredExitCode = $restoreExit
        restoredPassedCount = @($restoreRows | Where-Object outcome -eq 'Passed').Count
    })
    [IO.File]::WriteAllText((Join-Path $dir 'results.json'), ($records | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash -ne $baseHash) { throw 'Final source hash differs from baseline.' }
$records | ConvertTo-Json -Depth 5
