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

        public void Send(ProtocolMessage msg)
        {
            try
            {
                SendJson?.Invoke(msg.ToJson());
            }
            catch { }
        }

        public void Cleanup()
        {
            Shell?.Dispose();
            Shell = null;
            FileTransfer = null;
        }
    }
}
