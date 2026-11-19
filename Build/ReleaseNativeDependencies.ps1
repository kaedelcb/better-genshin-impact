[CmdletBinding(DefaultParameterSetName = 'Stage')]
param(
    [Parameter(Mandatory)] [string] $DistributionDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Stage')] [string] $ProjectAssets,
    [Parameter(Mandatory, ParameterSetName = 'Verify')] [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'

# Publish must keep these native files beside the app or at the package's expected relative path.
# Resolve versions and cache roots from this build's NuGet restore.
$dependencies = @{
    'Microsoft.ML.OnnxRuntime.DirectML' = @{
        'onnxruntime.dll' = 'runtimes/win-x64/native/onnxruntime.dll'
    }
    'Microsoft.AI.DirectML' = @{
        'DirectML.dll' = 'bin/x64-win/DirectML.dll'
        'DirectML.Debug.dll' = 'bin/x64-win/DirectML.Debug.dll'
    }
    'Melanchall.DryWetMidi' = @{
        'Melanchall_DryWetMidi_Native64.dll' = 'build/Melanchall_DryWetMidi_Native64.dll'
    }
    'Microsoft.Diagnostics.Tracing.TraceEvent' = @{
        'amd64/KernelTraceControl.dll' = 'build/native/amd64/KernelTraceControl.dll'
        'amd64/msdia140.dll' = 'build/native/amd64/msdia140.dll'
    }
    'Microsoft.Web.WebView2' = @{
        'runtimes/win-x64/native/WebView2Loader.dll' = 'runtimes/win-x64/native/WebView2Loader.dll'
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Stage') {
    $assets = Get-Content -LiteralPath $ProjectAssets -Raw | ConvertFrom-Json -AsHashtable
    $windowsTargets = @($assets.targets.Keys | Where-Object { $_ -like '*/win-x64' })
    if ($windowsTargets.Count -ne 1) {
        throw "Expected one win-x64 target in $ProjectAssets. Found $($windowsTargets.Count)."
    }
    $target = $assets.targets[$windowsTargets[0]]
    foreach ($package in $dependencies.GetEnumerator()) {
        $packageKeys = @($target.Keys | Where-Object { $_ -like "$($package.Key)/*" })
        if ($packageKeys.Count -ne 1) {
            throw "Expected one resolved package for $($package.Key) in $ProjectAssets. Found $($packageKeys.Count)."
        }
        $packageKey = $packageKeys[0]
        $library = $assets.libraries[$packageKey]
        if ($library.type -ne 'package' -or -not $library.path) {
            throw "Missing NuGet library path for $packageKey in $ProjectAssets."
        }
        foreach ($entry in $package.Value.GetEnumerator()) {
            $relativeSource = Join-Path $library.path $entry.Value
            $source = $null
            foreach ($folder in $assets.packageFolders.Keys) {
                $candidate = Get-Item -LiteralPath (Join-Path $folder $relativeSource) -ErrorAction SilentlyContinue
                if ($candidate -is [System.IO.FileInfo] -and $candidate.Length -gt 0) {
                    $source = $candidate
                    break
                }
            }
            if ($null -eq $source) {
                throw "Missing native dependency $relativeSource from NuGet package $packageKey. Restore the package before publishing."
            }
            $destination = Join-Path $DistributionDirectory $entry.Key
            [void] [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
            Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
            Write-Host "Staged $($entry.Key) from $packageKey ($($source.Length) bytes)."
        }
    }
    return
}

$listing = & 7z l -slt -ba $ArchivePath
if ($LASTEXITCODE -ne 0) {
    throw "Could not list $ArchivePath (7z exit code $LASTEXITCODE)."
}
$archiveSizes = @{}
$currentPath = $null
foreach ($line in $listing) {
    if ($line -match '^Path = (.+)$') {
        $currentPath = $Matches[1].Replace('\', '/')
    } elseif ($null -ne $currentPath -and $line -match '^Size = ([0-9]+)$') {
        $archiveSizes[$currentPath] = [long] $Matches[1]
        $currentPath = $null
    }
}
foreach ($files in $dependencies.Values) {
    foreach ($relativePath in $files.Keys) {
        $file = Get-Item -LiteralPath (Join-Path $DistributionDirectory $relativePath)
        $archivedPath = "BetterGI/$relativePath"
        if (-not $archiveSizes.ContainsKey($archivedPath) -or $file.Length -le 0 -or $archiveSizes[$archivedPath] -ne $file.Length) {
            throw "Archive $ArchivePath is missing or has an incorrect size for $archivedPath."
        }
    }
}
Write-Host "Verified all required native dependencies in $ArchivePath."
