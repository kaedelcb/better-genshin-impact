# Synthetic admin-summary data for the existing isolated preview launcher.
param([Parameter(Mandatory = $true)][string] $Artifacts, [int] $Port = 8959)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($Artifacts)
$expectedParent = [IO.Path]::GetFullPath((Join-Path $root 'Build/validation-runs')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $output -PathType Container)) { throw '需要独占验证输出目录' }
$logRoot = Join-Path $output 'route-preview-data/room-logs'
$historyRoot = Join-Path $logRoot 'hoeing-history'
[IO.Directory]::CreateDirectory($historyRoot) | Out-Null
$now = [DateTime]::UtcNow
$yesterday = $now.AddHours(8).Date.AddDays(-1).AddHours(-8)
$members = @(@{ uid = '10001'; name = '测试成员甲' }, @{ uid = '10002'; name = '测试成员乙' })
function New-Run($id, [DateTime]$start, [DateTime]$end, $status) {
    $terminal = $status -ne 'unknown'
    @{ run = @{ id = $id; requestedAtUtc = $start.ToString('o'); startedAtUtc = $start.ToString('o'); observedAtUtc = $end.ToString('o'); endedAtUtc = $(if ($terminal) { $end.ToString('o') } else { $null }); status = $status; reason = '合成验证记录'; members = $members }; revision = 2; terminal = $terminal }
}
$fixtures = @(
    @{ code = 'TST001'; runs = @((New-Run 'cross-day' $yesterday.AddHours(22) $yesterday.AddHours(24) 'completed'), (New-Run 'first' $yesterday.AddHours(8) $yesterday.AddHours(15) 'completed'), (New-Run 'second' $yesterday.AddHours(14) $yesterday.AddHours(16) 'stopped')) },
    @{ code = 'TST002'; runs = @((New-Run 'third' $yesterday.AddHours(9) $yesterday.AddHours(14) 'completedWithErrors'), (New-Run 'fourth' $yesterday.AddHours(15) $yesterday.AddHours(17) 'unknown')) },
    @{ code = 'TST003'; runs = @((New-Run 'fifth' $now.AddMinutes(-20) $now.AddMinutes(-5) 'completed')) }
)
foreach ($fixture in $fixtures) {
    $fixture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $historyRoot ($fixture.code + '.json')) -Encoding utf8NoBOM
}
@{ TST001 = '测试长跑队'; TST002 = '测试恢复队'; TST003 = '测试今日队' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logRoot 'assistant-room-notes.json') -Encoding utf8NoBOM
$worldStart = $yesterday.AddHours(9)
$worldEnd = $yesterday.AddHours(11)
@{ ControlCode = 'TST001'; GameCode = 'WLD001'; SessionId = 'summary-synthetic-world'; StartedUtc = $worldStart.ToString('o'); LinkedUtc = $worldStart.ToString('o'); EndedUtc = $worldEnd.ToString('o'); Source = 'persisted'; BatchId = 'first'; HostUid = '10001'; HostName = '测试成员甲' } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $logRoot 'room-links.jsonl') -Encoding utf8NoBOM
$localDay = $worldStart.AddHours(8).ToString('yyyy-MM-dd')
$token = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('summary-synthetic-world'))
@("房间 WLD001 · $localDay 服务器日志（北京时间 UTC+08:00）", "本文件首条记录：$localDay 09:00:00.000 +08:00", '仅包含启用保存后可关联到此房间的日志；按时间从早到晚排列。', '', "[$localDay 09:00:00.000 +08:00] [Information] Synthetic [room-log:v2;event=0123456789abcdef0123456789abcdef;session=$token]", '合成世界房间日志，仅用于本地测试。', '') | Set-Content -LiteralPath (Join-Path $logRoot "$localDay-WLD001.log") -Encoding utf8
& (Join-Path $root 'tools/hoeing-routes/preview-launch.ps1') -Artifacts $output -Port $Port
exit $LASTEXITCODE
