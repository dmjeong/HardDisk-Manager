[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$legacy=Join-Path $env:RUNNER_TEMP 'ExplorerManager-legacy'
$zip=Join-Path $env:RUNNER_TEMP 'ExplorerManager-legacy.zip'
$install=Join-Path $env:ProgramFiles 'ExplorerManager-Upgrade'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('ExplorerManager-UI-'+[Guid]::NewGuid().ToString('N'))
$oldUi=$null; $newUi=$null
# Compile the actual shipped 1.0.1 code, including its older installer and Desktop.
& git -C $repo archive --format=zip --output=$zip 85216e6f17c5363562b06bab1fc8f24b92c53cb9
if ($LASTEXITCODE -ne 0) { throw 'Could not extract released 1.0.1 source.' }
Expand-Archive -LiteralPath $zip -DestinationPath $legacy -Force
& "$legacy/scripts/Build.ps1"
try {
    & "$legacy/artifacts/Install.ps1" -InstallDirectory $install -NoStartUi -NoAutoStart -NoHardwareSensors
    New-Item $fixture -ItemType Directory -Force | Out-Null
    & "$legacy/scripts/Grant-ManagedRoot.ps1" -Path $fixture
    $oldUi=Start-Process "$install/Desktop/ExplorerManager.Desktop.exe" -ArgumentList '--tray' -PassThru
    Start-Sleep -Seconds 4
    if ($oldUi.HasExited) { throw 'The old installed Desktop must be running to reproduce locked binary updates.' }
    $newUi=Start-Process "$repo/src/ExplorerManager.Desktop/bin/Release/net48/ExplorerManager.Desktop.exe" -ArgumentList @('--upgrade-smoke',('"'+$fixture+'"'),'--test-output',('"'+(Join-Path $repo 'artifacts/desktop-upgrade.log')+'"')) -PassThru
    if (-not $newUi.WaitForExit(180000)) { $newUi.Kill(); throw 'Upgrade workflow timed out.' }
    if ($newUi.ExitCode -ne 0) { throw "Upgrade workflow failed: $($newUi.ExitCode)" }
    if (-not $oldUi.HasExited) { throw 'Installer failed to close the old installed Desktop.' }
    $installedVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo("$install/Service/ExplorerManager.Service.exe").FileVersion
    $expectedVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo("$repo/src/ExplorerManager.Service/bin/Release/net48/ExplorerManager.Service.exe").FileVersion
    if ($installedVersion -ne $expectedVersion) { throw 'Installed service binary version does not match new package.' }
    # Re-running installer while the current UI is running must also work (repair/reinstall).
    $oldUi=Start-Process "$install/Desktop/ExplorerManager.Desktop.exe" -ArgumentList '--tray' -PassThru
    Start-Sleep -Seconds 2
    & "$repo/artifacts/Install.ps1" -InstallDirectory $install -NoStartUi -NoAutoStart
    if (-not $oldUi.HasExited) { throw 'Same-version reinstall did not close the installed Desktop.' }
    Write-Host (Get-Content "$repo/artifacts/desktop-upgrade.log" -Raw)
    Write-Host 'PASS: same-version reinstall with installed Desktop open.'
    # Run the shipped double-click entry point from a path with spaces and Korean characters.
    $launcherFolder=Join-Path $env:RUNNER_TEMP ('Explorer Manager '+[char]0xD55C+[char]0xAE00)
    Expand-Archive -LiteralPath "$repo/artifacts/ExplorerManager-win-x64.zip" -DestinationPath $launcherFolder -Force
    $launcher=Start-Process $env:ComSpec -ArgumentList ('/d /c ""'+$launcherFolder+'\Setup.cmd""') -PassThru
    if (-not $launcher.WaitForExit(90000)) { $launcher.Kill(); throw 'Setup.cmd did not finish.' }
    if ($launcher.ExitCode -ne 0) { throw 'Setup.cmd reported installation failure.' }
    $running=Get-CimInstance Win32_Process -Filter "Name='ExplorerManager.Desktop.exe'" | Where-Object { $_.ExecutablePath -eq "$install\Desktop\ExplorerManager.Desktop.exe" }
    if (-not $running) { throw 'Setup.cmd did not launch the installed Desktop.' }
    Write-Host 'PASS: packaged Setup.cmd from a Unicode/space path, upgrade and installed Desktop launch.'
} finally {
    foreach ($process in @($newUi,$oldUi)) { if ($process -and -not $process.HasExited) { $process.Kill() } }
    if (Test-Path "$install/.explorermanager-install.json") { & "$repo/scripts/Uninstall.ps1" -InstallDirectory $install }
    Copy-Item "$env:LOCALAPPDATA/ExplorerManager/Logs/*.log" "$repo/artifacts" -ErrorAction SilentlyContinue
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
}
