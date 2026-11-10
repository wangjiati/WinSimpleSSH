param(
    [string]$Configuration = 'Debug'
)

# Run with: powershell -NoProfile -ExecutionPolicy Bypass -File tests/18-auth-audit-log.ps1
# Verifies two things in an isolated server instance:
#   1. a failed login is written to log/server_*.log with username + endpoint
#   2. the attempted password never appears in the log
# It also checks the weak-credential warning for the documented default admin/admin123.
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
    if (!(Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = New-Object IO.StreamReader($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

$testDir = Join-Path ([IO.Path]::GetTempPath()) ('WinSimpleSSH-authlog-' + [Guid]::NewGuid().ToString('N'))
$server = $null
$client = $null
$wrongPassword = 'wrong-password-9f3c'
try {
    New-Item -ItemType Directory -Path $testDir | Out-Null
    # Copy the whole output directory: Release builds embed dependencies via
    # Costura, plain `dotnet build` output and hand-built binaries do not.
    Get-ChildItem -LiteralPath $serverDir -File | Copy-Item -Destination $testDir

    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    @{
        port = $port
        ipWhitelist = @('127.0.0.1')
        users = @(@{ username = 'admin'; password = 'admin123' })
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

    $logPath = Join-Path $testDir ('log/server_' + (Get-Date).ToString('yyyyMMdd') + '.log')
    Wait-Until { return (Read-TestLog $logPath).Contains('Weak credentials detected') } 'Default password was not reported at startup.'
    $startupLog = Read-TestLog $logPath
    if ($startupLog.Contains('admin123')) {
        throw 'FAIL: the server wrote the configured password into its log.'
    }
    Write-Output 'PASS: startup warns about the default password without logging it.'

    $stdout = Join-Path $testDir 'client.stdout.log'
    $stderr = Join-Path $testDir 'client.stderr.log'
    $client = Start-Process -FilePath $clientExe -ArgumentList @(
        'exec', '127.0.0.1', '--port', $port, '-u', 'admin', '-p', $wrongPassword, '-q', '"echo never_runs"'
    ) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $client.Handle
    if (!$client.WaitForExit(10000)) { throw 'Failed-login client timed out.' }
    if ($client.ExitCode -ne 254) {
        throw ('FAIL: wrong password should exit 254, got ' + $client.ExitCode)
    }

    Wait-Until {
        return (Read-TestLog $logPath).Contains("user='admin'")
    } 'Failed authentication was not written to the server log.'
    $log = Read-TestLog $logPath
    if (!$log.Contains('Authentication failed')) {
        throw 'FAIL: failed authentication log entry is missing its message.'
    }
    if (!$log.Contains('127.0.0.1')) {
        throw 'FAIL: failed authentication log entry has no remote endpoint.'
    }
    if ($log.Contains($wrongPassword)) {
        throw 'FAIL: the attempted password leaked into the server log.'
    }
    Write-Output 'PASS: failed authentication is logged with username and endpoint, without the password.'

    # The server must keep serving after a failed login.
    $client = Start-Process -FilePath $clientExe -ArgumentList @(
        'exec', '127.0.0.1', '--port', $port, '-u', 'admin', '-p', 'admin123', '-q', '"echo auth_after_failure_ok"'
    ) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $client.Handle
    if (!$client.WaitForExit(10000)) { throw 'Post-failure login timed out.' }
    if ($client.ExitCode -ne 0 -or !(Read-TestLog $stdout).Contains('auth_after_failure_ok')) {
        throw ('FAIL: server stopped accepting logins after a failed attempt (exit ' + $client.ExitCode + ').')
    }
    Write-Output 'PASS: server still accepts a valid login after the failed attempt.'
} catch {
    if ($stdout -and (Test-Path -LiteralPath $stdout)) { Write-Output (Read-TestLog $stdout) }
    if ($stderr -and (Test-Path -LiteralPath $stderr)) { Write-Output (Read-TestLog $stderr) }
    if ($logPath -and (Test-Path -LiteralPath $logPath)) { Write-Output (Read-TestLog $logPath) }
    throw
} finally {
    Stop-TestProcessTree $client
    Stop-TestProcessTree $server
    if (Test-Path -LiteralPath $testDir) {
        $resolvedTestDir = (Resolve-Path -LiteralPath $testDir).Path
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (!$resolvedTestDir.StartsWith($tempRoot + 'WinSimpleSSH-authlog-', [StringComparison]::OrdinalIgnoreCase)) {
            throw ('Refusing to remove unexpected test directory: ' + $resolvedTestDir)
        }
        Remove-Item -LiteralPath $resolvedTestDir -Recurse -Force
    }
}
