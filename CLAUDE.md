# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

简易 SSH 工具，通过 WebSocket + JSON 协议在局域网内实现远程 cmd.exe Shell 和文件传输。解决 Windows 环境下无可用 SSH 且其他远程工具被杀毒软件拦截的问题。

## Tech Stack

- .NET Framework 4.5.2 (客户端+服务端)
- WebSocket 通信，JSON 报文格式
- Win7 / Win10 / Win11 兼容

## Architecture

两个独立应用 + 共享协议库：

- **Server** — 监听 WebSocket 端口(默认 22222)，管理 cmd.exe 进程，处理文件传输。支持多客户端连接、超时断开(10分钟无活动)、IP 白名单、控制台管理命令
- **Client (SSHC)** — 双模式 CLI：
  - **交互模式** `SSHC connect <ip> -u <user>`：进入 `ssh>` REPL，密码提示、历史命令、Ctrl+C 中断、文件传输进度条
  - **非交互模式** `SSHC <verb> <ip> -u <user> -P <pwd> ...`：4 个动词 `exec` / `start` / `upload` / `download`，Agent / 脚本一次性调用，支持 `--json` 输出和 OpenSSH 风格退出码
- **Common** — 共享协议层，MessageType 枚举 + JSON 消息模型

## Build

```
"C:/Users/Administrator/.dotnet/dotnet.exe" build WinSimpleSSH.sln
```

> 本机 `C:\Program Files\dotnet\` 只有 .NET 8 运行时，没有 SDK。用户级 .NET 10 SDK 在 `C:\Users\Administrator\.dotnet\sdk\10.0.201\`。或在 Rider 里直接 Build Solution。

Output: `src/SSHServer/bin/Debug/net452/SSHServer.exe` and `src/SSHClient/bin/Debug/net452/SSHC.exe`

## Project Structure

- `src/SSHCommon/Protocol/` — 消息类型枚举、JSON 协议模型（服务端+客户端共享）
- `src/SSHCommon/Crypto/Obfuscator.cs` — 报文混淆器（固定置换表 + 随机偏移，Encode/Decode）
- `src/SSHServer/` — WebSocket 服务端（WebSocketSharp）、多连接管理、Shell 会话、文件传输、超时检测、IP 白名单
- `src/SSHClient/` — CLI 客户端
  - `Program.cs` — verb 分派入口（connect / exec / start / upload / download / help），交互式 REPL `RunConnect`，非交互入口 `RunNonInteractive`
  - `Core/RemoteShell.cs` — WebSocket 连接、消息处理、心跳；`SetSignalHandler` + `SetShellOutputHandler` 双拦截接口
  - `Core/NonInteractiveRunner.cs` — 4 个非交互动词的实现，含 `TryConnectAndAuth` 共享 helper
  - `Core/OutputCapture.cs` — 两阶段标记（begin + end）+ 线程安全 buffer，处理 cmd.exe 命令完成检测
  - `Core/CommandResult.cs` — 非交互模式的 JSON 结果 DTO
  - `Core/ExitCodes.cs` — 退出码常量（0/130/253/254/255）
  - `Core/FileTransfer.cs` — 文件分块上传/下载，含 `Quiet` 静态开关供非交互模式静音

## Dependencies

- WebSocketSharp (WebSocket 通信，兼容 Win7)
- Newtonsoft.Json (JSON 序列化)
- Costura.Fody (将依赖嵌入 exe，单文件部署)

## Key Design Constraints

**通用：**
- 多客户端连接，每个连接独立 Shell 会话和文件传输
- 10分钟无活动超时：警告 → 10秒后断开
- 客户端心跳(30秒 Ping)保持连接活跃
- 非正常断开通过 OnClose 事件清理资源
- 用户名+密码认证，凭据存储在服务端 JSON 配置文件
- IP 白名单：server.json 中 `ipWhitelist` 配置允许连接的 IP，支持精确匹配和通配符（如 `192.168.1.*`）。
  白名单为空时放行所有 IP（向后兼容）。连接阶段（OnOpen）校验，拒绝的 IP 记录日志
- 报文混淆：固定置换表 + 每消息随机偏移，WebSocket 二进制帧传输。Wireshark 抓包看不到明文 JSON。
  混淆层在 `SSHCommon/Crypto/Obfuscator.cs`，客户端和服务端共用同一套编解码，无握手无状态
- cmd.exe 输出 GBK → 服务端转 UTF-8 后传输

**交互模式：**
- 文件传输需显示进度条
- 支持 Ctrl+C 中断命令、上下箭头历史命令
- 服务端和客户端帮助提示支持中英双语
- 保留 cmd.exe 默认 GBK 输出，避免破坏老用户终端

**非交互模式（Agent 友好）：**
- stdout 强制 UTF-8 编码（Console.OutputEncoding = UTF8）
- Banner / 横幅 / 错误提示走 stderr，stdout 永远只含命令输出或 JSON
- 退出码精确分类：0 成功 / 130 中断 / 253 协议错误 / 254 认证 / 255 连接 / 其他透传 %ERRORLEVEL%
- 每次调用 = 完整 connect → auth → execute → disconnect 生命周期，无状态
- Agent 并行调用通过多进程承载，进程内无共享状态
- 两阶段标记协议解决 cmd.exe 在 stdin 被管道化时 `@echo off` 失效的命令回显污染
- WebSocketSharp 内置 Fatal log 已通过 `_ws.Log.Output = no-op` 抑制
- Ctrl+C 转发 Interrupt + 3 秒看门狗硬退兜底
- 密码可由 `-P <pwd>` CLI 参数传递，也可由 `SSHC_PASSWORD` 环境变量传递。
  优先级：CLI > 环境变量 > 报错。环境变量方式更安全（不进 shell history，
  不进 tasklist 命令行字段），是 Agent 调用的推荐方式

## Requirements Doc

完整需求见 `头脑风暴.txt`，非交互 CLI 设计过程见 `docs/features/non-interactive-cli/`
