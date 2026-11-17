param([Parameter(Mandatory=$true)][string]$Artifacts, [switch]$ReuseVerifiedCore, [switch]$RemainingOnly)
$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
$env:ROOM_LOG_ROOT = Join-Path $Artifacts 'room-logs'
function Invoke-Test([string]$Project, [string]$Name, [string]$Filter = '', [switch]$NoBuild) {
    $env:NEXUSBGI_DATA_ROOT = Join-Path $Artifacts ($Name + '-data')
    $arguments = @('test', $Project, '--artifacts-path', $Artifacts, '-p:DeployToBgiTools=false', '--logger', "trx;LogFileName=$Name.trx", '--results-directory', (Join-Path $Artifacts 'TestResults'))
    if ($Filter) { $arguments += @('--filter', $Filter) }
    if ($NoBuild) { $arguments += '--no-build' }
    $hangBudget = if ($Name -eq 'four-member-entry') { '330s' } elseif ($Name -eq 'native-entry') { '210s' } else { '60s' }
    $arguments += @('--logger', 'console;verbosity=normal', '--blame-hang', '--blame-hang-timeout', $hangBudget, '--blame-hang-dump-type', 'none')
    & dotnet @arguments
    if ($LASTEXITCODE) { $failures.Add("${Name}: exit $LASTEXITCODE") }
    Write-Output "RECOVERY_TEST_FINISHED $Name exit=$LASTEXITCODE"
}
if ($ReuseVerifiedCore) {
    & dotnet build BgiCoordinatorServer/BgiCoordinatorServer.csproj --artifacts-path $Artifacts -p:DeployToBgiTools=false
    if ($LASTEXITCODE) { throw 'Isolated coordinator build failed.' }
} else { Invoke-Test 'BgiCoordinatorServer.Tests/BgiCoordinatorServer.Tests.csproj' 'server-recovery' }
$env:NEXUSBGI_TEST_COORDINATOR_DLL = Join-Path $Artifacts 'bin/BgiCoordinatorServer/debug/BgiCoordinatorServer.dll'
if (-not (Test-Path $env:NEXUSBGI_TEST_COORDINATOR_DLL)) { throw 'Isolated server output missing.' }
$unit = 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'
if ($ReuseVerifiedCore) {
    & dotnet build $unit --artifacts-path $Artifacts -p:DeployToBgiTools=false
    if ($LASTEXITCODE) { throw 'Isolated assistant tests build failed.' }
} else {
    Invoke-Test $unit 'assistant-recovery' 'FullyQualifiedName~CompletionStartExecutionTests|FullyQualifiedName~CompletionStateContinuationTests|FullyQualifiedName~Coordinated|FullyQualifiedName~UnifiedHoeingBatchLoopTests|FullyQualifiedName~ManualResetCleanupRecoveryTests|FullyQualifiedName~OnlineIntentLifecycleResetTests|FullyQualifiedName~CommandExecutorManualResetTests|FullyQualifiedName~CommandExecutorTakeoverResetTests|FullyQualifiedName~BatchReconcileDeciderTests'
}
Invoke-Test $unit 'native-entry' 'FullyQualifiedName~UnifiedHoeingNativeEntryTests' -NoBuild
Invoke-Test $unit 'four-member-entry' 'FullyQualifiedName~FourMemberFinishEntryTests' -NoBuild
if ($RemainingOnly) {
    Invoke-Test $unit 'completion-regression' 'FullyQualifiedName~CompletionStartExecutionTests|FullyQualifiedName~CompletionStateContinuationTests|FullyQualifiedName~Coordinated|FullyQualifiedName~UnifiedHoeingBatchLoopTests|FullyQualifiedName~ManualResetCleanupRecoveryTests|FullyQualifiedName~OnlineIntentLifecycleResetTests|FullyQualifiedName~CommandExecutorManualResetTests|FullyQualifiedName~CommandExecutorTakeoverResetTests|FullyQualifiedName~BatchReconcileDeciderTests' -NoBuild
} else {
    Invoke-Test $unit 'reset-button' 'FullyQualifiedName~StateResetButtonEntryTests' -NoBuild
}
if (-not $RemainingOnly) {
    $integration = 'Test/MultiplayerHoeingAssistant.LogRoom.IntegrationTest/MultiplayerHoeingAssistant.LogRoom.IntegrationTest.csproj'
    Invoke-Test $integration 'reset-gateway' 'FullyQualifiedName~ResetStateGatewayTests'
    Invoke-Test $integration 'reset-refresh' 'FullyQualifiedName~ResetRoomRefreshEntryTests' -NoBuild
}
& pwsh -NoProfile -STA -File Build/reset-recovery-20261117/app-smoke.ps1 -Executable (Join-Path $Artifacts 'bin/MultiplayerHoeingAssistant/debug/MultiplayerHoeingAssistant.exe') -DataRoot (Join-Path $Artifacts 'app-smoke-data')
if ($LASTEXITCODE) { $failures.Add("app-smoke: exit $LASTEXITCODE") }
if ($failures.Count) { throw ('Recovery validation failures: ' + ($failures -join '; ')) }
Write-Output 'RECOVERY_VALIDATION_COMPLETE'
