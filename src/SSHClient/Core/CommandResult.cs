using Newtonsoft.Json;

namespace SSHClient.Core
{
    /// <summary>
    /// 非交互模式下统一的结果 DTO。对应 docs/features/non-interactive-cli/02-design-decisions.md 的 D9 schema。
    /// </summary>
    public class CommandResult
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("verb")]
        public string Verb { get; set; }

        [JsonProperty("command")]
        public string Command { get; set; }

        [JsonProperty("exit_code")]
        public int? ExitCode { get; set; }

        [JsonProperty("stdout")]
        public string Stdout { get; set; }

        [JsonProperty("stderr")]
        public string Stderr { get; set; }

        [JsonProperty("duration_ms")]
        public long DurationMs { get; set; }

        [JsonProperty("error")]
        public ErrorDetail Error { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }
    }

    public class ErrorDetail
    {
        /// <summary>
        /// 错误类别枚举：connection_refused / connection_timeout / auth_failed /
        /// protocol_error / marker_not_found / interrupted
        /// </summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
