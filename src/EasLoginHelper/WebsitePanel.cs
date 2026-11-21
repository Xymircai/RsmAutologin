// SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;

namespace EasLoginHelper;

internal sealed class WebsitePanel : Panel
{
    private readonly WebsiteWorkerClient _worker;
    private WebsiteAccountSettings _settings = new();
    private readonly Dictionary<string, WebsiteNotice> _notices = new();
    private readonly WebsiteSelector _sites = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DataGridView _accounts = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
    };
    private readonly CheckBox _autoLogin = new() { Text = "填写完成后自动登录", AutoSize = true };
    private readonly WrappingLabel _help = UiTheme.WrapText("");
    private readonly WrappingLabel _status = UiTheme.WrapText("正在读取网站账号。", 9.5f);
    private readonly RoundedButton _edit, _delete, _makeDefault;
    private readonly RoundedButton _launch, _add, _connect;
    private bool _refreshing, _busy, _loaded;

    private Website SelectedWebsite => Website.All[_sites.SelectedIndex];
    private AccountProfile? Selection => _accounts.SelectedRows.Count == 1 ? _accounts.SelectedRows[0].Tag as AccountProfile : null;

    internal WebsitePanel(WebsiteWorkerClient worker)
    {
        _worker = worker;
        Dock = DockStyle.Fill;
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var siteActions = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        siteActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        siteActions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        siteActions.Controls.Add(_sites, 0, 0);
        _launch = UiTheme.Button("打开网站", async () => await RequestAsync("open"), true);
        _launch.Margin = new Padding(8, 0, 0, 0);
        siteActions.Controls.Add(_launch, 1, 0);
        page.Controls.Add(siteActions, 0, 0);

        _accounts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "默认", FillWeight = 15, MinimumWidth = 54 });
        _accounts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "账号名称", FillWeight = 35, MinimumWidth = 80 });
        _accounts.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "用户名", FillWeight = 50, MinimumWidth = 100 });
        page.Controls.Add(_accounts, 0, 1);

        _edit = UiTheme.Button("修改", () => Edit(Selection));
        _delete = UiTheme.Button("删除", async () => await DeleteAsync());
        _makeDefault = UiTheme.Button("设为默认", async () =>
        { if (Selection is { } profile) await RequestAsync("default", profile.Id); });
        _add = UiTheme.Button("新增账号", () => Edit(null));
        _connect = UiTheme.Button("连接浏览器", async () => await ConnectBrowserAsync());
        var actions = UiTheme.DialogActions(_add, _edit, _delete, _makeDefault, _connect);
        page.Controls.Add(actions, 0, 2);
        page.Controls.Add(_autoLogin, 0, 3);
        _help.ForeColor = UiTheme.Muted;
        page.Controls.Add(_help, 0, 4);
        _status.ForeColor = UiTheme.Blue;
        page.Controls.Add(_status, 0, 5);
        Controls.Add(page);

        _sites.SelectedIndexChanged += async (_, _) =>
        {
            _settings = new WebsiteAccountSettings();
            _loaded = false;
            RefreshAccounts();
            await RequestAsync("get");
        };
        _accounts.SelectionChanged += (_, _) => RefreshActions();
        _accounts.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && Selection is { } account) Edit(account); };
        _autoLogin.CheckedChanged += async (_, _) =>
        {
            if (!_refreshing && _loaded) await RequestAsync("auto", enabled: _autoLogin.Checked);
        };
        RefreshAccounts();
    }

    internal async Task EnsureLoadedAsync()
    {
        if (!_loaded && !_busy) await RequestAsync("get");
    }

    internal void SetNotice(WebsiteNotice notice)
    {
        _notices[notice.Site] = notice;
        if (SelectedWebsite.Key == notice.Site) _status.Text = NoticeText(notice);
    }

    internal static string NoticeText(WebsiteNotice notice) => (notice.Stage, notice.Reason) switch
    {
        (WebsiteStage.Opened, _) => "已打开网站，正在等待扩展。",
        (WebsiteStage.Filled, WebsiteReason.ManualCaptcha) => "已填账号密码，请在网页手输验证码。",
        (WebsiteStage.Filled, WebsiteReason.ManualSubmit) => "已填写，请在网页点击登录。",
        (WebsiteStage.Filled, _) => "正在填写登录表单。",
        (WebsiteStage.Submitted, _) => "已提交登录，正在确认结果。",
        (WebsiteStage.Success, _) => "登录成功。",
        (WebsiteStage.Failed, _) => "登录失败，请查看网页登录提示。",
        (WebsiteStage.NeedsAction, WebsiteReason.MissingAccount) => "请先设置默认账号，或在网页手动登录。",
        (WebsiteStage.NeedsAction, WebsiteReason.BrowserConnection) => "请连接默认浏览器并加载新版扩展，或在网页手动登录。",
        (WebsiteStage.NeedsAction, WebsiteReason.ExtraVerification) => "需要操作，请在网页完成额外验证。",
        (WebsiteStage.NeedsAction, _) => "登录表单需要手动处理，请查看网页。",
        (WebsiteStage.Unconfirmed, WebsiteReason.TabClosed) => "标签页已关闭，登录结果未确认。",
        _ => "登录结果未确认，请查看网页。"
    };

    private void RefreshAccounts(Guid? selected = null)
    {
        _refreshing = true;
        try
        {
            var settings = _settings;
            _accounts.Rows.Clear();
            foreach (var profile in settings.Accounts.OrderByDescending(a => a.Id == settings.DefaultAccountId).ThenBy(a => a.Label))
            {
                var index = _accounts.Rows.Add(profile.Id == settings.DefaultAccountId ? "●" : "", profile.Label, profile.UserName);
                _accounts.Rows[index].Tag = profile;
                if (profile.Id == selected) _accounts.CurrentCell = _accounts.Rows[index].Cells[0];
            }
            _autoLogin.Text = SelectedWebsite.RequiresManualCaptcha ? "手输验证码后自动提交" : "填写完成后自动登录";
            _autoLogin.Checked = SelectedWebsite.RequiresManualCaptcha ? settings.SubmitAfterManualCaptcha : settings.AutoLoginEnabled;
            _help.Text = SelectedWebsite.RequiresManualCaptcha
                ? "扩展填写账号密码。请在网页手输验证码。"
                : SelectedWebsite == Website.Mail
                    ? "邮箱支持用户名或完整的 @rccpa.com.cn 地址。自动填写需要配套扩展。"
                    : "在默认浏览器打开网站。自动填写需要配套扩展。";
            _status.Text = _notices.TryGetValue(SelectedWebsite.Key, out var notice) ? NoticeText(notice) :
                _loaded ? "请选择默认账号并打开网站。" : "正在读取网站账号。";
            RefreshActions();
        }
        finally { _refreshing = false; }
    }

    private void RefreshActions()
    {
        var profile = Selection;
        _edit.Enabled = _delete.Enabled = !_busy && profile is not null;
        _makeDefault.Enabled = !_busy && profile is not null && profile.Id != _settings.DefaultAccountId;
        _sites.Enabled = _launch.Enabled = _add.Enabled = _connect.Enabled = !_busy;
        _autoLogin.Enabled = _loaded && !_busy;
    }

    private void Edit(AccountProfile? profile)
    {
        var site = SelectedWebsite;
        using var editor = new AccountEditForm(async (id, label, user, password) =>
        {
            _settings = await _worker.SendAsync(new WebsiteRequest("save", site.Key, id, label, user, password))
                ?? throw new IOException("网站账号未保存。");
            _loaded = true;
        }, profile);
        if (editor.ShowDialog(FindForm()) == DialogResult.OK && !IsDisposed) RefreshAccounts(profile?.Id);
    }

    private async Task DeleteAsync()
    {
        if (Selection is not { } profile ||
            MessageBox.Show(this, "删除此站点的账号及保存的密码？", "删除账号", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RequestAsync("delete", profile.Id);
    }

    private async Task ConnectBrowserAsync()
    {
        var extensionPath = Path.Combine(AppContext.BaseDirectory, "BrowserExtension");
        if (!File.Exists(Path.Combine(extensionPath, "manifest.json")))
        {
            MessageBox.Show(this, "请把完整的 BrowserExtension 文件夹放在本 EXE 旁边，然后再点击「连接浏览器」。",
                "连接浏览器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (await RequestAsync("connect") && !IsDisposed)
        {
            MessageBox.Show(this, "已为当前 Windows 用户登记 Edge 和 Chrome 连接。\n\n" +
                "在 Edge 地址栏输入 edge://extensions/（Chrome 为 chrome://extensions/）。开启「开发人员模式」，" +
                "点「加载解压缩的扩展」，选择下面这个包含 manifest.json 的文件夹：\n" + extensionPath,
                "连接浏览器", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task<bool> RequestAsync(string kind, Guid id = default, bool enabled = false)
    {
        if (_busy || IsDisposed) return false;
        var selected = Selection?.Id;
        _busy = true;
        RefreshActions();
        string? error = null;
        try
        {
            if (await _worker.SendAsync(new WebsiteRequest(kind, SelectedWebsite.Key, id, Enabled: enabled)) is { } settings)
            {
                _settings = settings;
                _loaded = true;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        { error = e.Message; return false; }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                RefreshAccounts(selected);
                if (error is not null) _status.Text = error;
            }
        }
    }
}
