param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$clientExe = Join-Path $repoRoot "src/SSHClient/bin/$Configuration/net452/SSHC.exe"
$serverExe = Join-Path $repoRoot "src/SSHServer/bin/$Configuration/net452/SSHServer.exe"
if (!(Test-Path -LiteralPath $clientExe) -or !(Test-Path -LiteralPath $serverExe)) {
    throw 'Build WinSimpleSSH.sln before running this test.'
}

$testDir = Join-Path ([IO.Path]::GetTempPath()) ('WinSimpleSSH-input-' + [Guid]::NewGuid().ToString('N'))
$server = $null
$probe = $null
try {
    New-Item -ItemType Directory -Path $testDir | Out-Null
    Copy-Item -LiteralPath $serverExe, ($serverExe + '.config') -Destination $testDir
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    @{
        port = $port
        ipWhitelist = @('127.0.0.1')
        users = @(@{ username = 'input-test'; password = 'test-only' })
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $testDir 'server.json') -Encoding UTF8
    $server = Start-Process -FilePath (Join-Path $testDir 'SSHServer.exe') -WorkingDirectory $testDir -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ($server.HasExited) { throw 'Test server exited before listening.' }
        $socket = New-Object Net.Sockets.TcpClient
        try { $socket.Connect('127.0.0.1', $port); break } catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Test server did not start listening.' }
            Start-Sleep -Milliseconds 50
        } finally { $socket.Close() }
    } while ($true)

    $probeExe = Join-Path $testDir 'ConsoleInputProbe.exe'
    & "$env:SystemRoot/Microsoft.NET/Framework/v4.0.30319/csc.exe" /nologo /target:exe ("/out:" + $probeExe) (Join-Path $PSScriptRoot 'ConsoleInputProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the console input probe.' }
    $resultFile = Join-Path $testDir 'results.txt'
    $probe = Start-Process -FilePath $probeExe -ArgumentList @(('"' + $clientExe + '"'), $port, ('"' + $resultFile + '"')) -WindowStyle Hidden -PassThru
    $null = $probe.Handle
    if (!$probe.WaitForExit(30000)) { throw 'Console input probe timed out.' }
    if (Test-Path -LiteralPath $resultFile) { Get-Content -LiteralPath $resultFile -Encoding UTF8 }
    if ($probe.ExitCode -ne 0) { throw ('Console input regression failed (exit code ' + $probe.ExitCode + ').') }
} finally {
    $cleanupErrors = @()
    foreach ($process in @($probe, $server)) {
        try {
            if ($null -ne $process -and !$process.HasExited) {
                $cleanup = Start-Process -FilePath "$env:SystemRoot/System32/taskkill.exe" -ArgumentList @('/PID', $process.Id, '/T', '/F') -WindowStyle Hidden -PassThru
                if (!$cleanup.WaitForExit(5000)) { $cleanup.Kill(); throw 'Process cleanup timed out.' }
                if (!$process.WaitForExit(5000)) { throw 'Test process did not exit during cleanup.' }
            }
        } catch {
            $cleanupErrors += $_.Exception.Message
        }
    }
    try {
        if (Test-Path -LiteralPath $testDir) {
            $resolvedTestDir = (Resolve-Path -LiteralPath $testDir).Path
            $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
            if (!$resolvedTestDir.StartsWith($tempRoot + 'WinSimpleSSH-input-', [StringComparison]::OrdinalIgnoreCase)) {
                throw ('Refusing to remove unexpected test directory: ' + $resolvedTestDir)
            }
            Remove-Item -LiteralPath $resolvedTestDir -Recurse -Force
        }
    } catch {
        $cleanupErrors += $_.Exception.Message
    }
    if ($cleanupErrors.Count -gt 0) { throw ('Test cleanup failed: ' + ($cleanupErrors -join '; ')) }
}
