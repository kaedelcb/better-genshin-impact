$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$sourcePath = Join-Path $root 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'
$project = 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'
$outDir = Join-Path $root '_workflow/wave3-bo8-bo9/red-final'
function Get-Sha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([Convert]::ToHexString($sha.ComputeHash($Bytes))).ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$fixedBytes = [IO.File]::ReadAllBytes($sourcePath)
if ((Get-Sha256 $fixedBytes) -ne '539dfe39e281305b4b8181def55e7d5f1d5ee9f27035137de0f587f3f3a82087') { throw 'Unexpected fixed source before swap.' }
$prefixBytes = [IO.File]::ReadAllBytes((Join-Path $outDir 'prefix-source.cs'))
if ((Get-Sha256 $prefixBytes) -ne '5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96') { throw 'Unexpected prefix source bytes.' }
$filter = 'FullyQualifiedName~RevisionReload_CompletedIdentityReorderedAfterResumePoint_IsNotResubmitted|FullyQualifiedName~Resume_MultipleLiveParksAcrossRoundsInLooplessPlan_DrivesEachLiveParkOnceInPlanOrder|FullyQualifiedName~Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess'
$exits = [ordered]@{}
try {
    [IO.File]::WriteAllBytes($sourcePath, $prefixBytes)
    & dotnet build $project -t:Rebuild -p:DeployToBgiTools=false *> (Join-Path $outDir 'testproject-build.log')
    $exits['prefix_build_exit'] = $LASTEXITCODE
    if ($exits['prefix_build_exit'] -ne 0) { throw "Prefix Rebuild failed: $($exits['prefix_build_exit'])" }
    & dotnet test $project -p:DeployToBgiTools=false --no-build --filter $filter --logger "trx;LogFileName=bo8-bo9-red-final.trx" --results-directory $outDir *> (Join-Path $outDir 'bo8-bo9-red-final.log')
    $exits['prefix_test_exit'] = $LASTEXITCODE
}
finally {
    [IO.File]::WriteAllBytes($sourcePath, $fixedBytes)
}
$restored = Get-Sha256 ([IO.File]::ReadAllBytes($sourcePath))
if ($restored -ne '539dfe39e281305b4b8181def55e7d5f1d5ee9f27035137de0f587f3f3a82087') { throw 'Fixed source restoration failed.' }
[IO.File]::WriteAllText((Join-Path $outDir 'red-final-exits.json'), ((ConvertTo-Json -InputObject $exits) + [Environment]::NewLine), (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("prefix_build_exit={0} prefix_test_exit={1} restored={2}" -f $exits['prefix_build_exit'], $exits['prefix_test_exit'], $restored)
