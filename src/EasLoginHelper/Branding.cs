// SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EasLoginHelper;

public static class Branding
{
    private static readonly ConcurrentDictionary<int, Icon> Icons = new();
    public static string FileVersion => typeof(Branding).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    public static string Version => typeof(Branding).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static Icon IconForSize(int size) => Icons.GetOrAdd(Math.Clamp(size, 16, 256), requested =>
    {
        var bytes = EmbeddedAssets.Read("EasLoginHelper.Branding.Icon");
        var count = BitConverter.ToUInt16(bytes, 4);
        var frames = Enumerable.Range(0, count).Select(index => 6 + index * 16)
            .OrderBy(entry => Math.Abs((bytes[entry] == 0 ? 256 : bytes[entry]) - requested)).ToArray();
        if (frames.Length == 0) throw new InvalidDataException("应用图标资源缺失。");
        var entry = frames[0]; var length = checked((int)BitConverter.ToUInt32(bytes, entry + 8));
        var offset = checked((int)BitConverter.ToUInt32(bytes, entry + 12));
        var image = bytes.AsSpan(offset, length).ToArray();
        // GDI+'s ICO directory loader can skip the 256px (zero-width directory entry) frame.
        // Loading the selected PNG resource explicitly preserves every resolution and alpha edge.
        var handle = CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, requested, requested, 0);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载应用图标。");
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    });
    public static Icon WindowIcon(int dpi) => IconForSize((int)Math.Round(32 * dpi / 96d));
    public static Icon TrayIcon => IconForSize(SystemInformation.SmallIconSize.Width);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint length, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
