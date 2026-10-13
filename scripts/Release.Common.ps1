Set-StrictMode -Version Latest

function Write-ReleaseUtf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, ($Text.Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}

function Get-ReleaseSha256([byte[]]$Bytes) {
    $taskHash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($taskHash.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $taskHash.Dispose() }
}

function Assert-SourceRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[\\\x00-\x1f:]' -or $Path.StartsWith('/') -or $Path -match '(^|/)\.{1,2}(/|$)') {
        throw 'Invalid source archive path.'
    }
    if ($Path -match '(?i)(^|/)(bin|obj|artifacts|\.git|\.vs|\.idea|TestResults)(/|$)' -or
        $Path -match '(?i)(^|/)\.env($|\.)' -or $Path -match '(?i)\.(exe|dll|pdb|pbix|pbit|pem|pfx|key|log|zip|nupkg)$') {
        throw 'A generated, secret or model artifact cannot enter a source release.'
    }
    $taskRoots = @('.gitignore', 'LICENSE', 'NOTICE', 'README.md', 'CONTRIBUTING.md',
        'Directory.Build.props', 'NuGet.Config', 'global.json', 'SemanticAssistant.slnx', 'Start-Demo.cmd')
    if ($Path -notin $taskRoots -and ($Path -notmatch '^(\.github|docs|examples|scripts|src|tests)/' -or
        $Path -notmatch '(?i)\.(md|cs|csproj|xaml|json|slnx|props|config|ps1|cmd|yml|yaml|tmdl|txt|rtf)$')) {
        throw 'Source file is outside the reviewed release allowlist.'
    }
}

function Assert-ReleaseRegularFile([string]$Root, [string]$Path) {
    $taskRootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $taskFull = [IO.Path]::GetFullPath($Path)
    if (-not $taskFull.StartsWith($taskRootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release file is outside the expected root.'
    }
    $taskItem = Get-Item -LiteralPath $taskFull -Force -ErrorAction Stop
    if ($taskItem.PSIsContainer) { throw 'Expected a regular file.' }
    while ($null -ne $taskItem) {
        if (($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release files cannot traverse reparse points.' }
        if ($taskItem.FullName.TrimEnd('\', '/') -eq $taskRootFull) { return }
        if ($taskItem -is [IO.FileInfo]) { $taskItem = $taskItem.Directory } else { $taskItem = $taskItem.Parent }
    }
    throw 'Release root cannot be resolved.'
}

function Get-ReleaseSourceFiles([string]$Root) {
    $taskStart = [Diagnostics.ProcessStartInfo]::new()
    $taskStart.FileName = (Get-Command git -ErrorAction Stop).Source
    $taskStart.Arguments = 'ls-files -z --cached --others --exclude-standard'
    $taskStart.WorkingDirectory = $Root
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    $taskStart.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        $taskOutput = $taskProcess.StandardOutput.ReadToEnd()
        $taskError = $taskProcess.StandardError.ReadToEnd()
        $taskProcess.WaitForExit()
        if ($taskProcess.ExitCode -ne 0) { throw 'Git source inventory failed.' }
    } finally { $taskProcess.Dispose() }
    [string[]]$taskPaths = @($taskOutput.Split([char]0) | Where-Object { $_.Length -gt 0 } | Select-Object -Unique)
    [Array]::Sort($taskPaths, [StringComparer]::Ordinal)
    if ($taskPaths.Length -eq 0) { throw 'Empty source inventory.' }
    foreach ($taskPath in $taskPaths) {
        Assert-SourceRelativePath $taskPath
        $taskFull = Join-Path $Root $taskPath
        Assert-ReleaseRegularFile $Root $taskFull
        $taskFile = Get-Item -LiteralPath $taskFull
        if ($taskFile.Length -gt 8MB) { throw 'Unexpectedly large source file.' }
        [pscustomobject]@{ Path = $taskPath; FullPath = $taskFull }
    }
}
