// SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Text.Json;

namespace EasLoginHelper;

public static class OneShotFill
{
    public static string ReportPath => Path.Combine(SettingsStore.DataDirectory, "last-fill.json");
    public static void WriteBusy(ModuleId module = ModuleId.Eas)
    {
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var path = module == ModuleId.Eas ? ReportPath : Path.Combine(SettingsStore.DataDirectory, "last-fill-new-era.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        { CreatedUtc = DateTime.UtcNow, Module = Modules.Key(module), Outcome = "Busy", Message = "助手已经运行，请使用托盘中的立即填充。", Events = Array.Empty<string>(), NoLoginSubmission = true }, SettingsStore.JsonOptions));
    }

    public static int Run(SettingsStore settings, bool loginAfterFill = false)
    {
        var events = new ConcurrentQueue<string>(); var episode = new EasLoginEpisode(); var exit = 1;
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        using var activity = new InputActivity(); using var ocr = new CaptchaRecognizer();
        var policy = new TargetPolicy(ModuleId.Eas); var locator = new LoginLocator(policy, () => settings.Current.Calibration);
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                var windows = new List<IntPtr>();
                Native.EnumWindows((window, _) => { if (policy.Allows(window) && Native.IsWindowVisible(window) && !Native.IsIconic(window)) windows.Add(window); return true; }, IntPtr.Zero);
                var pages = await Task.Run(() => windows.Select(locator.Find).OfType<LoginPage>().ToArray());
                for (var retry = 0; pages.Length == 0 && retry < 8 && !windows.Any(AutoLoginController.HasSignedInMarker); retry++)
                {
                    await Task.Delay(250);
                    windows.Clear();
                    Native.EnumWindows((window, _) => { if (policy.Allows(window) && Native.IsWindowVisible(window) && !Native.IsIconic(window)) windows.Add(window); return true; }, IntPtr.Zero);
                    pages = await Task.Run(() => windows.Select(locator.Find).OfType<LoginPage>().ToArray());
                }
                if (loginAfterFill && pages.Length == 0 && await Task.Run(() => windows.Any(AutoLoginController.HasSignedInMarker)))
                    episode.Login = new(LoginOutcome.Succeeded, false, "当前已登录，无需重复提交。");
                else if (settings.Current.DefaultAccount is not { } account) episode.Fill = new(FillOutcome.MissingCredential, "未设置 EAS 默认账号。");
                else if (pages.Length != 1) episode.Fill = new(FillOutcome.InvalidPage, "未找到唯一且未最小化的 EAS 或审计工具箱登录页。");
                else
                {
                    var page = pages[0]; var gate = new LoginAttemptGate(); gate.ObserveLoginPage(page.ProcessKey);
                    var transaction = new EasLoginTransaction(policy, locator, activity, new CredentialVault(), ocr, WindowCaptureSource.Shared, events.Enqueue);
                    await transaction.RunAsync(page, account, episode, loginAfterFill && settings.Current.OcrEnabled, loginAfterFill, gate, () => true, CancellationToken.None);
                }
            }
            catch (Exception e) when (NewEraLocator.IsAutomationError(e) || e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
            { events.Enqueue("Stopped." + e.GetType().Name); episode.Fill = new(FillOutcome.InvalidPage, "后台操作未能完成，未继续提交。"); }
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(new
            {
                FormatVersion = 4, CreatedUtc = DateTime.UtcNow, Module = "eas", Application = episode.ApplicationName, Outcome = episode.Fill?.Outcome.ToString(),
                Message = episode.Login?.Message ?? episode.Fill?.Message, UserNameMatches = episode.UserNameConfirmed,
                Events = events.ToArray(), NoLoginSubmission = episode.Counts.Submissions == 0, LoginOutcome = episode.Login?.Outcome.ToString(),
                LoggedIn = episode.Login?.Outcome == LoginOutcome.Succeeded, UserNameWrites = episode.Counts.UserName,
                PasswordWrites = episode.Counts.Password, CaptchaWrites = episode.Counts.Captcha, episode.Counts.Submissions,
                GlobalInputRequests = Native.InputRequests, ForegroundRequests = Native.ActivationRequests
            }, SettingsStore.JsonOptions));
            exit = loginAfterFill ? episode.Login?.Outcome == LoginOutcome.Succeeded ? 0 : 1 : episode.Fill?.Outcome == FillOutcome.Filled ? 0 : 1;
            context.ExitThread();
        };
        timer.Start(); System.Windows.Forms.Application.Run(context); return exit;
    }
}
