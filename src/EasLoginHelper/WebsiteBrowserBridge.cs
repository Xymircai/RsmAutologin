// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32;

namespace EasLoginHelper;

internal static class WebsiteBrowserBridge
{
    internal const string HostName = "cn.rsmchina.eas_login_helper";
    internal const string ExtensionOrigin = "chrome-extension://kcnpldadhmokpklbclhkkihppblgiejp/";
    private const string Chrome = @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;
    private const string Edge = @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName;
    private const string HttpsAssociation = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";
    private static string ManifestPath => Path.Combine(SettingsStore.DataDirectory, "browser-native-host.json");
    internal static WebsiteLaunchTickets Tickets => new(Path.Combine(SettingsStore.DataDirectory, "website-launch"));

    internal static void Register()
    {
        var binary = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定助手的 EXE 路径。");
        SettingsStore.WriteAtomically(ManifestPath, new
        {
            name = HostName, description = "EasLoginHelper website autofill", path = binary,
            type = "stdio", allowed_origins = new[] { ExtensionOrigin }
        });
        foreach (var key in new[] { Edge, Chrome })
        {
            using var entry = Registry.CurrentUser.CreateSubKey(key);
            entry.SetValue("", ManifestPath, RegistryValueKind.String);
        }
    }

    /// <summary>确保 Edge / Chrome 的原生消息登记指向当前 EXE；登记失败时不阻断「打开网站」。</summary>
    internal static bool TryEnsureRegistered()
    {
        try { Register(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return false; }
    }

    internal static bool IsRegisteredForDefaultBrowser()
    {
        using var association = Registry.CurrentUser.OpenSubKey(HttpsAssociation);
        var key = (association?.GetValue("ProgId") as string) switch
        {
            "ChromeHTML" => Chrome,
            "MSEdgeHTM" => Edge,
            _ => null
        };
        if (key is null) return false;
        using var host = Registry.CurrentUser.OpenSubKey(key);
        if (host?.GetValue("") is not string path || !string.Equals(path, ManifestPath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(ManifestPath)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            return manifest.RootElement.TryGetProperty("path", out var executable) &&
                executable.ValueKind == JsonValueKind.String &&
                string.Equals(executable.GetString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    internal static int Run(string caller)
    {
        if ((caller != ExtensionOrigin && caller + "/" != ExtensionOrigin) || Diagnostics.IsElevated()) return 1;
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return RunMessage(input, output, () => new WebsiteSettingsStore(), Tickets);
    }

    internal static int RunMessage(Stream input, Stream output, Func<WebsiteSettingsStore> settings, WebsiteLaunchTickets tickets)
    {
        object response = new { ok = false };
        try
        {
            using var request = ReadRequest(input);
            var root = request.RootElement;
            var siteKey = root.GetProperty("site").GetString();
            var token = root.GetProperty("token").GetString();
            var kind = root.TryGetProperty("kind", out var operation) ? operation.GetString() : "take";
            var site = Website.All.FirstOrDefault(item => item.Key == siteKey);
            if (site is not null && Uri.TryCreate(root.GetProperty("url").GetString(), UriKind.Absolute, out var uri))
            {
                if (kind == "submit" && site.IsLoginPage(uri))
                    response = new { ok = tickets.Submit(site, token) };
                else if (kind == "result" && site.CanObserve(uri) && ReadNotice(root) is { } notice)
                    response = new { ok = tickets.Report(site, token, notice.Stage, notice.Reason) };
                else if (kind == "take" && site.IsLoginPage(uri) && tickets.Take(site, token) is { } accountId)
                {
                    var options = settings().For(site);
                    if (options.DefaultAccount is { } account && account.Id == accountId)
                    {
                        using var secret = site.Vault.Read(account.Id);
                        if (secret is not null)
                            response = new { ok = true, username = account.UserName, password = new string(secret.Characters),
                                autoSubmit = !site.RequiresManualCaptcha && options.AutoLoginEnabled,
                                submitAfterManualCaptcha = site.RequiresManualCaptcha && options.SubmitAfterManualCaptcha };
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        { }

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
            try
            {
                Span<byte> size = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
                output.Write(size);
                output.Write(bytes);
                output.Flush();
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            return 0;
        }
        catch (IOException) { return 1; }
    }

    private static (WebsiteStage Stage, WebsiteReason Reason)? ReadNotice(JsonElement root) =>
        root.GetProperty("status").GetString() switch
        {
            "captcha" => (WebsiteStage.Filled, WebsiteReason.ManualCaptcha),
            "manual-submit" => (WebsiteStage.Filled, WebsiteReason.ManualSubmit),
            "success" => (WebsiteStage.Success, WebsiteReason.None),
            "failed" => (WebsiteStage.Failed, WebsiteReason.Rejected),
            "verification" => (WebsiteStage.NeedsAction, WebsiteReason.ExtraVerification),
            "form-changed" => (WebsiteStage.NeedsAction, WebsiteReason.FormChanged),
            "closed" => (WebsiteStage.Unconfirmed, WebsiteReason.TabClosed),
            "timeout" => (WebsiteStage.Unconfirmed, WebsiteReason.None),
            _ => null
        };

    private static JsonDocument ReadRequest(Stream input)
    {
        Span<byte> length = stackalloc byte[4];
        input.ReadExactly(length);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is <= 0 or > 8192) throw new InvalidDataException("无效的浏览器消息。");
        var payload = new byte[size];
        input.ReadExactly(payload);
        return JsonDocument.Parse(payload);
    }
}
