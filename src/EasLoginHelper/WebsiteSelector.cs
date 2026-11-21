// SPDX-License-Identifier: MPL-2.0
using System.Drawing.Drawing2D;

namespace EasLoginHelper;

internal sealed class WebsiteSelector : Control
{
    private readonly ToolStripDropDown _menu = new()
    {
        AutoSize = false, AutoClose = true, CanOverflow = false,
        LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow
    };
    private bool _hovered;

    internal int SelectedIndex { get; private set; }
    internal event EventHandler? SelectedIndexChanged;
    private string SelectedName => Website.All[SelectedIndex].Name;

    internal WebsiteSelector()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoSize = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleName = "网站";
        foreach (var site in Website.All)
        {
            int index = _menu.Items.Count;
            var item = new ToolStripMenuItem(site.Name) { AutoSize = false, DisplayStyle = ToolStripItemDisplayStyle.Text };
            item.Click += (_, _) => SelectSite(index);
            _menu.Items.Add(item);
        }
        _menu.Renderer = new MenuRenderer(this);
        _menu.Opened += (_, _) =>
        {
            _menu.Items[SelectedIndex].Select();
            UpdateExpansion();
        };
        _menu.Closed += (_, _) => UpdateExpansion();
    }

    private int ScaleDip(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(ScaleDip(214), Math.Max(ScaleDip(36), Font.Height + ScaleDip(16)));

    private void SelectSite(int index)
    {
        if (SelectedIndex == index) return;
        SelectedIndex = index;
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleMenu()
    {
        if (!Enabled || !Visible) return;
        if (_menu.Visible) { _menu.Close(); return; }

        PrepareMenu();
        var gap = ScaleDip(4);
        var below = PointToScreen(new Point(0, Height + gap));
        var workingArea = Screen.FromPoint(below).WorkingArea;
        _menu.Show(this, new Point(0, below.Y + _menu.Height <= workingArea.Bottom ? Height + gap : -_menu.Height - gap));
    }

    private void PrepareMenu()
    {
        var padding = ScaleDip(5);
        var width = Math.Max(Width, ScaleDip(162));
        var rowHeight = Math.Max(ScaleDip(34), Font.Height + ScaleDip(14));
        _menu.Padding = new Padding(padding);
        _menu.Font = Font;
        _menu.Size = new Size(width, rowHeight * _menu.Items.Count + padding * 2);
        for (int i = 0; i < _menu.Items.Count; i++)
        {
            var item = (ToolStripMenuItem)_menu.Items[i];
            item.Margin = Padding.Empty;
            item.Size = new Size(width - padding * 2, rowHeight);
            item.Checked = i == SelectedIndex;
        }
        using var outline = UiTheme.Round(new RectangleF(0, 0, _menu.Width, _menu.Height), ScaleDip(11));
        _menu.Region = new Region(outline);
    }

    private void UpdateExpansion()
    {
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Background);
        if (Width < 2 || Height < 2) return;
        var active = Focused || _menu.Visible;
        var highContrast = SystemInformation.HighContrast;
        var back = highContrast ? SystemColors.Window : !Enabled ? Color.FromArgb(235, 240, 247) :
            _hovered || _menu.Visible ? Color.FromArgb(247, 251, 255) : Color.White;
        var fore = highContrast ? Enabled ? SystemColors.WindowText : SystemColors.GrayText :
            !Enabled ? UiTheme.Muted : UiTheme.Ink;
        var borderColor = highContrast ? active ? SystemColors.Highlight : SystemColors.WindowText :
            active ? UiTheme.Blue : UiTheme.Line;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var contour = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), ScaleDip(10));
        using var fill = new SolidBrush(back);
        using var line = new Pen(borderColor);
        e.Graphics.FillPath(fill, contour);
        e.Graphics.DrawPath(line, contour);
        TextRenderer.DrawText(e.Graphics, SelectedName, Font, new Rectangle(ScaleDip(12), 0,
            Math.Max(0, Width - ScaleDip(44)), Height), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var centerX = Width - ScaleDip(17);
        var centerY = Height / 2f;
        var dx = ScaleDip(4);
        var dy = _menu.Visible ? -ScaleDip(2) : ScaleDip(2);
        using var chevron = new Pen(active ? borderColor : fore, ScaleDip(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(chevron, [new PointF(centerX - dx, centerY - dy),
            new PointF(centerX, centerY + dy), new PointF(centerX + dx, centerY - dy)]);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled && !_menu.IsDisposed) _menu.Close();
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        ToggleMenu();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F4:
            case Keys.Space:
            case Keys.Enter:
            case Keys.Alt | Keys.Down:
                ToggleMenu();
                return true;
            case Keys.Up:
                SelectSite(Math.Max(0, SelectedIndex - 1));
                return true;
            case Keys.Down:
                SelectSite(Math.Min(Website.All.Count - 1, SelectedIndex + 1));
                return true;
            case Keys.Home:
                SelectSite(0);
                return true;
            case Keys.End:
                SelectSite(Website.All.Count - 1);
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SelectorAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }

    private sealed class SelectorAccessibleObject(WebsiteSelector owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.ComboBox;
        public override string? Value { get => owner.SelectedName; set { } }
        public override AccessibleStates State => base.State | (owner._menu.Visible ? AccessibleStates.Expanded : AccessibleStates.Collapsed);
        public override string DefaultAction => owner._menu.Visible ? "折叠" : "展开";
        public override void DoDefaultAction() => owner.ToggleMenu();
    }

    private sealed class MenuRenderer(WebsiteSelector owner) : ToolStripSystemRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(SystemInformation.HighContrast ? SystemColors.Window : Color.White);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = UiTheme.Round(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), owner.ScaleDip(11));
            using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : UiTheme.Line);
            e.Graphics.DrawPath(pen, shape);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!(e.Item.Selected || e.Item is ToolStripMenuItem { Checked: true })) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = UiTheme.Round(new RectangleF(0, 1, e.Item.Width, e.Item.Height - 2), owner.ScaleDip(7));
            var fill = SystemInformation.HighContrast ? SystemColors.Highlight : e.Item.Selected ?
                Color.FromArgb(226, 239, 255) : Color.FromArgb(240, 247, 255);
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, shape);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var selected = e.Item.Selected || e.Item is ToolStripMenuItem { Checked: true };
            var fore = SystemInformation.HighContrast ? selected ? SystemColors.HighlightText : SystemColors.WindowText :
                selected ? UiTheme.Blue : UiTheme.Ink;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, new Rectangle(owner.ScaleDip(8), 0,
                Math.Max(0, e.Item.Width - owner.ScaleDip(16)), e.Item.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
    }
}
