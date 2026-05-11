using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SSHServer.Service
{
    internal static class ProcessLauncher
    {
        public static string ExePath => typeof(ProcessLauncher).Assembly.Location;

        public static Process LaunchInUserSession()
        {
            int sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0)
                return null;

            IntPtr currentProcess = GetCurrentProcess();
            IntPtr systemToken;
            if (!OpenProcessToken(currentProcess, TOKEN_ALL_ACCESS, out systemToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");

            try
            {
                IntPtr dupToken;
                if (!DuplicateTokenEx(systemToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                    TOKEN_TYPE.TokenPrimary, out dupToken))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");

                try
                {
                    if (!SetTokenInformation(dupToken, TOKEN_INFORMATION_CLASS.TokenSessionId,
                        ref sessionId, sizeof(int)))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation failed");

                    IntPtr userToken;
                    IntPtr envBlock = IntPtr.Zero;
                    if (WTSQueryUserToken(sessionId, out userToken))
                    {
                        try { CreateEnvironmentBlock(out envBlock, userToken, false); }
                        finally { CloseHandle(userToken); }
                    }

                    var si = new STARTUPINFO();
                    si.cb = Marshal.SizeOf(si);
                    si.lpDesktop = "WinSta0\\Default";
                    PROCESS_INFORMATION pi;

                    var exePath = ExePath;
                    var cmdLine = exePath + " --service-server";

                    bool success = CreateProcessAsUser(
                        dupToken, exePath, ref cmdLine,
                        IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                        envBlock, Path.GetDirectoryName(exePath),
                        ref si, out pi);

                    if (!success)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");

                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);

                    if (envBlock != IntPtr.Zero)
                        DestroyEnvironmentBlock(envBlock);

                    return Process.GetProcessById((int)pi.dwProcessId);
                }
                finally { CloseHandle(dupToken); }
            }
            finally { CloseHandle(systemToken); }
        }

        public static void StopServerProcess()
        {
            var currentPid = Process.GetCurrentProcess().Id;
            foreach (var proc in Process.GetProcessesByName("SSHServer"))
            {
                if (proc.Id != currentPid)
                {
                    try { proc.Kill(); } catch { }
                }
            }
        }

        #region P/Invoke

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
            IntPtr tokenAttributes, SECURITY_IMPERSONATION_LEVEL impersonationLevel,
            TOKEN_TYPE tokenType, out IntPtr duplicatedToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool SetTokenInformation(IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInfoClass,
            ref int tokenInfo, int tokenInfoLength);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool CreateProcessAsUser(IntPtr token, string applicationName, ref string commandLine,
            IntPtr procAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
            IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern int WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSQueryUserToken(int sessionId, out IntPtr tokenHandle);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        const uint TOKEN_ALL_ACCESS = 0xF01FF;
        const uint MAXIMUM_ALLOWED = 0x2000000;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const uint CREATE_NO_WINDOW = 0x08000000;

        enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
        enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation = 2 }
        enum TOKEN_INFORMATION_CLASS { TokenSessionId = 12 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

        #endregion
    }
}
