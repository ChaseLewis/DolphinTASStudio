function Install-CorePatches([string]$CorePath, [string]$PatchDirectory) {
    $basePatch = Join-Path $PatchDirectory '0001-tas-contract.patch'
    $tracePatch = Join-Path $PatchDirectory '0002-tas-trace.patch'
    & git -C $CorePath apply --reverse --check $tracePatch 2>$null
    if ($LASTEXITCODE -eq 0) {
        $previousIndex = $env:GIT_INDEX_FILE
        $temporaryIndex = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString() + '.index')
        try {
            $env:GIT_INDEX_FILE = $temporaryIndex
            Invoke-Checked git @('-C', $CorePath, 'read-tree', 'HEAD')
            $paths = @(& git -C $CorePath apply --numstat $basePatch $tracePatch)
            if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate core patch paths' }
            $paths = @($paths | ForEach-Object { ($_ -split "`t")[2] } | Sort-Object -Unique)
            Invoke-Checked git (@('-C', $CorePath, 'add', '--') + $paths)
            Invoke-Checked git @('-C', $CorePath, 'apply', '--cached', '--reverse', $tracePatch)
            Invoke-Checked git @('-C', $CorePath, 'apply', '--cached', '--reverse', '--check', $basePatch)
        }
        finally {
            $env:GIT_INDEX_FILE = $previousIndex
            if (Test-Path -LiteralPath $temporaryIndex) { Remove-Item -LiteralPath $temporaryIndex }
        }
        return
    }
    & git -C $CorePath apply --reverse --check $basePatch 2>$null
    if ($LASTEXITCODE -ne 0) {
        Invoke-Checked git @('-C', $CorePath, 'apply', '--check', $basePatch)
        Invoke-Checked git @('-C', $CorePath, 'apply', $basePatch)
    }
    Invoke-Checked git @('-C', $CorePath, 'apply', '--check', $tracePatch)
    Invoke-Checked git @('-C', $CorePath, 'apply', $tracePatch)
}
