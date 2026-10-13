param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskAssetsPath = Join-Path $taskRoot 'src/SemanticAssistant.Desktop/obj/project.assets.json'
if (-not (Test-Path -LiteralPath $taskAssetsPath)) { throw 'Run locked restore before generating dependencies.' }
$taskAssets = Get-Content -LiteralPath $taskAssetsPath -Raw | ConvertFrom-Json
$taskCaches = @($taskAssets.packageFolders.PSObject.Properties.Name)
$taskSourceFiles = @(Get-ReleaseSourceFiles $taskRoot)
$taskLocks = @($taskSourceFiles | Where-Object { $_.Path.EndsWith('/packages.lock.json') })
$taskRecords = @{}
$taskRuntimeKeys = @{}
$taskFrameworkPruned = @{}
foreach ($taskFrameworkEntry in $taskAssets.project.frameworks.PSObject.Properties) {
    if ($null -ne $taskFrameworkEntry.Value.packagesToPrune) {
        foreach ($taskPruned in $taskFrameworkEntry.Value.packagesToPrune.PSObject.Properties) {
            $taskFrameworkPruned[$taskPruned.Name.ToLowerInvariant()] = $taskPruned.Value
        }
    }
}
Add-Type -AssemblyName System.IO.Compression
foreach ($taskLock in $taskLocks) {
    $taskData = Get-Content -LiteralPath $taskLock.FullPath -Raw | ConvertFrom-Json
    if ($taskData.version -ne 1) { throw 'Unsupported lock version.' }
    foreach ($taskFramework in $taskData.dependencies.PSObject.Properties) {
        foreach ($taskEntry in $taskFramework.Value.PSObject.Properties) {
            $taskValue = $taskEntry.Value
            if ($taskValue.type -eq 'Project') { continue }
            $taskKey = $taskEntry.Name + '/' + $taskValue.resolved
            if (-not $taskRecords.ContainsKey($taskKey)) {
                $taskRecords[$taskKey] = @{ Id=$taskEntry.Name; Version=$taskValue.resolved; ContentHash=$taskValue.contentHash; Uses=[Collections.Generic.List[object]]::new() }
            }
            if ($taskRecords[$taskKey].ContentHash -ne $taskValue.contentHash) { throw 'Conflicting package content hashes.' }
            $taskRecords[$taskKey].Uses.Add([ordered]@{ Lock=$taskLock.Path; Framework=$taskFramework.Name; DependencyType=$taskValue.type })
            if ($taskLock.Path -eq 'src/SemanticAssistant.Desktop/packages.lock.json') { $taskRuntimeKeys[$taskKey]=$true }
        }
    }
}
[string[]]$taskKeys = @($taskRecords.Keys)
[Array]::Sort($taskKeys, [StringComparer]::Ordinal)
$taskPackages = foreach ($taskKey in $taskKeys) {
    $taskRecord = $taskRecords[$taskKey]
    $taskPackageRoot = $null
    foreach ($taskCache in $taskCaches) {
        $taskCandidate = Join-Path $taskCache ($taskRecord.Id.ToLowerInvariant() + '/' + $taskRecord.Version)
        if (Test-Path -LiteralPath $taskCandidate -PathType Container) { $taskPackageRoot=$taskCandidate; break }
    }
    if ($null -eq $taskPackageRoot) { throw 'A locked package is absent from the NuGet cache.' }
    $taskArchive = Join-Path $taskPackageRoot ($taskRecord.Id.ToLowerInvariant()+'.'+$taskRecord.Version+'.nupkg')
    Assert-ReleaseRegularFile $taskPackageRoot $taskArchive
    $taskSha512 = [Security.Cryptography.SHA512]::Create()
    $taskStream = [IO.File]::OpenRead($taskArchive)
    try { $taskArchiveHash=[Convert]::ToBase64String($taskSha512.ComputeHash($taskStream)) }
    finally { $taskStream.Dispose(); $taskSha512.Dispose() }
    # NuGet contentHash excludes signing metadata; raw archive SHA512 is distinct.
    $taskArchiveHashFile = $taskArchive + '.sha512'
    $taskCacheMetadataPath = Join-Path $taskPackageRoot '.nupkg.metadata'
    Assert-ReleaseRegularFile $taskPackageRoot $taskArchiveHashFile
    Assert-ReleaseRegularFile $taskPackageRoot $taskCacheMetadataPath
    if ($taskArchiveHash -cne ([IO.File]::ReadAllText($taskArchiveHashFile)).Trim()) { throw 'NuGet archive hash does not match its cache checksum.' }
    $taskCacheMetadata = Get-Content -LiteralPath $taskCacheMetadataPath -Raw | ConvertFrom-Json
    if ($taskCacheMetadata.contentHash -cne $taskRecord.ContentHash) { throw 'NuGet restored content metadata does not match the lock file.' }
    $taskNuspec = Join-Path $taskPackageRoot ($taskRecord.Id.ToLowerInvariant()+'.nuspec')
    Assert-ReleaseRegularFile $taskPackageRoot $taskNuspec
    $taskArchiveStream = [IO.File]::OpenRead($taskArchive)
    $taskZip = [IO.Compression.ZipArchive]::new($taskArchiveStream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $taskRelevant = @($taskNuspec) + @(Get-ChildItem -LiteralPath $taskPackageRoot -File -Recurse | Where-Object { $_.Name -match '(?i)(license|notice|third.?party|eula|copying)' } | ForEach-Object { $_.FullName })
        foreach ($taskRelevantPath in $taskRelevant) {
            Assert-ReleaseRegularFile $taskPackageRoot $taskRelevantPath
            $taskRelative = $taskRelevantPath.Substring($taskPackageRoot.Length+1).Replace('\','/')
            $taskEntries = @($taskZip.Entries | Where-Object { $_.FullName -ieq $taskRelative })
            if ($taskEntries.Count -ne 1) { throw 'A restored license/metadata file is absent or duplicated in its package.' }
            $taskEntryStream = $taskEntries[0].Open()
            $taskMemory = [IO.MemoryStream]::new()
            try { $taskEntryStream.CopyTo($taskMemory); $taskEntryBytes=$taskMemory.ToArray() }
            finally { $taskEntryStream.Dispose(); $taskMemory.Dispose() }
            if ((Get-ReleaseSha256 $taskEntryBytes) -cne (Get-ReleaseSha256 ([IO.File]::ReadAllBytes($taskRelevantPath)))) { throw 'Restored license/metadata bytes differ from the package archive.' }
        }
    } finally { $taskZip.Dispose(); $taskArchiveStream.Dispose() }
    $taskXml = [Xml.XmlDocument]::new()
    $taskXml.XmlResolver = $null
    $taskReaderSettings = [Xml.XmlReaderSettings]::new()
    $taskReaderSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $taskReaderSettings.XmlResolver = $null
    $taskReader = [Xml.XmlReader]::Create($taskNuspec, $taskReaderSettings)
    try { $taskXml.Load($taskReader) } finally { $taskReader.Dispose() }
    $taskMetadata = $taskXml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $taskIdNode = $taskMetadata.SelectSingleNode('*[local-name()="id"]')
    $taskVersionNode = $taskMetadata.SelectSingleNode('*[local-name()="version"]')
    if ($taskIdNode.InnerText -ine $taskRecord.Id -or $taskVersionNode.InnerText -ne $taskRecord.Version) { throw 'Package metadata differs from the lock.' }
    $taskLicense = $taskMetadata.SelectSingleNode('*[local-name()="license"]')
    $taskLicenseUrl = $taskMetadata.SelectSingleNode('*[local-name()="licenseUrl"]')
    $taskCopyright = $taskMetadata.SelectSingleNode('*[local-name()="copyright"]')
    $taskRepository = $taskMetadata.SelectSingleNode('*[local-name()="repository"]')
    $taskLicenseKind = 'url-only'
    $taskLicenseValue = ''
    if ($null -ne $taskLicense) { $taskLicenseKind=$taskLicense.GetAttribute('type'); $taskLicenseValue=$taskLicense.InnerText }
    $taskNotices = @(Get-ChildItem -LiteralPath $taskPackageRoot -File -Recurse | Where-Object { $_.Name -match '(?i)(license|notice|third.?party|eula|copying)' } | Sort-Object FullName | ForEach-Object {
        Assert-ReleaseRegularFile $taskPackageRoot $_.FullName
        [ordered]@{ Path=$_.FullName.Substring($taskPackageRoot.Length+1).Replace('\','/'); Sha256=(Get-ReleaseSha256 ([IO.File]::ReadAllBytes($_.FullName))); Bytes=$_.Length }
    })
    $taskReview = 'expression-declared'
    if ($taskLicenseKind -eq 'file') { $taskReview='package-agreement-review' }
    if ($taskLicenseKind -eq 'url-only') { $taskReview='external-terms-review' }
    [ordered]@{
        Id=$taskRecord.Id; Version=$taskRecord.Version
        Scope=$(if ($taskRuntimeKeys.ContainsKey($taskKey)) { 'desktop-dependency' }
            elseif ($taskFrameworkPruned.ContainsKey($taskRecord.Id.ToLowerInvariant()) -and
                @($taskRecord.Uses | Where-Object { $_.Lock.StartsWith('src/', [StringComparison]::Ordinal) }).Count -gt 0) { 'desktop-framework-provided' }
            else { 'development-only' })
        DesktopFrameworkPruningRange=$(if ($taskFrameworkPruned.ContainsKey($taskRecord.Id.ToLowerInvariant())) { $taskFrameworkPruned[$taskRecord.Id.ToLowerInvariant()] } else { $null })
        ContentHashSha512=$taskRecord.ContentHash; CacheMetadataMatchesLock=$true
        ArchiveHashSha512=$taskArchiveHash; ArchiveMatchesCacheChecksum=$true
        ExtractedMetadataAndNoticesMatchArchive=$true; SignatureVerifiedByThisScript=$false
        License=[ordered]@{ Kind=$taskLicenseKind; Value=$taskLicenseValue; Url=$(if ($null -ne $taskLicenseUrl) { $taskLicenseUrl.InnerText } else { '' }) }
        Copyright=$(if ($null -ne $taskCopyright) { $taskCopyright.InnerText } else { '' })
        Repository=$(if ($null -ne $taskRepository) { $taskRepository.GetAttribute('url') } else { '' })
        RepositoryCommit=$(if ($null -ne $taskRepository) { $taskRepository.GetAttribute('commit') } else { '' })
        TermsReview=$taskReview; BundledNotices=$taskNotices; Uses=@($taskRecord.Uses.ToArray())
    }
}
$taskReport = [ordered]@{ FormatVersion=1; Source='NuGet lock files and locally restored packages'; IsLegalClearance=$false; IncludesBinaries=$false; Packages=@($taskPackages) }
$taskJson = $taskReport | ConvertTo-Json -Depth 12
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath=Join-Path $taskRoot 'artifacts/dependencies.json' }
$taskOutputFull = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutputFull)) | Out-Null
Write-ReleaseUtf8 $taskOutputFull $taskJson
Write-Output ('dependencies='+$taskPackages.Count+';cache-and-lock-hashes=matched;runtime='+$taskRuntimeKeys.Count+';binaries-included=false')
