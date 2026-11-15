// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

public sealed class LoginMonitor : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly InputActivity _activity;
    private readonly TargetPolicy _policy;
    private readonly LoginLocator _locator;
    private readonly EasLoginTransaction _transaction;
    private readonly InputCoordinator _coordinator;
    private readonly ICaptchaFrameSource _frames;
    private readonly Dictionary<string, EasLoginEpisode> _episodes = new();
    private readonly Dictionary<string, LoginAttemptGate> _gates = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private CancellationTokenSource _lifetime = new();
    private Task<Scan>? _scan;
    private bool _busy, _paused, _disposed, _ticking;
    private bool _manual, _manualCaptcha;
    private Guid? _manualAccount;
    private int _configuration;
    private string _status = "正在后台等待 EAS 登录页。";
    private EasLoginEpisode? _last;
    private DateTime _retryAfter;
    public event Action<string>? StatusChanged;
    public string Status => _status;
    public bool Paused => _paused;
    public LoginPage? LastPage { get; private set; }

    public LoginMonitor(SettingsStore settings, InputActivity activity, TargetPolicy policy, CredentialVault vault, ICaptchaRecognizer ocr,
        InputCoordinator? coordinator = null, ICaptchaFrameSource? frames = null, TimeSpan? loginTimeout = null)
    {
        _settings = settings; _activity = activity; _policy = policy; _coordinator = coordinator ?? new();
        _locator = new LoginLocator(policy, () => settings.Current.Calibration);
        _frames = frames ?? WindowCaptureSource.Shared;
        _transaction = new EasLoginTransaction(policy, _locator, activity, vault, ocr, _frames, loginTimeout: loginTimeout);
        _timer.Tick += async (_, _) => await TickAsync(); _timer.Start();
        if (settings.Current.DefaultAccount is null) _status = "请先为 EAS 保存默认账号。";
    }
    public void Pause(bool pause)
    {
        if (_paused == pause) return;
        _paused = pause;
        if (pause) { _lifetime.Cancel(); _manual = false; }
        else { _lifetime.Dispose(); _lifetime = new(); }
        SetStatus(pause ? "EAS 自动登录已暂停。" : "EAS 后台监听已恢复。");
    }
    public void ConfigurationChanged()
    {
        _configuration++; _lifetime.Cancel(); _lifetime = new();
        foreach (var episode in _episodes.Values) if (episode.Counts.Submissions == 0)
        { episode.CredentialsWritten = false; episode.Completed = false; episode.Preparations = 0; }
    }
    public void RequestFill(Guid? account, bool captchaOnly = false)
    {
        if (_busy) { SetStatus("EAS 正在处理本轮登录，请稍候。"); return; }
        if (_paused) Pause(false);
        _manual = true; _manualAccount = account; _manualCaptcha = captchaOnly;
        SetStatus("正在后台查找 EAS 登录页，无需停止鼠标键盘操作。");
    }

    private sealed record Scan(LoginPage[] Pages, HashSet<string> SignedIn);
    private Scan FindPages()
    {
        var roots = new List<IntPtr>();
        Native.EnumWindows((window, _) =>
        { if (_policy.Allows(window) && Native.IsWindowVisible(window) && !Native.IsIconic(window)) roots.Add(window); return true; }, IntPtr.Zero);
        var pages = new List<LoginPage>(); var signed = new HashSet<string>();
        foreach (var root in roots)
        {
            var page = _locator.Find(root);
            if (page is not null && !page.UsesCalibration) pages.Add(page);
            else if (page is null && AutoLoginController.HasSignedInMarker(root) && Native.ProcessIdentity(root) is { } key) signed.Add(key);
        }
        return new(pages.ToArray(), signed);
    }

    private async Task TickAsync()
    {
        if (_disposed || _paused || _busy || _ticking) return;
        _ticking = true;
        try
        {
            if (_scan is null) { _scan = Task.Run(FindPages); return; }
            if (!_scan.IsCompleted) return; // One UIA worker, even when a provider stalls.
            var finished = _scan; _scan = null; var scan = await finished;
            if (_disposed || _paused) return;
            foreach (var key in scan.SignedIn)
                if (_episodes.TryGetValue(key, out var previous) && !scan.Pages.Any(p => p.ProcessKey == key))
                {
                    previous.Login = new(LoginOutcome.Succeeded, previous.Counts.Submissions > 0, "EAS 已确认登录成功。");
                    _gates[key].MarkSucceeded(key); previous.Stage = "Login.Succeeded"; _last = previous;
                    SetStatus("EAS 已登录，正在等待下一次进入登录页。");
                }
            LastPage = scan.Pages.FirstOrDefault();
            if (scan.Pages.Length == 0) return;
            if (_manual && scan.Pages.Length != 1) { SetStatus("存在多个 EAS 登录页，请关闭多余窗口后选择立即填充。"); return; }
            foreach (var page in scan.Pages)
            {
                if (scan.Pages.Count(p => p.ProcessKey == page.ProcessKey) > 1) continue;
                if (!_gates.TryGetValue(page.ProcessKey, out var gate)) _gates[page.ProcessKey] = gate = new();
                var armed = gate.ObserveLoginPage(page.ProcessKey);
                if (!_episodes.TryGetValue(page.ProcessKey, out var episode) || armed)
                    _episodes[page.ProcessKey] = episode = new();
                var manual = _manual;
                if (!manual && gate.HasSubmitted(page.ProcessKey)) continue;
                var account = manual && _manualAccount is { } id ? _settings.Current.Accounts.Find(a => a.Id == id) : _settings.Current.DefaultAccount;
                if (account is null) { SetStatus("请为 EAS 保存默认账号。"); continue; }
                if (manual)
                {
                    gate.ResetExplicitly(); gate.ObserveLoginPage(page.ProcessKey);
                    episode.Login = null; episode.Completed = false; episode.Preparations = 0; episode.Counts.Submissions = 0;
                    if (!_manualCaptcha) episode.CredentialsWritten = false;
                }
                else if (episode.Completed)
                {
                    if (episode.Login is not null || episode.Fill?.Outcome != FillOutcome.Partial || episode.Preparations >= 3 || DateTime.UtcNow < _retryAfter) continue;
                    _retryAfter = DateTime.UtcNow.AddSeconds(2);
                    using var preview = await _frames.CaptureAsync(page.Window, page.Captcha, _lifetime.Token);
                    if (FillController.ImageHash(preview) == episode.Captcha.LastObservedHash) continue;
                }
                using var lease = _coordinator.TryEnter(); if (lease is null) return;
                _manual = false; _busy = true; _last = episode;
                var epoch = _configuration; var captchaOnly = manual && _manualCaptcha;
                var submit = _settings.Current.AutoLoginEnabled;
                try
                {
                    SetStatus("已识别 EAS 登录页，正在后台填写并核对验证码…");
                    await _transaction.RunAsync(page, account, episode, _settings.Current.OcrEnabled, submit, gate,
                        () => !_disposed && !_paused && epoch == _configuration && (!submit || _settings.Current.AutoLoginEnabled),
                        _lifetime.Token, captchaOnly);
                    if (episode.Login?.Outcome == LoginOutcome.Succeeded) SetStatus("EAS 已自动登录成功。");
                    else SetStatus(episode.Login?.Message ?? episode.Fill?.Message ?? "EAS 本轮已停止。");
                    _retryAfter = DateTime.UtcNow.AddSeconds(2);
                }
                finally { _busy = false; }
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (NewEraLocator.IsAutomationError(e) || e is IOException or System.ComponentModel.Win32Exception)
        { _scan = null; SetStatus("EAS 后台控件或验证码暂不可用，未重复提交；请恢复窗口后重试。"); }
        finally { _ticking = false; }
    }
    public ModuleRuntime Runtime => new(_status, _paused, LastPage is not null, _last?.Stage, (_last?.Counts.Submissions ?? 0) > 0,
        _last?.Login?.Outcome == LoginOutcome.Succeeded, _settings.Current.AutoLoginEnabled, _last?.Counts.Submissions ?? 0,
        _last?.Counts.UserName ?? 0, _last?.Counts.Password ?? 0, _last?.Counts.Captcha ?? 0);
    private void SetStatus(string message) { if (_disposed || _status == message) return; _status = message; StatusChanged?.Invoke(message); }
    internal static bool HasKeyboardEdits(InputActivity activity, IntPtr window, long baseline) => activity.KeyboardGenerationFor(window) != baseline;
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _timer.Stop(); _timer.Dispose(); }
}
