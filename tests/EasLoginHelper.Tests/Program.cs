// SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using EasLoginHelper;

namespace EasLoginHelper.Tests;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static string _work = "";
    internal static readonly string TestSecret = "Offline-Test!秘密7";
    private static readonly string VaultPrefix = "EasLoginHelper.Tests/" + Guid.NewGuid().ToString("N") + "/";
    private static readonly CredentialVault Vault = new(VaultPrefix);
    private static readonly List<Guid> Credentials = [];
    [STAThread]
    private static int Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        if (args.Length >= 3 && args[0] == "--fixture") { System.Windows.Forms.Application.Run(new FixtureForm(args[1], args[2])); return 0; }
        if (args.Length >= 3 && args[0] == "--new-era-fixture") { System.Windows.Forms.Application.Run(new NewEraFixtureForm(args[1], args[2])); return 0; }
        _work = Path.GetFullPath(args.Length >= 2 && args[0] == "--output" ? args[1] : Path.Combine("artifacts", "selftests", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(_work);
        try
        {
            if (args.Contains("--portable-only"))
            {
                RunPortableSuite();
            }
            else if (args.Contains("--background-ui-only"))
            {
                RunBackgroundSuite();
            }
            else if (args.Contains("--capture-only"))
            {
                Run("capture geometry maps window-relative physical coordinates", TestCaptureGeometry);
                PumpAsync(() => RunAsync("WGC reads captcha behind other windows without activation", TestWindowCapture));
            }
            else if (args.Contains("--monitor-ui-only"))
            {
                PumpAsync(() => RunAsync("New Era foreground monitor fills, retries and confirms", () => TestNewEraMonitor("monitor-session")));
                PumpAsync(() => RunAsync("New Era monitor confirms late success without resubmitting", () => TestNewEraMonitor("monitor-late")));
            }
            else
            {
            if (!args.Contains("--ui-only") && !args.Contains("--login-ui-only") && !args.Contains("--dual-ui-only"))
            {
            Run("settings round trip and plaintext exclusion", TestSettings);
            Run("corrupt settings preserved", TestCorruption);
            Run("invalid default and duplicate IDs rejected", TestInvalidMetadata);
            Run("credential CRUD, default replacement, blank-password preservation", TestAccounts);
            Run("credential rollback on metadata write failure", TestRollback);
            Run("control characters rejected before saving", TestControlCharacters);
            Run("typing in another window does not mark EAS fields as edited", TestWindowScopedActivity);
            Run("auto-login migration and persisted opt-out", TestAutoLoginSettings);
            Run("automatic submission requires full trusted fill", TestLoginReadiness);
            Run("failed and uncertain login attempts are never repeated", TestLoginAttemptGate);
            Run("only a confirmed sign-in or explicit retry rearms login", TestLoginRearm);
            Run("v1 migration preserves IDs/preferences and backs up original", TestModuleMigration);
            Run("independent module credentials and copying", TestModuleAccounts);
            Run("cross-module credential IDs and missing modules rejected", TestModuleValidation);
            Run("session conflict classification is narrowly scoped", TestNewEraClassification);
            Run("New Era retries bounded across recreated windows and process switches", TestNewEraGate);
            Run("input coordination excludes concurrent modules", TestInputCoordinator);
            Run("real EAS geometry with native status-bar edits", TestGeometry);
            Run("ambiguous, missing-button, and non-login layouts rejected", TestGeometryRejections);
            Run("CTC repeated digits and blank separators", TestRepeatedDigits);
            Run("OCR confidence and non-digit rejection", TestConfidence);
            Run("relative calibration maps move/resize/125%-150% DPI", TestMapping);
            Run("blank or changed visual anchors rejected", TestAnchors);
            Run("native OCR telemetry disabled before initialization", TestOcrPrivacy);
            PumpAsync(() => RunAsync("missing OCR model degrades without account impact", TestMissingModel));
            PumpAsync(() => RunAsync("local model recognizes captcha at 100/125/150 percent scale", () => TestScreenshot(args)));
            PumpAsync(() => RunAsync("local model synthetic digit samples", TestGeneratedOcr));
            }
            if (!args.Contains("--no-ui") && !args.Contains("--login-ui-only") && !args.Contains("--dual-ui-only"))
            {
                Run("account management UI layout", TestAccountsLayout);
                PumpAsync(() => RunAsync("bound UIA credential fill, no submission", () => TestNativeFill(false)));
                PumpAsync(() => RunAsync("background window capture and captcha fill, no submission", () => TestNativeFill(false, true)));
                PumpAsync(() => RunAsync("focus changes do not redirect bound input", () => TestNativeFill(true)));
                PumpAsync(() => RunAsync("tray monitor fills once, preserves edits, handles account switch and relogin", TestMonitor));
                PumpAsync(() => RunAsync("first account saved after tray startup triggers fill", TestFirstAccount));
            }
            if (args.Contains("--login-ui-only") || !args.Contains("--no-ui") && !args.Contains("--ui-only") && !args.Contains("--dual-ui-only"))
            {
                PumpAsync(() => RunAsync("bound login click reaches signed-in UI exactly once", () => TestLoginSubmission("login-success")));
                PumpAsync(() => RunAsync("login error stops without resubmission", () => TestLoginSubmission("login-error")));
                PumpAsync(() => RunAsync("unknown login result stops without resubmission", () => TestLoginSubmission("login-pending")));
                PumpAsync(() => RunAsync("changed captcha prevents login click", () => TestLoginSubmission("login-stale")));
                PumpAsync(() => RunAsync("physical input does not cancel bound login", () => TestLoginSubmission("login-interrupt")));
            }
            if (args.Contains("--dual-ui-only") || !args.Contains("--no-ui") && !args.Contains("--login-ui-only") && !args.Contains("--ui-only"))
            {
                Run("dual-module UI layout and exact signature", TestDualLayout);
                Run("dual-module UI at 100/125/150/200 percent layout scale", TestDualScaledLayout);
                foreach (var scenario in new[] { "success", "session-success", "session-loop", "password-error", "pending", "stale-session", "interrupted", "fill-only", "native-session", "recreated-session", "session-interrupted" })
                    PumpAsync(() => RunAsync("New Era UI: " + scenario, () => TestNewEraSubmission(scenario)));
                PumpAsync(() => RunAsync("New Era foreground monitor fills, retries and confirms", () => TestNewEraMonitor("monitor-session")));
                PumpAsync(() => RunAsync("New Era monitor confirms late success without resubmitting", () => TestNewEraMonitor("monitor-late")));
            }
            if (!args.Contains("--no-ui") && !args.Contains("--ui-only") && !args.Contains("--login-ui-only") && !args.Contains("--dual-ui-only")) { RunBackgroundSuite(); RunPortableSuite(); }
            }
        }
        finally { foreach (var id in Credentials) Vault.Delete(id); }
        var failures = Results.Count(x => !(bool)x.GetType().GetProperty("Passed")!.GetValue(x)!);
        File.WriteAllText(Path.Combine(_work, "results.json"), JsonSerializer.Serialize(new { Passed = Results.Count - failures, Failed = failures, Cases = Results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT {Results.Count - failures}/{Results.Count} passed; report: {_work}");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Run(string name, Action test)
    {
        try { test(); Results.Add(new { Name = name, Passed = true, Detail = "" }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { Results.Add(new { Name = name, Passed = false, Detail = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    private static async Task RunAsync(string name, Func<Task> test)
    {
        try { await test(); Results.Add(new { Name = name, Passed = true, Detail = "" }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { Results.Add(new { Name = name, Passed = false, Detail = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    private static void PumpAsync(Func<Task> action)
    {
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        timer.Tick += async (_, _) => { timer.Stop(); try { await action(); } finally { context.ExitThread(); } };
        timer.Start(); System.Windows.Forms.Application.Run(context);
    }
    private static Guid NewId() { var id = Guid.NewGuid(); Credentials.Add(id); return id; }
    private static SettingsStore Store(string name) => new(Path.Combine(_work, name + "." + Guid.NewGuid().ToString("N") + ".json"));

    private static void TestSettings()
    {
        var store = Store("metadata");
        var account = new AccountProfile(Guid.NewGuid(), "测试账号", "offline_user");
        store.Update(s => { s.Accounts.Add(account); s.DefaultAccountId = account.Id; s.OcrEnabled = false; });
        var restored = new SettingsStore(store.FilePath);
        Check(restored.Current.DefaultAccount == account && !restored.Current.OcrEnabled, "metadata changed across restart");
        var text = File.ReadAllText(store.FilePath);
        Check(!text.Contains(TestSecret) && !text.Contains("Password", StringComparison.OrdinalIgnoreCase), "secret field found in metadata");
        Check(!Directory.EnumerateFiles(_work, "*.tmp").Any(), "orphan temp config");
    }
    private static void TestCorruption()
    {
        var path = Path.Combine(_work, "corrupt.json"); File.WriteAllText(path, "{broken");
        try { _ = new SettingsStore(path); throw new InvalidOperationException("bad JSON accepted"); }
        catch (JsonException) { }
        Check(File.ReadAllText(path) == "{broken", "corrupt input was overwritten");
    }
    private static void TestInvalidMetadata()
    {
        var store = Store("validation");
        try { store.Update(s => s.DefaultAccountId = Guid.NewGuid()); throw new InvalidOperationException("invalid default accepted"); }
        catch (InvalidDataException) { }
        Check(!File.Exists(store.FilePath), "invalid config was saved");
        var account = new AccountProfile(Guid.NewGuid(), "test", "test");
        try { store.Update(s => { s.Accounts.Add(account); s.Accounts.Add(account); }); throw new InvalidOperationException("duplicates accepted"); }
        catch (InvalidDataException) { }
    }
    private static void TestAccounts()
    {
        var store = Store("accounts"); var service = new AccountService(store, Vault);
        var first = NewId(); var second = NewId();
        service.Save(first, "first", "offline_one", TestSecret);
        service.Save(second, "second", "offline_two", "Second-Test!8");
        using (var secret = Vault.Read(first)) Check(secret is not null && new string(secret.Characters) == TestSecret, "vault read mismatch");
        service.Save(first, "renamed", "offline_renamed", "");
        using (var secret = Vault.Read(first))
        {
            Check(secret is not null && new string(secret.Characters) == TestSecret, "empty edit removed password");
            secret!.Dispose(); Check(secret.Characters.All(c => c == '\0'), "secret memory not cleared");
        }
        Check(store.Current.DefaultAccountId == first, "first account not default");
        Check(!File.ReadAllText(store.FilePath).Contains(TestSecret), "password leaked into JSON");
        service.Delete(first);
        Check(Vault.Read(first) is null && store.Current.DefaultAccountId == second, "delete/default reassignment failed");
        service.Delete(second); Check(store.Current.DefaultAccountId is null && store.Current.Accounts.Count == 0, "last deletion left invalid default");
    }
    private static void TestRollback()
    {
        var path = Path.Combine(_work, "blocked-metadata"); Directory.CreateDirectory(path);
        var store = new SettingsStore(path); var id = NewId();
        try { new AccountService(store, Vault).Save(id, "test", "offline_user", TestSecret); throw new InvalidOperationException("metadata write unexpectedly succeeded"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Check(Vault.Read(id) is null && store.Current.Accounts.Count == 0, "failed save left credentials behind");
    }
    private static void TestControlCharacters()
    {
        var store = Store("control"); var id = NewId();
        try { new AccountService(store, Vault).Save(id, "test", "user", "fake\rsecret"); throw new InvalidOperationException("return in secret accepted"); }
        catch (ArgumentException) { }
        Check(Vault.Read(id) is null, "invalid secret persisted");
    }
    private static void TestWindowScopedActivity()
    {
        using var activity = new InputActivity();
        var target = new IntPtr(100); var other = new IntPtr(200);
        var baseline = activity.KeyboardGenerationFor(target); var global = activity.Generation;
        activity.RecordPhysicalKey(other);
        Check(!LoginMonitor.HasKeyboardEdits(activity, target, baseline), "another window marked EAS as edited");
        Check(activity.Generation > global, "global interruption protection was lost");
        activity.RecordPhysicalKey(target);
        Check(LoginMonitor.HasKeyboardEdits(activity, target, baseline), "EAS manual input was not protected");
    }
    private static void TestAutoLoginSettings()
    {
        var path = Path.Combine(_work, "legacy-autologin.json");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"Accounts\":[]}");
        var store = new SettingsStore(path);
        Check(store.Current.AutoLoginEnabled, "existing config did not adopt requested auto-login default");
        store.Update(s => s.AutoLoginEnabled = false);
        Check(!new SettingsStore(path).Current.AutoLoginEnabled, "manual-login preference was not preserved");
    }
    private static void TestLoginReadiness()
    {
        var full = new FillResult(FillOutcome.Filled, "", "image-hash")
            { CaptchaFilled = true, CaptchaValue = "4826", CredentialsFilled = true, InputGeneration = 0 };
        Check(LoginReadiness.CanAutoSubmit(true, true, full), "full fill was rejected");
        Check(!LoginReadiness.CanAutoSubmit(false, true, full), "disabled auto-login submitted");
        Check(!LoginReadiness.CanAutoSubmit(true, false, full), "unconfirmed credentials submitted");
        Check(!LoginReadiness.CanAutoSubmit(true, true, full with { CaptchaFilled = false }), "missing captcha submitted");
        Check(!LoginReadiness.CanAutoSubmit(true, true, full with { Outcome = FillOutcome.Partial }), "uncertain OCR submitted");
        Check(!LoginReadiness.CanAutoSubmit(true, true, full with { CaptchaHash = null }), "unverified image submitted");
    }
    private static void TestLoginAttemptGate()
    {
        var gate = new LoginAttemptGate(); Check(gate.ObserveLoginPage("page"), "initial page not armed");
        var accepted = 0; Parallel.For(0, 16, _ => { if (gate.TryReserve("page")) Interlocked.Increment(ref accepted); });
        Check(accepted == 1 && gate.HasSubmitted("page"), "more than one concurrent attempt allowed");
        Check(!gate.ObserveLoginPage("page") && !gate.TryReserve("page"), "same failed form was automatically rearmed");
    }
    private static void TestLoginRearm()
    {
        var gate = new LoginAttemptGate(); gate.ObserveLoginPage("page"); gate.TryReserve("page");
        gate.MarkSucceeded("page"); Check(gate.ObserveLoginPage("page") && gate.TryReserve("page"), "logout/relogin was not armed");
        gate.ResetExplicitly(); Check(gate.ObserveLoginPage("page") && gate.TryReserve("page"), "explicit retry was not armed");
        Check(!gate.ObserveLoginPage("new-window") && !gate.TryReserve("new-window"), "failed/uncertain submit rearmed when the window changed");
        gate.MarkSucceeded("page");
        Check(gate.ObserveLoginPage("new-window") && gate.TryReserve("new-window"), "confirmed sign-in did not allow a later login window");
    }

    private static async Task TestLoginSubmission(string scenario)
    {
        var fixtureExe = Path.Combine(AppContext.BaseDirectory, "EAS.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixtureExe, true);
        var link = Path.Combine(_work, "fixture.lnk"); CreateShortcut(link, fixtureExe);
        var report = Path.Combine(_work, scenario + ".json");
        using var process = Process.Start(new ProcessStartInfo(fixtureExe) { UseShellExecute = false, ArgumentList = { "--fixture", report, scenario } })!;
        try
        {
            var policy = new TargetPolicy(link); var locator = new LoginLocator(policy, () => null); LoginPage? page = null;
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(100); process.Refresh(); var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero) page = await Task.Run(() => locator.Find(handle));
                if (page is not null) break;
            }
            Check(page is not null && page.SubmitButton?.Element is not null, "offline login/button was not detected");
            for (var i = 0; i < 20; i++)
            {
                var fresh = await Task.Run(() => locator.Find(page!.Window));
                if (fresh is not null && LoginLocator.ReadNonSecretValue(fresh.Fields[0]) == "offline_user" &&
                    LoginLocator.ReadNonSecretValue(fresh.Fields[2]) == "4826") { page = fresh; break; }
                await Task.Delay(50);
            }
            Check(LoginLocator.ReadNonSecretValue(page!.Fields[0]) == "offline_user" &&
                LoginLocator.ReadNonSecretValue(page.Fields[2]) == "4826", "fixture values were not ready before login test");
            using var activity = new InputActivity(false);
            var generation = activity.Generation;
            if (scenario == "login-interrupt") activity.RecordPhysicalKey(page!.Window);
            var gate = new LoginAttemptGate(); gate.ObserveLoginPage(page!.Identity);
            var controller = new AutoLoginController(policy, locator, activity, resultTimeout: TimeSpan.FromSeconds(5));
            var result = await controller.SubmitAsync(page, new AccountProfile(Guid.NewGuid(), "test", "offline_user"),
                scenario == "login-stale" ? "1234" : "4826", null, generation, false, () => gate.TryReserve(page.Identity), CancellationToken.None);
            var expected = scenario switch
            {
                "login-success" => LoginOutcome.Succeeded, "login-error" => LoginOutcome.Rejected,
                "login-pending" => LoginOutcome.Pending, "login-interrupt" => LoginOutcome.Succeeded, _ => LoginOutcome.NotSubmitted
            };
            Check(result.Outcome == expected, "unexpected login result: " + result.Outcome + "; " + result.Message);
            if (result.Submitted && scenario != "login-success")
            {
                var again = await controller.SubmitAsync(page, new AccountProfile(Guid.NewGuid(), "test", "offline_user"), "4826", null,
                    activity.Generation, false, () => gate.TryReserve(page.Identity), CancellationToken.None);
                Check(!again.Submitted, "error/timeout was submitted again");
            }
            using var observed = JsonDocument.Parse(File.ReadAllText(report));
            Check(observed.RootElement.GetProperty("Submissions").GetInt32() == (result.Submitted ? 1 : 0), "login click count mismatch");
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }
    private static FieldGeometry[] SampleGeometry() => [
        new(new Rectangle(1057, 566, 446, 56), false, "用户名"),
        new(new Rectangle(1057, 646, 446, 56), true, "密码"),
        new(new Rectangle(1057, 726, 308, 56), false, "验证码"),
        new(new Rectangle(1, 1354, 2507, 24), false, "status"),
        new(new Rectangle(2508, 1353, 38, 27), false, "status")];
    private static void TestGeometry()
    {
        var selected = LoginGeometry.SelectLoginFields(SampleGeometry(), true);
        Check(selected is not null && selected.SequenceEqual(new[] { 0, 1, 2 }), "native status bar prevents EAS detection");
        var crop = LoginGeometry.CaptchaBeside(SampleGeometry()[2].Bounds, SampleGeometry()[1].Bounds, 120);
        Check(crop.Left > 1365 && crop.Right == 1503 && crop.Width > 100, "captcha crop invalid");
    }
    private static void TestGeometryRejections()
    {
        Check(LoginGeometry.SelectLoginFields(SampleGeometry(), false) is null, "missing login button accepted");
        var noPassword = SampleGeometry().Select(f => f with { Password = false }).ToArray();
        Check(LoginGeometry.SelectLoginFields(noPassword, true) is null, "non-login search fields accepted");
        var ambiguous = SampleGeometry().Concat(new[] { SampleGeometry()[0] with { Bounds = new Rectangle(1057, 490, 446, 56) } }).ToArray();
        Check(LoginGeometry.SelectLoginFields(ambiguous, true) is null, "ambiguous field triple accepted");
    }
    private static void TestRepeatedDigits()
    {
        var logits = MakeLogits(new[] { 1, 1, 0, 1, 0, 2, 2, 0, 2 }, 3);
        var result = CtcDecoder.Decode(logits, 9, 3, ["", "4", "9"]);
        Check(result.Text == "4499" && result.Accepted, "CTC lost repeated digits");
    }
    private static float[] MakeLogits(int[] sequence, int classes)
    {
        var values = Enumerable.Repeat(-8f, sequence.Length * classes).ToArray();
        for (var i = 0; i < sequence.Length; i++) values[i * classes + sequence[i]] = 8;
        return values;
    }
    private static void TestConfidence()
    {
        Check(!new CaptchaResult("12A4", .99f, "").Accepted, "non-digit accepted");
        Check(!new CaptchaResult("1234", .79f, "").Accepted, "uncertain result accepted");
        Check(!new CaptchaResult("123", .99f, "").Accepted, "wrong length accepted");
        var logits = new float[6]; var result = CtcDecoder.Decode(logits, 2, 3, ["", "1", "2"]);
        Check(!result.Accepted, "flat model output accepted");
    }
    private static void TestMapping()
    {
        var c = new Calibration { OriginalClientWidth = 1600, OriginalClientHeight = 900, OriginalDpi = 96, Panel = new RelativeRect(.35, .2, .3, .6) };
        var mapped = VisualLocator.MapPanel(c, new Rectangle(200, 100, 2000, 1200), 120);
        Check(mapped.Width == 600 && mapped.Height == 675 && mapped.Left == 900, "125% mapping mismatch");
        var larger = VisualLocator.MapPanel(c, new Rectangle(-100, 50, 2400, 1500), 144);
        Check(larger.Width == 720 && larger.Height == 810 && larger.Left == 740, "150% mapping mismatch");
    }
    private static void TestAnchors()
    {
        var blank = Enumerable.Repeat((byte)180, 80 * 24).ToArray();
        Check(!VisualLocator.AnchorMatches(blank, blank), "blank anchor authorized input");
        var text = blank.ToArray(); for (var i = 300; i < 650; i++) text[i] = 20;
        Check(VisualLocator.AnchorMatches(text, text), "valid text anchor rejected");
        Check(!VisualLocator.AnchorMatches(text, Enumerable.Repeat((byte)255, 80 * 24).ToArray()), "changed page accepted");
    }
    private static async Task TestMissingModel()
    {
        using var recognizer = new CaptchaRecognizer(Path.Combine(_work, "does-not-exist"));
        using var image = DrawCaptcha("4826");
        var result = await recognizer.RecognizeAsync(image, CancellationToken.None);
        Check(!result.Accepted && result.Message.Contains("手动"), "missing OCR did not degrade");
    }
    private static void TestOcrPrivacy()
    {
        using var recognizer = new CaptchaRecognizer();
        Check(Environment.GetEnvironmentVariable("ORT_DISABLE_TELEMETRY") == "1", "native telemetry has not been disabled for this process");
    }
    private static async Task TestScreenshot(string[] args)
    {
        var index = Array.IndexOf(args, "--sample");
        if (index >= 0 && index + 1 >= args.Length) throw new ArgumentException("--sample requires a screenshot path");
        using var image = index < 0 ? DrawCaptcha("4826") : CropSample(args[index + 1]);
        var expected = index < 0 ? "4826" : "4904";
        using var recognizer = new CaptchaRecognizer();
        foreach (var scale in new[] { 1f, 1.25f, 1.5f })
        {
            using var variant = new Bitmap(image, new Size((int)(image.Width * scale), (int)(image.Height * scale)));
            var watch = Stopwatch.StartNew();
            var result = await recognizer.RecognizeAsync(variant, CancellationToken.None);
            Console.WriteLine($"OCR scaled sample scale={scale}: text={result.Text}, confidence={result.Confidence:F3}, elapsed={watch.ElapsedMilliseconds}ms");
            Check(result.Text == expected && result.Accepted, "captcha not recognized confidently at scale " + scale);
        }

        static Bitmap CropSample(string path)
        {
            using var source = new Bitmap(path);
            Check(source.Width == 2560 && source.Height == 1320, "unexpected screenshot dimensions");
            return source.Clone(new Rectangle(1377, 734, 126, 55), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        }
    }
    private static async Task TestGeneratedOcr()
    {
        using var recognizer = new CaptchaRecognizer(); var accepted = 0; var correct = 0;
        foreach (var text in new[] { "4826", "0099", "1234", "7788", "5601", "9876", "2222", "7063", "1010", "8635" })
        {
            using var image = DrawCaptcha(text);
            image.Save(Path.Combine(_work, "synthetic-" + text + ".png"));
            var result = await recognizer.RecognizeAsync(image, CancellationToken.None);
            Console.WriteLine($"OCR synthetic {text}: text={result.Text}, confidence={result.Confidence:F3}, accepted={result.Accepted}");
            if (result.Text == text) correct++;
            if (result.Accepted) { accepted++; Check(result.Text == text, "confidently incorrect synthetic OCR result"); }
        }
        File.WriteAllText(Path.Combine(_work, "ocr-synthetic.json"), JsonSerializer.Serialize(new { Samples = 10, Correct = correct, Accepted = accepted }));
        Console.WriteLine($"OCR synthetic: correct={correct}/10, accepted={accepted}/10");
        Check(correct >= 8, "synthetic OCR accuracy below 8/10");
    }

    private static void TestAccountsLayout()
    {
        var store = Store("layout");
        using var form = new AccountsForm(store, new AccountService(store, Vault));
        form.Show(); System.Windows.Forms.Application.DoEvents();
        Check(form.Controls.Count > 0 && form.ClientSize.Width >= 700, "accounts UI failed to initialize");
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
        image.Save(Path.Combine(_work, "accounts-ui.png")); CheckButtons(form); form.Hide();
        using var editor = new AccountEditForm(new AccountService(store, Vault), null);
        editor.Show(); System.Windows.Forms.Application.DoEvents();
        CheckButtons(editor);
        using var editorImage = new Bitmap(editor.Width, editor.Height);
        editor.DrawToBitmap(editorImage, new Rectangle(0, 0, editorImage.Width, editorImage.Height));
        editorImage.Save(Path.Combine(_work, "account-editor-ui.png")); editor.Hide();
    }
    private static void CheckButtons(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button && child.Visible) Check(parent.ClientRectangle.Contains(child.Bounds), "clipped button: " + child.Text + " child=" + child.Bounds + " parent=" + parent.ClientRectangle);
            CheckButtons(child);
        }
    }

    private static async Task TestNativeFill(bool interrupt, bool enableOcr = false)
    {
        var fixtureExe = Path.Combine(AppContext.BaseDirectory, "EAS.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "EasLoginHelper.Tests.exe"), fixtureExe, true);
        var link = Path.Combine(_work, "fixture.lnk"); CreateShortcut(link, fixtureExe);
        var policy = new TargetPolicy(link);
        Check(!policy.Allows(Environment.ProcessPath), "unrelated process allowed");
        var report = Path.Combine(_work, interrupt ? "fixture-interrupt.json" : enableOcr ? "fixture-ocr.json" : "fixture-fill.json");
        using var process = Process.Start(new ProcessStartInfo(fixtureExe) { UseShellExecute = false, ArgumentList = { "--fixture", report, interrupt ? "interrupt" : "normal" } })!;
        try
        {
            var locator = new LoginLocator(policy, () => null); LoginPage? page = null;
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(100);
                process.Refresh(); page = await Task.Run(() => process.MainWindowHandle == IntPtr.Zero ? null : locator.Find(process.MainWindowHandle));
                if (page is not null) break;
            }
            if (page is null)
            {
                process.Refresh(); var handle = process.MainWindowHandle;
                var background = await Task.Run(() => handle == IntPtr.Zero ? null : locator.Find(handle));
                var current = ForegroundWindow(); GetWindowThreadProcessId(current, out var activePid);
                var edits = handle == IntPtr.Zero ? [] : await Task.Run(() => System.Windows.Automation.AutomationElement.FromHandle(handle)
                    .FindAll(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Edit))
                    .Cast<System.Windows.Automation.AutomationElement>().Select(e => new { e.Current.Name, e.Current.IsPassword, Bounds = e.Current.BoundingRectangle.ToString() }).ToArray());
                File.WriteAllText(Path.Combine(_work, interrupt ? "interrupt-diagnostic.json" : "fill-diagnostic.json"), JsonSerializer.Serialize(new { FixturePid = process.Id, Handle = handle.ToInt64(), ForegroundPid = activePid, BackgroundDetected = background is not null, Edits = edits }));
            }
            Check(page is not null, "fixture was not detected as a bound login form");
            using var activity = new InputActivity();
            using var recognizer = new CaptchaRecognizer();
            var id = NewId(); Vault.Save(id, "offline_user", TestSecret);
            var controller = new FillController(policy, locator, activity, Vault, recognizer);
            var result = await controller.FillAsync(page!, new AccountProfile(id, "test", "offline_user"), enableOcr, false, CancellationToken.None);
            await Task.Delay(200);
            using var observed = JsonDocument.Parse(File.ReadAllText(report)); var root = observed.RootElement;
            Check(root.GetProperty("Submissions").GetInt32() == 0, "login button was submitted");
            if (interrupt)
            {
                Check(result.Outcome == FillOutcome.Filled, "focus change incorrectly blocked bound fill");
                Check(root.GetProperty("Password").GetString() == TestSecret && root.GetProperty("Sentinel").GetString() == "", "bound input reached an unintended field");
            }
            else
            {
                Check(result.Outcome == FillOutcome.Filled, "native fill failed: " + result.Message);
                Check(root.GetProperty("User").GetString() == "offline_user" && root.GetProperty("Password").GetString() == TestSecret, "native field values mismatch");
                Check(root.GetProperty("Captcha").GetString() == (enableOcr ? "4826" : ""), "captcha content mismatch");
            }
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }

    private static async Task TestMonitor()
    {
        var fixtureExe = Path.Combine(AppContext.BaseDirectory, "EAS.exe");
        var link = Path.Combine(_work, "fixture.lnk"); CreateShortcut(link, fixtureExe);
        var report = Path.Combine(_work, "fixture-monitor.json");
        var store = Store("monitor-settings"); var service = new AccountService(store, Vault);
        var first = NewId(); var second = NewId();
        service.Save(first, "first", "offline_default", TestSecret);
        service.Save(second, "second", "offline_second", TestSecret);
        store.Update(s => { s.ShortcutPath = link; s.OcrEnabled = false; });
        using var process = Process.Start(new ProcessStartInfo(fixtureExe) { UseShellExecute = false, ArgumentList = { "--fixture", report, "normal" } })!;
        try
        {
            using var activity = new InputActivity(); using var recognizer = new CaptchaRecognizer();
            using var monitor = new LoginMonitor(store, activity, new TargetPolicy(link), Vault, recognizer);
            Console.WriteLine($"WAIT_FOREGROUND offline monitor fixture PID={process.Id}");
            await WaitUser("offline_default", 30);
            File.WriteAllText(report + ".command", "edit");
            await WaitUser("manual_user", 4); await Task.Delay(2400);
            Check(ReadUser() == "manual_user", "monitor overwrote an existing manual edit");
            monitor.RequestFill(second);
            await WaitUser("offline_second", 10);
            Check(store.Current.DefaultAccountId == first, "one-off account switch changed default");
            File.WriteAllText(report + ".command", "show-workspace"); await Task.Delay(4200);
            File.WriteAllText(report + ".command", "show-login");
            await WaitUser("offline_default", 10);
            using var final = JsonDocument.Parse(File.ReadAllText(report));
            Check(final.RootElement.GetProperty("Submissions").GetInt32() == 0, "monitor submitted login");

            string? ReadUser()
            {
                try { using var value = JsonDocument.Parse(File.ReadAllText(report)); return value.RootElement.GetProperty("User").GetString(); }
                catch (Exception e) when (e is IOException or JsonException) { return null; }
            }
            async Task WaitUser(string expected, int seconds)
            {
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline) { if (ReadUser() == expected) return; await Task.Delay(100); }
                throw new InvalidOperationException("monitor did not reach expected field state: " + expected + "; status=" + monitor.Status);
            }
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }
    private static async Task TestFirstAccount()
    {
        var fixtureExe = Path.Combine(AppContext.BaseDirectory, "EAS.exe");
        var link = Path.Combine(_work, "fixture.lnk"); CreateShortcut(link, fixtureExe);
        var report = Path.Combine(_work, "fixture-first-account.json");
        var store = Store("first-account-settings"); store.Update(s => { s.ShortcutPath = link; s.OcrEnabled = false; });
        using var process = Process.Start(new ProcessStartInfo(fixtureExe) { UseShellExecute = false, ArgumentList = { "--fixture", report, "normal" } })!;
        try
        {
            using var activity = new InputActivity(); using var recognizer = new CaptchaRecognizer();
            using var monitor = new LoginMonitor(store, activity, new TargetPolicy(link), Vault, recognizer);
            await Task.Delay(1500);
            var id = NewId(); new AccountService(store, Vault).Save(id, "first", "offline_new_account", TestSecret);
            for (var i = 0; i < 150; i++)
            {
                await Task.Delay(100);
                if (!File.Exists(report)) continue;
                using var observed = JsonDocument.Parse(File.ReadAllText(report));
                if (observed.RootElement.GetProperty("Password").GetString() == TestSecret)
                {
                    Check(observed.RootElement.GetProperty("User").GetString() == "offline_new_account", "first account username mismatch");
                    Check(observed.RootElement.GetProperty("Submissions").GetInt32() == 0, "first account flow submitted login");
                    return;
                }
            }
            throw new InvalidOperationException("new account did not trigger automatic fill; status=" + monitor.Status);
        }
        finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }
    }
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr ForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static void CreateShortcut(string path, string executable)
    {
        object? shell = null, link = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!); link = ((dynamic)shell!).CreateShortcut(path); ((dynamic)link).TargetPath = executable; ((dynamic)link).Save(); }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }

    internal static Bitmap DrawCaptcha(string text)
    {
        var image = new Bitmap(128, 54);
        using var g = Graphics.FromImage(image); g.Clear(Color.FromArgb(230, 248, 255)); g.SmoothingMode = SmoothingMode.AntiAlias;
        var colors = new[] { Color.FromArgb(134, 213, 192), Color.FromArgb(65, 201, 195), Color.FromArgb(112, 188, 230), Color.FromArgb(49, 174, 205) };
        using var font = new Font("Arial", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        for (var i = 0; i < text.Length; i++)
        {
            var state = g.Save(); g.TranslateTransform(17 + i * 31, 27); g.RotateTransform(i % 2 == 0 ? -8 : 5);
            using var brush = new SolidBrush(colors[i]); g.FillRectangle(brush, -13, -19, 26, 38);
            g.DrawString(text[i].ToString(), font, Brushes.Black, new RectangleF(-12, -14, 26, 32), new StringFormat { Alignment = StringAlignment.Center });
            g.Restore(state);
        }
        return image;
    }
}

internal sealed class FixtureForm : Form
{
    private readonly bool _backgroundFixture;
    protected override bool ShowWithoutActivation => _backgroundFixture;
    protected override CreateParams CreateParams
    {
        get { return base.CreateParams; }
    }
    private readonly TextBox _user = new() { AutoSize = false, Bounds = new Rectangle(60, 80, 400, 54), AccessibleName = "用户名" };
    private readonly TextBox _password = new() { AutoSize = false, Bounds = new Rectangle(60, 160, 400, 54), AccessibleName = "密码", UseSystemPasswordChar = true };
    private readonly TextBox _captcha = new() { AutoSize = false, Bounds = new Rectangle(60, 240, 260, 54), AccessibleName = "验证码" };
    private readonly TextBox _sentinel = new() { Bounds = new Rectangle(60, 420, 400, 30), AccessibleName = "停止输入测试框" };
    private readonly string _report;
    private int _submissions;
    private readonly System.Windows.Forms.Timer _close = new() { Interval = 60000 };
    private readonly System.Windows.Forms.Timer _commands = new() { Interval = 100 };
    public FixtureForm(string report, string scenario)
    {
        _backgroundFixture = true;
        _report = report; Text = "EAS 离线测试窗口 · 全部为假数据"; ClientSize = new Size(620, 510);
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.None; TopMost = !_backgroundFixture;
        Font = new Font("Microsoft YaHei UI", 10);
        Controls.Add(new Label { Text = "EasyAudit 离线测试：不连接任何服务", Bounds = new Rectangle(60, 20, 480, 40) });
        Controls.Add(_user); Controls.Add(_password); Controls.Add(_captcha); Controls.Add(_sentinel);
        if (scenario.StartsWith("login-")) { _user.Text = "offline_user"; _password.Text = Program.TestSecret; _captcha.Text = "4826"; }
        var picture = new PictureBox { Bounds = new Rectangle(334, 240, 126, 54), Image = Program.DrawCaptcha("4826"), SizeMode = PictureBoxSizeMode.StretchImage };
        Controls.Add(picture);
        var login = new Button { Text = "登录", AccessibleName = "登录", Bounds = new Rectangle(60, 330, 400, 42) };
        login.Click += (_, _) =>
        {
            _submissions++; Save();
            if (scenario is "login-success" or "login-interrupt" || scenario.StartsWith("background-"))
            {
                foreach (var control in new Control[] { _user, _password, _captcha, _sentinel, login, picture }) control.Visible = false;
                Controls.Add(new Button { Text = "⏻ 注销登录", AccessibleName = "poweroff 注销 登录", Bounds = new Rectangle(60, 330, 400, 42) });
            }
            if (scenario == "login-error") Controls.Add(new Label { Text = "验证码错误", AccessibleName = "验证码错误", Bounds = new Rectangle(60, 380, 400, 30) });
            if (scenario == "login-pending")
            {
                foreach (var control in new Control[] { _user, _password, _captcha, login }) control.Visible = false;
                Controls.Add(new Label { Text = "请退出登录后重试", AccessibleName = "请退出登录后重试", Bounds = new Rectangle(60, 380, 400, 30) });
            }
        }; Controls.Add(login);
        Controls.Add(new TextBox { Bounds = new Rectangle(1, 484, 600, 24), ReadOnly = true, Text = "offline status" });
        foreach (var field in new[] { _user, _password, _captcha, _sentinel }) field.TextChanged += (_, _) => Save();
        if (scenario == "interrupt") _user.TextChanged += (_, _) => { if (_user.Text.Length >= 3) _sentinel.Focus(); };
        _commands.Tick += (_, _) =>
        {
            var path = _report + ".command"; if (!File.Exists(path)) return;
            var command = File.ReadAllText(path); File.Delete(path);
            if (command == "edit") _user.Text = "manual_user";
            if (command == "edit-password") { _user.Text = "manual_user"; _password.Text = "Manually-Changed-Dummy"; }
            if (command == "show-workspace" || command == "show-login")
            {
                var visible = command == "show-login";
                foreach (var control in new Control[] { _user, _password, _captcha, _sentinel, login, picture }) control.Visible = visible;
                if (visible) { _user.Clear(); _password.Clear(); _captcha.Clear(); }
                foreach (var marker in Controls.OfType<Button>().Where(b => b.Name == "OfflineLogoutMarker").ToArray()) { Controls.Remove(marker); marker.Dispose(); }
                if (!visible) Controls.Add(new Button { Name = "OfflineLogoutMarker", Text = "注销登录", AccessibleName = "注销登录", Bounds = new Rectangle(60, 330, 400, 42) });
            }
        };
        Shown += (_, _) =>
        {
            Save(); _close.Start(); _commands.Start();
            if (_backgroundFixture) return;
            Activate(); SetForegroundWindow(Handle);
            // Windows can reject SetForegroundWindow for an unattended console child. Click only
            // our own unobscured, explicitly created offline field to establish the UI precondition.
            var activation = new System.Windows.Forms.Timer { Interval = 200 };
            activation.Tick += (_, _) =>
            {
                activation.Stop(); activation.Dispose();
                var point = _user.PointToScreen(new System.Drawing.Point(_user.Width / 2, _user.Height / 2));
                if (Native.OwnsPoint(Handle, point)) Native.Click(point);
            };
            activation.Start();
        };
        _close.Tick += (_, _) => Close();
        FormClosed += (_, _) => { _close.Dispose(); _commands.Dispose(); picture.Image?.Dispose(); };
    }
    private void Save() => File.WriteAllText(_report, JsonSerializer.Serialize(new { User = _user.Text, Password = _password.Text, Captcha = _captcha.Text, Sentinel = _sentinel.Text, Submissions = _submissions }));
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
