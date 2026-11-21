// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EasLoginHelper;

internal static class WebsiteProcessIdentity
{
    internal static SecurityIdentifier DesktopUser()
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero || Native.GetWindowThreadProcessId(shell, out var pid) == 0)
            throw new IOException("找不到当前 Windows 桌面。");
        using var process = OpenProcess(0x1000, false, pid);
        return ProcessUser(process);
    }

    internal static bool IsCaller(uint pid, SecurityIdentifier user)
    {
        if (!InCurrentSession(pid)) return false;
        using var process = OpenProcess(0x1000, false, pid);
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, path, ref length) ||
            !string.Equals(path.ToString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return false;
        return ProcessUser(process) == user;
    }

    internal static bool InCurrentSession(uint pid) => ProcessIdToSessionId(pid, out var session) &&
        ProcessIdToSessionId((uint)Environment.ProcessId, out var current) && session == current;

    private static SecurityIdentifier ProcessUser(SafeProcessHandle process)
    {
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User ?? throw new IOException("无法确定网站进程的 Windows 用户。");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
