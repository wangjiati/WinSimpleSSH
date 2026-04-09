using System;
using System.IO;
using System.Threading;

namespace SSHServer.Core
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public static class SLog
    {
        private static readonly object _lock = new object();
        private static string _logDir;
        private static string _logFile;
        private static LogLevel _minLevel = LogLevel.Info;
        private static long _maxFileSize = 10 * 1024 * 1024; // 10MB

        public static void Init(string logDir = null)
        {
            _logDir = logDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            _logFile = Path.Combine(_logDir, $"server_{DateTime.Now:yyyyMMdd}.log");

            // 确保目录存在
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);

            Info("日志系统已启动 / Logger initialized");
        }

        public static void SetLevel(LogLevel level)
        {
            _minLevel = level;
        }

        public static void Debug(string message)
        {
            Write(LogLevel.Debug, message);
        }

        public static void Info(string message)
        {
            Write(LogLevel.Info, message);
        }

        public static void Warn(string message)
        {
            Write(LogLevel.Warn, message);
        }

        public static void Error(string message)
        {
            Write(LogLevel.Error, message);
        }

        public static void Error(string message, Exception ex)
        {
            Write(LogLevel.Error, $"{message}: {ex.GetType().Name} - {ex.Message}");
        }

        public static void Write(LogLevel level, string message)
        {
            if (level < _minLevel) return;

            var timestamp = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
            var levelStr = level.ToString().ToUpper().PadRight(5);
            var line = $"{timestamp}|{levelStr}|{message}";

            // 控制台输出带颜色
            var oldColor = Console.ForegroundColor;
            switch (level)
            {
                case LogLevel.Warn:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
                case LogLevel.Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    break;
                case LogLevel.Debug:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    break;
                default:
                    Console.ForegroundColor = ConsoleColor.White;
                    break;
            }
            Console.WriteLine(line);
            Console.ForegroundColor = oldColor;

            // 写入文件（异步，不阻塞主线程）
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_lock)
                {
                    try
                    {
                        // 按日期滚动日志文件
                        var todayFile = Path.Combine(_logDir, $"server_{DateTime.Now:yyyyMMdd}.log");
                        if (todayFile != _logFile)
                            _logFile = todayFile;

                        // 检查文件大小，超过限制则截断
                        if (File.Exists(_logFile))
                        {
                            var fi = new FileInfo(_logFile);
                            if (fi.Length > _maxFileSize)
                            {
                                var bakFile = _logFile.Replace(".log", $".{DateTime.Now:HHmmss}.log");
                                File.Move(_logFile, bakFile);
                            }
                        }

                        File.AppendAllText(_logFile, line + Environment.NewLine);
                    }
                    catch { }
                }
            });
        }
    }
}
