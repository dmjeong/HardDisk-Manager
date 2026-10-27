@echo off
setlocal
set "EXPLORER_MANAGER_SETUP=%~dp0Install.ps1"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -Command "try { $p=Start-Process -FilePath ($env:SystemRoot+'\System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File '+[char]34+$env:EXPLORER_MANAGER_SETUP+[char]34) -Wait -PassThru; exit $p.ExitCode } catch { Write-Host $_.Exception.Message; exit 1 }"
if errorlevel 1 (
  echo Installation failed or elevation was cancelled.
  echo Logs: %%LOCALAPPDATA%%\ExplorerManager\Logs
  pause
  exit /b 1
)
exit /b 0
