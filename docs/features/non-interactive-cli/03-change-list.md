# 03 — 改动清单（文件 + 函数 + 行号）

> **目的**：把 D1-D9 的决策落成**精确到文件+函数的改动清单**，用来驱动实现。
> **前置**：已锁定 [02-design-decisions.md](./02-design-decisions.md) 的全部 9 个决策点（"全按推荐来"，2026-04-09）
> **服务端**：零改动。所有修改都在 `src/SSHClient/` 和（必要时）`src/SSHCommon/` 下。

## 总览

| 文件 | 类型 | 变更量预估 |
|------|------|-----------|
| `src/SSHClient/Program.cs` | ✏️ 大改 | 重构 + 新增 ~200 行 |
| `src/SSHClient/Core/RemoteShell.cs` | ✏️ 小改 | 新增输出拦截接口 ~20 行，banner 改 stderr ~5 行 |
| `src/SSHClient/Core/FileTransfer.cs` | ✏️ 小改 | 进度条/文字改 stderr，新增静默开关 ~15 行 |
| `src/SSHClient/Core/NonInteractiveRunner.cs` | 🆕 新建 | ~250 行 |
| `src/SSHClient/Core/CommandResult.cs` | 🆕 新建 | ~50 行（DTO） |
| `src/SSHClient/Core/ExitCodes.cs` | 🆕 新建 | ~20 行（常量） |
| `src/SSHClient/Core/OutputCapture.cs` | 🆕 新建 | ~100 行（buffer + 标记扫描） |
| `src/SSHClient/SSHClient.csproj` | ✏️ 小改 | `<Compile Include>` 追加 4 个新文件 |

**协议层 (`src/SSHCommon/`) 完全不动。** 只用已有的 MessageType 和 DTO。

---

## 🆕 新增文件

### 1. `src/SSHClient/Core/ExitCodes.cs`

```csharp
namespace SSHClient.Core
{
    /// <summary>
    /// 非交互模式下 SSHC.exe 的退出码规范。
    /// 仿 OpenSSH：连接层错误用高位码（253-255），命令本身的 exit code 透传。
    /// </summary>
    public static class ExitCodes
    {
        public const int Success            = 0;    // 命令执行成功 (exit 0)
        public const int Interrupted        = 130;  // Ctrl+C / 中断
        public const int ProtocolError      = 253;  // 标记丢失 / 协议异常
        public const int AuthFailed         = 254;  // 用户名或密码错误
        public const int ConnectionFailed   = 255;  // 连不上目标 / 握手失败
        // 其他退出码 = 远程命令的 %ERRORLEVEL% 透传
    }
}
```

### 2. `src/SSHClient/Core/CommandResult.cs`

```csharp
using Newtonsoft.Json;

namespace SSHClient.Core
{
    /// <summary>
    /// 非交互模式统一的结果 DTO。匹配 02-design-decisions.md D9 schema。
    /// </summary>
    public class CommandResult
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("verb")]
        public string Verb { get; set; }

        [JsonProperty("command", NullValueHandling = NullValueHandling.Include)]
        public string Command { get; set; }

        [JsonProperty("exit_code", NullValueHandling = NullValueHandling.Include)]
        public int? ExitCode { get; set; }

        [JsonProperty("stdout", NullValueHandling = NullValueHandling.Include)]
        public string Stdout { get; set; }

        [JsonProperty("stderr", NullValueHandling = NullValueHandling.Include)]
        public string Stderr { get; set; }

        [JsonProperty("duration_ms")]
        public long DurationMs { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Include)]
        public ErrorDetail Error { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }
    }

    public class ErrorDetail
    {
        [JsonProperty("kind")]
        public string Kind { get; set; }   // connection_refused/connection_timeout/auth_failed/protocol_error/marker_not_found/interrupted

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
```

### 3. `src/SSHClient/Core/OutputCapture.cs`

**职责**：拦截 `ShellOutput` / `ShellError`，缓冲到 StringBuilder，扫描 marker 标记检测命令完成。

```csharp
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SSHClient.Core
{
    public class OutputCapture
    {
        private readonly StringBuilder _stdoutBuf = new StringBuilder();
        private readonly StringBuilder _stderrBuf = new StringBuilder();
        private readonly string _markerPrefix;    // e.g. "__SSHC_DONE_7f3a9c__"
        private readonly Regex _markerRegex;
        private readonly ManualResetEventSlim _completed = new ManualResetEventSlim(false);
        private readonly object _lock = new object();

        public int? ExitCode { get; private set; }

        public OutputCapture(string markerPrefix)
        {
            _markerPrefix = markerPrefix;
            // 匹配 __SSHC_DONE_<guid>_<digits>__
            _markerRegex = new Regex(
                Regex.Escape(markerPrefix) + @"(\d+)__",
                RegexOptions.Compiled);
        }

        /// <summary>
        /// 追加一段 stdout 或 stderr。返回 true 表示已检测到标记，命令结束。
        /// </summary>
        public bool Append(string text, bool isStderr)
        {
            lock (_lock)
            {
                var targetBuf = isStderr ? _stderrBuf : _stdoutBuf;
                targetBuf.Append(text);

                // 只在 stdout 里找标记（echo __MARK_%ERRORLEVEL%__ 走 stdout）
                if (!isStderr)
                {
                    var match = _markerRegex.Match(_stdoutBuf.ToString());
                    if (match.Success)
                    {
                        if (int.TryParse(match.Groups[1].Value, out var code))
                            ExitCode = code;

                        // 截掉标记本身及之后的内容（可能还有后续 prompt）
                        var len = match.Index;
                        // 回溯去掉前面可能残留的换行和空格
                        while (len > 0 && (_stdoutBuf[len - 1] == '\n' || _stdoutBuf[len - 1] == '\r'))
                            len--;
                        _stdoutBuf.Length = len;

                        _completed.Set();
                        return true;
                    }
                }
                return false;
            }
        }

        public bool WaitForCompletion(int timeoutMs)
            => _completed.Wait(timeoutMs);

        public string Stdout { get { lock (_lock) return _stdoutBuf.ToString(); } }
        public string Stderr { get { lock (_lock) return _stderrBuf.ToString(); } }
    }
}
```

**关键设计**：
- 标记只在 **stdout** 里找（因为 `echo __MARK__` 走 stdout）
- 截断逻辑把标记本身从用户可见的 stdout 里去掉
- 线程安全（`lock`），因为 RemoteShell 的消息回调在 WebSocket 线程，主线程在 `WaitForCompletion` 等
- marker prefix 每次调用生成新 GUID，防止用户命令输出恰好包含标记字面量

### 4. `src/SSHClient/Core/NonInteractiveRunner.cs`

**职责**：4 个动词（exec / start / upload / download）的完整流程编排。

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using SSHCommon.Protocol;

namespace SSHClient.Core
{
    public class NonInteractiveRunner
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly bool _jsonOutput;
        private readonly bool _quiet;
        private readonly int _authTimeoutMs = 10000;
        private readonly int _cmdTimeoutMs = 600000;  // 10 分钟硬上限

        public NonInteractiveRunner(string host, int port, string username, string password, bool jsonOutput, bool quiet)
        {
            _host = host;
            _port = port;
            _username = username;
            _password = password;
            _jsonOutput = jsonOutput;
            _quiet = quiet;
        }

        // ========== Public entry points (verbs) ==========

        public int RunExec(string command) => RunShellVerb("exec", command, wrapForDetach: false);

        public int RunStart(string program) => RunShellVerb("start", program, wrapForDetach: true);

        public int RunUpload(string localPath, string remotePath) { /* ... 见下文 ... */ }

        public int RunDownload(string remotePath, string localPath) { /* ... 见下文 ... */ }

        // ========== Core shell flow (exec / start 共享) ==========

        private int RunShellVerb(string verb, string command, bool wrapForDetach)
        {
            var result = new CommandResult
            {
                Host = _host,
                Username = _username,
                Verb = verb,
                Command = command,
                Timestamp = DateTime.Now.ToString("o"),
            };
            var sw = Stopwatch.StartNew();

            // 1. 连接
            var shell = new RemoteShell();
            var authResult = new ManualResetEvent(false);
            bool authOk = false;

            shell.SetSignalHandler(sig =>
            {
                if (sig == "AUTH_OK") { authOk = true; authResult.Set(); }
                else if (sig == "AUTH_FAIL") { authOk = false; authResult.Set(); }
            });

            try
            {
                LogInfo($"Connecting to {_host}:{_port}...");
                shell.Connect(_host, _port, _username, _password);
            }
            catch (Exception ex)
            {
                return FinishWithError(result, sw, "connection_refused", ex.Message);
            }

            if (!authResult.WaitOne(_authTimeoutMs) || !shell.IsConnected)
                return FinishWithError(result, sw, "connection_timeout", "Connection or handshake timed out");

            if (!authOk)
                return FinishWithError(result, sw, "auth_failed", "Invalid username or password");

            // 2. 准备标记 + 输出捕获
            var markerPrefix = $"__SSHC_DONE_{Guid.NewGuid():N}_";
            var capture = new OutputCapture(markerPrefix);
            shell.SetShellOutputHandler((text, isStderr) => capture.Append(text, isStderr));

            // 3. 注册 Ctrl+C 转发
            ConsoleCancelEventHandler cancelHandler = (s, e) =>
            {
                e.Cancel = true;
                LogInfo("\n^C — sending interrupt");
                shell.SendInterrupt();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                // 4. 构造并发送真实 payload
                var realCmd = wrapForDetach
                    ? $"start \"\" {command}"
                    : command;
                // & 保证无论 realCmd 成功失败都执行 echo，拿到 %ERRORLEVEL%
                var payload = $"{realCmd} & echo {markerPrefix}%ERRORLEVEL%__\n";
                shell.SendInput(payload);

                // 5. 等待标记出现 or 超时
                if (!capture.WaitForCompletion(_cmdTimeoutMs))
                    return FinishWithError(result, sw, "marker_not_found", "Command did not complete within timeout");

                // 6. 主动断开（释放 cmd.exe）
                shell.SendInput("exit\n");
                Thread.Sleep(100);  // 给 cmd.exe 一点时间处理 exit
                shell.Disconnect();
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            // 7. 构造结果
            result.Ok = true;
            result.ExitCode = capture.ExitCode;
            result.Stdout = capture.Stdout;
            result.Stderr = capture.Stderr;
            result.DurationMs = sw.ElapsedMilliseconds;
            EmitResult(result);

            return capture.ExitCode ?? ExitCodes.ProtocolError;
        }

        // ========== Upload / Download 流程 ==========
        // 基本套路：Connect → Auth → 调 shell.Upload/Download → 等 TRANSFER_DONE 信号 → 断开
        // 详见下方"Upload/Download 细节"小节

        // ========== Helpers ==========

        private int FinishWithError(CommandResult result, Stopwatch sw, string errKind, string message)
        {
            result.Ok = false;
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Error = new ErrorDetail { Kind = errKind, Message = message };
            EmitResult(result);

            switch (errKind)
            {
                case "connection_refused":
                case "connection_timeout":   return ExitCodes.ConnectionFailed;
                case "auth_failed":          return ExitCodes.AuthFailed;
                case "interrupted":          return ExitCodes.Interrupted;
                default:                     return ExitCodes.ProtocolError;
            }
        }

        private void EmitResult(CommandResult result)
        {
            if (_jsonOutput)
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(result, Formatting.Indented));
            }
            else
            {
                // 人类可读：stdout 走 stdout，其他信息走 stderr
                if (!string.IsNullOrEmpty(result.Stdout))
                    Console.Out.Write(result.Stdout);
                if (!string.IsNullOrEmpty(result.Stderr))
                    Console.Error.Write(result.Stderr);
                if (result.Error != null && !_quiet)
                    Console.Error.WriteLine($"[error] {result.Error.Kind}: {result.Error.Message}");
            }
        }

        private void LogInfo(string msg)
        {
            if (!_quiet && !_jsonOutput)
                Console.Error.WriteLine(msg);
        }
    }
}
```

#### Upload/Download 细节

```csharp
public int RunUpload(string localPath, string remotePath)
{
    var result = new CommandResult
    {
        Host = _host, Username = _username, Verb = "upload",
        Command = $"{localPath} -> {remotePath ?? Path.GetFileName(localPath)}",
        Timestamp = DateTime.Now.ToString("o"),
    };
    var sw = Stopwatch.StartNew();

    if (!File.Exists(localPath))
        return FinishWithError(result, sw, "protocol_error", $"Local file not found: {localPath}");

    var shell = new RemoteShell();
    var done = new ManualResetEvent(false);
    bool authOk = false, transferOk = false;

    shell.SetSignalHandler(sig =>
    {
        if (sig == "AUTH_OK") authOk = true;
        else if (sig == "AUTH_FAIL") authOk = false;
        else if (sig == "TRANSFER_DONE") { transferOk = true; done.Set(); }
    });

    try { shell.Connect(_host, _port, _username, _password); }
    catch (Exception ex) { return FinishWithError(result, sw, "connection_refused", ex.Message); }

    // 等认证
    Thread.Sleep(500);  // 简化：等 RemoteShell 的 auth roundtrip
    if (!shell.IsConnected)
        return FinishWithError(result, sw, "connection_timeout", "Handshake timed out");
    if (!authOk)
        return FinishWithError(result, sw, "auth_failed", "Invalid credentials");

    // 调 FileTransfer（它会通过 RemoteShell 内部的 _ws 发送）
    shell.Upload(localPath, remotePath);

    if (!done.WaitOne(_cmdTimeoutMs))
        return FinishWithError(result, sw, "protocol_error", "Upload completion signal not received");

    shell.Disconnect();

    result.Ok = true;
    result.ExitCode = 0;
    result.DurationMs = sw.ElapsedMilliseconds;
    EmitResult(result);
    return ExitCodes.Success;
}
```

`RunDownload` 类似，但需要临时修改 `RemoteShell.HandleMessage` 里对 `DownloadStart/Chunk/Complete` 的处理，或者让 `NonInteractiveRunner` 自己订阅这些消息。**简化方案**：复用现有 `_onOutput?.Invoke($"MSG:{raw}")` 转发机制，在 Runner 这边解析 `MSG:` 前缀的消息，直接调用 `FileTransfer.HandleDownloadMessage`。

---

## ✏️ 修改文件

### `src/SSHClient/Core/RemoteShell.cs`

#### 修改 1：新增独立的 Shell 输出拦截器（**关键改动**）

**位置**：`_onOutput` 字段附近（当前 :13 行）

```csharp
// 追加
private Action<string, bool> _onShellOutput;  // (text, isStderr)

public void SetShellOutputHandler(Action<string, bool> handler)
{
    _onShellOutput = handler;
}
```

#### 修改 2：重命名 `SetOutputHandler` → `SetSignalHandler`（避免混淆）

**位置**：当前 `SetOutputHandler` 方法（:57-60）

```csharp
// 改名
public void SetSignalHandler(Action<string> handler)
{
    _onOutput = handler;
}
```

然后把 Program.cs 里所有 `shell.SetOutputHandler(...)` 调用改成 `SetSignalHandler(...)`。

#### 修改 3：路由 ShellOutput/ShellError

**位置**：`HandleMessage` 里的 `case MessageType.ShellOutput/ShellError`（:108-116）

**原代码**：
```csharp
case MessageType.ShellOutput:
    var output = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
    Console.Write(output.Text);
    break;

case MessageType.ShellError:
    var errOutput = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
    Console.Write(errOutput.Text);
    break;
```

**新代码**：
```csharp
case MessageType.ShellOutput:
    var output = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
    if (_onShellOutput != null) _onShellOutput(output.Text, false);
    else Console.Write(output.Text);
    break;

case MessageType.ShellError:
    var errOutput = JsonConvert.DeserializeObject<ShellOutputData>(msg.Data);
    if (_onShellOutput != null) _onShellOutput(errOutput.Text, true);
    else Console.Write(errOutput.Text);
    break;
```

#### 修改 4：Banner 改走 stderr

把 RemoteShell.cs 里这些 `Console.WriteLine` 改成 `Console.Error.WriteLine`：

| 行号 | 文本 |
|-----|------|
| :31 | `Disconnected from server` |
| :36 | `Connection error` |
| :53 | `Failed to connect` |
| :98 | `Authenticated successfully` |
| :103 | `Authentication failed` |
| :120 | `Error: {message}` |
| :129 | `Warning 即将断开连接` |
| :134 | `Kicked 您已被断开` |
| :141-142 | `Upload completed/failed` |

**批量替换**：`Console.WriteLine(` → `Console.Error.WriteLine(` （在这个文件里）

---

### `src/SSHClient/Core/FileTransfer.cs`

#### 修改 1：进度条和文字改 stderr

把所有 `Console.Write` / `Console.WriteLine` 改成 `Console.Error.Write` / `Console.Error.WriteLine`（行号 :18, :63, :104, :116）。

#### 修改 2：静默开关

新增一个静态 `Quiet` 属性：

```csharp
public static class FileTransfer
{
    public static bool Quiet { get; set; } = false;

    // DrawProgressBar 内部改成：
    private static void DrawProgressBar(...)
    {
        if (Quiet) return;
        // ... 原有代码，但改走 Console.Error
    }
}
```

`NonInteractiveRunner` 在构造时设置 `FileTransfer.Quiet = _quiet || _jsonOutput`。

> **副作用注意**：静态属性在并行调用时会有问题。但考虑到非交互模式下单个 SSHC 进程只跑一次 upload/download，进程内不会有并发，问题可以接受。Agent 并行时是**多进程**并行，每个进程有独立的静态状态，也没问题。

---

### `src/SSHClient/Program.cs`

#### 修改 1：`Main` 方法改成 verb 分派

**原代码** (:12-29)：
```csharp
static void Main(string[] args)
{
    if (args.Length == 0) { PrintUsage(); Console.ReadKey(); return; }
    var cmd = args[0].ToLower();
    if (cmd != "connect") { PrintUsage(); Console.ReadKey(); return; }
    // ... connect 流程
}
```

**新代码**：
```csharp
static int Main(string[] args)
{
    if (args.Length == 0) { PrintUsage(); return ExitCodes.Success; }

    var verb = args[0].ToLower();
    switch (verb)
    {
        case "connect":  return RunConnect(args);
        case "exec":     return RunNonInteractive(verb, args);
        case "start":    return RunNonInteractive(verb, args);
        case "upload":   return RunNonInteractive(verb, args);
        case "download": return RunNonInteractive(verb, args);
        case "help":
        case "-h":
        case "--help":   PrintUsage(); return ExitCodes.Success;
        default:
            Console.Error.WriteLine($"Unknown verb: {verb}");
            PrintUsage();
            return ExitCodes.ProtocolError;
    }
}
```

**注意**：
- `Main` 返回类型改成 `int`（返回退出码）
- 移除所有 `Console.ReadKey()`
- `connect` 动词保留原交互流程，抽成 `RunConnect` 方法

#### 修改 2：新增 `RunConnect`（从原 Main 抽出）

把原 Main 的 L31-L177 整段代码挪到新方法：
```csharp
static int RunConnect(string[] args)
{
    // 保持原逻辑：交互式 REPL
    // 但把 ReadKey 的 "press any key" 删掉
    // 结尾 return ExitCodes.Success;
}
```

#### 修改 3：新增 `RunNonInteractive` 统一入口

```csharp
static int RunNonInteractive(string verb, string[] args)
{
    var opts = ParseCommonArgs(args);
    if (opts == null) return ExitCodes.ProtocolError;  // 解析失败

    var runner = new NonInteractiveRunner(
        opts.Host, opts.Port, opts.Username, opts.Password,
        opts.JsonOutput, opts.Quiet);

    switch (verb)
    {
        case "exec":
            return runner.RunExec(opts.Positional);
        case "start":
            return runner.RunStart(opts.Positional);
        case "upload":
            var up = opts.Positional.Split(new[] { ' ' }, 2);
            return runner.RunUpload(up[0], up.Length > 1 ? up[1] : null);
        case "download":
            var dn = opts.Positional.Split(new[] { ' ' }, 2);
            return runner.RunDownload(dn[0], dn.Length > 1 ? dn[1] : null);
        default:
            return ExitCodes.ProtocolError;
    }
}
```

#### 修改 4：新增 `ParseCommonArgs`

```csharp
class CommonOpts
{
    public string Host;
    public int Port = 22222;
    public string Username;
    public string Password;
    public bool JsonOutput;
    public bool Quiet;
    public string Positional;  // 动词后的主要参数（命令字符串或文件路径对）
}

static CommonOpts ParseCommonArgs(string[] args)
{
    // args[0] = 动词, args[1] = host
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"Usage: SSHC {args[0]} <host> -u <username> -P <password> ...");
        return null;
    }
    var opts = new CommonOpts { Host = args[1] };
    var positional = new System.Text.StringBuilder();

    int i = 2;
    while (i < args.Length)
    {
        switch (args[i])
        {
            case "-p": opts.Port = int.Parse(args[i + 1]); i += 2; break;
            case "-u": opts.Username = args[i + 1]; i += 2; break;
            case "-P": opts.Password = args[i + 1]; i += 2; break;
            case "-q":
            case "--quiet": opts.Quiet = true; i++; break;
            case "--json": opts.JsonOutput = true; i++; break;
            default:
                if (positional.Length > 0) positional.Append(' ');
                positional.Append(args[i]);
                i++;
                break;
        }
    }
    opts.Positional = positional.ToString();

    if (string.IsNullOrEmpty(opts.Host) || string.IsNullOrEmpty(opts.Username) || string.IsNullOrEmpty(opts.Password))
    {
        Console.Error.WriteLine("Missing required arguments: -u <username> -P <password>");
        return null;
    }
    return opts;
}
```

#### 修改 5：更新 `PrintUsage`

**位置**：:317-342

新增说明所有 5 个动词的用法：

```csharp
static void PrintUsage()
{
    Console.WriteLine("SSHC - Simple SSH-like tool over WebSocket");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  SSHC connect  <host> [-p <port>] -u <user>");
    Console.WriteLine("      Interactive REPL (prompts for password)");
    Console.WriteLine();
    Console.WriteLine("  SSHC exec     <host> -u <user> -P <pwd> [opts] \"<command>\"");
    Console.WriteLine("  SSHC start    <host> -u <user> -P <pwd> [opts] \"<program> [args]\"");
    Console.WriteLine("  SSHC upload   <host> -u <user> -P <pwd> [opts] <local> [<remote>]");
    Console.WriteLine("  SSHC download <host> -u <user> -P <pwd> [opts] <remote> [<local>]");
    Console.WriteLine();
    Console.WriteLine("Options for non-interactive verbs:");
    Console.WriteLine("  -p <port>    Server port (default: 22222)");
    Console.WriteLine("  -u <user>    Username (required)");
    Console.WriteLine("  -P <pwd>     Password (required)");
    Console.WriteLine("  -q, --quiet  Suppress banner output");
    Console.WriteLine("  --json       Emit result as JSON to stdout");
    Console.WriteLine();
    Console.WriteLine("Exit codes:");
    Console.WriteLine("  0        Command succeeded");
    Console.WriteLine("  130      Interrupted (Ctrl+C)");
    Console.WriteLine("  253      Protocol / marker error");
    Console.WriteLine("  254      Authentication failed");
    Console.WriteLine("  255      Connection failed");
    Console.WriteLine("  other    Passthrough of remote %ERRORLEVEL%");
}
```

#### 修改 6：移除 `Console.ReadKey()` 阻塞点

原 :17-18 和 :26-27：

```csharp
Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();
```

**全部删除**。非交互模式下这是阻塞 bug；交互模式下也不需要——用户自己能看到错误。

#### 修改 7：修复双重 SetOutputHandler bug

`RunConnect` 里 L73 和 L79 有两次 `SetOutputHandler`（重命名后是 `SetSignalHandler`）。**合并成一个**：

```csharp
shell.SetSignalHandler(signal =>
{
    if (signal.StartsWith("MSG:"))
    {
        // 下载消息转发给 FileTransfer 处理
    }
    else if (signal.StartsWith("CLIENTLIST:"))
    {
        var json = signal.Substring("CLIENTLIST:".Length);
        try
        {
            var list = JsonConvert.DeserializeObject<ClientListData>(json);
            PrintClientList(list);
        }
        catch { }
    }
    else if (signal == "AUTH_OK" || signal == "AUTH_FAIL")
    {
        authResult.Set();
    }
});
```

---

### `src/SSHClient/SSHClient.csproj`

在 `<ItemGroup>` 里追加 4 个新文件：

```xml
<Compile Include="Core\ExitCodes.cs" />
<Compile Include="Core\CommandResult.cs" />
<Compile Include="Core\OutputCapture.cs" />
<Compile Include="Core\NonInteractiveRunner.cs" />
```

> **前提确认**：如果 `.csproj` 用的是 SDK-style (`<Project Sdk="Microsoft.NET.Sdk">`)，新文件会自动被 glob 包含，这一步可以跳过。动手前先读 csproj 确认。

---

## 🎯 验收要点

### 功能性
- [ ] `SSHC exec 127.0.0.1 -u admin -P admin123 "dir"` 打印 dir 输出到 stdout，退出码 0
- [ ] `SSHC exec 127.0.0.1 -u admin -P admin123 "exit 42"` 退出码 42
- [ ] `SSHC exec 127.0.0.1 -u admin -P wrongpwd "dir"` 退出码 254，stderr 有错误
- [ ] `SSHC exec 1.2.3.4 -u admin -P x "dir"` 退出码 255
- [ ] `SSHC exec ... --json ...` 输出合法 JSON，字段齐全
- [ ] `SSHC start 127.0.0.1 -u admin -P admin123 "notepad.exe"` 在设备桌面弹出 notepad
- [ ] `SSHC upload 127.0.0.1 ... C:\test.txt` 成功上传
- [ ] `SSHC download 127.0.0.1 ... C:\remote.txt C:\local.txt` 成功下载
- [ ] `SSHC connect 127.0.0.1 -u admin` 交互式 REPL 功能不变（回归）

### 非功能性
- [ ] stdin 重定向下不崩溃（`echo foo | SSHC exec ...` 能跑）
- [ ] 能被 Agent 并行 spawn 10 个进程不互相干扰
- [ ] 横幅都走 stderr，`SSHC exec ... > out.txt` 里 out.txt 只有命令输出
- [ ] `-q` 下完全无 stderr 噪音
- [ ] Ctrl+C 下服务端 cmd.exe 被正确中断，不留僵尸进程

### 边界
- [ ] 用户命令输出恰好包含 `__SSHC_DONE_` 字面量也不会误判（GUID 独一无二）
- [ ] 命令 10 分钟没出标记会超时返回 253（`marker_not_found`）
- [ ] 远程 cmd.exe 崩溃 / WebSocket 中断 → 返回合理错误码

---

## 下一步

→ [04-implementation-plan.md](./04-implementation-plan.md) — 分阶段落地顺序、每步验证方法、风险回滚
