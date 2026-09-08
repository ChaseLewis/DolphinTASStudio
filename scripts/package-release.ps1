param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('stable', 'beta', 'alpha')][string]$Channel,
    [string]$PublishDirectory = 'artifacts/package/Dolphin TAS Studio',
    [string]$OutputDirectory = 'artifacts/release',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$baseVersion = '(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)'
$pattern = if ($Channel -eq 'stable') { "^$baseVersion`$" } else { "^$baseVersion-$Channel\.(0|[1-9]\d*)`$" }
if ($Version -cnotmatch $pattern) {
    throw "The $Channel channel requires a version such as $(if ($Channel -eq 'stable') { '0.2.0' } else { "0.2.0-$Channel.1" })."
}
if ($ValidateOnly) { return }
Push-Location $workspace
try {
    $publishPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory } else { Join-Path $workspace $PublishDirectory }))
    $outputPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $workspace $OutputDirectory }))
    if (Test-Path -LiteralPath $outputPath) {
        if (Get-ChildItem -LiteralPath $outputPath -Force | Select-Object -First 1) { throw "Use an empty release output directory: $outputPath" }
    }
    foreach ($relative in @('TasStudio.App.exe', 'TasStudio.App.dll', 'TasStudio.Worker.exe', 'TasStudio.Worker.dll',
        'TasStudio.Sdk.dll', 'Velopack.dll', 'native/TasStudio.LibretroHost.dll', 'native/dolphin_libretro.dll',
        'system/dolphin-emu/Sys', 'NOTICE.md', 'LICENSE', 'LICENSES/Velopack-LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relative))) { throw "Published distribution is missing $relative" }
    }
    if (Test-Path -LiteralPath (Join-Path $publishPath 'tasstudio-data.path')) { throw 'Release packages must not contain a developer data-directory override.' }
    $builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishPath 'TasStudio.App.dll')).ProductVersion.Split('+')[0]
    if ($builtVersion -cne $Version) { throw "Published app version $builtVersion does not match package version $Version. Rebuild with -Version $Version." }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the pinned Velopack CLI failed.' }
    & dotnet vpk pack --packId DolphinTASStudio --packVersion $Version --packTitle 'Dolphin TAS Studio' `
        --packAuthors 'Dolphin TAS Studio contributors' --mainExe TasStudio.App.exe --packDir $publishPath `
        --outputDir $outputPath --channel "win-x64-$Channel" --runtime win-x64 --delta None `
        --icon (Join-Path $workspace 'src/TasStudio.App/Assets/dolphin-tas-studio.ico')
    if ($LASTEXITCODE -ne 0) { throw 'Creating update packages failed.' }
    $feedPath = Join-Path $outputPath "releases.win-x64-$Channel.json"
    if (-not (Test-Path -LiteralPath $feedPath)) { throw "Missing channel feed: $feedPath" }
    $assets = @(Get-ChildItem -LiteralPath $outputPath -File | Where-Object { $_.Extension -in @('.exe', '.zip', '.nupkg') -or $_.FullName -eq $feedPath })
    foreach ($extension in @('.exe', '.zip', '.nupkg')) {
        if (-not ($assets | Where-Object Extension -eq $extension)) { throw "Missing $extension release asset." }
    }
    $checksums = $assets | Sort-Object Name | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
    [IO.File]::WriteAllLines((Join-Path $outputPath 'SHA256SUMS.txt'), $checksums)
    Write-Output "Ready: $outputPath ($Channel)"
}
finally { Pop-Location }
