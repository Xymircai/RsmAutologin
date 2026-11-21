// SPDX-License-Identifier: MPL-2.0
(() => {
  if (window.top !== window) return;
  const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
  const message = (kind, details = {}) =>
    chrome.runtime.sendMessage({ kind, url: location.href, ...details }).catch(() => null);
  let lastTicket;
  let generation = 0;

  async function observe(site, expiresAt, live, submitted) {
    while (live() && Date.now() < expiresAt()) {
      const status = websiteResult(site, submitted());
      if (status) {
        await message("result", { status });
        return;
      }
      if (!sites.canObserve(new URL(location.href), site)) return;
      await delay(200);
    }
    if (live()) await message("result", { status: "timeout" });
  }

  async function drive(state, live) {
    const site = state.site;
    let expires = state.expiresAt;
    let submitted = state.stage === "submitted";
    async function watch() { await observe(site, () => expires, live, () => submitted); }
    if (state.stage !== "opened") {
      await watch();
      return;
    }

    let fields;
    const readyBy = Math.min(expires, Date.now() + 18_000);
    while (live() && Date.now() < readyBy) {
      const outcome = websiteResult(site, false);
      if (outcome) {
        await message("result", { status: outcome });
        return;
      }
      fields = findLoginForm(site);
      if (fields) break;
      await delay(200);
    }
    if (!live()) return;
    if (!fields || fields.verification) {
      await message("result", { status: fields?.verification ? "verification" : "form-changed" });
      return;
    }

    const page = location.href;
    const sameForm = () => {
      const current = findLoginForm(site);
      return live() && location.href === page && current && current.form === fields.form &&
        current.user === fields.user && current.pass === fields.pass && current.captcha === fields.captcha ? current : null;
    };
    let credential = await message("take");
    if (!live()) return;
    if (!credential?.ok || !sameForm() ||
        !fillLoginFields(site, fields, credential.username, credential.password, () => !!sameForm(), false)) {
      await message("result", { status: "form-changed" });
      return;
    }
    const expectedUser = fields.user.value;
    let expectedPassword = fields.pass.value;
    const automatic = fields.captcha ? credential.submitAfterManualCaptcha : credential.autoSubmit;
    credential = null;
    expires = Date.now() + 120_000;
    const listeners = new AbortController();
    const { signal } = listeners;
    let committing = false;
    let manualClick = false;
    let captchaReady = fields.captcha?.value === "";
    let captchaVersion = 0;
    let trustedDigits = 0;

    function finishSubmit() {
      if (submitted) return;
      submitted = true;
      expectedPassword = "";
      expires = Date.now() + 30_000;
    }

    function handleManualSubmit(event) {
      if (!live() || !event.isTrusted || submitted || !sameForm()) return;
      const button = sameForm().submit;
      if (!(event.type === "submit" ? event.target === fields.form : button.contains(event.target))) return;
      manualClick = true;
      finishSubmit();
      void message("submit");
    }
    document.addEventListener("click", handleManualSubmit, { capture: true, signal });
    document.addEventListener("submit", handleManualSubmit, { capture: true, signal });

    const buttonReady = () => {
      const current = sameForm();
      return current && !current.verification && !current.submit.matches(":disabled, [aria-disabled=true]") &&
        current.user.value === expectedUser && current.pass.value === expectedPassword ? current.submit : null;
    };
    async function submit(allowed = () => true) {
      if (committing || submitted) return;
      committing = true;
      try {
        const until = Math.min(expires, Date.now() + 6_000);
        while (live() && !submitted && allowed() && sameForm() && Date.now() < until) {
          if (buttonReady()) break;
          await delay(150);
        }
        if (submitted || !live() || !allowed()) return;
        if (!buttonReady()) {
          await message("result", { status: sameForm()?.verification ? "verification" : "form-changed" });
          expires = 0;
          return;
        }
        const accepted = await message("submit");
        const button = buttonReady();
        if (accepted?.ok && button && allowed() && !manualClick && !submitted) {
          finishSubmit();
          button.click();
        } else if (!manualClick && !submitted) {
          await message("result", { status: "form-changed" });
          expires = 0;
        }
      } finally { committing = false; }
    }

    if (fields.captcha) {
      const captcha = fields.captcha;
      const typed = /^[0-9]{4}$/;
      if (automatic) {
        captcha.addEventListener("input", event => {
          const edited = ++captchaVersion;
          if (!captcha.value) {
            captchaReady = true;
            trustedDigits = 0;
            return;
          }
          if (!event.isTrusted || !captchaReady) {
            captchaReady = false;
            return;
          }
          if (event.inputType?.startsWith("delete")) trustedDigits = Math.min(trustedDigits, captcha.value.length);
          else if (/^insert(?:Text|CompositionText)$/.test(event.inputType ?? "") &&
              /^[0-9]$/.test(event.data ?? "")) trustedDigits++;
          else {
            captchaReady = false;
            return;
          }
          if (!event.isComposing && trustedDigits >= 4 && typed.test(captcha.value)) {
            const currentInput = () => sameForm()?.captcha === captcha && edited === captchaVersion &&
              document.visibilityState === "visible" && typed.test(captcha.value);
            void delay(320).then(() => {
              if (currentInput()) void submit(currentInput);
            });
          }
        }, { signal });
        document.addEventListener("pointerdown", event => {
          if (event.isTrusted && event.target !== captcha) captchaVersion++;
        }, { capture: true, signal });
      }
      if (!captcha.value && document.visibilityState === "visible") captcha.focus();
      await message("result", { status: "captcha" });
    } else if (automatic) {
      await delay(0);
      await submit();
    } else {
      await message("result", { status: "manual-submit" });
    }

    try {
      if (expires > 0) await watch();
    } finally {
      expectedPassword = "";
      listeners.abort();
    }
  }

  async function start() {
    const page = new URL(location.href);
    const site = sites.entryFor(page);
    const [, query] = page.hash.split("?");
    const params = new URLSearchParams(query);
    const token = params.get(sites.ticketParameter);
    if (site && [...params].length === 1 && sites.isTicket(token)) {
      if (lastTicket === token) return;
      lastTicket = token;
      const current = ++generation;
      const armed = message("arm", { site, token });
      history.replaceState(history.state, "", page.pathname + page.search + "#/login");
      if (!(await armed)?.armed || current !== generation) return;
      const state = await message("state");
      if (state) await drive(state, () => current === generation);
      return;
    }
    if (generation) return;
    const current = ++generation;
    for (let i = 0; i < (location.origin === sites.ssoOrigin ? 8 : 2); i++) {
      if (current !== generation) return;
      const state = await message("state");
      if (state && current === generation) {
        await drive(state, () => current === generation);
        return;
      }
      await delay(200);
    }
  }

  function run() { void start().catch(() => {}); }
  run();
  window.addEventListener("hashchange", run);
})();
