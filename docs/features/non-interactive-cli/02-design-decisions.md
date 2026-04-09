# 02 — 设计决策点

> **目的**：把非交互 CLI 改造过程中**需要用户拍板**的问题集中列出来。每个决策都有可选方案和取舍分析。
> **状态**：✅ 全部决策已锁定
> **决策人**：xxf
> **决策日期**：2026-04-09（口头确认"全按这个来"）
> **⚠️ 阅读前先看**：[00-context.md](./00-context.md) —— 业务背景锁定了 Agent 作为主要调用方，D1-D6 的推荐倾向基于这个前提

## 决策总览（已锁定）

| # | 决策点 | 状态 | 最终答案 |
|---|--------|------|---------|
| D1 | 密码如何传递 | ✅ | **`-P <pwd>`**（明文参数，Agent 不在乎 shell history） |
| D2 | 命令执行模式 | ✅ | **每个动作独立动词**（exec/start/upload/download），无 `-e`/脚本文件 |
| D3 | 命令完成检测机制 | ✅ | **两阶段标记 + `exit`**（begin/end marker + 断连释放 cmd.exe） |
| D4 | 输出格式 | ✅ | **stderr 横幅 + `-q` + `--json`** |
| D5 | 退出码语义 | ✅ | **仿 OpenSSH**：0 成功 / 130 中断 / 253 协议 / 254 认证 / 255 连接 / 其他透传 %ERRORLEVEL% |
| D6 | Ctrl+C 行为 | ✅ | 转发 Interrupt 给服务端（最小实现，P5 再加超时强退保护） |
| D7 | CLI 命令形态 | ✅ | **动词式**：`SSHC exec / start / upload / download`，保留 `connect` 交互 REPL |
| D8 | 设备别名管理 | ✅ | **不做**（Agent 自己维护，SSHC 只认 IP） |
| D9 | JSON 输出 schema | ✅ | `ok / host / username / verb / command / exit_code / stdout / stderr / duration_ms / error / timestamp` |

## 实施落地情况（commit `44ef5d3`）

- D1 / D2 / D4 / D5 / D7 / D8 / D9 — **已完全实现**（`exec` 动词可用，`start`/`upload`/`download` 是 stub，P3/P4 补完）
- D3 — **已实现**但实施中发现原计划的 `@echo off` 对 cmd.exe 管道模式无效，
  实际方案改为"两阶段标记 + 客户端按行剥离回显"（见 `NonInteractiveRunner.StripEchoedCommands`）
- D6 — **最小实现**（CancelKeyPress handler 已注册转发 Interrupt）

详情见 [04-implementation-plan.md 的进度日志](./04-implementation-plan.md#进度日志)。

---

## D1. 密码如何传递

**问题**：交互模式下 `ReadPassword()` 要求真实终端，非交互模式必须另找渠道。

### 方案对比

| 方案 | 示例 | 安全性 | 脚本友好度 | 备注 |
|------|------|--------|-----------|------|
| **A. `-P <pwd>` 明文参数** | `SSHC connect host -u admin -P admin123` | ⚠️ 低（进 shell history + 进程列表可见） | ⭐⭐⭐⭐⭐ 最简单 | 最常见的 "sshpass" 式用法 |
| **B. `--password-stdin`** | `echo admin123 \| SSHC connect host -u admin --password-stdin` | ✅ 高（不进 history） | ⭐⭐⭐⭐ | Docker login 式，Unix 哲学 |
| **C. 环境变量** | `set SSHC_PASSWORD=admin123 && SSHC connect ...` | ⚠️ 中（子进程可见） | ⭐⭐⭐⭐ | CI 场景常用 |
| **D. 密码文件** | `SSHC connect ... --password-file C:\secrets\pwd.txt` | ✅ 高（配合 ACL） | ⭐⭐⭐ | 运维脚本场景 |

### 设计考量

- CLAUDE.md 已经明确本工具"**明文通信，仅局域网使用**"——威胁模型里本机进程隔离和命令历史泄露其实都不是重点
- 但**脚本化场景**会很在意密码进 shell history 这件事（容易被误提交到 git）
- 方案 B (`--password-stdin`) 和 D (`--password-file`) 不冲突，可以同时支持

### 请选择

- [ ] 只做 A（最快，够用）
- [ ] 只做 B（更 Unix）
- [ ] A + B 都做（推荐，给不同场景选）
- [ ] A + B + C（全都要）
- [ ] 其他：_________________

---

## D2. 命令执行模式

**问题**：非交互模式下用户怎么指定要执行哪些命令？

### 方案对比

| 方案 | 示例 | 说明 |
|------|------|------|
| **A. `-e "cmd"` 单命令** | `SSHC connect ... -e "dir C:\"` | 仿 OpenSSH `ssh host "command"`，执行完就退出 |
| **B. `--script file` 批处理** | `SSHC connect ... --script deploy.txt` | 按行读取文件，依次执行 |
| **C. stdin 管道** | `type cmds.txt \| SSHC connect ... --stdin-cmds` | 从 stdin 读命令，每行一条 |
| **D. 多个 `-e`** | `SSHC connect ... -e "cd C:\app" -e "dir"` | 每个 `-e` 一条命令 |

### 互相冲突的组合

- **B 和 C 不能同时用**（都要占用 stdin）
- **C 和 `--password-stdin` 不能同时用**（都要占用 stdin）
- **如果 D1 选了 B（`--password-stdin`），则 C 必然冲突**

### 设计考量

- 方案 A 覆盖 80% 场景（"远程执行一条命令拿输出"）
- 方案 B 给部署/运维脚本用（一组有顺序的命令）
- 方案 C 太容易和密码 stdin 打架，建议**不做**
- 方案 D 是 A 的扩展，实现成本只多一点点

### 请选择

- [ ] 只做 A（`-e` 单命令）
- [ ] 做 A + B（单命令 + 脚本文件，推荐）
- [ ] 做 A + D（支持多个 `-e`）
- [ ] 做 A + B + D（最灵活）
- [ ] 其他：_________________

---

## D3. 命令完成检测机制 ⚠️ 最关键

**问题**：cmd.exe 是持久进程，协议没有"命令结束"信号，客户端怎么知道该退出了？

详见 [01-current-state.md](./01-current-state.md#核心难点命令完成检测)。

### 方案对比

| 方案 | 做法 | 难度 | 拿得到 exit code 吗 |
|------|------|------|---------------------|
| **A. 标记注入**（推荐） | 发送 `{user_cmd} & echo __SSHC_DONE_%ERRORLEVEL%__`，客户端流式扫描输出 | 中 | ✅ 能 |
| **B. exit 法** | 发送 `{user_cmd}` 然后 `exit`，等服务端 cmd.exe 退出断连 | 低 | ❌ 不能 |
| **C. 超时法** | 发送命令，等待 N 秒无新输出就退出 | 低 | ❌ 不能 |

### 方案 A 细节

客户端发送的真实 payload：
```
dir C:\ & echo __SSHC_DONE_%ERRORLEVEL%__

```

客户端在输出流里用正则 `__SSHC_DONE_(\d+)__` 匹配，匹配到就：
1. 截断输出流中标记之前的内容作为"用户看到的输出"
2. 提取 %ERRORLEVEL% 作为进程退出码
3. 断开连接并退出

**坑点**：
- 用户命令里如果恰好包含字符串 `__SSHC_DONE_` 会误触发 → 用更独特的 GUID 标记（如 `__SSHC_DONE_7f3a9c_%ERRORLEVEL%__`）
- 命令本身就可能 echo 这个标记的字面值 → 给每次连接生成随机 GUID，不固定字符串
- 用 `&` 还是 `&&`？**必须用 `&`**（无论前一条是否失败都执行 echo），否则失败命令拿不到 exit code

### 请选择

- [ ] 方案 A（标记注入 + GUID，推荐）— 能拿 exit code，健壮
- [ ] 方案 B（exit 法）— 简单但没 exit code
- [ ] 方案 C（超时法）— 最 naive，不推荐
- [ ] 其他：_________________

---

## D4. 输出格式

**问题**：交互模式有 `Connecting to .../Authenticated successfully./ssh>` 这些横幅。非交互模式下这些东西会污染 stdout，让 `SSHC ... -e "cat file" > output.txt` 拿到的内容不干净。

### 方案

| 开关 | 效果 |
|------|------|
| 默认（不加开关）| 横幅打到 **stderr**，命令真实输出打到 **stdout** |
| `--quiet` / `-q` | 横幅完全静默 |
| `--verbose` / `-v` | 额外打印调试信息（握手、心跳等） |

### 设计考量

- **横幅必须打到 stderr**，否则 `SSHC ... -e "..." > out.txt` 会污染 out.txt（这是 Unix 工具的铁律）
- 当前 `RemoteShell.cs` 里一堆 `Console.WriteLine("Connecting...")` 直接走 stdout，改造时要**全部改成 stderr**

### 请确认

- [ ] 同意：横幅走 stderr，`-q` 完全静默，`-v` 调试日志
- [ ] 其他要求：_________________

---

## D5. 退出码语义

**问题**：`SSHC connect ... -e "exit 42"` 应该以什么退出码结束？

### 方案对比

| 方案 | 规则 | 参考 |
|------|------|------|
| **A. 透传 %ERRORLEVEL%** | 远程命令 exit 42 → SSHC 也 exit 42 | `ssh` 行为 |
| **B. 固定退出码** | 0=成功连接+执行，1=连接失败，2=认证失败，3=命令失败 | 更好脚本判断 |
| **C. 混合** | 连接/认证失败用固定码 (255, 254)，命令正常执行时透传 %ERRORLEVEL% | OpenSSH 做法 |

OpenSSH 的实际行为是方案 C：命令正常执行时透传，连接层问题用 255。

### 请选择

- [ ] 方案 C（推荐，仿 OpenSSH）
- [ ] 方案 A（纯透传）
- [ ] 方案 B（固定分类）
- [ ] 其他：_________________

---

## D6. Ctrl+C 行为

**问题**：交互模式下 Ctrl+C 会给服务端发 `Interrupt` 消息杀掉当前命令。非交互模式下用户在本地按 Ctrl+C 应该怎样？

### 方案

- **A. 转发中断**：本地 Ctrl+C → 发 Interrupt → 等服务端清理 → 本地 exit(130)
- **B. 直接退出**：本地 Ctrl+C → 本地立即 exit，服务端那边 cmd.exe 继续跑完（资源浪费）
- **C. 两次才退出**：第一次 Ctrl+C 转发中断，第二次强制本地退出

### 请选择

- [ ] 方案 A（推荐）
- [ ] 方案 B
- [ ] 方案 C
- [ ] 不做 Ctrl+C 处理（非交互模式通常在脚本里用，不需要）

---

---

## D7. CLI 命令形态：动词式 vs 子命令式

**问题**：SSHC 的命令行入口应该长什么样？当前只有 `SSHC connect <host> ...`，新增的非交互能力是挂在 `connect` 下面，还是独立动词？

### 方案对比

#### 方案 A：动词式（推荐）

```bash
# 执行远程命令
SSHC exec 192.168.1.34 -u admin -P secret "tasklist"

# 启动 GUI 软件（封装 start "" 技巧）
SSHC start 192.168.1.34 -u admin -P secret "C:\Apps\MyApp.exe --mode prod"

# 上传文件
SSHC upload 192.168.1.34 -u admin -P secret C:\local\patch.zip C:\remote\

# 下载文件
SSHC download 192.168.1.34 -u admin -P secret C:\remote\log.txt C:\local\

# 保留交互式 REPL（给人用）
SSHC connect 192.168.1.34 -u admin
```

**优点**：
- 对 Agent 最直观，每个动词对应一个动作，不用记"怎么传 `-e` 参数"
- 符合 Git / Docker / kubectl 的现代 CLI 范式
- `start` 动词可以内部封装 `start "" "program"` 的 cmd.exe 技巧，Agent 不用关心
- 每个动词可以有自己的 schema 和 JSON 输出格式

**缺点**：
- 需要重构 arg 解析（当前是 `args[0] == "connect"` 硬编码）
- 多一点实现工作量

#### 方案 B：子命令式（当前路径的扩展）

```bash
SSHC connect 192.168.1.34 -u admin -P secret -e "tasklist"
SSHC connect 192.168.1.34 -u admin -P secret --start "C:\Apps\MyApp.exe"
SSHC connect 192.168.1.34 -u admin -P secret --upload C:\local\patch.zip
```

**优点**：
- 改动最小，`connect` 入口不变

**缺点**：
- `-e` / `--start` / `--upload` 互斥关系复杂，容易拼错
- Agent 要记住"先写 connect 然后...再写 `-e`"这种语法
- 每个动作都要重建完整参数列表，冗长

### 请选择

- [ ] **方案 A：动词式**（推荐，Agent 友好）
- [ ] 方案 B：子命令式
- [ ] 混合：保留 `connect` 交互用，新增 `exec`/`start`/`upload`/`download` 独立动词
  - 其实这就是方案 A 的实际形态，因为方案 A 也保留 `connect`

---

## D8. 设备别名管理

**问题**：Agent 要调用 "车间 3 的测试工位 PC1"，是直接传 IP，还是 SSHC 内部维护一个别名表？

### 方案对比

| 方案 | 调用方式 | 复杂度 |
|------|---------|--------|
| **A. 不管**（推荐） | Agent 自己维护 `{"LINE3-PC1": "192.168.1.34"}`，调用时传 IP | 0 |
| B. 本地别名文件 | `%APPDATA%\SSHC\devices.json`，`SSHC exec LINE3-PC1 ...` 自动解析 | 中 |
| C. 服务器注册中心 | 每个 SSHServer 启动时向中心服务注册自己 | 高 |

### 设计考量

Agent 本身已经是一个有状态的系统，它**必然**要维护设备清单（不然怎么知道有哪些设备可控）。在 SSHC 里再搞一份别名表就是**重复造轮子**，而且会出现两边不同步的问题。

→ 本次坚决**不做**别名管理，只认 IP。如果未来发现 Agent 场景外也有别名需求，再独立加 feature。

### 请确认

- [ ] 同意：SSHC 只认 IP，别名由 Agent 自己管
- [ ] 不同意，我要本地别名文件

---

## D9. JSON 输出 schema

**问题**：`--json` 模式下返回什么字段？这个 schema 一旦定下来就是 Agent 依赖的契约，改动会破坏向后兼容。

### 推荐 schema

**成功情况**：

```json
{
  "ok": true,
  "host": "192.168.1.34",
  "username": "admin",
  "verb": "exec",
  "command": "tasklist /fi \"imagename eq MyApp.exe\"",
  "exit_code": 0,
  "stdout": "Image Name  PID ...\nMyApp.exe  1234 ...\n",
  "stderr": "",
  "duration_ms": 1245,
  "error": null,
  "timestamp": "2026-04-09T16:50:00+08:00"
}
```

**连接失败**：

```json
{
  "ok": false,
  "host": "192.168.1.34",
  "username": "admin",
  "verb": "exec",
  "command": "...",
  "exit_code": null,
  "stdout": null,
  "stderr": null,
  "duration_ms": 120,
  "error": {
    "kind": "connection_refused",
    "message": "Could not connect to 192.168.1.34:22222"
  },
  "timestamp": "2026-04-09T16:50:00+08:00"
}
```

**认证失败**：

```json
{
  "ok": false,
  "host": "192.168.1.34",
  "username": "admin",
  "verb": "exec",
  "command": "...",
  "exit_code": null,
  "stdout": null,
  "stderr": null,
  "duration_ms": 340,
  "error": {
    "kind": "auth_failed",
    "message": "Invalid username or password"
  },
  "timestamp": "2026-04-09T16:50:00+08:00"
}
```

**命令失败（远程 exit != 0）**：

```json
{
  "ok": true,
  "host": "192.168.1.34",
  "username": "admin",
  "verb": "exec",
  "command": "dir C:\\NotExist",
  "exit_code": 1,
  "stdout": "",
  "stderr": "系统找不到指定的文件。\n",
  "duration_ms": 520,
  "error": null,
  "timestamp": "2026-04-09T16:50:00+08:00"
}
```

### 字段说明

| 字段 | 类型 | 说明 |
|------|------|------|
| `ok` | bool | **只反映连接+认证是否成功**。命令本身返回 exit != 0 时 `ok` 仍为 true（符合 Unix 语义） |
| `host` | string | 目标 IP |
| `username` | string | 登录用户名（便于日志追踪，不含密码） |
| `verb` | string | `exec` / `start` / `upload` / `download` |
| `command` | string\|null | 被执行的命令原文（`exec`/`start` 时有值） |
| `exit_code` | int\|null | 远程 %ERRORLEVEL%，连接失败时为 null |
| `stdout` | string\|null | 合并后的 stdout（已去除标记） |
| `stderr` | string\|null | 合并后的 stderr |
| `duration_ms` | int | 从进程启动到结束的总耗时 |
| `error` | object\|null | 连接/认证/协议层错误，正常执行时为 null |
| `error.kind` | string | 枚举：`connection_refused` / `connection_timeout` / `auth_failed` / `protocol_error` / `marker_not_found` / `interrupted` |
| `error.message` | string | 人类可读的错误文字 |
| `timestamp` | string | ISO 8601，执行开始时间 |

### Agent 消费该 schema 的示例

```python
result = json.loads(subprocess.check_output(["SSHC", "exec", ...]))
if not result["ok"]:
    # 连接或认证问题 → 标记设备不可用
    alert(f"Device {result['host']} unreachable: {result['error']['kind']}")
elif result["exit_code"] != 0:
    # 命令执行失败 → 业务逻辑处理
    log_failure(result["stderr"])
else:
    # 成功
    parse_tasklist_output(result["stdout"])
```

### 请确认

- [ ] 同意以上 schema
- [ ] 要加字段：_________________
- [ ] 要改字段：_________________
- [ ] `ok` 的语义要改成"命令也成功才 true"
- [ ] 完全重新设计

---

## 决策完成后

当所有 ☐ 都变成 ☑ 后，进入 [03-change-list.md](./03-change-list.md)——基于你的选择输出精确的文件+函数+行号改动清单。
