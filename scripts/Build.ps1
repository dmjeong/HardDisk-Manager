[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release', [string]$CertificateThumbprint)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$builder = (Get-Command msbuild -ErrorAction Stop).Source
& $builder (Join-Path $repo 'ExplorerManager.sln') /restore /m "/p:Configuration=$Configuration" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
$stage = Join-Path $repo 'artifacts/app'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item "$stage/Service","$stage/Desktop","$stage/SensorHost" -ItemType Directory -Force | Out-Null
Copy-Item "$repo/src/ExplorerManager.Service/bin/$Configuration/net48/*" "$stage/Service" -Recurse -Force
Copy-Item "$repo/src/ExplorerManager.SensorHost/bin/$Configuration/net48/*" "$stage/SensorHost" -Recurse -Force
Get-ChildItem "$repo/src/ExplorerManager.Desktop/bin/$Configuration/net48" -Exclude Setup | Copy-Item -Destination "$stage/Desktop" -Recurse -Force
Copy-Item "$repo/scripts/Install.ps1","$repo/scripts/Uninstall.ps1","$repo/scripts/Grant-ManagedRoot.ps1" "$repo/artifacts" -Force
Copy-Item "$repo/README.md","$repo/THIRD-PARTY-NOTICES.md" "$repo/artifacts" -Force
if (Test-Path "$repo/artifacts/licenses") { Remove-Item "$repo/artifacts/licenses" -Recurse -Force }
Copy-Item "$repo/licenses" "$repo/artifacts/licenses" -Recurse -Force
if ($CertificateThumbprint) {
    $sign = (Get-Command signtool -ErrorAction Stop).Source
    Get-ChildItem $stage -Filter '*.exe' -Recurse | ForEach-Object {
        & $sign sign /sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $_.FullName
        if ($LASTEXITCODE -ne 0) { throw 'Code signing failed' }
    }
}
$zip = Join-Path $repo 'artifacts/ExplorerManager-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$stage","$repo/artifacts/Install.ps1","$repo/artifacts/Uninstall.ps1","$repo/artifacts/Grant-ManagedRoot.ps1","$repo/artifacts/README.md","$repo/artifacts/THIRD-PARTY-NOTICES.md","$repo/artifacts/licenses" -DestinationPath $zip
Write-Host "Package: $zip"
