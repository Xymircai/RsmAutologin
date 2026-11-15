// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Drawing;
using System.IO;
using EasLoginHelper;

namespace EasLoginHelper.Tests;

internal static partial class Program
{
    private static void RunBackgroundSuite()
    {
        Run("capture geometry maps physical coordinates", TestCaptureGeometry);
        PumpAsync(() => RunAsync("WGC captures a background window without activation", TestWindowCapture));
        foreach (var scenario in new[] { "stable", "repaint", "changed", "busy", "target-edit", "uncertain", "no-frame" })
            PumpAsync(() => RunAsync("background transaction: " + scenario, () => TestBackgroundTransaction(scenario)));
        PumpAsync(() => RunAsync("EAS monitor works under continuous activity without focus", TestBackgroundMonitor));
    }
    private static void TestCaptureGeometry()
    {
        var mapped = CaptureGeometry.Map(new Rectangle(450, 350, 160, 60), new Rectangle(100, 150, 1000, 700), new Size(1250, 875));
        Check(mapped == new Rectangle(438, 250, 200, 75), "capture coordinate scaling is incorrect: " + mapped);
    }
    private static async Task TestWindowCapture()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "EAS.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixture, true);
        var link = Path.Combine(_work, "capture.lnk"); CreateShortcut(link, fixture);
        using var process = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = false, ArgumentList = { "--fixture", Path.Combine(_work, "capture-fixture.json"), "login-capture" } })!;
        try
        {
            var locator = new LoginLocator(new TargetPolicy(link), () => null); LoginPage? page = null;
            for (var i = 0; i < 50 && page is null; i++)
            { await Task.Delay(100); process.Refresh(); if (process.MainWindowHandle != IntPtr.Zero) page = await Task.Run(() => locator.Find(process.MainWindowHandle)); }
            Check(page is not null, "capture fixture did not expose login fields");
            var foreground = Native.GetForegroundWindow();
            using var capture = new WindowCaptureSource();
            using var bitmap = await capture.CaptureAsync(page!.Window, page.Captcha, CancellationToken.None);
            bitmap.Save(Path.Combine(_work, "offline-captcha-wgc.png"));
            using var ocr = new CaptchaRecognizer(); var result = await ocr.RecognizeAsync(bitmap, CancellationToken.None);
            Check(result.Accepted && result.Text == "4826", "captured captcha was not recognized: " + result);
            Check(Native.GetForegroundWindow() == foreground, "capture activated a window");
            Native.ShowWindow(page.Window, 6);
            try { using var rejected = await capture.CaptureAsync(page.Window, page.Captcha, CancellationToken.None); throw new Exception("minimized capture was accepted"); }
            catch (InvalidOperationException) { }
            Check(Native.IsIconic(page.Window), "capture restored a minimized window without instruction");
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }

    private sealed class SequenceFrames(string scenario) : ICaptchaFrameSource
    {
        private int _frame;
        public Task<Bitmap> CaptureAsync(IntPtr window, Rectangle rectangle, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (scenario == "no-frame") throw new InvalidOperationException("offline frame unavailable");
            var index = Interlocked.Increment(ref _frame);
            var value = scenario == "changed" && index > 1 ? "0099" : "4826";
            var bitmap = DrawCaptcha(value);
            bitmap.SetPixel(0, 0, Color.FromArgb(scenario == "repaint" ? Math.Min(index, 250) : value == "0099" ? 99 : 48, 2, 3));
            return Task.FromResult(bitmap);
        }
    }
    private sealed class TestRecognizer(string scenario, Func<Task>? duringRecognition = null) : ICaptchaRecognizer
    {
        public async Task<CaptchaResult> RecognizeAsync(Bitmap source, CancellationToken cancellation)
        {
            if (duringRecognition is not null) await duringRecognition();
            cancellation.ThrowIfCancellationRequested();
            return new(scenario == "changed" && source.GetPixel(0, 0).R == 99 ? "0099" : "4826", scenario == "uncertain" ? .2f : .99f, "offline recognition");
        }
    }

    private static async Task TestBackgroundTransaction(string scenario)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "EAS.exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixture, true);
        var link = Path.Combine(_work, "background.lnk"); CreateShortcut(link, fixture);
        var report = Path.Combine(_work, "background-" + scenario + ".json");
        using var process = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = false, ArgumentList = { "--fixture", report, "background-success" } })!;
        try
        {
            var policy = new TargetPolicy(link); var locator = new LoginLocator(policy, () => null); LoginPage? page = null;
            for (var i = 0; i < 50 && page is null; i++)
            { await Task.Delay(100); process.Refresh(); if (process.MainWindowHandle != IntPtr.Zero) page = await Task.Run(() => locator.Find(process.MainWindowHandle)); }
            Check(page is not null, "background fixture was not located");
            var id = NewId(); Vault.Save(id, "offline_user", TestSecret); using var activity = new InputActivity(false);
            var callbacks = 0;
            async Task Activity()
            {
                if (scenario == "busy") for (var i = 0; i < 40; i++) { activity.RecordPhysicalKey(new IntPtr(12345)); activity.RecordPhysicalPointer(); }
                if (scenario == "target-edit" && Interlocked.Increment(ref callbacks) == 1)
                { File.WriteAllText(report + ".command", "edit-password"); activity.RecordPhysicalKey(page!.Window); await Task.Delay(250); }
            }
            var frames = new SequenceFrames(scenario); var ocr = new TestRecognizer(scenario, Activity);
            var episode = new EasLoginEpisode(); var gate = new LoginAttemptGate(); gate.ObserveLoginPage(page!.ProcessKey);
            var inputs = Native.InputRequests; var activations = Native.ActivationRequests;
            var transaction = new EasLoginTransaction(policy, locator, activity, Vault, ocr, frames, loginTimeout: TimeSpan.FromSeconds(3));
            await transaction.RunAsync(page, new(id, "offline", "offline_user"), episode, true, true, gate, () => true, CancellationToken.None);
            var expected = scenario is "uncertain" or "no-frame" ? 0 : 1;
            Check(episode.Counts.Submissions == expected, "unexpected submission count, stage=" + episode.Stage);
            if (expected == 1) Check(episode.Login?.Outcome == LoginOutcome.Succeeded, "background login not confirmed: " + episode.Login);
            using (var observed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(report)))
            {
                Check(observed.RootElement.GetProperty("User").GetString() == "offline_user" && observed.RootElement.GetProperty("Password").GetString() == TestSecret, "default credentials did not win");
                Check(observed.RootElement.GetProperty("Sentinel").GetString() == "", "bound operation typed into an unrelated field");
            }
            Check(episode.Counts.Captcha == (scenario == "changed" ? 2 : expected), "captcha was rewritten unexpectedly: " + episode.Counts.Captcha);
            Check(Native.InputRequests == inputs && Native.ActivationRequests == activations, "transaction used global input or window activation");
            if (scenario == "target-edit") Check(episode.Counts.Password == 2, "manual target edits did not receive a final default correction");
            if (scenario == "repaint") Check(episode.Counts.Captcha == 1, "same digits were written more than once after repaint");
            File.WriteAllText(Path.Combine(_work, "transaction-" + scenario + "-result.json"), System.Text.Json.JsonSerializer.Serialize(new
            { episode.Stage, episode.Counts.UserName, episode.Counts.Password, episode.Counts.Captcha, episode.Counts.Submissions, Outcome = episode.Login?.Outcome.ToString() }));
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }

    private static async Task TestBackgroundMonitor()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "EAS.exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixture, true);
        var link = Path.Combine(_work, "background-monitor.lnk"); CreateShortcut(link, fixture);
        var store = Store("background-monitor"); var id = NewId(); new AccountService(store, Vault).Save(id, "offline", "offline_user", TestSecret);
        store.Update(s => s.ShortcutPath = link);
        using var process = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = false, ArgumentList = { "--fixture", Path.Combine(_work, "background-monitor.json"), "background-success" } })!;
        try
        {
            using var activity = new InputActivity(false);
            using var busy = new System.Threading.Timer(_ => { activity.RecordPhysicalKey(new IntPtr(23456)); activity.RecordPhysicalPointer(); }, null, 0, 15);
            var inputs = Native.InputRequests; var activations = Native.ActivationRequests;
            using var monitor = new LoginMonitor(store, activity, new TargetPolicy(link), Vault, new TestRecognizer("stable"), new InputCoordinator(), new SequenceFrames("stable"), TimeSpan.FromSeconds(3));
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(100);
                if (!monitor.Runtime.LoggedIn) continue;
                Check(monitor.Runtime.CaptchaWrites == 1 && monitor.Runtime.Submissions == 1, "monitor duplicated captcha/login");
                Check(Native.InputRequests == inputs && Native.ActivationRequests == activations, "monitor used global input or window activation"); return;
            }
            throw new InvalidOperationException("background monitor did not log in: " + monitor.Status);
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }
}
