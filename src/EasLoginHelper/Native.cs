// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;

namespace EasLoginHelper;

internal static class Native
{
    public const uint KeyUp = 2, Unicode = 4;
    public const int GaRoot = 2;
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct LastInput { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    internal delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindowCore(IntPtr window);
    private static long _inputRequests, _activationRequests;
    internal static long InputRequests => Interlocked.Read(ref _inputRequests);
    internal static long ActivationRequests => Interlocked.Read(ref _activationRequests);
    internal static bool SetForegroundWindow(IntPtr window) { Interlocked.Increment(ref _activationRequests); return SetForegroundWindowCore(window); }
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? module);

    public static string? ProcessPath(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var id);
        var process = OpenProcess(0x1000, false, id);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(2048); uint size = 2048;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? Path.GetFullPath(buffer.ToString()) : null;
        }
        finally { CloseHandle(process); }
    }
    public static string? ProcessIdentity(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var id);
        try { using var process = Process.GetProcessById((int)id); return id + ":" + process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    public static Rectangle ClientBounds(IntPtr window)
    {
        if (!GetClientRect(window, out var rect)) return Rectangle.Empty;
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) return Rectangle.Empty;
        return new Rectangle(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static string ClassName(IntPtr window) { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }
    public static string Caption(IntPtr window) { var text = new StringBuilder(2048); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    public static Rectangle WindowBounds(IntPtr window) => GetWindowRect(window, out var rect) ? rect.ToRectangle() : Rectangle.Empty;
    public static bool IsReadOnlyEdit(IntPtr window) => (GetWindowLong(window, -16) & 0x800) != 0;
    public static bool IsPasswordEdit(IntPtr window) => (GetWindowLong(window, -16) & 0x20) != 0 ||
        SendMessageTimeout(window, 0x00D2, IntPtr.Zero, IntPtr.Zero, 2, 200, out var character) != IntPtr.Zero && character != UIntPtr.Zero;
    public static bool SetBoundText(IntPtr window, ReadOnlySpan<char> value)
    {
        var memory = Marshal.AllocHGlobal((value.Length + 1) * 2);
        try
        {
            for (var i = 0; i < value.Length; i++) Marshal.WriteInt16(memory, i * 2, value[i]);
            Marshal.WriteInt16(memory, value.Length * 2, 0);
            return SendMessageTimeout(window, 0x000C, IntPtr.Zero, memory, 2, 700, out _) != IntPtr.Zero;
        }
        finally
        {
            for (var i = 0; i <= value.Length; i++) Marshal.WriteInt16(memory, i * 2, 0);
            Marshal.FreeHGlobal(memory);
        }
    }
    public static string? ReadBoundUserName(IntPtr window)
    {
        if (IsPasswordEdit(window)) return null;
        var memory = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.WriteInt16(memory, 0, 0);
            return SendMessageTimeout(window, 0x000D, new IntPtr(2048), memory, 2, 700, out _) == IntPtr.Zero
                ? null : Marshal.PtrToStringUni(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public static List<IntPtr> Children(IntPtr window)
    {
        var children = new List<IntPtr>();
        EnumChildWindows(window, (child, _) => { if (children.Count < 500) children.Add(child); return children.Count < 500; }, IntPtr.Zero);
        return children;
    }

    public static bool OwnsPoint(IntPtr window, System.Drawing.Point point) =>
        GetAncestor(WindowFromPoint(new Point(point.X, point.Y)), GaRoot) == window;

    public static bool IsUnobscured(IntPtr window, Rectangle rect)
    {
        if (rect.Width < 2 || rect.Height < 2) return false;
        return new[] { new System.Drawing.Point(rect.Left + 1, rect.Top + 1), new(rect.Right - 2, rect.Top + 1),
            new(rect.Left + 1, rect.Bottom - 2), new(rect.Right - 2, rect.Bottom - 2), new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2) }
            .All(p => OwnsPoint(window, p));
    }

    public static bool IsIdle(int milliseconds = 700)
    {
        var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref last) && unchecked((uint)Environment.TickCount - last.Time) >= milliseconds &&
            new[] { 0x10, 0x11, 0x12, 0x01, 0x02 }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    }

    public static void Send(params Input[] inputs)
    {
        Interlocked.Increment(ref _inputRequests);
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 阻止了输入，请确认助手与 EAS 具有相同权限。");
    }
    public static Input Key(ushort key, uint flags = 0) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags } } };
    public static void ClearField() => Send(Key(0x11), Key(0x41), Key(0x41, KeyUp), Key(0x11, KeyUp));
    public static void SendCharacter(char character) => Send(
        new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = Unicode } } },
        new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = Unicode | KeyUp } } });
    public static void Click(System.Drawing.Point point)
    {
        var desktop = SystemInformation.VirtualScreen;
        var x = (int)Math.Round((point.X - desktop.Left) * 65535d / Math.Max(1, desktop.Width - 1));
        var y = (int)Math.Round((point.Y - desktop.Top) * 65535d / Math.Max(1, desktop.Height - 1));
        Send(new Input { Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = 0xC001 } } },
            new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 2 } } },
            new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } });
    }
}

// Hooks retain only an activity counter; no key code or text is collected.
public sealed class InputActivity : IDisposable
{
    private readonly Native.HookProc _keyboard, _mouse;
    private readonly IntPtr _keyboardHook, _mouseHook;
    private long _generation;
    private long _keyboardGeneration;
    private long _lastKeyboardWindow, _lastKeyboardAt;
    private sealed class KeyCounter { public long Value; }
    private readonly ConcurrentDictionary<long, KeyCounter> _windowKeys = new();
    public long Generation => Interlocked.Read(ref _generation);
    public long KeyboardGeneration => Interlocked.Read(ref _keyboardGeneration);
    public long KeyboardGenerationFor(IntPtr window)
    {
        return Interlocked.Read(ref _windowKeys.GetOrAdd(window.ToInt64(), _ => new KeyCounter()).Value);
    }
    internal void RecordPhysicalKey(IntPtr window)
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _keyboardGeneration);
        Interlocked.Increment(ref _windowKeys.GetOrAdd(window.ToInt64(), _ => new KeyCounter()).Value);
        Interlocked.Exchange(ref _lastKeyboardWindow, window.ToInt64());
        Interlocked.Exchange(ref _lastKeyboardAt, Environment.TickCount64);
    }
    public bool RecentlyTypedIn(IntPtr window) => Interlocked.Read(ref _lastKeyboardWindow) == window.ToInt64() &&
        Environment.TickCount64 - Interlocked.Read(ref _lastKeyboardAt) < 10000;
    public InputActivity()
    {
        _keyboard = (code, w, l) =>
        {
            if (code >= 0 && (Marshal.ReadInt32(l, 8) & 0x10) == 0)
            {
                RecordPhysicalKey(Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot));
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, w, l);
        };
        _mouse = (code, w, l) =>
        {
            if (code >= 0 && (Marshal.ReadInt32(l, 12) & 1) == 0) Interlocked.Increment(ref _generation);
            return Native.CallNextHookEx(IntPtr.Zero, code, w, l);
        };
        _keyboardHook = Native.SetWindowsHookEx(13, _keyboard, Native.GetModuleHandle(null), 0);
        _mouseHook = Native.SetWindowsHookEx(14, _mouse, Native.GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用输入中断保护。");
        }
    }
    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
    }
}

public sealed class TargetPolicy
{
    private static readonly string[] EasNames = ["EAS.Updater.exe", "EAS.exe"];
    private static readonly string[] NewEraNames = ["Xsjzb.exe"];
    public ModuleId Module { get; }
    public TargetPolicy(ModuleId module = ModuleId.Eas)
    {
        if (!Enum.IsDefined(module)) throw new ArgumentOutOfRangeException(nameof(module));
        Module = module;
    }
    public bool Allows(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        (Module == ModuleId.Eas ? EasNames : NewEraNames).Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    public bool Allows(IntPtr window) => window != IntPtr.Zero && Allows(Native.ProcessPath(window));
    public static string Version(string path) => FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
}