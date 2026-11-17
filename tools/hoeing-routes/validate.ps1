# 集中验证本次线路整包替换；仅由 validate_command.py 分配隔离输出。
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $Artifacts)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($Artifacts)
$expectedParent = [IO.Path]::GetFullPath((Join-Path $root 'Build/validation-runs')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $output -PathType Container)) {
    throw '必须使用 validate_command.py 创建的隔离 Artifacts 目录。'
}
Set-Location -LiteralPath $root
$env:TEMP = Join-Path $output 'tmp'
$env:TMP = $env:TEMP
[IO.Directory]::CreateDirectory($env:TEMP) | Out-Null
$env:BGI_ROUTE_TEST_ROOT = Join-Path $output 'route-fixtures'
[IO.Directory]::CreateDirectory($env:BGI_ROUTE_TEST_ROOT) | Out-Null
$results = [Collections.Generic.List[object]]::new()

# 精确关联：服务端准入/鉴权/版本库与既有房间协议不可回退；BGI 构建包含真实宿主和助手。
# TRX、bin、obj、WPF 临时输出全部留在本次受计量目录，不覆盖旧 TestResults。
& dotnet test BgiCoordinatorServer.Tests/BgiCoordinatorServer.Tests.csproj `
    --artifacts-path (Join-Path $output 'server') --disable-build-servers `
    -p:DeployToBgiTools=false -p:UseSharedCompilation=false `
    --results-directory (Join-Path $output 'results/server') `
    --logger 'trx;LogFileName=server-routes.trx'
$results.Add(@{ phase = 'server'; exitCode = $LASTEXITCODE })

$filter = 'FullyQualifiedName~AutoHoeingRoute|FullyQualifiedName~WorldPlanMemberNegotiationTests|FullyQualifiedName~WorldPlanStartBoundaryTests|FullyQualifiedName~WorldPlanPreviewTests|FullyQualifiedName~HoeingMissingResource|FullyQualifiedName~RouteDirectoryScanner|FullyQualifiedName~RoutePackage'
& dotnet test Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj `
    --artifacts-path (Join-Path $output 'client') --disable-build-servers `
    -p:DeployToBgiTools=false -p:UseSharedCompilation=false -p:Platform=x64 `
    --filter $filter --results-directory (Join-Path $output 'results/client') `
    --logger 'trx;LogFileName=client-routes.trx'
$results.Add(@{ phase = 'client'; exitCode = $LASTEXITCODE })

$webTests = @(Get-ChildItem -LiteralPath (Join-Path $root 'BgiCoordinatorServer.Tests/Web') -Filter '*route*test*.mjs' -File | Sort-Object Name | ForEach-Object FullName)
if ($webTests.Count -eq 0) {
    Write-Output 'FAIL: 未找到线路网页定向测试。'
    $results.Add(@{ phase = 'web'; exitCode = 1 })
} else {
    & node --test @webTests
    $results.Add(@{ phase = 'web'; exitCode = $LASTEXITCODE })
}

$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'route-validation-results.json') -Encoding utf8NoBOM
Write-Output 'ROUTE_VALIDATION_RESULTS'
$results | ConvertTo-Json -Depth 5 | Write-Output
if (@($results | Where-Object { $_.exitCode -ne 0 }).Count -gt 0) { exit 1 }
exit 0
