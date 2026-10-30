(() => {
  const API = window.WCLQ_CONFIG.apiBase.replace(/\/$/, "");
  const TOKEN_KEY = "wclq_token";
  const REPORT_KEY = "wclq_report";

  const EXAMPLES = [
    "Who pressed Bloodlust on the last pull, and when?",
    "Who ninja pulled the boss on the last pull?",
    "Who did the most damage to the adds in the first minute of the kill?",
    "Who died first on each wipe, and to what?",
    "Who interrupted the most on the kill?",
    "Who took the most avoidable damage on the last pull?",
  ];

  const $ = (id) => document.getElementById(id);
  const els = {
    userArea: $("user-area"), loginPanel: $("login-panel"), loginBtn: $("login-btn"),
    authError: $("auth-error"), askPanel: $("ask-panel"), report: $("report"),
    question: $("question"), examples: $("examples"), askBtn: $("ask-btn"),
    charCount: $("char-count"), results: $("results"), template: $("result-template"),
  };

  const storage = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
    remove(k) { try { localStorage.removeItem(k); } catch { /* private mode */ } },
  };

  // The API redirects back with #token=... or #authError=... after Discord login.
  function consumeAuthFragment() {
    const params = new URLSearchParams(location.hash.slice(1));
    const token = params.get("token");
    const authError = params.get("authError");
    if (token) storage.set(TOKEN_KEY, token);
    if (token || authError) history.replaceState(null, "", location.pathname + location.search);
    return authError;
  }

  async function api(path, options = {}) {
    const token = storage.get(TOKEN_KEY);
    const res = await fetch(API + path, {
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
    });
    return res;
  }

  function showLogin(error) {
    els.loginPanel.hidden = false;
    els.askPanel.hidden = true;
    els.userArea.replaceChildren();
    els.authError.hidden = !error;
    els.authError.textContent = error || "";
  }

  function showApp(user) {
    els.loginPanel.hidden = true;
    els.askPanel.hidden = false;
    const name = document.createElement("span");
    name.textContent = user.name;
    const logout = document.createElement("button");
    logout.className = "btn link";
    logout.textContent = "Log out";
    logout.onclick = () => { storage.remove(TOKEN_KEY); showLogin(); };
    els.userArea.replaceChildren(name, logout);
    els.report.value = storage.get(REPORT_KEY) || "";
    els.question.focus();
  }

  async function init() {
    const authError = consumeAuthFragment();
    renderExamples();

    if (authError) return showLogin(authError);
    try {
      const res = await api("/api/me");
      if (res.ok) return showApp(await res.json());
      storage.remove(TOKEN_KEY);
      showLogin();
    } catch {
      showLogin("Can't reach the server right now. Try again in a minute.");
    }
  }

  function renderExamples() {
    for (const text of EXAMPLES) {
      const chip = document.createElement("button");
      chip.className = "chip";
      chip.type = "button";
      chip.textContent = text;
      chip.onclick = () => { els.question.value = text; updateCount(); els.question.focus(); };
      els.examples.append(chip);
    }
  }

  function updateCount() {
    els.charCount.textContent = `${els.question.value.length} / 500`;
  }

  function addResultCard(question) {
    const card = els.template.content.firstElementChild.cloneNode(true);
    card.querySelector(".result-question").textContent = question;
    card.querySelector(".result-body").innerHTML =
      '<p class="thinking"><span class="spinner" aria-hidden="true"></span>Reading the logs…</p>';
    els.results.prepend(card);
    return card;
  }

  function fillResult(card, data) {
    const body = card.querySelector(".result-body");
    body.innerHTML = DOMPurify.sanitize(marked.parse(data.answer || ""));

    const calls = data.toolCalls || [];
    if (calls.length) {
      const trace = card.querySelector(".trace");
      trace.hidden = false;
      trace.querySelector("summary").textContent =
        `How I found this (${calls.length} lookup${calls.length === 1 ? "" : "s"})`;
      const list = trace.querySelector("ol");
      for (const c of calls) {
        const li = document.createElement("li");
        const code = document.createElement("code");
        code.textContent = `${c.name} ${c.input}`;
        li.append(code);
        if (c.error) {
          const err = document.createElement("span");
          err.className = "error";
          err.textContent = ` — ${c.error}`;
          li.append(err);
        }
        list.append(li);
      }
    }
  }

  function failResult(card, message) {
    const p = document.createElement("p");
    p.className = "error";
    p.textContent = message;
    card.querySelector(".result-body").replaceChildren(p);
  }

  async function ask() {
    const question = els.question.value.trim();
    const report = els.report.value.trim();
    if (!report) { els.report.focus(); return; }
    if (!question) { els.question.focus(); return; }

    storage.set(REPORT_KEY, report);
    els.askBtn.disabled = true;
    const card = addResultCard(question);

    try {
      const res = await api("/api/ask", { method: "POST", body: JSON.stringify({ report, question }) });
      if (res.status === 401) { storage.remove(TOKEN_KEY); showLogin("Your login expired. Please log in again."); return; }
      if (res.status === 429) { failResult(card, "You've hit the hourly question limit. Try again later."); return; }
      const data = await res.json().catch(() => ({}));
      if (!res.ok) { failResult(card, data.error || "Something went wrong answering that."); return; }
      fillResult(card, data);
    } catch {
      failResult(card, "Couldn't reach the server.");
    } finally {
      els.askBtn.disabled = false;
    }
  }

  els.loginBtn.onclick = () => {
    location.href = `${API}/auth/login?returnUrl=${encodeURIComponent(location.href.split("#")[0])}`;
  };
  els.askBtn.onclick = ask;
  els.question.addEventListener("input", updateCount);
  els.question.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) ask();
  });

  init();
})();
