using System;
using System.Threading;
using SSHClient.Core;

// Test helper: hold a real shell-output callback while the server closes the
// connection, then complete the operation from that callback before it returns.
public sealed class DisconnectRaceProbe : IDisposable
{
    private readonly RemoteShell _shell = new RemoteShell();
    private readonly ManualResetEvent _messageEntered = new ManualResetEvent(false);
    private readonly ManualResetEvent _releaseMessage = new ManualResetEvent(false);
    private readonly ManualResetEvent _operationDone = new ManualResetEvent(false);
    private readonly ManualResetEvent _waiterDone = new ManualResetEvent(false);
    private readonly Thread _waiter;

    public bool Succeeded { get; private set; }
    public bool IsConnected { get { return _shell.IsConnected; } }

    public DisconnectRaceProbe(int port)
    {
        _shell.SetShellOutputHandler((text, isStderr) =>
        {
            _messageEntered.Set();
            _releaseMessage.WaitOne();
            _operationDone.Set();
        });
        _shell.Connect("127.0.0.1", port, "disconnect-test", "test-only");
        _waiter = new Thread(() =>
        {
            Succeeded = _shell.WaitForSignal(_operationDone, 10000);
            _waiterDone.Set();
        });
        _waiter.IsBackground = true;
        _waiter.Start();
    }

    public bool WaitForMessage(int timeoutMs) { return _messageEntered.WaitOne(timeoutMs); }
    public bool WaitForResult(int timeoutMs) { return _waiterDone.WaitOne(timeoutMs); }
    public void ReleaseMessage() { _releaseMessage.Set(); }

    public void Dispose()
    {
        _releaseMessage.Set();
        if (_shell.IsConnected)
            _shell.Disconnect();
        _waiter.Join(2000);
    }
}
