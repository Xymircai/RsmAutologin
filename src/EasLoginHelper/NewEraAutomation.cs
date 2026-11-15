// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace EasLoginHelper;

public sealed record DesktopField(Rectangle Bounds, AutomationElement? Element, IntPtr Handle);
public sealed record NewEraPage(IntPtr Window, string ProcessKey, Rectangle Client,
    DesktopField UserName, DesktopField Password, DesktopField Confirm)
{
    public string Identity => ProcessKey + ":" + Window.ToInt64();
}
public enum NewEraError { None, SessionConflict, CredentialsRejected, Other }
public sealed record NewEraObservation(NewEraError Error, string Fingerprint, bool UserNameEmpty);

public sealed class NewEraLocator(TargetPolicy policy, bool useAutomation = true)
{
    public NewEraPage? Find(IntPtr window)
    {
        if (policy.Module != ModuleId.NewEra || !policy.Allows(window) || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return null;
        var client = Native.ClientBounds(window);
        if (client.Width < 250 || client.Height < 170 || !Normalize(Native.Caption(window)).StartsWith("容诚专用版", StringComparison.Ordinal)) return null;
        var process = ProcessKey(window);
        if (process is null) return null;
        if (useAutomation) try
        {
            var controls = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
            var edits = new List<(DesktopField Field, bool Password)>();
            var confirms = new List<DesktopField>(); var cancel = false;
            for (var i = 0; i < controls.Count && i < 120; i++)
            {
                var element = controls[i]; var current = element.Current;
                var bounds = LoginLocator.ToRectangle(current.BoundingRectangle);
                if (current.IsOffscreen || !current.IsEnabled || bounds.IsEmpty || !client.Contains(bounds)) continue;
                var handle = new IntPtr(current.NativeWindowHandle);
                var field = new DesktopField(bounds, element, handle);
                if (current.ControlType == ControlType.Edit && !(handle != IntPtr.Zero && Native.IsReadOnlyEdit(handle)) &&
                    !(element.TryGetCurrentPattern(ValuePattern.Pattern, out var readOnly) && ((ValuePattern)readOnly).Current.IsReadOnly))
                    edits.Add((field, current.IsPassword || handle != IntPtr.Zero && Native.IsPasswordEdit(handle)));
                else if (Normalize(current.Name) == "确定") confirms.Add(field);
                else if (Normalize(current.Name) == "取消") cancel = true;
            }
            if (Build(edits, confirms, cancel) is { } result) return result;
        }
        catch (Exception e) when (IsAutomationError(e)) { }

        // Delphi's standard TEdit/TButton controls remain addressable when its UIA provider is sparse.
        var nativeEdits = new List<(DesktopField Field, bool Password)>();
        var nativeConfirms = new List<DesktopField>(); var nativeCancel = false;
        foreach (var child in Native.Children(window))
        {
            if (!Native.IsWindowVisible(child) || !Native.IsWindowEnabled(child)) continue;
            var bounds = Native.WindowBounds(child); if (bounds.IsEmpty || !client.Contains(bounds)) continue;
            var type = Native.ClassName(child);
            if (IsEditClass(type) && !Native.IsReadOnlyEdit(child)) nativeEdits.Add((new(bounds, null, child), Native.IsPasswordEdit(child)));
            else if (IsButtonClass(type))
            {
                var caption = Normalize(Native.Caption(child));
                if (caption == "确定") nativeConfirms.Add(new(bounds, null, child));
                else if (caption == "取消") nativeCancel = true;
            }
        }
        return Build(nativeEdits, nativeConfirms, nativeCancel);

        NewEraPage? Build(List<(DesktopField Field, bool Password)> edits, List<DesktopField> confirms, bool cancel)
        {
            if (edits.Count != 2 || confirms.Count != 1 || !cancel || edits.Count(e => e.Password) != 1) return null;
            var password = edits.Single(e => e.Password).Field; var user = edits.Single(e => !e.Password).Field;
            if (user.Bounds.Bottom > password.Bounds.Top || Math.Abs(user.Bounds.Left - password.Bounds.Left) > user.Bounds.Width * .15 ||
                user.Bounds.Width < 60 || password.Bounds.Width < 60 || confirms[0].Bounds.Top < password.Bounds.Bottom) return null;
            return new(window, process, client, user, password, confirms[0]);
        }
    }

    public List<IntPtr> Windows(string? processKey = null)
    {
        var windows = new List<IntPtr>();
        Native.EnumWindows((window, _) =>
        {
            if (Native.IsWindowVisible(window) && policy.Allows(window) && (processKey is null || ProcessKey(window) == processKey)) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public bool IsSignedIn(string processKey)
    {
        var windows = Windows(processKey);
        // An existing workspace behind a visible authentication dialog is not a successful login.
        if (windows.Any(w => Normalize(Native.Caption(w)).StartsWith("容诚专用版", StringComparison.Ordinal))) return false;
        return windows.Any(window =>
        {
            var texts = ReadLabels(window);
            return texts.Any(t => Normalize(t) is "账套操作" or "帐套操作") &&
                texts.Any(t => Normalize(t) == "打开") && texts.Any(t => Normalize(t) == "取消");
        });
    }

    public NewEraObservation Observe(NewEraPage page)
    {
        var text = string.Join(" ", ReadLabels(page.Window));
        return new(ClassifyError(text), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text)))),
            ReadUserName(page.UserName) is "");
    }

    public static NewEraError ClassifyError(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Contains("该账号已于", StringComparison.Ordinal) && normalized.Contains("登录", StringComparison.Ordinal) &&
            normalized.Contains("重新输入登录信息获取授权", StringComparison.Ordinal)) return NewEraError.SessionConflict;
        if (new[] { "密码错误", "密码不正确", "用户名或密码", "账号或密码", "用户不存在", "无效用户" }.Any(normalized.Contains)) return NewEraError.CredentialsRejected;
        if (new[] { "连接失败", "连接超时", "网络错误", "服务器错误", "登录失败", "认证失败", "授权失败" }.Any(normalized.Contains)) return NewEraError.Other;
        return NewEraError.None;
    }

    internal static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ':' and not '：' and not '&'));
    internal static bool IsEditClass(string type) => type.Equals("Edit", StringComparison.OrdinalIgnoreCase) || type.Equals("TEdit", StringComparison.OrdinalIgnoreCase) ||
        type.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase);
    internal static bool IsButtonClass(string type) => type.Equals("Button", StringComparison.OrdinalIgnoreCase) || type is "TButton" or "TBitBtn" ||
        type.StartsWith("WindowsForms10.BUTTON", StringComparison.OrdinalIgnoreCase);
    internal static bool IsAutomationError(Exception e) => e is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException;
    public static string? ProcessKey(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out var id);
        try { using var process = Process.GetProcessById((int)id); return id + ":" + process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    public static string? ReadUserName(DesktopField field)
    {
        if (field.Handle != IntPtr.Zero && IsEditClass(Native.ClassName(field.Handle))) return Native.ReadBoundUserName(field.Handle);
        if (field.Element is { } element && !element.Current.IsPassword && element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            return ((ValuePattern)value).Current.Value;
        return field.Handle != IntPtr.Zero ? Native.ReadBoundUserName(field.Handle) : null;
    }

    // Only captions/label names are read. Values, grids, list items and password text are never inspected.
    internal static List<string> ReadLabels(IntPtr window)
    {
        var texts = new List<string> { Native.Caption(window) };
        var login = Normalize(texts[0]).StartsWith("容诚专用版", StringComparison.Ordinal);
        var hasErrorEdit = false;
        foreach (var child in Native.Children(window))
        {
            if (!Native.IsWindowVisible(child)) continue;
            var type = Native.ClassName(child);
            if (login && IsEditClass(type) && Native.IsReadOnlyEdit(child))
            {
                hasErrorEdit = true;
                var errorText = Native.ReadBoundUserName(child); if (!string.IsNullOrEmpty(errorText)) texts.Add(errorText);
            }
            if (IsButtonClass(type) || type.Contains("Static", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("Label", StringComparison.OrdinalIgnoreCase) || type.Contains("Form", StringComparison.OrdinalIgnoreCase) || type == "#32770")
                texts.Add(Native.Caption(child));
        }
        // Standard dialogs expose all required markers natively. Avoid traversing financial lists/grids.
        var names = texts.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        if (login && hasErrorEdit && names.Contains("确定") && names.Contains("取消") ||
            (names.Contains("账套操作") || names.Contains("帐套操作")) && names.Contains("打开") && names.Contains("取消")) return texts;
        try
        {
            var labels = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)));
            for (var i = 0; i < labels.Count && i < 220; i++)
            {
                var label = labels[i]; var current = label.Current;
                if (current.IsOffscreen) continue;
                texts.Add(current.Name);
            }
        }
        catch (Exception e) when (IsAutomationError(e)) { }
        ReadMsaaLabels(window, texts);
        return texts;
    }

    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object accessible);
    private static void ReadMsaaLabels(IntPtr window, List<string> texts)
    {
        try
        {
            var iid = typeof(Accessibility.IAccessible).GUID;
            if (AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref iid, out var root) != 0) return;
            var remaining = 180;
            Visit(root as Accessibility.IAccessible, 0);
            void Visit(Accessibility.IAccessible? node, int depth)
            {
                if (node is null || depth > 10 || --remaining < 0) return;
                var count = Math.Min(node.accChildCount, 100);
                for (var i = 1; i <= count && remaining > 0; i++)
                {
                    object? child = null;
                    try
                    {
                        child = node.get_accChild(i);
                        if (child is Accessibility.IAccessible nested) { Visit(nested, depth + 1); continue; }
                        var role = Convert.ToInt32(node.get_accRole(i));
                        var state = Convert.ToInt32(node.get_accState(i));
                        if ((state & 0x8000) == 0 && role is 0x29 or 0x2B or 0x14)
                        { var name = node.get_accName(i); if (!string.IsNullOrWhiteSpace(name)) texts.Add(name); }
                        remaining--;
                    }
                    catch (Exception e) when (e is COMException or ArgumentException or InvalidCastException) { }
                    finally { if (child is not null && Marshal.IsComObject(child)) Marshal.ReleaseComObject(child); }
                }
                // Leaf label/button objects expose their own name at CHILDID_SELF.
                var ownRole = Convert.ToInt32(node.get_accRole(0));
                if ((Convert.ToInt32(node.get_accState(0)) & 0x8000) == 0 && ownRole is 0x29 or 0x2B or 0x14)
                { var name = node.get_accName(0); if (!string.IsNullOrWhiteSpace(name)) texts.Add(name); }
            }
            if (Marshal.IsComObject(root)) Marshal.ReleaseComObject(root);
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidCastException or InvalidOperationException) { }
    }
}

public sealed class NewEraAttemptGate
{
    private readonly object _sync = new();
    private sealed class Attempt { public int Submissions; public bool Succeeded; }
    private readonly Dictionary<string, Attempt> _processes = new();
    private Attempt? _current;
    public int Submissions { get { lock (_sync) return _current?.Submissions ?? 0; } }
    public bool Observe(string processKey)
    {
        lock (_sync)
        {
            if (_processes.TryGetValue(processKey, out var previous) && !previous.Succeeded)
            { _current = previous; return false; }
            _processes[processKey] = _current = new Attempt(); return true;
        }
    }
    public bool Reserve(bool freshSessionConflict = false)
    {
        lock (_sync)
        {
            if (_current is null || _current.Submissions >= 2 || _current.Submissions == 1 && !freshSessionConflict) return false;
            _current.Submissions++; return true;
        }
    }
    public void MarkSucceeded() { lock (_sync) { if (_current is not null) _current.Succeeded = true; } }
    public void Reset() { lock (_sync) { _processes.Clear(); _current = null; } }
}

public sealed record NewEraRunResult(FillOutcome FillOutcome, LoginOutcome LoginOutcome, bool Submitted, int Submissions, string Message)
{
    public int UserNameWrites { get; init; }
    public int PasswordWrites { get; init; }
}

public sealed class NewEraController(TargetPolicy policy, NewEraLocator locator, InputActivity activity,
    CredentialVault vault, Action<string>? trace = null, TimeSpan? resultTimeout = null)
{
    public async Task<NewEraRunResult> RunAsync(NewEraPage expected, AccountProfile account, bool loginAfterFill,
        NewEraAttemptGate gate, bool requireForeground, Func<bool> canContinue, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter((resultTimeout ?? TimeSpan.FromSeconds(30)) * 2 + TimeSpan.FromSeconds(8));
        try
        {
            return await Task.Run(() => RunCoreAsync(expected, account, loginAfterFill, gate, requireForeground, canContinue, deadline.Token), CancellationToken.None).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return new(FillOutcome.Interrupted, gate.Submissions > 0 ? LoginOutcome.Pending : LoginOutcome.Interrupted,
                gate.Submissions > 0, gate.Submissions, "新纪元操作已取消或控件响应超时，已停止继续提交。");
        }
    }

    private async Task<NewEraRunResult> RunCoreAsync(NewEraPage expected, AccountProfile account, bool loginAfterFill,
        NewEraAttemptGate gate, bool requireForeground, Func<bool> canContinue, CancellationToken cancellation)
    {
        var writtenKeys = activity.KeyboardGenerationFor(expected.Window);
        var userWrites = 0; var passwordWrites = 0;
        _ = requireForeground;
        var filled = false;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Check();
                var page = Fresh();
                writtenKeys = activity.KeyboardGenerationFor(page.Window);
                using (var secret = vault.Read(account.Id))
                {
                    if (secret is null) return Result(FillOutcome.MissingCredential, LoginOutcome.NotSubmitted, "未找到保存的密码，请在本模块重新保存账号。");
                    Set(page.UserName, account.UserName.AsSpan());
                    page = Fresh();
                    Set(page.Password, secret.Characters, true);
                }
                page = Fresh();
                filled = true; trace?.Invoke("NewEra.CredentialsFilled");
                if (!loginAfterFill) return Result(FillOutcome.Filled, LoginOutcome.NotSubmitted, "新纪元账号密码已填写。");
                var baseline = locator.Observe(page);
                Check();
                if (!canContinue() || !gate.Reserve(attempt == 1))
                    return Result(FillOutcome.Filled, LoginOutcome.NotSubmitted, "本轮登录已处理，已停止重复提交。");
                trace?.Invoke(attempt == 0 ? "NewEra.SubmitRequested" : "NewEra.SessionRetryRequested");
                Invoke(page);
                var deadline = DateTime.UtcNow + (resultTimeout ?? TimeSpan.FromSeconds(30));
                var retry = false;
                while (DateTime.UtcNow < deadline)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (locator.IsSignedIn(expected.ProcessKey))
                    {
                        gate.MarkSucceeded(); trace?.Invoke("NewEra.Succeeded");
                        return Result(FillOutcome.Filled, LoginOutcome.Succeeded, "新纪元已自动登录成功。");
                    }
                    var fresh = locator.Windows(expected.ProcessKey).Select(locator.Find).Where(p => p is not null).ToArray();
                    if (fresh.Length == 1)
                    {
                        var observed = locator.Observe(fresh[0]!);
                        var newError = observed.Fingerprint != baseline.Fingerprint || observed.UserNameEmpty;
                        if (newError && observed.Error == NewEraError.SessionConflict)
                        {
                            if (attempt == 0 && canContinue())
                            {
                                expected = fresh[0]!; Check();
                                trace?.Invoke("NewEra.SessionConflict");
                                await Task.Delay(600, cancellation); retry = true; break;
                            }
                            return Result(FillOutcome.Filled, LoginOutcome.Rejected, "新纪元重新授权仍未成功，已停止；请检查软件提示后手动重试。");
                        }
                        if (newError && observed.Error is NewEraError.CredentialsRejected or NewEraError.Other)
                            return Result(FillOutcome.Filled, LoginOutcome.Rejected, "新纪元登录未成功，已停止；请检查账号密码或网络提示。");
                    }
                    await Task.Delay(250, cancellation);
                }
                if (!retry) return Result(FillOutcome.Filled, LoginOutcome.Pending, "已点击新纪元登录，暂未确认结果；助手不会继续提交。");
            }
            return Result(FillOutcome.Filled, LoginOutcome.Rejected, "新纪元已达到本轮重试次数，请检查后手动重试。");
        }
        catch (OperationCanceledException)
        {
            return Result(filled ? FillOutcome.Filled : FillOutcome.Interrupted,
                gate.Submissions > 0 ? LoginOutcome.Pending : LoginOutcome.Interrupted, "检测到操作或页面切换，已停止新纪元自动登录。");
        }
        catch (Exception e) when (NewEraLocator.IsAutomationError(e) || e is System.ComponentModel.Win32Exception or IOException)
        {
            trace?.Invoke("NewEra.Error." + e.GetType().Name);
            return Result(filled ? FillOutcome.Filled : FillOutcome.InvalidPage,
                gate.Submissions > 0 ? LoginOutcome.Pending : LoginOutcome.NotSubmitted, "新纪元控件暂不可访问，已停止；请检查登录页后重试。");
        }

        NewEraRunResult Result(FillOutcome fill, LoginOutcome login, string message) => new(fill, login, gate.Submissions > 0, gate.Submissions, message)
            { UserNameWrites = userWrites, PasswordWrites = passwordWrites };
        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (!policy.Allows(expected.Window) || !Native.IsWindowVisible(expected.Window) || Native.IsIconic(expected.Window))
                throw new OperationCanceledException();
        }
        NewEraPage Fresh()
        {
            Check();
            var fresh = locator.Find(expected.Window);
            if (fresh is null || fresh.Identity != expected.Identity) throw new OperationCanceledException();
            return fresh;
        }
        void Set(DesktopField field, ReadOnlySpan<char> value, bool password = false)
        {
            Check();
            foreach (var character in value) if (char.IsControl(character)) throw new InvalidOperationException("不支持控制字符。");
            if (password) passwordWrites++; else userWrites++;
            if (field.Handle != IntPtr.Zero && Native.GetAncestor(field.Handle, Native.GaRoot) == expected.Window && NewEraLocator.IsEditClass(Native.ClassName(field.Handle)))
            {
                // WM_SETTEXT sends the standard Edit change notification without asking UIA to move focus.
                if (!Native.SetBoundText(field.Handle, [])) throw new InvalidOperationException();
                Check(); if (!Native.SetBoundText(field.Handle, value)) throw new InvalidOperationException();
            }
            else if (field.Element is { } element && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly)
            {
                ((ValuePattern)pattern).SetValue(""); Check(); ((ValuePattern)pattern).SetValue(new string(value));
            }
            else throw new InvalidOperationException("输入框不支持绑定填写。");
            Check();
        }
        void Invoke(NewEraPage page)
        {
            Check();
            var current = Fresh();
            if (NewEraLocator.ReadUserName(current.UserName) != account.UserName || activity.KeyboardGenerationFor(page.Window) != writtenKeys)
            {
                using var secret = vault.Read(account.Id); if (secret is null) throw new InvalidOperationException("保存的密码不可用。");
                Set(current.UserName, account.UserName); Set(current.Password, secret.Characters, true);
                writtenKeys = activity.KeyboardGenerationFor(page.Window); current = Fresh(); trace?.Invoke("NewEra.DefaultCorrected");
            }
            if (NewEraLocator.ReadUserName(current.UserName) != account.UserName) throw new InvalidOperationException("默认账号未能确认。");
            var button = current.Confirm;
            if (button.Handle != IntPtr.Zero && Native.GetAncestor(button.Handle, Native.GaRoot) == current.Window && NewEraLocator.IsButtonClass(Native.ClassName(button.Handle)))
            { if (!Native.PostMessage(button.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("确定按钮不可用。"); }
            else if (button.Element is { } element && element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
            else throw new InvalidOperationException("未定位到确定按钮。");
        }
    }
}
