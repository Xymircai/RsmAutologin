// SPDX-License-Identifier: MPL-2.0
using System.Runtime.InteropServices;

namespace EasLoginHelper;

public sealed partial class AccountsForm : Form
{
    private readonly SettingsStore _store;
    private readonly AccountService _easAccounts, _newEraAccounts;
    private readonly WebsitePanel _websitePanel;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = UiTheme.Line, EnableHeadersVisualStyles = false,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, ColumnHeadersHeight = 42, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
    };
    private readonly Label _status = UiTheme.WrapText("正在等待登录页。", 9.5f);
    private readonly Label _description = UiTheme.WrapText("");
    private readonly CheckBox _autoLogin = new() { Text = "填写完成后自动登录", AutoSize = true, ForeColor = UiTheme.Ink };
    private readonly CheckBox _ocr = new() { Text = "本地识别验证码", AutoSize = true, ForeColor = UiTheme.Ink };
    private RoundedButton _easTab = null!, _newEraTab = null!, _websiteTab = null!, _startup = null!, _copy = null!;
    private readonly Dictionary<ModuleId, string> _statuses = new();
    private bool _refreshing, _showingWebsites;
    public ModuleId SelectedModule { get; private set; }
    public event Action<ModuleId, Guid?, bool>? ModuleFillRequested;
    public event Action<ModuleId>? PreferencesChanged, ModuleAccountsChanged;
    private ModuleSettings Current => _store.Current.For(SelectedModule);
    private AccountService Accounts => SelectedModule == ModuleId.Eas ? _easAccounts : _newEraAccounts;

    internal AccountsForm(SettingsStore store, AccountService accounts, WebsiteWorkerClient worker, CredentialVault? vault = null)
    {
        _store = store; _easAccounts = accounts; _newEraAccounts = new(store, vault ?? new CredentialVault(), ModuleId.NewEra);
        _websitePanel = new WebsitePanel(worker) { Visible = false };
        _layouts = UiLayoutStore.ForSettings(store); SelectedModule = store.Current.SelectedModule;
        InitializeLayout();
        Activated += (_, _) => RefreshStartupButton();
        _autoLogin.CheckedChanged += (_, _) => ChangePreferences(s => s.AutoLoginEnabled = _autoLogin.Checked);
        _ocr.CheckedChanged += (_, _) => ChangePreferences(s => s.OcrEnabled = _ocr.Checked);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        RefreshAccounts();
    }
    public void SelectModule(ModuleId module)
    {
        Try(() =>
        {
            _store.Update(s => s.SelectedModule = module);
            SelectedModule = module;
            SetWebsiteView(false);
            RefreshAccounts();
        });
    }
    internal void SelectWebsites()
    {
        SetWebsiteView(true);
        _ = _websitePanel.EnsureLoadedAsync();
    }
    internal void ShowWebsiteNotice(WebsiteNotice notice) => _websitePanel.SetNotice(notice);
    private void SetWebsiteView(bool show)
    {
        _showingWebsites = show;
        _split.Visible = !show;
        _websitePanel.Visible = show;
        _websiteTab.Primary = show;
        _easTab.Primary = !show && SelectedModule == ModuleId.Eas;
        _newEraTab.Primary = !show && SelectedModule == ModuleId.NewEra;
        if (show) _description.Text = "管理网站的默认账号，登录页会在 Windows 默认浏览器打开。";
        LayoutWorkspace();
    }
    private AccountProfile? Selected => _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Tag as AccountProfile : null;
    public void ShowStatus(string value) => ShowStatus(SelectedModule, value);
    public void ShowStatus(ModuleId module, string value) { _statuses[module] = value; if (SelectedModule == module) _status.Text = value; }
    public void RefreshAccounts()
    {
        if (_showingWebsites) return;
        _refreshing = true;
        try
        {
            var selected = Selected?.Id; _grid.Rows.Clear();
            foreach (var account in Current.Accounts.OrderByDescending(a => a.Id == Current.DefaultAccountId).ThenBy(a => a.Label))
            {
                var index = _grid.Rows.Add(account.Id == Current.DefaultAccountId ? "●" : "", account.Label, account.UserName);
                _grid.Rows[index].Tag = account; if (account.Id == selected) _grid.Rows[index].Selected = true;
            }
            _easTab.Primary = SelectedModule == ModuleId.Eas; _newEraTab.Primary = SelectedModule == ModuleId.NewEra;
            _autoLogin.Checked = Current.AutoLoginEnabled; _ocr.Checked = Current.OcrEnabled;
            _ocr.Visible = SelectedModule == ModuleId.Eas; _copy.Visible = SelectedModule == ModuleId.NewEra;
            _description.Text = SelectedModule == ModuleId.Eas
                ? "EasyAudit 与审计工具箱共用本模块的默认账号和设置，后台填写并识别验证码。\n无需停止鼠标键盘操作；页面被遮挡也可处理，最小化时等待恢复。"
                : "在后台直接填写默认账号并登录，无需停止鼠标键盘操作。\n无需验证码；遇到异地登录提示时，最多再补填重试一次。";
            _status.Text = _statuses.GetValueOrDefault(SelectedModule, Current.DefaultAccount is null ? "请先保存本模块账号并设置默认账号。" : "正在等待登录页。两个模块同时在托盘监听。");
        }
        finally { _refreshing = false; }
    }
    private void RefreshStartupButton()
    {
        try { SetStartupButton(StartupTask.IsEnabled()); }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        { SetStartupButton(null); }
    }
    private void ToggleStartup()
    {
        try { SetStartupButton(StartupTask.Toggle()); }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            RefreshStartupButton();
            MessageBox.Show(this, "无法切换开机自启。请检查 Windows 任务计划程序。\n" + e.Message,
                "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    private void SetStartupButton(bool? enabled)
    {
        _startup.Text = enabled switch { true => "开机自启：开", false => "开机自启：关", null => "开机自启：未知" };
        _startup.Primary = enabled == true;
    }
    private void ChangePreferences(Action<ModuleSettings> action)
    {
        if (_refreshing) return;
        Try(() => { _store.Update(s => action(s.For(SelectedModule))); PreferencesChanged?.Invoke(SelectedModule); }); RefreshAccounts();
    }
    private void EditAccount() { if (Selected is { } selected) ShowEditor(selected); }
    private void ShowEditor(AccountProfile? account)
    {
        using var editor = new AccountEditForm(Accounts, account, _layouts);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        RefreshAccounts(); ModuleAccountsChanged?.Invoke(SelectedModule);
    }
    private void DeleteAccount()
    {
        if (Selected is not { } selected || MessageBox.Show(this, "删除本模块的此账号及保存的密码？", "删除账号", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Try(() => { Accounts.Delete(selected.Id); RefreshAccounts(); ModuleAccountsChanged?.Invoke(SelectedModule); });
    }
    private void SetDefault()
    {
        if (Selected is not { } selected) return;
        Try(() => { _store.Update(s => s.For(SelectedModule).DefaultAccountId = selected.Id); RefreshAccounts(); ModuleAccountsChanged?.Invoke(SelectedModule); });
    }
    private void CopyFromEas()
    {
        if (_store.Current.Eas.Accounts.Count == 0) { MessageBox.Show(this, "EAS 模块尚未保存账号。", "复制账号"); return; }
        using var picker = new CopyAccountForm(_store.Current.Eas.Accounts, _layouts);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } account) return;
        Try(() => { _newEraAccounts.CopyFrom(ModuleId.Eas, account.Id); RefreshAccounts(); ModuleAccountsChanged?.Invoke(ModuleId.NewEra); });
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        { MessageBox.Show(this, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}

public sealed class AccountEditForm : Form
{
    private readonly AccountTextBox _label = new() { MaxLength = 80 };
    private readonly AccountTextBox _user = new() { MaxLength = 256 };
    private readonly AccountTextBox _password = new() { UseSystemPasswordChar = true, MaxLength = 1280 };
    public AccountEditForm(AccountService service, AccountProfile? account, UiLayoutStore? layouts = null)
        : this((id, label, user, password) =>
        {
            service.Save(id, label, user, password);
            return Task.CompletedTask;
        }, account, layouts)
    { }

    internal AccountEditForm(Func<Guid, string, string, string, Task> saveAccount, AccountProfile? account,
        UiLayoutStore? layouts = null)
    {
        Text = account is null ? "新增账号" : "修改账号";
        UiTheme.ConfigureWindow(this, new Size(480, 460), new Size(300, 260), true);
        var page = UiTheme.DialogPage(this);
        foreach (var pair in new[] { ("账号名称", _label), ("用户名", _user), ("密码", _password) })
        {
            page.Controls.Add(UiTheme.WrapText(pair.Item1));
            page.Controls.Add(UiTheme.AccountField(pair.Item2));
        }
        var note = UiTheme.WrapText(account is null ? "密码仅保存在本机当前 Windows 用户的凭据库。" : "密码留空则保留已保存的密码。", 9);
        note.ForeColor = UiTheme.Muted; page.Controls.Add(note);
        var saving = false;
        var save = UiTheme.Button("保存", async () =>
        {
            if (saving) return;
            saving = true;
            try
            {
                await saveAccount(account?.Id ?? Guid.NewGuid(), _label.Text, _user.Text, _password.Text);
                saving = false;
                if (IsDisposed) return;
                _password.Clear();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
            { if (!IsDisposed) MessageBox.Show(this, e.Message, "账号未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { saving = false; }
        }, true);
        var cancel = UiTheme.Button("取消", () => { DialogResult = DialogResult.Cancel; Close(); }); cancel.DialogResult = DialogResult.Cancel;
        page.Controls.Add(UiTheme.DialogActions(save, cancel)); AcceptButton = save; CancelButton = cancel;
        if (account is not null) { _label.Text = account.Label; _user.Text = account.UserName; }
        (layouts ?? new UiLayoutStore()).Attach(this, "account-editor");
        FormClosing += (_, e) => { if (saving && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        FormClosed += (_, _) => _password.Clear();
    }
}

internal sealed class CopyAccountForm : Form
{
    private readonly ComboBox _accounts = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Label" };
    public AccountProfile? Selected => _accounts.SelectedItem as AccountProfile;
    public CopyAccountForm(IEnumerable<AccountProfile> accounts, UiLayoutStore? layouts = null)
    {
        Text = "复制 EAS 账号到新纪元"; UiTheme.ConfigureWindow(this, new Size(500, 260), new Size(300, 200), true);
        var page = UiTheme.DialogPage(this);
        page.Controls.Add(UiTheme.WrapText("请选择要复制的账号。复制后分别保存密码，修改或删除一边不会影响另一边。"));
        foreach (var account in accounts) _accounts.Items.Add(account);
        _accounts.Format += (_, e) => { if (e.ListItem is AccountProfile account) e.Value = account.Label + "（" + account.UserName + "）"; };
        if (_accounts.Items.Count > 0) _accounts.SelectedIndex = 0; page.Controls.Add(_accounts);
        var confirm = UiTheme.Button("复制", () => { DialogResult = DialogResult.OK; Close(); }, true);
        var cancel = UiTheme.Button("取消", () => { DialogResult = DialogResult.Cancel; Close(); });
        page.Controls.Add(UiTheme.DialogActions(confirm, cancel)); AcceptButton = confirm; CancelButton = cancel;
        (layouts ?? new UiLayoutStore()).Attach(this, "copy-account");
    }
}
