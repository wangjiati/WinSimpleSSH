using System;
using System.Text;

namespace SSHClient.Core
{
    /// <summary>
    /// 非交互模式的控制台输出编码作用域：进入时把控制台切到 UTF-8，退出时恢复原值。
    ///
    /// 设置 Console.OutputEncoding = UTF-8 底层会调用 SetConsoleOutputCP(65001)，
    /// 改动的是与父 cmd.exe 共享的控制台代码页，且该改动在进程退出后仍然生效。
    /// 父 cmd.exe 按读取每一行时控制台的输出代码页解码批处理内容——若不恢复，
    /// 同一窗口里 .bat 的后续行会被按 UTF-8 解码 GBK 字节（或反之），中文参数全部乱码。
    /// 因此所有退出路径（正常返回、异常、Environment.Exit）都必须调用 Restore()。
    /// </summary>
    static class ConsoleEncodingScope
    {
        private static Encoding _previous;
        private static bool _entered;

        /// <summary>
        /// 记录当前输出编码并把控制台切到 UTF-8。重复调用安全。
        /// 返回 true 表示本次调用真正进入了作用域，false 表示已进入过。
        /// </summary>
        public static bool Enter()
        {
            if (_entered) return false;
            _entered = true;

            try { _previous = Console.OutputEncoding; } catch { _previous = null; }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            // 兜底：Environment.Exit（如 Ctrl+C 看门狗硬退）不会执行调用方的 finally，
            // 而 ProcessExit 事件在正常退出和 Environment.Exit 时都会触发。
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Restore();

            return true;
        }

        /// <summary>恢复 Enter() 之前的输出编码。未 Enter、恢复失败或无控制台时静默 no-op。</summary>
        public static void Restore()
        {
            if (!_entered) return;
            _entered = false;

            var prev = _previous;
            _previous = null;
            if (prev == null) return;

            try
            {
                // 先冲刷，避免替换 Console.Out writer 时丢掉未写完的缓冲
                Console.Out.Flush();
                Console.Error.Flush();
                Console.OutputEncoding = prev;
            }
            catch { }
        }
    }
}
