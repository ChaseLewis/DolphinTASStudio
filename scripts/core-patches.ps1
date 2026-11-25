function Install-CorePatches([string]$CorePath, [string]$PatchDirectory) {
    Install-BaseCorePatches $CorePath $PatchDirectory
    foreach ($name in @('0003-play-memory-card-size.patch', '0004-d3d-logic-ops.patch', '0005-realtime-audio.patch', '0006-boot-polls.patch', '0007-runtime-resolution.patch', '0008-audio-output-clear.patch', '0009-gci-card-import.patch', '0010-project-gci-folder.patch')) {
        $patch = Join-Path $PatchDirectory $name
        if (-not (Test-CorePatchApplied $CorePath $patch)) {
            # The output-clear fix extends the base audio patch's added function.
            # Validate the earlier layer in a temporary index without changing sources.
            $audioClearPatch = Join-Path $PatchDirectory '0008-audio-output-clear.patch'
            if ($name -eq '0005-realtime-audio.patch') {
                if (Test-CorePatchApplied $CorePath $audioClearPatch) {
                    $previousIndex = $env:GIT_INDEX_FILE
                    $temporaryIndex = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString() + '.index')
                    try {
                        $env:GIT_INDEX_FILE = $temporaryIndex
                        Invoke-Checked git @('-C', $CorePath, 'read-tree', 'HEAD')
                        $paths = @(& git -C $CorePath apply --numstat $patch)
                        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate audio patch paths' }
                        $paths = @($paths | ForEach-Object { ($_ -split "`t")[2] })
                        Invoke-Checked git (@('-C', $CorePath, 'add', '--') + $paths)
                        Invoke-Checked git @('-C', $CorePath, 'apply', '--cached', '--reverse', $audioClearPatch)
                        Invoke-Checked git @('-C', $CorePath, 'apply', '--cached', '--reverse', '--check', $patch)
                    }
                    finally {
                        $env:GIT_INDEX_FILE = $previousIndex
                        if (Test-Path -LiteralPath $temporaryIndex) { Remove-Item -LiteralPath $temporaryIndex }
                    }
                    continue
                }
            }
            Invoke-Checked git @('-C', $CorePath, 'apply', '--check', $patch)
            Invoke-Checked git @('-C', $CorePath, 'apply', $patch)
        }
    }
}

function Install-BaseCorePatches([string]$CorePath, [string]$PatchDirectory) {
    $basePatch = Join-Path $PatchDirectory '0001-tas-contract.patch'
    $tracePatch = Join-Path $PatchDirectory '0002-tas-trace.patch'
    $bootPatch = Join-Path $PatchDirectory '0006-boot-polls.patch'
    $bootApplied = Test-CorePatchApplied $CorePath $bootPatch
    $traceApplied = Test-CorePatchApplied $CorePath $tracePatch
    if ($traceApplied -or $bootApplied) {
        $previousIndex = $env:GIT_INDEX_FILE
        $temporaryIndex = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString() + '.index')
        try {
            $env:GIT_INDEX_FILE = $temporaryIndex
            Invoke-Checked git @('-C', $CorePath, 'read-tree', 'HEAD')
            $paths = @(& git -C $CorePath apply --numstat $basePatch $tracePatch)
            if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate core patch paths' }
            $paths = @($paths | ForEach-Object { ($_ -split "`t")[2] } | Sort-Object -Unique)
            Invoke-Checked git (@('-C', $CorePath, 'add', '--') + $paths)
            # The boot extension overlaps base/trace context; validate those layers without it.
            if ($bootApplied) { Invoke-Checked git @('-C', $CorePath, 'apply', '--cached', '--reverse', $bootPatch) }
            Invoke-Checked git @('-C', $CorePath, 'apply', '--cached', '--reverse', $tracePatch)
            Invoke-Checked git @('-C', $CorePath, 'apply', '--cached', '--reverse', '--check', $basePatch)
        }
        finally {
            $env:GIT_INDEX_FILE = $previousIndex
            if (Test-Path -LiteralPath $temporaryIndex) { Remove-Item -LiteralPath $temporaryIndex }
        }
        return
    }
    if (-not (Test-CorePatchApplied $CorePath $basePatch)) {
        Invoke-Checked git @('-C', $CorePath, 'apply', '--check', $basePatch)
        Invoke-Checked git @('-C', $CorePath, 'apply', $basePatch)
    }
    Invoke-Checked git @('-C', $CorePath, 'apply', '--check', $tracePatch)
    Invoke-Checked git @('-C', $CorePath, 'apply', $tracePatch)
}

function Test-CorePatchApplied([string]$CorePath, [string]$Patch) {
    # A missing/overlapped patch is an expected probe result. Windows PowerShell
    # otherwise turns git's stderr into a terminating error under build.ps1.
    $ErrorActionPreference = 'Continue'
    & git -C $CorePath apply --reverse --check $Patch 2>$null
    return $LASTEXITCODE -eq 0
}
