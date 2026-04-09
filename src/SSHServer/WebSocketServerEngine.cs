using System;
using System.IO;
using SSHServer.Config;
using SSHServer.Core;
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

            ConnectionManager.SetConfig(config);

            _server = new WebSocketServer(config.Port);
            _server.AddWebSocketService<ConnectionManager>("/");
            _server.KeepClean = true;
            _server.Start();

            ConnectionManager.StartTimeoutTimer();

            Console.WriteLine($"SSH Server started on port {config.Port}");
        }

        public void Stop()
        {
            ConnectionManager.StopTimeoutTimer();
            _server?.Stop();
            Console.WriteLine("SSH Server stopped.");
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
