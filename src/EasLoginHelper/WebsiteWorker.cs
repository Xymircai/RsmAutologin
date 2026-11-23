// SPDX-License-Identifier: MPL-2.0
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace EasLoginHelper;

internal sealed record WebsiteRequest(string Kind, string? Site = null, Guid Id = default,
    string? Label = null, string? UserName = null, string? Password = null, bool Enabled = false);
internal sealed record WebsiteReply(bool Ok, string? Error = null, WebsiteAccountSettings? Settings = null,
    WebsiteNotice[]? Notices = null);

internal static class WebsiteWorker
{
    internal const string Argument = "--website-worker";
    internal const string PipePrefix = "EasLoginHelper.Website.";

    internal static async Task<int> RunAsync(string token, string processId, string ownerId)
    {
        if (Diagnostics.IsElevated() || !Guid.TryParseExact(token, "N", out _) || !uint.TryParse(processId, out var parentId))
            return 1;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            var owner = new SecurityIdentifier(ownerId);
            if (current.User is not { } user || !WebsiteProcessIdentity.InCurrentSession(parentId))
                return 1;

            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            if (owner != user)
                security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                    AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(PipePrefix + token, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
            using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await pipe.WaitForConnectionAsync(startup.Token).ConfigureAwait(false);
            }
            if (!WebsiteProcessIdentity.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actualClient) ||
                parentId != actualClient)
                return 1;

            var active = new Dictionary<string, (Website Site, WebsiteNotice Last)>();
            while (true)
            {
                var command = await WebsitePipe.ReadAsync<WebsiteRequest>(pipe, CancellationToken.None).ConfigureAwait(false);
                var reply = Dispatch(command, active);
                await WebsitePipe.WriteAsync(pipe, reply, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) { return 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
            OperationCanceledException or JsonException or System.Security.SecurityException or Win32Exception)
        { return 1; }
    }

    private static WebsiteReply Dispatch(WebsiteRequest request,
        Dictionary<string, (Website Site, WebsiteNotice Last)> active)
    {
        try
        {
            var tickets = WebsiteBrowserBridge.Tickets;
            if (request.Kind == "ping") return new WebsiteReply(true);
            if (request.Kind == "poll")
            {
                var changes = new List<WebsiteNotice>();
                foreach (var (token, entry) in active.ToArray())
                {
                    var next = tickets.Read(entry.Site, token) ??
                        new WebsiteNotice(entry.Site.Key, WebsiteStage.Unconfirmed, AttemptId: token);
                    if (next != entry.Last) changes.Add(next);
                    if (WebsiteLaunchTickets.IsTerminal(next.Stage))
                    {
                        tickets.Remove(entry.Site, token);
                        active.Remove(token);
                    }
                    else active[token] = (entry.Site, next);
                }
                return new WebsiteReply(true, Notices: changes.ToArray());
            }

            if (request.Kind == "connect")
            {
                // 保留命令供自检与旧版界面兼容；正常流程已由「打开网站」自动登记。
                if (!WebsiteBrowserBridge.TryEnsureRegistered()) return new WebsiteReply(false, "无法登记浏览器连接。");
                return new WebsiteReply(true);
            }

            var site = Website.All.FirstOrDefault(item => item.Key == request.Site)
                ?? throw new ArgumentException("未知网站。");
            var settings = new WebsiteSettingsStore();
            switch (request.Kind)
            {
                case "get": break;
                case "save":
                    settings.Accounts(site).Save(request.Id, request.Label ?? "", request.UserName ?? "", request.Password ?? "");
                    break;
                case "delete":
                    settings.Accounts(site).Delete(request.Id);
                    break;
                case "default":
                    settings.Update(site, options => options.DefaultAccountId = request.Id);
                    break;
                case "auto":
                    settings.Update(site, options =>
                    {
                        if (site.RequiresManualCaptcha) options.SubmitAfterManualCaptcha = request.Enabled;
                        else options.AutoLoginEnabled = request.Enabled;
                    });
                    break;
                case "open":
                    // 每次打开网站都把 Edge / Chrome 的登记刷新为当前 EXE：
                    // 同事把 EXE 拖到新位置后，浏览器仍能找到这一份，无需手动「连接」。
                    WebsiteBrowserBridge.TryEnsureRegistered();
                    var options = settings.For(site);
                    var token = options.DefaultAccount is { } account && WebsiteBrowserBridge.IsRegisteredForDefaultBrowser()
                        ? tickets.Issue(site, account.Id) : null;
                    try
                    {
                        var url = token is null ? site.EntryUrl : site.EntryUrl + "?__rsm_login=" + token;
                        using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch
                    {
                        if (token is not null) tickets.Remove(site, token);
                        throw;
                    }
                    if (token is null) return new WebsiteReply(true, Notices: [new(site.Key, WebsiteStage.NeedsAction,
                        options.DefaultAccount is null ? WebsiteReason.MissingAccount : WebsiteReason.BrowserConnection)]);
                    var notice = new WebsiteNotice(site.Key, WebsiteStage.Opened, AttemptId: token);
                    active[token] = (site, notice);
                    return new WebsiteReply(true, Notices: [notice]);
                default:
                    throw new ArgumentException("未知的网站操作。");
            }
            return new WebsiteReply(true, Settings: settings.For(site));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or
            InvalidOperationException or InvalidDataException or JsonException or System.Security.SecurityException)
        {
            return new WebsiteReply(false, request.Kind == "open" ? "无法打开网站。请检查默认浏览器和配套扩展连接。" :
                e is ArgumentException ? e.Message : "无法访问当前桌面用户的网站配置或凭据。");
        }
    }
}

internal static class WebsitePipe
{
    private const int MaxMessageBytes = 512 * 1024;

    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            if (data.Length > MaxMessageBytes) throw new IOException("网站消息太大。");
            var size = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
            await stream.WriteAsync(size, token).ConfigureAwait(false);
            await stream.WriteAsync(data, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var size = new byte[4];
        await stream.ReadExactlyAsync(size, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > MaxMessageBytes) throw new IOException("网站消息长度无效。");
        var data = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(data) ?? throw new IOException("网站消息为空。");
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
}
