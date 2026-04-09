using System;
using SSHCommon.Protocol;

namespace SSHServer.Core
{
    public class ClientSession
    {
        public string ConnectionId { get; set; }
        public string Username { get; set; }
        public string RemoteEndpoint { get; set; }
        public DateTime ConnectTime { get; set; }
        public DateTime LastActivity { get; set; }
        public bool IsAuthenticated { get; set; }

        // 超时状态
        public DateTime? TimeoutWarningTime { get; set; }

        // 功能组件
        public ShellSession Shell { get; set; }
        public FileTransferHandler FileTransfer { get; set; }

        // 发送回调
        public Action<string> SendJson { get; set; }
        public Action CloseConnection { get; set; }

        // 连接是否已关闭
        private volatile bool _closed;

        public void Send(ProtocolMessage msg)
        {
            if (_closed) return;
            try
            {
                SendJson?.Invoke(msg.ToJson());
            }
            catch
            {
                _closed = true;
            }
        }

        public void MarkClosed()
        {
            _closed = true;
        }

        public void Cleanup()
        {
            MarkClosed();
            Shell?.Dispose();
            Shell = null;
            FileTransfer = null;
        }
    }
}
