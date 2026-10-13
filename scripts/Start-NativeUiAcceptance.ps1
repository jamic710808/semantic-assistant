param([Parameter(Mandatory)][string]$OwnedSessionPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSdk = Join-Path (Split-Path $taskRoot -Parent) '.tools\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) { $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source }
$taskSession = [IO.Path]::GetFullPath($OwnedSessionPath)
$taskParent = Split-Path $taskSession -Parent
Push-Location $taskRoot
try {
    & $taskSdk restore tests\SemanticAssistant.NativeUiAcceptance --locked-mode -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Native UI acceptance restore failed.' }
    & $taskSdk build tests\SemanticAssistant.NativeUiAcceptance -c Release --no-restore -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Native UI acceptance build failed.' }
    $taskDll = Join-Path $taskRoot 'tests\SemanticAssistant.NativeUiAcceptance\bin\Release\net10.0-windows\SemanticAssistant.NativeUiAcceptance.dll'
    # This is the interactive product window being observed and operated, not a background helper.
    $taskProcess = Start-Process -FilePath $taskSdk -ArgumentList ('"'+$taskDll+'" --owned-session "'+$taskSession+'" --output-parent "'+$taskParent+'"') -WindowStyle Normal -PassThru
    $taskLaunchPath = Join-Path $taskParent ('native-ui-process-'+[Guid]::NewGuid().ToString('N')+'.json')
    $taskRecord = [ordered]@{ProcessId=$taskProcess.Id;StartedUtcTicks=$taskProcess.StartTime.ToUniversalTime().Ticks;OwnedSessionPath=$taskSession}
    $taskFile=[IO.File]::Open($taskLaunchPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
    try{$taskBytes=[Text.UTF8Encoding]::new($false).GetBytes(($taskRecord|ConvertTo-Json));$taskFile.Write($taskBytes,0,$taskBytes.Length)}finally{$taskFile.Dispose()}
    Write-Output $taskLaunchPath
    Write-Output 'Interactive acceptance launched; no automated UI action or live provider verification is implied.'
} finally { Pop-Location }
