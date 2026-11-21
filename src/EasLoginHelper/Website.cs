// SPDX-License-Identifier: MPL-2.0
namespace EasLoginHelper;

internal sealed record Website(string Key, string Name, string EntryUrl, bool RequiresManualCaptcha = false)
{
    internal static readonly Website Mail = new("mail", "容诚邮箱", "https://mail.rccpa.com.cn/#/login");
    internal static readonly Website EasyGo = new("easygo", "EasyGo 单点登录", "https://www.rccpa.com.cn/#/login");
    internal static readonly Website Download = new("download", "资源下载平台", "https://download.rsmchina.com.cn/#/login", true);
    internal static readonly IReadOnlyList<Website> All = [Mail, EasyGo, Download];

    private const string EasyGoCallback = "https://www.rccpa.com.cn/#/callbackfromkeycloak";
    private const string SsoAuthorizationPath = "/realms/RSM/protocol/openid-connect/auth";

    internal CredentialVault Vault => new("EasLoginHelper/Website/" + Key + "/");

    internal bool IsLoginPage(Uri url)
    {
        if (!IsHttps(url)) return false;
        if (this != EasyGo)
            return url.Host == new Uri(EntryUrl).Host && url.AbsolutePath == "/" && url.Fragment == "#/login";

        if (url.Host != "sso.rccpa.com.cn" || url.AbsolutePath != SsoAuthorizationPath || url.Fragment.Length != 0)
            return false;
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["client_id"] == "EasyGo" && query["redirect_uri"] == EasyGoCallback &&
            query["response_type"] == "code" && query["scope"] == "openid" &&
            query.GetValues("state") is [var state] && !string.IsNullOrWhiteSpace(state);
    }

    internal bool CanObserve(Uri url) => IsHttps(url) && (url.Host == new Uri(EntryUrl).Host ||
        (this == EasyGo && url.Host == "sso.rccpa.com.cn" &&
            (url.AbsolutePath == SsoAuthorizationPath || url.AbsolutePath.StartsWith("/realms/RSM/login-actions/", StringComparison.Ordinal))));

    private static bool IsHttps(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
        url.IsDefaultPort && url.UserInfo.Length == 0;
}
