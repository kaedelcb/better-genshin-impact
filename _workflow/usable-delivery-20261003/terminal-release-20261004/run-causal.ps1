param([string]$Only='',[string]$Suffix='')
$ErrorActionPreference='Stop'
$proofRoot=$PWD.Path
$proofBase=Join-Path $proofRoot '_workflow/usable-delivery-20261003/terminal-release-20261004'
$proofCases=@(
    @{id='body-exit';file='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs';needle='!s.ExecutionExitConfirmed || ';replacement='';filter='FullyQualifiedName~RawTerminalWithoutExit_DoesNotReleaseNode';assertion='Assert.Null() Failure'},
    @{id='run-immutable';file='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs';range=$true;filter='FullyQualifiedName~RunSeal_RejectsFactChangesAndRemainsSameAfterReopen&DisplayName~state';assertion='Assert.Throws() Failure'},
    @{id='node-epoch';file='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs';needle=' && op.TargetEpoch == sub.Epoch';replacement='';filter='FullyQualifiedName~NodeSeal_CannotReleaseDifferentOriginalOperation&DisplayName~epoch';assertion='Assert.False() Failure'},
    @{id='history-wire';file='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs';needle='        hasFrozen |= current.SubmissionHistory.Any(s => s.SendAttempted || !string.IsNullOrEmpty(s.JobId));';replacement='';filter='FullyQualifiedName~ArchivedOriginalWireCannotChangeBeforeSuccessorPreparation';assertion='Assert.Throws() Failure'},
    @{id='typed-effect';file='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs';needle=' && effect == "not_executed"';replacement='';filter='FullyQualifiedName~TypedRejectionMismatchKeepsOriginalResponsibility&DisplayName~effect';assertion='Assert.Null() Failure'},
    @{id='stop-node';file='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs';needle=' && !(outcomes.Count == 0 && stoppedAndSettled)';replacement='';filter='FullyQualifiedName~StoppedNodeWithoutOrdinaryOutcome_RequiresOriginalExit&DisplayName~True';assertion='Assert.NotNull() Failure'}
)
foreach($proofCase in @($proofCases | Where-Object {$Only -eq '' -or $_.id -eq $Only})){
    $proofCaseId=$proofCase.id+$Suffix
    $proofSubject=Join-Path $proofRoot $proofCase.file
    $proofBytes=[IO.File]::ReadAllBytes($proofSubject)
    $proofHash=(Get-FileHash -LiteralPath $proofSubject).Hash
    & "$proofBase/run-stage.ps1" -Stage ('causal-'+$proofCaseId+'/baseline') -Filter $proofCase.filter
    $proofBaseline=Get-Content -Raw -Encoding UTF8 "$proofBase/causal-$proofCaseId/baseline/summary.json" | ConvertFrom-Json
    if($proofBaseline.test_exit -ne 0){throw 'causal baseline failed'}
    try {
        $proofText=[Text.Encoding]::UTF8.GetString($proofBytes)
        if($proofCase.range){
            $proofStart=$proofText.IndexOf('        if (current.TerminalRelease is { } runSeal)')
            $proofEnd=$proofText.IndexOf('        foreach (var seal in current.NodeReleaseSeals)',$proofStart)
            if($proofStart -lt 0 -or $proofEnd -lt 0){throw 'causal range missing'}
            $proofNeedle=$proofText.Substring($proofStart,$proofEnd-$proofStart)
            $proofChanged=$proofText.Remove($proofStart,$proofEnd-$proofStart)
        }else{
            $proofNeedle=$proofCase.needle
            if($proofText.Split([string[]]@($proofNeedle),[StringSplitOptions]::None).Count -ne 2){throw 'causal anchor not unique'}
            $proofChanged=$proofText.Replace($proofNeedle,$proofCase.replacement)
        }
        [IO.File]::WriteAllBytes($proofSubject,[Text.Encoding]::UTF8.GetBytes($proofChanged))
        $proofMutantHash=(Get-FileHash -LiteralPath $proofSubject).Hash
        & "$proofBase/run-stage.ps1" -Stage ('causal-'+$proofCaseId+'/negative') -Filter $proofCase.filter
        $proofNegative=Get-Content -Raw -Encoding UTF8 "$proofBase/causal-$proofCaseId/negative/summary.json" | ConvertFrom-Json
        if(@($proofNegative.failed | Where-Object {$_.message.Contains($proofCase.assertion)}).Count -eq 0){throw 'target assertion did not detect mutant'}
    } finally {[IO.File]::WriteAllBytes($proofSubject,$proofBytes)}
    if((Get-FileHash -LiteralPath $proofSubject).Hash -ne $proofHash){throw 'source restoration mismatch'}
    & "$proofBase/run-stage.ps1" -Stage ('causal-'+$proofCaseId+'/restored') -Filter $proofCase.filter
    $proofRestored=Get-Content -Raw -Encoding UTF8 "$proofBase/causal-$proofCaseId/restored/summary.json" | ConvertFrom-Json
    if($proofRestored.test_exit -ne 0){throw 'causal restored failed'}
    [pscustomobject]@{id=$proofCase.id;subject=$proofCase.file;original_sha256=$proofHash;mutant_sha256=$proofMutantHash;restored_sha256=(Get-FileHash -LiteralPath $proofSubject).Hash;removed=$proofNeedle;baseline=$proofBaseline;negative=$proofNegative;restored=$proofRestored;kind='ordinary P/F/P, not authenticated receipt'} | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 "$proofBase/causal-$proofCaseId/record.json"
}
