// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace EasLoginHelper;

// Runs only on explicit diagnostic invocation. Uses an owned, non-activating dummy window.
internal static class CaptureSelfTest
{
    private sealed class SampleWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        public SampleWindow()
        {
            Text = "后台捕获自检 · 离线假数据"; ClientSize = new Size(320, 110); BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(new Label { Text = "4826", Bounds = new Rectangle(30, 24, 260, 65), Font = new Font("Arial", 32, FontStyle.Bold), ForeColor = Color.Black });
        }
    }
    public static int Run()
    {
        using var window = new SampleWindow(); using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        var exit = 1;
        window.Shown += (_, _) => timer.Start();
        timer.Tick += async (_, _) =>
        {
            timer.Stop(); var captured = false; string? error = null;
            try
            {
                using var source = new WindowCaptureSource();
                using var image = await source.CaptureAsync(window.Handle, window.RectangleToScreen(new Rectangle(15, 15, 290, 80)), CancellationToken.None);
                captured = image.Width > 20 && image.Height > 10;
            }
            catch (Exception e) { error = e.GetType().Name; }
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "capture-selftest.json"), JsonSerializer.Serialize(new
            {
                CreatedUtc = DateTime.UtcNow, Captured = captured, ErrorType = error,
                GlobalInputRequests = Native.InputRequests, ForegroundRequests = Native.ActivationRequests,
                Note = "Owned offline window only. No credentials, real application pixels, or saved images."
            }, SettingsStore.JsonOptions));
            exit = captured && Native.InputRequests == 0 && Native.ActivationRequests == 0 ? 0 : 1;
            window.Close();
        };
        System.Windows.Forms.Application.Run(window); return exit;
    }
}
