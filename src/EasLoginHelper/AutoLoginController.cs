// SPDX-License-Identifier: MPL-2.0
using System.Windows.Automation;

namespace EasLoginHelper;

public enum LoginOutcome { NotSubmitted, Succeeded, Rejected, Pending, Interrupted }
public sealed record LoginResult(LoginOutcome Outcome, bool Submitted, string Message);

public sealed class LoginAttemptGate
{
    private readonly object _sync = new();
    private string? _identity;
    private bool _submitted, _succeeded;
    public bool ObserveLoginPage(string identity)
    {
        lock (_sync)
        {
            if (_submitted && !_succeeded) return false;
            if (_identity == identity && !_succeeded) return false;
            _identity = identity; _submitted = false; _succeeded = false;
            return true;
        }
    }
    public bool TryReserve(string identity)
    {
        lock (_sync)
        {
            if (_identity != identity || _submitted) return false;
            _submitted = true; return true;
        }
    }
    public bool HasSubmitted(string identity) { lock (_sync) return _submitted && (_identity == identity || !_succeeded); }
    public void MarkSucceeded(string identity) { lock (_sync) { if (_identity == identity) _succeeded = true; } }
    public void ResetExplicitly() { lock (_sync) { _identity = null; _submitted = false; _succeeded = false; } }
}

public static class LoginReadiness
{
    public static bool CanAutoSubmit(bool enabled, bool credentialsReady, FillResult fill) => enabled && credentialsReady &&
        fill.Outcome == FillOutcome.Filled && fill.CaptchaFilled && !string.IsNullOrWhiteSpace(fill.CaptchaHash) &&
        fill.CaptchaValue is { Length: 4 } value && value.All(char.IsAsciiDigit) && fill.InputGeneration is not null;
}

public sealed class AutoLoginController(TargetPolicy policy, LoginLocator locator, InputActivity activity,
    Action<string>? trace = null, TimeSpan? resultTimeout = null)
{
    public Task<LoginResult> SubmitAsync(LoginPage expected, AccountProfile account, string captchaValue,
        string? captchaHash, long generation, bool requireForeground, Func<bool> reserveAttempt,
        CancellationToken cancellation) => SubmitCoreAsync(expected, account, captchaValue, captchaHash,
            generation, requireForeground, reserveAttempt, cancellation);

    // This local worker owns all UIA calls; the tray stays responsive while EAS authenticates.
    private async Task<LoginResult> SubmitCoreAsync(LoginPage expected, AccountProfile account, string captchaValue,
        string? captchaHash, long generation, bool requireForeground, Func<bool> reserveAttempt,
        CancellationToken cancellation)
    {
        var submitted = 0;
        // Kept for source compatibility. Desktop activity and focus no longer gate bound submissions.
        _ = activity; _ = generation; _ = requireForeground; _ = captchaHash;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(resultTimeout ?? TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        try
        {
            return await Task.Run(async () =>
            {
                CheckBeforeSubmit();
                var fresh = locator.Find(expected.Window);
                if (fresh is null || fresh.Identity != expected.Identity || fresh.SubmitButton?.Element is not { } button ||
                    LoginLocator.ReadNonSecretValue(fresh.Fields[0]) != account.UserName ||
                    !IsFourDigits(captchaValue) || LoginLocator.ReadNonSecretValue(fresh.Fields[2]) != captchaValue)
                    return new LoginResult(LoginOutcome.NotSubmitted, false, "登录框内容尚未就绪，已保留手动登录。");
                var invoke = button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ? (InvokePattern)pattern : null;
                var nativeButton = new IntPtr(button.Current.NativeWindowHandle);
                var canPost = nativeButton != IntPtr.Zero && Native.GetAncestor(nativeButton, Native.GaRoot) == fresh.Window && NewEraLocator.IsButtonClass(Native.ClassName(nativeButton));
                if (invoke is null && !canPost) return NotReady();
                var previousSuccessWindows = SignedInWindows(expected);
                CheckBeforeSubmit();
                if (!reserveAttempt()) return new LoginResult(LoginOutcome.NotSubmitted, false, "本次登录已处理，停止重复提交。");
                Interlocked.Exchange(ref submitted, 1); // An uncertain invoke is still consumed, never automatically retried.
                trace?.Invoke("Login.SubmitRequested");
                if (canPost) { if (!Native.PostMessage(nativeButton, 0x00F5, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("绑定按钮不可用。"); }
                else invoke!.Invoke();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsSignedIn(expected, expected.Window) || SignedInWindows(expected).Any(w => !previousSuccessWindows.Contains(w)))
                    {
                        trace?.Invoke("Login.Succeeded");
                        return new LoginResult(LoginOutcome.Succeeded, true, "已自动登录成功。");
                    }
                    if (HasLoginError(expected.Window))
                    {
                        trace?.Invoke("Login.Rejected");
                        return new LoginResult(LoginOutcome.Rejected, true, "登录未成功，已停止自动提交；请检查账号密码或验证码。");
                    }
                    await Task.Delay(300, token);
                }

                void CheckBeforeSubmit()
                {
                    token.ThrowIfCancellationRequested();
                    if (!policy.Allows(expected.Window) || Native.IsIconic(expected.Window) || !Native.IsWindowVisible(expected.Window))
                        throw new OperationCanceledException();
                }
            }, CancellationToken.None).WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            var requested = Volatile.Read(ref submitted) != 0;
            return new LoginResult(requested ? LoginOutcome.Pending : LoginOutcome.Interrupted, requested,
                requested ? $"已点击登录，暂未确认结果；请查看{expected.ApplicationName}界面，助手不会重复提交。" : "检测到操作或页面变化，已停止自动登录。");
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            var requested = Volatile.Read(ref submitted) != 0;
            return new LoginResult(requested ? LoginOutcome.Pending : LoginOutcome.NotSubmitted, requested,
                requested ? $"已请求登录，暂未确认结果；请查看{expected.ApplicationName}界面。" : "未能操作登录按钮，请手动登录。");
        }

        static LoginResult NotReady() => new(LoginOutcome.NotSubmitted, false, "验证码或登录按钮尚未就绪，已停止自动登录。");
    }

    private HashSet<IntPtr> SignedInWindows(LoginPage expected)
    {
        var result = new HashSet<IntPtr>();
        Native.EnumWindows((window, _) =>
        {
            if (Native.IsWindowVisible(window) && IsSignedIn(expected, window)) result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private bool IsSignedIn(LoginPage expected, IntPtr window)
    {
        if (!policy.Allows(window) || !MatchesAttempt(expected, window)) return false;
        return HasSignedInMarker(window) && locator.Find(window) is null;
    }

    private static bool MatchesAttempt(LoginPage expected, IntPtr window)
    {
        var path = Native.ProcessPath(window);
        if (expected.ApplicationKind is not { } kind || TargetPolicy.EasKind(path) != kind) return false;
        if (Native.ProcessIdentity(window) == expected.ProcessKey) return true;
        // Only EasyAudit's own launcher-to-client handoff may cross a process boundary.
        return kind == EasAppKind.EasyAudit &&
            Path.GetFileName(expected.ExecutablePath).Equals("EAS.Updater.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(path), "EAS.exe", StringComparison.OrdinalIgnoreCase) &&
            Native.IsSuccessorProcess(expected.ProcessKey, window);
    }

    internal static bool HasSignedInMarker(IntPtr window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var markers = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink)));
            for (var i = 0; i < markers.Count && i < 300; i++)
            {
                var current = markers[i].Current;
                var name = string.Concat(current.Name.Where(c => !char.IsWhiteSpace(c)));
                var logoutAction = current.ControlType == ControlType.Text
                    ? name is "注销登录" or "退出登录"
                    : name.Contains("注销登录", StringComparison.Ordinal) || name.Contains("退出登录", StringComparison.Ordinal);
                if (!current.IsOffscreen && current.IsEnabled &&
                    logoutAction) return true;
            }
            return false;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    private bool HasLoginError(IntPtr window)
    {
        if (!policy.Allows(window) || locator.Find(window) is null) return false;
        try
        {
            var texts = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            for (var i = 0; i < texts.Count && i < 150; i++)
            {
                var c = texts[i].Current;
                if (!c.IsOffscreen && (new[] { "验证码错误", "验证码不正确", "验证码已过期", "登录失败", "用户名或密码错误", "账号或密码错误", "密码错误" }.Any(c.Name.Contains) ||
                    c.Name.Contains("验证码", StringComparison.Ordinal) && new[] { "有误", "过期", "失效" }.Any(c.Name.Contains) ||
                    c.Name.Contains("请输入", StringComparison.Ordinal) && new[] { "用户名", "密码", "验证码" }.Any(c.Name.Contains))) return true;
            }
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        return false;
    }

    public static bool IsFourDigits(string? value) => value is { Length: 4 } && value.All(char.IsAsciiDigit);
}
