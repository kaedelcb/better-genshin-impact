param([Parameter(Mandatory=$true)][string]$Stage,[string]$Filter='',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
$stepRoot=$PWD.Path
$stepOut=Join-Path $stepRoot ('_workflow/usable-delivery-20261003/terminal-release-20261004/'+$Stage)
$stepProducts=Join-Path $stepRoot '_workflow/usable-delivery-20261003/adapter-exit-20261004/products'
New-Item -ItemType Directory -Force $stepOut | Out-Null
$stepPaths=@(rg --files MultiplayerHoeingAssistant Test/MultiplayerHoeingAssistant.UnitTest Test/R56ControlledWriterProbe -g '*.cs' -g '*.csproj' -g '*.xaml' -g '!bin/**' -g '!obj/**' | Where-Object {$_ -notmatch '[/\\](bin|obj)[/\\]'})
$stepInputs=@($stepPaths | ForEach-Object {[pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash}})
$stepInputs | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 "$stepOut/source-inputs.json"
if(!$SkipBuild){
    & dotnet build Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj -t:Rebuild -c Debug -p:Platform=x64 -p:DeployToBgiTools=false -o $stepProducts --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false -maxcpucount:1 > "$stepOut/build.log" 2>&1
    $stepBuildExit=$LASTEXITCODE
    $stepBuildExit | Set-Content "$stepOut/build.exit"
    if($stepBuildExit -ne 0){Get-Content "$stepOut/build.log" -Tail 12; throw 'stage build failed'}
}
$stepArgs=@("$stepProducts/MultiplayerHoeingAssistant.UnitTest.dll",'/Logger:trx;LogFileName=results.trx',"/ResultsDirectory:$stepOut")
if($Filter -ne ''){$stepArgs+="/TestCaseFilter:$Filter"}
& dotnet vstest @stepArgs > "$stepOut/test.log" 2>&1
$stepTestExit=$LASTEXITCODE
$stepTestExit | Set-Content "$stepOut/test.exit"
$stepChanged=@($stepInputs | Where-Object {(Get-FileHash -LiteralPath $_.path).Hash -ne $_.sha256})
$stepXml=[xml]([IO.File]::ReadAllText("$stepOut/results.trx",[Text.Encoding]::UTF8))
$stepRows=@($stepXml.TestRun.Results.UnitTestResult)
$stepSummary=[pscustomobject]@{kind='ordinary controlled observations, not execution receipt';stage=$Stage;test_exit=$stepTestExit;inputs=$stepInputs.Count;changed_inputs=$stepChanged;counts=@($stepRows | Group-Object outcome | Select-Object Name,Count);failed=@($stepRows | Where-Object {$_.outcome -eq 'Failed'} | ForEach-Object {[pscustomobject]@{id=$_.testId;name=$_.testName;message=$_.Output.ErrorInfo.Message}})}
$stepSummary | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 "$stepOut/summary.json"
$stepSummary | ConvertTo-Json -Depth 6
