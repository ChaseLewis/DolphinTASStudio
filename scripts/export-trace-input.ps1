param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$root = Split-Path -Parent $projectPath
$destination = [System.IO.Path]::GetFullPath($Output)
if ($destination.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an artifact output outside the source project'
}
function Read-VerifiedAsset($Asset) {
    $path = [System.IO.Path]::GetFullPath((Join-Path $root $Asset.Path))
    if (-not $path.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Asset is outside the project'
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Asset.Sha256) {
        throw "Asset hash mismatch: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
$manifest = Get-Content -LiteralPath $projectPath -Raw | ConvertFrom-Json
$timeline = Read-VerifiedAsset $manifest.Timeline
$metadata = $destination + '.json'
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $metadata)) { throw 'Output must be fresh' }
$stream = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew)
$writer = [System.IO.BinaryWriter]::new($stream)
$count = 0
try {
    foreach ($chunk in $timeline.Inputs) {
        foreach ($input in (Read-VerifiedAsset $chunk)) {
            $writer.Write([uint16]$input.Buttons)
            foreach ($field in @('StickX', 'StickY', 'CStickX', 'CStickY', 'TriggerL', 'TriggerR')) {
                $writer.Write([byte]$input.$field)
            }
            $count++
        }
    }
}
finally { $writer.Dispose() }
@{
    Kind = 'input-intent-only-not-canonical-poll-replay'
    Project = $projectPath
    ProjectSha256 = (Get-FileHash -LiteralPath $projectPath).Hash
    Timeline = $manifest.Timeline
    Inputs = $timeline.Inputs
    Records = $count
    OutputSha256 = (Get-FileHash -LiteralPath $destination).Hash
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadata -Encoding utf8
Write-Output "Exported $count input intents to $destination. No saved state was transferred."
