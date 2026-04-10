using System;
using System.IO;
using System.Security.Principal;
using SSHServer.Config;
using SSHServer.Core;
using WsLogLevel = WebSocketSharp.LogLevel;
using WebSocketSharp.Server;

namespace SSHServer
{
    public class WebSocketServerEngine
    {
        private WebSocketServer _server;

        public void Start()
        {
            var configPath = FindConfigFile();
            var config = ServerConfig.Load(configPath);

            // 初始化日志系统
            SLog.Init();

            ConnectionManager.SetConfig(config);

            _server = new WebSocketServer(config.Port);
            _server.AddWebSocketService<ConnectionManager>("/");
            _server.KeepClean = true;

            // 抑制 WebSocketSharp 内部日志（客户端异常断开时的预期错误降级为警告）
            _server.Log.Level = WsLogLevel.Fatal;
            _server.Log.Output = (data, msg) =>
            {
                SLog.Warn($"[WS] {msg}");
            };

            _server.Start();

            ConnectionManager.StartTimeoutTimer();

            SLog.Info($"SSH Server started on port {config.Port}");

            // 记录 IP 白名单状态
            if (config.IsWhitelistEnabled)
            {
                SLog.Info($"IP 白名单已启用 / IP whitelist enabled: {string.Join(", ", config.IpWhitelist)}");
            }
            else
            {
                SLog.Warn("IP 白名单未配置，允许所有 IP 连接 / IP whitelist not configured, all IPs allowed");
            }

            // 记录管理员权限状态
            var isAdmin = IsRunningAsAdmin();
            SLog.Info(isAdmin
                ? "服务端以管理员权限运行 / Running as Administrator"
                : "服务端以普通权限运行 / Running as standard user");
        }

        private bool IsRunningAsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            ConnectionManager.StopTimeoutTimer();
            _server?.Stop();
            SLog.Info("SSH Server stopped.");
        }

        public void ListClients()
        {
            var clients = ConnectionManager.GetClientList();
            if (clients.Count == 0)
            {
                Console.WriteLine("  (No connected clients / 无已连接客户端)");
                return;
            }

            Console.WriteLine($"  {"ID",-8} {"User",-12} {"Endpoint",-25} {"Connect Time",-20}");
            Console.WriteLine($"  {new string('-', 65)}");
            foreach (var c in clients)
            {
                Console.WriteLine($"  {c.ConnectionId.Substring(0, Math.Min(8, c.ConnectionId.Length)),-8} {c.Username,-12} {c.RemoteEndpoint,-25} {c.ConnectTime,-20}");
            }
            Console.WriteLine($"  Total: {clients.Count} client(s)");
        }

        public void KickClient(string id)
        {
            if (id == "all")
            {
                ConnectionManager.KickAll();
                Console.WriteLine("  All clients kicked / 所有客户端已断开");
            }
            else
            {
                ConnectionManager.KickClient(id);
                Console.WriteLine($"  Client {id} kicked / 客户端 {id} 已断开");
            }
        }

        private string FindConfigFile()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var paths = new[]
            {
                Path.Combine(exeDir, "server.json"),
                Path.Combine(exeDir, "config", "server.json"),
                "server.json"
            };

            foreach (var p in paths)
            {
                if (File.Exists(p))
                    return p;
            }

            throw new FileNotFoundException("server.json not found", "server.json");
        }
    }
}
