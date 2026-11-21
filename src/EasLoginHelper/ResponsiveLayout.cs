// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace EasLoginHelper;

public sealed class WrappingLabel : Label
{
    public WrappingLabel() { AutoSize = true; Dock = DockStyle.Top; Margin = new Padding(0, 0, 0, 8); }
    protected override void OnParentChanged(EventArgs e)
    {
        if (_parent is not null) _parent.SizeChanged -= ParentResized;
        base.OnParentChanged(e); _parent = Parent;
        if (_parent is not null) _parent.SizeChanged += ParentResized;
        Constrain();
    }
    private Control? _parent;
    private void ParentResized(object? sender, EventArgs e) => Constrain();
    private void Constrain()
    {
        if (Parent is null || Parent.ClientSize.Width < 1) return;
        var width = Math.Max(40, Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal);
        if (MaximumSize.Width != width) MaximumSize = new Size(width, 0);
    }
    protected override void Dispose(bool disposing)
    { if (disposing && _parent is not null) _parent.SizeChanged -= ParentResized; base.Dispose(disposing); }
}

public sealed class WindowLayout
{
    public int WidthDip { get; set; }
    public int HeightDip { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool Maximized { get; set; }
    public double SplitRatio { get; set; } = .52;
    public int[] ColumnsDip { get; set; } = [];
}

public sealed class UiLayoutStore
{
    private readonly string? _path;
    private Dictionary<string, WindowLayout> _windows = new();
    public UiLayoutStore(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try { _windows = JsonSerializer.Deserialize<Dictionary<string, WindowLayout>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public static UiLayoutStore ForSettings(SettingsStore settings)
    {
        var standard = Path.Combine(SettingsStore.DataDirectory, "settings.json");
        return new UiLayoutStore(string.Equals(settings.FilePath, standard, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(SettingsStore.DataDirectory, "ui-state.json") : settings.FilePath + ".ui-state.json");
    }
    public WindowLayout Get(string key)
    {
        if (!_windows.TryGetValue(key, out var value) || value is null) _windows[key] = value = new();
        return value;
    }
    public void Save()
    {
        if (_path is null) return;
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_windows, SettingsStore.JsonOptions));
            File.Move(temporary, _path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Attach(Form form, string key, Action<WindowLayout>? restore = null, Action<WindowLayout>? capture = null)
    {
        var restoring = true;
        form.Shown += (_, _) =>
        {
            var state = Get(key); var scale = form.DeviceDpi / 96d;
            if (state.WidthDip is >= 280 and <= 5000 && state.HeightDip is >= 180 and <= 5000)
            {
                form.Bounds = new Rectangle(state.X, state.Y, (int)(state.WidthDip * scale), (int)(state.HeightDip * scale));
                UiTheme.FitWorkingArea(form);
                if (state.Maximized) form.WindowState = FormWindowState.Maximized;
            }
            restore?.Invoke(state); restoring = false;
        };
        void Capture()
        {
            if (restoring || form.WindowState == FormWindowState.Minimized) return;
            var state = Get(key); var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            state.WidthDip = (int)Math.Round(bounds.Width * 96d / form.DeviceDpi);
            state.HeightDip = (int)Math.Round(bounds.Height * 96d / form.DeviceDpi);
            state.X = bounds.X; state.Y = bounds.Y; state.Maximized = form.WindowState == FormWindowState.Maximized;
            capture?.Invoke(state); Save();
        }
        form.ResizeEnd += (_, _) => Capture(); form.FormClosing += (_, _) => Capture();
    }
}
