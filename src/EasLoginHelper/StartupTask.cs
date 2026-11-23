// SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace EasLoginHelper;

internal enum StartupState { Disabled, Enabled, Stale, Unknown }

internal static class StartupTask
{
    internal const string Argument = "--startup";
    private const int TaskLogonTrigger = 9, TaskExecAction = 0;
    private const int TaskCreateOrUpdate = 6, TaskInteractiveToken = 3, TaskRunHighest = 1;
    private const int FileNotFound = unchecked((int)0x80070002);

    private static string Executable =>
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定助手的 EXE 路径。");

    /// <summary>任务是否存在、是否真正指向当前正在运行的这个 EXE。</summary>
    internal static StartupState GetState()
    {
        try
        {
            object folder = Connect().GetFolder("\\");
            if (!TryRead(folder, Name(CurrentUserId()), out var enabled, out var path, out var arguments))
                return StartupState.Disabled;
            if (!enabled) return StartupState.Disabled;
            var current = Normalize(Executable);
            if (current is null) return StartupState.Unknown;
            if (path is null)
            {
                // 读不到任务动作（如 COM 绑定差异）时，回退到上次成功注册时记下的路径，
                // 避免开关一直显示「需更新」。
                var marker = Normalize(ReadMarker());
                return marker is not null && string.Equals(marker, current, StringComparison.OrdinalIgnoreCase)
                    ? StartupState.Enabled : StartupState.Stale;
            }
            var recorded = Normalize(path);
            if (recorded is null || !File.Exists(recorded) ||
                !string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals((arguments ?? string.Empty).Trim(), Argument, StringComparison.OrdinalIgnoreCase))
                return StartupState.Stale;
            return StartupState.Enabled;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or IOException)
        { return StartupState.Unknown; }
    }

    /// <summary>开 → 关；关或路径过期 → 以当前 EXE 路径注册并保持在开。</summary>
    internal static StartupState Toggle()
    {
        if (GetState() == StartupState.Enabled)
        {
            dynamic folder = Connect().GetFolder("\\");
            folder.DeleteTask(Name(CurrentUserId()), 0);
            DeleteMarker();
            return StartupState.Disabled;
        }
        Register();
        return StartupState.Enabled;
    }

    /// <summary>
    /// 每次手动运行都调用：任务已启用但指向别的（或已不存在的）EXE 时，
    /// 把启动路径改写成当前进程路径。开机自启实例跳过，避免每次登录都重写任务。
    /// </summary>
    internal static void SyncCurrentPath()
    {
        try
        {
            if (GetState() != StartupState.Stale) return;
            Register();
            Log("已将开机自启路径更新为当前 EXE。");
        }
        catch (Exception e) { Log("同步开机自启路径失败：" + e.Message); }
    }

    internal static void Log(string message)
    {
        try
        {
            var directory = SettingsStore.DataDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Environment.ProcessPath}  {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Register()
    {
        var userId = CurrentUserId();
        var executable = Executable;
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder("\\");
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "当前 Windows 用户登录后，在托盘启动审计软件自动登录助手。EXE 路径由助手每次运行时自动同步。";
        task.Principal.UserId = userId;
        task.Principal.LogonType = TaskInteractiveToken;
        task.Principal.RunLevel = TaskRunHighest;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        dynamic trigger = task.Triggers.Create(TaskLogonTrigger);
        trigger.UserId = userId;
        dynamic action = task.Actions.Create(TaskExecAction);
        action.Path = executable;
        action.Arguments = Argument;
        action.WorkingDirectory = Path.GetDirectoryName(executable)!;
        // CREATE_OR_UPDATE：任务已存在时直接覆盖，无需先删除，也不会影响正在运行的实例。
        folder.RegisterTaskDefinition(Name(userId), task, TaskCreateOrUpdate, userId, null, TaskInteractiveToken, null);
        WriteMarker(executable);
    }

    private static string MarkerPath => Path.Combine(SettingsStore.DataDirectory, "startup-path.txt");

    private static string? ReadMarker()
    {
        try { return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteMarker(string path)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(MarkerPath, path, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool TryRead(object folder, string name, out bool enabled, out string? path, out string? arguments)
    {
        enabled = false; path = null; arguments = null;
        dynamic target = folder;
        dynamic registered;
        try { registered = target.GetTask(name); }
        catch (Exception e) when (e.HResult == FileNotFound) { return false; }
        enabled = (bool)registered.Enabled;
        try
        {
            dynamic actions = registered.Definition.Actions;
            if ((int)actions.Count < 1) return true;
            dynamic action = actions.Item(1); // Task Scheduler 的 COM 集合从 1 开始
            path = (string)action.Path;
            arguments = (string)action.Arguments;
        }
        catch (Exception) { path = null; arguments = null; } // 读不清动作就当过期，交给 Register 修复
        return true;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value.Trim().Trim('"').Trim()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        { return null; }
    }

    private static string CurrentUserId()
    {
        using var user = WindowsIdentity.GetCurrent();
        return user.User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 账户。");
    }

    private static string Name(string userId) => "RsmAutologin-Logon-" + userId;

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }
}
