// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

public sealed partial class AccountsForm
{
    private const string MainLayoutKey = "accounts-compact";
    private readonly UiLayoutStore _layouts;
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel _workspace = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly TableLayoutPanel _page = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 5, Padding = new Padding(20, 16, 20, 16) };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
        Size = new Size(680, 224), SplitterWidth = 8, Margin = Padding.Empty };
    private readonly TableLayoutPanel _details = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
    private readonly List<FlowLayoutPanel> _flows = [];
    private bool _layingOut, _layoutReady, _splitterMoving;
    private double? _splitRatio;

    private FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        flow.Controls.AddRange(controls); _flows.Add(flow); return flow;
    }
    private void InitializeLayout()
    {
        var preferredSize = new Size(720, 410);
        Text = "审计软件自动登录助手"; UiTheme.ConfigureWindow(this, preferredSize, new Size(440, 300));
        Load += (_, _) => ClientSize = new Size(ToDevicePixels(preferredSize.Width), ToDevicePixels(preferredSize.Height));
        _page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) _page.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Absolute : SizeType.AutoSize, i == 3 ? 224 : 0));
        var title = UiTheme.WrapText(Text, 21, true); title.ForeColor = UiTheme.Blue; _page.Controls.Add(title, 0, 0);
        _easTab = UiTheme.Button("EAS · EasyAudit / 审计工具箱", () => SelectModule(ModuleId.Eas));
        _newEraTab = UiTheme.Button("新纪元 · 账证查询", () => SelectModule(ModuleId.NewEra));
        _websiteTab = UiTheme.Button(Website.ModuleTitle, SelectWebsites);
        _page.Controls.Add(Flow(_easTab, _newEraTab, _websiteTab), 0, 1);
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
        RoundedButton ActionButton(string text, Action click, bool primary = false)
        {
            var button = UiTheme.Button(text, click, primary);
            button.Padding = new Padding(10, 7, 10, 7);
            button.Margin = new Padding(0, 0, 8, 6);
            return button;
        }
        _copy = ActionButton("从 EAS 复制账号", CopyFromEas);
        _startup = ActionButton("开机自启", ToggleStartup);
        _startup.AccessibleDescription = "点击切换。开启后，登录当前 Windows 账户时在托盘启动助手；把 EXE 移到别处后，只要打开新位置的助手就会自动更新启动路径。";
        var fill = ActionButton("立即填充", () => ModuleFillRequested?.Invoke(SelectedModule, Selected?.Id ?? Current.DefaultAccountId, false));
        // The two controls wrap as a unit when the window becomes narrow.
        var fillAndStartup = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty };
        fillAndStartup.Controls.AddRange([fill, _startup]);
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _details.Controls.Add(Flow(ActionButton("新增账号", () => ShowEditor(null), true), ActionButton("修改", EditAccount),
            ActionButton("删除", DeleteAccount), ActionButton("设为默认", SetDefault), fillAndStartup, _copy));
        _details.Controls.Add(Flow(_autoLogin, _ocr));
        _status.ForeColor = UiTheme.Blue; _details.Controls.Add(_status);
        foreach (Control unused in _details.Controls) _details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _split.Panel2.AutoScroll = true; _split.Panel2.Controls.Add(_details);
        _workspace.Controls.Add(_split);
        _workspace.Controls.Add(_websitePanel);
        _page.Controls.Add(_workspace, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var signature = UiTheme.WrapText(UiTheme.Signature, 9, true); signature.ForeColor = UiTheme.Blue;
        footer.Controls.Add(signature, 0, 0); _page.Controls.Add(footer, 0, 4);
        _viewport.Controls.Add(_page); Controls.Add(_viewport);
        _viewport.SizeChanged += (_, _) => LayoutWorkspace(); _page.Layout += (_, _) => LayoutWorkspace();
        _details.SizeChanged += (_, _) => LayoutWorkspace();
        _split.SplitterMoving += (_, _) => _splitterMoving = _layoutReady && !_layingOut;
        _split.SplitterMoved += (_, _) =>
        {
            if (!_splitterMoving) return;
            _splitterMoving = false;
            _splitRatio = _split.SplitterDistance / (double)_split.Height;
            SavePartitions();
            LayoutWorkspace();
        };
        _split.MouseCaptureChanged += (_, _) => FinishCancelledSplitterMove();
        _split.KeyUp += (_, _) => FinishCancelledSplitterMove();
        _grid.ColumnWidthChanged += (_, _) => { if (_layoutReady && !_layingOut) SavePartitions(); };
        _layouts.Attach(this, MainLayoutKey, state =>
        {
            _splitRatio = state.WidthDip > 0 && state.SplitRatio is > 0 and < 1 ? state.SplitRatio : null;
            var columns = state.ColumnsDip is { Length: 3 } ? state.ColumnsDip : _layouts.Get("accounts").ColumnsDip;
            if (columns is { Length: 3 } widths && widths.All(w => w is > 20 and < 5000))
                for (var i = 0; i < 3; i++) _grid.Columns[i].FillWeight = widths[i];
            LayoutWorkspace();
        }, CapturePartitions);
        Shown += (_, _) => { _layoutReady = true; LayoutWorkspace(); };
        DpiChanged += (_, _) => BeginInvoke(LayoutWorkspace);
    }
    private void LayoutWorkspace()
    {
        if (_layingOut || _splitterMoving || IsDisposed || _page.Controls.Count < 5) return;
        _layingOut = true;
        try
        {
            foreach (var flow in _flows)
            {
                var width = Math.Max(120, (flow.Parent?.ClientSize.Width ?? _page.Width) - (flow.Parent?.Padding.Horizontal ?? 0));
                if (flow.MaximumSize.Width != width) flow.MaximumSize = new Size(width, 0);
            }
            var otherHeight = _page.Padding.Vertical;
            foreach (Control child in _page.Controls) if (child != _workspace) otherHeight += child.Height + child.Margin.Vertical;
            if (_showingWebsites)
            {
                var siteHeight = Math.Max(ToDevicePixels(270), _viewport.ClientSize.Height - otherHeight - 2);
                if (Math.Abs(_page.RowStyles[3].Height - siteHeight) > 1) _page.RowStyles[3].Height = siteHeight;
                return;
            }
            var gridMinimum = ToDevicePixels(86);
            var actionsMinimum = ToDevicePixels(65);
            var actionsHeight = Math.Max(actionsMinimum, _details.Height);
            var height = Math.Max(gridMinimum + _split.SplitterWidth + actionsHeight, _viewport.ClientSize.Height - otherHeight - 2);
            if (Math.Abs(_page.RowStyles[3].Height - height) > 1) _page.RowStyles[3].Height = height;
            if (_split.Height < gridMinimum + actionsMinimum + _split.SplitterWidth) return;
            _split.Panel1MinSize = gridMinimum;
            _split.Panel2MinSize = actionsMinimum;
            var maximum = _split.Height - actionsMinimum - _split.SplitterWidth;
            var distance = _splitRatio is { } ratio ? (int)(_split.Height * ratio) : _split.Height - _split.SplitterWidth - actionsHeight;
            _split.SplitterDistance = Math.Clamp(distance, gridMinimum, maximum);
        }
        finally { _layingOut = false; }
    }
    private void FinishCancelledSplitterMove()
    {
        if (!_splitterMoving || IsDisposed) return;
        // WinForms applies the new distance after the input event returns.
        BeginInvoke(() =>
        {
            if (!_splitterMoving) return;
            _splitterMoving = false;
            LayoutWorkspace();
        });
    }
    private int ToDevicePixels(int value) => (int)Math.Round(value * DeviceDpi / 96d);
    private void CapturePartitions(WindowLayout state)
    {
        state.SplitRatio = _splitRatio ?? 0;
        state.ColumnsDip = _grid.Columns.Cast<DataGridViewColumn>().Select(c => (int)Math.Round(c.Width * 96d / DeviceDpi)).ToArray();
    }
    private void SavePartitions() { CapturePartitions(_layouts.Get(MainLayoutKey)); _layouts.Save(); }
}
