namespace SSHClient.Core
{
    /// <summary>
    /// 非交互模式下 SSHC.exe 的退出码规范。
    /// 仿 OpenSSH：连接层错误用高位码（253-255），命令本身的 exit code 透传。
    /// </summary>
    public static class ExitCodes
    {
        public const int Success          = 0;    // 命令执行成功 (exit 0)
        public const int Interrupted      = 130;  // Ctrl+C / 中断
        public const int ProtocolError    = 253;  // 标记丢失 / 协议异常 / 参数错误
        public const int AuthFailed       = 254;  // 用户名或密码错误
        public const int ConnectionFailed = 255;  // 连不上目标 / 握手超时
        // 其他退出码 = 远程命令的 %ERRORLEVEL% 透传
    }
}
