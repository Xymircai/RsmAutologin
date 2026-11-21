// SPDX-License-Identifier: MPL-2.0
function visibleLoginElement(element) {
  return !!element?.isConnected && element.getClientRects().length > 0 &&
    !element.closest("[aria-hidden=true], [inert]") && getComputedStyle(element).visibility === "visible";
}

function findLoginForm(site) {
  if (window !== window.top || sites.loginFor(new URL(location.href)) !== site ||
      document.visibilityState === "prerender") return null;

  const sso = site === "easygo";
  const download = site === "download";
  const unique = (parent, selector) => {
    const elements = [...parent.querySelectorAll(selector)].filter(visibleLoginElement);
    return elements.length === 1 ? elements[0] : null;
  };
  const form = unique(document, sso ? "form#kc-form-login" : download ? "form.login-form" : "form.loginForm");
  if (!form) return null;
  const user = unique(form, sso ? "input#username[name=username][type=text]" :
    download ? "input#normal_login_userName[type=text]" : "input[name=username][type=text]");
  const pass = unique(form, sso ? "input#password[name=password][type=password]" :
    download ? "input#normal_login_password[type=password]" : "input[type=password]");
  const area = site === "mail" ? form.closest(".loginArea.normalForm") : form;
  if (!area) return null;
  const submit = unique(area, sso ? "input#kc-login[type=submit]" :
    download ? "button[type=submit]" : "button.el-button--primary[type=button]");
  if (!user || !pass || !submit || user.matches(":disabled, [readonly]") || pass.matches(":disabled, [readonly]"))
    return null;

  if (sso) {
    const action = new URL(form.action);
    if (form.method !== "post" || action.origin !== location.origin ||
        action.pathname !== "/realms/RSM/login-actions/authenticate" ||
        submit.hasAttribute("formaction") || submit.hasAttribute("formmethod")) return null;
  } else if (submit.textContent.replace(/\s/g, "") !== "登录") {
    return null;
  }

  if (site === "mail" && user.parentElement.querySelector(".el-input-group__append")?.textContent
    .replace(/\s/g, "").toLowerCase() !== "@rccpa.com.cn") return null;

  const captcha = download ? unique(form, "input#normal_login_valid[type=text]") : null;
  if (download && !captcha) return null;
  const extra = [...area.querySelectorAll("input, select, textarea, [contenteditable=true]")].some(element =>
    element !== user && element !== pass && element !== captcha && visibleLoginElement(element) &&
    !element.matches("[readonly], input[type=checkbox], input[type=hidden], input[type=submit], input[type=button]"));
  return { form, user, pass, submit, captcha,
    verification: extra || !!area.querySelector("iframe, [autocomplete=one-time-code]") };
}

function fillLoginFields(site, fields, username, password, isCurrent, allowFocus = true) {
  if (site === "mail" && username.toLowerCase().endsWith("@rccpa.com.cn"))
    username = username.slice(0, -"@rccpa.com.cn".length);
  if (!username || !password || site === "mail" && username.includes("@") ||
      fields.user.value && fields.user.value !== username || fields.pass.value && fields.pass.value !== password)
    return false;

  const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
  function fill(input, value) {
    if (input.value === value) return;
    setValue.call(input, value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  }
  fill(fields.user, username);
  if (!isCurrent() || !fields.pass.isConnected) return false;
  fill(fields.pass, password);
  if (allowFocus && fields.captcha && !fields.captcha.value && document.visibilityState === "visible")
    fields.captcha.focus();
  return fields.user.value === username && fields.pass.value === password;
}

function websiteResult(site, submitted) {
  const url = new URL(location.href);
  if (!sites.canObserve(url, site)) return null;
  const route = url.hash.split("?")[0];
  const visibleText = selector => [...document.querySelectorAll(selector)].filter(visibleLoginElement)
    .map(element => element.textContent.trim());
  const actions = "a, button, [role=button], [role=menuitem], .ant-dropdown-menu-item";

  if (site === "mail" && /^#\/(?:mailbox(?:\/|$)|welcome$)/.test(route)) {
    const inbox = visibleText("a, [role=treeitem], .el-tree-node__label").some(value => /^(收件箱|Inbox)$/i.test(value));
    const controls = visibleText(actions + ", .el-menu-item");
    if (inbox && controls.some(value => /^(写信|写邮件|撰写邮件|Compose|退出登录)$/i.test(value))) return "success";
  }
  if (site === "easygo" && url.origin === sites.portalOrigin && !/^#\/(?:login|callbackfromkeycloak)(?:\/|$)/.test(route) &&
      [...document.querySelectorAll(actions)].some(element => element.isConnected && element.textContent.trim() === "退出登录"))
    return "success";

  if (site === "download" && route === "#/home" &&
      visibleText(".commonHeader .actionGroup-text").includes("注销") &&
      visibleLoginElement(document.querySelector(".commonContent")))
    return "success";

  if (site === "mail" && /^#\/twofactor_login(?:\/|$)/.test(route) ||
      document.querySelector("[autocomplete=one-time-code], #otp, #kc-otp-login-form"))
    return "verification";

  if (submitted) {
    const errors = visibleText("[role=alert], #input-error, #kc-feedback-text, .pf-c-alert__title, " +
      ".el-message--error, .ant-message-error, .ant-notification-notice-error, .ant-form-explain");
    if (errors.some(value => /登录失败|认证失败|用户名或密码(?:不正确|错误)|(?:账号|用户名|密码|验证码)(?:不正确|错误|已失效)|invalid username or password|invalid credentials/i.test(value)))
      return "failed";
  }
  return null;
}
