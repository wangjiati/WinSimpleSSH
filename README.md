# WinSimpleSSH - 简易远程 Shell 工具

通过 WebSocket + JSON 协议在局域网内实现远程 cmd.exe Shell 和文件传输。

解决 Windows 环境下无可用 SSH 且其他远程工具被杀毒软件拦截的问题。

---

## 目录

- [特性](#特性)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [编译](#编译)
- [使用说明](#使用说明)
  - [服务端](#服务端)
  - [配置文件](#配置文件)
  - [客户端](#客户端)
  - [交互命令](#交互命令)
- [连接管理](#连接管理)
  - [多客户端连接](#多客户端连接)
  - [超时断开](#超时断开)
  - [心跳保活](#心跳保活)
  - [管理��令](#管理命令)
- [通信协议](#通信协议)
- [项目结构](#项目结构)
- [依赖](#依赖)
- [更新日志](#更新日志)
- [注意事项](#注意事项)

---

## 特性

- **单文件部署** — Costura.Fody 嵌入依赖，编译为独立 exe，无需附带 DLL
- **交互式 Shell** — 远程执行 cmd.exe 命令，实时输出，体验类似真实 SSH
- **文件传输** — 支持上传和下载，64KB 分块传输，带进度条显示，权限预检
- **多客户端** — 支持多个客户端同时连接，同一用户名可多次登录
- **超时保护** — 10分钟无活动自动断开（先警告，10秒后执行）
- **心跳保活** — 客户端每30秒发送心跳，防止空闲超时
- **管理能力** — 服务端/客户端均可列出连接、踢出指定客户端
- **日志系统** — 服务端自动记录操作日志到 exe 同级 log 目录
- **快捷操作** — 支持 Ctrl+C 中断命令、上下箭头翻历史命令
- **中英双���** — 所有帮助和提示信息均支持中英双语显示
- **广泛兼容** — 支持 Windows 7 / 10 / 11，基于 .NET Framework 4.5.2

## 环境要求

| 项目 | 要求 |
|------|------|
| 运行时 | .NET Framework 4.5.2 及以上（Win10/11 已内置） |
| 网络 | 局域网环境 |
| 编译 | .NET SDK + .NET Framework 4.5.2 Targeting Pack |

> Win7 用户需手动安装 [.NET Framework 4.5.2](https://dotnet.microsoft.com/download/dotnet-framework/net452)

## 快速开始

### 1. 编译

```bash
git clone https://github.com/wangjiati/WinSimpleSSH.git
cd WinSimpleSSH
dotnet build WinSimpleSSH.sln
```

### 2. 启动服务端

```bash
cd src/SSHServer/bin/Debug/net452/
SSHServer.exe
```

输出：
```
SSH Server started on port 22222
服务端以管理员权限运行 / Running as Administrator
关闭窗口即可停止服务器
```

### 3. 客户端连接

```bash
cd src/SSHClient/bin/Debug/net452/
SSHC.exe connect 192.168.1.100 -u admin
```

输入密码后即可进入远程 Shell。

## 编译

```bash
dotnet build WinSimpleSSH.sln            # Debug 编译
dotnet build WinSimpleSSH.sln -c Release # Release 编译
```

编译输出为单文件，无需附带 DLL：

| 应用 | 路径 |
|------|------|
| 服务端 | `src/SSHServer/bin/Debug/net452/SSHServer.exe` |
| 客户端 | `src/SSHClient/bin/Debug/net452/SSHC.exe` |

### 部署文件清单

```
SSHServer/
├── SSHServer.exe      # 单文件主程序（内含所有依赖）
└── server.json        # 配置文件（必须）

SSHC/
└── SSHC.exe           # 单文件主程序（内含所有依赖）
```

## 使用说明

### 服务端

直接双击 `SSHServer.exe` 或命令行启动：

```bash
SSHServer.exe
```

- 启动后读取同目录下 `server.json` 配置文件
- 控制台窗口保持打开，显示连接/断开/认证/超时日志
- 日志文件自动保存到 exe 同级 `log/` 目录，按日期滚动
- 服务端终端不接受输入，**Ctrl+C** 或关闭窗口即可停止

### 配置文件

`server.json` 必须与 `SSHServer.exe` 放在同一目录：

```json
{
  "port": 22222,
  "users": [
    {
      "username": "admin",
      "password": "admin123"
    },
    {
      "username": "user",
      "password": "user123"
    }
  ]
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `port` | int | WebSocket 监听端口，默认 `22222` |
| `users` | array | 允许登录的用户列表 |
| `users[].username` | string | 用户名 |
| `users[].password` | string | 密码（明文） |

> 同一用户名可以被多个客户端同时使用，没有限制。

### 客户端

```bash
SSHC.exe connect <主机IP> [-p <端口>] -u <用户名>
```

**参数说明：**

| 参数 | 必填 | 说明 |
|------|------|------|
| `<主机IP>` | 是 | 服务端 IP 地址 |
| `-p <端口>` | 否 | 服务端端口，默认 `22222` |
| `-u <用户名>` | 是 | 登录用户名 |

**连接示例：**

```bash
# 使用默认端口 22222
SSHC.exe connect 192.168.1.100 -u admin

# 指定端口
SSHC.exe connect 192.168.1.100 -p 30000 -u admin
```

连接后会提示输入密码（输入时显示为 `***`），认证成功后进入交互式 Shell。密码错误时程序自动退出。

### 交互命令

连接成功后进入 `ssh>` 提示符，支持以下操作：

| 命令 | 说明 | 示例 |
|------|------|------|
| `<命令>` | 执行远程 cmd 命令 | `ssh> ipconfig` |
| `upload <本地> [远程]` | 上传文件到服务端 | `ssh> upload "C:\test.txt" "D:\test.txt"` |
| `download <远程> [本地]` | 从服务端下载文件 | `ssh> download "D:\log.txt" "C:\log.txt"` |
| `clients` | 列出所有已连接客户端 | `ssh> clients` |
| `kick <id>` | 断开指定客户端 | `ssh> kick abc12345` |
| `kick all` | 断开除自己外的所有客户端 | `ssh> kick all` |
| `cls` / `clear` | 清屏 | `ssh> cls` |
| `help` | 显示中英双语帮助 | `ssh> help` |
| `exit` / `quit` | 断开连接并退出 | `ssh> exit` |

> 文件路径支持引号包裹，可处理含空格的路径。

**快捷键：**

| 按键 | 功能 |
|------|------|
| `Ctrl+C` | 中断当前正在执行的远程命令 |
| `↑` / `↓` | 翻阅历史命令 |
| `←` / `→` | 移动光标 |
| `Backspace` | 删除光标前字符 |

**文件传输示例：**

```
ssh> upload "C:\data\report.xlsx" "D:\reports\report.xlsx"
  [████████████████████████████████████████] 100.0% 2.3MB/2.3MB
Upload completed: D:\reports\report.xlsx / 上传完成

ssh> download "D:\logs\app.log" "app.log"
  [████████████████████████████████████████] 100.0% 5.1MB/5.1MB
```

> 文件路径不指定时，上传目标默认为服务端工作目录，下载目标默认为客户端当前目录。

## 连接管理

### 多客户端连接

- 服务端支持**多个客户端同时连接**
- 每个客户端连接拥有独立的 cmd.exe Shell 会话和文件传输通道
- **同一用户名可以被多个客户端同时使用**，互不影响
- 每个连接分配唯一 Connection ID，用于管理操作

### 超时断开

服务端自动检测空闲连接并断开：

```
时间线：
0s          10min              10min + 10s
|-- 正常活动 --|-- 无任何消息 --|-- 警告 --|-- 断开 --|
```

1. **10分钟无活动** → 服务端发送超时警告
   ```
   [Warning] 即将断开连接: 10秒内无活动 / Connection timeout in 10s
   ```
2. **警告后10秒仍无活动** → 服务端主动关闭连接
3. 如果警告后客户端发送了任何消息（包括心跳），超时计时器重置，连接保持

### 心跳保活

- 客户端每 **30秒** 自动发送 Ping 消息
- 服务端回复 Pong，同时刷新活动时间
- 只要心跳正常运行，空闲连接不会被超时断开
- 心跳在后台自动运行，无需用户干预

### 管理命令

**服务端：** 关闭窗口或 Ctrl+C 停止。

**客户端管理命令（在 `ssh>` 提示符下输入）：**

| 命令 | 说明 |
|------|------|
| `clients` | 请求服务端返回所有已连接客户端列表 |
| `kick <id>` | 断开指定 Connection ID 的客户端 |
| `kick all` | 断开除自己外的所有其他客户端 |

**列出客户端示例：**

```
ssh> clients
  ID       User         Endpoint                 Connect Time
  -----------------------------------------------------------------
  6a3f2d   admin        192.168.1.50:52341        2026-04-09 14:30:22
  8b7e1c   admin        192.168.1.60:48910        2026-04-09 14:35:10
  4d9a5b   user         192.168.1.70:61203        2026-04-09 14:40:55
  Total: 3 client(s) / 共 3 个客户端
```

**被踢出的客户端会收到通知：**

```
[Kicked] 您已被断开 / You were disconnected: 管理员断开 / Kicked by administrator
```

## 通信协议

客户端与服务端通过 WebSocket 通信，所有消息均为 JSON 文本格式。

### 消息格式

```json
{
  "type": "<消息类型>",
  "data": "<JSON字符串或纯文本>"
}
```

### 消息类型

| 类型 | 方向 | 说明 |
|------|------|------|
| **认证** | | |
| `AuthRequest` | C → S | 登录请求，data 含 username/password |
| `AuthResponse` | S → C | 认证结果，data 含 success/message |
| **Shell** | | |
| `ShellInput` | C → S | 发送命令输入 |
| `ShellOutput` | S → C | 返回命令标准输出 |
| `ShellError` | S → C | 返回命令标准错误输出 |
| `Interrupt` | C → S | 中断当前命令（Ctrl+C） |
| **心跳** | | |
| `Ping` | C → S | 心跳（客户端每30秒发送） |
| `Pong` | S → C | 心跳回复 |
| **超时** | | |
| `TimeoutWarning` | S → C | 超时警告，data 含 secondsRemaining |
| **客户端管理** | | |
| `ListClients` | C → S | 请求客户端列表 |
| `ClientList` | S → C | 返回客户端列表，data 含 clients 数组 |
| `KickClient` | C → S | 踢出客户端，data 含 connectionId（"all"=踢出所有） |
| `Kicked` | S → C | 被踢出通知，data 含 reason |
| **文件上传** | | |
| `UploadStart` | C → S | 开始上传，data 含 fileName/fileSize/chunkSize |
| `UploadReady` | S → C | 服务端确认文件可写，客户端收到后开始发送数据 |
| `UploadChunk` | C → S | 传输数据块，data 含 index + base64 数据 |
| `UploadComplete` | C → S | 上传结束通知 |
| `UploadComplete` | S → C | 上传结果确认 |
| **文件下载** | | |
| `DownloadStart` | C → S | 请求下载，data 为远程文件路径 |
| `DownloadStart` | S → C | 下载开始，data 含文件元信息 |
| `DownloadChunk` | S → C | 传输数据块 |
| `DownloadComplete` | S → C | 下载完成通知 |
| **通用** | | |
| `Error` | S → C | 错误消息 |
| `Disconnect` | 双向 | 断开连接 |

### 通信流程示例

**认证 + Shell：**

```
Client                          Server
  |------ AuthRequest ---------->|
  |<----- AuthResponse ----------|
  |------ ShellInput (dir\n) --->|
  |<----- ShellOutput (输出) ---->|
  |------ ShellInput (ipconfig)->|
  |<----- ShellOutput (输出) ---->|
  |------ Interrupt (Ctrl+C) --->|  (中断并重启 cmd.exe)
```

**心跳保活：**

```
Client                          Server
  |------ Ping ----------------->|  (每30秒)
  |<----- Pong ------------------|
```

**超时断开：**

```
Client                          Server
  |                              |  (10分钟无任何消息)
  |<----- TimeoutWarning --------|  (警告: 10秒后断开)
  |                              |  (10秒后仍无活动)
  |          连接关闭              |
```

**文件上传（带权限预检）：**

```
Client                          Server
  |------ UploadStart ---------->|  (文件名、大小、分块数)
  |<----- UploadReady -----------|  (权限检查通过，可以开始)
  |------ UploadChunk (0) ------>|  (base64 数据)
  |------ UploadChunk (1) ------>|
  |------ ... ------------------>|
  |------ UploadComplete ------->|
  |<----- UploadComplete --------|  (确认结果)
```

> 若服务端无写入权限，返回 Error 而非 UploadReady，客户端立即提示，不会浪费传输时间。

**文件下载：**

```
Client                          Server
  |------ DownloadStart -------->|  (请求文件路径)
  |<----- DownloadStart ---------|  (文件元信息)
  |<----- DownloadChunk (0) -----|  (base64 数据)
  |<----- DownloadChunk (1) -----|
  |<----- ... -------------------|
  |<----- DownloadComplete ------|  (传输完成)
```

### 编码处理

- cmd.exe 默认输出 GBK 编码
- 服务端启动 cmd.exe 时设置 `StandardOutputEncoding = GBK`
- 读取后 .NET 内部转为 UTF-16 字符串
- 通过 JSON/WebSocket 传输时自动为 UTF-8
- 客户端接收后直接显示，无需额外转码

### 文件传输细节

- 分块大小：64KB（65536 字节）
- 编码方式：每块原始字节 → Base64 字符串 → JSON 传输
- 进度计算：`当前块序号 / 总块数`
- 权限预检：上传前服务端先创建文件验证写权限，下载前验证读权限

## 项目结构

```
WinSimpleSSH/
├── WinSimpleSSH.sln               # 解决方案文件
├── README.md
├── CLAUDE.md
├── 头脑风暴.txt                      # 原始需求文档
│
└── src/
    ├── SSHCommon/                   # 共享协议库
    │   ├── SSHCommon.csproj
    │   └── Protocol/
    │       ├── MessageType.cs       #   消息类型枚举
    │       └── ProtocolMessage.cs   #   JSON 协议模型
    │
    ├── SSHServer/                   # 服务端
    │   ├── SSHServer.csproj
    │   ├── Program.cs               #   入口（仅显示日志，不接受输入）
    │   ├── WebSocketServerEngine.cs #   WebSocket 服务引擎
    │   ├── server.json              #   配置文件
    │   ├── Config/
    │   │   └── ServerConfig.cs      #   配置模型与加载
    │   └── Core/
    │       ├── ClientSession.cs     #   客户端会话数据模型
    │       ├── ConnectionManager.cs #   多连接管理、认证、超时检测
    │       ├── ShellSession.cs      #   cmd.exe 进程管理
    │       ├── FileTransferHandler.cs # 文件传输处理
    │       └── Logger.cs            #   日志系统（控制台+文件）
    │
    └── SSHClient/                   # 客户端
        ├── SSHClient.csproj
        ├── FodyWeavers.xml          #   Costura.Fody 配置
        ├── Program.cs               #   CLI 入口、交互循环
        └── Core/
            ├── RemoteShell.cs       #   WebSocket 连接、消息处理、心跳
            └── FileTransfer.cs      #   文件传输 + 进度条
```

## 依赖

| 库 | 版本 | 用途 |
|----|------|------|
| [WebSocketSharp](https://www.nuget.org/packages/WebSocketSharp/) | 1.0.3-rc9 | WebSocket 服务端+客户端通信，纯 C# 实现，Win7 兼容 |
| [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json/) | 13.0.3 | JSON 序列化与反序列化 |
| [Costura.Fody](https://www.nuget.org/packages/Costura.Fody/) | 5.7.0 | 将依赖 DLL 嵌入 exe，实现单文件部署 |

## 更新日志

### v1.1.0 (2026-04-09)

**稳定性修复：**
- 修复客户端异常断开时服务端 Fatal 报错，降级为 WARN
- 修复 `ClientSession.Send` 向已关闭连接写数据的连锁异常
- 修复 WebSocket `Send` 线程安全问题（心跳与上传并发导致帧损坏）
- 修复大文件上传时 `UploadComplete` 先于 chunk 处理导致 NullReferenceException

**上传/下载改进：**
- 新增 `UploadReady` 握手协议，权限不足时客户端立即收到提示，不再浪费传输
- 上传/下载前预检文件读写权限
- 文件路径支持引号包裹（处理含空格路径）
- 下载失败时显示具体错误信息

**日志系统：**
- 新增 `SLog` 日志模块，控制台彩色输出 + 文件持久化
- 日志按日期滚动，存放在 exe 同级 `log/` 目录
- 记录连接/断开/认证/Shell 命令/文件传输/超时等详细操作
- 日志中显示用户名+短连接ID，区分同名多客户端

**客户端改进：**
- 客户端 exe 改名为 `SSHC.exe`，便于命令行调用
- 密码认证失败时自动退出程序
- 新增 `cls` / `clear` 清屏命令

**服务端改进：**
- 终端完全禁止输入，防止光标卡住（Ctrl+C 或关闭窗口停止）
- 编译为单文件 exe（Costura.Fody 嵌入依赖）
- 启动时记录是否以管理员权限运行

## 注意事项

- **仅限局域网** — 通信不加密（明文 WebSocket），请勿暴露到公网
- **多客户端** — 支持多客户端同时连接，同一用户名可多次登录
- **超时机制** — 10分钟无活动自动断开，心跳可保持连接
- **非正常断开** — 客户端直接关闭时，服务端通过心跳超时自动检测并清理资源
- **Ctrl+C 行为** — 中断当前命令后，服务端会终止并重新启动 cmd.exe 进程
- **配置安全** — 密码以明文存储在 `server.json` 中，注意文件访问权限
- **文件路径** — 支持 Windows 绝对路径和相对路径，相对路径基于服务端工作目录
- **单文件部署** — exe 已内含所有依赖，分发时只需拷贝 exe + 配置文件
- **界面语言** — 所有帮助和提示信息支持中英双语显示
