// SPDX-License-Identifier: MPL-2.0
using System.Security.Principal;

namespace EasLoginHelper;

internal static class StartupTask
{
    internal const string Argument = "--startup";
    private const int TaskLogonTrigger = 9, TaskExecAction = 0;
    private const int TaskCreateOrUpdate = 6, TaskInteractiveToken = 3, TaskRunHighest = 1;
    private const int FileNotFound = unchecked((int)0x80070002);

    internal static bool IsEnabled()
    {
        var userId = CurrentUserId();
        dynamic scheduler = Connect();
        return ReadEnabled(scheduler.GetFolder("\\"), Name(userId));
    }

    internal static bool Toggle()
    {
        var userId = CurrentUserId();
        var name = Name(userId);
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder("\\");
        if (ReadEnabled(folder, name))
        {
            folder.DeleteTask(name, 0);
            return false;
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定助手的 EXE 路径。");
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "当前 Windows 用户登录后，在托盘启动审计软件自动登录助手。";
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

        // The interactive token uses this account's Windows session and credential vault.
        dynamic registered = folder.RegisterTaskDefinition(name, task, TaskCreateOrUpdate, userId, null, TaskInteractiveToken, null);
        return (bool)registered.Enabled;
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

    private static bool ReadEnabled(dynamic folder, string name)
    {
        try { return (bool)folder.GetTask(name).Enabled; }
        catch (Exception e) when (e.HResult == FileNotFound) { return false; }
    }
}
