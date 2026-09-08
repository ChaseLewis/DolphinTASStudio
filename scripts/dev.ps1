param([switch]$Unique, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$directoryName = if ($Unique) { (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) } else { 'default' }
$workDirectory = Join-Path $workspace ('.local/dev/' + $directoryName)
$runDirectory = Join-Path $workDirectory 'app'
$executable = Join-Path $runDirectory 'TasStudio.App.exe'
$lockBytes = [System.Text.Encoding]::UTF8.GetBytes($workDirectory.ToLowerInvariant())
$lockHash = [BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($lockBytes)).Replace('-', '')
$buildLock = New-Object System.Threading.Mutex($false, ('Local\TasStudioDev-' + $lockHash))
$ownsLock = $false
Push-Location $workspace
try {
    try { $ownsLock = $buildLock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $ownsLock = $true }
    if (-not $ownsLock) { throw 'This dev workspace is already being built. Wait for that command to finish.' }
    $running = @(Get-Process -Name TasStudio.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
    if ($running.Count -gt 0) {
        Write-Output "Using the existing dev instance (PID $($running[0].Id)). No rebuild or second instance was started."
        Write-Output "Workspace: $workDirectory"
        Write-Output 'Close that app and run just dev again to pick up code changes; just dev-unique starts an isolated experiment.'
        return
    }
    $nativeDependencies = @(
        'native/build-host/Release/TasStudio.LibretroHost.dll',
        'native/build-dolphin/Binaries/dolphin_libretro.dll',
        'native/dolphin-libretro/Data/Sys'
    )
    if ($nativeDependencies.Where({ -not (Test-Path -LiteralPath $_) }).Count -gt 0) {
        Write-Output 'Preparing Dolphin for the first local run...'
        & (Join-Path $PSScriptRoot 'build.ps1')
    }

    # Build output is separate from data. Neither command deletes a workspace or its recovery files.
    & dotnet build src/TasStudio.App -c Debug --nologo --output $runDirectory
    if ($LASTEXITCODE -ne 0) { throw "Development build failed with exit code $LASTEXITCODE" }
    & dotnet build src/TasStudio.Worker -c Debug --nologo --output $runDirectory
    if ($LASTEXITCODE -ne 0) { throw "Worker build failed with exit code $LASTEXITCODE" }

    if ($Unique) {
        New-Item -ItemType Directory -Force -Path (Join-Path $workDirectory 'data') | Out-Null
        # Relative to the executable: also works when it is launched directly later.
        Set-Content -LiteralPath (Join-Path $runDirectory 'tasstudio-data.path') -Value '../data' -Encoding UTF8
    }
    Write-Output "Workspace: $workDirectory"
    $dataDirectory = if ($Unique) { Join-Path $workDirectory 'data' } else { Join-Path $env:LOCALAPPDATA 'TasStudio' }
    Write-Output "Settings, recovery and loose states: $dataDirectory"
    if ($NoLaunch) { Write-Output "Built: $executable"; return }

    # Launch the apphost directly so Dolphin's required CET compatibility setting applies.
    $executable = Join-Path $runDirectory 'TasStudio.App.exe'
    $process = Start-Process -FilePath $executable -WorkingDirectory $workDirectory -PassThru
    Write-Output "Development app started (PID $($process.Id)): $executable"
}
finally { Pop-Location; if ($ownsLock) { $buildLock.ReleaseMutex() }; $buildLock.Dispose() }
