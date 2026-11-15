// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

public sealed class AccountsForm : Form
{
    private readonly SettingsStore _store;
    private readonly AccountService _easAccounts, _newEraAccounts;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = UiTheme.Line, EnableHeadersVisualStyles = false,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, ColumnHeadersHeight = 42, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
    };
    private readonly Label _status = UiTheme.Text("正在等待登录页。", 9.5f);
    private readonly Label _description = UiTheme.Text("");
    private readonly CheckBox _autoLogin = new() { Text = "填写完成后自动登录", AutoSize = true, ForeColor = UiTheme.Ink };
    private readonly CheckBox _ocr = new() { Text = "本地识别验证码", AutoSize = true, ForeColor = UiTheme.Ink };
    private readonly RoundedButton _easTab, _newEraTab, _calibration, _copy;
    private readonly Dictionary<ModuleId, string> _statuses = new();
    private bool _refreshing;
    public ModuleId SelectedModule { get; private set; }
    public event Action<ModuleId, Guid?, bool>? ModuleFillRequested;
    public event Action<ModuleId>? ModuleShortcutRequested, PreferencesChanged, ModuleAccountsChanged;
    public event Action? CalibrationRequested;
    private ModuleSettings Current => _store.Current.For(SelectedModule);
    private AccountService Accounts => SelectedModule == ModuleId.Eas ? _easAccounts : _newEraAccounts;

    public AccountsForm(SettingsStore store, AccountService accounts, CredentialVault? vault = null)
    {
        _store = store; _easAccounts = accounts; _newEraAccounts = new(store, vault ?? new CredentialVault(), ModuleId.NewEra);
        SelectedModule = store.Current.SelectedModule;
        Text = "审计软件自动登录助手"; Font = new Font("Microsoft YaHei UI", 10); ForeColor = UiTheme.Ink;
        BackColor = UiTheme.Background; ClientSize = new Size(960, 690); MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 16), ColumnCount = 1, RowCount = 7, AutoScroll = true };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = UiTheme.Text("审计软件自动登录助手", 21, true); title.ForeColor = UiTheme.Blue; title.Dock = DockStyle.Fill;
        layout.Controls.Add(title, 0, 0);
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _easTab = UiTheme.Button("EAS · EasyAudit", () => SelectModule(ModuleId.Eas));
        _newEraTab = UiTheme.Button("新纪元 · 账证查询", () => SelectModule(ModuleId.NewEra));
        _easTab.MinimumSize = _newEraTab.MinimumSize = new Size(220, 48);
        tabs.Controls.AddRange([_easTab, _newEraTab]); layout.Controls.Add(tabs, 0, 1);
        _description.Dock = DockStyle.Fill; _description.ForeColor = UiTheme.Muted; layout.Controls.Add(_description, 0, 2);
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = UiTheme.Ink, SelectionBackColor = Color.FromArgb(225, 239, 255), SelectionForeColor = UiTheme.Ink, Padding = new Padding(10, 0, 10, 0) };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(242, 247, 255), ForeColor = UiTheme.Muted, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(10, 0, 10, 0) };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Default", HeaderText = "默认", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "账号名称", FillWeight = 36 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UserName", HeaderText = "用户名", FillWeight = 50 });
        _grid.RowTemplate.Height = 42; _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditAccount(); };
        var card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(12), MinimumSize = new Size(0, 125) };
        card.Controls.Add(_grid); layout.Controls.Add(card, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        actions.Controls.Add(UiTheme.Button("新增账号", () => ShowEditor(null), true));
        actions.Controls.Add(UiTheme.Button("修改", EditAccount)); actions.Controls.Add(UiTheme.Button("删除", DeleteAccount));
        actions.Controls.Add(UiTheme.Button("设为默认", SetDefault));
        actions.Controls.Add(UiTheme.Button("立即填充", () => ModuleFillRequested?.Invoke(SelectedModule, Selected?.Id ?? Current.DefaultAccountId, false)));
        _copy = UiTheme.Button("从 EAS 复制账号", CopyFromEas); actions.Controls.Add(_copy);
        layout.Controls.Add(actions, 0, 4);
        var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 4, 0, 0) };
        tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 212));
        tools.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); tools.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var preferences = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        preferences.Controls.AddRange([_autoLogin, _ocr]); tools.Controls.Add(preferences, 0, 0);
        _status.Dock = DockStyle.Fill; _status.ForeColor = UiTheme.Blue; tools.Controls.Add(_status, 0, 1);
        _calibration = UiTheme.Button("校准 EAS 登录页", () => CalibrationRequested?.Invoke()); _calibration.Dock = DockStyle.Fill;
        var shortcut = UiTheme.Button("选择软件快捷方式", () => ModuleShortcutRequested?.Invoke(SelectedModule)); shortcut.Dock = DockStyle.Fill;
        tools.Controls.Add(_calibration, 1, 0); tools.Controls.Add(shortcut, 1, 1); layout.Controls.Add(tools, 0, 5);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = UiTheme.Text("密码保存在本机 Windows 凭据管理器中。关闭此窗口后，助手继续在托盘运行。", 9); note.Dock = DockStyle.Fill; note.ForeColor = UiTheme.Muted;
        var signature = UiTheme.Text(UiTheme.Signature, 9, true); signature.Dock = DockStyle.Fill; signature.ForeColor = UiTheme.Blue;
        footer.Controls.Add(note, 0, 0); footer.Controls.Add(signature, 0, 1); layout.Controls.Add(footer, 0, 6);
        Controls.Add(layout);
        _autoLogin.CheckedChanged += (_, _) => ChangePreferences(s => s.AutoLoginEnabled = _autoLogin.Checked);
        _ocr.CheckedChanged += (_, _) => ChangePreferences(s => s.OcrEnabled = _ocr.Checked);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) => UiTheme.FitWorkingArea(this);
        RefreshAccounts();
    }

    public void SelectModule(ModuleId module)
    {
        Try(() => { _store.Update(s => s.SelectedModule = module); SelectedModule = module; RefreshAccounts(); });
    }
    private AccountProfile? Selected => _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Tag as AccountProfile : null;
    public void ShowStatus(string value) => ShowStatus(SelectedModule, value);
    public void ShowStatus(ModuleId module, string value) { _statuses[module] = value; if (SelectedModule == module) _status.Text = value; }
    public void RefreshAccounts()
    {
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
            _ocr.Visible = _calibration.Visible = SelectedModule == ModuleId.Eas; _copy.Visible = SelectedModule == ModuleId.NewEra;
            _description.Text = SelectedModule == ModuleId.Eas
                ? "在后台自动填写默认账号并识别验证码，无需停止鼠标键盘操作。\n登录页被其他窗口遮挡也可处理；最小化时等待恢复。"
                : "在后台直接填写默认账号并登录，无需停止鼠标键盘操作。\n无需验证码；遇到异地登录提示时，最多再补填重试一次。";
            _status.Text = _statuses.GetValueOrDefault(SelectedModule, Current.DefaultAccount is null ? "请先保存本模块账号并设置默认账号。" : "正在等待登录页。两个模块同时在托盘监听。");
        }
        finally { _refreshing = false; }
    }
    private void ChangePreferences(Action<ModuleSettings> action)
    {
        if (_refreshing) return;
        Try(() => { _store.Update(s => action(s.For(SelectedModule))); PreferencesChanged?.Invoke(SelectedModule); }); RefreshAccounts();
    }
    private void EditAccount() { if (Selected is { } selected) ShowEditor(selected); }
    private void ShowEditor(AccountProfile? account)
    {
        using var editor = new AccountEditForm(Accounts, account);
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
        using var picker = new CopyAccountForm(_store.Current.Eas.Accounts);
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
    private readonly TextBox _label = new() { Dock = DockStyle.Fill, MaxLength = 80, BorderStyle = BorderStyle.None };
    private readonly TextBox _user = new() { Dock = DockStyle.Fill, MaxLength = 256, BorderStyle = BorderStyle.None };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 1280, BorderStyle = BorderStyle.None };
    public AccountEditForm(AccountService service, AccountProfile? account)
    {
        Text = account is null ? "新增账号" : "修改账号"; Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = UiTheme.Background; ForeColor = UiTheme.Ink; ClientSize = new Size(480, 440); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 9, AutoScroll = true };
        foreach (var pair in new[] { ("账号名称", _label), ("用户名", _user), ("密码", _password) })
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.Controls.Add(UiTheme.Text(pair.Item1));
            var field = new RoundedPanel { Dock = DockStyle.Fill, Radius = 10, Padding = new Padding(12, 9, 12, 7), Margin = new Padding(0, 0, 0, 8) };
            pair.Item2.BackColor = Color.White; field.Controls.Add(pair.Item2); layout.Controls.Add(field);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        var note = UiTheme.Text(account is null ? "密码仅保存在本机当前 Windows 用户的凭据库。" : "密码留空则保留已保存的密码。", 9); note.Dock = DockStyle.Fill; note.ForeColor = UiTheme.Muted;
        layout.Controls.Add(note);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        var save = UiTheme.Button("保存", () =>
        {
            try { service.Save(account?.Id ?? Guid.NewGuid(), _label.Text, _user.Text, _password.Text); _password.Clear(); DialogResult = DialogResult.OK; Close(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
            { MessageBox.Show(this, e.Message, "账号未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }, true);
        var cancel = UiTheme.Button("取消", () => { DialogResult = DialogResult.Cancel; Close(); }); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([save, cancel]); layout.Controls.Add(actions); Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        if (account is not null) { _label.Text = account.Label; _user.Text = account.UserName; }
        FormClosed += (_, _) => _password.Clear(); Shown += (_, _) => UiTheme.FitWorkingArea(this);
    }
}

internal sealed class CopyAccountForm : Form
{
    private readonly ComboBox _accounts = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Label" };
    public AccountProfile? Selected => _accounts.SelectedItem as AccountProfile;
    public CopyAccountForm(IEnumerable<AccountProfile> accounts)
    {
        Text = "复制 EAS 账号到新纪元"; Font = new Font("Microsoft YaHei UI", 10); BackColor = UiTheme.Background;
        ClientSize = new Size(500, 230); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var text = UiTheme.Text("请选择要复制的账号。复制后分别保存密码，\n修改或删除一边不会影响另一边。", 10); text.Dock = DockStyle.Fill;
        layout.Controls.Add(text); foreach (var account in accounts) _accounts.Items.Add(account);
        _accounts.Format += (_, e) => { if (e.ListItem is AccountProfile account) e.Value = account.Label + "（" + account.UserName + "）"; };
        if (_accounts.Items.Count > 0) _accounts.SelectedIndex = 0; layout.Controls.Add(_accounts);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var confirm = UiTheme.Button("复制", () => { DialogResult = DialogResult.OK; Close(); }, true);
        var cancel = UiTheme.Button("取消", () => { DialogResult = DialogResult.Cancel; Close(); });
        actions.Controls.AddRange([confirm, cancel]); layout.Controls.Add(actions); Controls.Add(layout); AcceptButton = confirm; CancelButton = cancel;
    }
}
