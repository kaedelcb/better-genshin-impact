# 仅由 .claude/launch.json 的隔离预览入口调用；不连接生产或真实 User。
# validate_command.py 分配并计量 Artifacts，退出后自动回收可再生成的构建输出。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Artifacts,
    [ValidateRange(1024, 65535)][int] $Port = 8951
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($Artifacts)
$expectedParent = [IO.Path]::GetFullPath((Join-Path $root 'Build/validation-runs')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $output -PathType Container)) {
    throw '必须使用 validate_command.py 创建的隔离 Artifacts 目录。'
}

$data = Join-Path $output 'route-preview-data'
[IO.Directory]::CreateDirectory($data) | Out-Null
$stopFile = Join-Path $output 'route-preview.stop'
$stateFile = Join-Path $output 'route-preview.json'
if (Test-Path -LiteralPath $stopFile) { throw '隔离预览停止标记已经存在，拒绝复用。' }

# 以下均为公开、固定的本机测试凭据，绝非生产口令；服务器仅绑定 loopback。
$env:ADMIN_MONITOR_PASSWORD = 'isolated-route-preview-admin-20261117'
$env:ROUTE_UPLOAD_TOKEN = 'isolated-route-preview-uploader-20261117'
$env:HOEING_REQUIRED_CLIENT_VERSION = '0.0.0+route-preview'
$env:ROUTE_TRUSTED_PROXY_IPS = ''
$env:ROUTE_PACKAGE_ROOT = Join-Path $data 'packages'
$env:ROUTE_PACKAGE_MAX_BYTES = '67108864'
$env:ROOM_LOG_ROOT = Join-Path $data 'room-logs'
$env:ADMIN_MONITOR_KEY_ROOT = Join-Path $data 'authentication-keys'
$env:BGI_UPDATE_ROOT = Join-Path $data 'bgi-updates-empty'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"

$env:TEMP = Join-Path $output 'tmp'
$env:TMP = $env:TEMP
[IO.Directory]::CreateDirectory($env:TEMP) | Out-Null

# .NET 8 的 dotnet run --artifacts-path 可能构建新输出却运行默认 bin 的旧 DLL。
# 分开构建与运行，显式绑定本次产物身份，禁止回落默认输出。
$buildRoot = Join-Path $output 'server-build'
& dotnet build (Join-Path $root 'BgiCoordinatorServer/BgiCoordinatorServer.csproj') `
    --artifacts-path $buildRoot --disable-build-servers --configuration Debug `
    -p:DeployToBgiTools=false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "隔离服务构建失败，exitCode=$LASTEXITCODE" }
$assembly = Join-Path $buildRoot 'bin/BgiCoordinatorServer/debug/BgiCoordinatorServer.dll'
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw '未找到本次隔离服务 DLL，拒绝启动旧程序。' }
if (Test-Path -LiteralPath $stopFile) { throw '构建期间已请求停止，未启动服务器。' }

$info = [Diagnostics.ProcessStartInfo]::new('dotnet')
$info.WorkingDirectory = Join-Path $root 'BgiCoordinatorServer'
$info.UseShellExecute = $false
@($assembly, '--contentRoot', $info.WorkingDirectory, '--urls', $env:ASPNETCORE_URLS
) | ForEach-Object { $info.ArgumentList.Add($_) }

$process = $null
try {
    $process = [Diagnostics.Process]::Start($info)
    @{
        processId = $process.Id
        url = $env:ASPNETCORE_URLS
        stopFile = $stopFile
        dataRoot = $data
        assembly = $assembly
        state = 'starting'
    } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding utf8NoBOM
    $deadline = [DateTime]::UtcNow.AddMinutes(50)
    while (-not $process.WaitForExit(250)) {
        if (Test-Path -LiteralPath $stopFile) {
            # 仅终止此脚本创建且仍持有 Process 身份的隔离服务器树，不碰用户程序。
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw '隔离预览服务器未确认停止。' }
            @{ state = 'stopped'; stoppedByControl = $true } | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $output 'route-preview-completed.json') -Encoding utf8NoBOM
            exit 0
        }
        if ([DateTime]::UtcNow -ge $deadline) { throw '隔离预览超过 50 分钟，验证未完成。' }
    }
    throw "隔离预览服务器意外退出，exitCode=$($process.ExitCode)；请保留本次失败日志。"
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw '隔离预览服务器清理未确认。' }
        }
        $process.Dispose()
    }
}
