using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SSHServer.Core;

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

        /// <summary>
        /// 禁用控制台快速编辑模式（鼠标选中会暂停所有输出）和插入模式。
        /// </summary>
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
            // 检查是否显示控制台窗口
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
                SLog.Info("服务器正在关闭 (Ctrl+C) / Server shutting down (Ctrl+C)");
                _engine.Stop();
                _quitEvent.Set();
            };

            // 捕获窗口关闭（点 X 按钮）、系统关机、用户注销等事件
            _ctrlHandler = (ctrlType) =>
            {
                // CTRL_CLOSE_EVENT = 2, CTRL_LOGOFF_EVENT = 5, CTRL_SHUTDOWN_EVENT = 6
                if (ctrlType == 2 || ctrlType == 5 || ctrlType == 6)
                {
                    SLog.Info($"服务器正在关闭 (事件={ctrlType}) / Server shutting down (event={ctrlType})");
                    _engine.Stop();
                }
                return false;
            };
            SetConsoleCtrlHandler(_ctrlHandler, true);

            PrintHelp();

            // 主线程等待退出信号，不接受控制台输入
            _quitEvent.WaitOne();
        }

        static void PrintHelp()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Console.WriteLine();
            Console.WriteLine($"SSH Server v{version}");
            Console.WriteLine();
            Console.WriteLine("=== 帮助 / Help ===");
            Console.WriteLine();
            Console.WriteLine("  关闭窗口即可停止服务器");
            Console.WriteLine("  Close the window to stop the server");
            Console.WriteLine("  Ctrl+C  停止服务器并退出 / Stop server and exit");
            Console.WriteLine();
        }
    }
}
