param(
    [switch]$SkipNative,
    [switch]$Publish,
    [string]$PublishDirectory = 'artifacts/app',
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$coreRevision = 'e1e6d25fa1392b7d1bc05bf800c71b807a2bd2e0'
$corePath = Join-Path $workspace 'native/dolphin-libretro'
$coreBuild = Join-Path $workspace 'native/build-dolphin'
$hostBuild = Join-Path $workspace 'native/build-host'
. (Join-Path $PSScriptRoot 'core-patches.ps1')
if ($Version -and $Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw "Version must be SemVer without a leading v, for example 0.1.0 or 1.0.0-rc.1. Found: $Version"
}
$versionArguments = if ($Version) { @("-p:Version=$Version") } else { @() }
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
Push-Location $workspace
try {
    if (-not $SkipNative) {
        if (-not (Test-Path -LiteralPath (Join-Path $corePath '.git'))) {
            Invoke-Checked git @('clone', '--depth', '1', '--no-checkout', 'https://github.com/libretro/dolphin.git', $corePath)
            Invoke-Checked git @('-C', $corePath, 'fetch', '--depth', '1', 'origin', $coreRevision)
            Invoke-Checked git @('-C', $corePath, 'checkout', '--detach', $coreRevision)
        }
        $actualRevision = (& git -C $corePath rev-parse HEAD).Trim()
        if ($actualRevision -ne $coreRevision) { throw "Expected core revision $coreRevision; found $actualRevision. Existing source was preserved." }
        Invoke-Checked git @('-C', $corePath, 'submodule', 'update', '--init', '--depth', '1', '--jobs', '8')
        Install-CorePatches $corePath (Join-Path $workspace 'native/patches')
        $configure = @('-S', $corePath, '-B', $coreBuild, '-G', 'Visual Studio 18 2026', '-A', 'x64',
            '-DLIBRETRO=ON', '-DDOLPHIN_TAS_TRACE=OFF', '-DENABLE_QT=OFF', '-DENABLE_NOGUI=OFF', '-DENABLE_TESTS=OFF',
            '-DENABLE_CLI_TOOL=OFF', '-DENABLE_AUTOUPDATE=OFF', '-DENABLE_ANALYTICS=OFF',
            '-DUSE_DISCORD_PRESENCE=OFF', '-DUSE_MGBA=OFF', '-DUSE_RETRO_ACHIEVEMENTS=OFF',
            '-DENABLE_SDL=OFF', '-DENABLE_CUBEB=OFF', '-DENCODE_FRAMEDUMPS=OFF',
            '-DENABLE_VULKAN=OFF', '-DUSE_UPNP=OFF', '-DDISTRIBUTOR=TasStudio')
        Invoke-Checked cmake $configure
        Invoke-Checked cmake @('--build', $coreBuild, '--config', 'Release', '--target', 'dolphin_libretro', '--parallel', '12')
        Invoke-Checked cmake @('-S', (Join-Path $workspace 'native/TasStudio.LibretroHost'), '-B', $hostBuild, '-G', 'Visual Studio 18 2026', '-A', 'x64')
        Invoke-Checked cmake @('--build', $hostBuild, '--config', 'Release', '--parallel', '4')
    }
    Invoke-Checked dotnet (@('build', 'TasStudio.slnx', '-c', 'Release') + $versionArguments)
    Invoke-Checked dotnet @('test', 'tests/TasStudio.Core.Tests', '-c', 'Release', '--no-build')
    if ($Publish) {
        $destination = if ([System.IO.Path]::IsPathRooted($PublishDirectory)) { [System.IO.Path]::GetFullPath($PublishDirectory) } else { [System.IO.Path]::GetFullPath((Join-Path $workspace $PublishDirectory)) }
        Invoke-Checked dotnet (@('publish', 'src/TasStudio.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $destination) + $versionArguments)
        Invoke-Checked dotnet (@('publish', 'src/TasStudio.Worker', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $destination) + $versionArguments)
        New-Item -ItemType Directory -Force -Path (Join-Path $destination 'native') | Out-Null
        Copy-Item -LiteralPath (Join-Path $hostBuild 'Release/TasStudio.LibretroHost.dll') -Destination (Join-Path $destination 'native')
        Copy-Item -LiteralPath (Join-Path $coreBuild 'Binaries/dolphin_libretro.dll') -Destination (Join-Path $destination 'native')
        $systemParent = Join-Path $destination 'system/dolphin-emu'
        New-Item -ItemType Directory -Force -Path $systemParent | Out-Null
        Copy-Item -LiteralPath (Join-Path $corePath 'Data/Sys') -Destination $systemParent -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $corePath 'COPYING') -Destination (Join-Path $destination 'Dolphin-COPYING.txt')
        Copy-Item -LiteralPath (Join-Path $corePath 'LICENSES') -Destination $destination -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $workspace 'licenses/Dock-LICENSE.txt') -Destination (Join-Path $destination 'LICENSES/Dock-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $workspace 'licenses/Velopack-LICENSE.txt') -Destination (Join-Path $destination 'LICENSES/Velopack-LICENSE.txt')
        New-Item -ItemType Directory -Force -Path (Join-Path $destination 'examples') | Out-Null
        # Ship C# source without bin/obj trees; SDK references resolve to this build.
        $csharpSource = Join-Path $workspace 'examples/csharp'
        foreach ($file in Get-ChildItem -LiteralPath $csharpSource -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
            $relative = $file.FullName.Substring($csharpSource.Length).TrimStart('\', '/')
            $target = Join-Path (Join-Path $destination 'examples/csharp') $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            if ($file.Extension -eq '.csproj') {
                $text = [System.IO.File]::ReadAllText($file.FullName)
                $text = $text.Replace('<ProjectReference Include="../../../src/TasStudio.Sdk/TasStudio.Sdk.csproj" />', '<Reference Include="TasStudio.Sdk"><HintPath>../../../TasStudio.Sdk.dll</HintPath></Reference><Reference Include="TasStudio.Core"><HintPath>../../../TasStudio.Core.dll</HintPath></Reference>')
                [System.IO.File]::WriteAllText($target, $text)
            } else { Copy-Item -LiteralPath $file.FullName -Destination $target -Force }
        }
        Copy-Item -LiteralPath (Join-Path $workspace 'NOTICE.md') -Destination $destination
        Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination $destination
        Write-Output "Ready: $destination/TasStudio.App.exe"
    }
}
finally { Pop-Location }
