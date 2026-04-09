namespace SSHCommon.Protocol
{
    public enum MessageType
    {
        // 认证
        AuthRequest,
        AuthResponse,

        // Shell
        ShellInput,
        ShellOutput,
        ShellError,

        // 中断
        Interrupt,

        // 文件上传 (客户端→服务端)
        UploadStart,
        UploadChunk,
        UploadComplete,

        // 文件下载 (服务端→客户端)
        DownloadStart,
        DownloadChunk,
        DownloadComplete,

        // 心跳
        Ping,
        Pong,

        // 超时
        TimeoutWarning,

        // 客户端管理
        ListClients,
        ClientList,
        KickClient,
        Kicked,

        // 通用
        Error,
        Disconnect
    }
}
