# WinSimpleSSH — 简易远程 Shell 工具

通过 WebSocket + JSON 协议在局域网内实现远程 cmd.exe Shell 和文件传输。

解决 Windows 环境下无可用 SSH 且其他远程工具被杀毒软件拦截的问题。

---

## 目录

- [两种使用模式](#两种使用模式)
- [特性](#特性)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [编译](#编译)
- [服务端](#服务端)
  - [启动](#启动)
  - [配置文件](#配置文件)
- [客户端](#客户端)
  - [交互模式 connect](#交互模式-connect)
  - [非交互模式 Agent--脚本调用](#非交互模式-agent--脚本调用)
- [连接管理](#连接管理)
  - [多客户端连接](#多客户端连接)
  - [超时断开](#超时断开)
  - [心跳保活](#心跳保活)
  - [管理命令](#管理命令)
- [通信协议](#通信协议)
- [项目结构](#项目结构)
- [依赖](#依赖)
- [更新日志](#更新日志)
- [注意事项](#注意事项)

---

## 两种使用模式

WinSimpleSSH 客户端 `SSHC.exe` 同时支持两种使用方式，由第一个参数（动词）区分：

| 模式 | 命令形态 | 适合 | 输出特点 |
|------|---------|------|---------|
| **交互模式** | `SSHC connect <host> -u <user>` | 人类操作员、临时调试、运维 | `ssh>` REPL，cmd.exe 默认 GBK，带进度条/历史命令/Ctrl+C 中断 |
| **非交互模式** | `SSHC <verb> <host> -u <user> -p <pwd> ...` | 脚本、CI、AI Agent 调用 | UTF-8 stdout、JSON 可选、退出码精确分类、banner 全走 stderr |

非交互模式有 4 个动词：`exec` / `start` / `upload` / `download`。每次调用 = 完整的 connect → auth → execute → disconnect 生命周期，**无状态、可并行**。

服务端 `SSHServer.exe` **不区分模式**——同一个服务端能同时接受两种客户端连接，新老客户端混用没有问题。

---

## 特性

### 核心

- **单文件部署** — Costura.Fody 嵌入依赖，编译为独立 exe，无需附带 DLL
- **零配置启动** — 首次运行自动生成默认 `server.json`，双击即可使用
- **静默运行** — 默认无控制台窗口，后台静默运行；`--console` 参数显示窗口（调试/监控）
- **多客户端** — 同一服务端支持多个客户端同时连接，同一用户名可多次登录
- **超时保护** — 10 分钟无活动自动断开（先警告，10 秒后执行）
- **心跳保活** — 客户端每 30 秒发送心跳，防止空闲超时
- **管理能力** — 服务端/客户端均可列出连接、踢出指定客户端
- **日志系统** — 服务端自动记录操作日志（含启动/退出）到 exe 同级 `log/` 目录
- **IP 白名单** — server.json 配置允许连接的 IP，支持精确匹配和通配符（`192.168.1.*`）
- **报文混淆** — 固定置换表 + 随机偏移，WebSocket 二进制帧传输，抓包不可直接读取原文
- **广泛兼容** — 支持 Windows 7 / 10 / 11，基于 .NET Framework 4.5.2

### 交互模式

- **类 SSH 体验** — `ssh>` 提示符，命令实时输出
- **历史命令** — 上下方向键翻历史
- **Ctrl+C 中断** — 中断当前命令而不断开会话
- **文件传输** — 上传/下载带进度条，权限预检
- **管理命令** — `clients` / `kick` / `cls` / `help`
- **中英双语** — 所有帮助和提示信息均支持中英双语显示

### 非交互模式（Agent 友好）

- **4 个动词** — `exec` 执行命令 / `start` fire-and-forget 启动程序 / `upload` 上传文件 / `download` 下载文件
- **JSON 输出** — `--json` 选项输出结构化结果，Agent 可直接 `json.loads`
- **退出码规范** — 仿 OpenSSH：`0` / `130` / `253` / `254` / `255` + 透传远程 `%ERRORLEVEL%`
- **UTF-8 stdout** — 中文不乱码，可被任意脚本/Agent 直接消费
- **stdout / stderr 分流** — 横幅走 stderr，stdout 永远只含命令输出或 JSON
- **Ctrl+C 看门狗** — 转发 Interrupt 给远端 + 3 秒硬退兜底

---

## 环境要求

| 项目 | 要求 |
|------|------|
| 运行时 | .NET Framework 4.5.2 及以上（Win10/11 已内置） |
| 网络 | 局域网环境 |
| 编译 | .NET SDK + .NET Framework 4.5.2 Targeting Pack |

> Win7 用户需手动安装 [.NET Framework 4.5.2](https://dotnet.microsoft.com/download/dotnet-framework/net452)

---

## 快速开始

### 1. 编译

```bash
git clone https://github.com/wangjiati/WinSimpleSSH.git
cd WinSimpleSSH
dotnet build WinSimpleSSH.sln
```

### 2. 启动服务端（设备机）

```bash
cd src/SSHServer/bin/Debug/net452/
SSHServer.exe              # 静默后台运行（无窗口）
SSHServer.exe --console    # 显示控制台窗口（调试/监控）
```

静默模式无窗口输出，日志写入 exe 同级 `log/` 目录。显示窗口模式下输出：
```
SSH Server started on port 22222
服务端以管理员权限运行 / Running as Administrator
关闭窗口即可停止服务器
```

### 3. 客户端使用（任选一种）

**交互模式（给人用）：**
```bash
cd src/SSHClient/bin/Debug/net452/
SSHC.exe connect 192.168.1.100 -u admin
# 输入密码后进入 ssh> 提示符
```

**非交互模式（给脚本/Agent 用）：**
```bash
SSHC.exe exec 192.168.1.100 -u admin -p admin123 "dir C:\"
# 直接拿到输出和退出码
```

---

## 编译

```bash
dotnet build WinSimpleSSH.sln            # Debug 编译
dotnet build WinSimpleSSH.sln -c Release # Release 编译
```

编译输出为单文件，无需附带 DLL：

| 应用 | 路径 |
|------|------|
| 服务端 | `src/SSHServer/bin/<config>/net452/SSHServer.exe` |
| 客户端 | `src/SSHClient/bin/<config>/net452/SSHC.exe` |

### 部署文件清单

```
SSHServer/
├── SSHServer.exe      # 单文件主程序（内含所有依赖）
├── StartAdmin.bat     # 管理员权限启动脚本（带 --console 参数，可选）
└── server.json        # 配置文件（首次运行自动生成）

SSHC/
└── SSHC.exe           # 单文件主程序（内含所有依赖）
```

---

## 服务端

### 启动

服务端支持两种运行模式：

```bash
SSHServer.exe              # 静默模式（默认）：无控制台窗口，后台运行
SSHServer.exe --console    # 控制台模式：显示窗口，用于调试和监控
```

也可通过 `StartAdmin.bat` 以管理员权限启动（自动附加 `--console` 参数，显示窗口）。

**静默模式（默认）：**
- 双击 `SSHServer.exe` 或不带参数启动，无黑窗口弹出
- 所有日志写入 exe 同级 `log/` 目录，按日期滚动
- 适合部署到无人值守的设备、加入开机启动项
- 停止方式：`taskkill /IM SSHServer.exe` 或通过客户端远程操作

**控制台模式（`--console`）：**
- 显示控制台窗口，实时查看连接/断开/认证/超时日志
- 已禁用快速编辑模式（鼠标选中不会卡住输出）
- **Ctrl+C** 或关闭窗口即可停止，退出时记录日志
- 适合调试、临时监控

**通用：**
- 首次启动自动生成默认 `server.json` 配置文件（含默认白名单和用户）
- 启动日志显示版本号、端口、IP 白名单状态、管理员权限状态

### 配置文件

`server.json` 必须与 `SSHServer.exe` 放在同一目录。**首次启动时若文件不存在，将自动生成默认配置。**

自动生成的默认配置：

```json
{
  "port": 22222,
  "ipWhitelist": [
    "127.0.0.1",
    "192.168.0.*",
    "192.168.1.*"
  ],
  "users": [
    {
      "username": "admin",
      "password": "admin123"
    }
  ]
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `port` | int | WebSocket 监听端口，默认 `22222` |
| `ipWhitelist` | string[] | 允许连接的 IP 白名单，支持精确 IP 和通配符（如 `192.168.1.*`）。为空或不填则放行所有 IP |
| `users` | array | 允许登录的用户列表 |
| `users[].username` | string | 用户名 |
| `users[].password` | string | 密码（明文） |

> 同一用户名可以被多个客户端同时使用，没有限制。

---

## 客户端

`SSHC.exe` 是双模式 CLI，通过第一个参数区分：

```bash
SSHC.exe connect  <host> -u <user> [-p <pwd>]     # 交互模式（省略 -p 则提示输入密码）
SSHC.exe exec     <host> -u <user> -p <pwd> "<命令>"
SSHC.exe start    <host> -u <user> -p <pwd> "<程序>"
SSHC.exe upload   <host> -u <user> -p <pwd> <local> <remote>
SSHC.exe download <host> -u <user> -p <pwd> <remote> <local>
SSHC.exe --version                                  # 显示版本号
SSHC.exe help                                       # 显示用法
```

### 交互模式 (connect)

```bash
SSHC.exe connect <主机IP> [--port <端口>] -u <用户名> [-p <密码>]
```

**参数：**

| 参数 | 必填 | 说明 |
|------|------|------|
| `<主机IP>` | 是 | 服务端 IP 地址 |
| `--port <端口>` | 否 | 服务端端口，默认 `22222` |
| `-u <用户名>` | 是 | 登录用户名 |
| `-p <密码>` | 否 | 登录密码。省略则提示输入；也可通过环境变量 `SSHC_PASSWORD` 传入 |

**连接示例：**

```bash
# 默认端口 22222，提示输入密码
SSHC.exe connect 192.168.1.100 -u admin

# 指定端口
SSHC.exe connect 192.168.1.100 --port 30000 -u admin

# 一键免密启动（适合其他应用调用）
SSHC.exe connect 192.168.1.100 -u admin -p admin123

# 环境变量免密启动
set SSHC_PASSWORD=admin123 && SSHC.exe connect 192.168.1.100 -u admin
```

连接后认证成功进入交互式 Shell。密码错误时程序自动退出。

#### 交互命令

进入 `ssh>` 提示符后，支持以下操作：

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

#### 快捷键

| 按键 | 功能 |
|------|------|
| `Ctrl+C` | 中断当前正在执行的远程命令 |
| `↑` / `↓` | 翻阅历史命令 |
| `←` / `→` | 移动光标 |
| `Backspace` | 删除光标前字符 |

#### 文件传输示例

```
ssh> upload "C:\data\report.xlsx" "D:\reports\report.xlsx"
  [████████████████████████████████████████] 100.0% 2.3MB/2.3MB
Upload completed: D:\reports\report.xlsx / 上传完成

ssh> download "D:\logs\app.log" "app.log"
  [████████████████████████████████████████] 100.0% 5.1MB/5.1MB
```

> 文件路径不指定时，上传目标默认为服务端工作目录，下载目标默认为客户端当前目录。

---

### 非交互模式 (Agent / 脚本调用)

不进入 REPL，一次性执行单个动作并退出。专为脚本、CI、AI Agent 设计。

```bash
SSHC.exe exec     <主机> -u <用户> -p <密码> [选项] "<命令>"
SSHC.exe start    <主机> -u <用户> -p <密码> [选项] "<程序> [参数]"
SSHC.exe upload   <主机> -u <用户> -p <密码> [选项] <本地文件> <远程路径>
SSHC.exe download <主机> -u <用户> -p <密码> [选项] <远程文件> <本地路径>
```

#### 通用选项

| 选项 | 说明 |
|------|------|
| `--port <端口>` | 服务端端口，默认 `22222` |
| `-u <用户>` | 用户名（必填） |
| `-p <密码>` | 密码（注意大写 `P`，与交互式密码提示区分；可改用环境变量） |
| `-q` / `--quiet` | 静默模式：抑制 stderr 上的辅助信息 |
| `--json` | 以 JSON 对象输出结果到 stdout，便于脚本解析 |

**密码传递方式（两种任选一种）：**

| 方式 | 命令示例 | 适用场景 |
|------|---------|---------|
| **CLI 参数 `-p <密码>`** | `SSHC.exe exec host -u admin -p admin123 "dir"` | 临时测试、单次调用 |
| **环境变量 `SSHC_PASSWORD`** | `set SSHC_PASSWORD=admin123 && SSHC.exe exec host -u admin "dir"` | Agent / 脚本批量调用，更安全 |

**优先级**：CLI `-p` > 环境变量 `SSHC_PASSWORD` > 报错。两个都没给会得到友好提示。

**为什么环境变量更安全：**
- 不进 shell history（`-p admin123` 会被 bash/cmd 的 ↑ 键翻出来）
- 不出现在 `tasklist /v` 等进程列表的命令行字段里
- Agent 父进程一次设置后，所有 `subprocess` 子进程自动继承

#### 4 个动词

| 动词 | 行为 | 退出语义 |
|------|------|---------|
| `exec` | 在远程 cmd.exe 执行命令，等待结束，捕获 stdout/stderr/exit_code | 透传远程 `%ERRORLEVEL%` |
| `start` | 启动远程 GUI/服务程序后立即返回（fire-and-forget，封装 `start ""` 语法） | 5 秒内拿到结束标记或 cmd.exe 确认收到命令即返回 0 |
| `upload` | 上传本地文件到远端，等待服务端确认 | 0 = 成功，非 0 = 错误 |
| `download` | 从远端下载文件到本地，等待传输完成 | 0 = 成功，非 0 = 错误 |

#### 退出码规范（仿 OpenSSH）

| 退出码 | 含义 |
|------|------|
| `0` | 命令成功 |
| `130` | Ctrl+C 中断（含 3 秒看门狗硬退） |
| `253` | 协议错误 / 参数错误 / 标记丢失 |
| `254` | 认证失败（用户名或密码错误） |
| `255` | 连接失败（服务端不可达 / 握手超时） |
| 其他 | 远程命令的 `%ERRORLEVEL%` 透传 |

#### 输出分流和编码

- **stdout** — 命令真实输出（`exec`）或 JSON 结果（`--json`）
- **stderr** — 横幅、连接日志、错误提示
- `SSHC ... > out.txt` 永远只写干净的命令输出到文件
- 非交互模式 stdout 强制 **UTF-8** 编码，Agent 按 UTF-8 解析中文不会乱码
- 交互模式不变，保留 cmd.exe 默认 GBK 编码，避免破坏老用户终端

#### 调用示例

```bash
# 1. 一次性远程命令，拿退出码
SSHC.exe exec 192.168.1.100 -u admin -p admin123 "tasklist | findstr MyApp.exe"
echo 退出码=%ERRORLEVEL%

# 2. 启动远程程序（fire-and-forget）
SSHC.exe start 192.168.1.100 -u admin -p admin123 "C:\Apps\MyApp.exe --mode prod"

# 3. 上传补丁文件
SSHC.exe upload 192.168.1.100 -u admin -p admin123 "C:\patches\update.zip" "C:\app\update.zip"

# 4. 下载日志
SSHC.exe download 192.168.1.100 -u admin -p admin123 "C:\app\logs\error.log" "C:\local\error.log"
```

#### JSON 输出 schema

成功示例：

```json
{
  "ok": true,
  "host": "192.168.1.100",
  "username": "admin",
  "verb": "exec",
  "command": "tasklist",
  "exit_code": 0,
  "stdout": "Image Name  PID ...",
  "stderr": "",
  "duration_ms": 1245,
  "error": null,
  "timestamp": "2026-04-09T20:55:00+08:00"
}
```

连接失败示例：

```json
{
  "ok": false,
  "host": "1.2.3.4",
  "username": "admin",
  "verb": "exec",
  "command": "...",
  "exit_code": null,
  "stdout": null,
  "stderr": null,
  "duration_ms": 120,
  "error": {
    "kind": "connection_refused",
    "message": "Failed to reach 1.2.3.4:22222"
  },
  "timestamp": "2026-04-09T20:55:00+08:00"
}
```

| 字段 | 说明 |
|------|------|
| `ok` | 连接 + 认证是否成功（**不**反映命令本身的 exit_code） |
| `verb` | `exec` / `start` / `upload` / `download` |
| `exit_code` | 远程 `%ERRORLEVEL%`，连接层失败时为 `null` |
| `stdout` / `stderr` | 命令输出，已剥离回显行 |
| `duration_ms` | 进程从启动到结束的总耗时 |
| `error.kind` | `connection_refused` / `connection_timeout` / `auth_failed` / `protocol_error` / `marker_not_found` / `interrupted` |
| `timestamp` | ISO 8601 |

#### Python 调用示例（AI Agent 场景）

```python
import os, subprocess, json

# 推荐：Agent 启动时设置一次密码到环境变量，所有子进程自动继承
os.environ["SSHC_PASSWORD"] = "admin123"

result = subprocess.run([
    "SSHC.exe", "exec", "192.168.1.100",
    "-u", "admin",
    "--json",
    "tasklist /fi \"imagename eq MyApp.exe\""
], capture_output=True, text=True, encoding="utf-8")

data = json.loads(result.stdout)
if not data["ok"]:
    print(f"设备不可达: {data['error']['kind']}")
elif data["exit_code"] != 0:
    print(f"命令失败: {data['stderr']}")
elif "MyApp.exe" not in data["stdout"]:
    # 启动应用
    subprocess.run(["SSHC.exe", "start", "192.168.1.100",
                    "-u", "admin", "-p", "admin123",
                    "C:\\Apps\\MyApp.exe"])
```

> **设计要点：** Agent 并行调用是多进程承载（每个 SSHC 调用一个独立进程），互不共享状态。每次调用 = 完整的 connect → auth → execute → disconnect 生命周期。

---

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

1. **10 分钟无活动** → 服务端发送超时警告
   ```
   [Warning] 即将断开连接: 10秒内无活动 / Connection timeout in 10s
   ```
2. **警告后 10 秒仍无活动** → 服务端主动关闭连接
3. 如果警告后客户端发送了任何消息（包括心跳），超时计时器重置，连接保持

### 心跳保活

- 客户端每 **30 秒** 自动发送 Ping 消息
- 服务端回复 Pong，同时刷新活动时间
- 只要心跳正常运行，空闲连接不会被超时断开
- 心跳在后台自动运行，无需用户干预

### 管理命令

**服务端：** 控制台模式下关闭窗口或 Ctrl+C 停止；静默模式下 `taskkill /IM SSHServer.exe` 停止。

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

---

## 通信协议

客户端与服务端通过 WebSocket 通信，消息格式为 JSON，经报文混淆后以二进制帧传输。

### 报文混淆

所有 WebSocket 消息在发送前经过混淆处理，接收后自动还原：

```
发送: JSON string → UTF-8 bytes → Obfuscator.Encode() → WebSocket 二进制帧
接收: WebSocket 二进制帧 → Obfuscator.Decode() → UTF-8 string → JSON 解析
```

混淆算法：固定 256 字节置换表 + 每消息 1 字节随机偏移 + 逐字节位置偏移。
Wireshark 抓包仅显示不可读的 hex dump。详见 `docs/features/obfuscation.md`。

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
| `Ping` | C → S | 心跳（客户端每 30 秒发送） |
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
  |<----- ShellOutput (输出) ----|
  |------ ShellInput (ipconfig)->|
  |<----- ShellOutput (输出) ----|
  |------ Interrupt (Ctrl+C) --->|  (中断并重启 cmd.exe)
```

**心跳保活：**

```
Client                          Server
  |------ Ping ----------------->|  (每 30 秒)
  |<----- Pong ------------------|
```

**超时断开：**

```
Client                          Server
  |                              |  (10 分钟无任何消息)
  |<----- TimeoutWarning --------|  (警告: 10 秒后断开)
  |                              |  (10 秒后仍无活动)
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
- 客户端接收后：
  - **交互模式**：直接写到 Console，依赖终端编码
  - **非交互模式**：`Console.OutputEncoding` 强制 UTF-8，Agent 解析无歧义

### 文件传输细节

- 分块大小：64KB（65536 字节）
- 编码方式：每块原始字节 → Base64 字符串 → JSON 传输
- 进度计算：`当前块序号 / 总块数`
- 权限预检：上传前服务端先创建文件验证写权限，下载前验证读权限

### 非交互模式的两阶段标记协议

`exec` / `start` 动词通过持久 cmd.exe 执行命令时，需要检测命令边界。WinSimpleSSH 用一个 GUID 标记对解决：

```
客户端发送:
  @echo off
  echo __SSHC_BEGIN_<guid>__
  <user_command>
  echo __SSHC_DONE_<guid>_%ERRORLEVEL%__

客户端 OutputCapture 用行首锚点正则 (?m)^__SSHC_BEGIN_<guid>__ 匹配,
phase 1 截掉欢迎横幅+回显命令行,phase 2 截掉 end marker 拿 exit code,
按行剥离 cmd.exe 在管道模式下的命令回显污染。
```

完整设计见 `docs/features/non-interactive-cli/`。

---

## 项目结构

```
WinSimpleSSH/
├── WinSimpleSSH.sln                    # 解决方案文件
├── README.md
├── CLAUDE.md                           # AI 协作说明
├── 头脑风暴.txt                          # 原始需求文档
│
├── docs/                               # 设计文档
│   ├── README.md                       # 文档索引
│   ├── security-audit.md               # 安全审计报告
│   └── features/
│       ├── obfuscation.md              # 报文混淆方案设计
│       └── non-interactive-cli/
│           ├── 00-context.md           # 业务场景
│           ├── 01-current-state.md     # 改造前代码现状
│           ├── 02-design-decisions.md  # 9 个设计决策点
│           ├── 03-change-list.md       # 精确改动清单
│           ├── 04-implementation-plan.md  # 分阶段落地 + 进度日志
│           └── pr-description.md       # 提交上游 PR 的描述备份
│
└── src/
    ├── SSHCommon/                      # 共享协议库
    │   ├── SSHCommon.csproj
    │   ├── Crypto/
    │   │   └── Obfuscator.cs           #   报文混淆器（置换表 + 随机偏移）
    │   └── Protocol/
    │       ├── MessageType.cs          #   消息类型枚举
    │       └── ProtocolMessage.cs      #   JSON 协议模型
    │
    ├── SSHServer/                      # 服务端
    │   ├── SSHServer.csproj
    │   ├── Program.cs                  #   入口（仅显示日志，不接受输入）
    │   ├── WebSocketServerEngine.cs    #   WebSocket 服务引擎
    │   ├── server.json                 #   配置文件
    │   ├── Config/
    │   │   └── ServerConfig.cs         #   配置模型与加载
    │   └── Core/
    │       ├── ClientSession.cs        #   客户端会话数据模型
    │       ├── ConnectionManager.cs    #   多连接管理、认证、超时检测
    │       ├── ShellSession.cs         #   cmd.exe 进程管理
    │       ├── FileTransferHandler.cs  #   文件传输处理
    │       └── Logger.cs               #   日志系统（控制台+文件）
    │
    └── SSHClient/                      # 客户端
        ├── SSHClient.csproj
        ├── FodyWeavers.xml             #   Costura.Fody 配置
        ├── Program.cs                  #   verb 分派入口（connect/exec/start/upload/download/help）
        └── Core/
            ├── RemoteShell.cs          #   WebSocket 连接、消息处理、心跳；信号 + Shell 输出双拦截接口
            ├── FileTransfer.cs         #   文件传输 + 进度条 + Quiet 静默开关
            ├── NonInteractiveRunner.cs #   4 个非交互动词的流程编排 + TryConnectAndAuth 共享 helper
            ├── OutputCapture.cs        #   两阶段标记扫描（begin + end marker）+ 线程安全 buffer
            ├── CommandResult.cs        #   非交互模式的 JSON 结果 DTO
            └── ExitCodes.cs            #   退出码常量（0/130/253/254/255）
```

---

## 依赖

| 库 | 版本 | 用途 |
|----|------|------|
| [WebSocketSharp](https://www.nuget.org/packages/WebSocketSharp/) | 1.0.3-rc9 | WebSocket 服务端+客户端通信，纯 C# 实现，Win7 兼容 |
| [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json/) | 13.0.3 | JSON 序列化与反序列化 |
| [Costura.Fody](https://www.nuget.org/packages/Costura.Fody/) | 5.7.0 | 将依赖 DLL 嵌入 exe，实现单文件部署 |

---

## 更新日志

### v1.5.0 (2026-04-13)

**静默后台运行：**
- 服务端编译目标从 `Exe` 改为 `WinExe`，默认不创建控制台窗口，后台静默运行
- 新增 `--console` 启动参数：传入后分配控制台窗口，行为与之前版本一致
- `StartAdmin.bat` 自动附加 `--console` 参数，双击管理员启动仍显示窗口
- 后台模式通过 `taskkill /IM SSHServer.exe` 停止，日志写入 `log/` 目录

**客户端改进：**
- 交互模式新增 `-p <密码>` 可选参数，支持一键免密启动（`SSHC connect host -u admin -p pwd`）
- 密码回退链：CLI `-p` > 环境变量 `SSHC_PASSWORD` > 手动输入
- 参数标记统一：端口 `-p` → `--port`，密码 `-P` → `-p`（交互模式和非交互模式一致）
- 新增 `tests/` 目录，含 14 个批处理测试用例

### v1.4.0 (2026-04-10)

**易用性改进：**
- 版本号：服务端和客户端 csproj 统一管理版本号，启动日志/帮助页/`SSHC --version` 均可查看
- 零配置启动：首次运行自动生成默认 `server.json`（含 `127.0.0.1`、`192.168.0.*`、`192.168.1.*` 白名单和 `admin/admin123` 用户）
- 禁用快速编辑模式：服务端启动时自动禁用控制台 QuickEdit（鼠标选中不再卡住所有输出）
- 退出日志：Ctrl+C / 关闭窗口 / 系统关机时记录服务器关闭日志
- 部署简化：`StartAdmin.bat` 输出到 exe 同级目录，不再创建 `Scripts\` 子文件夹

### v1.3.0 (2026-04-10)

**安全加固：**
- IP 白名单：server.json 新增 `ipWhitelist` 配置项，连接阶段（OnOpen）校验，支持精确 IP 和通配符（`192.168.1.*`），白名单为空时放行所有 IP（向后兼容）
- 报文混淆：固定 256 字节置换表 + 每消息随机偏移 + 逐字节位置偏移，WebSocket 从文本帧切换为二进制帧，Wireshark 抓包无法读取原文
- 安全审计报告：全模块安全审计，27 项发现，分级评估，含修复路线图（`docs/security-audit.md`）

**设计文档见 `docs/features/obfuscation.md` 和 `docs/security-audit.md`**

### v1.2.0 (2026-04-09)

**新增非交互 CLI 模式（Agent 友好）：**
- 4 个动词命令：`exec` / `start` / `upload` / `download`
- 一次性执行，连接 → 认证 → 执行 → 断开 全在一个进程生命周期内完成
- `--json` 模式输出结构化结果，schema 含 ok / host / verb / exit_code / stdout / stderr / duration_ms / error / timestamp
- 退出码规范（仿 OpenSSH）：0 / 130 / 253 / 254 / 255 + 透传远程 %ERRORLEVEL%
- 非交互模式 stdout 强制 UTF-8 编码，中文 Agent 解析无乱码
- Banner 全部走 stderr，stdout 永远只含命令真实输出或 JSON
- WebSocketSharp 内置 Fatal 日志已抑制，错误输出语义化干净
- Ctrl+C 转发 Interrupt + 3 秒看门狗硬退兜底
- `start` 动词的 fire-and-forget 兜底：cmd.exe 收到命令但 GUI 未在 5 秒内返回时假定 detach 成功

**架构改进：**
- 客户端入口改为 verb 分派式（`SSHC <verb> <host> ...`），保留 `connect` 交互 REPL 完全无回归
- RemoteShell 新增 `SetShellOutputHandler` 拦截接口，支持非交互模式捕获 cmd.exe 输出
- 抽出共享 `TryConnectAndAuth` helper，所有动词复用连接+认证流程
- OutputCapture 实现两阶段标记协议（begin marker + end marker），解决 cmd.exe 在 stdin 被管道化时 `@echo off` 失效的命令回显污染

**完整设计文档见 `docs/features/non-interactive-cli/`**

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

---

## 注意事项

- **仅限局域网** — 报文已混淆但非密码学加密，请勿暴露到公网
- **静默运行** — 服务端默认无窗口后台运行，加 `--console` 参数显示窗口；`StartAdmin.bat` 自动带 `--console`
- **多客户端** — 支持多客户端同时连接，同一用户名可多次登录
- **超时机制** — 10 分钟无活动自动断开，心跳可保持连接
- **非正常断开** — 客户端直接关闭时，服务端通过心跳超时自动检测并清理资源
- **Ctrl+C 行为** — 交互模式中断当前命令后服务端会终止并重新启动 cmd.exe；非交互模式转发 Interrupt + 3 秒看门狗硬退
- **配置安全** — 密码以明文存储在 `server.json` 中，注意文件访问权限；建议配置 IP 白名单
- **文件路径** — 支持 Windows 绝对路径和相对路径，相对路径基于服务端工作目录
- **单文件部署** — exe 已内含所有依赖，首次运行自动生成配置文件，分发时只需拷贝 exe
- **界面语言** — 所有帮助和提示信息支持中英双语显示
- **非交互模式 stdout 编码** — 强制 UTF-8，Agent/Python 等消费者按 UTF-8 解析；交互模式保留 cmd.exe 默认 GBK
