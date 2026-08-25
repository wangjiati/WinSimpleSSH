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

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        [DllImport("kernel32.dll")]
        static extern uint GetFileType(IntPtr hFile);

        const uint FILE_TYPE_CHAR = 0x0002;

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint OPEN_EXISTING = 3;

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
                HelpTrace($"printing usage, allocated={allocated}, redirected={Console.IsOutputRedirected}");
                PrintUsage();
                if (allocated)
                {
                    Console.WriteLine();
                    Console.WriteLine("Press any key to exit / 按任意键退出...");
                    try { Console.ReadKey(true); } catch { }
                }
                HelpTrace("usage printed");
            }
            catch (Exception ex)
            {
                HelpTrace("direct write failed: " + ex.Message);
                // Win7（csrss 控制台架构）：GUI 进程继承的控制台句柄未附着不可写，
                // 抛 IOException。附着父控制台（cmd 场景即命令行所在控制台）后重试。
                try
                {
                    if (AttachParentConsole())
                    {
                        ConfigureRedirectedOutput();
                        PrintUsage();
                        HelpTrace("retry after AttachConsole: usage printed");
                        return;
                    }
                    HelpTrace("AttachConsole unavailable, giving up");
                }
                catch (Exception ex2)
                {
                    HelpTrace("retry failed: " + ex2);
                }
            }
        }

        /// <summary>
        /// 确保有可写控制台。返回 true 表示我们 AllocConsole 新开了窗口（调用方需等待按键）。
        /// </summary>
        static bool EnsureConsoleAttached()
        {
            var h = GetStdHandle(STD_OUTPUT_HANDLE);
            var ft = (h != IntPtr.Zero && h != (IntPtr)(-1)) ? GetFileType(h) : 0;
            var wnd = GetConsoleWindow();
            HelpTrace($"stdout handle=0x{h.ToString("X")}, filetype=0x{ft:X}, consoleWnd=0x{wnd.ToString("X")}");
            if (h != IntPtr.Zero && h != (IntPtr)(-1))
            {
                if ((ft == FILE_TYPE_CHAR || ft == 0) && wnd == IntPtr.Zero)
                {
                    // 继承了控制台句柄但本进程未附着任何控制台。
                    // Win7（csrss 架构）下 .NET 会把这种句柄误判为重定向，
                    // OpenStandardOutput 返回空流，WriteLine 静默丢弃、不抛异常；
                    // 此时 GetFileType 也返回 FILE_TYPE_UNKNOWN(0)。Win8+（ConDrv
                    // 架构）返回 FILE_TYPE_CHAR 且可直接写。统一附着父控制台
                    // （cmd 场景即命令行所在控制台），附着后 CONOUT$ 必定可写，
                    // 两代架构都正确；附着失败（如坏管道句柄误报 0）则退回直写。
                    if (AttachParentConsole())
                    {
                        HelpTrace("inherited console handle without attach: attached parent console");
                        return false;
                    }
                    HelpTrace($"AttachConsole failed err={Marshal.GetLastWin32Error()}, fallback to direct write");
                }
                return false; // 管道/文件/已附着：直接写
            }

            if (AttachParentConsole())
                return false; // 附着父控制台（PowerShell 等）

            AllocConsole(); // 双击启动：新开控制台窗口
            BindConsoleOutput();
            return true;
        }

        /// <summary>
        /// 附着父进程的控制台并绑定输出。AttachConsole 在 Win7 上不保证更新 std handle，
        /// 显式打开 CONOUT$ 确保拿到当前控制台的可写句柄。
        /// </summary>
        static bool AttachParentConsole()
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                return false;
            BindConsoleOutput();
            return true;
        }

        /// <summary>
        /// 打开 CONOUT$ 作为 stdout（指向当前附着控制台），并重建 Console 缓存的 writer。
        /// 必须带 GENERIC_READ：只写打开的句柄 GetConsoleMode 会失败，
        /// .NET 据此把输出误判为重定向并强制 UTF-8，中文就会乱码。
        /// </summary>
        static void BindConsoleOutput()
        {
            var conout = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            HelpTrace($"CONOUT$ handle=0x{conout.ToString("X")}" +
                (conout == IntPtr.Zero || conout == (IntPtr)(-1) ? $" err={Marshal.GetLastWin32Error()}" : ""));
            if (conout != IntPtr.Zero && conout != (IntPtr)(-1))
                SetStdHandle(STD_OUTPUT_HANDLE, conout);
            ResetConsoleWriters();
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
        /// help 路径诊断日志（exe 同目录 log\help_trace.log）。RunHelp 的输出环节在无控制台/
        /// 句柄不可写等场景下静默失败过，留下痕迹便于远程排查。
        /// </summary>
        static void HelpTrace(string msg)
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "help_trace.log"),
                    $"{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}|{msg}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
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
