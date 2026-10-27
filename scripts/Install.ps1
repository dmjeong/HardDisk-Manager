#Requires -RunAsAdministrator
#Requires -PSEdition Desktop
[CmdletBinding()]
param([string]$InstallDirectory="$env:ProgramFiles\ExplorerManager", [switch]$NoStartUi, [switch]$NoAutoStart, [switch]$NoHardwareSensors)
$ErrorActionPreference='Stop'
$logDirectory=Join-Path $env:LOCALAPPDATA 'ExplorerManager\Logs'
New-Item $logDirectory -ItemType Directory -Force | Out-Null
$logPath=Join-Path $logDirectory ('install-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.log')
Start-Transcript -Path $logPath -Force | Out-Null
$stage=$null; $replacing=$false; $rollbackFailed=$false; $wasRunning=@{}
try {
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
     if(!ChangeServiceConfigW(service,uint.MaxValue,2,uint.MaxValue,binary,null,IntPtr.Zero,null,account=="LocalSystem"?null:account,account.StartsWith("NT SERVICE\\",StringComparison.OrdinalIgnoreCase)?null:"",display))
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

function Stop-ExplorerService([string]$Name) {
    $service=Get-Service $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        if ($service.Status -ne 'StopPending') { $service.Stop() }
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40))
    }
}
$source=Join-Path $PSScriptRoot 'app'
foreach ($file in @('Service\ExplorerManager.Service.exe','Service\ExplorerManager.Infrastructure.dll','Desktop\ExplorerManager.Desktop.exe','Desktop\ExplorerManager.Client.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file))) { throw "Incomplete package: $file. Extract the whole ZIP before running Setup.cmd." }
}
if (-not $NoHardwareSensors -and -not (Test-Path "$source/SensorHost/ExplorerManager.SensorHost.exe")) { throw 'SensorHost files are missing from the package.' }
# Keep the actual installation location when updating a custom installation.
if (-not $PSBoundParameters.ContainsKey('InstallDirectory')) {
    $existing=Get-CimInstance Win32_Service -Filter "Name='ExplorerManager'"
    if ($existing) {
        $image=$existing.PathName
        if ($image -match '^"([^"]+)"') { $image=$Matches[1] }
        if ([IO.Path]::GetFileName($image) -ne 'ExplorerManager.Service.exe') { throw 'Existing service image path is not an Explorer Manager executable.' }
        $InstallDirectory=Split-Path (Split-Path $image -Parent) -Parent
    }
}
$InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$trustedRoot=[Environment]::GetFolderPath('ProgramFiles').TrimEnd('\')
if (-not $InstallDirectory.StartsWith($trustedRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Install the service under a protected Program Files subdirectory.' }
if ((Test-Path $InstallDirectory) -and (Get-ChildItem -LiteralPath $InstallDirectory -Force) -and -not (Test-Path "$InstallDirectory/.explorermanager-install.json")) { throw 'Installation directory must be empty or an existing Explorer Manager installation.' }
$checkPath=$InstallDirectory
while ($checkPath.Length -gt 3) {
    if ((Test-Path -LiteralPath $checkPath) -and ((Get-Item -LiteralPath $checkPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse installation paths are not allowed.' }
    $checkPath=Split-Path $checkPath -Parent
}
$packageVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'Service\ExplorerManager.Service.exe')).FileVersion
$installedExe=Join-Path $InstallDirectory 'Service\ExplorerManager.Service.exe'
if ((Test-Path $installedExe) -and ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).FileVersion -gt [version]$packageVersion)) { throw 'This package is older than the installed service. Download the matching or newer package.' }
New-Item $InstallDirectory -ItemType Directory -Force | Out-Null
& icacls.exe $InstallDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Executable ACL configuration failed.' }
$stage=Join-Path $InstallDirectory ('.update-'+[Guid]::NewGuid().ToString('N'))
New-Item "$stage/app","$stage/previous" -ItemType Directory -Force | Out-Null
Copy-Item "$source/*" "$stage/app" -Recurse -Force
foreach ($part in @('Desktop','Service','SensorHost')) {
    if (Test-Path "$InstallDirectory/$part") { Copy-Item "$InstallDirectory/$part" "$stage/previous" -Recurse -Force }
}
foreach ($part in @('Grant-ManagedRoot.ps1','.explorermanager-install.json')) {
    if (Test-Path "$InstallDirectory/$part") { Copy-Item "$InstallDirectory/$part" "$stage/previous" -Force }
}
foreach ($name in @('ExplorerManager','ExplorerManagerSensors')) {
    $service=Get-Service $name -ErrorAction SilentlyContinue
    $wasRunning[$name]=($service -and $service.Status -eq 'Running')
}
# Only close our installed UI. Other programs and source-build/Desktop instances are untouched.
$desktopPath=Join-Path $InstallDirectory 'Desktop\ExplorerManager.Desktop.exe'
foreach ($instance in @(Get-CimInstance Win32_Process -Filter "Name='ExplorerManager.Desktop.exe'" | Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath,$desktopPath,[StringComparison]::OrdinalIgnoreCase) })) {
    $process=Get-Process -Id $instance.ProcessId -ErrorAction SilentlyContinue
    if ($process) {
        Write-Warning 'Closing the installed Desktop for update. Unsaved UI edits will be discarded.'
        $null=$process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) { $process.Kill(); if (-not $process.WaitForExit(5000)) { throw 'Installed Desktop did not exit.' } }
    }
}
if (Get-Service ExplorerManager -ErrorAction SilentlyContinue) { Write-Warning 'Restarting the service clears RAM recovery copies. Saved settings, policies and history are retained.' }
Stop-ExplorerService 'ExplorerManager'
Stop-ExplorerService 'ExplorerManagerSensors'
$replacing=$true
foreach ($part in @('Desktop','Service','SensorHost')) {
    if (Test-Path "$InstallDirectory/$part") { Remove-Item "$InstallDirectory/$part" -Recurse -Force }
    if (Test-Path "$stage/app/$part") { Copy-Item "$stage/app/$part" $InstallDirectory -Recurse -Force }
}
if (Test-Path "$PSScriptRoot/Grant-ManagedRoot.ps1") { Copy-Item "$PSScriptRoot/Grant-ManagedRoot.ps1" "$InstallDirectory/Grant-ManagedRoot.ps1" -Force }
Set-Content (Join-Path $InstallDirectory '.explorermanager-install.json') (@{Path=$InstallDirectory;Version=$packageVersion} | ConvertTo-Json) -Encoding UTF8
$serviceExe=Join-Path $InstallDirectory 'Service\ExplorerManager.Service.exe'
Set-ExplorerService 'ExplorerManager' 'Explorer Manager' ('"'+$serviceExe+'"') 'NT SERVICE\ExplorerManager'
& sc.exe config ExplorerManager start= delayed-auto | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Delayed service startup configuration failed.' }
& sc.exe sidtype ExplorerManager unrestricted | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service SID configuration failed.' }
& icacls.exe $InstallDirectory /grant 'NT SERVICE\ExplorerManager:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service executable access configuration failed.' }
foreach ($groupSid in @('S-1-5-32-573','S-1-5-32-558')) {
    try {
        $group=[Security.Principal.SecurityIdentifier]::new($groupSid)
        if (-not (Get-LocalGroupMember -SID $group | Where-Object { $_.Name -eq 'NT SERVICE\ExplorerManager' })) { Add-LocalGroupMember -SID $group -Member 'NT SERVICE\ExplorerManager' }
    } catch { Write-Warning "Optional event/performance group membership failed; native CPU/volume telemetry remains available. $($_.Exception.Message)" }
}
$data=Join-Path $env:ProgramData 'ExplorerManager'
New-Item $data -ItemType Directory -Force | Out-Null
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' 'NT SERVICE\ExplorerManager:(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Data ACL configuration failed.' }
if (-not [Diagnostics.EventLog]::SourceExists('ExplorerManager')) { New-EventLog -LogName Application -Source ExplorerManager }
& sc.exe failure ExplorerManager reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Service recovery configuration failed.' }
if (-not $NoHardwareSensors) {
    try {
        $sensorExe=Join-Path $InstallDirectory 'SensorHost\ExplorerManager.SensorHost.exe'
        Set-ExplorerService 'ExplorerManagerSensors' 'Explorer Manager Hardware Sensors' ('"'+$sensorExe+'" --service') 'LocalSystem'
        & sc.exe failure ExplorerManagerSensors reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Sensor recovery configuration failed.' }
        Start-Service ExplorerManagerSensors
        (Get-Service ExplorerManagerSensors).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    } catch { Write-Warning "Hardware sensor host is unavailable. The core service still starts; unavailable temperatures will be reported. $($_.Exception.Message)" }
} elseif (Get-Service ExplorerManagerSensors -ErrorAction SilentlyContinue) {
    & sc.exe config ExplorerManagerSensors start= disabled | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not disable hardware sensors.' }
}
if (-not $NoAutoStart) {
    try {
        $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
        $action=New-ScheduledTaskAction -Execute "$InstallDirectory\Desktop\ExplorerManager.Desktop.exe" -Argument '--tray'
        $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        Register-ScheduledTask -TaskName 'ExplorerManager.Desktop' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
    } catch { Write-Warning "Login auto-start could not be registered. Start Desktop manually. $($_.Exception.Message)" }
}
Start-Service ExplorerManager
(Get-Service ExplorerManager).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
$replacing=$false
Write-Host "Installed version $packageVersion at $InstallDirectory. Policies and history were preserved."
if (-not $NoStartUi) { Start-Process "$InstallDirectory\Desktop\ExplorerManager.Desktop.exe" -Verb RunAs }
} catch {
    Write-Warning $_.Exception.ToString()
    if ($replacing) {
        try {
            Stop-ExplorerService 'ExplorerManager'; Stop-ExplorerService 'ExplorerManagerSensors'
            foreach ($part in @('Desktop','Service','SensorHost','Grant-ManagedRoot.ps1','.explorermanager-install.json')) {
                if (Test-Path "$stage/previous/$part") {
                    if (Test-Path "$InstallDirectory/$part") { Remove-Item "$InstallDirectory/$part" -Recurse -Force }
                    Copy-Item "$stage/previous/$part" $InstallDirectory -Recurse -Force
                }
            }
            Write-Warning 'Previous binaries were restored.'
        } catch { $rollbackFailed=$true; Write-Warning "Rollback failed. Backup retained at $stage. $($_.Exception.Message)" }
    }
    foreach ($name in @('ExplorerManagerSensors','ExplorerManager')) {
        if ($wasRunning[$name]) { try { Start-Service $name -ErrorAction Stop } catch { Write-Warning $_.Exception.Message } }
    }
    throw
} finally {
    if ($stage -and -not $rollbackFailed -and (Test-Path $stage)) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "Installation log: $logPath"
    Stop-Transcript | Out-Null
}
