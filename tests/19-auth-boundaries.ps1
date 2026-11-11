param(
    [string]$Configuration = 'Debug',
    [ValidateSet('All', 'MalformedAuth', 'NullEntries')]
    [string]$Mode = 'All'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$clientExe = Join-Path $repoRoot "src/SSHClient/bin/$Configuration/net452/SSHC.exe"
$serverDir = Join-Path $repoRoot "src/SSHServer/bin/$Configuration/net452"
$serverExe = Join-Path $serverDir 'SSHServer.exe'
if (!(Test-Path -LiteralPath $clientExe) -or !(Test-Path -LiteralPath $serverExe)) {
    throw 'Build WinSimpleSSH.sln before running this test.'
}

function Wait-Until([scriptblock]$Condition, [string]$Message) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Message
}

function Read-TestLog([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return '' }
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    $reader = New-Object IO.StreamReader($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Stop-TestProcess($Process) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    $cleanup = Start-Process -FilePath "$env:SystemRoot/System32/taskkill.exe" -ArgumentList @('/PID', $Process.Id, '/T', '/F') -WindowStyle Hidden -PassThru
    if (!$cleanup.WaitForExit(5000)) { $cleanup.Kill(); throw 'Process cleanup timed out.' }
    if (!$Process.WaitForExit(5000)) { throw 'Test process did not exit.' }
}

function Start-TestServer($Users) {
    @{
        port = $port
        ipWhitelist = @('127.0.0.1')
        users = $Users
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testDir 'server.json') -Encoding UTF8
    $script:server = Start-Process -FilePath (Join-Path $testDir 'SSHServer.exe') -ArgumentList '--service-server' -WorkingDirectory $testDir -WindowStyle Hidden -PassThru
    $null = $script:server.Handle
    Wait-Until {
        if ($script:server.HasExited) { throw 'FAIL: server exited during startup.' }
        $socket = New-Object Net.Sockets.TcpClient
        try { $socket.Connect('127.0.0.1', $port); return $true } catch { return $false } finally { $socket.Close() }
    } 'Server did not start listening.'
}

function Expect-Login([int]$ExitCode) {
    $script:client = Start-Process -FilePath $clientExe -ArgumentList @(
        'exec', '127.0.0.1', '--port', $port, '-u', 'boundary-user', '-p', 'test-only-7x3', '-q', '"echo auth_boundary_ok"'
    ) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $script:client.Handle
    if (!$script:client.WaitForExit(10000)) { throw 'Login timed out.' }
    if ($script:client.ExitCode -ne $ExitCode) { throw ('Expected exit ' + $ExitCode + ', got ' + $script:client.ExitCode) }
    if ($ExitCode -eq 0 -and !(Read-TestLog $stdout).Contains('auth_boundary_ok')) { throw 'Authenticated command output was lost.' }
}

$testDir = Join-Path ([IO.Path]::GetTempPath()) ('WinSimpleSSH-auth-boundary-' + [Guid]::NewGuid().ToString('N'))
$server = $null
$client = $null
try {
    New-Item -ItemType Directory -Path $testDir | Out-Null
    Copy-Item -LiteralPath $serverExe, ($serverExe + '.config') -Destination $testDir
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $stdout = Join-Path $testDir 'client.stdout.log'
    $stderr = Join-Path $testDir 'client.stderr.log'
    $logPath = Join-Path $testDir ('log/server_' + (Get-Date).ToString('yyyyMMdd') + '.log')
    $validUser = @{ username = 'boundary-user'; password = 'test-only-7x3' }

    if ($Mode -ne 'NullEntries') {
        [Reflection.Assembly]::LoadFrom($clientExe) | Out-Null
        $probeDll = Join-Path $testDir 'AuthRequestProbe.dll'
        & "$env:SystemRoot/Microsoft.NET/Framework/v4.0.30319/csc.exe" /nologo /target:library ("/out:" + $probeDll) ("/reference:" + $clientExe) (Join-Path $PSScriptRoot 'AuthRequestProbe.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Could not compile the raw auth probe.' }
        [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($probeDll)) | Out-Null
        Start-TestServer @($validUser)
        $frames = @(
            @{ Name = 'missing data'; Json = '{"type":0}' },
            @{ Name = 'null data'; Json = '{"type":0,"data":null}' },
            @{ Name = 'empty data'; Json = '{"type":0,"data":""}' },
            @{ Name = 'JSON null payload'; Json = '{"type":0,"data":"null"}' },
            @{ Name = 'invalid JSON payload'; Json = '{"type":0,"data":"{"}' },
            @{ Name = 'missing username'; Json = '{"type":0,"data":"{}"}' }
        )
        $expectedFailures = 0
        foreach ($frame in $frames) {
            [AuthRequestProbe]::ExpectRejection($port, $frame.Json)
            $expectedFailures++
            Wait-Until {
                return @((Read-TestLog $logPath) -split '\r?\n' | Where-Object { $_ -match '\[Auth\].*malformed request from 127\.0\.0\.1:\d+' }).Count -ge $expectedFailures
            } 'Malformed authentication was not logged with its remote endpoint.'
            Write-Output ('PASS: ' + $frame.Name + ' returns failed AuthResponse and an audit entry.')
        }
        $marker = 'injected-auth-record'
        $attemptedPassword = 'auth-log-password-must-not-appear-7x3'
        $payload = @{
            username = "boundary-user`r`n2026/11/11 00:00:00|INFO |$marker"
            password = $attemptedPassword
        } | ConvertTo-Json -Compress
        $frame = @{ type = 0; data = $payload } | ConvertTo-Json -Compress
        [AuthRequestProbe]::ExpectRejection($port, $frame)
        Wait-Until { return (Read-TestLog $logPath).Contains($marker) } 'Crafted username was not audited.'
        $log = Read-TestLog $logPath
        $records = @($log -split '[\r\n]' | Where-Object { $_.Contains($marker) })
        if ($records.Count -ne 1 -or $records[0] -notmatch "^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\|WARN\s*\|\[Auth\].*user='boundary-user.* from 127\.0\.0\.1:\d+\s*$") {
            throw 'FAIL: a username containing CR/LF forged a separate audit-log record.'
        }
        if ($log.Contains($attemptedPassword)) { throw 'FAIL: the attempted password leaked into the audit log.' }
        Write-Output 'PASS: CR/LF in a username stays in one failed-auth record without exposing the password.'
        Expect-Login 0
        Write-Output 'PASS: valid authentication still works after malformed requests.'
        Stop-TestProcess $server
        $server = $null
    }

    if ($Mode -ne 'MalformedAuth') {
        Start-TestServer @($null, $validUser, $null)
        Expect-Login 0
        Wait-Until { return (Read-TestLog $logPath).Contains('Ignoring null user entry') } 'Skipped null user entries were not logged.'
        Write-Output 'PASS: users=[null,valid,null] starts, logs invalid entries, and accepts the valid account.'
        Stop-TestProcess $server
        $server = $null
        Start-TestServer @($null)
        Expect-Login 254
        Write-Output 'PASS: users=[null] starts and promptly rejects authentication.'
    }
} catch {
    foreach ($path in @($stdout, $stderr, $logPath)) { if ($path) { Write-Output (Read-TestLog $path) } }
    throw
} finally {
    $cleanupErrors = @()
    foreach ($process in @($client, $server)) {
        try { Stop-TestProcess $process } catch { $cleanupErrors += $_.Exception.Message }
    }
    try {
        if (Test-Path -LiteralPath $testDir) {
            $resolved = (Resolve-Path -LiteralPath $testDir).Path
            $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
            if (!$resolved.StartsWith($tempRoot + 'WinSimpleSSH-auth-boundary-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test cleanup path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    } catch { $cleanupErrors += $_.Exception.Message }
    if ($cleanupErrors.Count -gt 0) { throw ($cleanupErrors -join '; ') }
}
