using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json;

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
        private const int CommandTimeoutMs = 600000;  // 10 分钟硬上限

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
            return RunShellVerb("exec", command, wrapForDetach: false);
        }

        /// <summary>启动远程 GUI 程序（封装 `start "" ...`），立即返回。P3 实现。</summary>
        public int RunStart(string program)
        {
            return RunShellVerb("start", program, wrapForDetach: true);
        }

        /// <summary>上传文件。P4 实现。</summary>
        public int RunUpload(string localPath, string remotePath)
        {
            Console.Error.WriteLine("[upload] not implemented yet (P4)");
            return ExitCodes.ProtocolError;
        }

        /// <summary>下载文件。P4 实现。</summary>
        public int RunDownload(string remotePath, string localPath)
        {
            Console.Error.WriteLine("[download] not implemented yet (P4)");
            return ExitCodes.ProtocolError;
        }

        // ================== Core shell flow (exec / start shared) ==================

        private int RunShellVerb(string verb, string command, bool wrapForDetach)
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
                if (!capture.WaitForCompletion(CommandTimeoutMs))
                {
                    TryDisconnect(shell);
                    return FinishWithError(result, sw, "marker_not_found",
                        $"Command did not complete within {CommandTimeoutMs / 1000}s");
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
