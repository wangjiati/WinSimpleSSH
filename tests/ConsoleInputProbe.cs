using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

// Runs in an isolated, real Windows console so cursor bounds and ReadKey are
// exercised without sending keystrokes to the user's terminal.
public static class ConsoleInputProbe
{
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public short EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public short RepeatCount;
        [FieldOffset(10)] public short VirtualKey;
        [FieldOffset(14)] public char Character;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern bool FlushConsoleInputBuffer(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleInputW(IntPtr handle, InputRecord[] records, int count, out int written);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleOutputCharacterW(IntPtr handle, [Out] char[] text, int count, int position, out int read);

    private static readonly List<string> Results = new List<string>();
    private static MethodInfo _readLine;
    private static MethodInfo _redraw;
    private static object _shell;

    public static int Main(string[] args)
    {
        int exitCode = 0;
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new Exception("Probe requires its own real console.");
            var assembly = Assembly.LoadFrom(args[0]);
            var program = assembly.GetType("SSHClient.Program", true);
            _readLine = program.GetMethod("ReadLineWithHistory", BindingFlags.Static | BindingFlags.NonPublic);
            _redraw = program.GetMethod("RedrawLine", BindingFlags.Static | BindingFlags.NonPublic);
            var shellType = assembly.GetType("SSHClient.Core.RemoteShell", true);
            _shell = Activator.CreateInstance(shellType);
            var authenticated = new ManualResetEvent(false);
            shellType.GetMethod("SetSignalHandler").Invoke(_shell, new object[] {
                new Action<string>(signal => { if (signal == "AUTH_OK") authenticated.Set(); })
            });
            shellType.GetMethod("SetShellOutputHandler").Invoke(_shell, new object[] { new Action<string, bool>((text, error) => { }) });
            shellType.GetMethod("Connect").Invoke(_shell, new object[] { "127.0.0.1", int.Parse(args[1]), "input-test", "test-only" });
            if (!authenticated.WaitOne(10000)) throw new Exception("Probe authentication failed.");

            foreach (int width in new[] { 120, 80, 40 })
            {
                SetConsoleSize(width, width);
                Check("width " + width + ": typing across the right edge", () => {
                    var keys = Text(new string('a', width + 50));
                    keys.Add(Key(ConsoleKey.Enter));
                    Expect(Read(keys), new string('a', width + 50));
                });
                Check("width " + width + ": edit at the former wrap boundary", () => {
                    var keys = Text(new string('a', width - 5));
                    keys.Add(Key(ConsoleKey.LeftArrow));
                    keys.Add(Key(ConsoleKey.RightArrow));
                    keys.Add(Key(ConsoleKey.Backspace));
                    keys.AddRange(Text("Z"));
                    keys.Add(Key(ConsoleKey.Enter));
                    Expect(Read(keys), new string('a', width - 6) + "Z");
                });
                Check("width " + width + ": recall, edit and clear long history", () => {
                    var longLine = new string('h', width * 3);
                    var keys = new List<InputRecord> { Key(ConsoleKey.UpArrow), Key(ConsoleKey.LeftArrow) };
                    keys.AddRange(Text("X"));
                    keys.Add(Key(ConsoleKey.Enter));
                    Expect(Read(keys, new List<string> { longLine }), longLine.Substring(1) + "Xh");
                    Expect(Read(new List<InputRecord> { Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter) },
                        new List<string> { longLine }), "");
                });
                Check("width " + width + ": long-to-short redraw clears old text", () => {
                    Console.Clear();
                    _redraw.Invoke(null, new object[] { new string('x', width * 2), width * 2 });
                    _redraw.Invoke(null, new object[] { "ok", 2 });
                    string row = ReadRow();
                    if (Console.BufferWidth != width)
                        throw new Exception("Console width changed: window=" + Console.WindowWidth + ", buffer=" + Console.BufferWidth);
                    Expect(row, "ssh> ok" + new string(' ', width - 7));
                });
                Check("width " + width + ": redraw stays on the bottom row", () => {
                    Console.Clear();
                    Console.SetCursorPosition(0, Console.BufferHeight - 1);
                    _redraw.Invoke(null, new object[] { new string('b', width * 3), width * 3 });
                    if (Console.CursorTop != Console.BufferHeight - 1 || Console.CursorLeft >= width)
                        throw new Exception("Long input moved outside the current row.");
                });
                Check("width " + width + ": wide characters fit without wrapping", () => {
                    Console.Clear();
                    var text = new string('\u4e2d', width);
                    _redraw.Invoke(null, new object[] { text, text.Length });
                    if (Console.CursorTop != 0 || Console.CursorLeft >= width)
                        throw new Exception("Wide characters wrapped onto another row.");
                    if (!ReadRow().StartsWith("ssh> \u4e2d"))
                        throw new Exception("Wide characters were lost in the viewport.");
                    var keys = Text(text);
                    keys.Add(Key(ConsoleKey.LeftArrow));
                    keys.AddRange(Text("X"));
                    keys.Add(Key(ConsoleKey.Enter));
                    Expect(Read(keys), text.Substring(1) + "X\u4e2d");
                });
                Check("width " + width + ": cursor tracks displayed text", () => {
                    var text = new string('a', width) + new string('b', width) + "XYZ";
                    foreach (int cursor in new[] { 0, 1, width + 1, text.Length })
                    {
                        Console.Clear();
                        _redraw.Invoke(null, new object[] { text, cursor });
                        string row = ReadRow();
                        char expected = cursor == text.Length ? ' ' : text[cursor];
                        if (!row.StartsWith("ssh> ") || row[Console.CursorLeft] != expected)
                            throw new Exception("Cursor does not point to the expected visible character.");
                    }
                });
                Check("width " + width + ": mixed wide text has the correct cursor", () => {
                    Console.Clear();
                    _redraw.Invoke(null, new object[] { "\u4e2dA\u6587B", 2 });
                    string row = ReadRow();
                    if (Console.CursorLeft != 8 || !row.StartsWith("ssh> \u4e2d") || !row.Contains("A") || !row.Contains("\u6587") || !row.Contains("B"))
                        throw new Exception("Mixed wide text or cursor position changed.");
                });
            }
            SetConsoleSize(80, 160);
            Check("window narrower than buffer: redraw fits the visible row", () => {
                Console.Clear();
                _redraw.Invoke(null, new object[] { new string('w', 300), 300 });
                if (Console.CursorTop != 0 || Console.CursorLeft >= 80)
                    throw new Exception("Cursor left the visible row.");
            });
            if (Results.Exists(result => result.StartsWith("FAIL:"))) exitCode = 1;
        }
        catch (Exception ex)
        {
            Results.Add("FAIL: " + Unwrap(ex));
            exitCode = 1;
        }
        finally
        {
            if (_shell != null)
                _shell.GetType().GetMethod("Disconnect").Invoke(_shell, null);
            File.WriteAllLines(args[2], Results.ToArray());
        }
        return exitCode;
    }

    private static void SetConsoleSize(int windowWidth, int bufferWidth)
    {
        Console.SetWindowSize(Math.Min(windowWidth, Console.WindowWidth), Math.Min(20, Console.WindowHeight));
        Console.SetBufferSize(bufferWidth, 60);
        Console.SetWindowSize(windowWidth, 20);
        Console.Clear();
    }

    private static void Check(string name, Action test)
    {
        try { test(); Results.Add("PASS: " + name); }
        catch (Exception ex) { Results.Add("FAIL: " + name + ": " + Unwrap(ex)); }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        return ex;
    }

    private static string Read(List<InputRecord> keys, List<string> history = null)
    {
        Console.Clear();
        Console.Write("ssh> ");
        if (!FlushConsoleInputBuffer(GetStdHandle(-10))) throw new Exception("Could not clear console input.");
        int written;
        if (!WriteConsoleInputW(GetStdHandle(-10), keys.ToArray(), keys.Count, out written) || written != keys.Count)
            throw new Exception("Could not inject console keys: " + Marshal.GetLastWin32Error());
        return (string)_readLine.Invoke(null, new object[] { history ?? new List<string>(), -1, _shell });
    }

    private static List<InputRecord> Text(string text)
    {
        var keys = new List<InputRecord>();
        foreach (char c in text) keys.Add(new InputRecord { EventType = 1, KeyDown = 1, RepeatCount = 1, Character = c });
        return keys;
    }

    private static InputRecord Key(ConsoleKey key)
    {
        return new InputRecord { EventType = 1, KeyDown = 1, RepeatCount = 1, VirtualKey = (short)key,
            Character = key == ConsoleKey.Enter ? '\r' : '\0' };
    }

    private static string ReadRow()
    {
        var text = new char[Console.BufferWidth];
        int read;
        int position = Console.CursorTop << 16;
        if (!ReadConsoleOutputCharacterW(GetStdHandle(-11), text, Console.BufferWidth, position, out read))
            throw new Exception("Could not read console output.");
        return new string(text, 0, read);
    }

    private static void Expect(string actual, string expected)
    {
        if (actual != expected) throw new Exception("Expected " + expected.Length + " characters, got " + (actual == null ? "null" : actual.Length.ToString()) + ".");
    }
}
