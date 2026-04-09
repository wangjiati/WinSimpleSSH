# 04 — 实现顺序与验证计划

> **目的**：把 [03-change-list.md](./03-change-list.md) 的改动切成可独立验证的阶段，确保每一步都能跑通再进下一步，不做"大爆炸式"改造。
> **前置**：决策已锁定，改动清单已定稿
> **风险策略**：每阶段结束必须能编译通过 + 交互模式 REPL 功能无回归

## 阶段总览

| 阶段 | 目标 | 新增/改动 | 可验证产出 |
|------|------|-----------|----------|
| **P0** | 安全网：回归基线 | 无改动 | 交互模式手动测一遍，记录基线行为 |
| **P1** | 基础设施 | ExitCodes, CommandResult, OutputCapture, RemoteShell 拦截接口, Main 返回 int | 编译通过，REPL 无回归 |
| **P2** | `exec` 动词 | NonInteractiveRunner.RunExec, Program 分派, ParseCommonArgs | `SSHC exec` 能执行远程命令并返回正确 exit code |
| **P3** | `start` 动词 | RunStart（仅加 `wrapForDetach`） | `SSHC start` 能启动远程 GUI |
| **P4** | `upload` / `download` 动词 | RunUpload, RunDownload, FileTransfer 静默开关 | 文件传输非交互化 |
| **P5** | `--json` + Ctrl+C + Banner 路由 | Runner 的 JSON 输出路径, Console.CancelKeyPress, stderr 化 | Agent 友好全功能 |
| **P6** | 验收回归 | 修 bug、微调、文档 | 所有 [03 验收要点](./03-change-list.md#-验收要点) 通过 |

**关键原则**：
1. 每阶段结束 `dotnet build SSH.sln` 必须零警告通过
2. 每阶段结束执行 **P0 基线测试脚本**，交互模式不能坏
3. P2 是最大的阶段，结束就有"能用"的 MVP

---

## P0 — 安全网（不写代码）

**目标**：在动任何代码之前，确认当前主干 `main @ 804abe7` 的交互模式基线行为，后续每阶段回归用。

### 手动测试脚本（你在真实环境跑一次）

1. 启动服务端（设备机上应该已经常驻）
2. 在当前目录跑：`SSHC.exe connect <device_ip> -u admin`
3. 输入密码，验证：
   - [ ] 登录成功横幅显示
   - [ ] `dir` 输出正常（中文无乱码）
   - [ ] `ping -n 3 127.0.0.1` 能看到逐行输出
   - [ ] Ctrl+C 能中断 `ping -t 127.0.0.1`
   - [ ] ↑↓ 键能翻历史命令
   - [ ] `upload` / `download` 能传一个小文件
   - [ ] `clients` 能列出在线
   - [ ] `exit` 正常退出

### 产出

在 `docs/features/non-interactive-cli/baseline-notes.md`（由你记录）里简单记下哪些工作正常、哪些有小问题——后面每阶段拿这个做回归对照。

> **如果基线本身就有问题**（比如某个功能本来就不工作），先在这一步暴露出来，避免后续误把"老 bug"当成"改造引入的 bug"。

---

## P1 — 基础设施（不影响现有功能）

**目标**：建立非交互模式的"骨架"，但暂时不接任何动词。交互模式 100% 不动。

### 步骤

1. **创建 `Core/ExitCodes.cs`** — 纯常量类，零依赖
2. **创建 `Core/CommandResult.cs`** — DTO，零依赖
3. **创建 `Core/OutputCapture.cs`** — buffer + marker 扫描逻辑
4. **修改 `Core/RemoteShell.cs`**：
   - 新增 `_onShellOutput` 字段
   - 新增 `SetShellOutputHandler` 方法
   - 重命名 `SetOutputHandler` → `SetSignalHandler`
   - 修改 `HandleMessage` 的 `ShellOutput/ShellError` 分支，支持拦截
5. **修改 `Program.cs`**：
   - `static void Main` → `static int Main`，所有返回路径加 `return ExitCodes.X`
   - `shell.SetOutputHandler` 的两处调用改成 `SetSignalHandler`
6. **修改 `SSHClient.csproj`**（如果是非 SDK-style）：追加 3 个新文件 Compile Include
7. **编译**：`dotnet build SSH.sln`
   - [ ] 零警告通过
8. **回归测试**：跑 P0 脚本
   - [ ] 所有交互功能照常工作

### 风险

- 重命名 `SetOutputHandler` 会同时影响 Program.cs 里的两处调用，漏改一处就编译失败——这其实是**好事**（编译器帮你找到所有引用）
- `Main` 返回类型改动不会影响非交互模式，因为 `void Main` 默认退出码就是 0

### 验证方式

```cmd
dotnet build SSH.sln
echo %ERRORLEVEL%
SSHC.exe connect <device> -u admin
# 手动跑一遍 P0 脚本
```

---

## P2 — `exec` 动词（MVP）

**目标**：让 `SSHC exec <host> -u admin -P xxx "dir"` 能工作。这是整个改造的核心里程碑。

### 步骤

1. **创建 `Core/NonInteractiveRunner.cs`**
   - 先只实现 `RunExec` 和 `RunShellVerb` 核心流程
   - `RunStart` / `RunUpload` / `RunDownload` 留空或 `throw new NotImplementedException()`
   - `EmitResult` 先只做纯文本输出，暂时不做 `--json` 分支（P5 再加）
   - `LogInfo` 走 `Console.Error.WriteLine`
2. **修改 `Program.cs`**：
   - `Main` 加 verb 分派 switch
   - 新增 `RunConnect`（从原 Main 抽出来）
   - 新增 `RunNonInteractive` 和 `ParseCommonArgs`
   - 更新 `PrintUsage` 展示新动词
   - 删除 `Console.ReadKey()` 阻塞点
3. **编译通过**
4. **回归 P0 脚本**
   - [ ] 交互模式无回归

### 验证（在你的测试环境跑）

```cmd
# 成功路径
SSHC.exe exec 127.0.0.1 -u admin -P admin123 "dir"
echo %ERRORLEVEL%
# 期望：看到 dir 输出，ERRORLEVEL=0

# 命令失败退出码透传
SSHC.exe exec 127.0.0.1 -u admin -P admin123 "exit 42"
echo %ERRORLEVEL%
# 期望：ERRORLEVEL=42

# 中文输出
SSHC.exe exec 127.0.0.1 -u admin -P admin123 "echo 你好世界"
# 期望：看到"你好世界"，无乱码

# 认证失败
SSHC.exe exec 127.0.0.1 -u admin -P wrongpwd "dir"
echo %ERRORLEVEL%
# 期望：ERRORLEVEL=254，stderr 有错误

# 连接失败
SSHC.exe exec 1.2.3.4 -u admin -P x "dir"
echo %ERRORLEVEL%
# 期望：ERRORLEVEL=255

# 长命令
SSHC.exe exec 127.0.0.1 -u admin -P admin123 "ping -n 5 127.0.0.1"
# 期望：大约 5 秒后返回，输出完整

# stdin 重定向不崩溃（这是阻塞点验证）
echo dummy | SSHC.exe exec 127.0.0.1 -u admin -P admin123 "echo hello"
# 期望：正常输出 hello
```

### P2 完成意味着什么

**MVP 达成**：你已经可以让 AI Agent 通过 `subprocess.run` 调用 SSHC 执行任意远程命令并拿到 exit code + stdout。后面的阶段都是在这个基础上加功能。

---

## P3 — `start` 动词（小改动）

**目标**：让 Agent 调用 `SSHC start <host> ... "notepad.exe"` 启动远程 GUI 程序。

### 步骤

1. 在 `NonInteractiveRunner.RunStart` 里调用 `RunShellVerb("start", program, wrapForDetach: true)`
2. 验证 `RunShellVerb` 里 `wrapForDetach` 分支生成的 payload 是 `start "" <program> & echo __MARK__...`
3. **不需要改别的文件**

### 验证

```cmd
SSHC.exe start 127.0.0.1 -u admin -P admin123 "notepad.exe"
# 期望：notepad 在设备桌面弹出，SSHC 立即返回，ERRORLEVEL=0

# 启动带参数的程序
SSHC.exe start 127.0.0.1 -u admin -P admin123 "cmd.exe /c timeout /t 30"
# 期望：SSHC 立即返回，不被 cmd.exe 阻塞

# 启动失败（程序不存在）
SSHC.exe start 127.0.0.1 -u admin -P admin123 "nonexistent.exe"
# 期望：ERRORLEVEL != 0（cmd.exe 的 start 命令返回错误码）
```

### 风险

- `start ""` 的第一个空引号是 cmd.exe 的"标题"参数——如果写成 `start "notepad.exe"`，`notepad.exe` 会被当成标题而不是命令。**单元测试**（或手工）验证 payload 里包含了那对空引号
- 带参数的 program 路径如果含空格需要 escape。建议用户调用时自己加引号（`SSHC start ... "\"C:\Program Files\App\app.exe\" --flag"`）

---

## P4 — `upload` / `download` 动词

**目标**：文件传输也能非交互化。

### 步骤

1. **修改 `Core/FileTransfer.cs`**：
   - 新增 `public static bool Quiet { get; set; }`
   - 进度条方法里最顶加 `if (Quiet) return;`
   - 所有 `Console.Write` / `Console.WriteLine` 改走 `Console.Error`
2. **实现 `NonInteractiveRunner.RunUpload`**
3. **实现 `NonInteractiveRunner.RunDownload`**
   - 这里需要处理 `DownloadStart/Chunk/Complete` 消息
   - **建议简化**：复用 `_onOutput?.Invoke($"MSG:{raw}")` 转发机制，在 Runner 的 SignalHandler 里解析并分发给 `FileTransfer.HandleDownloadMessage`
4. 在 `NonInteractiveRunner` 构造时设置 `FileTransfer.Quiet = _quiet || _jsonOutput`
5. **回归 P0 脚本**（交互模式的 upload/download 仍要工作）

### 验证

```cmd
# 上传
SSHC.exe upload 127.0.0.1 -u admin -P admin123 C:\test.txt C:\remote_test.txt
echo %ERRORLEVEL%
# 期望：文件出现在远端，ERRORLEVEL=0

# 下载
SSHC.exe download 127.0.0.1 -u admin -P admin123 C:\remote_test.txt C:\local_downloaded.txt
# 期望：文件出现在本地

# 本地文件不存在
SSHC.exe upload 127.0.0.1 -u admin -P admin123 C:\nonexistent.txt
# 期望：ERRORLEVEL != 0，有错误提示

# 进度条不污染 stdout（保存文件正确）
SSHC.exe download 127.0.0.1 -u admin -P admin123 C:\remote_test.txt C:\out.txt > stdout.log
# 期望：stdout.log 为空（或只包含命令真实 stdout），进度条在屏幕上
```

---

## P5 — `--json` + Ctrl+C + Banner 路由

**目标**：补齐 Agent 消费所需的所有 polish 特性。

### 步骤

1. **`NonInteractiveRunner.EmitResult`** 完善 JSON 分支
   - 成功和失败路径都要能输出合法 JSON
2. **`NonInteractiveRunner.RunShellVerb`** 注册 `Console.CancelKeyPress` 处理器
3. **`RemoteShell.cs`** 批量把 Banner 相关的 `Console.WriteLine` 改成 `Console.Error.WriteLine`（列表见 03-change-list.md）
4. **`FileTransfer.cs`** 确认 P4 改动已经完成 stderr 路由

### 验证

```cmd
# JSON 成功
SSHC.exe exec 127.0.0.1 -u admin -P admin123 --json "dir"
# 期望：stdout 是一个完整的 JSON 对象，ok=true，stdout 字段非空

# JSON 失败路径
SSHC.exe exec 127.0.0.1 -u admin -P wrongpwd --json "dir"
# 期望：JSON，ok=false，error.kind="auth_failed"

# JSON parsability（关键）
SSHC.exe exec ... --json "dir" | jq .
# 期望：jq 能解析，不抛错

# 重定向隔离
SSHC.exe exec 127.0.0.1 -u admin -P admin123 "echo clean" > out.txt 2> err.txt
type out.txt
# 期望：只有 "clean\r\n"，没有横幅
type err.txt
# 期望：横幅在这里

# -q 静默
SSHC.exe exec 127.0.0.1 -u admin -P admin123 -q "echo hello" 2> err.txt
type err.txt
# 期望：err.txt 为空

# Ctrl+C 转发
SSHC.exe exec 127.0.0.1 -u admin -P admin123 "ping -t 127.0.0.1"
# 按 Ctrl+C
echo %ERRORLEVEL%
# 期望：ping 被中断，ERRORLEVEL=130
# 在设备机上 tasklist 验证没有遗留的 ping.exe
```

---

## P6 — 验收回归

**目标**：把 [03-change-list.md#-验收要点](./03-change-list.md#-验收要点) 全部跑一遍，修掉发现的小问题，补文档。

### 任务

1. 所有功能性验收项
2. 所有非功能性验收项
3. 所有边界验收项
4. 更新项目根 `README.md` 的使用说明（新增动词）
5. 更新 `CLAUDE.md` 的 "Key Design Constraints" 添加非交互 CLI 的约定

### 可选扩展（不在 MVP 范围）

- 单元测试项目：为 `OutputCapture` 的 marker 检测写测试
- 集成测试：起一个本地 SSHServer 进程跑一组端到端测试
- GitHub Actions CI：在 PR 上自动跑构建
- PowerShell cmdlet 封装：`Invoke-SSHC` / `Get-SSHCResult`

---

## 风险与回滚

### 主要风险

| 风险 | 可能性 | 影响 | 缓解 |
|------|--------|------|------|
| Marker 被截断（WebSocket 分片恰好切在标记中间） | 低 | 超时误判 | OutputCapture 每次 Append 都全量扫描缓冲区，跨分片也能匹配 |
| `start ""` 语法在某些 Windows 版本行为不一致 | 低 | `start` 动词失效 | P0 基线阶段在 Win7/Win10 都试一次 |
| Ctrl+C 在某些终端下被吞掉 | 中 | Interrupt 不转发 | 退回方案：不保证 Ctrl+C 转发，依赖进程级 kill |
| `FileTransfer.Quiet` 静态字段和并发冲突 | 低 | 并发调用下进度条状态异常 | Agent 并行是**多进程**，每进程独立静态状态，无冲突 |
| P2 重构 Program.cs 引入交互模式回归 | 中 | REPL 不能用 | 每阶段 P0 脚本回归 |

### 回滚策略

每个阶段一个独立 git commit，并用 feature branch 开发：

```
main
 └── feature/non-interactive-cli
      ├── commit P1: infra
      ├── commit P2: exec verb (MVP)
      ├── commit P3: start verb
      ├── commit P4: upload/download
      ├── commit P5: json + ctrl+c + stderr
      └── commit P6: docs & polish
```

如果 Px 发现根本性问题，`git reset --hard` 到 Px-1，或直接废弃 feature 分支重来。

---

## 下一步

所有设计文档就位。准备好进入实现：

- P0：由**你**在真实环境跑一次基线脚本，可选记录 `baseline-notes.md`
- P1+：由**我**基于本文档开始改代码，每阶段完成后请你手动验收
