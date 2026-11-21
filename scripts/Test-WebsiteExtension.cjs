// SPDX-License-Identifier: MPL-2.0
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");

const source = name => fs.readFileSync(path.join(__dirname, "../src/BrowserExtension", name), "utf8");
const scripts = ["sites.js", "login-form.js", "content.js"].map(source).join("\n");
const token = "?__rsm_login=" + "a".repeat(32);
const mail = "https://mail.rccpa.com.cn/#/login";
const download = "https://download.rsmchina.com.cn/#/login";
const portal = "https://www.rccpa.com.cn/#/login";
const sso = "https://sso.rccpa.com.cn/realms/RSM/protocol/openid-connect/auth?client_id=EasyGo" +
  "&redirect_uri=https%3A%2F%2Fwww.rccpa.com.cn%2F%23%2Fcallbackfromkeycloak&response_type=code&scope=openid&state=offline";

class EventTarget {
  constructor() { this.listeners = new Map(); }
  addEventListener(type, handler, options) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(handler);
    this.listeners.set(type, listeners);
    options?.signal?.addEventListener("abort", () => this.listeners.set(type,
      (this.listeners.get(type) ?? []).filter(callback => callback !== handler)));
  }
  dispatchEvent(event) {
    event.target ??= this;
    for (const callback of this.listeners.get(event.type) ?? []) callback(event);
  }
}

class Field extends EventTarget {
  constructor(text = "") {
    super();
    this.isConnected = true;
    this.visible = true;
    this.textContent = text;
    this.currentValue = "";
    this.disabled = false;
    this.clicks = 0;
  }
  get value() { return this.currentValue; }
  set value(value) { this.currentValue = value; }
  getClientRects() { return this.visible ? [1] : []; }
  closest() { return null; }
  matches(selector) { return this.disabled && selector.includes(":disabled"); }
  hasAttribute() { return false; }
  contains(node) { return this === node; }
  click() { this.clicks++; }
  focus() { this.focused = true; }
}

function fixture(auto = true) {
  let receiver, tabClosed;
  let now = Date.now();
  let nextTimer = 0;
  let timers = [];
  const calls = [];
  const values = new Map();
  const hostState = new Map();
  const chrome = {
    runtime: {
      id: "offline-extension",
      onMessage: { addListener: handler => { receiver = handler; } },
      async sendNativeMessage(_, request) {
        calls.push(request.kind === "result" ? request.status : request.kind);
        const key = request.site + ":" + request.token;
        const state = hostState.get(key);
        if (request.kind === "take" && !state) {
          hostState.set(key, "filled");
          return { ok: true, username: request.site === "mail" ? "name@Rccpa.com.cn" : "name",
            password: "offline-test", autoSubmit: auto, submitAfterManualCaptcha: auto };
        }
        if (request.kind === "submit" && state === "filled") {
          hostState.set(key, "submitted");
          return { ok: true };
        }
        if (request.kind === "result" && !["success", "failed", "closed", "timeout"].includes(state)) {
          if (["success", "failed", "closed", "timeout"].includes(request.status)) hostState.set(key, request.status);
          return { ok: true };
        }
        return { ok: false };
      }
    },
    storage: { session: {
      get: async key => ({ [key]: values.get(key) }),
      set: async data => Object.entries(data).forEach(([key, value]) => values.set(key, value)),
      remove: async key => values.delete(key)
    } },
    tabs: { onRemoved: { addListener: handler => { tabClosed = handler; } } }
  };
  const clock = { now: () => now };
  const background = vm.createContext({ chrome, URL, Date: clock });
  background.importScripts = file => vm.runInContext(source(file), background);
  vm.runInContext(source("background.js"), background);
  const flush = () => new Promise(resolve => setImmediate(resolve));
  async function advance(ms = 0) {
    const limit = now + ms;
    for (;;) {
      await flush();
      timers.sort((a, b) => a.when - b.when || a.id - b.id);
      if (!timers.length || timers[0].when > limit) break;
      const timer = timers.shift();
      now = timer.when;
      timer.callback();
    }
    now = limit;
  }

  function page(url, site, options = {}, tabId = 1) {
    const location = new URL(url);
    const doc = new EventTarget();
    const window = new EventTarget();
    window.top = window;
    doc.visibilityState = "visible";

    const user = new Field(), pass = new Field(), captcha = new Field();
    user.parentElement = { querySelector: () => new Field("@ rccpa.com.cn") };
    const submit = new Field("登 录");
    submit.disabled = !!options.disabled;
    const form = new Field();
    form.method = "post";
    form.action = "https://sso.rccpa.com.cn/realms/RSM/login-actions/authenticate";
    const extra = new Field();
    const inputs = [user, pass, ...(site === "download" ? [captcha] : []), ...(options.extra ? [extra] : [])];
    const queryFields = selector => {
      if (selector.startsWith("input, ")) return inputs;
      if (selector.includes("type=password")) return [pass];
      if (selector.includes("normal_login_valid")) return [captcha];
      if (selector.includes("type=text")) return [user];
      return selector.includes("submit") || selector.includes("button") ? [submit] : [];
    };
    form.closest = selector => selector === ".loginArea.normalForm" ? { querySelectorAll: queryFields, querySelector: () => null } : null;
    form.querySelectorAll = queryFields;
    form.querySelector = () => null;
    const resultMarkers = {
      ".commonHeader .actionGroup-text": [new Field("注销")],
      "a, [role=treeitem], .el-tree-node__label": [new Field("收件箱")],
      "a, button, [role=button], [role=menuitem], .ant-dropdown-menu-item, .el-menu-item": [new Field("写信")],
      "a, button, [role=button], [role=menuitem], .ant-dropdown-menu-item": [new Field("退出登录")]
    };

    doc.querySelector = selector => options.verification && selector.includes("one-time-code") ||
      options.success && selector === ".commonContent" ? new Field() : null;
    doc.querySelectorAll = selector => {
      if (selector.startsWith("form")) return options.form === false ? [] : [form];
      if (selector.startsWith("[role=alert]")) return options.error ? [new Field(options.error)] : [];
      return options.success ? resultMarkers[selector] ?? [] : [];
    };
    const local = { runtime: { sendMessage: message => new Promise(resolve => {
      if (!receiver(message, { id: chrome.runtime.id, frameId: 0, tab: { id: tabId }, url: location.href }, resolve))
        resolve(null);
    }) } };
    const context = { window, document: doc, location, URL, URLSearchParams, chrome: local,
      history: { state: null, replaceState: (_, __, href) => { location.href = new URL(href, location.href).href; } },
      Date: clock, AbortController, HTMLInputElement: Field,
      getComputedStyle: () => ({ visibility: "visible" }),
      Event: class { constructor(type) { this.type = type; } },
      setTimeout: (callback, delay) => timers.push({ callback, when: now + delay, id: ++nextTimer })
    };
    vm.runInNewContext(scripts, context);
    return {
      user, pass, captcha, submit, form, location, options, doc,
      type(digits, isTrusted = true) {
        for (const digit of digits) {
          captcha.value += digit;
          captcha.dispatchEvent({ type: "input", isTrusted, inputType: "insertText", data: digit });
        }
      },
      navigate(hash) { location.hash = hash; window.dispatchEvent({ type: "hashchange" }); }
    };
  }

  return { page, advance, calls, close: tabId => tabClosed(tabId) };
}

test("邮箱等待按钮启用，并且成功结果只收一次", async () => {
  const f = fixture();
  const p = f.page(mail + token, "mail", { disabled: true });
  await f.advance(600);
  assert.equal(p.user.value, "name");
  assert.equal(p.submit.clicks, 0);
  p.submit.disabled = false;
  await f.advance(600);
  assert.equal(p.submit.clicks, 1);
  p.options.success = true;
  p.navigate("#/mailbox/welcome");
  await f.advance(1000);
  assert.deepEqual(f.calls, ["take", "submit", "success"]);
});

test("下载平台保留前导零，只在人工输入完整后提交", async () => {
  const f = fixture();
  const p = f.page(download + token, "download");
  await f.advance(300);
  assert.equal(p.captcha.value, "");
  p.type("012");
  await f.advance(500);
  assert.equal(p.submit.clicks, 0);
  p.type("3");
  await f.advance(650);
  assert.equal(p.captcha.value, "0123");
  assert.equal(p.submit.clicks, 1);
  p.options.success = true;
  p.navigate("#/home");
  await f.advance(700);
  assert.deepEqual(f.calls, ["take", "captcha", "submit", "success"]);
});

test("下载平台忽略脚本填入的数字", async () => {
  const f = fixture();
  const p = f.page(download + token, "download");
  await f.advance(300);
  p.type("0123", false);
  await f.advance(1000);
  assert.equal(p.submit.clicks, 0);
  assert.deepEqual(f.calls, ["take", "captcha"]);
});

test("用户先点击登录时，取消同一轮的自动点击", async () => {
  const f = fixture();
  const p = f.page(download + token, "download");
  await f.advance(300);
  p.type("0123");
  p.doc.dispatchEvent({ type: "click", target: p.submit, isTrusted: true });
  p.submit.click();
  await f.advance(1000);
  assert.equal(p.submit.clicks, 1);
  assert.deepEqual(f.calls, ["take", "captcha", "submit"]);
});

test("浏览器直接打开的网页无需尝试，关闭自动提交时只填写", async () => {
  const f = fixture(false);
  const direct = f.page(mail, "mail", {}, 2);
  const p = f.page(mail + token, "mail");
  await f.advance(1000);
  assert.equal(direct.pass.value, "");
  assert.equal(p.pass.value, "offline-test");
  assert.equal(p.submit.clicks, 0);
  assert.deepEqual(f.calls, ["take", "manual-submit"]);
});

test("登录失败、超时和关闭标签页分别返回明确状态", async () => {
  const fail = fixture();
  const p = fail.page(mail + token, "mail");
  await fail.advance(600);
  p.options.error = "用户名或密码错误";
  await fail.advance(700);
  assert.deepEqual(fail.calls, ["take", "submit", "failed"]);

  const slow = fixture();
  slow.page(mail + token, "mail");
  await slow.advance(31000);
  assert.deepEqual(slow.calls, ["take", "submit", "timeout"]);

  const close = fixture();
  close.page(download + token, "download");
  await close.advance(600);
  close.close(1);
  await close.advance(100);
  assert.deepEqual(close.calls, ["take", "captcha", "closed"]);
});

test("EasyGo 在同一标签页经过单点登录与门户回调", async () => {
  const f = fixture();
  f.page(portal + token, "easygo", { form: false });
  await f.advance(250);
  const p = f.page(sso, "easygo");
  await f.advance(650);
  assert.equal(p.submit.clicks, 1);
  f.page("https://www.rccpa.com.cn/#/home", "easygo", { success: true, form: false });
  await f.advance(700);
  assert.deepEqual(f.calls, ["take", "submit", "success"]);
});
