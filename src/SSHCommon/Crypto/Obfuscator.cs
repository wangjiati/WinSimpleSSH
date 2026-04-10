using System;

namespace SSHCommon.Crypto
{
    /// <summary>
    /// 报文混淆器 — 固定置换表 + 随机偏移。
    /// 每条消息独立编解码，无握手、无状态。
    /// 用途：让 Wireshark 抓包看不到明文 JSON，非密码学安全。
    /// </summary>
    public static class Obfuscator
    {
        private static readonly byte[] _table = new byte[256];
        private static readonly byte[] _reverseTable = new byte[256];

        [ThreadStatic]
        private static Random _rng;

        static Obfuscator()
        {
            // 初始化恒等映射
            for (int i = 0; i < 256; i++)
                _table[i] = (byte)i;

            // Fisher-Yates 洗牌，固定种子生成确定性置换表
            uint state = 0x5F3759DF;
            for (int i = 255; i > 0; i--)
            {
                state = Xorshift32(state);
                int j = (int)(state % (uint)(i + 1));
                var tmp = _table[i];
                _table[i] = _table[j];
                _table[j] = tmp;
            }

            // 生成反查表
            for (int i = 0; i < 256; i++)
                _reverseTable[_table[i]] = (byte)i;
        }

        private static uint Xorshift32(uint x)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x;
        }

        /// <summary>
        /// 混淆：[1字节随机偏移] + [逐字节查表替换]
        /// output[i+1] = table[(input[i] + offset + i) &amp; 0xFF]
        /// </summary>
        public static byte[] Encode(byte[] data)
        {
            if (_rng == null) _rng = new Random();
            var offset = (byte)_rng.Next(256);

            var output = new byte[data.Length + 1];
            output[0] = offset;

            for (int i = 0; i < data.Length; i++)
            {
                output[i + 1] = _table[(data[i] + offset + i) & 0xFF];
            }
            return output;
        }

        /// <summary>
        /// 还原：读取首字节偏移，逆向查表
        /// </summary>
        public static byte[] Decode(byte[] data)
        {
            if (data == null || data.Length < 1)
                return new byte[0];

            var offset = data[0];
            var output = new byte[data.Length - 1];

            for (int i = 0; i < output.Length; i++)
            {
                output[i] = (byte)((_reverseTable[data[i + 1]] - offset - i) & 0xFF);
            }
            return output;
        }
    }
}
