// SPDX-License-Identifier: MPL-2.0
using System.Security.Principal;
using System.Text.Json;

namespace EasLoginHelper;

internal enum WebsiteStage { Opened, Filled, Submitted, Success, Failed, Unconfirmed, NeedsAction }
internal enum WebsiteReason { None, ManualCaptcha, ManualSubmit, FormChanged, ExtraVerification, TabClosed, Rejected,
    MissingAccount, BrowserConnection }
internal sealed record WebsiteNotice(string Site, WebsiteStage Stage, WebsiteReason Reason = WebsiteReason.None,
    string? AttemptId = null);

internal sealed class WebsiteLaunchTickets(string directory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly TimeSpan InputTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResultTime = TimeSpan.FromSeconds(30);
    private sealed record Attempt(string Site, Guid AccountId, DateTimeOffset Until, WebsiteStage Stage,
        WebsiteReason Reason = WebsiteReason.None);

    internal static bool IsTerminal(WebsiteStage stage) =>
        stage is WebsiteStage.Success or WebsiteStage.Failed or WebsiteStage.Unconfirmed or WebsiteStage.NeedsAction;

    internal string Issue(Website site, Guid accountId)
    {
        var token = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (Path.GetFileNameWithoutExtension(old) is { Length: 32 } &&
                File.GetLastWriteTimeUtc(old) < _clock.GetUtcNow().UtcDateTime.AddMinutes(-10))
            {
                try { File.Delete(old); }
                catch (IOException) { }
            }
        }
        SettingsStore.WriteAtomically(AttemptPath(token), new Attempt(site.Key, accountId,
            _clock.GetUtcNow() + InputTime, WebsiteStage.Opened));
        return token;
    }

    internal Guid? Take(Website site, string? token) => Edit(site, token, attempt =>
    {
        if (attempt.Stage != WebsiteStage.Opened) return null;
        return attempt with { Stage = WebsiteStage.Filled, Until = _clock.GetUtcNow() + InputTime };
    })?.AccountId;

    internal bool Submit(Website site, string? token) => Edit(site, token, attempt =>
        attempt.Stage == WebsiteStage.Filled
            ? attempt with { Stage = WebsiteStage.Submitted, Until = _clock.GetUtcNow() + ResultTime }
            : null) is not null;

    internal bool Report(Website site, string? token, WebsiteStage stage, WebsiteReason reason) => Edit(site, token, attempt =>
    {
        if (stage == WebsiteStage.Filled && attempt.Stage == WebsiteStage.Filled)
            return attempt with { Reason = reason };
        if (!IsTerminal(stage) || stage == WebsiteStage.Failed && attempt.Stage != WebsiteStage.Submitted)
            return null;
        return attempt with { Stage = stage, Reason = reason };
    }) is not null;

    internal WebsiteNotice? Read(Website site, string token) => Access(site, token, (path, attempt) =>
    {
        if (attempt is null) return null;
        return new WebsiteNotice(site.Key, attempt.Stage, attempt.Reason, token);
    });

    internal void Remove(Website site, string token) => Access<object>(site, token, (path, _) =>
    {
        File.Delete(path);
        return null;
    });

    private string AttemptPath(string token) => Path.Combine(directory, token + ".json");

    private Attempt? Edit(Website site, string? token, Func<Attempt, Attempt?> change) =>
        Access(site, token, (path, old) =>
        {
            if (old is null || IsTerminal(old.Stage) || change(old) is not { } updated) return null;
            SettingsStore.WriteAtomically(path, updated);
            return updated;
        });

    private T? Access<T>(Website site, string? token, Func<string, Attempt?, T?> action) where T : class
    {
        if (token is not { Length: 32 } || !token.All(char.IsAsciiHexDigitLower)) return null;
        using var identity = WindowsIdentity.GetCurrent();
        using var gate = new Mutex(false, "Local\\EasLoginHelper_WebsiteTicket_" + site.Key + "_" + identity.User?.Value);
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException("网站登录请求正在使用。");
        }
        catch (AbandonedMutexException) { }

        try
        {
            var path = AttemptPath(token);
            if (!File.Exists(path)) return null;
            var attempt = JsonSerializer.Deserialize<Attempt>(File.ReadAllText(path));
            if (attempt?.Site != site.Key) return null;
            if (!IsTerminal(attempt.Stage) && attempt.Until <= _clock.GetUtcNow())
            {
                attempt = attempt with { Stage = WebsiteStage.Unconfirmed, Reason = WebsiteReason.None };
                SettingsStore.WriteAtomically(path, attempt);
            }
            return action(path, attempt);
        }
        finally { gate.ReleaseMutex(); }
    }
}
