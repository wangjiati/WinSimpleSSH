using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using SSHServer.Core;
using SSHServer.Service;

namespace SSHServer
{
    class Program
    {
        private static WebSocketServerEngine _engine;
        private static ManualResetEvent _quitEvent = new ManualResetEvent(false);

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        private delegate bool ConsoleCtrlDelegate(uint ctrlType);

        private static ConsoleCtrlDelegate _ctrlHandler;

        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll")]
        static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint dwMode);

        const int STD_INPUT_HANDLE = -10;
        const uint ENABLE_EXTENDED_FLAGS = 0x0080;
        const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
        const uint ENABLE_INSERT_MODE = 0x0020;

        static void DisableQuickEditMode()
        {
            var handle = GetStdHandle(STD_INPUT_HANDLE);
            if (GetConsoleMode(handle, out uint mode))
            {
                mode &= ~ENABLE_QUICK_EDIT_MODE;
                mode &= ~ENABLE_INSERT_MODE;
                mode |= ENABLE_EXTENDED_FLAGS;
                SetConsoleMode(handle, mode);
            }
        }

        static void Main(string[] args)
        {
            // 1. 服务安装/卸载
            if (HasArg(args, "--install"))
            {
                ServiceHelper.Install();
                return;
            }
            if (HasArg(args, "--uninstall"))
            {
                ServiceHelper.Uninstall();
                return;
            }

            // 2. 服务守护模式（SCM 启动，非交互式）
            if (!Environment.UserInteractive)
            {
                ServiceBase.Run(new SSHServiceHost());
                return;
            }

            // 3. 由服务守护进程启动的 WebSocket 服务器模式
            if (HasArg(args, "--service-server"))
            {
                RunServer();
                return;
            }

            // 4. 控制台/后台模式（现有行为）
            RunConsole(args);
        }

        /// <summary>
        /// service-server 模式：运行 WebSocket 服务器，由服务守护进程监控。
        /// </summary>
        static void RunServer()
        {
            try
            {
                _engine = new WebSocketServerEngine();
                _engine.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return;
            }

            // 等待退出信号
            _quitEvent.WaitOne();
        }

        /// <summary>
        /// 控制台/后台模式：现有行为。
        /// </summary>
        static void RunConsole(string[] args)
        {
            bool showConsole = false;
            foreach (var arg in args)
            {
                if (arg.Equals("--console", StringComparison.OrdinalIgnoreCase))
                {
                    showConsole = true;
                    break;
                }
            }

            if (showConsole)
            {
                AllocConsole();
                DisableQuickEditMode();
                var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                Console.Title = $"SSH Server v{version}";
            }

            try
            {
                _engine = new WebSocketServerEngine();
                _engine.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                SLog.Info("Server shutting down (Ctrl+C)");
                _engine.Stop();
                _quitEvent.Set();
            };

            _ctrlHandler = (ctrlType) =>
            {
                if (ctrlType == 2 || ctrlType == 5 || ctrlType == 6)
                {
                    SLog.Info($"Server shutting down (event={ctrlType})");
                    _engine.Stop();
                }
                return false;
            };
            SetConsoleCtrlHandler(_ctrlHandler, true);

            PrintHelp();
            _quitEvent.WaitOne();
        }

        static bool HasArg(string[] args, string name)
        {
            foreach (var arg in args)
                if (arg.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void PrintHelp()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Console.WriteLine();
            Console.WriteLine($"SSH Server v{version}");
            Console.WriteLine();
            Console.WriteLine("=== Help ===");
            Console.WriteLine();
            Console.WriteLine("  --install    Install as Windows service");
            Console.WriteLine("  --uninstall  Uninstall Windows service");
            Console.WriteLine("  --console    Show console window");
            Console.WriteLine("  Ctrl+C       Stop server and exit");
            Console.WriteLine();
        }
    }
}
