$ErrorActionPreference = 'Stop'

# Receive-batch reverse mutations, executed on the INTEGRATED mainline working tree.
# Adapted from the source batch runner (_workflow/wave3-bo6-bo7/mutations/run-final-mutants.ps1
# and run-tail-persisted-mutant.ps1); output paths are the receive batch only.
$root = (Get-Location).Path
$sourcePath = Join-Path $root 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'
$testSourcePath = Join-Path $root 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs'
$project = 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'
$outputRoot = '_workflow/wave3-bo6bo7-receive/mutations'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$mixedName = 'MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.WorkflowRunnerTests.Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences'
$tailName = 'MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.WorkflowRunnerTests.Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion'
$candidateName = 'MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.WorkflowRunnerTests.Resume_CandidateAndRescueAcrossLoop_UsesFullPlanOrderAndAdvancesThroughRunner'
$rescueName = 'MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.WorkflowRunnerTests.Resume_RescueBeforeNextLoopCandidate_UsesFullPlanOrderAndFiltersCompletedAnchor'
$failMarker = 'Assert.Equal() Failure'

$mutations = @(
    @{ Id = 'bo6-resume-live-park-v3'; Mode = 'replace'; TestName = $mixedName; Old = 'if (!run.TailReached && run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord))'; New = 'if (false && run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord))'; Purpose = 'Disable the explicit-Resume parked recomputation so a still-locatable R cursor hides the earlier P1/Q parks.' },
    @{ Id = 'bo6-completed-filter-v3'; Mode = 'replace'; TestName = $mixedName; Old = 'if (filterCompletedDuringParkRecovery && HasCompletedOutcome(run, occurrence))'; New = 'if (false && HasCompletedOutcome(run, occurrence))'; Purpose = 'Disable the completed-identity filter inside park-recovery advancement.' },
    @{ Id = 'bo6-tail-fail-closed-v3'; Mode = 'replace'; TestName = $tailName; Old = 'if (hadBadOutcome || unresolvedParkedOutcome || flowFailure)'; New = 'if (hadBadOutcome || flowFailure)'; Purpose = 'Disable only the unresolved-parked tail fail-closed aggregation.' },
    @{ Id = 'bo7-candidate-first-v3'; Mode = 'replace'; TestName = $candidateName; Old = 'candidate.LoopIteration < parkedRescue.LoopIteration'; New = 'candidate.LoopIteration > parkedRescue.LoopIteration'; Purpose = 'Invert the cross-loop candidate/rescue full-order comparison.' },
    @{ Id = 'bo7-rescue-first-v3'; Mode = 'replace'; TestName = $rescueName; Old = 'return parkedRescue ?? candidate;'; New = 'return candidate ?? parkedRescue;'; Purpose = 'Let the candidate unconditionally override an earlier parked rescue point.' },
    @{ Id = 'bo7-earliest-park-v3'; Mode = 'replace'; TestName = $mixedName; Old = 'candidateOcc.SequenceIndex < earliestOcc.SequenceIndex'; New = 'candidateOcc.SequenceIndex > earliestOcc.SequenceIndex'; Purpose = 'Invert the plan-order selection among multiple active parks in one loop iteration.' },
    @{ Id = 'bo7-stable-identity-v3'; Mode = 'replace'; TestName = $mixedName; Old = '&& o.LoopIteration == occurrence.LoopIteration);'; New = '&& o.LoopIteration == occurrence.LoopIteration && o.SequenceIndex == occurrence.SequenceIndex);'; Purpose = 'Wrongly fold the mutable SequenceIndex into the stable completion identity.' },
    @{ Id = 'bo6-tail-persisted-failure-v4'; Mode = 'remove-write'; TestName = $tailName; Purpose = 'Remove only the durable _runs.Update(run) from the unresolved-parked aggregation branch; the in-memory record still returns Failed.' }
)

function Get-Sha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([Convert]::ToHexString($sha.ComputeHash($Bytes))).ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Invoke-Build([string]$Directory) {
    $log = Join-Path $Directory 'build.log'
    & dotnet build $project -t:Rebuild -p:DeployToBgiTools=false *> $log
    return $LASTEXITCODE
}

function Invoke-TargetTest([string]$Directory, [string]$TestName, [string]$TrxName) {
    $log = Join-Path $Directory 'test.log'
    & dotnet test $project -p:DeployToBgiTools=false --no-build --filter "FullyQualifiedName~$TestName" `
        --logger "trx;LogFileName=$TrxName" --results-directory $Directory *> $log
    return $LASTEXITCODE
}

function Get-TargetResult([string]$TrxPath, [string]$TestName) {
    [xml]$trx = [IO.File]::ReadAllText($TrxPath)
    $rows = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | Where-Object { $_.GetAttribute('testName') -eq $TestName })
    if ($rows.Count -ne 1) { throw "Expected one target test in $TrxPath; found $($rows.Count)." }
    return $rows[0]
}

$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
$hasBom = $sourceBytes.Length -ge 3 -and $sourceBytes[0] -eq 0xef -and $sourceBytes[1] -eq 0xbb -and $sourceBytes[2] -eq 0xbf
$prefixLength = if ($hasBom) { 3 } else { 0 }
$sourceEncoding = New-Object System.Text.UTF8Encoding($hasBom)
$sourceText = [Text.Encoding]::UTF8.GetString($sourceBytes, $prefixLength, $sourceBytes.Length - $prefixLength)
$originalSha = Get-Sha256 $sourceBytes
$testSourceSha = (Get-FileHash -LiteralPath $testSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
$records = New-Object System.Collections.ArrayList

try {
    foreach ($mutation in $mutations) {
        if ((Get-Sha256 ([IO.File]::ReadAllBytes($sourcePath))) -ne $originalSha) {
            throw "Source drift before mutation $($mutation.Id)."
        }
        if ($mutation.Mode -eq 'replace') {
            $oldCount = ([regex]::Matches($sourceText, [regex]::Escape($mutation.Old))).Count
            if ($oldCount -ne 1) { throw "Mutation $($mutation.Id) expected one source pattern; found $oldCount." }
            $mutantText = $sourceText.Replace($mutation.Old, $mutation.New)
            $mutantLabel = $mutation.Old + '  ==>  ' + $mutation.New
        }
        else {
            $anchor = 'if (hadBadOutcome || unresolvedParkedOutcome || flowFailure)'
            $anchorIndex = $sourceText.IndexOf($anchor, [StringComparison]::Ordinal)
            if ($anchorIndex -lt 0 -or $sourceText.IndexOf($anchor, $anchorIndex + $anchor.Length, [StringComparison]::Ordinal) -ge 0) {
                throw 'Expected one unresolved parked aggregation branch.'
            }
            $updateIndex = $sourceText.IndexOf('_runs.Update(run);', $anchorIndex, [StringComparison]::Ordinal)
            $returnIndex = $sourceText.IndexOf('return run;', $anchorIndex, [StringComparison]::Ordinal)
            if ($updateIndex -lt $anchorIndex -or $returnIndex -lt $updateIndex -or $returnIndex - $updateIndex -gt 1000) {
                throw 'Could not isolate the durable Failed write within the target aggregation branch.'
            }
            $mutantText = $sourceText.Remove($updateIndex, '_runs.Update(run);'.Length)
            $mutantLabel = 'remove _runs.Update(run); from the unresolved parked aggregation branch'
        }
        $mutantBytes = $sourceEncoding.GetBytes($mutantText)
        $mutantSha = Get-Sha256 $mutantBytes
        if ($mutantSha -eq $originalSha) { throw "Mutation $($mutation.Id) did not change source bytes." }

        $mutationRoot = Join-Path $outputRoot $mutation.Id
        $baselineDir = Join-Path $mutationRoot 'baseline'
        $mutantDir = Join-Path $mutationRoot 'mutant'
        $restoredDir = Join-Path $mutationRoot 'restored'
        New-Item -ItemType Directory -Force -Path $baselineDir, $mutantDir, $restoredDir | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $mutationRoot 'source-original.cs'), $sourceBytes)
        [IO.File]::WriteAllText((Join-Path $mutationRoot 'mutation.patch'), ("--- a/MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`n+++ b/MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`n@@ " + $mutation.Id + " @@`n- " + $mutantLabel + "`n"), $utf8NoBom)

        $baselineBuildExit = Invoke-Build $baselineDir
        if ($baselineBuildExit -ne 0) { throw "Baseline Rebuild failed for $($mutation.Id): $baselineBuildExit" }
        $baselineTrx = "$($mutation.Id)-baseline.trx"
        $baselinePath = Join-Path $mutationRoot "baseline/$baselineTrx"
        $baselineExit = Invoke-TargetTest $baselineDir $mutation.TestName $baselineTrx
        $baselineTarget = Get-TargetResult $baselinePath $mutation.TestName
        if ($baselineExit -ne 0 -or $baselineTarget.GetAttribute('outcome') -ne 'Passed') {
            throw "Baseline target did not pass for $($mutation.Id)."
        }

        $mutantBuildExit = -1
        $mutantExit = -1
        $mutantTrx = "$($mutation.Id)-mutant.trx"
        $mutantPath = Join-Path $mutationRoot "mutant/$mutantTrx"
        try {
            [IO.File]::WriteAllBytes($sourcePath, $mutantBytes)
            if ((Get-Sha256 ([IO.File]::ReadAllBytes($sourcePath))) -ne $mutantSha) { throw "Mutant bytes on disk differ for $($mutation.Id)." }
            $mutantBuildExit = Invoke-Build $mutantDir
            if ($mutantBuildExit -ne 0) { throw "Mutant Rebuild failed for $($mutation.Id): $mutantBuildExit" }
            $mutantExit = Invoke-TargetTest $mutantDir $mutation.TestName $mutantTrx
        }
        finally {
            [IO.File]::WriteAllBytes($sourcePath, $sourceBytes)
        }

        $restoredSha = Get-Sha256 ([IO.File]::ReadAllBytes($sourcePath))
        if ($restoredSha -ne $originalSha) { throw "Exact source restoration failed for $($mutation.Id)." }
        $mutantTarget = Get-TargetResult $mutantPath $mutation.TestName
        if ($mutantExit -eq 0 -or $mutantTarget.GetAttribute('outcome') -ne 'Failed') {
            throw "Mutant target did not fail for $($mutation.Id)."
        }
        $errorInfo = $mutantTarget.SelectSingleNode("*[local-name()='Output']/*[local-name()='ErrorInfo']")
        $message = if ($null -eq $errorInfo) { '' } else { $errorInfo.SelectSingleNode("*[local-name()='Message']").InnerText }
        $stack = if ($null -eq $errorInfo) { '' } else { $errorInfo.SelectSingleNode("*[local-name()='StackTrace']").InnerText }
        $assertion = [regex]::Match($stack, 'WorkflowRunnerTests\.cs:line \d+').Value
        if (-not $message.Contains($failMarker) -or -not $assertion -or -not $stack.Contains($mutation.TestName)) {
            throw "Mutant failure did not come from the intended test assertion for $($mutation.Id)."
        }

        $restoredBuildExit = Invoke-Build $restoredDir
        if ($restoredBuildExit -ne 0) { throw "Restored Rebuild failed for $($mutation.Id): $restoredBuildExit" }
        $restoredTrx = "$($mutation.Id)-restored.trx"
        $restoredPath = Join-Path $mutationRoot "restored/$restoredTrx"
        $restoredExit = Invoke-TargetTest $restoredDir $mutation.TestName $restoredTrx
        $restoredTarget = Get-TargetResult $restoredPath $mutation.TestName
        if ($restoredExit -ne 0 -or $restoredTarget.GetAttribute('outcome') -ne 'Passed') {
            throw "Restored target did not pass for $($mutation.Id)."
        }

        $record = [ordered]@{
            id = $mutation.Id
            description = $mutation.Purpose
            source = 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'
            test_source = 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs'
            test_source_sha256 = $testSourceSha
            mutation_label = $mutantLabel
            original_sha256 = $originalSha
            mutant_sha256 = $mutantSha
            restored_sha256 = $restoredSha
            target_test_id = $baselineTarget.GetAttribute('testId')
            target_name = $mutation.TestName
            failure_contains = $failMarker
            assertion_contains = $assertion
            assertion_message = $message
            baseline_trx = $baselinePath.Replace('\','/')
            mutant_trx = $mutantPath.Replace('\','/')
            restored_trx = $restoredPath.Replace('\','/')
            baseline_build_log = (Join-Path $mutationRoot 'baseline/build.log').Replace('\','/')
            mutant_build_log = (Join-Path $mutationRoot 'mutant/build.log').Replace('\','/')
            restored_build_log = (Join-Path $mutationRoot 'restored/build.log').Replace('\','/')
            build_log = (Join-Path $mutationRoot 'mutant/build.log').Replace('\','/')
            build_exit = $mutantBuildExit
            baseline_build_exit = $baselineBuildExit
            restored_build_exit = $restoredBuildExit
            baseline_exit = $baselineExit
            mutant_exit = $mutantExit
            restored_exit = $restoredExit
        }
        [void]$records.Add($record)
        Write-Output ("PASS {0} target={1} outcome=P/F/P assertion={2}" -f $mutation.Id, $record.target_test_id, $assertion)
    }
}
finally {
    [IO.File]::WriteAllBytes($sourcePath, $sourceBytes)
    $finalSha = Get-Sha256 ([IO.File]::ReadAllBytes($sourcePath))
    if ($finalSha -ne $originalSha) { throw 'Final mutation runner source restoration failed.' }
}

[IO.File]::WriteAllText((Join-Path $root '_workflow/wave3-bo6bo7-receive/mutation-records.json'), ((ConvertTo-Json -InputObject @($records) -Depth 20) + [Environment]::NewLine), $utf8NoBom)
Write-Output "ALL MUTATIONS COMPLETE original=$originalSha count=$($records.Count)"
