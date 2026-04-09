using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SSHServer.Config
{
    public class ServerConfig
    {
        [JsonProperty("port")]
        public int Port { get; set; } = 22222;

        [JsonProperty("users")]
        public List<UserConfig> Users { get; set; } = new List<UserConfig>();

        public static ServerConfig Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ServerConfig>(json);
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
