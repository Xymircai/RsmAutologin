// SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;

namespace EasLoginHelper;

internal static class ExplorerDesktopLaunch
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid DispatchInterface = new("00020400-0000-0000-C000-000000000046");

    internal static void Open(string file, string arguments)
    {
        // Acquire the desktop view's shell dispatch in Explorer's process, so the
        // new instance uses the interactive user's account and privilege level.
        var classType = Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!;
        var windows = (IShellWindows)Activator.CreateInstance(classType)!;
        object desktopLocation = 0; // CSIDL_DESKTOP
        object? rootLocation = null;
        const int shellDesktop = 8, needDispatch = 1, showNormal = 1;
        const uint background = 0;
        var desktop = (IShellServiceProvider?)windows.FindWindowSW(ref desktopLocation, ref rootLocation,
            shellDesktop, out _, needDispatch)
            ?? throw new InvalidOperationException("没有找到当前 Windows 桌面。");
        var service = TopLevelBrowserService;
        var browserInterface = typeof(IShellBrowser).GUID;
        var browser = (IShellBrowser)desktop.QueryService(ref service, ref browserInterface);
        var dispatchInterface = DispatchInterface;
        var desktopView = (IShellFolderView)browser.QueryActiveShellView().GetItemObject(background, ref dispatchInterface);
        var shell = (IShellDispatch)desktopView.Application;
        shell.ShellExecute(file, arguments, Path.GetDirectoryName(file)!, "open", showNormal);
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object? root, int windowClass, out int windowHandle, int flags);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryService(ref Guid service, ref Guid interfaceId);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _VtblGap1_12();
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void _VtblGap1_12();
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint aspect, ref Guid interfaceId);
    }

    [ComImport, Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolderView
    {
        object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    }

    [ComImport, Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch
    {
        void ShellExecute([MarshalAs(UnmanagedType.BStr)] string file, [MarshalAs(UnmanagedType.Struct)] object arguments,
            [MarshalAs(UnmanagedType.Struct)] object directory, [MarshalAs(UnmanagedType.Struct)] object verb,
            [MarshalAs(UnmanagedType.Struct)] object show);
    }
}
