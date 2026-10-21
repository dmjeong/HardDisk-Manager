#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
$full=(Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
if ($full.Length -le 3 -or $full.StartsWith('\\') -or -not (Test-Path $full -PathType Container)) { throw 'A local managed subdirectory is required.' }
foreach ($blocked in @($env:windir,$env:ProgramFiles,[Environment]::GetFolderPath('ProgramFilesX86'),"$env:ProgramData\ExplorerManager")) {
    if ($blocked -and ($full -eq $blocked -or $full.StartsWith($blocked.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase))) { throw 'Protected path.' }
}
if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse roots are not allowed.' }
& icacls.exe $full /grant 'NT SERVICE\ExplorerManager:(OI)(CI)M'
if ($LASTEXITCODE -ne 0) { throw 'Could not grant service access.' }
Write-Host "Granted service read/delete/restore permission to: $full"
