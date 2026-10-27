[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$desktop=Join-Path $repo 'src/ExplorerManager.Desktop/bin/Release/net48/ExplorerManager.Desktop.exe'
$output=Join-Path $repo 'artifacts'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('ExplorerManager-UI-'+[Guid]::NewGuid().ToString('N'))
$install=Join-Path $env:ProgramFiles 'ExplorerManager'
if (Get-Service ExplorerManager -ErrorAction SilentlyContinue) { throw 'The first-run workflow requires a clean CI machine without an installed ExplorerManager service.' }
New-Item $fixture -ItemType Directory -Force | Out-Null
$ui=$null
try {
    $offline=Start-Process $desktop -ArgumentList @('--offline-smoke','--test-output',('"'+(Join-Path $output 'desktop-offline.log')+'"')) -PassThru
    if (-not $offline.WaitForExit(40000)) { $offline.Kill(); throw 'X did not close the offline desktop process.' }
    if ($offline.ExitCode -ne 0) { throw "Offline desktop workflow failed: $($offline.ExitCode)" }
    if (-not (Test-Path (Join-Path (Split-Path $desktop) 'Setup/app/Service/ExplorerManager.Service.exe'))) { throw 'The F5 output does not contain its service setup files.' }
    $ui=Start-Process $desktop -ArgumentList @('--workflow-smoke',('"'+$fixture+'"'),'--test-output',('"'+(Join-Path $output 'desktop-workflow.log')+'"')) -PassThru
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    do {
        if ($ui.HasExited) { throw "Desktop setup exited early: $($ui.ExitCode)" }
        $service=Get-Service ExplorerManager -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') { break }
        if ($deadline.Elapsed.TotalSeconds -gt 100) { throw 'The UI setup button did not install/start the service.' }
        Start-Sleep -Milliseconds 300
    } while ($true)
    & "$repo/scripts/Grant-ManagedRoot.ps1" -Path $fixture
    Set-Content (Join-Path $fixture 'expired.txt') 'Real UI workflow fixture'
    (Get-Item (Join-Path $fixture 'expired.txt')).LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-60)
    Set-Content (Join-Path $fixture '.ready') 'Ready'
    if (-not $ui.WaitForExit(100000)) { $ui.Kill(); throw 'Desktop policy workflow or X close timed out.' }
    if ($ui.ExitCode -ne 0) { throw "Desktop policy workflow failed: $($ui.ExitCode)" }
    Write-Host (Get-Content (Join-Path $output 'desktop-offline.log') -Raw)
    Write-Host (Get-Content (Join-Path $output 'desktop-workflow.log') -Raw)
} finally {
    if ($ui -and -not $ui.HasExited) { $ui.Kill() }
    if (Get-Service ExplorerManager -ErrorAction SilentlyContinue) { & "$repo/scripts/Uninstall.ps1" -InstallDirectory $install }
    Copy-Item "$env:ProgramData/ExplorerManager/Data/*.log" $output -ErrorAction SilentlyContinue
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
}
