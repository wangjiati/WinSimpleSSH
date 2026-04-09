using System;
using System.Security.Principal;
using System.Threading;
using SSHServer.Core;

namespace SSHServer
{
    class Program
    {
        private static WebSocketServerEngine _engine;
        private static ManualResetEvent _quitEvent = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            Console.Title = "SSH Server";

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
                _engine.Stop();
                _quitEvent.Set();
            };

            PrintHelp();

            // 主线程等待退出信号，不接受控制台输入
            _quitEvent.WaitOne();
        }

        static void PrintHelp()
        {
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
