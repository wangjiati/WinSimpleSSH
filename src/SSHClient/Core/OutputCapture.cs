using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SSHClient.Core
{
    /// <summary>
    /// 非交互模式下拦截 ShellOutput/ShellError，缓冲到内存，用两阶段标记协议检测命令完成。
    ///
    /// 两阶段协议：
    /// 1. Begin marker：调用方发送 `echo &lt;beginMarker&gt;` 作为命令序列的第一步。
    ///    在 begin marker 出现前收到的所有 stdout（cmd.exe 欢迎横幅、初始 prompt、@echo off 的命令回显等）
    ///    全部丢弃。
    /// 2. End marker：调用方在用户命令末尾附加 `echo &lt;endPrefix&gt;%ERRORLEVEL%__`。
    ///    捕获到 end marker 后关闭 buffer，从中抽取 exit code，唤醒主线程。
    ///
    /// 约束：调用方需用 `@echo off` 关闭 cmd.exe 的命令回显，否则 begin/end 命令行本身会被回显进 stdout
    /// （虽然正则不匹配字面 %ERRORLEVEL%，但会污染输出）。
    /// </summary>
    public class OutputCapture
    {
        private readonly StringBuilder _stdoutBuf = new StringBuilder();
        private readonly StringBuilder _stderrBuf = new StringBuilder();
        private readonly string _beginMarker;
        private readonly Regex _beginMarkerRegex;  // 必须匹配行首以避开 cmd.exe 管道模式的命令回显
        private readonly Regex _endMarkerRegex;
        private readonly ManualResetEventSlim _completed = new ManualResetEventSlim(false);
        private readonly object _lock = new object();
        private bool _phase1Done;  // begin marker 已出现，可以开始在 phase 2 扫描 end marker
        private bool _closed;      // end marker 已出现，拒绝后续追加

        public int? ExitCode { get; private set; }

        /// <summary>
        /// 构造一个两阶段 OutputCapture。
        /// </summary>
        /// <param name="beginMarker">开始标记的完整字符串，例如 "__SSHC_BEGIN_7f3a9c__"</param>
        /// <param name="endMarkerPrefix">结束标记前缀（带末尾下划线），例如 "__SSHC_DONE_7f3a9c_"，
        /// 实际出现的形式是 "__SSHC_DONE_7f3a9c_0__"</param>
        public OutputCapture(string beginMarker, string endMarkerPrefix)
        {
            _beginMarker = beginMarker;
            // 行首锚点至关重要：cmd.exe 在 stdin 被管道化时会把收到的每一行回显到 stdout，
            // 回显行形如 "echo __SSHC_BEGIN_xxx__"——标记前有 "echo " 前缀。
            // 只有真正的输出行才会把标记放在行首。用 (?m)^ 精确匹配后者。
            _beginMarkerRegex = new Regex(
                @"(?m)^" + Regex.Escape(beginMarker),
                RegexOptions.Compiled);
            _endMarkerRegex = new Regex(
                @"(?m)^" + Regex.Escape(endMarkerPrefix) + @"(\d+)__",
                RegexOptions.Compiled);
        }

        /// <summary>
        /// 追加一段 stdout 或 stderr 到缓冲区。返回 true 表示已检测到结束标记。
        /// </summary>
        public bool Append(string text, bool isStderr)
        {
            lock (_lock)
            {
                // 已到终态——后续 cmd.exe 的 prompt 不应污染用户可见输出
                if (_closed)
                    return true;

                // stderr 始终捕获（不含标记）。注意 phase1 未完成时也捕获 stderr——
                // 但 Phase 1 的目的是扔掉 cmd.exe 欢迎横幅这些 stdout 噪音，stderr 通常没有这些噪音
                if (isStderr)
                {
                    _stderrBuf.Append(text);
                    return false;
                }

                _stdoutBuf.Append(text);

                // Phase 1: 丢弃所有"begin marker 之前"的 stdout
                if (!_phase1Done)
                {
                    var buf = _stdoutBuf.ToString();
                    var beginMatch = _beginMarkerRegex.Match(buf);
                    if (!beginMatch.Success)
                        return false;  // begin marker 还没出现在行首，继续等

                    // 砍掉：begin marker 之前的所有内容 + 标记本身 + 紧随的 \r\n
                    var cutFrom = beginMatch.Index + _beginMarker.Length;
                    while (cutFrom < buf.Length && (buf[cutFrom] == '\n' || buf[cutFrom] == '\r'))
                        cutFrom++;
                    _stdoutBuf.Remove(0, cutFrom);
                    _phase1Done = true;
                    // fall through —— end marker 可能已经在剩余 buffer 里
                }

                // Phase 2: 扫描 end marker 抓 exit code
                var haystack = _stdoutBuf.ToString();
                var endMatch = _endMarkerRegex.Match(haystack);
                if (!endMatch.Success)
                    return false;

                if (int.TryParse(endMatch.Groups[1].Value, out var code))
                    ExitCode = code;

                // 截断：保留 end marker 之前的真实输出
                var cutAt = endMatch.Index;
                while (cutAt > 0 && (_stdoutBuf[cutAt - 1] == '\n' || _stdoutBuf[cutAt - 1] == '\r'))
                    cutAt--;
                _stdoutBuf.Length = cutAt;

                _closed = true;
                _completed.Set();
                return true;
            }
        }

        /// <summary>阻塞等待命令结束标记出现，超时返回 false。</summary>
        public bool WaitForCompletion(int timeoutMs)
        {
            return _completed.Wait(timeoutMs);
        }

        public string Stdout
        {
            get { lock (_lock) return _stdoutBuf.ToString(); }
        }

        public string Stderr
        {
            get { lock (_lock) return _stderrBuf.ToString(); }
        }
    }
}
