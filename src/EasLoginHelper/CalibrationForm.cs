// SPDX-License-Identifier: MPL-2.0
using System.Drawing.Drawing2D;

namespace EasLoginHelper;

public sealed class CalibrationForm : Form
{
    private readonly Bitmap _image;
    private readonly Rectangle _client;
    private readonly string _path;
    private readonly uint _dpi;
    private readonly List<Rectangle> _regions = [];
    private readonly string[] _instructions = [
        "框选整个登录面板（包含 EasyAudit 标题、输入框和登录按钮）",
        "框选用户名输入框（包含完整边框）", "框选密码输入框（包含完整边框）",
        "框选验证码输入框（包含完整边框）", "框选验证码图片（不要包含输入框）",
        "框选 EasyAudit 标题文字，作为页面识别锚点（不要包含输入框）"
    ];
    private System.Drawing.Point? _start;
    private Rectangle _drag;
    public Calibration? Result { get; private set; }

    public CalibrationForm(IntPtr window, string path)
    {
        _client = Native.ClientBounds(window); _path = path; _dpi = Native.GetDpiForWindow(window);
        _image = ScreenCapture.Capture(window, _client);
        Text = "校准 EAS 登录页"; Icon = Branding.WindowIcon(DeviceDpi); FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; Bounds = _client; TopMost = true; DoubleBuffered = true;
        KeyPreview = true; Cursor = Cursors.Cross;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            if (e.KeyCode == Keys.Back && _regions.Count > 0) { _regions.RemoveAt(_regions.Count - 1); Invalidate(); }
        };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _start = e.Location; Capture = true; } };
        MouseMove += (_, e) => { if (_start is { } start) { _drag = RectBetween(start, e.Location); Invalidate(); } };
        MouseUp += (_, e) =>
        {
            if (_start is not { } start) return;
            _start = null; Capture = false;
            var rect = Rectangle.Intersect(new Rectangle(System.Drawing.Point.Empty, _image.Size), RectBetween(start, e.Location));
            _drag = Rectangle.Empty;
            if (rect.Width < 12 || rect.Height < 10) { Invalidate(); return; }
            if (_regions.Count > 0 && !_regions[0].Contains(rect)) { ShowError("该区域必须在第一步选定的登录面板内。"); return; }
            _regions.Add(rect);
            if (_regions.Count == _instructions.Length) Finish();
            Invalidate();
        };
    }

    private void Finish()
    {
        var panel = _regions[0]; var fields = _regions.Skip(1).Take(3).ToArray(); var captcha = _regions[4]; var anchor = _regions[5];
        if (!LoginGeometry.IsLogin(fields.Select((r, i) => new FieldGeometry(r, i == 1, "")).ToArray(), true) ||
            fields.Any(r => r.IntersectsWith(anchor) || r.IntersectsWith(captcha)) || anchor.Bottom >= fields[0].Top ||
            captcha.Left < fields[2].Right || Math.Abs(captcha.Top - fields[2].Top) > fields[2].Height)
        {
            _regions.Clear(); ShowError("区域排列与三个登录输入框不一致，请从整个登录面板重新框选。"); return;
        }
        var sample = VisualLocator.SampleGray(_image, anchor);
        if (!VisualLocator.AnchorMatches(sample, sample))
        { _regions.RemoveAt(5); ShowError("标题区域需要包含清晰的 EasyAudit 文字，请重新选择标题。"); return; }
        Result = new Calibration
        {
            ExecutablePath = _path, FileVersion = TargetPolicy.Version(_path), OriginalDpi = _dpi,
            OriginalClientWidth = _client.Width, OriginalClientHeight = _client.Height,
            Panel = RelativeRect.From(panel, new Rectangle(System.Drawing.Point.Empty, _image.Size)),
            Fields = fields.Select(r => RelativeRect.From(r, panel)).ToArray(), Captcha = RelativeRect.From(captcha, panel),
            Anchor = RelativeRect.From(anchor, panel), AnchorGray = sample
        };
        DialogResult = DialogResult.OK; Close();
    }
    private void ShowError(string text) => MessageBox.Show(this, text, "校准提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    private static Rectangle RectBetween(System.Drawing.Point a, System.Drawing.Point b) => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.DrawImageUnscaled(_image, 0, 0);
        using var fill = new SolidBrush(Color.FromArgb(28, 0, 0, 0)); e.Graphics.FillRectangle(fill, ClientRectangle);
        using var pen = new Pen(Color.LimeGreen, 3) { DashStyle = DashStyle.Dash };
        foreach (var rect in _regions) e.Graphics.DrawRectangle(pen, rect);
        if (!_drag.IsEmpty) e.Graphics.DrawRectangle(Pens.OrangeRed, _drag);
        using var font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
        using var background = new SolidBrush(Color.FromArgb(240, 20, 40, 55));
        var prompt = $"{Math.Min(_regions.Count + 1, 6)}/6  {_instructions[Math.Min(_regions.Count, 5)]}    Esc 取消 · Backspace 返回上一步";
        var promptHeight = Math.Min(100, Height / 5);
        e.Graphics.FillRectangle(background, new Rectangle(0, 0, Width, promptHeight));
        e.Graphics.DrawString(prompt, font, Brushes.White, new RectangleF(20, 12, Width - 40, promptHeight - 15));
    }
    protected override void Dispose(bool disposing) { if (disposing) _image.Dispose(); base.Dispose(disposing); }
}
