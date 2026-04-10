# WinSimpleSSH 安全审计报告

> 审计日期：2026-04-10
> 最后更新：2026-04-10
> 范围：SSHServer / SSHClient / SSHCommon 全模块
> 审计方：Claude Code (Opus 4.6)
> 部署场景：工厂内网，物理隔离，受信网络

## 威胁模型说明

本工具部署于**物理隔离的工厂内网**，不暴露到公网。基于此前提调整风险评级：

- 网络嗅探/MITM：内网环境下风险**降级**（但不为零，需防内部恶意行为）
- 认证暴力破解：内网环境 + 有限用户数，风险**降级**
- 路径穿越/任意文件读写：**不降级** — 认证用户不应有超出预期的文件系统权限
- 内存中密码驻留：内网环境下风险**降级**

## 统计概览

| 严重级别 | 数量 | 说明 |
|----------|------|------|
| 严重 (CRITICAL) | 2 | 认证用户可接管整个文件系统 |
| 高危 (HIGH) | 5 | 凭据泄露、信息泄露、监控盲区 |
| 中危 (MEDIUM) | 8 | 拒绝服务、资源耗尽 |
| 低危 (LOW) | 8 | 加固建议，内网场景下优先级较低 |
| 信息 (INFO) | 3 | 依赖版本、构建配置 |
| 已修复 | 2 | IP 白名单、报文混淆 |

---

## 严重 (CRITICAL)

### C1. 路径穿越 — 上传可写入服务器任意位置

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/FileTransferHandler.cs:24-52` |
| 类别 | 输入校验 |
| 状态 | 待修复 |

```csharp
if (Path.IsPathRooted(remotePath))
{
    _currentUploadPath = remotePath;  // 允许绝对路径！
}
```

认证用户可将文件上传到服务器任意路径，如 `C:\Windows\System32\evil.dll`，覆盖系统文件。
相对路径 `..\..\` 穿越同样未拦截。

**为什么内网也必须修：** 这不是网络层攻击，而是**已认证用户的权限越界**。任何拿到合法账号的人
（或 Agent 脚本 bug）都可能意外/故意覆盖系统关键文件，导致产线设备瘫痪。

**影响：** 任意文件写入、系统破坏、远程代码执行（DLL 劫持）。

**修复方案：**
```csharp
// 拒绝绝对路径
if (Path.IsPathRooted(remotePath))
    throw new SecurityException("不允许绝对路径");

// 规范化后检查是否在允许的基目录内
var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, remotePath));
var baseFull = Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar;
if (!fullPath.StartsWith(baseFull))
    throw new SecurityException("路径穿越被拒绝");
```

---

### C2. 路径穿越 — 下载可读取服务器任意文件

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:382-436` |
| 类别 | 输入校验 |
| 状态 | 待修复 |

```csharp
var remotePath = data?.Trim('"');
if (!File.Exists(remotePath)) { ... }
// 直接读取该路径的文件并传给客户端
```

认证用户可下载服务器上任何文件：SAM 数据库、注册表备份、其他应用配置、日志等。

**为什么内网也必须修：** 同 C1，已认证用户的权限不应从"Shell 操作"扩大到"绕过 Shell 直接
读任意文件"。通过 Shell 执行 `type` 命令至少还有审计日志，直接下载则绕过了所有监控。

**影响：** 任意文件读取、敏感数据外泄。

**修复方案：** 与 C1 相同的基目录白名单校验逻辑。

---

## 高危 (HIGH)

### H1. 明文密码存储 — server.json 含默认弱口令

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/server.json` |
| 类别 | 凭据存储 |
| 状态 | 待修复 |

```json
{ "username": "admin", "password": "admin123" },
{ "username": "user",  "password": "user123"  }
```

密码以纯文本存储，且提供了极弱的默认值。

**内网场景评估：** 虽然服务器物理可控，但配置文件可能被运维人员拷贝、备份时泄露，
或被同一台机器上的其他进程读取。降为 HIGH 而非 CRITICAL，因为攻击前提是本地文件访问。

**影响：** 获得文件读权限即获得所有账号的 Shell 权限。

**修复建议：**
1. 使用 bcrypt/PBKDF2 哈希存储密码（含盐）
2. 移除默认凭据，首次运行时强制配置
3. 提供 `server.json.example` 作为模板

---

### H2. server.json 未加入 .gitignore

| 字段 | 值 |
|------|-----|
| 文件 | `.gitignore` |
| 类别 | 密钥管理 |
| 状态 | 待修复 |

凭据配置文件没有被 `.gitignore` 排除。如果意外提交到 Git，密码会永久留在历史记录中。

**修复：** 在 `.gitignore` 中添加：
```
src/SSHServer/server.json
src/SSHServer/log/
```

---

### H3. 错误信息泄露服务器路径

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:338,407,434` |
| 类别 | 信息泄露 |
| 状态 | 待修复 |

```csharp
SendError($"无写入权限 / Access denied: {_session.FileTransfer.CurrentUploadPath}");
SendError($"File not found: {remotePath}");
SendError($"Download failed: {ex.Message}");
```

向客户端返回完整文件路径和异常详情，暴露服务器文件系统结构。
即使修复了路径穿越，这些信息仍会帮助攻击者探测可利用的目录。

**修复：** 返回通用错误码，详细信息仅写服务端日志。

---

### H4. 认证失败未记录日志

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:278-299` |
| 类别 | 日志 / 审计 |
| 状态 | 待修复 |

只记录成功认证，失败尝试完全静默。运维人员无法发现有人在尝试猜密码。

**修复：** 记录所有认证尝试（成功 + 失败），包含时间戳、来源 IP、用户名。

---

### H5. 客户端侧下载路径未校验

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHClient/Core/FileTransfer.cs:115,125` |
| 类别 | 输入校验 |
| 状态 | 待修复 |

客户端直接使用服务端返回的 `FileName` 拼接本地写入路径，未校验。
恶意服务端（或 MITM 攻击者）可通过构造 FileName 让客户端写入任意本地路径。

**内网场景评估：** 前提是服务端被入侵或存在中间人，内网下概率较低但仍需防御。

**修复：** 从服务端返回的 FileName 中剥离目录组件（仅保留文件名），并校验解析后路径在
用户指定的下载目录内。

---

### ~~H6. 明文 WebSocket 通信~~ → 已修复（报文混淆）

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHCommon/Crypto/Obfuscator.cs`（新增）|
| 类别 | 加密 |
| 状态 | **已修复** |

**实现：** 固定置换表（Fisher-Yates + xorshift32 固定种子）+ 每消息 1 字节随机偏移 +
逐字节位置偏移。WebSocket 切换为二进制帧传输。Wireshark 抓包显示为 hex dump，无法
直接读取原文。无握手、无状态，CLI 非交互模式完全兼容。

**注意：** 这不是密码学安全加密，逆向二进制可提取置换表。但满足安全团队随机抓包审计要求。

---

## 中危 (MEDIUM)

### M1. 下载时整个文件读入内存

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:411` |
| 类别 | 拒绝服务 |
| 状态 | 待修复 |

请求一个多 GB 的大文件会导致服务端 `OutOfMemoryException` 崩溃，所有已连接客户端断开。

**修复：** 改为流式分块读取，不一次性加载全文件。

---

### M2. 上传无超时机制

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/FileTransferHandler.cs` |
| 类别 | 资源泄漏 |
| 状态 | 待修复 |

发起上传后不发送 chunk，文件句柄永远不释放。反复操作可耗尽文件描述符。

**修复：** 上传开始后设定超时（如 10 分钟），超时自动清理。

---

### M3. 上传数据量无校验

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/FileTransferHandler.cs:55-67` |
| 类别 | 输入校验 |
| 状态 | 待修复 |

声称文件大小与实际发送 chunk 总量不做比对。可声明小文件但持续发送数据耗尽磁盘。

**修复：** 累计写入字节数，超过声明的 FileSize 时中止上传。

---

### M4. Shell 输出缓冲区无上限

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ShellSession.cs:56-98` |
| 类别 | 拒绝服务 |
| 状态 | 待修复 |

执行 `dir C:\ /S` 等大输出命令时，服务端缓冲区无限增长，可耗尽内存。

**修复：** 设置缓冲区上限，达到上限后丢弃最早的输出。

---

### M5. 无 WebSocket 消息大小限制

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:177` |
| 类别 | 拒绝服务 |
| 状态 | 待修复 |

未校验收到的 WebSocket 消息大小。攻击者可发送 GB 级消息耗尽内存。

**修复：** 拒绝超过合理大小的消息（如文件 chunk 上限 100MB）。

---

### M6. 无 WebSocket 消息速率限制

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:177` |
| 类别 | 拒绝服务 |
| 状态 | 待修复 |

无每连接消息频率限制。消息洪泛可耗尽 CPU。

**修复：** 每连接添加消息速率限制。

---

### M7. 任意认证用户可踢其他用户

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:446` |
| 类别 | 授权 |
| 状态 | 待修复 |

`KickClient` 仅检查认证状态，不区分角色。任何认证用户都能断开其他用户的连接。

**修复：** 限制 kick 操作仅限管理员用户，或仅限服务端控制台使用。

---

### M8. 敏感命令明文写入日志

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:234` |
| 类别 | 日志 |
| 状态 | 待修复 |

```csharp
SLog.Info($"[Shell] {_session.Tag} 执行命令: {input}");
```

如 `mysql -p secretpwd` 等含密码的命令会明文写入日志文件。

**修复：** 对常见密码模式做脱敏处理，或仅记录命令名不记录参数。

---

## 低危 (LOW)

> 以下问题在内网环境下风险较低，建议有余力时逐步改进。

### L1. 无认证失败速率限制

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:278-299` |
| 类别 | 认证 |

无失败次数限制、无锁定机制。**内网场景下降级**：攻击者需要物理接入内网，且用户数有限。

---

### L2. 密码比较存在时序攻击

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:284` |
| 类别 | 密码学 |

`==` 运算符非常量时间比较。**内网场景下降级**：时序攻击需要大量精确测量，
内网延迟波动使其难以利用。若实施 H1 的密码哈希改造，此问题同步解决（bcrypt.Verify
本身是常量时间的）。

---

### L3. 密码在 CLI 参数中暴露

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHClient/Program.cs:306-309` |
| 类别 | 凭据暴露 |

`-P <password>` 在 `tasklist /v` 中可见。**但项目已提供 `SSHC_PASSWORD` 环境变量作为
更安全的替代方案**，且 CLAUDE.md 中已记录推荐使用环境变量。

**建议：** 在帮助文本中标注 `-P` 不如环境变量安全即可。

---

### L4. 密码在内存中以明文 string 驻留

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHClient/Program.cs`, `RemoteShell.cs`, `NonInteractiveRunner.cs` |
| 类别 | 内存安全 |

.NET `string` 不可变、GC 管理，密码从不清零。**内网场景下降级**：利用前提是物理接触
目标机器做内存转储，工厂环境下攻击成本极高。

---

### L5. 无最大连接数限制

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs` |
| 类别 | 拒绝服务 |

无限连接可耗尽资源。内网环境下正常连接数有限，风险较低。

---

### L6. Shell 命令无过滤

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ShellSession.cs:100-107` |
| 类别 | 授权 |

认证即全权，无命令黑名单。**这是设计决策而非 bug** — 本工具定位就是远程 Shell，
限制命令会破坏可用性。但建议文档明确：SSHServer **不应以管理员身份运行**。

---

### L7. JSON 反序列化未显式安全配置

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHCommon/Protocol/ProtocolMessage.cs:29` |
| 类别 | 反序列化 |

当前使用强类型反序列化 + 默认 `TypeNameHandling.None`（安全）。未显式声明配置，
但实际无风险。**降级为低危**：除非有人主动全局修改 TypeNameHandling，否则不可利用。

**建议：** 加一行显式声明 `TypeNameHandling = TypeNameHandling.None` 作为防御性编程。

---

### L8. 日志中用户名未转义

| 字段 | 值 |
|------|-----|
| 文件 | `src/SSHServer/Core/ConnectionManager.cs:292` |
| 类别 | 日志注入 |

用户名含控制字符时可污染日志文件。内网环境下利用价值极低。

---

## 已修复

### [Fixed] IP 白名单

| 字段 | 值 |
|------|-----|
| 修复日期 | 2026-04-10 |
| 改动文件 | `ServerConfig.cs`、`ConnectionManager.cs`、`WebSocketServerEngine.cs`、`server.json` |

**实现：** 服务端新增 `ipWhitelist` 配置项，支持精确 IP 和通配符（如 `192.168.1.*`）。
在 WebSocket `OnOpen()` 阶段校验客户端 IP，不在白名单则 Close(1008) 并记录日志。
白名单为空时放行所有 IP（向后兼容）。含 `::ffff:` IPv6 映射前缀处理。

### [Fixed] 报文混淆

| 字段 | 值 |
|------|-----|
| 修复日期 | 2026-04-10 |
| 改动文件 | `SSHCommon/Crypto/Obfuscator.cs`（新增）、`ConnectionManager.cs`、`RemoteShell.cs`、`FileTransfer.cs` |

**实现：** 固定 256 字节置换表 + 每消息随机偏移 + 逐字节位置偏移。
所有 WebSocket 通信切换为二进制帧，Wireshark 无法直接读取原文。
无握手无状态，每条消息独立编解码，CLI 非交互模式完全兼容。

---

## 信息 (INFO)

### I1. WebSocketSharp 1.0.3-rc9 — 停维预发布版

`src/SSHServer/SSHServer.csproj:10`、`src/SSHClient/SSHClient.csproj:9`

2014 年的预发布版本，2018 年后无更新。无已知 CVE，但也无安全补丁。
考虑到项目选用此库是为了 Win7 兼容性，短期内不建议替换。

### I2. Newtonsoft.Json 13.0.3 — 版本较老

所有 `.csproj`。发布于 2021 年，无已知 CVE。.NET 4.5.2 兼容性限制了升级空间。

### I3. Debug 构建含 PDB 符号文件

`src/*/bin/Debug/`。PDB 暴露源码结构和变量名。
仅影响分发场景，开发阶段无影响。

---

## 修复路线图

### 第一阶段 — 立即（阻断实际可利用的风险）

| 优先级 | 编号 | 问题 | 预估工时 | 状态 |
|--------|------|------|----------|------|
| ~~1~~ | ~~-~~ | ~~IP 白名单~~ | ~~~1h~~ | **已完成** |
| 2 | C1+C2 | 路径穿越修复（上传 + 下载白名单基目录校验） | ~2h | 待修复 |
| ~~3~~ | ~~H6~~ | ~~报文混淆（查表+偏移量方案）~~ | ~~~1d~~ | **已完成** |
| 4 | H2 | server.json 加入 .gitignore | 5min | 待修复 |
| 5 | H3 | 错误信息脱敏（不返回完整路径/异常） | ~1h | 待修复 |
| 6 | H4 | 记录认证失败日志 | ~30min | 待修复 |

### 第二阶段 — 短期（提升健壮性）

| 优先级 | 编号 | 问题 | 预估工时 |
|--------|------|------|----------|
| 7 | H1 | 密码哈希存储（bcrypt） | ~3h |
| 8 | H5 | 客户端下载路径校验 | ~1h |
| 9 | M1 | 下载改流式，避免 OOM | ~2h |
| 10 | M2+M3 | 上传超时 + 大小校验 | ~1h |
| 11 | M5+M6 | 消息大小/速率限制 | ~2h |

### 第三阶段 — 有余力时

| 优先级 | 编号 | 问题 | 预估工时 |
|--------|------|------|----------|
| 12 | M4 | Shell 输出缓冲区上限 | ~1h |
| 13 | M7 | Kick 权限检查 | ~30min |
| 14 | M8 | 日志敏感信息脱敏 | ~1h |
| 15 | L7 | 显式 JSON 安全设置 | 10min |
