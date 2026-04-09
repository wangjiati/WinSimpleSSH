using System;
using System.Collections.Generic;
using System.Threading;
using SSHClient.Core;
using SSHCommon.Protocol;
using Newtonsoft.Json;

namespace SSHClient
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            var cmd = args[0].ToLower();
            if (cmd != "connect")
            {
                PrintUsage();
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return;
            }

            // Parse: connect <host> -p <port> -u <username>
            string host = null;
            int port = 22222;
            string username = null;
            string password = null;

            int i = 1;
            while (i < args.Length)
            {
                if (args[i] == "-p" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out port);
                    i += 2;
                }
                else if (args[i] == "-u" && i + 1 < args.Length)
                {
                    username = args[i + 1];
                    i += 2;
                }
                else if (host == null)
                {
                    host = args[i];
                    i++;
                }
                else
                {
                    i++;
                }
            }

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username))
            {
                Console.WriteLine("Usage: ssh-cli connect <host> -p <port> -u <username>");
                return;
            }

            Console.Write("Password: ");
            password = ReadPassword();

            var shell = new RemoteShell();
            var authResult = new ManualResetEvent(false);

            shell.SetOutputHandler(signal =>
            {
                if (signal == "AUTH_OK" || signal == "AUTH_FAIL")
                    authResult.Set();
            });

            shell.SetOutputHandler(signal =>
            {
                if (signal.StartsWith("MSG:"))
                {
                    // 下载消息转发给 FileTransfer 处理
                }
                else if (signal.StartsWith("CLIENTLIST:"))
                {
                    var json = signal.Substring("CLIENTLIST:".Length);
                    try
                    {
                        var list = JsonConvert.DeserializeObject<ClientListData>(json);
                        PrintClientList(list);
                    }
                    catch { }
                }
                else if (signal == "AUTH_OK" || signal == "AUTH_FAIL")
                {
                    authResult.Set();
                }
            });

            Console.WriteLine($"Connecting to {host}:{port}... / 正在连接 {host}:{port}...");
            shell.Connect(host, port, username, password);

            authResult.WaitOne(10000);

            if (!shell.IsConnected)
            {
                Console.WriteLine("Connection failed. / 连接失败");
                return;
            }

            shell.StartHeartbeat();

            // Interactive loop
            var history = new List<string>();
            int historyIndex = -1;

            while (shell.IsConnected)
            {
                Console.Write("\nssh> ");
                var input = ReadLineWithHistory(history, ref historyIndex, shell);

                if (string.IsNullOrEmpty(input))
                    continue;

                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintInteractiveHelp();
                    continue;
                }

                if (input.Equals("clients", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    shell.ListClients();
                    continue;
                }

                if (input.StartsWith("kick ", StringComparison.OrdinalIgnoreCase))
                {
                    var target = input.Substring(5).Trim();
                    if (!string.IsNullOrEmpty(target))
                        shell.KickClient(target);
                    else
                        Console.WriteLine("  Usage: kick <id|all>");
                    continue;
                }

                if (input.StartsWith("upload ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = SplitTransferCommand(input.Substring(7));
                    if (parts != null)
                        shell.Upload(parts[0], parts.Length > 1 ? parts[1] : null);
                    continue;
                }

                if (input.StartsWith("download ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = SplitTransferCommand(input.Substring(9));
                    if (parts != null)
                        shell.Download(parts[0], parts.Length > 1 ? parts[1] : null);
                    continue;
                }

                // Regular shell input
                history.Add(input);
                shell.SendInput(input + "\n");
            }

            shell.Disconnect();
        }

        static void PrintClientList(ClientListData list)
        {
            if (list.Clients == null || list.Clients.Count == 0)
            {
                Console.WriteLine("  (No other clients / 无其他已连接客户端)");
                return;
            }

            Console.WriteLine($"  {"ID",-8} {"User",-12} {"Endpoint",-25} {"Connect Time",-20}");
            Console.WriteLine($"  {new string('-', 65)}");
            foreach (var c in list.Clients)
            {
                Console.WriteLine($"  {c.ConnectionId.Substring(0, Math.Min(8, c.ConnectionId.Length)),-8} {c.Username,-12} {c.RemoteEndpoint,-25} {c.ConnectTime,-20}");
            }
            Console.WriteLine($"  Total: {list.Clients.Count} client(s) / 共 {list.Clients.Count} 个客户端");
        }

        static string ReadLineWithHistory(List<string> history, ref int historyIndex, RemoteShell shell)
        {
            var line = "";
            var cursorPos = 0;

            while (true)
            {
                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return line;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (cursorPos > 0)
                    {
                        line = line.Remove(cursorPos - 1, 1);
                        cursorPos--;
                        RedrawLine(line, cursorPos);
                    }
                }
                else if (key.Key == ConsoleKey.UpArrow)
                {
                    if (history.Count > 0)
                    {
                        if (historyIndex < history.Count - 1)
                            historyIndex++;
                        line = history[history.Count - 1 - historyIndex];
                        cursorPos = line.Length;
                        RedrawLine(line, cursorPos);
                    }
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (historyIndex > 0)
                    {
                        historyIndex--;
                        line = history[history.Count - 1 - historyIndex];
                    }
                    else
                    {
                        historyIndex = -1;
                        line = "";
                    }
                    cursorPos = line.Length;
                    RedrawLine(line, cursorPos);
                }
                else if (key.Key == ConsoleKey.C && key.Modifiers == ConsoleModifiers.Control)
                {
                    Console.WriteLine("^C");
                    shell.SendInterrupt();
                    return "";
                }
                else if (key.Key == ConsoleKey.LeftArrow)
                {
                    if (cursorPos > 0)
                    {
                        cursorPos--;
                        Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                    }
                }
                else if (key.Key == ConsoleKey.RightArrow)
                {
                    if (cursorPos < line.Length)
                    {
                        cursorPos++;
                        Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    line = line.Insert(cursorPos, key.KeyChar.ToString());
                    cursorPos++;
                    RedrawLine(line, cursorPos);
                }
            }
        }

        static void RedrawLine(string line, int cursorPos)
        {
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
            Console.Write("ssh> " + line);
            Console.SetCursorPosition(5 + cursorPos, Console.CursorTop);
        }

        static string ReadPassword()
        {
            var pass = "";
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return pass;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (pass.Length > 0)
                    {
                        pass = pass.Remove(pass.Length - 1);
                        Console.Write("\b \b");
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    pass += key.KeyChar;
                    Console.Write("*");
                }
            }
        }

        static string[] SplitTransferCommand(string args)
        {
            var parts = args.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            return parts;
        }

        static void PrintUsage()
        {
            Console.WriteLine("SSH Client - Simple SSH-like tool over WebSocket");
            Console.WriteLine("SSH 客户端 - 基于 WebSocket 的简易 SSH 工具");
            Console.WriteLine();
            Console.WriteLine("=== Usage / 用法 ===");
            Console.WriteLine("  SSHC.exe connect <host> [-p <port>] -u <username>");
            Console.WriteLine();
            Console.WriteLine("=== Options / 选项 ===");
            Console.WriteLine("  -p <port>      Server port (default: 22222) / 服务端口（默认: 22222）");
            Console.WriteLine("  -u <username>  Username for authentication / 认证用户名");
            Console.WriteLine();
            Console.WriteLine("=== Interactive Commands / 交互命令 ===");
            Console.WriteLine("  <command>                    Execute remote cmd / 执行远程命令");
            Console.WriteLine("  upload <local> [remote]      Upload file / 上传文件");
            Console.WriteLine("  download <remote> [local]    Download file / 下载文件");
            Console.WriteLine("  clients, list                List connected clients / 列出已连接客户端");
            Console.WriteLine("  kick <id>                    Kick a client / 断开指定客户端");
            Console.WriteLine("  kick all                     Kick all other clients / 断开其他所有客户端");
            Console.WriteLine("  help                         Show help / 显示帮助");
            Console.WriteLine("  exit, quit                   Disconnect / 断开连接");
            Console.WriteLine();
            Console.WriteLine("=== Shortcuts / 快捷键 ===");
            Console.WriteLine("  Ctrl+C     Interrupt command / 中断命令");
            Console.WriteLine("  Up/Down    History / 历史命令");
        }

        static void PrintInteractiveHelp()
        {
            Console.WriteLine();
            Console.WriteLine("=== Interactive Commands / 交互命令 ===");
            Console.WriteLine("  <command>                    Execute remote cmd / 执行远程命令");
            Console.WriteLine("  upload <local> [remote]      Upload file / 上传文件");
            Console.WriteLine("  download <remote> [local]    Download file / 下载文件");
            Console.WriteLine("  clients                      List clients / 列出客户端");
            Console.WriteLine("  kick <id|all>                Kick client(s) / 断开客户端");
            Console.WriteLine("  help                         Show this help / 显示帮助");
            Console.WriteLine("  exit                         Disconnect / 断开连接");
            Console.WriteLine();
            Console.WriteLine("=== Shortcuts / 快捷键 ===");
            Console.WriteLine("  Ctrl+C     Interrupt command / 中断命令");
            Console.WriteLine("  Up/Down    History / 历史命令");
            Console.WriteLine();
        }
    }
}
