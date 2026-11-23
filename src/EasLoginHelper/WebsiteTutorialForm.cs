// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

/// <summary>「网站登录教程」四步图文说明，图片全部内置在单 EXE 中。</summary>
internal sealed class WebsiteTutorialForm : Form
{
    private static readonly (string Title, string Text, string Resource)[] Steps =
    [
        ("① 准备扩展文件夹",
         "将发行包里的 BrowserExtension 文件夹放在可以找得到的位置，例如桌面。文件夹里应直接包含 manifest.json，不要只复制里面的文件。",
         "EasLoginHelper.Tutorial.Step1"),
        ("② 加载解压缩的扩展",
         "在自己的浏览器中打开扩展页面：Edge 地址栏输入 edge://extensions/，Chrome 输入 chrome://extensions/。开启「开发人员模式」，点击「加载解压缩的扩展」。",
         "EasLoginHelper.Tutorial.Step2"),
        ("③ 选择扩展文件夹",
         "在弹出的选择文件夹窗口中，直接选中第 ① 步准备好的 BrowserExtension 文件夹，然后点击「选择文件夹」。",
         "EasLoginHelper.Tutorial.Step3"),
        ("④ 确认扩展已添加",
         "随后就可以在浏览器的扩展列表中看到「容诚网站登录助手」，且开关处于开启状态，表示扩展已正常添加。回到助手点击「打开网站」即可自动填写；与浏览器的连接登记由助手自动完成，无需其他设置。",
         "EasLoginHelper.Tutorial.Step4")
    ];

    private readonly RoundedButton[] _tabs = new RoundedButton[Steps.Length];
    private readonly WrappingLabel _description = UiTheme.WrapText("", 10.5f);
    private readonly Label _counter = UiTheme.Text("", 9.5f);
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly RoundedButton _previous, _next;
    private int _index;

    internal WebsiteTutorialForm()
    {
        Text = "网站登录教程";
        UiTheme.ConfigureWindow(this, new Size(1000, 720), new Size(720, 540), dialog: true);

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(20, 16, 20, 16) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = UiTheme.WrapText("网站登录教程", 16, true);
        title.ForeColor = UiTheme.Blue;
        page.Controls.Add(title, 0, 0);

        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 12, 0, 8) };
        for (var i = 0; i < Steps.Length; i++)
        {
            var step = i;
            _tabs[i] = UiTheme.Button(Steps[i].Title, () => ShowStep(step));
            tabs.Controls.Add(_tabs[i]);
        }
        page.Controls.Add(tabs, 0, 1);

        _description.Margin = new Padding(0, 0, 0, 10);
        page.Controls.Add(_description, 0, 2);

        var card = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(10), Margin = new Padding(0, 0, 0, 12) };
        card.Controls.Add(_picture);
        page.Controls.Add(card, 0, 3);

        _previous = UiTheme.Button("上一步", () => ShowStep(_index - 1));
        _next = UiTheme.Button("下一步", () => ShowStep(_index + 1), true);
        var close = UiTheme.Button("关闭", Close);
        _counter.ForeColor = UiTheme.Muted;
        _counter.Margin = new Padding(8, 14, 16, 0);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        footer.Controls.AddRange([_previous, _next, _counter, close]);
        page.Controls.Add(footer, 0, 4);

        Controls.Add(page);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Left) { ShowStep(_index - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { ShowStep(_index + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) Close();
        };
        ShowStep(0);
    }

    private void ShowStep(int index)
    {
        if (index < 0 || index >= Steps.Length) return;
        _index = index;
        var step = Steps[index];
        _description.Text = step.Text;
        _counter.Text = $"第 {index + 1} / {Steps.Length} 步";
        for (var i = 0; i < _tabs.Length; i++) _tabs[i].Primary = i == index;
        _previous.Enabled = index > 0;
        _next.Enabled = index < Steps.Length - 1;

        var previous = _picture.Image;
        try { _picture.Image = EmbeddedAssets.LoadImage(step.Resource); }
        catch (Exception e) when (e is FileNotFoundException or IOException or ArgumentException or OutOfMemoryException)
        {
            _picture.Image = null;
            _description.Text = step.Text + "\n内置教程图片缺失，请重新下载完整 EXE。（" + e.Message + "）";
        }
        previous?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _picture.Image?.Dispose();
        _picture.Image = null;
        base.OnFormClosed(e);
    }
}
