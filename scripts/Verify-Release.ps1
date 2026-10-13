param([switch]$BuildExtracted)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskCases = @(
    @{Path='LICENSE';Allowed=$true}, @{Path='src/Module/File.cs';Allowed=$true},
    @{Path='.gitattributes';Allowed=$true},
    @{Path='examples/synthetic-model/definition/tables/Sales.tmdl';Allowed=$true},
    @{Path='docs/dependencies.json';Allowed=$true},
    @{Path='../outside.cs';Allowed=$false}, @{Path='src/../outside.cs';Allowed=$false},
    @{Path='C:/private/file.cs';Allowed=$false}, @{Path='src\file.cs';Allowed=$false},
    @{Path='src/bin/tool.dll';Allowed=$false}, @{Path='src/obj/cache.json';Allowed=$false},
    @{Path='.env';Allowed=$false}, @{Path='docs/.env.example';Allowed=$false},
    @{Path='examples/customer.pbix';Allowed=$false}, @{Path='src/key.pem';Allowed=$false},
    @{Path='artifacts/report.json';Allowed=$false}, @{Path='docs/payload.exe';Allowed=$false},
    @{Path='unknown.txt';Allowed=$false}, @{Path='docs/./readme.md';Allowed=$false}
    @{Path='xgithub/unreviewed.yml';Allowed=$false}
)
foreach ($taskCase in $taskCases) {
    $taskAllowed=$true
    try { Assert-SourceRelativePath $taskCase.Path } catch { $taskAllowed=$false }
    if ($taskAllowed -ne $taskCase.Allowed) { throw 'Source allowlist regression.' }
}
$taskRun = [Guid]::NewGuid().ToString('N')
$taskNames = @(('verify-source-a-'+$taskRun), ('verify-source-b-'+$taskRun))
foreach ($taskName in $taskNames) { & (Join-Path $PSScriptRoot 'New-SourceRelease.ps1') -Name $taskName }
$taskReleaseRoot = Join-Path $taskRoot 'artifacts/releases'
$taskFirst = Join-Path $taskReleaseRoot ($taskNames[0]+'.zip')
$taskSecond = Join-Path $taskReleaseRoot ($taskNames[1]+'.zip')
if ((Get-ReleaseSha256 ([IO.File]::ReadAllBytes($taskFirst))) -cne (Get-ReleaseSha256 ([IO.File]::ReadAllBytes($taskSecond)))) { throw 'Repeated source archives differ.' }
$taskManifest = Get-Content -LiteralPath (Join-Path $taskReleaseRoot ($taskNames[0]+'.manifest.json')) -Raw | ConvertFrom-Json
$taskExpected = @{}
foreach ($taskRecord in $taskManifest.Files) { $taskExpected['semantic-assistant-source/'+$taskRecord.Path]=$taskRecord }
$taskStream = [IO.File]::OpenRead($taskFirst)
$taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Read, $false)
try {
    if ($taskZip.Entries.Count -ne $taskManifest.FileCount) { throw 'Archive entry count differs.' }
    foreach ($taskEntry in $taskZip.Entries) {
        if (-not $taskExpected.ContainsKey($taskEntry.FullName)) { throw 'Unexpected archive entry.' }
        Assert-SourceRelativePath $taskEntry.FullName.Substring('semantic-assistant-source/'.Length)
        $taskRecord = $taskExpected[$taskEntry.FullName]
        $taskInput = $taskEntry.Open()
        $taskMemory = [IO.MemoryStream]::new()
        try { $taskInput.CopyTo($taskMemory); $taskBytes=$taskMemory.ToArray() }
        finally { $taskInput.Dispose(); $taskMemory.Dispose() }
        if ($taskBytes.Length -ne $taskRecord.Bytes -or (Get-ReleaseSha256 $taskBytes) -cne $taskRecord.Sha256) { throw 'Source manifest mismatch.' }
        $taskExpected.Remove($taskEntry.FullName)
    }
} finally { $taskZip.Dispose(); $taskStream.Dispose() }
if ($taskExpected.Count -ne 0) { throw 'Manifest contains missing entries.' }
$taskOverwriteRejected=$false
try { & (Join-Path $PSScriptRoot 'New-SourceRelease.ps1') -Name $taskNames[0] } catch { $taskOverwriteRejected=$true }
if (-not $taskOverwriteRejected) { throw 'A release candidate was overwritten.' }
if ($BuildExtracted) {
    $taskExtractRoot = Join-Path $taskRoot ('artifacts/source-rebuild-'+$taskRun)
    if (Test-Path -LiteralPath $taskExtractRoot) { throw 'The isolated source rebuild directory already exists.' }
    Expand-Archive -LiteralPath $taskFirst -DestinationPath $taskExtractRoot
    $taskExtractedSource = Join-Path $taskExtractRoot 'semantic-assistant-source'
    $taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools/dotnet10/dotnet.exe'
    if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk=(Get-Command dotnet -ErrorAction Stop).Source }
    $taskOldPath = $env:PATH
    try {
        $env:PATH=[IO.Path]::GetDirectoryName($taskSdk)+[IO.Path]::PathSeparator+$taskOldPath
        & (Join-Path $taskExtractedSource 'scripts/Verify.ps1') *> (Join-Path $taskExtractRoot 'verification.log')
        if (-not $?) { throw 'Extracted source build and synthetic verification failed.' }
    } finally { $env:PATH=$taskOldPath }
    Write-Output 'extracted-source;locked-restore-build-tests-wpf=passed;synthetic-only=true'
}
Write-Output ('release-verified;path-cases='+$taskCases.Count+';two-archives=identical;manifest=all-entries;overwrite=rejected;binary-inclusion=false;synthetic-only=true')
