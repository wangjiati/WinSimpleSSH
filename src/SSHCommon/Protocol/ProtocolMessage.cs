using System.Collections.Generic;
using Newtonsoft.Json;

namespace SSHCommon.Protocol
{
    public class ProtocolMessage
    {
        [JsonProperty("type")]
        public MessageType Type { get; set; }

        [JsonProperty("data")]
        public string Data { get; set; }

        public ProtocolMessage() { }

        public ProtocolMessage(MessageType type, string data = null)
        {
            Type = type;
            Data = data ?? string.Empty;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }

        public static ProtocolMessage FromJson(string json)
        {
            return JsonConvert.DeserializeObject<ProtocolMessage>(json);
        }
    }

    // 认证请求
    public class AuthRequest
    {
        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }
    }

    // 认证响应
    public class AuthResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    // 文件传输起始
    public class FileTransferStart
    {
        [JsonProperty("fileName")]
        public string FileName { get; set; }

        [JsonProperty("fileSize")]
        public long FileSize { get; set; }

        [JsonProperty("chunkSize")]
        public int ChunkSize { get; set; }

        [JsonProperty("totalChunks")]
        public int TotalChunks { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("remotePath")]
        public string RemotePath { get; set; }
    }

    // 文件数据块
    public class FileChunk
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("data")]
        public string Data { get; set; }  // base64 encoded
    }

    // 文件传输完成
    public class FileTransferComplete
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("fileName")]
        public string FileName { get; set; }
    }

    // Shell 输出
    public class ShellOutputData
    {
        [JsonProperty("text")]
        public string Text { get; set; }
    }

    // 错误消息
    public class ErrorData
    {
        [JsonProperty("message")]
        public string Message { get; set; }
    }

    // 客户端信息
    public class ClientInfo
    {
        [JsonProperty("connectionId")]
        public string ConnectionId { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("remoteEndpoint")]
        public string RemoteEndpoint { get; set; }

        [JsonProperty("connectTime")]
        public string ConnectTime { get; set; }
    }

    // 客户端列表
    public class ClientListData
    {
        [JsonProperty("clients")]
        public List<ClientInfo> Clients { get; set; }
    }

    // 踢出请求
    public class KickRequestData
    {
        [JsonProperty("connectionId")]
        public string ConnectionId { get; set; }  // "all" = 踢出除自己外的所有
    }

    // 超时警告
    public class TimeoutWarningData
    {
        [JsonProperty("secondsRemaining")]
        public int SecondsRemaining { get; set; }
    }

    // 被踢出通知
    public class KickedData
    {
        [JsonProperty("reason")]
        public string Reason { get; set; }
    }
}
