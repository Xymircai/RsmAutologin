// SPDX-License-Identifier: MPL-2.0
using System.Windows.Automation;
using UiaCondition = System.Windows.Automation.Condition;

namespace EasLoginHelper;

public sealed record FieldTarget(Rectangle Bounds, AutomationElement? Element);
public sealed record LoginPage(IntPtr Window, string ExecutablePath, Rectangle Client,
    FieldTarget[] Fields, Rectangle Captcha, bool UsesCalibration)
{
    public string ProcessKey { get; } = Native.ProcessIdentity(Window) ?? Window.ToInt64().ToString();
    public string Identity => ProcessKey + ":" + Window.ToInt64() + ":" + ExecutablePath;
    public FieldTarget? SubmitButton { get; init; }
}

public sealed record FieldGeometry(Rectangle Bounds, bool Password, string Hint);

public static class LoginGeometry
{
    public static int[]? SelectLoginFields(IReadOnlyList<FieldGeometry> all, bool hasLoginButton)
    {
        if (!hasLoginButton) return null;
        var matches = new List<int[]>();
        for (var p = 0; p < all.Count; p++)
        {
            if (!all[p].Password) continue;
            for (var u = 0; u < all.Count; u++) for (var c = 0; c < all.Count; c++)
            {
                if (u == p || c == p || u == c || all[u].Password || all[c].Password ||
                    all[u].Bounds.Top >= all[p].Bounds.Top || all[c].Bounds.Top <= all[p].Bounds.Top) continue;
                if (IsLogin(new[] { all[u], all[p], all[c] }, true)) matches.Add([u, p, c]);
            }
        }
        // Native status bars can also expose Edit controls. Only a unique coherent login triple is accepted.
        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool IsLogin(IReadOnlyList<FieldGeometry> fields, bool hasLoginButton)
    {
        if (!hasLoginButton || fields.Count != 3 || fields.Count(f => f.Password) != 1) return false;
        var sorted = fields.OrderBy(f => f.Bounds.Top).ToArray();
        var user = sorted[0]; var password = sorted[1]; var captcha = sorted[2];
        if (!password.Password || user.Password || captcha.Password ||
            sorted.Any(f => f.Bounds.Width < 60 || f.Bounds.Height < 15 || f.Bounds.Height > 150)) return false;
        var tolerance = user.Bounds.Width * 0.12;
        return Math.Abs(user.Bounds.Left - password.Bounds.Left) < tolerance &&
            Math.Abs(user.Bounds.Left - captcha.Bounds.Left) < tolerance &&
            password.Bounds.Top > user.Bounds.Bottom && captcha.Bounds.Top > password.Bounds.Bottom &&
            password.Bounds.Width >= user.Bounds.Width * 0.75 &&
            captcha.Bounds.Width < password.Bounds.Width * 0.95 &&
            captcha.Bounds.Top - user.Bounds.Bottom < 650;
    }

    public static Rectangle CaptchaBeside(Rectangle input, Rectangle password, uint dpi)
    {
        var gap = (int)Math.Round(6 * dpi / 96d);
        var rect = Rectangle.FromLTRB(input.Right + gap, input.Top, password.Right, input.Bottom);
        return rect.Width >= 35 * dpi / 96d ? rect : Rectangle.Empty;
    }
}

public sealed class LoginLocator(TargetPolicy policy, Func<Calibration?> calibration)
{
    public LoginPage? FindForeground()
    {
        var window = Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot);
        if (!policy.Allows(window) || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return null;
        return Find(window);
    }

    public LoginPage? Find(IntPtr window)
    {
        if (!policy.Allows(window) || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return null;
        var client = Native.ClientBounds(window);
        var path = Native.ProcessPath(window)!;
        if (client.Width < 300 || client.Height < 300) return null;
        try
        {
            var root = AutomationElement.FromHandle(window);
            var candidates = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image)));
            var fields = new List<(AutomationElement Element, FieldGeometry Geometry)>();
            var buttons = new List<FieldTarget>();
            var images = new List<Rectangle>();
            var loginButton = false;
            for (var i = 0; i < candidates.Count && i < 200; i++)
            {
                var element = candidates[i];
                var current = element.Current;
                if (current.IsOffscreen || !current.IsEnabled || current.BoundingRectangle.IsEmpty) continue;
                var rect = ToRectangle(current.BoundingRectangle);
                if (!client.Contains(rect) || rect.Width < 20 || rect.Height < 10) continue;
                if (current.ControlType == ControlType.Image) images.Add(rect);
                else if (current.ControlType == ControlType.Button)
                {
                    if (current.Name.Trim() is "登录" or "登 录" or "Log in" or "Login")
                    { loginButton = true; buttons.Add(new FieldTarget(rect, element)); }
                }
                else fields.Add((element, new FieldGeometry(rect, current.IsPassword, current.Name + " " + current.AutomationId)));
            }
            fields.Sort((a, b) => a.Geometry.Bounds.Top.CompareTo(b.Geometry.Bounds.Top));
            var selected = LoginGeometry.SelectLoginFields(fields.Select(f => f.Geometry).ToArray(), loginButton);
            if (selected is not null)
            {
                var targets = selected.Select(i => new FieldTarget(fields[i].Geometry.Bounds, fields[i].Element)).ToArray();
                var submit = buttons.Where(b => b.Bounds.Top >= targets[2].Bounds.Bottom &&
                    Math.Abs(b.Bounds.Left - targets[1].Bounds.Left) < targets[1].Bounds.Width * .12 &&
                    b.Bounds.Width >= targets[1].Bounds.Width * .75).ToArray();
                var captcha = LoginGeometry.CaptchaBeside(targets[2].Bounds, targets[1].Bounds, Native.GetDpiForWindow(window));
                var imageMatches = images.Where(image => image.Width >= 35 && image.Left >= targets[2].Bounds.Right &&
                    image.Right <= targets[1].Bounds.Right + 4 && Math.Abs(image.Top - targets[2].Bounds.Top) <= targets[2].Bounds.Height * .3 &&
                    image.Height >= targets[2].Bounds.Height * .5 && image.Height <= targets[2].Bounds.Height * 1.5).ToArray();
                if (imageMatches.Length == 1) captcha = imageMatches[0];
                return new LoginPage(window, path, client, targets, captcha, false)
                    { SubmitButton = submit.Length == 1 ? submit[0] : null };
            }
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
        return VisualLocator.TryLocate(window, path, client, calibration());
    }

    public static Rectangle ToRectangle(System.Windows.Rect rect) => Rectangle.FromLTRB(
        (int)Math.Round(rect.Left), (int)Math.Round(rect.Top), (int)Math.Round(rect.Right), (int)Math.Round(rect.Bottom));

    public static string? ReadNonSecretValue(FieldTarget field)
    {
        if (field.Element is null || field.Element.Current.IsPassword) return null;
        return field.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : null;
    }
}

public static class ScreenCapture
{
    public static Bitmap Capture(IntPtr window, Rectangle rect)
    {
        if (Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot) != window ||
            !Native.ClientBounds(window).Contains(rect) || !Native.IsUnobscured(window, rect))
            throw new OperationCanceledException("登录页被遮挡或已不在前台。");
        var bitmap = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, rect.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}

public static class VisualLocator
{
    public const int AnchorWidth = 80, AnchorHeight = 24;
    public static byte[] SampleGray(Bitmap image, Rectangle area)
    {
        if (!new Rectangle(0, 0, image.Width, image.Height).Contains(area) || area.Width < 2 || area.Height < 2)
            throw new ArgumentException("图像区域无效。");
        using var scaled = new Bitmap(AnchorWidth, AnchorHeight);
        using (var graphics = Graphics.FromImage(scaled)) graphics.DrawImage(image, new Rectangle(0, 0, scaled.Width, scaled.Height), area, GraphicsUnit.Pixel);
        var values = new byte[AnchorWidth * AnchorHeight];
        for (var y = 0; y < AnchorHeight; y++) for (var x = 0; x < AnchorWidth; x++)
        {
            var p = scaled.GetPixel(x, y);
            values[y * AnchorWidth + x] = (byte)((p.R * 299 + p.G * 587 + p.B * 114) / 1000);
        }
        return values;
    }

    public static bool AnchorMatches(byte[] expected, byte[] actual)
    {
        if (expected.Length != AnchorWidth * AnchorHeight || actual.Length != expected.Length) return false;
        // Require a text-like anchor, so a blank colored rectangle cannot authorize input.
        var average = expected.Average(p => (double)p);
        if (expected.Max() - expected.Min() < 50 || expected.Count(p => p < average - 25) < 35) return false;
        return expected.Zip(actual, (a, b) => Math.Abs(a - b)).Average() <= 18;
    }

    public static Rectangle MapPanel(Calibration c, Rectangle client, uint dpi)
    {
        var original = c.Panel.Map(new Rectangle(0, 0, c.OriginalClientWidth, c.OriginalClientHeight));
        var factor = dpi / (double)Math.Max(1u, c.OriginalDpi);
        var width = (int)Math.Round(original.Width * factor); var height = (int)Math.Round(original.Height * factor);
        var centerX = client.Left + client.Width / 2d + (original.Left + original.Width / 2d - c.OriginalClientWidth / 2d) * factor;
        var centerY = client.Top + client.Height / 2d + (original.Top + original.Height / 2d - c.OriginalClientHeight / 2d) * factor;
        return new Rectangle((int)Math.Round(centerX - width / 2d), (int)Math.Round(centerY - height / 2d), width, height);
    }

    public static LoginPage? TryLocate(IntPtr window, string path, Rectangle client, Calibration? c)
    {
        if (c is null || !string.Equals(path, c.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            c.FileVersion != TargetPolicy.Version(path)) return null;
        var panel = MapPanel(c, client, Native.GetDpiForWindow(window));
        if (!client.Contains(panel)) return null;
        try
        {
            using var image = ScreenCapture.Capture(window, panel);
            var local = new Rectangle(0, 0, panel.Width, panel.Height);
            if (!AnchorMatches(c.AnchorGray, SampleGray(image, c.Anchor.Map(local)))) return null;
            var fields = c.Fields.Select(r => r.Map(panel)).ToArray();
            // The screenshot style uses dark input interiors. Refuse a changed/blank/covered form.
            foreach (var field in fields)
            {
                var sample = new Rectangle(field.Left - panel.Left + field.Width / 6, field.Top - panel.Top + field.Height / 5,
                    Math.Max(2, field.Width * 2 / 3), Math.Max(2, field.Height * 3 / 5));
                var gray = SampleGray(image, sample);
                if (gray.Count(p => p < 110) < gray.Length * 0.70) return null;
            }
            return new LoginPage(window, path, client, fields.Select(r => new FieldTarget(r, null)).ToArray(), c.Captcha.Map(panel), true);
        }
        catch (OperationCanceledException) { return null; }
    }
}
