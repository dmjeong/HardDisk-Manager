[CmdletBinding()]
param([string]$InstallDirectory="$env:ProgramFiles\ExplorerManager-CI")
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
& "$repo/artifacts/Install.ps1" -InstallDirectory $InstallDirectory -NoStartUi -NoAutoStart
try {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','ExplorerManager.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(15000)
        $id=[Guid]::NewGuid().ToString('N')
        $json=@{Version=1;Id=$id;Command='Dashboard';Data=@{}} | ConvertTo-Json -Compress
        $payload=[Text.Encoding]::UTF8.GetBytes($json)
        $writer=[IO.BinaryWriter]::new($pipe,[Text.Encoding]::UTF8,$true)
        $writer.Write([int]$payload.Length);$writer.Write($payload);$writer.Flush()
        $reader=[IO.BinaryReader]::new($pipe,[Text.Encoding]::UTF8,$true)
        $length=$reader.ReadInt32()
        if ($length -lt 1 -or $length -gt 2097152) { throw 'Invalid IPC response size.' }
        $response=[Text.Encoding]::UTF8.GetString($reader.ReadBytes($length)) | ConvertFrom-Json
        if (-not $response.Success -or $response.Id -ne $id) { throw "IPC probe failed: $($response.Error)" }
        if ($response.Data.Policies.Count -ne 0) { throw 'Fresh installation has unexpected policies.' }
    } finally { $pipe.Dispose() }
    $png=Join-Path $repo 'artifacts/dashboard.png'
    $ui=Start-Process "$InstallDirectory/Desktop/ExplorerManager.Desktop.exe" -ArgumentList @('--smoke','--screenshot',('"'+$png+'"')) -PassThru
    if (-not $ui.WaitForExit(45000)) { $ui.Kill(); throw 'WPF smoke test timed out.' }
    if ($ui.ExitCode -ne 0) { throw "WPF smoke test failed: $($ui.ExitCode)" }
    if (-not (Test-Path $png)) { throw 'WPF screenshot was not created.' }
} finally {
    & "$repo/artifacts/Uninstall.ps1" -InstallDirectory $InstallDirectory
    Copy-Item "$env:ProgramData/ExplorerManager/Data/*.log" "$repo/artifacts" -ErrorAction SilentlyContinue
}
