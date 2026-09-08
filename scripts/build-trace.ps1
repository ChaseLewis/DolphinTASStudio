param([int]$Jobs = 10)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$source = Join-Path $workspace 'native/dolphin-libretro'
$build = Join-Path $workspace 'native/build-dolphin-trace'
$tests = Join-Path $workspace 'native/build-trace-tests'
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
. (Join-Path $PSScriptRoot 'core-patches.ps1')
if ($Jobs -lt 1 -or $Jobs -gt 64) { throw 'Jobs must be between 1 and 64' }
$revision = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -ne 'e1e6d25fa1392b7d1bc05bf800c71b807a2bd2e0') {
    throw 'Expected the pinned Dolphin source checkout. Run the ordinary build setup first.'
}
Install-CorePatches $source (Join-Path $workspace 'native/patches')
Invoke-Checked cmake @('-S', $source, '-B', $build, '-G', 'Visual Studio 18 2026', '-A', 'x64',
    '-DLIBRETRO=ON', '-DDOLPHIN_TAS_TRACE=ON', '-DENABLE_QT=OFF', '-DENABLE_NOGUI=OFF',
    '-DENABLE_TESTS=OFF', '-DENABLE_CLI_TOOL=OFF', '-DENABLE_AUTOUPDATE=OFF',
    '-DENABLE_ANALYTICS=OFF', '-DUSE_DISCORD_PRESENCE=OFF', '-DUSE_MGBA=OFF',
    '-DUSE_RETRO_ACHIEVEMENTS=OFF', '-DENABLE_SDL=OFF', '-DENABLE_CUBEB=OFF',
    '-DENCODE_FRAMEDUMPS=OFF', '-DENABLE_VULKAN=OFF', '-DUSE_UPNP=OFF', '-DDISTRIBUTOR=TasStudioTrace')
Invoke-Checked cmake @('--build', $build, '--config', 'Release', '--target', 'dolphin_libretro', '--parallel', "$Jobs")
Invoke-Checked cmake @('-S', (Join-Path $workspace 'native/tracing'), '-B', $tests, '-G', 'Visual Studio 18 2026', '-A', 'x64')
Invoke-Checked cmake @('--build', $tests, '--config', 'Release', '--parallel', "$Jobs")
Invoke-Checked ctest @('--test-dir', $tests, '-C', 'Release', '--output-on-failure')
Write-Output "Trace core (not published): $build/Binaries/dolphin_libretro.dll"
