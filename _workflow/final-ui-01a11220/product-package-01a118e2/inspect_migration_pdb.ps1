param([Parameter(Mandatory=$true)][string]$ModuleRoot,[Parameter(Mandatory=$true)][string]$SourcePath)
$ErrorActionPreference='Stop'
$dllPath=Join-Path $ModuleRoot 'MultiplayerHoeingAssistant.dll'
$pdbPath=Join-Path $ModuleRoot 'MultiplayerHoeingAssistant.pdb'
$dllStream=[IO.File]::OpenRead($dllPath)
$pdbStream=[IO.File]::OpenRead($pdbPath)
try {
    $peReader=[System.Reflection.PortableExecutable.PEReader]::new($dllStream)
    $pdbReader=[System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pdbStream)
    try {
        $reader=$pdbReader.GetMetadataReader()
        [byte[]]$identifier=$reader.DebugMetadataHeader.Id
        $pdbGuid=[Guid]::new([byte[]]$identifier[0..15])
        $pdbStamp=[BitConverter]::ToUInt32($identifier,16)
        $codeViews=@(foreach($entry in $peReader.ReadDebugDirectory()) {
            if($entry.Type -eq [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) {
                $cv=$peReader.ReadCodeViewDebugDirectoryData($entry)
                [pscustomobject]@{guid=$cv.Guid;stamp=$entry.Stamp;age=$cv.Age;path=$cv.Path;paired=($cv.Guid -eq $pdbGuid -and $entry.Stamp -eq $pdbStamp)}
            }
        })
        if($codeViews.Count -ne 1 -or -not $codeViews[0].paired){throw 'DLL and PDB do not identify the same compiler output'}
        $documents=@(foreach($handle in $reader.Documents) {
            $document=$reader.GetDocument($handle);$name=$reader.GetString($document.Name)
            if($name -eq $SourcePath) {
                [pscustomobject]@{name=$name;algorithm=$reader.GetGuid($document.HashAlgorithm);checksum=[Convert]::ToHexString($reader.GetBlobBytes($document.Hash))}
            }
        })
        if($documents.Count -ne 1){throw 'Linked migration source document is missing or duplicated'}
        $sourceHash=(Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash
        if($documents[0].algorithm -ne [Guid]'8829d00f-11b8-4213-878b-770e8597ac16' -or $documents[0].checksum -ne $sourceHash){throw 'Compiler source checksum differs from current linked migration source'}
        [pscustomobject]@{dll_path=$dllPath;pdb_path=$pdbPath;dll_sha256=(Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash;pdb_sha256=(Get-FileHash -LiteralPath $pdbPath -Algorithm SHA256).Hash;code_views=$codeViews;document=$documents[0];source_sha256=$sourceHash;paired=$true;source_matches=$true;scope='compiler provenance only; no runtime or independent review verdict'}|ConvertTo-Json -Depth 6
    } finally {$peReader.Dispose();$pdbReader.Dispose()}
} finally {$dllStream.Dispose();$pdbStream.Dispose()}
