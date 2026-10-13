param([ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$')][string]$Name='semantic-assistant-source-preview')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOutput = Join-Path $taskRoot 'artifacts/releases'
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskArchive = Join-Path $taskOutput ($Name+'.zip')
$taskManifestPath = Join-Path $taskOutput ($Name+'.manifest.json')
$taskChecksum = Join-Path $taskOutput ($Name+'.sha256')
foreach ($taskPath in @($taskArchive,$taskManifestPath,$taskChecksum)) {
    if (Test-Path -LiteralPath $taskPath) { throw 'Release output already exists. Use a new name to preserve the earlier candidate.' }
}
$taskInventoryPath = Join-Path $taskOutput ($Name+'.dependencies.json')
if (Test-Path -LiteralPath $taskInventoryPath) { throw 'Dependency report output already exists.' }
& (Join-Path $PSScriptRoot 'Get-DependencyReport.ps1') -OutputPath $taskInventoryPath
$taskCurrentInventory = [IO.File]::ReadAllBytes($taskInventoryPath)
$taskSavedInventory = Join-Path $taskRoot 'docs/dependencies.json'
if (-not (Test-Path -LiteralPath $taskSavedInventory)) { throw 'The reviewed dependency inventory is missing.' }
$taskSavedBytes = [IO.File]::ReadAllBytes($taskSavedInventory)
# PS 5.1 and PS 7 indent JSON differently; compare parsed metadata in this runtime.
$taskCurrentCanonical = [Text.Encoding]::UTF8.GetString($taskCurrentInventory) | ConvertFrom-Json | ConvertTo-Json -Depth 12 -Compress
$taskSavedCanonical = [Text.Encoding]::UTF8.GetString($taskSavedBytes) | ConvertFrom-Json | ConvertTo-Json -Depth 12 -Compress
if ($taskCurrentCanonical -cne $taskSavedCanonical) {
    throw 'docs/dependencies.json differs from restored packages. Review and update the checked-in inventory before releasing.'
}
[IO.File]::WriteAllBytes($taskInventoryPath, $taskSavedBytes)
$taskCurrentInventory = $taskSavedBytes
$taskFiles = @(Get-ReleaseSourceFiles $taskRoot)
$taskRecords = [Collections.Generic.List[object]]::new()
Add-Type -AssemblyName System.IO.Compression
$taskStream = [IO.File]::Open($taskArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($taskFile in $taskFiles) {
        Assert-ReleaseRegularFile $taskRoot $taskFile.FullPath
        $taskBytes = [IO.File]::ReadAllBytes($taskFile.FullPath)
        $taskRecords.Add([ordered]@{ Path=$taskFile.Path; Bytes=$taskBytes.Length; Sha256=(Get-ReleaseSha256 $taskBytes) })
        $taskEntry = $taskZip.CreateEntry('semantic-assistant-source/'+$taskFile.Path, [IO.Compression.CompressionLevel]::Optimal)
        $taskEntry.LastWriteTime = [DateTimeOffset]::new(2020,1,1,0,0,0,[TimeSpan]::Zero)
        $taskEntryStream = $taskEntry.Open()
        try { $taskEntryStream.Write($taskBytes,0,$taskBytes.Length) } finally { $taskEntryStream.Dispose() }
    }
} finally { $taskZip.Dispose(); $taskStream.Dispose() }
$taskArchiveHash = Get-ReleaseSha256 ([IO.File]::ReadAllBytes($taskArchive))
$taskManifest = [ordered]@{
    FormatVersion=1; Kind='source-preview'; SourceLicense='MIT'; ContainsThirdPartyBinaries=$false
    SecretContentAudit=$false; PubliclyPublished=$false
    Sdk=(Get-Content -LiteralPath (Join-Path $taskRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    DependencyInventorySha256=(Get-ReleaseSha256 $taskCurrentInventory)
    ArchiveSha256=$taskArchiveHash; FileCount=$taskRecords.Count; Files=@($taskRecords.ToArray())
}
Write-ReleaseUtf8 $taskManifestPath ($taskManifest | ConvertTo-Json -Depth 8)
Write-ReleaseUtf8 $taskChecksum ($taskArchiveHash+'  '+$Name+'.zip')
Write-Output ('source-release='+$Name+';files='+$taskRecords.Count+';sha256='+$taskArchiveHash+';published=false')
