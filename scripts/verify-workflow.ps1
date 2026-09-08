param(
    [Parameter(Mandatory = $true)][string]$Rom,
    [string]$OutputDirectory = ('artifacts/workflow-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$romPath = (Resolve-Path -LiteralPath $Rom).Path
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $workspace $OutputDirectory)) }
if (Test-Path -LiteralPath $output) { throw 'Use a new output folder to verify cold-start behavior and preserve earlier evidence.' }
New-Item -ItemType Directory -Path $output | Out-Null
$results = New-Object System.Collections.Generic.List[object]
$executable = Join-Path $workspace 'tests/TasStudio.Integration.Tests/bin/Release/net10.0/TasStudio.Integration.Tests.exe'
function Check-Native([string]$Name, [string]$Folder, [string[]]$Modes) {
    Write-Output "Checking $Name..."
    $log = Join-Path $output ($Name + '.log')
    $exitCode = $null
    try { & $executable $romPath (Join-Path $output $Folder) @Modes *> $log; $exitCode = $LASTEXITCODE }
    finally {
        $passed = $null -ne $exitCode -and $exitCode -eq 0
        $results.Add([pscustomobject]@{ Check = $Name; Passed = $passed; Log = $log })
    }
    if (-not $passed) { throw "$Name failed. See $log" }
}
Push-Location $workspace
try {
    # Uses the locally built native core. Run just build after native source changes.
    & dotnet build tests/TasStudio.Integration.Tests -c Release --nologo *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Integration build failed; see build.log.' }
    Get-FileHash (Join-Path (Split-Path $executable) 'native/dolphin_libretro.dll'), (Join-Path (Split-Path $executable) 'native/TasStudio.LibretroHost.dll') -Algorithm SHA256 |
        Select-Object Path, Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'runtime-hashes.json') -Encoding UTF8
    Check-Native 'project-create' 'project' @('project-create')
    Check-Native 'project-reopen' 'project' @('project-restore')
    Check-Native 'boot-precompile' 'boot' @('boot-preview', 'restore-baseline')
    Check-Native 'pixels-create' 'pixels' @('--diagnostic-input', '0')
    Check-Native 'pixels-reopen' 'pixels' @('restore', '--diagnostic-input', '0')

    Write-Output 'Checking recovery after terminating its writer...'
    $recoveryOutput = Join-Path $output 'recovery'
    $ready = Join-Path $recoveryOutput 'crash-ready'
    # Windows paths cannot contain quotes. Quote each path for Start-Process on PowerShell 5.1.
    $crashWriter = Start-Process -FilePath $executable -ArgumentList @('"' + $romPath + '"', '"' + $recoveryOutput + '"', 'recovery-crash') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'recovery-writer.log') -RedirectStandardError (Join-Path $output 'recovery-writer-error.log')
    try {
        $deadline = [DateTime]::UtcNow.AddMinutes(2)
        while (-not (Test-Path -LiteralPath $ready)) {
            if ($crashWriter.HasExited) { throw 'Recovery writer exited before committing its snapshot.' }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Recovery writer timed out.' }
            Start-Sleep -Milliseconds 100
        }
        if ([IO.File]::ReadAllText($ready).Trim() -ne [string]$crashWriter.Id) { throw 'Unexpected recovery writer identity.' }
        # Terminate only the child created above, bypassing its disposal/normal-close save path.
        Stop-Process -Id $crashWriter.Id -Force
        $crashWriter.WaitForExit()
    }
    finally {
        if (-not $crashWriter.HasExited) { Stop-Process -Id $crashWriter.Id -Force }
        $crashWriter.Dispose()
    }
    Check-Native 'recovery-reopen-after-kill' 'recovery' @('recovery-restore')
    Write-Output "Workflow checks passed. Evidence: $output"
}
finally {
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding UTF8
    Pop-Location
}
