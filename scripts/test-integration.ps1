param([Parameter(Mandatory)][string]$RomPath)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    & dotnet run --project tests/TasStudio.Integration.Tests -c Release -- $RomPath artifacts/integration
    if ($LASTEXITCODE -ne 0) { throw 'Initial core integration test failed.' }
    & dotnet run --project tests/TasStudio.Integration.Tests -c Release --no-build -- $RomPath artifacts/integration restore
    if ($LASTEXITCODE -ne 0) { throw 'Fresh-process core replay failed.' }
    & dotnet run --project tests/TasStudio.Integration.Tests -c Release --no-build -- $RomPath artifacts/integration-folder-v2 project-create
    if ($LASTEXITCODE -ne 0) { throw 'Project workflow test failed.' }
    & dotnet run --project tests/TasStudio.Integration.Tests -c Release --no-build -- $RomPath artifacts/integration-folder-v2 project-restore
    if ($LASTEXITCODE -ne 0) { throw 'Fresh-process project replay failed.' }
}
finally { Pop-Location }
