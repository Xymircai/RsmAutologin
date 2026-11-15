// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

public sealed partial class AccountsForm
{
    private readonly UiLayoutStore _layouts;
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _page = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 5, Padding = new Padding(20, 16, 20, 16) };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(860, 360), SplitterWidth = 8 };
    private readonly TableLayoutPanel _details = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
    private readonly List<FlowLayoutPanel> _flows = [];
    private bool _layingOut, _layoutReady;
    private double _splitRatio = .52;

    private FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        flow.Controls.AddRange(controls); _flows.Add(flow); return flow;
    }
    private void InitializeLayout()
    {
        Text = "审计软件自动登录助手"; UiTheme.ConfigureWindow(this, new Size(940, 700), new Size(480, 320));
        _page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) _page.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Absolute : SizeType.AutoSize, i == 3 ? 360 : 0));
        var title = UiTheme.WrapText(Text, 21, true); title.ForeColor = UiTheme.Blue; _page.Controls.Add(title, 0, 0);
        _easTab = UiTheme.Button("EAS · EasyAudit", () => SelectModule(ModuleId.Eas));
        _newEraTab = UiTheme.Button("新纪元 · 账证查询", () => SelectModule(ModuleId.NewEra));
        _page.Controls.Add(Flow(_easTab, _newEraTab), 0, 1);
        _description.ForeColor = UiTheme.Muted; _page.Controls.Add(_description, 0, 2);
        _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = UiTheme.Ink, SelectionBackColor = Color.FromArgb(225, 239, 255), SelectionForeColor = UiTheme.Ink, Padding = new Padding(10, 6, 10, 6) };
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(242, 247, 255), ForeColor = UiTheme.Muted, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(10, 6, 10, 6) };
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; _grid.AllowUserToResizeColumns = true;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Default", HeaderText = "默认", FillWeight = 14, MinimumWidth = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "账号名称", FillWeight = 36, MinimumWidth = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UserName", HeaderText = "用户名", FillWeight = 50, MinimumWidth = 100 });
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditAccount(); };
        var card = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = Padding.Empty }; card.Controls.Add(_grid);
        _split.BackColor = UiTheme.Line; _split.Panel1.BackColor = _split.Panel2.BackColor = UiTheme.Background;
        _split.Panel1.Controls.Add(card);
        _copy = UiTheme.Button("从 EAS 复制账号", CopyFromEas);
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _details.Controls.Add(Flow(UiTheme.Button("新增账号", () => ShowEditor(null), true), UiTheme.Button("修改", EditAccount),
            UiTheme.Button("删除", DeleteAccount), UiTheme.Button("设为默认", SetDefault),
            UiTheme.Button("立即填充", () => ModuleFillRequested?.Invoke(SelectedModule, Selected?.Id ?? Current.DefaultAccountId, false)), _copy));
        _details.Controls.Add(Flow(_autoLogin, _ocr));
        _status.ForeColor = UiTheme.Blue; _details.Controls.Add(_status);
        _calibration = UiTheme.Button("校准 EAS 登录页", () => CalibrationRequested?.Invoke());
        _details.Controls.Add(Flow(_calibration, UiTheme.Button("环境自检", async () => await RunSelfTest()),
            UiTheme.Button("使用说明与许可", () => { using var info = new InformationForm(_layouts); info.ShowDialog(this); }),
            UiTheme.Button("恢复默认布局", ResetLayout)));
        foreach (Control unused in _details.Controls) _details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _split.Panel2.AutoScroll = true; _split.Panel2.Controls.Add(_details); _page.Controls.Add(_split, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 12, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = UiTheme.WrapText("密码保存在本机 Windows 凭据管理器中。关闭此窗口后，助手继续在托盘运行。", 9); note.ForeColor = UiTheme.Muted;
        var signature = UiTheme.WrapText(UiTheme.Signature, 9, true); signature.ForeColor = UiTheme.Blue;
        footer.Controls.Add(note, 0, 0); footer.Controls.Add(signature, 0, 1); _page.Controls.Add(footer, 0, 4);
        _viewport.Controls.Add(_page); Controls.Add(_viewport);
        _viewport.SizeChanged += (_, _) => LayoutWorkspace(); _page.Layout += (_, _) => LayoutWorkspace();
        _split.SplitterMoved += (_, _) => { if (!_layingOut && _layoutReady) { _splitRatio = _split.SplitterDistance / (double)Math.Max(1, _split.Height); SavePartitions(); } };
        _grid.ColumnWidthChanged += (_, _) => { if (_layoutReady && !_layingOut) SavePartitions(); };
        _layouts.Attach(this, "accounts", state =>
        {
            _splitRatio = double.IsFinite(state.SplitRatio) ? Math.Clamp(state.SplitRatio, .2, .8) : .52;
            if (state.ColumnsDip is { Length: 3 } widths && widths.All(w => w is > 20 and < 5000))
                for (var i = 0; i < 3; i++) _grid.Columns[i].FillWeight = widths[i];
            LayoutWorkspace();
        }, CapturePartitions);
        Shown += (_, _) => { _layoutReady = true; LayoutWorkspace(); };
        DpiChanged += (_, _) => BeginInvoke(LayoutWorkspace);
    }
    private void LayoutWorkspace()
    {
        if (_layingOut || IsDisposed || _page.Controls.Count < 5) return;
        _layingOut = true;
        try
        {
            foreach (var flow in _flows)
            {
                var width = Math.Max(120, (flow.Parent?.ClientSize.Width ?? _page.Width) - (flow.Parent?.Padding.Horizontal ?? 0));
                if (flow.MaximumSize.Width != width) flow.MaximumSize = new Size(width, 0);
            }
            var otherHeight = _page.Padding.Vertical;
            foreach (Control child in _page.Controls) if (child != _split) otherHeight += child.Height + child.Margin.Vertical;
            var scale = DeviceDpi / 96d;
            var height = Math.Max((int)(250 * scale), _viewport.ClientSize.Height - otherHeight - 2);
            if (Math.Abs(_page.RowStyles[3].Height - height) > 1) _page.RowStyles[3].Height = height;
            _split.Panel1MinSize = (int)(80 * scale); _split.Panel2MinSize = (int)(96 * scale);
            var maximum = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
            if (maximum >= _split.Panel1MinSize) _split.SplitterDistance = Math.Clamp((int)(_split.Height * _splitRatio), _split.Panel1MinSize, maximum);
        }
        finally { _layingOut = false; }
    }
    private void CapturePartitions(WindowLayout state)
    {
        state.SplitRatio = _split.SplitterDistance / (double)Math.Max(1, _split.Height);
        state.ColumnsDip = _grid.Columns.Cast<DataGridViewColumn>().Select(c => (int)Math.Round(c.Width * 96d / DeviceDpi)).ToArray();
    }
    private void SavePartitions() { CapturePartitions(_layouts.Get("accounts")); _layouts.Save(); }
    private void ResetLayout()
    {
        _layouts.ResetAll(); _splitRatio = .52; WindowState = FormWindowState.Normal;
        var weights = new[] { 14f, 36f, 50f }; for (var i = 0; i < 3; i++) _grid.Columns[i].FillWeight = weights[i];
        ClientSize = new Size((int)(940 * DeviceDpi / 96d), (int)(700 * DeviceDpi / 96d));
        UiTheme.FitWorkingArea(this); LayoutWorkspace();
    }
    private async Task RunSelfTest()
    {
        ShowStatus("正在检查内置模型与后台捕获环境，使用离线模拟窗口…");
        var report = await EnvironmentSelfTest.CheckAsync(this);
        ShowStatus(report.Success ? "环境自检通过。请打开 EAS／新纪元登录页。" : report.Message);
        using var info = new InformationForm(_layouts, "环境自检结果", System.Text.Json.JsonSerializer.Serialize(report, SettingsStore.JsonOptions));
        info.ShowDialog(this);
    }
}
