param([Parameter(Mandatory)][string]$OriginalSessionPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOriginalPath = [IO.Path]::GetFullPath($OriginalSessionPath)
$taskFixtureRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts\live-fixtures')) + [IO.Path]::DirectorySeparatorChar
if (-not $taskOriginalPath.StartsWith($taskFixtureRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Only this checkout synthetic fixture sessions are accepted.' }
$taskOriginalInfo = Get-Item -LiteralPath $taskOriginalPath
if ($taskOriginalInfo.Length -gt 16384) { throw 'Owned session exceeds size limit.' }
$taskOriginal = Get-Content -LiteralPath $taskOriginalPath -Raw | ConvertFrom-Json
$taskProject = [IO.Path]::GetFullPath($taskOriginal.ProjectPath)
$taskFolder = Split-Path $taskProject -Parent
if ((Split-Path $taskOriginalPath -Parent) -ne $taskFolder -or
    (Split-Path $taskProject -Leaf) -ne 'SemanticAssistantSynthetic.pbip' -or -not (Test-Path -LiteralPath $taskProject)) { throw 'Owned fixture path differs.' }
for ($taskAncestor = $taskFolder; $taskAncestor; $taskAncestor = Split-Path $taskAncestor -Parent) {
    if ((Get-Item -LiteralPath $taskAncestor).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse paths are refused.' }
}
$taskManifest = Get-Content -LiteralPath (Join-Path $taskFolder 'fixture-manifest.json') -Raw | ConvertFrom-Json
if ($taskManifest.Fixture -ne 'desktop-v1' -or $taskManifest.Synthetic -ne $true) { throw 'Synthetic fixture marker missing.' }
$taskExe = Join-Path $env:ProgramFiles 'Microsoft Power BI Desktop\bin\PBIDesktop.exe'
if (-not [IO.Path]::GetFullPath($taskOriginal.DesktopExe).Equals($taskExe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Desktop executable differs.' }
foreach ($taskPair in @(@($taskOriginal.DesktopProcessId,$taskOriginal.DesktopStartedUtcTicks),@($taskOriginal.EngineProcessId,$taskOriginal.EngineStartedUtcTicks))) {
    $taskPrevious = Get-Process -Id $taskPair[0] -ErrorAction SilentlyContinue
    if ($taskPrevious -and $taskPrevious.StartTime.ToUniversalTime().Ticks -eq $taskPair[1]) { throw 'Save and close the original synthetic Desktop first.' }
}
$taskDesktop = $null
$taskEngine = $null
$taskCompleted = $false
try {
    $taskDesktop = Start-Process -FilePath $taskExe -ArgumentList ('"' + $taskProject + '"') -WindowStyle Hidden -PassThru
    $taskDesktopTicks = $taskDesktop.StartTime.ToUniversalTime().Ticks
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $taskDesktop.Refresh()
        if ($taskDesktop.HasExited) { throw 'New launcher exited; no existing-instance fallback.' }
        $taskChildren = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($taskDesktop.Id) AND Name='msmdsrv.exe'")
        if ($taskChildren.Count -gt 1) { throw 'Ambiguous engine ownership.' }
        if ($taskChildren.Count -eq 1 -and $taskChildren[0].CommandLine -match '-s "([^"]+)"') {
            $taskWorkspace = Split-Path $Matches[1] -Parent
            if ((Test-Path -LiteralPath (Join-Path $taskWorkspace 'Data\msmdsrv.port.txt')) -and $taskDesktop.MainWindowTitle.Contains('SemanticAssistantSynthetic')) {
                $taskEngine = Get-Process -Id $taskChildren[0].ProcessId
                $taskEngineTicks = $taskEngine.StartTime.ToUniversalTime().Ticks
                break
            }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (-not $taskEngine) { throw 'Owned reopened fixture did not load.' }
    $taskRecord = Join-Path $taskFolder ('owned-reopened-session-' + [Guid]::NewGuid().ToString('N') + '.json')
    $taskJson = [ordered]@{ProjectPath=$taskProject;DesktopExe=$taskExe;DesktopProcessId=$taskDesktop.Id;DesktopStartedUtcTicks=$taskDesktopTicks;
        DesktopVersion=(Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion;EngineProcessId=$taskEngine.Id;EngineStartedUtcTicks=$taskEngineTicks;Workspace=$taskWorkspace} | ConvertTo-Json
    $taskFile = [IO.File]::Open($taskRecord,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $taskBytes=[Text.UTF8Encoding]::new($false).GetBytes($taskJson);$taskFile.Write($taskBytes,0,$taskBytes.Length) } finally { $taskFile.Dispose() }
    $taskCompleted = $true
    Write-Output $taskRecord
    Write-Output 'Reopened isolated fixture; persistence is not yet verified. Use --verify-persisted with this and the original session.'
} finally {
    if (-not $taskCompleted) {
        if ($taskDesktop) {
            $taskCurrent=Get-Process -Id $taskDesktop.Id -ErrorAction SilentlyContinue
            if($taskCurrent -and $taskCurrent.StartTime.ToUniversalTime().Ticks -eq $taskDesktopTicks){[void]$taskCurrent.CloseMainWindow();if(-not $taskCurrent.WaitForExit(3000)){$taskCurrent.Kill()}}
        }
        if($taskEngine){$taskCurrentEngine=Get-Process -Id $taskEngine.Id -ErrorAction SilentlyContinue;if($taskCurrentEngine -and $taskCurrentEngine.StartTime.ToUniversalTime().Ticks -eq $taskEngineTicks){$taskCurrentEngine.Kill()}}
    }
}
