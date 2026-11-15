// SPDX-License-Identifier: MPL-2.0
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace EasLoginHelper;

public sealed record EnvironmentReport(bool Success, string Code, string Message, bool EmbeddedOcr, bool EngineReady,
    bool Captured, bool OcrAccepted, bool BoundFieldWritten, string CaptureBackend, int Dpi, Size WorkingArea,
    string WindowsVersion, string Architecture, string ApplicationVersion, long GlobalInputRequests, long ForegroundRequests, DateTime CreatedUtc);

public static class EnvironmentSelfTest
{
    public static string OutputPath => Path.Combine(SettingsStore.DataDirectory, "environment-selftest.json");
    private sealed class SampleWindow : Form
    {
        public PictureBox Picture { get; } = new() { Bounds = new Rectangle(16, 16, 128, 54), SizeMode = PictureBoxSizeMode.StretchImage };
        public TextBox Field { get; } = new() { Bounds = new Rectangle(16, 86, 160, 28), AccessibleName = "离线自检输入框" };
        protected override bool ShowWithoutActivation => true;
        public SampleWindow()
        {
            Text = "环境自检 · 离线模拟窗口"; ClientSize = new Size(210, 132); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Picture.Image = SampleImage(); Controls.Add(Picture); Controls.Add(Field);
        }
        protected override void Dispose(bool disposing) { if (disposing) Picture.Image?.Dispose(); base.Dispose(disposing); }
    }
    internal static Bitmap SampleImage()
    {
        var image = new Bitmap(128, 54); using var g = Graphics.FromImage(image);
        g.Clear(Color.FromArgb(230, 248, 255)); g.SmoothingMode = SmoothingMode.AntiAlias;
        var colors = new[] { Color.FromArgb(134, 213, 192), Color.FromArgb(65, 201, 195), Color.FromArgb(112, 188, 230), Color.FromArgb(49, 174, 205) };
        using var font = new Font("Arial", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        for (var i = 0; i < 4; i++)
        {
            var state = g.Save(); g.TranslateTransform(17 + i * 31, 27); g.RotateTransform(i % 2 == 0 ? -8 : 5);
            using var brush = new SolidBrush(colors[i]); g.FillRectangle(brush, -13, -19, 26, 38);
            using var format = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("4826"[i].ToString(), font, Brushes.Black, new RectangleF(-12, -14, 26, 32), format); g.Restore(state);
        }
        return image;
    }
    public static async Task<EnvironmentReport> CheckAsync(Form? owner = null, string? outputPath = null)
    {
        using var window = new SampleWindow(); using var capture = new WindowCaptureSource(); using var ocr = new CaptchaRecognizer();
        var input = Native.InputRequests; var foreground = Native.ActivationRequests;
        var captured = false; var recognized = false; var written = false; var engine = false;
        var code = "SelfTest.Ready"; var message = "环境自检通过。";
        var dpi = 96; var area = Size.Empty;
        try
        {
            var health = await ocr.WarmupAsync(); engine = health.Ready;
            if (!engine) { code = health.Code; message = health.Message; }
            else
            {
                window.Show(owner); await Task.Delay(250); dpi = window.DeviceDpi; area = Screen.FromControl(window).WorkingArea.Size;
                using var bitmap = await capture.CaptureAsync(window.Handle, window.Picture.RectangleToScreen(window.Picture.ClientRectangle), CancellationToken.None);
                captured = true;
                var result = await ocr.RecognizeAsync(bitmap, CancellationToken.None);
                recognized = result.Accepted && result.Text == "4826";
                if (!recognized) { code = result.ErrorCode ?? "Ocr.LowConfidence"; message = "捕获已完成，但离线样本未识别通过；请提供自检报告。"; }
                else
                {
                    var handle = window.Field.Handle;
                    written = await Task.Run(() =>
                    {
                        var element = AutomationElement.FromHandle(handle);
                        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)) return false;
                        var value = (ValuePattern)raw; value.SetValue("4826"); return value.Current.Value == "4826";
                    });
                    if (!written) { code = "Control.WriteFailed"; message = "OCR 通过，但离线控件写入失败。"; }
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or COMException or IOException or System.ComponentModel.Win32Exception or TypeLoadException or TypeInitializationException)
        {
            code = e is CaptureFailureException failure ? failure.Code : captured ? "Control.Unavailable" : "Capture.Unavailable";
            message = code + "：离线自检未通过，请提供此报告。错误类型：" + e.GetType().Name + "，HRESULT=0x" + e.HResult.ToString("X8");
        }
        finally { window.Close(); }
        var inputs = Native.InputRequests - input; var activations = Native.ActivationRequests - foreground;
        var success = engine && captured && recognized && written && inputs == 0 && activations == 0;
        if (!success && code == "SelfTest.Ready") { code = "SelfTest.InputDetected"; message = "自检发现非预期的全局输入或窗口激活请求。"; }
        var report = new EnvironmentReport(success, code, message, EmbeddedAssets.OcrPresent, engine, captured, recognized, written,
            capture.Backend, dpi, area, Environment.OSVersion.VersionString, RuntimeInformation.ProcessArchitecture.ToString(),
            typeof(EnvironmentSelfTest).Assembly.GetName().Version?.ToString() ?? "", inputs, activations, DateTime.UtcNow);
        var path = outputPath ?? OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(path, JsonSerializer.Serialize(report, SettingsStore.JsonOptions));
        return report;
    }
    public static int Run()
    {
        using var context = new ApplicationContext(); using var timer = new System.Windows.Forms.Timer { Interval = 50 }; var exit = 1;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try { exit = (await CheckAsync()).Success ? 0 : 1; }
            finally { context.ExitThread(); }
        };
        timer.Start(); Application.Run(context); return exit;
    }
}
