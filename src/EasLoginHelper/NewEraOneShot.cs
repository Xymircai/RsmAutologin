// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Collections.Concurrent;

namespace EasLoginHelper;

public static class NewEraOneShot
{
    public static int Run(SettingsStore settings, bool submit)
    {
        using var activity = new InputActivity();
        var policy = new TargetPolicy(ModuleId.NewEra);
        var locator = new NewEraLocator(policy);
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        var exitCode = 1;
        var events = new ConcurrentQueue<string>();
        timer.Tick += async (_, _) =>
        {
            timer.Stop(); NewEraRunResult result;
            try
            {
                var windows = locator.Windows();
                var pages = await Task.Run(() => windows.Select(locator.Find).Where(p => p is not null).ToArray());
                if (pages.Length == 0 && windows.Any(w => NewEraLocator.ProcessKey(w) is { } key && locator.IsSignedIn(key)))
                    result = new(FillOutcome.Filled, LoginOutcome.Succeeded, false, 0, "新纪元当前已登录，无需重复提交。");
                else if (settings.Current.NewEra.DefaultAccount is not { } account)
                    result = new(FillOutcome.MissingCredential, LoginOutcome.NotSubmitted, false, 0, "请先为新纪元保存默认账号。");
                else if (pages.Length != 1)
                    result = new(FillOutcome.InvalidPage, LoginOutcome.NotSubmitted, false, 0, "未找到唯一的新纪元登录页。");
                else
                {
                    var page = pages[0]!; var gate = new NewEraAttemptGate(); gate.Observe(page.ProcessKey);
                    var controller = new NewEraController(policy, locator, activity, new CredentialVault(), events.Enqueue);
                    result = await controller.RunAsync(page, account, submit, gate, false, () => true, CancellationToken.None);
                }
            }
            catch (Exception e) when (NewEraLocator.IsAutomationError(e) || e is IOException or System.ComponentModel.Win32Exception)
            { result = new(FillOutcome.InvalidPage, LoginOutcome.NotSubmitted, false, 0, "新纪元本次操作未完成，请检查登录页。"); }
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "last-fill-new-era.json"), JsonSerializer.Serialize(new
            {
                CreatedUtc = DateTime.UtcNow, Module = "new-era", Outcome = result.FillOutcome.ToString(),
                result.Message, NoLoginSubmission = !result.Submitted, LoginOutcome = result.LoginOutcome.ToString(),
                LoggedIn = result.LoginOutcome == LoginOutcome.Succeeded, result.Submissions, result.UserNameWrites, result.PasswordWrites, CaptchaWrites = 0,
                GlobalInputRequests = Native.InputRequests, ForegroundRequests = Native.ActivationRequests, Events = events.ToArray()
            }, SettingsStore.JsonOptions));
            exitCode = submit ? result.LoginOutcome == LoginOutcome.Succeeded ? 0 : 1 : result.FillOutcome == FillOutcome.Filled ? 0 : 1;
            context.ExitThread();
        };
        timer.Start(); System.Windows.Forms.Application.Run(context); return exitCode;
    }
}
