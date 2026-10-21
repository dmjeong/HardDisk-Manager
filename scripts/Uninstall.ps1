#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$InstallDirectory="$env:ProgramFiles\ExplorerManager")
$ErrorActionPreference='Stop'
$InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$marker=Join-Path $InstallDirectory '.explorermanager-install.json'
if (-not (Test-Path $marker)) { throw 'Installation marker not found. Refusing to remove this directory.' }
$record=Get-Content $marker -Raw | ConvertFrom-Json
if ($record.Path -ne $InstallDirectory -or $InstallDirectory.Length -lt 10) { throw 'Installation marker does not match.' }
Write-Warning 'Stopping the service clears all RAM recovery copies.'
if (Get-Service ExplorerManager -ErrorAction SilentlyContinue) {
    Stop-Service ExplorerManager
    (Get-Service ExplorerManager).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    & sc.exe delete ExplorerManager | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
}
Unregister-ScheduledTask -TaskName 'ExplorerManager.Desktop' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process ExplorerManager.Desktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDirectory\*" } | Stop-Process
Remove-Item $InstallDirectory -Recurse -Force
Write-Host 'Removed application and service. Policies, logs and audit metadata remain in ProgramData\ExplorerManager.'
