// SPDX-License-Identifier: MPL-2.0
importScripts("sites.js");

const nativeHost = "cn.rsmchina.eas_login_helper";
const keyFor = tabId => "pending:" + tabId;
const session = chrome.storage.session;
const pendingByTab = new Map();

async function toHost(attempt, kind, url, status) {
  const reply = await chrome.runtime.sendNativeMessage(nativeHost,
    { kind, site: attempt.site, token: attempt.token, url, ...(status ? { status } : {}) });
  return reply?.ok ? reply : null;
}

function enqueue(tabId, work) {
  const pending = (pendingByTab.get(tabId) ?? Promise.resolve()).catch(() => {}).then(work);
  pendingByTab.set(tabId, pending);
  pending.finally(() => {
    if (pendingByTab.get(tabId) === pending) pendingByTab.delete(tabId);
  }).catch(() => {});
  return pending;
}

async function handle(message, sender) {
  const source = new URL(sender.url);
  const page = new URL(message.url);
  if (source.origin !== page.origin || source.pathname !== page.pathname) return null;
  const key = keyFor(sender.tab.id);

  if (message.kind === "arm" && sites.entryFor(page) === message.site && sites.isTicket(message.token)) {
    const existing = (await session.get(key))[key];
    if (existing?.token === message.token) return { armed: true };
    if (existing && existing.expiresAt > Date.now())
      await toHost(existing, "result", existing.url, "timeout").catch(() => {});
    await session.set({ [key]: { site: message.site, token: message.token, url: page.href,
      stage: "opened", expiresAt: Date.now() + 120_000 } });
    return { armed: true };
  }

  const attempt = (await session.get(key))[key];
  if (!attempt || !sites.canObserve(page, attempt.site)) return null;
  if (attempt.expiresAt <= Date.now()) {
    await session.remove(key);
    if (message.kind === "result" && message.status === "timeout")
      return toHost(attempt, "result", page.href, "timeout");
    return null;
  }
  if (message.kind === "state") return { site: attempt.site, stage: attempt.stage, expiresAt: attempt.expiresAt };

  if (message.kind === "take" && attempt.stage === "opened" && sites.loginFor(page) === attempt.site) {
    const reply = await toHost(attempt, "take", page.href);
    if (!reply) return null;
    await session.set({ [key]: { ...attempt, url: page.href, stage: "filled", expiresAt: Date.now() + 120_000 } });
    return reply;
  }

  if (message.kind === "submit" && attempt.stage === "filled" && sites.loginFor(page) === attempt.site) {
    if (!await toHost(attempt, "submit", page.href)) return null;
    await session.set({ [key]: { ...attempt, url: page.href, stage: "submitted", expiresAt: Date.now() + 30_000 } });
    return { ok: true };
  }

  if (message.kind === "result") {
    const waiting = message.status === "captcha" || message.status === "manual-submit";
    if (waiting && attempt.stage !== "filled") return null;
    const accepted = await toHost(attempt, "result", page.href, message.status);
    if (accepted && !waiting) await session.remove(key);
    return accepted;
  }
  return null;
}

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (sender.id !== chrome.runtime.id || sender.frameId !== 0 || !Number.isInteger(sender.tab?.id) ||
      !["arm", "state", "take", "submit", "result"].includes(message?.kind)) return false;
  enqueue(sender.tab.id, () => handle(message, sender)).then(reply, () => reply(null));
  return true;
});

chrome.tabs.onRemoved.addListener(tabId => {
  enqueue(tabId, async () => {
    const key = keyFor(tabId);
    const attempt = (await session.get(key))[key];
    await session.remove(key);
    if (attempt && attempt.expiresAt > Date.now()) await toHost(attempt, "result", attempt.url, "closed");
  }).catch(() => {});
});
