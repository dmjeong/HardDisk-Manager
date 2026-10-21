#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$InstallDirectory="$env:ProgramFiles\ExplorerManager", [switch]$NoStartUi, [switch]$NoAutoStart, [switch]$NoHardwareSensors)
$ErrorActionPreference='Stop'
if (-not ('ExplorerManagerInstaller.ServiceConfig' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ExplorerManagerInstaller {
 public static class ServiceConfig {
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenSCManagerW(string machine,string database,uint access);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenServiceW(IntPtr manager,string name,uint access);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateServiceW(IntPtr manager,string name,string display,uint access,uint type,uint start,uint error,string binary,string group,IntPtr tag,string dependencies,string account,string password);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ChangeServiceConfigW(IntPtr service,uint type,uint start,uint error,string binary,string group,IntPtr tag,string dependencies,string account,string password,string display);
  [DllImport("advapi32.dll",SetLastError=true)] static extern bool DeleteService(IntPtr service);
  [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
  public static void Apply(string name,string display,string binary,string account) {
   IntPtr manager=OpenSCManagerW(null,null,3);
   if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
   try {
    bool created=false;
    IntPtr service=OpenServiceW(manager,name,0x10006);
    if(service==IntPtr.Zero) {
     int code=Marshal.GetLastWin32Error(); if(code!=1060) throw new Win32Exception(code);
     // Create stopped/manual, then atomically select the account and automatic start.
     service=CreateServiceW(manager,name,display,0x10006,16,3,1,binary,null,IntPtr.Zero,null,null,null);
     if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
     created=true;
    }
    try {
     if(!ChangeServiceConfigW(service,uint.MaxValue,2,uint.MaxValue,binary,null,IntPtr.Zero,null,account=="LocalSystem"?null:account,"",display))
      throw new Win32Exception(Marshal.GetLastWin32Error());
    } catch { if(created) DeleteService(service); throw; }
    finally { CloseServiceHandle(service); }
   } finally { CloseServiceHandle(manager); }
  }
 }
}
'@
}
function Set-ExplorerService([string]$Name,[string]$Display,[string]$Binary,[string]$Account) {
    [ExplorerManagerInstaller.ServiceConfig]::Apply($Name,$Display,$Binary,$Account)
}
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
Set-ExplorerService 'ExplorerManager' 'Explorer Manager' ('"'+$serviceExe+'"') 'NT SERVICE\ExplorerManager'
& sc.exe config ExplorerManager start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Delayed service startup configuration failed.' }
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
& sc.exe failure ExplorerManager reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service recovery configuration failed.' }
if (-not $NoHardwareSensors) {
    $sensorExe=Join-Path $InstallDirectory 'Service\ExplorerManager.SensorHost.exe'
    Set-ExplorerService 'ExplorerManagerSensors' 'Explorer Manager Hardware Sensors' ('"'+$sensorExe+'" --service') 'LocalSystem'
    & sc.exe failure ExplorerManagerSensors reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
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
