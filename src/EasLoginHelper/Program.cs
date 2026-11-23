// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using FormsApplication = System.Windows.Forms.Application;

namespace EasLoginHelper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
            return WebsiteBrowserBridge.Run(args[0]);
        if (args is [WebsiteWorker.Argument, var token, var parent, var user])
            return WebsiteWorker.RunAsync(token, parent, user).GetAwaiter().GetResult();

        FormsApplication.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        FormsApplication.EnableVisualStyles();
        FormsApplication.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args is [WebsiteSelfTest.Argument]) return Diagnostics.IsElevated() ? 1 : WebsiteSelfTest.Run();

            var module = ModuleId.Eas;
            var startInTray = args is [StartupTask.Argument];
            var startOnWebsites = args is ["--websites"];
            var operations = startInTray || startOnWebsites ? new List<string>() : args.ToList();
            var moduleIndex = operations.IndexOf("--module");
            if (moduleIndex >= 0)
            {
                if (moduleIndex + 1 >= operations.Count) throw new ArgumentException("--module 需要 eas 或 new-era。");
                module = operations[moduleIndex + 1] switch { "eas" => ModuleId.Eas, "new-era" => ModuleId.NewEra, _ => throw new ArgumentException("模块应为 eas 或 new-era。") };
                operations.RemoveRange(moduleIndex, 2);
            }
            if (operations.Count > 1 || operations.Count == 1 && operations[0] is not ("--diagnose" or "--fill-once" or "--login-once" or "--copy-eas-default" or "--capture-selftest" or "--selftest"))
                throw new ArgumentException("支持 --diagnose、--fill-once、--login-once、--copy-eas-default、--selftest，及 --module eas|new-era。");
            if (operations.Contains("--selftest")) return EnvironmentSelfTest.Run();
            if (operations.Contains("--capture-selftest")) return CaptureSelfTest.Run();
            if (!Diagnostics.IsElevated())
            {
                if (startInTray) StartupTask.Log("自启实例未获得管理员权限，已退出；请检查计划任务的 RunLevel=Highest。");
                return startInTray ? 1 : StartElevated(args, waitForResult: operations.Count == 1);
            }
            // 任何手动运行（包括单实例冲突、--websites）都把开机自启路径刷新为当前 EXE；
            // 开机自启实例本身跳过，避免每次登录都重写任务定义。
            if (!startInTray) StartupTask.SyncCurrentPath();

            var settings = new SettingsStore(persistMigration: !operations.Contains("--diagnose"));
            if (operations.Contains("--diagnose")) { Diagnostics.Write(settings, module); return 0; }
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var showEventName = "Local\\EasLoginHelper_Show_" + sid;
            var websiteEventName = "Local\\EasLoginHelper_ShowWebsite_" + sid;
            using var instance = new Mutex(true, "Local\\EasLoginHelper_" + sid, out var created);
            if (!created)
            {
                if (startInTray) return 0;
                if (operations.Count == 1) { OneShotFill.WriteBusy(module); return 1; }
                try { using var request = EventWaitHandle.OpenExisting(startOnWebsites ? websiteEventName : showEventName); request.Set(); }
                catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("助手已在运行，请在任务栏右下角的托盘中打开账号管理。", "审计软件自动登录助手", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                return 0;
            }
            if (operations.Contains("--copy-eas-default"))
            {
                if (module != ModuleId.NewEra) throw new ArgumentException("复制操作需要指定 --module new-era。");
                if (settings.Current.NewEra.Accounts.Count != 0) throw new InvalidOperationException("新纪元已保存账号，请在主窗口中选择要复制的账号。");
                var source = settings.Current.DefaultAccount ?? throw new InvalidOperationException("EAS 尚未设置默认账号。");
                new AccountService(settings, new CredentialVault(), ModuleId.NewEra).CopyFrom(ModuleId.Eas, source.Id);
                return 0;
            }
            if (operations.Count == 1 && module == ModuleId.NewEra) return NewEraOneShot.Run(settings, operations[0] == "--login-once");
            if (operations.Contains("--fill-once")) return OneShotFill.Run(settings);
            if (operations.Contains("--login-once")) return OneShotFill.Run(settings, true);
            FormsApplication.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            FormsApplication.ThreadException += (_, _) => MessageBox.Show("本次操作未完成。请暂停助手，重新打开登录页后再试。", "EAS 登录填充助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName);
            using var websiteRequest = new EventWaitHandle(false, EventResetMode.AutoReset, websiteEventName);
            using var context = new TrayContext(settings, showRequest, startInTray, websiteRequest, startOnWebsites);
            FormsApplication.Run(context);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(e.Message, "审计软件自动登录助手未能启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static int StartElevated(string[] args, bool waitForResult)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定助手的 EXE 路径。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (!waitForResult) return 0;
            if (process is null) return 1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return 1;
        }
    }
}
