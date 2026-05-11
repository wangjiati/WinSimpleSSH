using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace SSHServer.Service
{
    internal class SSHServiceHost : ServiceBase
    {
        private Thread _monitorThread;
        private volatile bool _running;
        private Process _serverProcess;

        private static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string UpdateMarker = Path.Combine(ExeDir, "update_marker");
        private static readonly string StagingDir = Path.Combine(ExeDir, "update_staging");

        public SSHServiceHost()
        {
            ServiceName = ServiceHelper.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            _running = true;
            CleanupOldFiles();
            _monitorThread = new Thread(MonitorLoop) { IsBackground = true, Name = "SSHServer Monitor" };
            _monitorThread.Start();
        }

        protected override void OnStop() => StopMonitor();
        protected override void OnShutdown() => StopMonitor();

        void StopMonitor()
        {
            _running = false;
            try { _serverProcess?.Kill(); } catch { }
            if (_monitorThread?.IsAlive == true) _monitorThread.Join(5000);
        }

        void MonitorLoop()
        {
            while (_running)
            {
                try
                {
                    bool serverAlive = _serverProcess != null && !_serverProcess.HasExited;

                    if (!serverAlive)
                    {
                        // 检查是否有更新标记
                        if (File.Exists(UpdateMarker))
                            ApplyUpdate();

                        TryLaunch();
                    }
                }
                catch (Exception ex) { Log("Monitor error: " + ex.Message); }

                for (int i = 0; i < 50 && _running; i++)
                    Thread.Sleep(100);
            }
        }

        void TryLaunch()
        {
            try
            {
                _serverProcess = ProcessLauncher.LaunchInUserSession();
                if (_serverProcess != null)
                    Log($"SSHServer started (PID={_serverProcess.Id})");
                else
                    Log("No active user session, waiting...");
            }
            catch (Exception ex) { Log("Failed to launch: " + ex.Message); }
        }

        void ApplyUpdate()
        {
            try
            {
                Log("Update marker found, applying update...");
                var stagingExe = Path.Combine(StagingDir, "SSHServer.exe");

                if (!File.Exists(stagingExe))
                {
                    Log("Staging file not found, removing marker.");
                    File.Delete(UpdateMarker);
                    return;
                }

                // 终止旧的服务端进程
                try { _serverProcess?.Kill(); } catch { }
                _serverProcess = null;

                // 重命名当前 EXE（Windows 允许重命名运行中的文件）
                var currentExe = ProcessLauncher.ExePath;
                var backup = currentExe + $".{DateTime.Now:yyyyMMddHHmmss}.old";
                try { File.Move(currentExe, backup); } catch (Exception ex) { Log($"Rename failed: {ex.Message}"); }

                // 复制新版本
                File.Copy(stagingExe, currentExe, true);
                Log("Update files replaced.");

                // 清理
                try { Directory.Delete(StagingDir, true); } catch { }
                try { File.Delete(UpdateMarker); } catch { }
            }
            catch (Exception ex) { Log($"Update failed: {ex.Message}"); }
        }

        void CleanupOldFiles()
        {
            try
            {
                foreach (var f in Directory.GetFiles(ExeDir, "*.old"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        void Log(string message)
        {
            var line = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss}|INFO|{message}";
            try
            {
                var logDir = Path.Combine(ExeDir, "log");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, $"service_{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch { }
        }
    }
}
