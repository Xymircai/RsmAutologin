// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using EasLoginHelper;

namespace EasLoginHelper.Tests;

internal static partial class Program
{
    private static void TestModuleMigration()
    {
        var id = Guid.NewGuid(); var path = Path.Combine(_work, "schema1-" + id.ToString("N") + ".json");
        var original = JsonSerializer.Serialize(new { SchemaVersion = 1, Accounts = new[] { new AccountProfile(id, "旧账号", "offline_user") }, DefaultAccountId = id,
            AutoLoginEnabled = false, OcrEnabled = false, ShortcutPath = "old-shortcut.lnk" });
        File.WriteAllText(path, original);
        var readOnly = new SettingsStore(path, false);
        Check(readOnly.Current.SchemaVersion == 2 && File.ReadAllText(path) == original, "read-only diagnosis modified settings");
        var migrated = new SettingsStore(path);
        Check(migrated.Current.DefaultAccountId == id && !migrated.Current.AutoLoginEnabled && !migrated.Current.OcrEnabled && migrated.Current.ShortcutPath == "old-shortcut.lnk", "EAS metadata changed during migration");
        Check(migrated.Current.NewEra.Accounts.Count == 0 && migrated.Current.NewEra.DefaultAccountId is null, "new module inherited a credential ID");
        Check(File.ReadAllText(path + ".schema1.bak") == original && new SettingsStore(path).Current.DefaultAccountId == id, "backup/reload failed");
    }
    private static void TestModuleAccounts()
    {
        var store = Store("module-accounts"); var eas = new AccountService(store, Vault); var era = new AccountService(store, Vault, ModuleId.NewEra);
        var id = NewId(); eas.Save(id, "EAS 假账号", "offline_user", TestSecret);
        var copied = era.CopyFrom(ModuleId.Eas, id); Credentials.Add(copied);
        Check(copied != id && store.Current.NewEra.DefaultAccountId == copied && store.Current.DefaultAccountId == id, "copy did not create independent IDs/defaults");
        using (var secret = Vault.Read(copied)) Check(secret is not null && secret.Characters.AsSpan().SequenceEqual(TestSecret), "copied credential differs");
        era.Save(copied, "新纪元假账号", "offline_user2", "Other-Test!9");
        using (var secret = Vault.Read(id)) Check(secret is not null && secret.Characters.AsSpan().SequenceEqual(TestSecret), "editing copied credential changed EAS");
        era.Delete(copied); Check(Vault.Read(copied) is null && store.Current.DefaultAccountId == id && store.Current.NewEra.Accounts.Count == 0, "deleting new module changed EAS");
        using var retained = Vault.Read(id); Check(retained is not null, "EAS credential was removed");
    }
    private static void TestModuleValidation()
    {
        var store = Store("module-invalid"); var id = Guid.NewGuid();
        store.Update(s => s.Accounts.Add(new(id, "A", "offline_user")));
        var original = File.ReadAllText(store.FilePath);
        try { store.Update(s => s.NewEra.Accounts.Add(new(id, "B", "offline_user"))); throw new Exception("duplicate cross-module ID accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(store.FilePath) == original, "failed update changed config");
        var missing = Path.Combine(_work, "missing-module.json"); File.WriteAllText(missing, "{\"SchemaVersion\":2,\"Eas\":{}}");
        try { _ = new SettingsStore(missing); throw new Exception("missing module was silently reset"); } catch (InvalidDataException) { }
        var a = new IntPtr(123); var b = new IntPtr(456); using var activity = new InputActivity(false);
        var baselineA = activity.KeyboardGenerationFor(a); _ = activity.KeyboardGenerationFor(b); activity.RecordPhysicalKey(a);
        Check(activity.KeyboardGenerationFor(a) == baselineA + 1 && activity.KeyboardGenerationFor(b) == 0, "other module reset keyboard counters");
    }
    private static void TestNewEraClassification()
    {
        Check(NewEraLocator.ClassifyError(NewEraFixtureForm.Conflict) == NewEraError.SessionConflict, "session conflict not recognized");
        Check(NewEraLocator.ClassifyError("用户名或密码错误") == NewEraError.CredentialsRejected, "password error misclassified");
        Check(NewEraLocator.ClassifyError("网络错误") == NewEraError.Other, "network error misclassified");
        Check(NewEraLocator.ClassifyError("请重新输入密码") == NewEraError.None, "generic instruction allowed retry");
    }
    private static void TestNewEraGate()
    {
        var gate = new NewEraAttemptGate(); Check(gate.Observe("process-A"), "initial process not observed");
        Check(gate.Reserve() && !gate.Reserve(), "unconfirmed error allowed retry");
        Check(gate.Reserve(true) && !gate.Reserve(true), "more than one session retry allowed");
        Check(!gate.Observe("process-A") && !gate.Reserve(true), "window rebuild rearmed failed session");
        gate.Observe("process-B"); Check(gate.Reserve(), "new process could not log in");
        gate.Observe("process-A"); Check(!gate.Reserve(true), "switching processes rearmed failed session");
        gate.MarkSucceeded(); Check(gate.Observe("process-A") && gate.Reserve(), "confirmed success did not allow later login");
        gate.Reset(); Check(gate.Observe("process-A") && gate.Reserve(), "explicit retry failed");
    }
    private static void TestInputCoordinator()
    {
        var coordinator = new InputCoordinator(); var first = coordinator.TryEnter();
        Check(first is not null && coordinator.TryEnter() is null, "two modules entered concurrently");
        first!.Dispose(); first.Dispose(); using var second = coordinator.TryEnter();
        Check(second is not null && coordinator.TryEnter() is null, "lease release was not idempotent");
    }
    private static void TestDualLayout()
    {
        var store = Store("dual-layout"); store.Update(s =>
        {
            s.Accounts.Add(new(Guid.NewGuid(), "EAS 示例账号", "offline_eas"));
            s.NewEra.Accounts.Add(new(Guid.NewGuid(), "新纪元示例账号", "offline_new_era"));
        });
        using var form = new AccountsForm(store, new AccountService(store, Vault), Vault);
        form.Show(); Application.DoEvents();
        foreach (var module in Enum.GetValues<ModuleId>())
        {
            form.SelectModule(module); Application.DoEvents(); CheckButtons(form);
            var labels = Descendants(form).OfType<Label>().ToArray(); var signature = labels.Single(l => l.Text == UiTheme.Signature);
            Check(signature.Visible && signature.Font.Bold && signature.ForeColor == UiTheme.Blue, "signature missing or wrong style");
            var grid = Descendants(form).OfType<DataGridView>().Single();
            Check(grid.Rows.Count == 1 && (string)grid.Rows[0].Cells[2].Value! == (module == ModuleId.Eas ? "offline_eas" : "offline_new_era"), "module shows another module's accounts");
            using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            image.Save(Path.Combine(_work, "ui-" + Modules.Key(module) + ".png"));
        }
        form.Hide();
        using var editor = new AccountEditForm(new AccountService(store, Vault), null); editor.Show(); Application.DoEvents(); CheckButtons(editor);
        using var editorImage = new Bitmap(editor.Width, editor.Height); editor.DrawToBitmap(editorImage, new Rectangle(0, 0, editor.Width, editor.Height));
        editorImage.Save(Path.Combine(_work, "ui-editor.png")); editor.Hide();
        static IEnumerable<Control> Descendants(Control control) { foreach (Control child in control.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    }

    private static async Task TestNewEraSubmission(string scenario)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Xsjzb.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixture, true);
        var link = Path.Combine(_work, "new-era-fixture.lnk"); CreateShortcut(link, fixture);
        var report = Path.Combine(_work, "new-era-" + scenario + ".json");
        using var process = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = false, ArgumentList = { "--new-era-fixture", report, scenario } })!;
        try
        {
            var policy = new TargetPolicy(link, ModuleId.NewEra); var locator = new NewEraLocator(policy, scenario != "native-session"); NewEraPage? page = null;
            for (var i = 0; i < 50 && page is null; i++)
            { await Task.Delay(100); process.Refresh(); if (process.MainWindowHandle != IntPtr.Zero) page = await Task.Run(() => locator.Find(process.MainWindowHandle)); }
            Check(page is not null, "login fields not located alongside read-only error edit");
            var id = NewId(); Vault.Save(id, "offline_user", TestSecret); var account = new AccountProfile(id, "offline", "offline_user");
            using var activity = new InputActivity(false); var gate = new NewEraAttemptGate(); gate.Observe(page!.ProcessKey);
            var trace = new List<string>();
            var controller = new NewEraController(policy, locator, activity, Vault, stage =>
            {
                trace.Add(stage);
                if (scenario == "interrupted" && stage == "NewEra.CredentialsFilled" || scenario == "session-interrupted" && stage == "NewEra.SessionConflict") activity.RecordPhysicalKey(page.Window);
            }, TimeSpan.FromSeconds(2));
            var result = await controller.RunAsync(page, account, scenario != "fill-only", gate, false, () => true, CancellationToken.None);
            File.WriteAllText(Path.Combine(_work, "new-era-" + scenario + "-result.json"), JsonSerializer.Serialize(new { Result = result, Trace = trace }));
            await Task.Delay(200);
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            var submissions = json.RootElement.GetProperty("Submissions").GetInt32();
            var expected = scenario is "session-success" or "session-loop" or "stale-session" or "native-session" or "recreated-session" or "session-interrupted" ? 2 : scenario == "fill-only" ? 0 : 1;
            Check(submissions == expected && result.Submissions == expected, "unexpected submissions: " + submissions + "; result=" + result);
            if (scenario is "success" or "session-success" or "stale-session" or "native-session" or "recreated-session") Check(result.LoginOutcome == LoginOutcome.Succeeded, "did not reach account-set UI: " + result);
            if (scenario is "password-error" or "session-loop") Check(result.LoginOutcome == LoginOutcome.Rejected, "error did not stop: " + result);
            if (scenario == "pending") Check(result.LoginOutcome == LoginOutcome.Pending, "unknown result not preserved");
            if (expected == 2) Check(json.RootElement.GetProperty("RefilledAfterConflict").GetBoolean(), "retry submitted without retyping credentials");
            File.WriteAllText(Path.Combine(_work, "new-era-" + scenario + "-result.json"), JsonSerializer.Serialize(new { Result = result, Trace = trace }));
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }

    private static void TestDualScaledLayout()
    {
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var store = Store("scale-" + (int)(scale * 100));
            using var form = new AccountsForm(store, new AccountService(store, Vault), Vault);
            form.Show(); Application.DoEvents();
            form.Scale(new SizeF(scale, scale)); Application.DoEvents();
            foreach (var module in Enum.GetValues<ModuleId>())
            { form.SelectModule(module); Application.DoEvents(); CheckButtons(form); }
            using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
            image.Save(Path.Combine(_work, "ui-scale-" + (int)(scale * 100) + ".png")); form.Hide();
        }
    }

    private static async Task TestNewEraMonitor(string scenario)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Xsjzb.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixture, true);
        var link = Path.Combine(_work, "monitor-new-era.lnk"); CreateShortcut(link, fixture);
        var report = Path.Combine(_work, scenario + ".json");
        var store = Store(scenario); var id = NewId();
        new AccountService(store, Vault, ModuleId.NewEra).Save(id, "离线测试", "offline_user", TestSecret);
        store.Update(s => s.NewEra.ShortcutPath = link);
        using var process = Process.Start(new ProcessStartInfo(fixture) { UseShellExecute = false, ArgumentList = { "--new-era-fixture", report, scenario } })!;
        try
        {
            for (var i = 0; i < 30 && process.MainWindowHandle == IntPtr.Zero; i++) { await Task.Delay(100); process.Refresh(); }
            var controlledWindow = process.MainWindowHandle;
            Check(controlledWindow != IntPtr.Zero, "fixture window did not appear");
            using var activity = new InputActivity(false);
            using var busy = new System.Threading.Timer(_ => { activity.RecordPhysicalKey(new IntPtr(23456)); activity.RecordPhysicalPointer(); }, null, 0, 20);
            using var monitor = new NewEraMonitor(store, activity, new TargetPolicy(link, ModuleId.NewEra), Vault, new InputCoordinator(),
                TimeSpan.FromMilliseconds(scenario == "monitor-late" ? 700 : 2000), () => IntPtr.Zero, () => false, true);
            var sawPending = false;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(100);
                sawPending |= monitor.Runtime.LoginAttempted && !monitor.Runtime.LoggedIn && monitor.Status.Contains("暂未确认", StringComparison.Ordinal);
                if (monitor.Runtime.LoggedIn)
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(report));
                    var count = json.RootElement.GetProperty("Submissions").GetInt32();
                    Check(count == (scenario == "monitor-late" ? 1 : 2), "monitor submitted an unexpected number of times");
                    if (scenario == "monitor-late") Check(sawPending, "late-success fixture did not exercise timeout confirmation");
                    await Task.Delay(500);
                    Check(monitor.Runtime.LoggedIn && monitor.Runtime.Submissions == count, "confirmed result was lost"); return;
                }
            }
            process.Refresh();
            var foreground = Native.GetAncestor(Native.GetForegroundWindow(), Native.GaRoot);
            var detail = new { Fixture = process.MainWindowHandle.ToInt64(), Foreground = foreground.ToInt64(),
                ForegroundClass = Native.ClassName(foreground), ForegroundAllowed = new TargetPolicy(link, ModuleId.NewEra).Allows(foreground),
                FixtureClass = Native.ClassName(process.MainWindowHandle), FixtureVisible = Native.IsWindowVisible(process.MainWindowHandle),
                LastPage = monitor.LastPage is not null, Runtime = monitor.Runtime };
            File.WriteAllText(Path.Combine(_work, scenario + "-diagnostic.json"), JsonSerializer.Serialize(detail));
            throw new InvalidOperationException("monitor did not confirm sign-in: " + JsonSerializer.Serialize(detail));
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }
}

internal sealed class NewEraFixtureForm : Form
{
    internal const string Conflict = "该账号已于 2026/11/13 20:09:29 在电脑 离线测试机 登录。请重新输入登录信息获取授权！";
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
    private readonly TextBox _user = new() { Bounds = new Rectangle(210, 60, 180, 32), AccessibleName = "用户名" };
    private readonly TextBox _password = new() { Bounds = new Rectangle(210, 110, 180, 32), UseSystemPasswordChar = true, AccessibleName = "密码" };
    private readonly TextBox _error = new() { Bounds = new Rectangle(30, 200, 440, 60), ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, ForeColor = Color.Red };
    private readonly string _report;
    private int _submissions;
    private bool _conflicted, _refilled;
    private readonly System.Windows.Forms.Timer _close = new() { Interval = 45000 };
    public NewEraFixtureForm(string report, string scenario)
    {
        _report = report; Text = "容诚专用版 [Ver: 离线测试]"; ClientSize = new Size(500, 360); AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Text = "用户名：", Bounds = new Rectangle(50, 60, 130, 30) });
        Controls.Add(new Label { Text = "密 码：", Bounds = new Rectangle(50, 110, 130, 30) });
        Controls.AddRange([_user, _password, _error]);
        Controls.Add(new CheckBox { Text = "保存登录信息", Bounds = new Rectangle(50, 160, 180, 32) });
        if (scenario == "stale-session") _error.Text = Conflict;
        var confirm = new Button { Text = "确定", Bounds = new Rectangle(110, 290, 110, 36) };
        var cancel = new Button { Text = "取消", Bounds = new Rectangle(280, 290, 110, 36) }; cancel.Click += (_, _) => Close();
        confirm.Click += (_, _) =>
        {
            _submissions++;
            var correct = _user.Text == "offline_user" && _password.Text == Program.TestSecret;
            if (scenario == "recreated-session" && _submissions == 1)
            {
                Hide(); var replacement = new NewEraFixtureForm(report, "recreated-second") { _submissions = 1, _conflicted = true };
                replacement._error.Text = Conflict; replacement.FormClosed += (_, _) => Close(); replacement.Show();
            }
            else if (scenario is "session-success" or "session-loop" or "stale-session" or "native-session" or "session-interrupted" or "monitor-session" && _submissions == 1 || scenario == "session-loop")
            { _conflicted = true; _user.Clear(); _password.Clear(); _error.Text = Conflict; }
            else if (scenario == "password-error" || !correct) _error.Text = "用户名或密码错误";
            else if (scenario != "pending")
            {
                if (scenario == "monitor-late")
                {
                    var delayed = new System.Windows.Forms.Timer { Interval = 3000 };
                    delayed.Tick += (_, _) => { delayed.Stop(); delayed.Dispose(); ShowWorkspace(); }; delayed.Start();
                }
                else ShowWorkspace();
            }
            Save();
        };
        Controls.AddRange([confirm, cancel]);
        void Changed(object? sender, EventArgs e) { if (_conflicted && _user.Text == "offline_user" && _password.Text == Program.TestSecret) _refilled = true; Save(); }
        _user.TextChanged += Changed; _password.TextChanged += Changed;
        Shown += (_, _) =>
        {
            Save(); _close.Start();
        }; _close.Tick += (_, _) => Close(); FormClosed += (_, _) => _close.Dispose();
    }
    private void ShowWorkspace()
    {
        foreach (Control control in Controls) control.Visible = false;
        Text = "账套操作";
        Controls.Add(new Label { Text = "选择客户", Bounds = new Rectangle(30, 30, 100, 30) });
        Controls.Add(new Button { Text = "打开", Bounds = new Rectangle(110, 290, 110, 36) });
        Controls.Add(new Button { Text = "取消", Bounds = new Rectangle(280, 290, 110, 36) });
    }
    private void Save() => File.WriteAllText(_report, JsonSerializer.Serialize(new { Submissions = _submissions, RefilledAfterConflict = _refilled }));
}
