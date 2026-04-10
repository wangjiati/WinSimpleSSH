# 报文混淆方案

> 实现日期：2026-04-10
> 代码位置：`src/SSHCommon/Crypto/Obfuscator.cs`

## 目标

安全团队随机抓包时，Wireshark 看不到明文 JSON。不追求密码学安全，追求：
1. 抓包工具无法直接显示原文
2. 不能用简单手段（如 base64 解码）还原
3. 零配置、零握手、无状态
4. 速度极快，不影响文件传输吞吐量

## 设计约束

- **CLI 非交互模式**：每次调用 = connect → auth → execute → disconnect，不允许加握手步骤
- **无配置**：不需要在 server.json 或客户端加任何参数
- **向后兼容**：客户端和服务端必须同时更新，不做新旧版本兼容

## 算法

### 置换表生成（编译期固定）

```
1. 初始化 table[0..255] = [0, 1, 2, ..., 255]
2. 用 xorshift32 PRNG（固定种子 0x5F3759DF）驱动 Fisher-Yates 洗牌
3. 得到一张确定性的 256 字节全排列置换表
4. 同时生成反查表：reverseTable[table[i]] = i
```

客户端和服务端编译的是同一份 `Obfuscator.cs`，所以 table 完全一致。

### 混淆（每条消息）

```
输入: JSON 字符串 → UTF-8 字节数组 input[]
随机: offset = 随机 1 字节 (0~255)

输出: output[0] = offset
      output[i+1] = table[(input[i] + offset + i) & 0xFF]
                     ─────  ────────   ──────────────────
                     查表    原始字节    偏移(随机+位置)
```

### 还原

```
输入: 二进制帧 data[]
读取: offset = data[0]

输出: output[i] = (reverseTable[data[i+1]] - offset - i) & 0xFF
```

### 为什么需要 `+ i`（位置偏移）

JSON 报文中大量重复字符（`"`, `{`, `:`, `\`），如果只用固定 offset，
相同字符总是映射到相同输出，Wireshark 的"查找字符串"功能仍可能定位模式。
加上 `+ i` 后，同一个字符在不同位置产生不同输出，彻底打破频率特征。

## 数据流

```
发送方:
  ProtocolMessage → .ToJson() → UTF-8 bytes → Obfuscator.Encode() → WebSocket Send(byte[])
                                                                     ^^^^^^^^^^^^^^^^^^^^^^^^
                                                                     二进制帧，非文本帧

接收方:
  WebSocket OnMessage → e.RawData → Obfuscator.Decode() → UTF-8 string → HandleMessage()
```

## 改动清单

| 文件 | 改动 |
|------|------|
| `SSHCommon/Crypto/Obfuscator.cs` | **新增** — 置换表 + Encode/Decode |
| `SSHServer/Core/ConnectionManager.cs` | SendJson 回调改为 `Send(byte[])`；OnMessage 从 `e.RawData` 解码 |
| `SSHClient/Core/RemoteShell.cs` | SafeSend 改为 `Send(byte[])`；OnMessage 从 `e.RawData` 解码 |
| `SSHClient/Core/FileTransfer.cs` | SendLocked 改为 `Send(byte[])` |

## 安全性分析

**能防的：**
- Wireshark 随机抓包（看到二进制 hex dump，无可读内容）
- 简单手段还原（不是 base64，不是简单 XOR，需要知道置换表 + 算法）
- 跨消息模式分析（每条消息 offset 不同，同样的 JSON 产生不同密文）

**不能防的：**
- 逆向工程 exe 提取置换表后可解码所有流量
- 已知明文攻击（如果知道某条消息的原文，理论上可推导 offset 和部分表映射）

**对于内网工具来说这是合理的权衡。**

## 性能

- 每字节 1 次数组下标访问 ≈ 1ns
- 相比 AES-NI 加速的 AES（≈ 3ns/byte）快约 3 倍
- 每条消息额外开销：1 字节（offset）
- 对文件传输（64KB/chunk）的吞吐量影响可忽略

## Wireshark 对比

混淆前（文本帧，直接可读）：
```
{"type":"AuthRequest","data":"{\"username\":\"admin\",\"password\":\"admin123\"}"}
```

混淆后（二进制帧，hex dump）：
```
2A B4 F6 73 3D C0 19 46 E9 AE 3E 88 88 C0 61 85
48 D6 73 D6 98 9D E9 70 B4 9F 0B B4 64 A5 8B CC
B4 DA 7F FD 79 7F 8A 98 1E 0B D6 EC 86 79 42 3A
...
```
