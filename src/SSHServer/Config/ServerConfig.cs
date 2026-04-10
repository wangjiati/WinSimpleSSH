using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Newtonsoft.Json;

namespace SSHServer.Config
{
    public class ServerConfig
    {
        [JsonProperty("port")]
        public int Port { get; set; } = 22222;

        [JsonProperty("ipWhitelist")]
        public List<string> IpWhitelist { get; set; } = new List<string>();

        [JsonProperty("users")]
        public List<UserConfig> Users { get; set; } = new List<UserConfig>();

        /// <summary>白名单是否启用（配置了至少一条规则）</summary>
        [JsonIgnore]
        public bool IsWhitelistEnabled => IpWhitelist != null && IpWhitelist.Count > 0;

        /// <summary>
        /// 检查 IP 是否在白名单中。
        /// 支持精确匹配（192.168.1.100）和通配符（192.168.1.*）。
        /// 白名单为空时放行所有 IP。
        /// </summary>
        public bool IsIpAllowed(string ip)
        {
            if (!IsWhitelistEnabled) return true;

            // 去掉 IPv6 映射前缀 (::ffff:192.168.1.1 → 192.168.1.1)
            if (ip.StartsWith("::ffff:"))
                ip = ip.Substring(7);

            return IpWhitelist.Any(rule => MatchIpRule(ip, rule));
        }

        private static bool MatchIpRule(string ip, string rule)
        {
            // 精确匹配
            if (rule == ip) return true;

            // 通配符匹配：按 . 拆分逐段比较，* 匹配任意段
            var ipParts = ip.Split('.');
            var ruleParts = rule.Split('.');

            if (ipParts.Length != 4 || ruleParts.Length != 4)
                return false;

            for (int i = 0; i < 4; i++)
            {
                if (ruleParts[i] == "*") continue;
                if (ruleParts[i] != ipParts[i]) return false;
            }
            return true;
        }

        public static ServerConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                var defaults = CreateDefaults();
                File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                return defaults;
            }

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ServerConfig>(json);
        }

        private static ServerConfig CreateDefaults()
        {
            return new ServerConfig
            {
                Port = 22222,
                IpWhitelist = new List<string>
                {
                    "127.0.0.1",
                    "192.168.0.*",
                    "192.168.1.*"
                },
                Users = new List<UserConfig>
                {
                    new UserConfig { Username = "admin", Password = "admin123" }
                }
            };
        }
    }

    public class UserConfig
    {
        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }
    }
}
