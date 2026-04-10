using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Timers;
using Newtonsoft.Json;
using WebSocketSharp;
using WebSocketSharp.Server;
using SSHCommon.Crypto;
using SSHCommon.Protocol;
using SSHServer.Config;

namespace SSHServer.Core
{
    public class ConnectionManager : WebSocketBehavior
    {
        private static readonly Dictionary<string, ClientSession> _sessions = new Dictionary<string, ClientSession>();
        private static readonly object _lock = new object();
        private static ServerConfig _sharedConfig;
        private static Timer _timeoutTimer;

        private ClientSession _session;

        public static void SetConfig(ServerConfig config)
        {
            _sharedConfig = config;
        }

        public static void StartTimeoutTimer()
        {
            _timeoutTimer = new Timer(30000); // 30秒检查一次
            _timeoutTimer.Elapsed += CheckTimeouts;
            _timeoutTimer.Start();
        }

        public static void StopTimeoutTimer()
        {
            _timeoutTimer?.Stop();
            _timeoutTimer?.Dispose();
        }

        /// <summary>获取所有已连接客户端信息</summary>
        public static List<ClientInfo> GetClientList()
        {
            lock (_lock)
            {
                var list = new List<ClientInfo>();
                foreach (var kv in _sessions)
                {
                    list.Add(new ClientInfo
                    {
                        ConnectionId = kv.Value.ConnectionId,
                        Username = kv.Value.Username ?? "(未认证)",
                        RemoteEndpoint = kv.Value.RemoteEndpoint,
                        ConnectTime = kv.Value.ConnectTime.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
                return list;
            }
        }

        /// <summary>踢出指定客户端</summary>
        public static void KickClient(string connectionId, string requesterId = null)
        {
            lock (_lock)
            {
                if (connectionId == "all")
                {
                    var toKick = new List<ClientSession>();
                    foreach (var kv in _sessions)
                    {
                        if (kv.Key != requesterId)
                            toKick.Add(kv.Value);
                    }
                    foreach (var s in toKick)
                    {
                        KickSession(s, "管理员断开 / Kicked by administrator");
                    }
                }
                else
                {
                    ClientSession target;
                    if (_sessions.TryGetValue(connectionId, out target))
                    {
                        KickSession(target, "管理员断开 / Kicked by administrator");
                    }
                }
            }
        }

        private static void KickSession(ClientSession session, string reason)
        {
            session.Send(new ProtocolMessage(MessageType.Kicked,
                JsonConvert.SerializeObject(new KickedData { Reason = reason })));
            System.Threading.Thread.Sleep(100); // 确保消息发出
            session.CloseConnection?.Invoke();
        }

        /// <summary>踢出所有客户端</summary>
        public static void KickAll()
        {
            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    KickSession(kv.Value, "管理员断开所有 / All clients kicked by administrator");
                }
            }
        }

        // ===== 超时检测 =====
        private static void CheckTimeouts(object sender, ElapsedEventArgs e)
        {
            var now = DateTime.Now;
            var toClose = new List<ClientSession>();

            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    var session = kv.Value;
                    var inactive = (now - session.LastActivity).TotalMinutes;

                    // 10分钟无活动，发送警告
                    if (inactive >= 10 && session.TimeoutWarningTime == null)
                    {
                        session.TimeoutWarningTime = now;
                        session.Send(new ProtocolMessage(MessageType.TimeoutWarning,
                            JsonConvert.SerializeObject(new TimeoutWarningData { SecondsRemaining = 10 })));

                        SLog.Warn($"[Timeout] 警告已发送: {session.Tag}");
                    }

                    // 警告后10秒仍无活动，断开连接
                    if (session.TimeoutWarningTime.HasValue)
                    {
                        // 如果有新活动（在警告之后），取消超时
                        if (session.LastActivity > session.TimeoutWarningTime.Value)
                        {
                            session.TimeoutWarningTime = null;
                        }
                        else if ((now - session.TimeoutWarningTime.Value).TotalSeconds >= 10)
                        {
                            toClose.Add(session);
                        }
                    }
                }
            }

            foreach (var s in toClose)
            {
                SLog.Warn($"[Timeout] 断开超时客户端: {s.Tag}");
                s.CloseConnection?.Invoke();
            }
        }

        // ===== WebSocket 事件 =====
        protected override void OnOpen()
        {
            var clientIp = Context.UserEndPoint.Address.ToString();
            var endpoint = Context.UserEndPoint.ToString();

            // IP 白名单检查
            if (!_sharedConfig.IsIpAllowed(clientIp))
            {
                SLog.Warn($"[Blocked] IP 不在白名单 / IP not whitelisted: {endpoint}");
                Context.WebSocket.Close(1008, "IP not allowed");
                return;
            }

            _session = new ClientSession
            {
                ConnectionId = ID,
                RemoteEndpoint = endpoint,
                ConnectTime = DateTime.Now,
                LastActivity = DateTime.Now,
                FileTransfer = new FileTransferHandler(),
                SendJson = (json) => Send(Obfuscator.Encode(Encoding.UTF8.GetBytes(json))),
                CloseConnection = () => { try { Context.WebSocket.Close(); } catch { } }
            };

            lock (_lock)
            {
                _sessions[ID] = _session;
            }

            SLog.Info($"[Connect] {_session.ShortId} from {_session.RemoteEndpoint}");
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (_session == null || e.RawData == null || e.RawData.Length == 0) return;

            var raw = Encoding.UTF8.GetString(Obfuscator.Decode(e.RawData));

            _session.LastActivity = DateTime.Now;
            _session.TimeoutWarningTime = null;

            HandleMessage(raw);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            // 客户端异常断开(Code=1006)是预期行为，仅记录警告
            var level = e.Code == 1006 ? LogLevel.Warn : LogLevel.Info;
            SLog.Write(level, $"[Disconnect] {_session?.Tag ?? ID}: Code={e.Code} Reason={e.Reason}");

            lock (_lock)
            {
                if (_sessions.ContainsKey(ID))
                {
                    _sessions.Remove(ID);
                }
            }

            _session?.Cleanup();
        }

        // ===== 消息处理 =====
        private void HandleMessage(string raw)
        {
            ProtocolMessage msg;
            try
            {
                msg = ProtocolMessage.FromJson(raw);
            }
            catch
            {
                SLog.Warn($"[Message] Invalid message format from {_session?.Tag ?? "unknown"}");
                SendError("Invalid message format");
                return;
            }

            switch (msg.Type)
            {
                case MessageType.AuthRequest:
                    HandleAuth(msg.Data);
                    break;

                case MessageType.Ping:
                    _session.Send(new ProtocolMessage(MessageType.Pong, ""));
                    break;

                case MessageType.ShellInput:
                    if (RequireAuth())
                    {
                        var input = msg.Data?.TrimEnd('\n', '\r');
                        if (!string.IsNullOrEmpty(input))
                            SLog.Info($"[Shell] {_session.Tag} 执行命令: {input}");
                        _session.Shell?.WriteInput(msg.Data);
                    }
                    break;

                case MessageType.Interrupt:
                    if (RequireAuth())
                    {
                        SLog.Info($"[Shell] {_session.Tag} 中断命令 (Ctrl+C)");
                        _session.Shell?.Interrupt();
                        StartShell();
                    }
                    break;

                case MessageType.UploadStart:
                    if (RequireAuth()) HandleUploadStart(msg.Data);
                    break;

                case MessageType.UploadChunk:
                    if (RequireAuth()) HandleUploadChunk(msg.Data);
                    break;

                case MessageType.UploadComplete:
                    if (RequireAuth()) HandleUploadComplete();
                    break;

                case MessageType.DownloadStart:
                    if (RequireAuth()) HandleDownloadStart(msg.Data);
                    break;

                case MessageType.ListClients:
                    if (RequireAuth()) HandleListClients();
                    break;

                case MessageType.KickClient:
                    if (RequireAuth()) HandleKickClient(msg.Data);
                    break;

                default:
                    SendError($"Unknown message type: {msg.Type}");
                    break;
            }
        }

        private void HandleAuth(string data)
        {
            var req = JsonConvert.DeserializeObject<AuthRequest>(data);

            foreach (var user in _sharedConfig.Users)
            {
                if (user.Username == req.Username && user.Password == req.Password)
                {
                    _session.IsAuthenticated = true;
                    _session.Username = req.Username;
                    _session.Send(new ProtocolMessage(MessageType.AuthResponse,
                        JsonConvert.SerializeObject(new AuthResponse { Success = true, Message = "Authenticated" })));
                    StartShell();

                    SLog.Info($"[Auth] {_session.ShortId} authenticated as {req.Username}");
                    return;
                }
            }

            _session.Send(new ProtocolMessage(MessageType.AuthResponse,
                JsonConvert.SerializeObject(new AuthResponse { Success = false, Message = "Invalid credentials" })));
        }

        private void StartShell()
        {
            _session.Shell?.Dispose();
            _session.Shell = new ShellSession();
            _session.Shell.Start(
                text => _session.Send(new ProtocolMessage(MessageType.ShellOutput,
                    JsonConvert.SerializeObject(new ShellOutputData { Text = text }))),
                text => _session.Send(new ProtocolMessage(MessageType.ShellError,
                    JsonConvert.SerializeObject(new ShellOutputData { Text = text })))
            );
        }

        private bool RequireAuth()
        {
            if (!_session.IsAuthenticated)
            {
                SendError("Not authenticated");
                return false;
            }
            return true;
        }

        // ===== 文件传输 =====
        private void HandleUploadStart(string data)
        {
            try
            {
                _session.FileTransfer.ParseUploadStart(data);
                _session.FileTransfer.StartUpload(Environment.CurrentDirectory);

                // StartUpload 成功，发送 UploadReady 让客户端开始发数据
                SLog.Info($"[Upload] {_session.Tag} 开始上传: {_session.FileTransfer.CurrentUploadPath}");
                _session.Send(new ProtocolMessage(MessageType.UploadReady, ""));
            }
            catch (UnauthorizedAccessException)
            {
                SLog.Error($"[Upload] {_session.Tag} 无写入权限: {_session.FileTransfer.CurrentUploadPath}");
                SendError($"无写入权限 / Access denied: {_session.FileTransfer.CurrentUploadPath}");
            }
            catch (System.Security.SecurityException)
            {
                SLog.Error($"[Upload] {_session.Tag} 无写入权限: {_session.FileTransfer.CurrentUploadPath}");
                SendError($"无写入权限 / Access denied: {_session.FileTransfer.CurrentUploadPath}");
            }
            catch (Exception ex)
            {
                SLog.Error($"Upload start failed", ex);
                SendError($"Upload failed: {ex.Message}");
            }
        }

        private void HandleUploadChunk(string data)
        {
            try
            {
                if (!_session.FileTransfer.WriteChunk(data))
                {
                    // 上传未开始或已结束，静默忽略多余的 chunk
                }
            }
            catch (Exception ex)
            {
                SLog.Error($"Upload chunk failed", ex);
            }
        }

        private void HandleUploadComplete()
        {
            var result = _session.FileTransfer.FinishUpload();
            if (result.Success)
            {
                SLog.Info($"[Upload] {_session.Tag} 上传完成: {result.FileName}");
            }
            else
            {
                SLog.Warn($"[Upload] {_session.Tag} 上传未正常完成");
            }
            _session.Send(new ProtocolMessage(MessageType.UploadComplete,
                JsonConvert.SerializeObject(result)));
        }

        private void HandleDownloadStart(string data)
        {
            try
            {
                var remotePath = data?.Trim('"');
                SLog.Info($"[Download] {_session.Tag} 下载文件: {remotePath}");

                // 检查文件是否存在
                if (!File.Exists(remotePath))
                {
                    SendError($"文件不存在 / File not found: {remotePath}");
                    return;
                }

                // 检查文件是否可读
                try
                {
                    using (var fs = new FileStream(remotePath, FileMode.Open, FileAccess.Read))
                    {
                        // 仅测试打开，立即关闭
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    SLog.Error($"[Download] {_session.Tag} 无读取权限: {remotePath}");
                    SendError($"无读取权限 / Access denied: {remotePath}");
                    return;
                }

                var startInfo = FileTransferHandler.PrepareDownload(remotePath, out var fileData);

                _session.Send(new ProtocolMessage(MessageType.DownloadStart,
                    JsonConvert.SerializeObject(startInfo)));

                for (int i = 0; i < startInfo.TotalChunks; i++)
                {
                    var chunk = FileTransferHandler.BuildChunk(fileData, i, startInfo.ChunkSize);
                    _session.Send(new ProtocolMessage(MessageType.DownloadChunk,
                        JsonConvert.SerializeObject(chunk)));
                }

                _session.Send(new ProtocolMessage(MessageType.DownloadComplete,
                    JsonConvert.SerializeObject(new FileTransferComplete
                    {
                        Success = true,
                        Message = "Download completed",
                        FileName = startInfo.FileName
                    })));
            }
            catch (Exception ex)
            {
                SLog.Error($"Download failed", ex);
                SendError($"Download failed: {ex.Message}");
            }
        }

        // ===== 客户端管理 =====
        private void HandleListClients()
        {
            var list = GetClientList();
            _session.Send(new ProtocolMessage(MessageType.ClientList,
                JsonConvert.SerializeObject(new ClientListData { Clients = list })));
        }

        private void HandleKickClient(string data)
        {
            var req = JsonConvert.DeserializeObject<KickRequestData>(data);
            KickClient(req.ConnectionId, ID);
        }

        private void SendError(string message)
        {
            _session.Send(new ProtocolMessage(MessageType.Error,
                JsonConvert.SerializeObject(new ErrorData { Message = message })));
        }
    }
}
