// SPDX-License-Identifier: MPL-2.0
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace EasLoginHelper;

public static class UiTheme
{
    public static readonly Color Blue = Color.FromArgb(22, 119, 255);
    public static readonly Color Background = Color.FromArgb(244, 248, 255);
    public static readonly Color Ink = Color.FromArgb(31, 45, 61);
    public static readonly Color Muted = Color.FromArgb(102, 119, 140);
    public static readonly Color Line = Color.FromArgb(220, 230, 244);
    public const string Signature = "EAS Autologin Version: 0.2.0.0, Xymircai Initial Development";
    public static WrappingLabel WrapText(string text, float size = 10, bool bold = false) => new()
    { Text = text, ForeColor = Ink, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
    public static TableLayoutPanel DialogPage(Form form)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var page = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(24) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); viewport.Controls.Add(page); form.Controls.Add(viewport); return page;
    }
    public static FlowLayoutPanel DialogActions(params Control[] buttons)
    {
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 12, 0, 0) };
        actions.Controls.AddRange(buttons); return actions;
    }
    public static void ConfigureWindow(Form form, Size preferred, Size minimum, bool dialog = false)
    {
        form.AutoScaleDimensions = new SizeF(96, 96); form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("Microsoft YaHei UI", 10); form.ForeColor = Ink; form.BackColor = Background;
        form.FormBorderStyle = FormBorderStyle.Sizable; form.MaximizeBox = true; form.MinimizeBox = !dialog;
        form.SizeGripStyle = SizeGripStyle.Show; form.ClientSize = preferred; form.MinimumSize = minimum;
        form.StartPosition = dialog ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
        void Fit()
        {
            var work = Screen.FromControl(form).WorkingArea; var scale = form.DeviceDpi / 96d;
            form.MinimumSize = new Size(Math.Max(150, Math.Min((int)(minimum.Width * scale), work.Width - 24)),
                Math.Max(120, Math.Min((int)(minimum.Height * scale), work.Height - 24)));
            FitWorkingArea(form);
        }
        form.Shown += (_, _) => Fit();
        form.DpiChanged += (_, _) => { if (!form.IsDisposed) form.BeginInvoke(Fit); };
    }
    internal static GraphicsPath Round(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter < 1) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
    public static RoundedButton Button(string text, Action click, bool primary = false)
    {
        var button = new RoundedButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 10, 18, 10), Margin = new Padding(0, 0, 10, 8), Primary = primary };
        button.Click += (_, _) => click(); return button;
    }
    public static Label Text(string value, float size = 10, bool bold = false) => new()
    {
        Text = value, AutoSize = true, ForeColor = Ink, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 0, 0, 6)
    };
    public static void FitWorkingArea(Form form, Rectangle? workArea = null)
    {
        var preference = 2;
        _ = DwmSetWindowAttribute(form.Handle, 33, ref preference, sizeof(int));
        var working = workArea ?? Screen.FromControl(form).WorkingArea;
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, working.Width - 32), Math.Min(form.MinimumSize.Height, working.Height - 32));
        form.Size = new Size(Math.Min(form.Width, working.Width - 32), Math.Min(form.Height, working.Height - 32));
        if (!working.Contains(form.Bounds)) form.Location = new System.Drawing.Point(working.Left + (working.Width - form.Width) / 2, working.Top + (working.Height - form.Height) / 2);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

public sealed class RoundedPanel : Panel
{
    [System.ComponentModel.DefaultValue(16)] public int Radius { get; set; } = 16;
    [System.ComponentModel.DefaultValue(typeof(Color), "White")] public Color FillColor { get; set; } = Color.White;
    public RoundedPanel() { DoubleBuffered = true; ResizeRedraw = true; Padding = new Padding(14); BackColor = UiTheme.Background; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius * DeviceDpi / 96f);
        using var fill = new SolidBrush(FillColor); using var line = new Pen(UiTheme.Line);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(line, shape);
    }
}

public sealed class RoundedButton : Button
{
    private bool _hover, _down, _primary;
    [System.ComponentModel.DefaultValue(false)] public bool Primary { get => _primary; set { _primary = value; Invalidate(); } }
    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        ForeColor = UiTheme.Ink; BackColor = Color.White; AccessibleRole = AccessibleRole.PushButton;
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 2 || Height < 2) return;
        using var path = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 10 * DeviceDpi / 96f);
        var fill = !Enabled ? Color.FromArgb(235, 240, 247) : Primary
            ? _down ? Color.FromArgb(12, 86, 195) : _hover ? Color.FromArgb(60, 143, 255) : UiTheme.Blue
            : _down ? Color.FromArgb(216, 232, 255) : _hover ? Color.FromArgb(233, 243, 255) : Color.White;
        using var brush = new SolidBrush(fill); using var border = new Pen(Primary ? fill : UiTheme.Line);
        e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -6, -3), !Enabled ? UiTheme.Muted : Primary ? Color.White : UiTheme.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), Primary ? Color.White : UiTheme.Blue, fill);
    }
}
