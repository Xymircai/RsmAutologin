// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;

namespace EasLoginHelper;

public sealed class TrayContext : ApplicationContext
{
    private readonly SettingsStore _settings;
    private readonly CredentialVault _vault = new();
    private readonly CaptchaRecognizer _ocr = new();
    private readonly InputActivity _activity = new();
    private readonly InputCoordinator _coordinator = new();
    private readonly NotifyIcon _tray = new();
    private readonly AccountsForm _accounts;
    private readonly RuntimeStatusWriter _runtime;
    private readonly Dictionary<ModuleId, ToolStripMenuItem> _pauseMenus = new(), _autoMenus = new(), _states = new();
    private readonly Dictionary<ModuleId, string> _errors = new();
    private readonly System.Windows.Forms.Timer _calibrationTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _requests = new() { Interval = 300 };
    private readonly EventWaitHandle? _showRequest;
    private ToolStripMenuItem? _ocrMenu;
    private TargetPolicy? _easPolicy;
    private LoginMonitor? _monitor;
    private NewEraMonitor? _newEraMonitor;
    private DateTime _calibrationUntil, _calibrationAfter;
    private bool _resumeAfterCalibration, _quitting;

    public TrayContext(SettingsStore settings, EventWaitHandle? showRequest = null, bool startInTray = false)
    {
        _settings = settings; _showRequest = showRequest;
        _runtime = new RuntimeStatusWriter(Path.GetDirectoryName(settings.FilePath)!);
        _accounts = new AccountsForm(settings, new AccountService(settings, _vault), _vault);
        _accounts.ModuleFillRequested += (module, account, captcha) => { _accounts.Hide(); RequestFill(module, account, captcha); };
        _accounts.PreferencesChanged += module => { ConfigurationChanged(module); RefreshMenus(); PublishRuntime(); };
        _accounts.ModuleAccountsChanged += module => { ConfigurationChanged(module); _accounts.ShowStatus(module, Status(module)); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowAccounts());
        menu.Items.Add(CreateModuleMenu(ModuleId.Eas)); menu.Items.Add(CreateModuleMenu(ModuleId.NewEra));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("暂停全部", null, (_, _) => { _monitor?.Pause(true); _newEraMonitor?.Pause(true); RefreshMenus(); });
        menu.Items.Add("恢复全部", null, (_, _) => { _monitor?.Pause(false); _newEraMonitor?.Pause(false); RefreshMenus(); });
        menu.Items.Add("打开使用说明", null, (_, _) => OpenInstructions());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出助手", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu; _tray.Icon = Branding.TrayIcon;
        _tray.Text = "审计软件自动登录助手 · EAS / 新纪元"; _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowAccounts();
        CreateMonitor(ModuleId.Eas); CreateMonitor(ModuleId.NewEra);
        _ = _ocr.WarmupAsync();
        _calibrationTimer.Tick += (_, _) => CalibrationTick();
        _requests.Tick += (_, _) => { if (_showRequest?.WaitOne(0) == true) ShowAccounts(); }; _requests.Start();
        RefreshMenus(); PublishRuntime();
        if (!startInTray) ShowAccounts();
    }

    private ToolStripMenuItem CreateModuleMenu(ModuleId module)
    {
        var menu = new ToolStripMenuItem(Modules.Name(module));
        menu.DropDownItems.Add("管理账号", null, (_, _) => { _accounts.SelectModule(module); ShowAccounts(); });
        menu.DropDownItems.Add("立即填充默认账号", null, (_, _) => RequestFill(module, _settings.Current.For(module).DefaultAccountId, false));
        var pause = new ToolStripMenuItem("暂停本模块");
        pause.Click += (_, _) => { if (module == ModuleId.Eas) _monitor?.Pause(!(_monitor?.Paused ?? false)); else _newEraMonitor?.Pause(!(_newEraMonitor?.Paused ?? false)); RefreshMenus(); };
        _pauseMenus[module] = pause; menu.DropDownItems.Add(pause);
        var auto = new ToolStripMenuItem("填写完成后自动登录");
        auto.Click += (_, _) => ChangeSetting(() => _settings.Update(s => s.For(module).AutoLoginEnabled = !s.For(module).AutoLoginEnabled));
        _autoMenus[module] = auto; menu.DropDownItems.Add(auto);
        if (module == ModuleId.Eas)
        {
            menu.DropDownItems.Add("重新识别验证码", null, (_, _) => RequestFill(module, _settings.Current.DefaultAccountId, true));
            _ocrMenu = new ToolStripMenuItem("本地识别验证码");
            _ocrMenu.Click += (_, _) => ChangeSetting(() => _settings.Update(s => s.OcrEnabled = !s.OcrEnabled));
            menu.DropDownItems.Add(_ocrMenu); menu.DropDownItems.Add("校准登录页", null, (_, _) => BeginCalibration());
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        var state = new ToolStripMenuItem("等待登录页") { Enabled = false }; _states[module] = state; menu.DropDownItems.Add(state);
        return menu;
    }

    private void CreateMonitor(ModuleId module)
    {
        try
        {
            var policy = new TargetPolicy(module);
            _errors.Remove(module);
            if (module == ModuleId.Eas)
            {
                _easPolicy = policy;
                _monitor = new LoginMonitor(_settings, _activity, policy, _vault, _ocr, _coordinator);
                _monitor.StatusChanged += message => OnStatus(module, message);
            }
            else
            {
                _newEraMonitor = new NewEraMonitor(_settings, _activity, policy, _vault, _coordinator);
                _newEraMonitor.StatusChanged += message => OnStatus(module, message);
            }
            OnStatus(module, Status(module));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Runtime.InteropServices.COMException)
        { _errors[module] = "未找到" + Modules.Name(module) + "进程暂不可访问，请以管理员权限运行助手。"; OnStatus(module, _errors[module]); }
    }

    private string Status(ModuleId module) => _errors.GetValueOrDefault(module) ?? (module == ModuleId.Eas ? _monitor?.Status : _newEraMonitor?.Status) ?? "正在等待登录页。";
    private void ConfigurationChanged(ModuleId module) { if (module == ModuleId.Eas) _monitor?.ConfigurationChanged(); else _newEraMonitor?.ConfigurationChanged(); }
    private void OnStatus(ModuleId module, string message)
    {
        if (_quitting) return;
        if (_states.TryGetValue(module, out var state)) state.Text = message;
        _accounts.ShowStatus(module, message); PublishRuntime();
        if (message.Contains("已填写", StringComparison.Ordinal) || message.Contains("登录成功", StringComparison.Ordinal) ||
            message.Contains("未成功", StringComparison.Ordinal) || message.Contains("暂未确认", StringComparison.Ordinal)) Balloon(Modules.Name(module) + "：" + message);
    }
    private void PublishRuntime() => _runtime.Write(_monitor?.Runtime ?? new(Status(ModuleId.Eas), false), _newEraMonitor?.Runtime ?? new(Status(ModuleId.NewEra), false));
    private void RefreshMenus()
    {
        foreach (var module in Enum.GetValues<ModuleId>())
        {
            _autoMenus[module].Checked = _settings.Current.For(module).AutoLoginEnabled;
            var paused = module == ModuleId.Eas ? _monitor?.Paused ?? false : _newEraMonitor?.Paused ?? false;
            _pauseMenus[module].Text = paused ? "恢复本模块" : "暂停本模块";
        }
        if (_ocrMenu is not null) _ocrMenu.Checked = _settings.Current.OcrEnabled;
    }
    private void ChangeSetting(Action action)
    {
        try { action(); _accounts.RefreshAccounts(); RefreshMenus(); PublishRuntime(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowWarning(e.Message); }
    }
    private void RequestFill(ModuleId module, Guid? account, bool captcha)
    {
        if (_errors.TryGetValue(module, out var error)) { Balloon(error); return; }
        if (module == ModuleId.Eas) _monitor?.RequestFill(account, captcha); else _newEraMonitor?.RequestFill(account);
        RefreshMenus(); Balloon(Status(module));
    }
    private void ShowAccounts()
    {
        _accounts.RefreshAccounts(); _accounts.Show();
        if (_accounts.WindowState == FormWindowState.Minimized) _accounts.WindowState = FormWindowState.Normal;
        _accounts.Activate();
    }
    private void BeginCalibration()
    {
        if (_calibrationTimer.Enabled || _monitor is null || _easPolicy is null) return;
        _resumeAfterCalibration = !_monitor.Paused; _monitor.Pause(true); _accounts.Hide();
        _calibrationAfter = DateTime.UtcNow.AddSeconds(2); _calibrationUntil = DateTime.UtcNow.AddSeconds(20);
        _calibrationTimer.Start(); Balloon("请切到 EAS 登录页并停留。稍后按提示框选六个区域；Esc 可取消。");
    }
    private void CalibrationTick()
    {
        if (DateTime.UtcNow > _calibrationUntil)
        { _calibrationTimer.Stop(); FinishCalibration(); Balloon("未找到前台 EAS 页面，校准已取消。"); return; }
        if (DateTime.UtcNow < _calibrationAfter || !Native.IsIdle()) return;
        var window = Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot);
        if (_easPolicy?.Allows(window) != true) return;
        _calibrationTimer.Stop();
        try
        {
            using var wizard = new CalibrationForm(window, Native.ProcessPath(window)!);
            if (wizard.ShowDialog() == DialogResult.OK && wizard.Result is { } result)
            { _settings.Update(s => s.Calibration = result); Balloon("校准已保存。请回到 EAS 登录页，必要时选择“立即填充”。"); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { ShowWarning("校准未完成，请确保整个登录面板可见后重试。"); }
        finally { FinishCalibration(); }
    }
    private void FinishCalibration() { if (_resumeAfterCalibration) _monitor?.Pause(false); RefreshMenus(); }
    private static void ShowWarning(string text) => MessageBox.Show(text, "审计软件自动登录助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private void Balloon(string text) { _tray.BalloonTipTitle = "审计软件自动登录助手"; _tray.BalloonTipText = text; _tray.ShowBalloonTip(4000); }
    private void OpenInstructions()
    { using var info = new InformationForm(UiLayoutStore.ForSettings(_settings)); info.ShowDialog(_accounts); }
    protected override void ExitThreadCore()
    {
        _quitting = true; _calibrationTimer.Stop(); _requests.Stop(); _monitor?.Dispose(); _newEraMonitor?.Dispose(); _tray.Visible = false;
        _accounts.Dispose(); _activity.Dispose(); _ocr.Dispose(); base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _calibrationTimer.Dispose(); _requests.Dispose(); _monitor?.Dispose(); _newEraMonitor?.Dispose(); _tray.Dispose(); }
        base.Dispose(disposing);
    }
}
