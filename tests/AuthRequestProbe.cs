using System;
using System.Threading;
using SSHClient.Core;

// Send raw protocol frames so missing/null Data values are not normalized by
// ProtocolMessage's convenience constructor before reaching the real server.
public static class AuthRequestProbe
{
    public static void ExpectRejection(int port, string frame)
    {
        using (var completed = new AutoResetEvent(false))
        {
            var shell = new RemoteShell();
            bool authenticated = false;
            shell.SetShellOutputHandler((text, error) => { });
            shell.SetSignalHandler(signal =>
            {
                if (signal != "AUTH_OK" && signal != "AUTH_FAIL") return;
                authenticated = signal == "AUTH_OK";
                completed.Set();
            });
            try
            {
                // A rejected login opens the real connection without granting
                // access. SendRaw then preserves missing/null Data verbatim.
                shell.Connect("127.0.0.1", port, "boundary-user", "wrong-probe-password");
                if (!shell.WaitForSignal(completed, 3000) || authenticated)
                    throw new Exception("Auth probe could not establish an unauthenticated connection.");
                shell.SendRaw(frame);
                if (!shell.WaitForSignal(completed, 3000))
                    throw new Exception("FAIL: malformed authentication did not return AuthResponse.");
                if (authenticated)
                    throw new Exception("FAIL: malformed authentication was accepted.");
            }
            finally { shell.Disconnect(); }
        }
    }
}
