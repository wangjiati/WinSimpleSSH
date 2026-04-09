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
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return ExitCodes.Success;
            }

            var verb = args[0].ToLower();
            switch (verb)
            {
                case "connect":
                    return RunConnect(args);
                case "exec":
                case "start":
                case "upload":
                case "download":
                    return RunNonInteractive(verb, args);
                case "help":
                case "-h":
                case "--help":
                    PrintUsage();
                    return ExitCodes.Success;
                default:
                    Console.Error.WriteLine($"Unknown verb: {verb}");
                    PrintUsage();
                    return ExitCodes.ProtocolError;
            }
        }

        /// <summary>
        /// 交互式 REPL（原 Main 主体）。保持与非交互动词的行为隔离。
        /// </summary>
        static int RunConnect(string[] args)
        {
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
                Console.WriteLine("Usage: SSHC connect <host> -p <port> -u <username>");
                return ExitCodes.ProtocolError;
            }

            Console.Write("Password: ");
            password = ReadPassword();

            var shell = new RemoteShell();
            var authResult = new ManualResetEvent(false);
            var authSuccess = new bool[1];
            var downloadState = new SSHClient.Core.DownloadState[1];
            var downloadLocalPath = new string[1];

            shell.SetSignalHandler(signal =>
            {
                if (signal.StartsWith("MSG:"))
                {
                    var json = signal.Substring(4);
                    try
                    {
                        var msg = ProtocolMessage.FromJson(json);
                        FileTransfer.HandleDownloadMessage(msg, downloadLocalPath[0], ref downloadState[0]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\nDownload error: {ex.Message} / 下载错误: {ex.Message}");
                    }
                }
                else if (signal.StartsWith("CLIENTLIST:"))
                {
                    var clientListJson = signal.Substring("CLIENTLIST:".Length);
                    try
                    {
                        var list = JsonConvert.DeserializeObject<ClientListData>(clientListJson);
                        PrintClientList(list);
                    }
                    catch { }
                }
                else if (signal == "AUTH_OK")
                {
                    authSuccess[0] = true;
                    authResult.Set();
                }
                else if (signal == "AUTH_FAIL")
                {
                    authResult.Set();
                }
            });

            Console.WriteLine($"Connecting to {host}:{port}... / 正在连接 {host}:{port}...");
            shell.Connect(host, port, username, password);

            authResult.WaitOne(10000);

            if (!authSuccess[0])
            {
                Console.WriteLine("认证失败，程序退出 / Authentication failed");
                shell.Disconnect();
                return ExitCodes.AuthFailed;
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

                if (input.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Clear();
                    continue;
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
                    {
                        downloadLocalPath[0] = parts.Length > 1 ? parts[1] : null;
                        downloadState[0] = null;
                        shell.Download(parts[0], parts.Length > 1 ? parts[1] : null);
                    }
                    continue;
                }

                // Regular shell input
                history.Add(input);
                shell.SendInput(input + "\n");
            }

            shell.Disconnect();
            return ExitCodes.Success;
        }

        /// <summary>
        /// 非交互模式入口——Agent 友好的一次性命令执行。
        /// </summary>
        static int RunNonInteractive(string verb, string[] args)
        {
            var opts = ParseCommonArgs(verb, args);
            if (opts == null) return ExitCodes.ProtocolError;

            var runner = new NonInteractiveRunner(
                opts.Host, opts.Port, opts.Username, opts.Password,
                opts.JsonOutput, opts.Quiet);

            switch (verb)
            {
                case "exec":
                    return runner.RunExec(opts.Positional);
                case "start":
                    return runner.RunStart(opts.Positional);
                case "upload":
                case "download":
                    Console.Error.WriteLine($"[{verb}] not implemented yet (coming in P4)");
                    return ExitCodes.ProtocolError;
                default:
                    return ExitCodes.ProtocolError;
            }
        }

        class CommonOpts
        {
            public string Host;
            public int Port = 22222;
            public string Username;
            public string Password;
            public bool JsonOutput;
            public bool Quiet;
            public string Positional;
        }

        /// <summary>
        /// 解析非交互动词共用的参数：`&lt;verb&gt; &lt;host&gt; -u &lt;user&gt; -P &lt;pwd&gt; [-p &lt;port&gt;] [-q] [--json] [--] &lt;positional...&gt;`
        /// 返回 null 表示解析失败（错误已输出到 stderr）。
        /// </summary>
        static CommonOpts ParseCommonArgs(string verb, string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine($"Usage: SSHC {verb} <host> -u <username> -P <password> [options] <args...>");
                return null;
            }
            var opts = new CommonOpts { Host = args[1] };
            var positional = new System.Text.StringBuilder();

            int idx = 2;
            while (idx < args.Length)
            {
                switch (args[idx])
                {
                    case "-p":
                        if (idx + 1 >= args.Length) { Console.Error.WriteLine("-p requires a value"); return null; }
                        if (!int.TryParse(args[idx + 1], out opts.Port)) { Console.Error.WriteLine("-p value must be an integer"); return null; }
                        idx += 2;
                        break;
                    case "-u":
                        if (idx + 1 >= args.Length) { Console.Error.WriteLine("-u requires a value"); return null; }
                        opts.Username = args[idx + 1];
                        idx += 2;
                        break;
                    case "-P":
                        if (idx + 1 >= args.Length) { Console.Error.WriteLine("-P requires a value"); return null; }
                        opts.Password = args[idx + 1];
                        idx += 2;
                        break;
                    case "-q":
                    case "--quiet":
                        opts.Quiet = true;
                        idx++;
                        break;
                    case "--json":
                        opts.JsonOutput = true;
                        idx++;
                        break;
                    default:
                        if (positional.Length > 0) positional.Append(' ');
                        positional.Append(args[idx]);
                        idx++;
                        break;
                }
            }
            opts.Positional = positional.ToString();

            if (string.IsNullOrEmpty(opts.Host))
            {
                Console.Error.WriteLine("Missing <host>");
                return null;
            }
            if (string.IsNullOrEmpty(opts.Username) || string.IsNullOrEmpty(opts.Password))
            {
                Console.Error.WriteLine("Missing -u <username> or -P <password>");
                return null;
            }
            return opts;
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
            var parts = new List<string>();
            var current = "";
            var inQuotes = false;

            foreach (var ch in args.Trim())
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if ((ch == ' ' || ch == '\t') && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current);
                        current = "";
                    }
                }
                else
                {
                    current += ch;
                }
            }

            if (current.Length > 0)
                parts.Add(current);

            if (parts.Count == 0) return null;
            return parts.ToArray();
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
