[CmdletBinding()]
param(
    [string[]]$ChangedPath = @(),
    [string[]]$Term = @(),
    [string]$IndexPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($IndexPath)) {
    $IndexPath = Join-Path $PSScriptRoot "..\..\Docs\design\mistletoe-source-history-index-20261009.json"
}

if (-not (Test-Path -LiteralPath $IndexPath)) {
    throw "历史索引不存在：$IndexPath"
}

$index = Get-Content -LiteralPath $IndexPath -Raw | ConvertFrom-Json
$paths = @($ChangedPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Replace('\', '/') })
$terms = @($Term | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$pathTriggers = @($index.queryPolicy.triggerPaths)
$termTriggers = @($index.queryPolicy.triggerTerms)
$pathHit = @($paths | Where-Object { $candidate = $_; @($pathTriggers | Where-Object { $candidate -like $_ }).Count -gt 0 })
$termHit = @($terms | Where-Object { $termTriggers -contains $_ })
$triggered = $pathHit.Count -gt 0 -or $termHit.Count -gt 0

$matches = @()
if ($triggered) {
    foreach ($entry in @($index.fallbackEntries)) {
        $pathMatch = @($paths | Where-Object { $candidate = $_; @($entry.paths | Where-Object { $candidate -like (($_ -replace '\\','/')) }).Count -gt 0 })
        $termMatch = @($terms | Where-Object { $needle = $_; @($entry.terms | Where-Object { $needle -eq $_ -or $needle.Contains($_) -or $_.Contains($needle) }).Count -gt 0 })
        if ($pathMatch.Count -gt 0 -or $termMatch.Count -gt 0) {
            $matches += [ordered]@{ key = $entry.key; paths = @($entry.paths); terms = @($entry.terms); legacyAction = $entry.legacyAction }
        }
    }
}

$result = [ordered]@{
    mode = if ($triggered) { "targeted-history" } else { "current-source-only" }
    changedPath = @($paths)
    terms = @($terms)
    triggerPaths = @($pathHit)
    triggerTerms = @($termHit)
    matches = @($matches)
    legacyRootMode = $index.legacyRootMode
    fullTreeLegacyScan = [bool]$index.queryPolicy.fullTreeLegacyScan
    automaticCopy = $false
    note = if ($triggered) { "只查索引命中的条目；未命中时不得扩大为旧目录整树扫描。" } else { "当前范围不触发历史查询。" }
}
$result | ConvertTo-Json -Depth 8 -Compress