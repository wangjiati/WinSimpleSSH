param(
    [string]$Configuration = 'Debug'
)

# Run with: powershell -NoProfile -ExecutionPolicy Bypass -File tests/17-interactive-disconnect.ps1
# Uses a real console without sending any keys, and an isolated local server.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$clientExe = Join-Path $repoRoot "src/SSHClient/bin/$Configuration/net452/SSHC.exe"
$serverDir = Join-Path $repoRoot "src/SSHServer/bin/$Configuration/net452"
$serverExe = Join-Path $serverDir 'SSHServer.exe'
if (!(Test-Path -LiteralPath $clientExe) -or !(Test-Path -LiteralPath $serverExe)) {
    throw 'Build WinSimpleSSH.sln before running this test.'
}

function Wait-Until([scriptblock]$Condition, [string]$FailureMessage) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $FailureMessage
}

function Stop-TestProcessTree($Process) {
    if ($null -ne $Process -and !$Process.HasExited) {
        & "$env:SystemRoot/System32/taskkill.exe" /PID $Process.Id /T /F | Out-Null
        $Process.WaitForExit()
    }
}

function Read-TestLog([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = New-Object IO.StreamReader($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

$testDir = Join-Path ([IO.Path]::GetTempPath()) ('WinSimpleSSH-disconnect-' + [Guid]::NewGuid().ToString('N'))
$server = $null
$client = $null
try {
    New-Item -ItemType Directory -Path $testDir | Out-Null
    Copy-Item -LiteralPath $serverExe -Destination $testDir
    Copy-Item -LiteralPath ($serverExe + '.config') -Destination $testDir

    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    @{
        port = $port
        ipWhitelist = @('127.0.0.1')
        users = @(@{ username = 'disconnect-test'; password = 'test-only' })
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $testDir 'server.json') -Encoding UTF8

    $server = Start-Process -FilePath (Join-Path $testDir 'SSHServer.exe') -WorkingDirectory $testDir -WindowStyle Hidden -PassThru
    Wait-Until {
        if ($server.HasExited) { throw 'Test server exited before listening.' }
        $probe = New-Object Net.Sockets.TcpClient
        try {
            $probe.Connect('127.0.0.1', $port)
            return $true
        } catch {
            return $false
        } finally {
            $probe.Close()
        }
    } 'Test server did not start listening.'

    $stdout = Join-Path $testDir 'client.stdout.log'
    $stderr = Join-Path $testDir 'client.stderr.log'
    # IsConnected is also used by non-interactive commands; verify that path
    # against the same server before exercising the interactive disconnect.
    $client = Start-Process -FilePath $clientExe -ArgumentList @(
        'exec', '127.0.0.1', '--port', $port, '-u', 'disconnect-test', '-p', 'test-only', '-q', '"echo disconnect_smoke_ok"'
    ) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $client.Handle
    if (!$client.WaitForExit(10000)) { throw 'Non-interactive exec timed out.' }
    if ($client.ExitCode -ne 0 -or !(Read-TestLog $stdout).Contains('disconnect_smoke_ok')) {
        throw ('Non-interactive exec failed before the disconnect test (exit code: ' + $client.ExitCode + ').')
    }
    Write-Output 'PASS: non-interactive exec still completes successfully.'

    # Let cmd redirect only the output while preserving a real console input
    # handle. Start-Process redirection in Windows PowerShell redirects stdin too.
    $clientCommand = '""{0}" connect 127.0.0.1 --port {1} -u disconnect-test -p test-only >"{2}" 2>"{3}""' -f $clientExe, $port, $stdout, $stderr
    $client = Start-Process -FilePath $env:ComSpec -ArgumentList @('/d', '/s', '/c', $clientCommand) -WindowStyle Hidden -PassThru
    Wait-Until {
        if ($client.HasExited) { throw ('Client exited before the prompt: ' + (Read-TestLog $stderr)) }
        return (Read-TestLog $stdout).Contains('ssh> ')
    } 'Client did not reach the interactive prompt.'

    Stop-TestProcessTree $server
    Wait-Until {
        if ($client.HasExited -and !(Read-TestLog $stderr).Contains('Disconnected from server.')) {
            throw 'Client exited without detecting the disconnect.'
        }
        return (Read-TestLog $stderr).Contains('Disconnected from server.')
    } 'Client did not detect the server disconnect.'
    if (!$client.WaitForExit(2000)) {
        throw 'FAIL: client stayed blocked after disconnect without keyboard input.'
    }
    if ($client.ExitCode -ne 0) {
        throw ('FAIL: client exited with code ' + $client.ExitCode + ': ' + (Read-TestLog $stderr))
    }
    Write-Output 'PASS: interactive client exits after server disconnect without any keyboard input.'
} catch {
    if ($stdout -and (Test-Path -LiteralPath $stdout)) { Write-Output (Read-TestLog $stdout) }
    if ($stderr -and (Test-Path -LiteralPath $stderr)) { Write-Output (Read-TestLog $stderr) }
    throw
} finally {
    Stop-TestProcessTree $client
    Stop-TestProcessTree $server
    if (Test-Path -LiteralPath $testDir) {
        $resolvedTestDir = (Resolve-Path -LiteralPath $testDir).Path
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (!$resolvedTestDir.StartsWith($tempRoot + 'WinSimpleSSH-disconnect-', [StringComparison]::OrdinalIgnoreCase)) {
            throw ('Refusing to remove unexpected test directory: ' + $resolvedTestDir)
        }
        Remove-Item -LiteralPath $resolvedTestDir -Recurse -Force
    }
}
