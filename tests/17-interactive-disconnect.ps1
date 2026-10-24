param(
    [string]$Configuration = 'Debug',
    [ValidateSet('Interactive', 'Exec', 'ExecJson', 'Upload', 'MessageRace')]
    [string]$Mode = 'Interactive'
)

# Run with: powershell -NoProfile -ExecutionPolicy Bypass -File tests/17-interactive-disconnect.ps1
# Uses an isolated local server. Interactive mode uses a real console without
# sending keys; Exec/ExecJson kill the server from the remote command itself.
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
$raceProbe = $null
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

    if ($Mode -eq 'MessageRace') {
        $clientAssemblyPath = (Resolve-Path -LiteralPath $clientExe).Path
        [Reflection.Assembly]::LoadFrom($clientAssemblyPath) | Out-Null
        $probeAssemblyPath = Join-Path $testDir 'DisconnectRaceProbe.dll'
        & "$env:SystemRoot/Microsoft.NET/Framework/v4.0.30319/csc.exe" /nologo /target:library ("/out:" + $probeAssemblyPath) ("/reference:" + $clientAssemblyPath) (Join-Path $PSScriptRoot 'DisconnectRaceProbe.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Could not compile the disconnect race probe.' }
        [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($probeAssemblyPath)) | Out-Null
        $raceProbe = New-Object DisconnectRaceProbe($port)
        if (!$raceProbe.WaitForMessage(10000)) { throw 'Shell output did not enter the callback.' }
        Stop-TestProcessTree $server
        Wait-Until { return !$raceProbe.IsConnected } 'Connection did not close during the output callback.'
        if ($raceProbe.WaitForResult(300)) { throw 'FAIL: disconnect was reported before the active output callback completed.' }
        $raceProbe.ReleaseMessage()
        if (!$raceProbe.WaitForResult(2000) -or !$raceProbe.Succeeded) {
            throw 'FAIL: a completed callback was reported as connection loss.'
        }
        Write-Output 'PASS: an active output callback completes before disconnect is reported.'
        return
    }

    if ($Mode -eq 'Upload') {
        $smallSource = Join-Path $testDir 'small-source.txt'
        $smallRemote = Join-Path $testDir 'small-remote.txt'
        $smallDownload = Join-Path $testDir 'small-download.txt'
        [IO.File]::WriteAllText($smallSource, 'transfer_before_disconnect')
        foreach ($transfer in @(
            @{ Verb = 'upload'; Source = $smallSource; Target = $smallRemote },
            @{ Verb = 'download'; Source = $smallRemote; Target = $smallDownload }
        )) {
            $client = Start-Process -FilePath $clientExe -ArgumentList @(
                $transfer.Verb, '127.0.0.1', '--port', $port, '-u', 'disconnect-test', '-p', 'test-only', '-q',
                ('"' + $transfer.Source + '"'), ('"' + $transfer.Target + '"')
            ) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            $null = $client.Handle
            if (!$client.WaitForExit(10000) -or $client.ExitCode -ne 0) { throw 'Normal file transfer failed.' }
        }
        if ([IO.File]::ReadAllText($smallDownload) -ne 'transfer_before_disconnect') { throw 'Downloaded content changed.' }
        Write-Output 'PASS: normal upload and download preserve file contents.'

        $sourceFile = Join-Path $testDir 'large-upload.bin'
        $remoteFile = Join-Path $testDir 'remote-upload.bin'
        $sourceStream = [IO.File]::Create($sourceFile)
        try { $sourceStream.SetLength(2GB) } finally { $sourceStream.Dispose() }
        $client = Start-Process -FilePath $clientExe -ArgumentList @(
            'upload', '127.0.0.1', '--port', $port, '-u', 'disconnect-test', '-p', 'test-only', '-q',
            ('"' + $sourceFile + '"'), ('"' + $remoteFile + '"')
        ) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        $null = $client.Handle
        Wait-Until {
            return (Test-Path -LiteralPath $remoteFile) -and (Get-Item -LiteralPath $remoteFile).Length -gt 0
        } 'Upload did not start transferring file chunks.'
        Stop-TestProcessTree $server
        Wait-Until {
            return (Read-TestLog $stderr).Contains('Disconnected from server.')
        } 'Upload client did not detect the server disconnect.'
        if (!$client.WaitForExit(2000)) { throw 'FAIL: upload kept reading the local file after disconnect.' }
        if ($client.ExitCode -ne 255) { throw ('FAIL: upload should exit with 255, got ' + $client.ExitCode) }
        Write-Output 'PASS: upload stops reading the file and exits with code 255 after server disconnect.'
        return
    }

    if ($Mode -ne 'Interactive') {
        # Kill only this test server and its child processes from the remote
        # command, reproducing exec of taskkill without touching other servers.
        $command = '"echo output_before_disconnect & ping -n 2 127.0.0.1 >nul & taskkill /F /T /PID {0}"' -f $server.Id
        $clientArguments = @('exec', '127.0.0.1', '--port', $port, '-u', 'disconnect-test', '-p', 'test-only')
        if ($Mode -eq 'ExecJson') { $clientArguments += '--json' }
        $clientArguments += $command
        $client = Start-Process -FilePath $clientExe -ArgumentList $clientArguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        $null = $client.Handle
        Wait-Until {
            return (Read-TestLog $stderr).Contains('Disconnected from server.')
        } 'Exec client did not detect the server disconnect.'
        if (!$client.WaitForExit(2000)) {
            throw 'FAIL: exec client kept waiting for the completion marker after disconnect.'
        }
        if ($client.ExitCode -ne 255) {
            throw ('FAIL: exec should report connection loss as exit code 255, got ' + $client.ExitCode)
        }
        $output = Read-TestLog $stdout
        if ($Mode -eq 'ExecJson') {
            $result = $output | ConvertFrom-Json
            if ($result.ok -ne $false -or $result.error.kind -ne 'connection_lost') {
                throw 'FAIL: JSON result did not report connection_lost.'
            }
            $output = $result.stdout
        }
        if (!$output.Contains('output_before_disconnect')) {
            throw 'FAIL: output received before the disconnect was lost.'
        }
        Write-Output ('PASS: ' + $Mode + ' exits promptly with code 255 and preserves output after server disconnect.')
        return
    }

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
    if ($null -ne $raceProbe) { $raceProbe.Dispose() }
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
