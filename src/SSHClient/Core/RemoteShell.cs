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
        private Action<string> _onSignal;
        private Action<string, bool> _onShellOutput;  // (text, isStderr) — 非交互模式拦截 Shell 输出
        private readonly object _sendLock = new object();
        private readonly object _uploadLock = new object();
        private bool _uploadReady;

        private void SafeSend(string data)
        {
            lock (_sendLock)
            {
                if (_ws?.IsAlive == true)
                    _ws.Send(data);
            }
        }

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
                Console.Error.WriteLine("\nDisconnected from server. / 已与服务端断开连接");
            };

            _ws.OnError += (sender, e) =>
            {
                Console.Error.WriteLine($"\nConnection error: {e.Message} / 连接错误: {e.Message}");
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
                SafeSend(authMsg.ToJson());
            }
            else
            {
                Console.Error.WriteLine("Failed to connect. / 连接失败");
            }
        }

        /// <summary>
        /// 注册"信号"回调——用于认证结果、客户端列表、下载消息转发等非文本流事件。
        /// </summary>
        public void SetSignalHandler(Action<string> handler)
        {
            _onSignal = handler;
        }

        /// <summary>
        /// 注册 Shell 输出拦截器。若已注册，ShellOutput/ShellError 不再写入 Console，
        /// 而是通过此回调交给调用方处理（非交互模式用）。
        /// </summary>
        public void SetShellOutputHandler(Action<string, bool> handler)
        {
            _onShellOutput = handler;
        }

        /// <summary>启动心跳，每30秒发送 Ping</summary>
        public void StartHeartbeat()
        {
            _heartbeatTimer = new Timer(30000);
            _heartbeatTimer.Elapsed += (s, e) =>
            {
                SafeSend(new ProtocolMessage(MessageType.Ping, "").ToJson());
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
                        Console.Error.WriteLine("Authenticated successfully. / 认证成功");
                        _onSignal?.Invoke("AUTH_OK");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Authentication failed: {authResp.Message} / 认证失败: {authResp.Message}");
                        _onSignal?.Invoke("AUTH_FAIL");
                    }
                    break;

                case MessageType.ShellOutput:
                    var output = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
                    if (_onShellOutput != null)
                        _onShellOutput(output.Text, false);
                    else
                        Console.Write(output.Text);
                    break;

                case MessageType.ShellError:
                    var errOutput = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
                    if (_onShellOutput != null)
                        _onShellOutput(errOutput.Text, true);
                    else
                        Console.Write(errOutput.Text);
                    break;

                case MessageType.Error:
                    var err = JsonConvert.DeserializeObject<ErrorData>(msg.Data);
                    Console.WriteLine($"\nError: {err.Message} / 错误: {err.Message}");
                    // 唤醒可能正在等待 UploadReady 的线程
                    lock (_uploadLock)
                    {
                        System.Threading.Monitor.Pulse(_uploadLock);
                    }
                    break;

                case MessageType.Pong:
                    break;

                case MessageType.UploadReady:
                    lock (_uploadLock)
                    {
                        _uploadReady = true;
                        System.Threading.Monitor.Pulse(_uploadLock);
                    }
                    break;

                case MessageType.TimeoutWarning:
                    var tw = JsonConvert.DeserializeObject<TimeoutWarningData>(msg.Data);
                    Console.WriteLine($"\n[Warning] 即将断开连接: {tw.SecondsRemaining}秒内无活动 / Connection timeout in {tw.SecondsRemaining}s");
                    break;

                case MessageType.Kicked:
                    var kick = JsonConvert.DeserializeObject<KickedData>(msg.Data);
                    Console.WriteLine($"\n[Kicked] 您已被断开 / You were disconnected: {kick.Reason}");
                    _onSignal?.Invoke("KICKED");
                    break;

                case MessageType.UploadComplete:
                    var uploadResult = JsonConvert.DeserializeObject<FileTransferComplete>(msg.Data);
                    Console.WriteLine(uploadResult.Success
                        ? $"\nUpload completed: {uploadResult.FileName} / 上传完成: {uploadResult.FileName}"
                        : $"\nUpload failed: {uploadResult.Message} / 上传失败: {uploadResult.Message}");
                    _onSignal?.Invoke("TRANSFER_DONE");
                    break;

                case MessageType.ClientList:
                    _onSignal?.Invoke($"CLIENTLIST:{msg.Data}");
                    break;

                case MessageType.DownloadStart:
                case MessageType.DownloadChunk:
                case MessageType.DownloadComplete:
                    _onSignal?.Invoke($"MSG:{raw}");
                    break;

                default:
                    break;
            }
        }

        public void SendInput(string input)
        {
            SafeSend(new ProtocolMessage(MessageType.ShellInput, input).ToJson());
        }

        public void SendInterrupt()
        {
            SafeSend(new ProtocolMessage(MessageType.Interrupt, "").ToJson());
        }

        public void Upload(string localPath, string remotePath)
        {
            StopHeartbeat();
            try
            {
                if (!FileTransfer.SendUploadStart(_sendLock, _ws, localPath, remotePath))
                    return;

                // 等待服务端确认文件可写（UploadReady），最多等 10 秒
                lock (_uploadLock)
                {
                    _uploadReady = false;
                    if (!_uploadReady)
                        System.Threading.Monitor.Wait(_uploadLock, 10000);

                    if (!_uploadReady)
                    {
                        Console.WriteLine("Upload rejected by server / 服务端拒绝上传");
                        return;
                    }
                }

                FileTransfer.SendUploadChunks(_sendLock, _ws, localPath);
            }
            finally
            {
                StartHeartbeat();
            }
        }

        public void Download(string remotePath, string localPath)
        {
            FileTransfer.Download(_sendLock, _ws, remotePath, localPath);
        }

        public void ListClients()
        {
            SafeSend(new ProtocolMessage(MessageType.ListClients, "").ToJson());
        }

        public void KickClient(string connectionId)
        {
            SafeSend(new ProtocolMessage(MessageType.KickClient,
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
