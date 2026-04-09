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

- **Server** — 监听 WebSocket 端口(默认 22222)，管理 cmd.exe 进程，处理文件传输。支持多客户端连接、超时断开(10分钟无活动)、控制台管理命令
- **Client** — CLI 工具，`connect <ip> -p <port> -u <user>` 连接后进入交互式 Shell。心跳保活(30秒)、管理命令(clients/kick/kickall)
- **Common** — 共享协议层，MessageType 枚举 + JSON 消息模型

## Build

```
dotnet build SSH.sln
```

Output: `src/SSHServer/bin/Debug/net452/SSHServer.exe` and `src/SSHClient/bin/Debug/net452/SSHClient.exe`

## Project Structure

- `src/SSHCommon/Protocol/` — 消息类型枚举、JSON 协议模型（服务端+客户端共享）
- `src/SSHServer/` — WebSocket 服务端（WebSocketSharp）、多连接管理、Shell 会话、文件传输、超时检测
- `src/SSHClient/` — CLI 客户端、交互式 Shell 循环、文件传输+进度条、心跳、管理命令

## Dependencies

- WebSocketSharp (WebSocket 通信，兼容 Win7)
- Newtonsoft.Json (JSON 序列化)

## Key Design Constraints

- 多客户端连接，每个连接独立 Shell 会话和文件传输
- 10分钟无活动超时：警告 → 10秒后断开
- 客户端心跳(30秒 Ping)保持连接活跃
- 非正常断开通过 OnClose 事件清理资源
- 用户名+密码认证，凭据存储在服务端 JSON 配置文件
- 明文通信，仅局域网使用
- cmd.exe 输出 GBK → 服务端转 UTF-8 后传输
- 文件传输需显示进度条
- 支持 Ctrl+C 中断命令、上下箭头历史命令
- 服务端和客户端帮助提示支持中英双语

## Requirements Doc

完整需求见 `头脑风暴.txt`
