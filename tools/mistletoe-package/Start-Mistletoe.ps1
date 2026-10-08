param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'

function Stop-Launch([string]$Reason) {
    if ($CheckOnly) { throw $Reason }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($Reason, '启动槲寄生', 'OK', 'Information') | Out-Null
    exit 1
}

try {
    $packageDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
    $bgiExecutable = Join-Path $packageDirectory 'BetterGI.exe'
    $assistantExecutable = Join-Path $packageDirectory 'Tools\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.exe'
    $markerPath = Join-Path $packageDirectory 'mistletoe-package.json'
    if (!(Test-Path -LiteralPath $bgiExecutable -PathType Leaf) -or
        !(Test-Path -LiteralPath $assistantExecutable -PathType Leaf)) {
        Stop-Launch '软件包不完整，请使用包含 BetterGI 和 Tools 文件夹的完整目录。'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.schema -ne 'mistletoe.local-package' -or $marker.schemaVersion -ne 1) {
        Stop-Launch '软件包的数据绑定无法识别，原数据未改动。'
    }
    if ([string]$marker.userRoot -notmatch '^[A-Za-z]:[\\/]') {
        Stop-Launch '软件包没有明确的本机旧数据目录，原数据未改动。'
    }
    $userDirectory = [System.IO.Path]::GetFullPath([string]$marker.userRoot).TrimEnd([char[]]@(47,92))
    if (!(Test-Path -LiteralPath $userDirectory -PathType Container)) {
        Stop-Launch "原数据目录不存在：$userDirectory`n请恢复原目录后再打开本软件包。"
    }
    $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $running = @(Get-CimInstance Win32_Process | Where-Object { $_.SessionId -eq $sessionId })
    if (@($running | Where-Object Name -EQ 'MultiplayerHoeingAssistant.exe').Count -gt 0) {
        Stop-Launch '旧槲寄生助手仍在运行。请从旧助手界面或托盘菜单正常退出，再双击此入口。已有计划与配置会保留。'
    }
    foreach ($process in @($running | Where-Object Name -EQ 'BetterGI.exe')) {
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
            Stop-Launch '无法确认正在运行的 BetterGI 的数据目录。请正常退出旧 BetterGI 后再次打开。'
        }
        $otherDirectory = [System.IO.Path]::GetDirectoryName($process.ExecutablePath)
        $otherMarkerPath = Join-Path $otherDirectory 'mistletoe-package.json'
        $otherUser = Join-Path $otherDirectory 'User'
        if (Test-Path -LiteralPath $otherMarkerPath -PathType Leaf) {
            $otherMarker = Get-Content -LiteralPath $otherMarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($otherMarker.userRoot) { $otherUser = [string]$otherMarker.userRoot }
        }
        if ([System.IO.Path]::GetFullPath($otherUser).TrimEnd([char[]]@(47,92)) -eq $userDirectory) {
            Stop-Launch '旧 BetterGI 正在使用同一份数据。请从其界面正常退出，再双击此入口；本入口不会关闭程序或覆盖原数据。'
        }
    }
    if ($CheckOnly) {
        [pscustomobject]@{ Ready = $true; Package = $packageDirectory; UserRoot = $userDirectory }
        exit 0
    }
    # Normal user startup uses the existing per-user assistant store.
    [Environment]::SetEnvironmentVariable('NEXUSBGI_DATA_ROOT', $null, 'Process')
    Start-Process -FilePath $bgiExecutable -WorkingDirectory $packageDirectory
    Start-Process -FilePath $assistantExecutable -WorkingDirectory ([System.IO.Path]::GetDirectoryName($assistantExecutable)) -ArgumentList '--no-auto-launch'
} catch {
    Stop-Launch ("启动未完成：" + $_.Exception.Message + "`n原数据未移动或覆盖。")
}
