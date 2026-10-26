[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$runner=Get-Command vstest.console.exe -ErrorAction SilentlyContinue
if ($runner) { $testConsole=$runner.Source }
else {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path $vswhere) {
        $testConsole=& $vswhere -latest -products '*' -find 'Common7/IDE/CommonExtensions/Microsoft/TestWindow/vstest.console.exe' | Select-Object -First 1
    }
}
if (-not $testConsole) { throw 'Visual Studio Test Explorer / Test Tools are required to run the .NET Framework tests.' }
$assembly=Join-Path $repo "tests/ExplorerManager.Tests/bin/$Configuration/net48/ExplorerManager.Tests.dll"
if (-not (Test-Path $assembly)) { throw 'Build the solution before running scripts/Test.ps1.' }
& $testConsole $assembly /Platform:x64 '/Framework:.NETFramework,Version=v4.8' "/TestAdapterPath:$(Split-Path $assembly -Parent)" "/ResultsDirectory:$repo/artifacts/TestResults" '/Logger:trx;LogFileName=windows.trx' '/Logger:console;verbosity=detailed' '/Blame' "/Settings:$PSScriptRoot/tests.runsettings" "/Diag:$repo/artifacts/test-platform.log"
if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
