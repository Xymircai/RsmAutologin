// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

public sealed class NewEraMonitor : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly NewEraLocator _locator;
    private readonly NewEraController _controller;
    private readonly InputCoordinator _coordinator;
    private readonly NewEraAttemptGate _gate = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private CancellationTokenSource _lifetime = new();
    private Task<NewEraPage[]>? _scan;
    private Task<bool>? _confirmation;
    private string? _submittedProcess, _confirmationProcess;
    private DateTime _confirmAfter;
    private bool _busy, _paused, _disposed, _ticking;
    private string? _lastTrace;
    private readonly HashSet<string> _completed = new();
    private DateTime _manualUntil;
    private int _configuration;
    private Guid? _manualAccount;
    private string _status = "正在后台等待新纪元登录页。";
    private NewEraRunResult? _result;
    public NewEraPage? LastPage { get; private set; }
    public bool Paused => _paused;
    public string Status => _status;
    public event Action<string>? StatusChanged;
    public ModuleRuntime Runtime => new(_status, _paused, LastPage is not null, Volatile.Read(ref _lastTrace),
        _result?.Submitted ?? false, _result?.LoginOutcome == LoginOutcome.Succeeded, _settings.Current.NewEra.AutoLoginEnabled, _result?.Submissions ?? 0,
        _result?.UserNameWrites ?? 0, _result?.PasswordWrites ?? 0);

    public NewEraMonitor(SettingsStore settings, InputActivity activity, TargetPolicy policy, CredentialVault vault, InputCoordinator coordinator,
        TimeSpan? resultTimeout = null) : this(settings, activity, policy, vault, coordinator, resultTimeout,
            () => IntPtr.Zero, () => false, false) { }

    // Legacy test overload. Neither desktop focus nor idle state participates in background login.
    internal NewEraMonitor(SettingsStore settings, InputActivity activity, TargetPolicy policy, CredentialVault vault, InputCoordinator coordinator,
        TimeSpan? resultTimeout, Func<IntPtr> foreground, Func<bool> idle, bool requireForeground)
    {
        _ = foreground; _ = idle; _ = requireForeground;
        _settings = settings; _coordinator = coordinator;
        _locator = new NewEraLocator(policy);
        _controller = new NewEraController(policy, _locator, activity, vault, stage => Volatile.Write(ref _lastTrace, stage), resultTimeout);
        _timer.Tick += async (_, _) => await TickAsync(); _timer.Start();
        if (settings.Current.NewEra.DefaultAccount is null) _status = "请为新纪元保存账号并设置默认账号。";
    }

    public void Pause(bool pause)
    {
        if (_paused == pause) return;
        _paused = pause;
        if (pause) { _lifetime.Cancel(); _manualUntil = default; }
        else { _lifetime.Dispose(); _lifetime = new(); }
        SetStatus(pause ? "新纪元自动填充已暂停。" : "新纪元监听已恢复。");
    }
    public void RequestFill(Guid? id)
    {
        if (_busy) { SetStatus("新纪元正在填写，请稍候。"); return; }
        if (_paused) Pause(false);
        _gate.Reset(); _completed.Clear(); _result = null;
        _manualAccount = id; _manualUntil = DateTime.UtcNow.AddSeconds(20);
        SetStatus("正在后台查找新纪元登录页，无需停止鼠标键盘操作。");
    }

    public void ConfigurationChanged()
    {
        _configuration++; _lifetime.Cancel(); _lifetime = new(); _completed.Clear();
    }

    private async Task TickAsync()
    {
        if (_disposed || _paused || _busy || _ticking) return;
        _ticking = true;
        try
        {
            if (_result is { Submitted: true, LoginOutcome: LoginOutcome.Pending } && _submittedProcess is { } submittedProcess)
            {
                if (_confirmation is null && DateTime.UtcNow >= _confirmAfter)
                { _confirmationProcess = submittedProcess; _confirmation = Task.Run(() => _locator.IsSignedIn(submittedProcess)); }
                if (_confirmation?.IsCompleted == true)
                {
                    var finishedConfirmation = _confirmation; _confirmation = null; _confirmAfter = DateTime.UtcNow.AddSeconds(2);
                    var confirmed = await finishedConfirmation;
                    if (confirmed && _confirmationProcess == _submittedProcess && !_disposed && !_paused)
                    {
                        _gate.MarkSucceeded(); _result = _result with { LoginOutcome = LoginOutcome.Succeeded, Message = "新纪元已确认登录成功。" };
                        SetStatus(_result.Message);
                    }
                }
            }
            if (_manualUntil != default && DateTime.UtcNow > _manualUntil)
            { _manualUntil = default; SetStatus("未找到新纪元登录页，本次手动填充已取消。"); }
            if (_scan is null) { _scan = Task.Run(() => _locator.Windows().Where(w => !Native.IsIconic(w)).Select(_locator.Find).OfType<NewEraPage>().ToArray()); return; }
            if (!_scan.IsCompleted) return;
            var finished = _scan; _scan = null;
            var pages = await finished;
            if (_disposed || _paused) return;
            if (_manualUntil != default && pages.Length > 1) { SetStatus("存在多个新纪元登录页，请关闭多余窗口后选择立即填充。"); return; }
            var page = pages.FirstOrDefault(p => !_completed.Contains(p.ProcessKey));
            LastPage = page;
            if (page is null)
            {
                // A confirmed sign-in may later show another login form in the same process.
                if (_result?.LoginOutcome == LoginOutcome.Succeeded && pages.FirstOrDefault() is { } relogin) { _completed.Remove(relogin.ProcessKey); page = relogin; }
                else return;
            }
            if (_gate.Observe(page.ProcessKey)) { _completed.Remove(page.ProcessKey); _result = null; }
            var manual = _manualUntil != default;
            if (_gate.Submissions > 0 || _completed.Contains(page.ProcessKey) && !manual) return;
            var account = manual && _manualAccount is { } id ? _settings.Current.NewEra.Accounts.Find(a => a.Id == id) : _settings.Current.NewEra.DefaultAccount;
            if (account is null) { SetStatus("请为新纪元保存账号并设置默认账号。"); return; }
            using var lease = _coordinator.TryEnter(); if (lease is null) return;
            _manualUntil = default; _busy = true; _completed.Add(page.ProcessKey);
            var epoch = _configuration;
            try
            {
                SetStatus("正在后台填写新纪元账号…");
                _submittedProcess = page.ProcessKey;
                _result = await _controller.RunAsync(page, account, _settings.Current.NewEra.AutoLoginEnabled,
                    _gate, false, () => !_paused && !_disposed && epoch == _configuration && _settings.Current.NewEra.AutoLoginEnabled, _lifetime.Token);
                if (!_disposed && !_paused) SetStatus(_result.Message);
            }
            finally { _busy = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (NewEraLocator.IsAutomationError(e) || e is IOException or System.ComponentModel.Win32Exception)
        { _scan = null; SetStatus("新纪元页面暂时不可访问，已停止本次操作；可稍后选择“立即填充”。"); }
        finally { _ticking = false; }
    }
    private void SetStatus(string value) { if (_disposed || _status == value) return; _status = value; StatusChanged?.Invoke(value); }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _timer.Stop(); _timer.Dispose(); }
}
