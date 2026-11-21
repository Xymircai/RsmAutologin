// SPDX-License-Identifier: MPL-2.0
const sites = (() => {
  const entries = {
    "https://mail.rccpa.com.cn": "mail",
    "https://www.rccpa.com.cn": "easygo",
    "https://download.rsmchina.com.cn": "download"
  };
  const easyGoCallback = "https://www.rccpa.com.cn/#/callbackfromkeycloak";
  const authorizationPath = "/realms/RSM/protocol/openid-connect/auth";
  const ssoOrigin = "https://sso.rccpa.com.cn";
  const ticketParameter = "__rsm_login";
  const isTicket = value => typeof value === "string" && /^[0-9a-f]{32}$/.test(value);
  const portalOrigin = "https://www.rccpa.com.cn";

  function entryFor(url) {
    if (url.username || url.password || url.pathname !== "/") return null;
    const [route] = url.hash.split("?");
    return route === "#/login" ? entries[url.origin] ?? null : null;
  }

  function loginFor(url) {
    if (url.origin !== ssoOrigin) {
      const site = url.hash === "#/login" ? entryFor(url) : null;
      return site === "easygo" ? null : site;
    }
    if (url.username || url.password || url.pathname !== authorizationPath || url.hash) return null;
    const params = url.searchParams;
    const exact = (name, value) => params.getAll(name).length === 1 && params.get(name) === value;
    return exact("client_id", "EasyGo") && exact("redirect_uri", easyGoCallback) &&
      exact("response_type", "code") && exact("scope", "openid") &&
      params.getAll("state").length === 1 && params.get("state")?.trim() ? "easygo" : null;
  }

  function canObserve(url, site) {
    return !url.username && !url.password && (entries[url.origin] === site ||
      (site === "easygo" && url.origin === ssoOrigin &&
        (url.pathname === authorizationPath || url.pathname.startsWith("/realms/RSM/login-actions/"))));
  }

  return { entryFor, loginFor, canObserve, isTicket, ssoOrigin, portalOrigin, ticketParameter };
})();
