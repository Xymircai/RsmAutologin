// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Text.Json;

namespace EasLoginHelper;

internal static class WebsiteSelfTest
{
    internal const string Argument = "--website-selftest";

    internal static int Run()
    {
        var results = new Dictionary<string, bool>();
        var testDirectory = Path.Combine(Path.GetTempPath(), "EasLoginHelper-website-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(testDirectory, "accounts.json");
        var clock = new TestClock();
        var tickets = new WebsiteLaunchTickets(testDirectory, clock);
        var id = Guid.NewGuid();
        var error = "";
        try
        {
            var store = new WebsiteSettingsStore(path);
            foreach (var site in Website.All)
                store.Accounts(site).Save(id, "Offline test", site.Key + "-test", site.Key + "-password");

            var download = store.For(Website.Download);
            results["AccountIsolation"] = download.DefaultAccount?.UserName == "download-test" &&
                store.For(Website.Mail).DefaultAccount?.UserName == "mail-test" &&
                store.For(Website.EasyGo).DefaultAccount?.UserName == "easygo-test" &&
                !File.ReadAllText(path).Contains("-password", StringComparison.Ordinal);

            var mailToken = tickets.Issue(Website.Mail, id);
            results["WrongOrigin"] = !Ok(Request(Website.Mail, mailToken, "https://fixture.invalid/#/login"));
            var mail = Request(Website.Mail, mailToken, Website.Mail.EntryUrl);
            results["MailCredentials"] = Ok(mail) && mail.GetProperty("username").GetString() == "mail-test" &&
                mail.GetProperty("password").GetString() == "mail-password" && mail.GetProperty("autoSubmit").GetBoolean();
            results["NoReplay"] = !Ok(Request(Website.Mail, mailToken, Website.Mail.EntryUrl));
            results["SingleSubmit"] = Ok(Request(Website.Mail, mailToken, Website.Mail.EntryUrl, "submit")) &&
                !Ok(Request(Website.Mail, mailToken, Website.Mail.EntryUrl, "submit"));
            results["ConfirmedOutcome"] = !Ok(Request(Website.Mail, mailToken, "https://fixture.invalid/#/home", "result", "success")) &&
                Ok(Request(Website.Mail, mailToken, "https://mail.rccpa.com.cn/#/mailbox/welcome", "result", "success")) &&
                !Ok(Request(Website.Mail, mailToken, Website.Mail.EntryUrl, "result", "failed")) &&
                tickets.Read(Website.Mail, mailToken)?.Stage == WebsiteStage.Success;

            var downloadToken = tickets.Issue(Website.Download, id);
            results["CrossSite"] = !Ok(Request(Website.Mail, downloadToken, Website.Mail.EntryUrl));
            store.Update(Website.Download, options => options.AutoLoginEnabled = true);
            var downloadReply = Request(Website.Download, downloadToken, Website.Download.EntryUrl);
            results["DownloadManual"] = Ok(downloadReply) && downloadReply.GetProperty("password").GetString() == "download-password" &&
                !downloadReply.GetProperty("autoSubmit").GetBoolean() &&
                downloadReply.GetProperty("submitAfterManualCaptcha").GetBoolean();
            results["DownloadWaiting"] = Ok(Request(Website.Download, downloadToken, Website.Download.EntryUrl, "result", "captcha")) &&
                tickets.Read(Website.Download, downloadToken)?.Reason == WebsiteReason.ManualCaptcha;
            results["ResultTimeout"] = Ok(Request(Website.Download, downloadToken, Website.Download.EntryUrl, "submit"));
            clock.Now += TimeSpan.FromSeconds(31);
            results["ResultTimeout"] &= tickets.Read(Website.Download, downloadToken)?.Stage == WebsiteStage.Unconfirmed;

            const string sso = "https://sso.rccpa.com.cn/realms/RSM/protocol/openid-connect/auth" +
                "?client_id=EasyGo&redirect_uri=https%3A%2F%2Fwww.rccpa.com.cn%2F%23%2Fcallbackfromkeycloak" +
                "&response_type=code&scope=openid&state=offline-test";
            var ssoToken = tickets.Issue(Website.EasyGo, id);
            results["EasyGoFlow"] = !Ok(Request(Website.EasyGo, ssoToken, Website.EasyGo.EntryUrl)) &&
                !Ok(Request(Website.EasyGo, ssoToken, sso.Replace("client_id=EasyGo", "client_id=other"))) &&
                Ok(Request(Website.EasyGo, ssoToken, sso));
            results["MissingTicket"] = !Ok(Request(Website.Mail, Guid.NewGuid().ToString("N"), Website.Mail.EntryUrl));

            store.Update(Website.Download, options => options.SubmitAfterManualCaptcha = false);
            var optOut = Request(Website.Download, tickets.Issue(Website.Download, id), Website.Download.EntryUrl);
            results["DownloadOptOut"] = Ok(optOut) && !optOut.GetProperty("submitAfterManualCaptcha").GetBoolean();

            var legacyWebsite = File.ReadAllText(path);
            File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(legacyWebsite,
                "\"SubmitAfterManualCaptcha\"\\s*:\\s*(?:true|false),\\s*", ""));
            var restored = new WebsiteSettingsStore(path);
            results["LegacyWebsiteSettings"] = restored.For(Website.Download).SubmitAfterManualCaptcha &&
                restored.For(Website.Download).DefaultAccountId == id;

            File.WriteAllText(path, """{"SchemaVersion":1,"Accounts":[],"AutoLoginEnabled":false,"OcrEnabled":true}""");
            var desktop = new SettingsStore(path, persistMigration: false);
            results["LegacySettings"] = desktop.Current.SchemaVersion == 2 && !desktop.Current.Eas.AutoLoginEnabled &&
                desktop.Current.Eas.OcrEnabled && desktop.Current.NewEra.Accounts.Count == 0;

            using var worker = new WebsiteWorkerClient();
            worker.SendAsync(new WebsiteRequest("ping")).GetAwaiter().GetResult();
            results["WorkerHandshake"] = true;
            using var form = new AccountsForm(desktop, new AccountService(desktop, new CredentialVault()), worker);
            var controls = Descendants(form).ToArray();
            results["WebsitePanelInMainWindow"] = controls.OfType<WebsitePanel>().Single().FindForm() == form &&
                controls.OfType<RoundedButton>().Count(button => button.Text == "网站登录") == 1;
        }
        catch (Exception failure) { error = failure.GetType().Name; }
        finally
        {
            foreach (var site in Website.All)
            {
                site.Vault.Delete(id);
            }
            if (Directory.Exists(testDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(testDirectory, "*.json")) File.Delete(file);
                Directory.Delete(testDirectory);
            }
        }

        var success = error.Length == 0 && results.Count == 17 && results.Values.All(value => value);
        SettingsStore.WriteAtomically(Path.Combine(SettingsStore.DataDirectory, "website-selftest.json"),
            new { Success = success, Checks = results, ErrorCode = error, CreatedUtc = DateTime.UtcNow });
        return success ? 0 : 1;

        JsonElement Request(Website site, string token, string url, string kind = "take", string? status = null)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { site = site.Key, token, url, kind, status });
            using var command = new MemoryStream();
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            command.Write(header);
            command.Write(payload);
            command.Position = 0;
            using var reply = new MemoryStream();
            if (WebsiteBrowserBridge.RunMessage(command, reply, () => new WebsiteSettingsStore(path), tickets) != 0)
                throw new InvalidOperationException("Offline messaging failed.");
            reply.Position = 0;
            reply.ReadExactly(header);
            if (BinaryPrimitives.ReadInt32LittleEndian(header) != reply.Length - 4)
                throw new InvalidOperationException("Invalid offline response length.");
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.Clone();
        }
    }

    private static bool Ok(JsonElement response) => response.GetProperty("ok").GetBoolean();
    private static IEnumerable<Control> Descendants(Control root) =>
        root.Controls.Cast<Control>().SelectMany(child => Descendants(child).Prepend(child));

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
