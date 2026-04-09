using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using SSHCommon.Protocol;

namespace SSHServer.Core
{
    public class ShellSession : IDisposable
    {
        private Process _process;
        private Action<string> _onOutput;
        private Action<string> _onError;
        private Thread _outputThread;
        private Thread _errorThread;
        private volatile bool _running;

        public bool IsRunning => _running && _process != null && !_process.HasExited;

        public void Start(Action<string> onOutput, Action<string> onError)
        {
            _onOutput = onOutput;
            _onError = onError;
            _running = true;

            _process = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding("GBK"),
                    StandardErrorEncoding = Encoding.GetEncoding("GBK")
                }
            };

            _process.Start();

            _outputThread = new Thread(ReadOutput)
            {
                IsBackground = true
            };
            _outputThread.Start();

            _errorThread = new Thread(ReadError)
            {
                IsBackground = true
            };
            _errorThread.Start();
        }

        private void ReadOutput()
        {
            var buffer = new char[4096];
            try
            {
                while (_running && !_process.HasExited)
                {
                    var read = _process.StandardOutput.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        var text = new string(buffer, 0, read);
                        _onOutput?.Invoke(text);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        private void ReadError()
        {
            var buffer = new char[4096];
            try
            {
                while (_running && !_process.HasExited)
                {
                    var read = _process.StandardError.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        var text = new string(buffer, 0, read);
                        _onError?.Invoke(text);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        public void WriteInput(string input)
        {
            if (IsRunning)
            {
                _process.StandardInput.Write(input);
                _process.StandardInput.Flush();
            }
        }

        public void Interrupt()
        {
            if (IsRunning)
            {
                try
                {
                    // Kill cmd and its child processes, then restart
                    KillProcessTree(_process);
                    _running = false;
                }
                catch { }
            }
        }

        private void KillProcessTree(Process process)
        {
            try
            {
                // Kill child processes first
                var killer = new Process
                {
                    StartInfo = new ProcessStartInfo("taskkill")
                    {
                        Arguments = $"/PID {process.Id} /T /F",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                killer.Start();
                killer.WaitForExit(3000);
            }
            catch { }

            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }
        }

        public void Dispose()
        {
            _running = false;
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    KillProcessTree(_process);
                }
            }
            catch { }
        }
    }
}
