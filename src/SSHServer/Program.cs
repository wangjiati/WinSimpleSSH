using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(uint dwProcessId);

        const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        const int STD_OUTPUT_HANDLE = -11;

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
            // 0. 帮助指令：打印用法后直接退出，不启动服务器
            if (HasHelpArg(args))
            {
                RunHelp();
                return;
            }

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

            // 静默模式（无 --console）不打印任何内容到 stdout，避免从控制台启动时泄漏输出
            if (showConsole) PrintHelp();
            _quitEvent.WaitOne();
        }

        static bool HasArg(string[] args, string name)
        {
            foreach (var arg in args)
                if (arg.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool HasHelpArg(string[] args)
        {
            foreach (var arg in args)
            {
                string a = arg.ToLowerInvariant();
                if (a == "help" || a == "-h" || a == "--help" || a == "-?" || a == "/?" ||
                    a == "-help" || a == "/help" || a == "--usage")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 打印用法后退出。SSHServer 是 WinExe（GUI 子系统）：
        /// - cmd / bash / 重定向管道：stdout 句柄有效，直接打印
        /// - PowerShell 等：句柄无效，需 AttachConsole 附着父控制台
        /// - 双击启动（Explorer）：无父控制台，AllocConsole 新开窗口并等待按键
        /// </summary>
        static void RunHelp()
        {
            bool allocated = false;
            try
            {
                allocated = EnsureConsoleAttached();
                ConfigureRedirectedOutput();
                PrintUsage();
                if (allocated)
                {
                    Console.WriteLine();
                    Console.WriteLine("Press any key to exit / 按任意键退出...");
                    try { Console.ReadKey(true); } catch { }
                }
            }
            catch
            {
                // 无控制台可用（如 Session 0 服务上下文）：静默退出
            }
        }

        /// <summary>
        /// 确保有可写控制台。返回 true 表示我们 AllocConsole 新开了窗口（调用方需等待按键）。
        /// </summary>
        static bool EnsureConsoleAttached()
        {
            var h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h != IntPtr.Zero && h != (IntPtr)(-1))
                return false; // 已有可用输出（cmd / bash / 重定向管道）

            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                ResetConsoleWriters(); // 附着父控制台（PowerShell 等）
                return false;
            }

            AllocConsole(); // 双击启动：新开控制台窗口
            ResetConsoleWriters();
            return true;
        }

        /// <summary>
        /// AttachConsole / AllocConsole 之后 GetStdHandle 才返回有效句柄，
        /// 而 Console 类会缓存初始化失败的 writer，必须重建输出流。
        /// </summary>
        static void ResetConsoleWriters()
        {
            var encoding = Console.OutputEncoding;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
        }

        /// <summary>
        /// 管道/重定向的输出统一使用 UTF-8，便于脚本和 Agent 解析。
        /// 真实控制台保持当前代码页，避免污染父 cmd/PowerShell 的编码状态。
        /// </summary>
        static void ConfigureRedirectedOutput()
        {
            if (!Console.IsOutputRedirected) return;

            var utf8 = new UTF8Encoding(false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            if (Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }

        static void PrintHelp()
        {
            Console.WriteLine();
            PrintUsage();
        }

        static void PrintUsage()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Console.WriteLine($"SSH Server v{version} - Simple SSH server over WebSocket");
            Console.WriteLine($"基于 WebSocket 的简易 SSH 服务端");
            Console.WriteLine();
            Console.WriteLine("=== Usage / 用法 ===");
            Console.WriteLine("  SSHServer.exe [options]");
            Console.WriteLine();
            Console.WriteLine("=== Options / 选项 ===");
            Console.WriteLine("  help, -h, --help, -?, /?   Show this help and exit / 显示本帮助后退出");
            Console.WriteLine("  --install                  Install as Windows service / 安装为 Windows 服务（需管理员）");
            Console.WriteLine("  --uninstall                Uninstall Windows service / 卸载 Windows 服务（需管理员）");
            Console.WriteLine("  --console                  Run with console window / 控制台窗口模式（调试/监控）");
            Console.WriteLine("  (no args)                  Run silently in background / 无参数时后台静默运行");
            Console.WriteLine();
            Console.WriteLine("  Ctrl+C                     Stop server and exit (console mode) / 停止并退出（控制台模式）");
            Console.WriteLine();
        }
    }
}
