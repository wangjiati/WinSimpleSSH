using System;
using System.Timers;
using Newtonsoft.Json;
using WebSocketSharp;
using SSHCommon.Protocol;

namespace SSHClient.Core
{
    public class RemoteShell
    {
        private WebSocket _ws;
        private Timer _heartbeatTimer;
        private Action<string> _onOutput;

        public void Connect(string host, int port, string username, string password)
        {
            var url = $"ws://{host}:{port}/";
            _ws = new WebSocket(url);

            _ws.OnMessage += (sender, e) =>
            {
                if (e.Data != null)
                {
                    HandleMessage(e.Data);
                }
            };

            _ws.OnClose += (sender, e) =>
            {
                StopHeartbeat();
                Console.WriteLine("\nDisconnected from server. / 已与服务端断开连接");
            };

            _ws.OnError += (sender, e) =>
            {
                Console.WriteLine($"\nConnection error: {e.Message} / 连接错误: {e.Message}");
            };

            _ws.Connect();

            if (_ws.IsAlive)
            {
                var authMsg = new ProtocolMessage(MessageType.AuthRequest,
                    JsonConvert.SerializeObject(new AuthRequest
                    {
                        Username = username,
                        Password = password
                    }));
                _ws.Send(authMsg.ToJson());
            }
            else
            {
                Console.WriteLine("Failed to connect. / 连接失败");
            }
        }

        public void SetOutputHandler(Action<string> handler)
        {
            _onOutput = handler;
        }

        /// <summary>启动心跳，每30秒发送 Ping</summary>
        public void StartHeartbeat()
        {
            _heartbeatTimer = new Timer(30000);
            _heartbeatTimer.Elapsed += (s, e) =>
            {
                if (_ws?.IsAlive == true)
                {
                    _ws.Send(new ProtocolMessage(MessageType.Ping, "").ToJson());
                }
            };
            _heartbeatTimer.Start();
        }

        public void StopHeartbeat()
        {
            _heartbeatTimer?.Stop();
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }

        private void HandleMessage(string raw)
        {
            ProtocolMessage msg;
            try
            {
                msg = ProtocolMessage.FromJson(raw);
            }
            catch { return; }

            switch (msg.Type)
            {
                case MessageType.AuthResponse:
                    var authResp = JsonConvert.DeserializeObject<AuthResponse>(msg.Data);
                    if (authResp.Success)
                    {
                        Console.WriteLine("Authenticated successfully. / 认证成功");
                        _onOutput?.Invoke("AUTH_OK");
                    }
                    else
                    {
                        Console.WriteLine($"Authentication failed: {authResp.Message} / 认证失败: {authResp.Message}");
                        _onOutput?.Invoke("AUTH_FAIL");
                    }
                    break;

                case MessageType.ShellOutput:
                    var output = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
                    Console.Write(output.Text);
                    break;

                case MessageType.ShellError:
                    var errOutput = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
                    Console.Write(errOutput.Text);
                    break;

                case MessageType.Error:
                    var err = JsonConvert.DeserializeObject<ErrorData>(msg.Data);
                    Console.WriteLine($"\nError: {err.Message} / 错误: {err.Message}");
                    break;

                case MessageType.Pong:
                    // 心跳回复，无需处理
                    break;

                case MessageType.TimeoutWarning:
                    var tw = JsonConvert.DeserializeObject<TimeoutWarningData>(msg.Data);
                    Console.WriteLine($"\n[Warning] 即将断开连接: {tw.SecondsRemaining}秒内无活动 / Connection timeout in {tw.SecondsRemaining}s");
                    break;

                case MessageType.Kicked:
                    var kick = JsonConvert.DeserializeObject<KickedData>(msg.Data);
                    Console.WriteLine($"\n[Kicked] 您已被断开 / You were disconnected: {kick.Reason}");
                    _onOutput?.Invoke("KICKED");
                    break;

                case MessageType.UploadComplete:
                    var uploadResult = JsonConvert.DeserializeObject<FileTransferComplete>(msg.Data);
                    Console.WriteLine(uploadResult.Success
                        ? $"\nUpload completed: {uploadResult.FileName} / 上传完成: {uploadResult.FileName}"
                        : $"\nUpload failed: {uploadResult.Message} / 上传失败: {uploadResult.Message}");
                    _onOutput?.Invoke("TRANSFER_DONE");
                    break;

                case MessageType.ClientList:
                    _onOutput?.Invoke($"CLIENTLIST:{msg.Data}");
                    break;

                case MessageType.DownloadStart:
                case MessageType.DownloadChunk:
                case MessageType.DownloadComplete:
                    _onOutput?.Invoke($"MSG:{raw}");
                    break;

                default:
                    break;
            }
        }

        public void SendInput(string input)
        {
            if (_ws?.IsAlive != true) return;
            _ws.Send(new ProtocolMessage(MessageType.ShellInput, input).ToJson());
        }

        public void SendInterrupt()
        {
            if (_ws?.IsAlive != true) return;
            _ws.Send(new ProtocolMessage(MessageType.Interrupt, "").ToJson());
        }

        public void Upload(string localPath, string remotePath)
        {
            FileTransfer.Upload(_ws, localPath, remotePath);
        }

        public void Download(string remotePath, string localPath)
        {
            FileTransfer.Download(_ws, remotePath, localPath);
        }

        public void ListClients()
        {
            if (_ws?.IsAlive != true) return;
            _ws.Send(new ProtocolMessage(MessageType.ListClients, "").ToJson());
        }

        public void KickClient(string connectionId)
        {
            if (_ws?.IsAlive != true) return;
            _ws.Send(new ProtocolMessage(MessageType.KickClient,
                JsonConvert.SerializeObject(new KickRequestData { ConnectionId = connectionId })).ToJson());
        }

        public void Disconnect()
        {
            StopHeartbeat();
            _ws?.Close();
        }

        public bool IsConnected => _ws?.IsAlive == true;
    }
}
