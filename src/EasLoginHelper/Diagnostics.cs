// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace EasLoginHelper;

public static class Diagnostics
{
    public static string OutputPath => Path.Combine(SettingsStore.DataDirectory, "diagnostics.json");
    public static void Write(SettingsStore settings, ModuleId module = ModuleId.Eas)
    {
        if (module == ModuleId.NewEra) { WriteNewEra(settings); return; }
        var policy = new TargetPolicy(ModuleId.Eas);
        var locator = new LoginLocator(policy, () => settings.Current.Calibration);
        var windows = new List<object>();
        Native.EnumWindows((window, _) =>
        {
            if (!policy.Allows(window)) return true;
            var stopwatch = Stopwatch.StartNew();
            var elements = new List<object>();
            string? error = null;
            try
            {
                var all = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                for (var i = 0; i < all.Count && i < 100; i++)
                {
                    var current = all[i].Current;
                    var knownName = current.Name.Trim();
                    if (!new[] { "用户名", "密码", "验证码", "登录", "登 录", "Login", "Log in" }.Contains(knownName)) knownName = "[omitted]";
                    elements.Add(new { Type = current.ControlType.ProgrammaticName, Name = knownName,
                        Password = current.IsPassword, current.IsOffscreen, current.IsEnabled,
                        Bounds = LoginLocator.ToRectangle(current.BoundingRectangle),
                        ValuePatternSupported = (bool)all[i].GetCurrentPropertyValue(AutomationElement.IsValuePatternAvailableProperty) });
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ElementNotAvailableException)
            { error = e.GetType().Name; }
            var page = locator.Find(window);
            windows.Add(new { Handle = window.ToInt64(), Executable = Native.ProcessPath(window), NativeClass = Native.ClassName(window),
                Visible = Native.IsWindowVisible(window), Minimized = Native.IsIconic(window), WindowBounds = Native.WindowBounds(window),
                Dpi = Native.GetDpiForWindow(window), Client = Native.ClientBounds(window),
                LoginDetected = page is not null, SignedIn = page is null && AutoLoginController.HasSignedInMarker(window), UsesCalibration = page?.UsesCalibration,
                ElapsedMs = stopwatch.ElapsedMilliseconds, ErrorType = error, Elements = elements });
            return true;
        }, IntPtr.Zero);
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var report = new { FormatVersion = 1, CreatedUtc = DateTime.UtcNow, Elevated = IsElevated(),
            AccountCount = settings.Current.Accounts.Count, EmbeddedOcrPresent = EmbeddedAssets.OcrPresent,
            ApplicationVersion = typeof(Diagnostics).Assembly.GetName().Version?.ToString(), WindowsVersion = Environment.OSVersion.VersionString,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ForegroundIsEas = policy.Allows(Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot)),
            Windows = windows, Note = "Read-only. No values, passwords, screenshots, window titles, or login requests are collected." };
        File.WriteAllText(OutputPath, JsonSerializer.Serialize(report, SettingsStore.JsonOptions), new UTF8Encoding(false));
    }

    private static void WriteNewEra(SettingsStore settings)
    {
        var policy = new TargetPolicy(ModuleId.NewEra);
        var locator = new NewEraLocator(policy);
        var windows = new List<object>();
        foreach (var window in locator.Windows())
        {
            string? error = null; NewEraPage? page = null; bool signedIn = false;
            try { page = locator.Find(window); var process = NewEraLocator.ProcessKey(window); signedIn = process is not null && locator.IsSignedIn(process); }
            catch (Exception e) when (NewEraLocator.IsAutomationError(e)) { error = e.GetType().Name; }
            var children = Native.Children(window).Where(Native.IsWindowVisible).Take(100).Select(child =>
            {
                var type = Native.ClassName(child);
                var edit = NewEraLocator.IsEditClass(type);
                var name = !edit && (NewEraLocator.IsButtonClass(type) || type.Contains("Static", StringComparison.OrdinalIgnoreCase) || type.Contains("Group", StringComparison.OrdinalIgnoreCase))
                    ? NewEraLocator.Normalize(Native.Caption(child)) : "";
                if (!new[] { "确定", "取消", "保存登录信息", "登录信息", "用户名", "密码", "打开", "选择客户" }.Contains(name)) name = "[omitted]";
                return new { Class = type, Name = name, Password = edit && Native.IsPasswordEdit(child), ReadOnly = edit && Native.IsReadOnlyEdit(child), Enabled = Native.IsWindowEnabled(child), Bounds = Native.WindowBounds(child) };
            }).ToArray();
            windows.Add(new
            {
                Handle = window.ToInt64(), Class = Native.ClassName(window), Client = Native.ClientBounds(window), Dpi = Native.GetDpiForWindow(window),
                LoginDetected = page is not null, SignedIn = signedIn, ErrorType = error,
                UserNamePattern = SupportsValue(page?.UserName), PasswordPattern = SupportsValue(page?.Password),
                SubmitPattern = page?.Confirm.Element?.TryGetCurrentPattern(InvokePattern.Pattern, out _) ?? false,
                LoginError = page is null ? null : locator.Observe(page).Error.ToString(), Children = children
            });
        }
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "diagnostics-new-era.json"), JsonSerializer.Serialize(new
        {
            FormatVersion = 2, Module = "new-era", CreatedUtc = DateTime.UtcNow, Elevated = IsElevated(), AccountCount = settings.Current.NewEra.Accounts.Count,
            ForegroundIsTarget = policy.Allows(Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot)), Windows = windows,
            Note = "Read-only. No values, passwords, screenshots, window titles, or login requests are collected."
        }, SettingsStore.JsonOptions), new UTF8Encoding(false));
        static bool SupportsValue(DesktopField? field) => field?.Element?.TryGetCurrentPattern(ValuePattern.Pattern, out _) ?? false;
    }
    public static bool IsElevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
