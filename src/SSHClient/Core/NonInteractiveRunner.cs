using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using SSHCommon.Protocol;

namespace SSHClient.Core
{
    /// <summary>
    /// 非交互模式入口——每次调用对应一个完整的 connect → auth → execute → disconnect 生命周期。
    /// Agent 并行调用由多进程承载，类内不共享状态。
    /// </summary>
    public class NonInteractiveRunner
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly bool _jsonOutput;
        private readonly bool _quiet;
        private const int AuthTimeoutMs = 10000;
        private const int ExecTimeoutMs = 600000;   // exec 命令的硬上限 10 分钟
        private const int StartTimeoutMs = 5000;    // start 是 fire-and-forget，5 秒没拿到 end marker 就假定已 detach

        public NonInteractiveRunner(string host, int port, string username, string password, bool jsonOutput, bool quiet)
        {
            _host = host;
            _port = port;
            _username = username;
            _password = password;
            _jsonOutput = jsonOutput;
            _quiet = quiet;
        }

        // ================== Public verbs ==================

        /// <summary>执行一条远程命令，等待完成，返回 %ERRORLEVEL%（或连接层错误码）。</summary>
        public int RunExec(string command)
        {
            return RunShellVerb("exec", command, wrapForDetach: false, commandTimeoutMs: ExecTimeoutMs);
        }

        /// <summary>
        /// 启动远程 GUI 程序（封装 `start "" ...`），fire-and-forget 语义。
        /// 短超时（5 秒）：若超时时 begin marker 已出现（cmd.exe 已收到并执行了 start），
        /// 假定程序已 detach 成功，返回 0。
        /// </summary>
        public int RunStart(string program)
        {
            return RunShellVerb("start", program, wrapForDetach: true, commandTimeoutMs: StartTimeoutMs);
        }

        /// <summary>上传文件：connect → auth → 分块发送 → 等 TRANSFER_DONE → 断开。</summary>
        public int RunUpload(string localPath, string remotePath)
        {
            var result = new CommandResult
            {
                Host = _host,
                Username = _username,
                Verb = "upload",
                Command = $"{localPath} -> {remotePath ?? Path.GetFileName(localPath)}",
                Timestamp = DateTime.Now.ToString("o"),
            };
            var sw = Stopwatch.StartNew();

            if (!File.Exists(localPath))
                return FinishWithError(result, sw, "protocol_error", $"Local file not found: {localPath}");

            var shell = new RemoteShell();
            if (!TryConnectAndAuth(shell, result, sw, out int connErr, out ManualResetEvent transferDone, extraSignal: null))
                return connErr;

            FileTransfer.Quiet = _quiet || _jsonOutput;

            try
            {
                shell.Upload(localPath, remotePath);
                if (!transferDone.WaitOne(ExecTimeoutMs))
                {
                    TryDisconnect(shell);
                    return FinishWithError(result, sw, "protocol_error", "Upload completion signal not received");
                }
            }
            catch (Exception ex)
            {
                TryDisconnect(shell);
                return FinishWithError(result, sw, "protocol_error", $"Upload failed: {ex.Message}");
            }

            TryDisconnect(shell);

            result.Ok = true;
            result.ExitCode = 0;
            result.DurationMs = sw.ElapsedMilliseconds;
            EmitResult(result);
            return ExitCodes.Success;
        }

        /// <summary>下载文件：connect → auth → 请求下载 → 拦截 MSG: 下载消息写入本地 → 等 TRANSFER_DONE → 断开。</summary>
        public int RunDownload(string remotePath, string localPath)
        {
            var result = new CommandResult
            {
                Host = _host,
                Username = _username,
                Verb = "download",
                Command = $"{remotePath} -> {localPath ?? "(filename from server)"}",
                Timestamp = DateTime.Now.ToString("o"),
            };
            var sw = Stopwatch.StartNew();

            var shell = new RemoteShell();
            DownloadState downloadState = null;

            // 下载需要额外拦截 MSG: 前缀的消息（DownloadStart/Chunk/Complete），
            // 转发给 FileTransfer.HandleDownloadMessage 去做文件写入
            Action<string> downloadSignal = signal =>
            {
                if (!signal.StartsWith("MSG:")) return;
                try
                {
                    var msg = ProtocolMessage.FromJson(signal.Substring(4));
                    FileTransfer.HandleDownloadMessage(msg, localPath, ref downloadState);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Download parse error: {ex.Message}");
                }
            };

            if (!TryConnectAndAuth(shell, result, sw, out int connErr, out ManualResetEvent transferDone, extraSignal: downloadSignal))
                return connErr;

            FileTransfer.Quiet = _quiet || _jsonOutput;

            try
            {
                shell.Download(remotePath, localPath);
                if (!transferDone.WaitOne(ExecTimeoutMs))
                {
                    TryDisconnect(shell);
                    return FinishWithError(result, sw, "protocol_error", "Download completion signal not received");
                }
            }
            catch (Exception ex)
            {
                TryDisconnect(shell);
                return FinishWithError(result, sw, "protocol_error", $"Download failed: {ex.Message}");
            }

            TryDisconnect(shell);

            result.Ok = true;
            result.ExitCode = 0;
            result.DurationMs = sw.ElapsedMilliseconds;
            EmitResult(result);
            return ExitCodes.Success;
        }

        /// <summary>
        /// 共享的连接 + 认证流程：注册 signal handler（含可选的额外 handler）、Connect、等认证结果。
        /// </summary>
        /// <param name="extraSignal">额外的 signal 处理（例如 download 需要拦截 MSG:）</param>
        /// <param name="transferDone">输出：TRANSFER_DONE 的等待 event，供调用方等文件传输完成</param>
        /// <returns>true = 连接 + 认证都成功，false = 已经通过 FinishWithError 输出错误，errorCode 含退出码</returns>
        private bool TryConnectAndAuth(
            RemoteShell shell,
            CommandResult result,
            Stopwatch sw,
            out int errorCode,
            out ManualResetEvent transferDone,
            Action<string> extraSignal)
        {
            errorCode = 0;
            var authResult = new ManualResetEvent(false);
            var done = new ManualResetEvent(false);
            transferDone = done;
            bool authOk = false;

            shell.SetSignalHandler(signal =>
            {
                if (signal == "AUTH_OK") { authOk = true; authResult.Set(); }
                else if (signal == "AUTH_FAIL") { authOk = false; authResult.Set(); }
                else if (signal == "TRANSFER_DONE") { done.Set(); }
                extraSignal?.Invoke(signal);
            });

            // 关键：注册一个 no-op shell 输出拦截器，丢弃 cmd.exe 欢迎横幅和 prompt。
            // upload/download 不需要 shell 输出，这样能防止它们污染 stdout。
            shell.SetShellOutputHandler((_, __) => { });

            LogInfo($"Connecting to {_host}:{_port}...");

            try
            {
                shell.Connect(_host, _port, _username, _password);
            }
            catch (Exception ex)
            {
                errorCode = FinishWithError(result, sw, "connection_refused", ex.Message);
                return false;
            }

            if (!shell.IsConnected)
            {
                errorCode = FinishWithError(result, sw, "connection_refused", $"Failed to reach {_host}:{_port}");
                return false;
            }

            if (!authResult.WaitOne(AuthTimeoutMs))
            {
                TryDisconnect(shell);
                errorCode = FinishWithError(result, sw, "connection_timeout", "Handshake timed out");
                return false;
            }

            if (!authOk)
            {
                TryDisconnect(shell);
                errorCode = FinishWithError(result, sw, "auth_failed", "Invalid username or password");
                return false;
            }

            return true;
        }

        // ================== Core shell flow (exec / start shared) ==================

        private int RunShellVerb(string verb, string command, bool wrapForDetach, int commandTimeoutMs)
        {
            if (string.IsNullOrEmpty(command))
            {
                Console.Error.WriteLine($"[{verb}] missing command argument");
                return ExitCodes.ProtocolError;
            }

            var result = new CommandResult
            {
                Host = _host,
                Username = _username,
                Verb = verb,
                Command = command,
                Timestamp = DateTime.Now.ToString("o"),
            };
            var sw = Stopwatch.StartNew();

            // 1) 生成本次调用独有的 GUID 标记（避免用户命令输出恰好包含固定字面量造成误匹配）
            var guid = Guid.NewGuid().ToString("N");
            var beginMarker = $"__SSHC_BEGIN_{guid}__";
            var endMarkerPrefix = $"__SSHC_DONE_{guid}_";
            var capture = new OutputCapture(beginMarker, endMarkerPrefix);

            // 2) 建立连接
            var shell = new RemoteShell();
            var authResult = new ManualResetEvent(false);
            bool authOk = false;

            shell.SetSignalHandler(signal =>
            {
                if (signal == "AUTH_OK")   { authOk = true;  authResult.Set(); }
                else if (signal == "AUTH_FAIL") { authOk = false; authResult.Set(); }
            });

            // 关键：在 Connect 之前就注册 ShellOutput 拦截器，
            // 避免服务端 cmd.exe 欢迎横幅在 Auth 到 handler 注册的空窗期里漏到 Console
            shell.SetShellOutputHandler((text, isStderr) => capture.Append(text, isStderr));

            LogInfo($"Connecting to {_host}:{_port}...");

            try
            {
                shell.Connect(_host, _port, _username, _password);
            }
            catch (Exception ex)
            {
                return FinishWithError(result, sw, "connection_refused", ex.Message);
            }

            if (!shell.IsConnected)
                return FinishWithError(result, sw, "connection_refused", $"Failed to reach {_host}:{_port}");

            // 3) 等待认证结果
            if (!authResult.WaitOne(AuthTimeoutMs))
            {
                TryDisconnect(shell);
                return FinishWithError(result, sw, "connection_timeout", "Handshake timed out");
            }

            if (!authOk)
            {
                TryDisconnect(shell);
                return FinishWithError(result, sw, "auth_failed", "Invalid username or password");
            }

            // 4) Ctrl+C 转发（P5 完善，这里先埋一个最小实现）
            ConsoleCancelEventHandler cancelHandler = (s, e) =>
            {
                e.Cancel = true;
                LogInfo("\n^C — forwarding interrupt to remote");
                try { shell.SendInterrupt(); } catch { }
            };
            Console.CancelKeyPress += cancelHandler;

            // 把 realCmd 声明提到 try 外面，因为 StripEchoedCommands 需要它来剥离回显行
            var realCmd = wrapForDetach
                ? $"start \"\" {command}"
                : command;

            try
            {
                // 5) 构造 4 行 payload：
                //    @echo off         —— 关闭 cmd.exe 的命令回显（管道模式下实际无效，但无害）
                //    echo <beginMarker> —— phase 1 结束标志：OutputCapture 会丢弃这行之前的所有噪音
                //    <realCmd>         —— 用户的真实命令
                //    echo <endMarker>  —— phase 2 结束标志：OutputCapture 提取 %ERRORLEVEL% 并终止
                //
                //    每行之间用 \n 分隔，cmd.exe 会逐行顺序执行。
                //    注意：cmd.exe 在 stdin 被管道化时会回显 realCmd 和 echo <endMarker> 这两行，
                //    Phase 1/2 的截断保留它们，后续由 StripEchoedCommands 按行精确剥离。
                var payload =
                    "@echo off\n" +
                    $"echo {beginMarker}\n" +
                    $"{realCmd}\n" +
                    $"echo {endMarkerPrefix}%ERRORLEVEL%__\n";
                shell.SendInput(payload);

                // 6) 等待 end marker 出现
                if (!capture.WaitForCompletion(commandTimeoutMs))
                {
                    // start 动词是 fire-and-forget 语义：只要 cmd.exe 确实收到并执行了我们的 payload
                    // （phase 1 begin marker 已出现），就假定 start 的目标程序已 detach 成功，返回 0
                    if (wrapForDetach && capture.IsPhase1Done)
                    {
                        LogInfo($"[{verb}] end marker not received within {commandTimeoutMs / 1000}s, " +
                                "assuming detached (phase 1 confirmed cmd.exe received payload)");
                        try { shell.SendInput("exit\n"); } catch { }
                        Thread.Sleep(100);
                        TryDisconnect(shell);

                        result.Ok = true;
                        result.ExitCode = 0;
                        result.Stdout = StripEchoedCommands(capture.Stdout, realCmd, $"echo {endMarkerPrefix}%ERRORLEVEL%__");
                        result.Stderr = capture.Stderr;
                        result.DurationMs = sw.ElapsedMilliseconds;
                        EmitResult(result);
                        return ExitCodes.Success;
                    }

                    TryDisconnect(shell);
                    return FinishWithError(result, sw, "marker_not_found",
                        $"Command did not complete within {commandTimeoutMs / 1000}s");
                }

                // 7) 主动 exit 释放 cmd.exe
                try { shell.SendInput("exit\n"); } catch { }
                Thread.Sleep(100);
                TryDisconnect(shell);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            // 8) 输出结果
            // cmd.exe 在 stdin 被管道化时会把收到的每一行命令回显到 stdout，即使 @echo off 也压不住。
            // Phase 1/2 之间我们发送的 <realCmd> 和 `echo <endMarker>` 两行会作为回显污染 stdout，
            // 需要在这里按行精确剥离。
            var echoedEndMarker = $"echo {endMarkerPrefix}%ERRORLEVEL%__";
            result.Ok = true;
            result.ExitCode = capture.ExitCode;
            result.Stdout = StripEchoedCommands(capture.Stdout, realCmd, echoedEndMarker);
            result.Stderr = capture.Stderr;
            result.DurationMs = sw.ElapsedMilliseconds;
            EmitResult(result);

            return capture.ExitCode ?? ExitCodes.ProtocolError;
        }

        /// <summary>
        /// 从 stdout 中剥离已知的命令回显行：
        /// - 开头第一行如果等于用户命令原文，剥掉
        /// - 结尾最后一行（忽略尾部空行）如果等于 end marker 的 echo 命令，剥掉
        /// 保留所有其他行，保持原本的行分隔（统一 \r\n 输出）。
        /// </summary>
        private static string StripEchoedCommands(string text, string userCmd, string echoedEndMarker)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 按 \n 拆行并去掉每行尾的 \r
            var lines = new List<string>();
            var start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    var end = i;
                    if (end > start && text[end - 1] == '\r') end--;
                    lines.Add(text.Substring(start, end - start));
                    start = i + 1;
                }
            }
            if (start < text.Length) lines.Add(text.Substring(start));

            // 开头：剥去与用户命令完全相同的第一行
            if (lines.Count > 0 && lines[0] == userCmd)
                lines.RemoveAt(0);

            // 结尾：剥掉尾部空行，再看最后一行是否是 echo 命令回显
            while (lines.Count > 0 && string.IsNullOrEmpty(lines[lines.Count - 1]))
                lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0 && lines[lines.Count - 1] == echoedEndMarker)
                lines.RemoveAt(lines.Count - 1);

            return string.Join("\r\n", lines);
        }

        // ================== Helpers ==================

        private static void TryDisconnect(RemoteShell shell)
        {
            try { shell.Disconnect(); } catch { }
        }

        private int FinishWithError(CommandResult result, Stopwatch sw, string errKind, string message)
        {
            result.Ok = false;
            result.DurationMs = sw.ElapsedMilliseconds;
            result.Error = new ErrorDetail { Kind = errKind, Message = message };
            EmitResult(result);

            switch (errKind)
            {
                case "connection_refused":
                case "connection_timeout": return ExitCodes.ConnectionFailed;
                case "auth_failed":        return ExitCodes.AuthFailed;
                case "interrupted":        return ExitCodes.Interrupted;
                default:                   return ExitCodes.ProtocolError;
            }
        }

        private void EmitResult(CommandResult result)
        {
            if (_jsonOutput)
            {
                // P5 会完善 JSON 细节；P2 先直接序列化
                Console.Out.WriteLine(JsonConvert.SerializeObject(result, Formatting.Indented));
                return;
            }

            // 人类可读路径：stdout 走 stdout，辅助信息走 stderr
            if (!string.IsNullOrEmpty(result.Stdout))
                Console.Out.Write(result.Stdout);
            if (!string.IsNullOrEmpty(result.Stderr))
                Console.Error.Write(result.Stderr);
            if (result.Error != null && !_quiet)
                Console.Error.WriteLine($"[error] {result.Error.Kind}: {result.Error.Message}");
        }

        private void LogInfo(string msg)
        {
            if (_quiet || _jsonOutput) return;
            Console.Error.WriteLine(msg);
        }
    }
}
