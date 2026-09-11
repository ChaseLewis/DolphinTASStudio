using System.Text;

namespace TasStudio.Emulation;

public static class DolphinMovieLauncher
{
    /// <summary>Create a launcher for a new or existing export without rebaking its input stream.</summary>
    public static string Save(string moviePath, string companionDirectory, string gamePath)
    {
        var root = Path.GetFullPath(companionDirectory);
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "dolphin-launcher-helper.ps1");
        var launcher = Path.Combine(root, "Play in Dolphin.cmd");
        var relativeMovie = Path.GetRelativePath(root, Path.GetFullPath(moviePath));
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        File.WriteAllText(script, $$"""
            param([string]$DolphinPath)
            $ErrorActionPreference = 'Stop'
            $moviePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot {{Quote(relativeMovie)}}))
            $gamePath = {{Quote(Path.GetFullPath(gamePath))}}
            $userProfile = Join-Path $PSScriptRoot 'User'
            $originalCards = Join-Path $PSScriptRoot 'InitialCards'
            $savedExecutable = Join-Path $PSScriptRoot 'dolphin-executable.txt'
            if (-not (Test-Path -LiteralPath $moviePath -PathType Leaf)) { throw "Movie missing: $moviePath" }
            if (-not (Test-Path -LiteralPath $gamePath -PathType Leaf)) { throw "Game image missing: $gamePath" }
            if (-not (Test-Path -LiteralPath (Join-Path $userProfile 'Config/Dolphin.ini'))) { throw 'The exported Dolphin profile is missing.' }

            if (-not $DolphinPath -and (Test-Path -LiteralPath $savedExecutable)) {
                $DolphinPath = (Get-Content -LiteralPath $savedExecutable -Raw).Trim()
            }
            if (-not $DolphinPath -or -not (Test-Path -LiteralPath $DolphinPath -PathType Leaf)) {
                Add-Type -AssemblyName System.Windows.Forms
                $picker = New-Object System.Windows.Forms.OpenFileDialog
                $picker.Title = 'Select standalone Dolphin.exe'
                $picker.Filter = 'Dolphin executable (Dolphin.exe)|Dolphin.exe'
                try {
                    if ($picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit 0 }
                    $DolphinPath = $picker.FileName
                } finally { $picker.Dispose() }
            }
            $DolphinPath = (Resolve-Path -LiteralPath $DolphinPath).Path

            # Never reset a working card while this export's Dolphin session is still using it.
            $running = @(Get-CimInstance Win32_Process -Filter "Name='Dolphin.exe'" | Where-Object {
                $_.CommandLine -and $_.CommandLine.IndexOf($userProfile, [StringComparison]::OrdinalIgnoreCase) -ge 0
            })
            if ($running.Count -gt 0) { throw 'Close the Dolphin window using this export before starting another playback.' }
            if (Test-Path -LiteralPath $originalCards -PathType Container) {
                $workingCards = Join-Path $userProfile 'GC'
                New-Item -ItemType Directory -Path $workingCards -Force | Out-Null
                foreach ($card in Get-ChildItem -LiteralPath $originalCards -Filter 'MemoryCardA.*.raw' -File) {
                    Copy-Item -LiteralPath $card.FullName -Destination (Join-Path $workingCards $card.Name) -Force
                }
            }
            Set-Content -LiteralPath $savedExecutable -Value $DolphinPath -Encoding UTF8
            $arguments = @('-u', ('"' + $userProfile + '"'), '-e', ('"' + $gamePath + '"'), '-m', ('"' + $moviePath + '"'))
            # This is the interactive Dolphin window the user launched to inspect playback.
            Start-Process -FilePath $DolphinPath -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Normal
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(launcher, "@echo off\r\n\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -STA -ExecutionPolicy Bypass -File \"%~dp0dolphin-launcher-helper.ps1\"\r\nif errorlevel 1 pause\r\n", Encoding.ASCII);
        return launcher;
    }
}
