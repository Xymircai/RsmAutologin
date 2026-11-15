// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using EasLoginHelper;

namespace EasLoginHelper.Tests;

internal static partial class Program
{
    private static void RunPortableSuite()
    {
        Run("automatic target policy works across installation paths", TestAutomaticPolicy);
        Run("legacy missing shortcut metadata remains readable", TestShortcutCompatibility);
        Run("model, charset, help and complete licenses are embedded", TestEmbeddedDistribution);
        Run("multi-resolution icons are embedded and applied to windows and tray", TestBranding);
        Run("main layout reflows across laptop workspaces and font scales", TestResponsiveMain);
        Run("account and information dialogs resize without overlapping rows", TestResponsiveDialogs);
        Run("window, column and split layout survives restart and offscreen restore", TestLayoutPersistence);
        PumpAsync(() => RunAsync("embedded OCR, background capture and bound write self-test", async () =>
        {
            var result = await EnvironmentSelfTest.CheckAsync(outputPath: Path.Combine(_work, "environment-selftest.json"));
            Check(result.Success && result.GlobalInputRequests == 0 && result.ForegroundRequests == 0, "environment self-test failed: " + result);
        }));
        foreach (var module in Enum.GetValues<ModuleId>())
            PumpAsync(() => RunAsync("shortcut-free startup and process restart: " + module, () => TestAutomaticMonitor(module)));
    }
    private static void TestAutomaticPolicy()
    {
        var eas = new TargetPolicy(ModuleId.Eas); var era = new TargetPolicy(ModuleId.NewEra);
        foreach (var prefix in new[] { @"C:\Other User\Audit", @"D:\Portable", @"E:\任意安装位置" })
        {
            Check(eas.Allows(Path.Combine(prefix, "EAS.exe")) && eas.Allows(Path.Combine(prefix, "eas.updater.EXE")), "EAS path was pinned");
            Check(era.Allows(Path.Combine(prefix, "Xsjzb.exe")), "New Era path was pinned");
            Check(!eas.Allows(Path.Combine(prefix, "Xsjzb.exe")) && !era.Allows(Path.Combine(prefix, "EAS.exe")), "modules crossed");
            Check(!eas.Allows(Path.Combine(prefix, "EAS.exe.txt")) && !era.Allows(Path.Combine(prefix, "other.exe")), "unsupported filename allowed");
        }
        Check(!eas.Allows((string?)null) && !eas.Allows("EAS.exe"), "invalid process path accepted");
    }
    private static void TestShortcutCompatibility()
    {
        var store = Store("no-shortcuts");
        store.Update(s => { s.Eas.ShortcutPath = ""; s.NewEra.ShortcutPath = "missing-file.lnk"; });
        var restored = new SettingsStore(store.FilePath);
        Check(restored.Current.Eas.ShortcutPath == "" && restored.Current.NewEra.ShortcutPath == "missing-file.lnk", "legacy metadata rejected");
        _ = new TargetPolicy(ModuleId.Eas); _ = new TargetPolicy(ModuleId.NewEra);
    }
    private static void TestEmbeddedDistribution()
    {
        Check(EmbeddedAssets.OcrPresent, "OCR resources missing"); var assets = EmbeddedAssets.Ocr();
        Check(assets.Model.Length > 10_000_000 && assets.Charset.Length > 1000, "unexpected embedded model");
        var licenses = EmbeddedAssets.Licenses();
        foreach (var notice in new[] { "Mozilla Public License", "ONNX-Runtime.txt", "ONNX-ThirdPartyNotices.txt", "DotNet.txt", "DotNet-ThirdPartyNotices.txt", "ddddocr-MIT.txt" })
            Check(licenses.Contains(notice, StringComparison.Ordinal), "license missing: " + notice);
        Check(EmbeddedAssets.Text("EasLoginHelper.Help.txt").Length > 1000, "embedded instructions missing");
    }
    private static void TestBranding()
    {
        var bytes = EmbeddedAssets.Read("EasLoginHelper.Branding.Icon");
        Check(BitConverter.ToUInt16(bytes, 2) == 1 && BitConverter.ToUInt16(bytes, 4) == 15, "icon directory incomplete");
        foreach (var size in new[] { 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80, 96, 128, 256 })
        {
            var icon = Branding.IconForSize(size); Check(icon.Size == new Size(size, size), "missing icon size " + size);
            using var bitmap = icon.ToBitmap(); var blue = 0; var white = 0; var green = 0;
            Check(bitmap.GetPixel(0, 0).A == 0, "icon lost corner transparency");
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
            {
                var color = bitmap.GetPixel(x, y); if (color.A < 200) continue;
                if (color.B > color.R * 1.5 && color.B > color.G) blue++;
                if (color.R > 230 && color.G > 230 && color.B > 230) white++;
                if (color.G > color.R * 1.5 && color.G > color.B * 1.1) green++;
            }
            Check(blue > size && white > size / 2 && green > 0, "brand artwork missing at " + size);
        }
        using var tray = new NotifyIcon { Icon = Branding.TrayIcon };
        Check(tray.Icon.Handle != IntPtr.Zero, "tray icon has no native handle");
        var store = Store("brand"); using var form = new AccountsForm(store, new AccountService(store, Vault), Vault);
        form.Show(); Application.DoEvents();
        Check(form.Icon is not null && ReferenceEquals(form.Icon, Branding.WindowIcon(form.DeviceDpi)), "window did not use application icon");
        Check(EmbeddedAssets.SourceUrl.EndsWith("/v" + Branding.Version) && UiTheme.Signature.Contains(Branding.FileVersion), "version labels diverged");
        using var snapshot = new Bitmap(form.Width, form.Height); form.DrawToBitmap(snapshot, new Rectangle(0, 0, form.Width, form.Height));
        snapshot.Save(Path.Combine(_work, "branded-window.png")); form.Hide();
    }
    private static IEnumerable<Control> AllControls(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var nested in AllControls(child)) yield return nested; }
    }
    private static void AssertLayout(Control root)
    {
        CheckButtons(root);
        foreach (var table in AllControls(root).OfType<TableLayoutPanel>())
        {
            var children = table.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            for (var a = 0; a < children.Length; a++) for (var b = a + 1; b < children.Length; b++)
                Check(!children[a].Bounds.IntersectsWith(children[b].Bounds), "overlapping table controls: " + children[a].Text + " / " + children[b].Text);
        }
        foreach (var label in AllControls(root).OfType<WrappingLabel>().Where(c => c.Visible))
        {
            Check(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height - 2, "clipped text: " + label.Text);
            Check(label.Right <= label.Parent!.ClientSize.Width + 2, "label exceeds available width");
        }
    }
    private static void TestResponsiveMain()
    {
        foreach (var workspace in new[] { new Size(1366, 768), new Size(1920, 1080), new Size(2560, 1440) })
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var store = Store("responsive"); using var form = new AccountsForm(store, new AccountService(store, Vault), Vault);
            var fonts = AllControls(form).Prepend(form).Select(c => (Control: c, Font: c.Font)).ToArray();
            form.Show(); Application.DoEvents();
            // Simulate scaled content at constrained workspaces without changing the user's system DPI.
            form.Scale(new SizeF(scale, scale)); foreach (var entry in fonts) entry.Control.Font = new Font(entry.Font.FontFamily, entry.Font.Size * scale, entry.Font.Style);
            var maximum = new Size(Math.Min(workspace.Width - 40, 1100), Math.Min(workspace.Height - 100, 900));
            var working = new Rectangle(0, 0, workspace.Width, workspace.Height - 48);
            form.ClientSize = maximum; Application.DoEvents();
            foreach (var size in new[] { maximum, new Size(500, 360), new Size(720, 480) })
            {
                form.ClientSize = size; UiTheme.FitWorkingArea(form, working); Application.DoEvents();
                Check(working.Contains(form.Bounds), "window exceeds simulated laptop working area");
                foreach (var module in Enum.GetValues<ModuleId>())
                {
                    form.SelectModule(module); form.ShowStatus("正在等待登录页面。此状态文本用于验证较长文字在窄窗口和较大字体下自动换行，不覆盖按钮或底部版本说明。"); Application.DoEvents(); AssertLayout(form);
                }
            }
            form.ClientSize = new Size(600, 440); UiTheme.FitWorkingArea(form, working); Application.DoEvents();
            using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height));
            image.Save(Path.Combine(_work, $"responsive-{workspace.Width}-{scale * 100:0}.png")); form.Hide();
        }
    }
    private static void TestResponsiveDialogs()
    {
        var store = Store("dialog-layout"); var service = new AccountService(store, Vault);
        using var edit = new AccountEditForm(service, null); using var copy = new CopyAccountForm([new(Guid.NewGuid(), "离线测试账号", "offline_user")]);
        using var info = new InformationForm();
        foreach (var dialog in new Form[] { edit, copy, info })
        {
            Check(dialog.FormBorderStyle == FormBorderStyle.Sizable, "dialog is fixed-size"); dialog.Show(); Application.DoEvents();
            foreach (var size in new[] { new Size(340, 260), new Size(700, 540) }) { dialog.ClientSize = size; Application.DoEvents(); AssertLayout(dialog); }
            dialog.Hide();
        }
    }
    private static void TestLayoutPersistence()
    {
        var store = Store("persist-layout");
        using (var first = new AccountsForm(store, new AccountService(store, Vault), Vault))
        {
            first.Show(); first.ClientSize = new Size(740, 520); Application.DoEvents();
            var split = AllControls(first).OfType<SplitContainer>().Single(); split.SplitterDistance = Math.Max(split.Panel1MinSize, split.SplitterDistance - 30);
            first.Close();
        }
        var layouts = UiLayoutStore.ForSettings(store); var saved = layouts.Get("accounts");
        Check(saved.WidthDip > 500 && saved.ColumnsDip.Length == 3 && saved.SplitRatio > 0, "layout not persisted");
        saved.X = -50000; saved.Y = -50000; layouts.Save();
        using var restored = new AccountsForm(store, new AccountService(store, Vault), Vault); restored.Show(); Application.DoEvents();
        Check(Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(restored.Bounds)), "offscreen window not recovered"); AssertLayout(restored); restored.Hide();
    }
    private static async Task TestAutomaticMonitor(ModuleId module)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, module == ModuleId.Eas ? "EAS.exe" : "Xsjzb.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixture, true);
        var store = Store("automatic-monitor"); var id = NewId(); new AccountService(store, Vault, module).Save(id, "offline", "offline_user", TestSecret);
        store.Update(s => s.For(module).ShortcutPath = "this-shortcut-does-not-exist.lnk");
        // The production discovery rules are used, restricted to this test's process image to protect live applications.
        var policy = new TargetPolicy(module, w => string.Equals(Native.ProcessPath(w), fixture, StringComparison.OrdinalIgnoreCase));
        using var activity = new InputActivity(false); var coordinator = new InputCoordinator();
        using var eas = module == ModuleId.Eas ? new LoginMonitor(store, activity, policy, Vault, new TestRecognizer("stable"), coordinator, new SequenceFrames("stable"), TimeSpan.FromSeconds(3)) : null;
        using var era = module == ModuleId.NewEra ? new NewEraMonitor(store, activity, policy, Vault, coordinator) : null;
        await Task.Delay(400); var inputs = Native.InputRequests; var activations = Native.ActivationRequests;
        for (var round = 0; round < 2; round++)
        {
            var report = Path.Combine(_work, "automatic-" + module + "-" + round + ".json");
            var args = module == ModuleId.Eas ? "--fixture" : "--new-era-fixture";
            using var process = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = false, ArgumentList = { args, report, module == ModuleId.Eas ? "background-success" : "success" } })!;
            try
            {
                var done = false;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    await Task.Delay(100);
                    if (!File.Exists(report)) continue;
                    try
                    {
                        using var observed = JsonDocument.Parse(File.ReadAllText(report));
                        var runtime = eas?.Runtime ?? era!.Runtime;
                        if (observed.RootElement.GetProperty("Submissions").GetInt32() == 1 && runtime.LoggedIn) { done = true; break; }
                    }
                    catch (Exception e) when (e is IOException or JsonException) { }
                }
                Check(done, "automatic monitor did not log in after startup/restart: " + (eas?.Status ?? era?.Status));
                await Task.Delay(400); using var result = JsonDocument.Parse(File.ReadAllText(report));
                Check(result.RootElement.GetProperty("Submissions").GetInt32() == 1, "automatic monitor repeated submission");
            }
            finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(1500)) process.Kill(); } }
            await Task.Delay(400);
        }
        Check(Native.InputRequests == inputs && Native.ActivationRequests == activations, "discovery used global input or activation");
    }
}
