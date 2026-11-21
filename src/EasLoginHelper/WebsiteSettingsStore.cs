// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace EasLoginHelper;

internal sealed class WebsiteAccountSettings : AccountSettings
{
    public bool SubmitAfterManualCaptcha { get; set; } = true;
}

internal sealed class WebsiteSettingsStore
{
    private readonly string _path;
    private Dictionary<string, WebsiteAccountSettings> _accounts;

    internal WebsiteSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(SettingsStore.DataDirectory, "website-settings.json");
        if (File.Exists(_path))
        {
            _accounts = JsonSerializer.Deserialize<Dictionary<string, WebsiteAccountSettings>>(
                File.ReadAllText(_path), SettingsStore.JsonOptions)
                ?? throw new InvalidDataException("网站账号配置为空。");
            Validate(_accounts);
        }
        else
        {
            _accounts = Website.All.ToDictionary(site => site.Key,
                site => new WebsiteAccountSettings { AutoLoginEnabled = !site.RequiresManualCaptcha });
        }
    }

    internal WebsiteAccountSettings For(Website site) => _accounts[site.Key];

    internal AccountService Accounts(Website site) =>
        new(site.Vault, () => For(site), change => Update(site, change));

    internal void Update(Website site, Action<WebsiteAccountSettings> change)
    {
        var settings = JsonSerializer.Deserialize<WebsiteAccountSettings>(JsonSerializer.Serialize(For(site)))!;
        change(settings);
        var next = new Dictionary<string, WebsiteAccountSettings>(_accounts) { [site.Key] = settings };
        Validate(next);
        SettingsStore.WriteAtomically(_path, next);
        _accounts = next;
    }

    private static void Validate(Dictionary<string, WebsiteAccountSettings> accounts)
    {
        if (accounts.Count != Website.All.Count ||
            Website.All.Any(site => !accounts.TryGetValue(site.Key, out var value) || value is null))
            throw new InvalidDataException("网站账号配置缺少必要的站点。");
        foreach (var settings in accounts.Values)
            SettingsStore.ValidateAccounts(settings);
    }
}
