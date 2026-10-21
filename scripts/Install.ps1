#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$InstallDirectory="$env:ProgramFiles\ExplorerManager", [switch]$NoStartUi, [switch]$NoAutoStart, [switch]$NoHardwareSensors)
$ErrorActionPreference='Stop'
$source = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path "$source/Service/ExplorerManager.Service.exe")) { throw 'Extract the complete build package, then run Install.ps1 next to app/.' }
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ($InstallDirectory.Length -lt 10 -or $InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory).TrimEnd('\')) { throw 'Invalid installation path.' }
if (Get-Service ExplorerManager -ErrorAction SilentlyContinue) {
    Write-Warning 'Updating the service clears all RAM recovery copies.'
    Stop-Service ExplorerManager -ErrorAction Stop
    (Get-Service ExplorerManager).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
}
if (Get-Service ExplorerManagerSensors -ErrorAction SilentlyContinue) { Stop-Service ExplorerManagerSensors }
New-Item $InstallDirectory -ItemType Directory -Force | Out-Null
& icacls.exe $InstallDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Executable ACL configuration failed.' }
Copy-Item "$source/*" $InstallDirectory -Recurse -Force
Set-Content (Join-Path $InstallDirectory '.explorermanager-install.json') (@{Path=$InstallDirectory;Version='1.0.0'} | ConvertTo-Json) -Encoding UTF8
$serviceExe = Join-Path $InstallDirectory 'Service\ExplorerManager.Service.exe'
if (-not (Get-Service ExplorerManager -ErrorAction SilentlyContinue)) {
    & sc.exe create ExplorerManager "binPath= ""$serviceExe""" 'start= delayed-auto' 'obj= NT SERVICE\ExplorerManager' 'DisplayName= Explorer Manager'
    if ($LASTEXITCODE -ne 0) { throw 'Service creation failed.' }
} else {
    & sc.exe config ExplorerManager "binPath= ""$serviceExe""" 'start= delayed-auto' 'obj= NT SERVICE\ExplorerManager'
    if ($LASTEXITCODE -ne 0) { throw 'Service update failed.' }
}
& sc.exe sidtype ExplorerManager unrestricted | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service SID configuration failed.' }
& icacls.exe $InstallDirectory /grant 'NT SERVICE\ExplorerManager:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service executable access configuration failed.' }
$eventGroup=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-573')
if (-not (Get-LocalGroupMember -SID $eventGroup | Where-Object { $_.Name -eq 'NT SERVICE\ExplorerManager' })) {
    Add-LocalGroupMember -SID $eventGroup -Member 'NT SERVICE\ExplorerManager'
}
$data = Join-Path $env:ProgramData 'ExplorerManager'
New-Item $data -ItemType Directory -Force | Out-Null
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' 'NT SERVICE\ExplorerManager:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Data ACL configuration failed.' }
if (-not [Diagnostics.EventLog]::SourceExists('ExplorerManager')) { New-EventLog -LogName Application -Source ExplorerManager }
& sc.exe failure ExplorerManager 'reset= 86400' 'actions= restart/10000/restart/30000/restart/60000' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service recovery configuration failed.' }
if (-not $NoHardwareSensors) {
    $sensorExe=Join-Path $InstallDirectory 'Service\ExplorerManager.SensorHost.exe'
    if (-not (Get-Service ExplorerManagerSensors -ErrorAction SilentlyContinue)) {
        & sc.exe create ExplorerManagerSensors "binPath= ""$sensorExe"" --service" 'start= auto' 'obj= LocalSystem' 'DisplayName= Explorer Manager Hardware Sensors'
    } else {
        & sc.exe config ExplorerManagerSensors "binPath= ""$sensorExe"" --service" 'start= auto' 'obj= LocalSystem'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Read-only hardware sensor service configuration failed.' }
    & sc.exe failure ExplorerManagerSensors 'reset= 86400' 'actions= restart/10000/restart/30000/restart/60000' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Sensor recovery configuration failed.' }
    Start-Service ExplorerManagerSensors
}
if (-not $NoAutoStart) {
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute "$InstallDirectory\Desktop\ExplorerManager.Desktop.exe" -Argument '--tray'
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName 'ExplorerManager.Desktop' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
}
Start-Service ExplorerManager
(Get-Service ExplorerManager).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
if (-not $NoStartUi) { Start-Process "$InstallDirectory\Desktop\ExplorerManager.Desktop.exe" -Verb RunAs }
Write-Host 'Installed. Automatic deletion remains disabled until a policy is saved and enabled.'
Write-Host 'Grant-ManagedRoot.ps1 explicitly grants the service access to managed roots.'
