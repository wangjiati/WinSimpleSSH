using System;
using System.Threading;

namespace SSHServer
{
    class Program
    {
        private static WebSocketServerEngine _engine;

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
            };

            PrintHelp();

            while (true)
            {
                Console.Write("server> ");
                var cmd = Console.ReadLine();
                if (cmd == null) break;
                cmd = cmd.Trim();
                if (string.IsNullOrEmpty(cmd)) continue;

                var parts = cmd.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                var command = parts[0].ToLower();

                switch (command)
                {
                    case "exit":
                    case "quit":
                        _engine.Stop();
                        return;

                    case "help":
                        PrintHelp();
                        break;

                    case "list":
                    case "clients":
                        _engine.ListClients();
                        break;

                    case "kick":
                        if (parts.Length > 1)
                            _engine.KickClient(parts[1].Trim());
                        else
                            Console.WriteLine("  Usage: kick <id|all>");
                        break;

                    default:
                        Console.WriteLine($"  Unknown command: {cmd} / 未知命令: {cmd}");
                        break;
                }
            }

            _engine.Stop();
        }

        static void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("=== 帮助 / Help ===");
            Console.WriteLine();
            Console.WriteLine("  list, clients   列出已连接客户端 / List connected clients");
            Console.WriteLine("  kick <id>       断开指定客户端 / Kick a client by ID");
            Console.WriteLine("  kick all        断开所有客户端 / Kick all clients");
            Console.WriteLine("  help            显示帮助 / Show this help");
            Console.WriteLine("  exit, quit      停止服务器并退出 / Stop server and exit");
            Console.WriteLine("  Ctrl+C          停止服务器并退出 / Stop server and exit");
            Console.WriteLine();
        }
    }
}
