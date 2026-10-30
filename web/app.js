(() => {
  const API = window.WCLQ_CONFIG.apiBase.replace(/\/$/, "");
  const TOKEN_KEY = "wclq_token";
  const REPORT_KEY = "wclq_report";
  const FILTERS_KEY = "wclq_filters";
  const RECENT_KEY = "wclq_recent";
  const MAX_RECENT = 8;
  const PULLS_SHOWN = 8;

  // Standard WoW class colours, as used on Warcraft Logs.
  const CLASS_COLORS = {
    DeathKnight: "#C41E3A", DemonHunter: "#A330C9", Druid: "#FF7C0A", Evoker: "#33937F",
    Hunter: "#AAD372", Mage: "#3FC7EB", Monk: "#00FF98", Paladin: "#F48CBA", Priest: "#FFFFFF",
    Rogue: "#FFF468", Shaman: "#0070DD", Warlock: "#8788EE", Warrior: "#C69B6D",
  };
  const FALLBACK_COLORS = ["#e0b04a", "#7fb3ff", "#ff8a80", "#9be29b", "#d7a6ff", "#ffd27f"];
  const CHART_LINES = 10;

  const $ = (id) => document.getElementById(id);
  const els = {
    userArea: $("user-area"), glossaryBtn: $("glossary-btn"),
    loginPanel: $("login-panel"), loginBtn: $("login-btn"), authError: $("auth-error"),
    sidebar: $("sidebar"), workspace: $("workspace"),
    report: $("report"), reportSummary: $("report-summary"), reportTitle: $("report-title"),
    reportSub: $("report-sub"), reportStatus: $("report-status"),
    scopeBlock: $("scope-block"), scopeList: $("scope-list"), pullsMore: $("pulls-more"),
    deathsOn: $("filter-deaths"), deathsCount: $("filter-deaths-count"),
    recentList: $("recent-list"), clearRecent: $("clear-recent"),
    thread: $("thread"), emptyState: $("empty-state"),
    composer: $("composer"), question: $("question"), askBtn: $("ask-btn"), newBtn: $("new-btn"),
    charCount: $("char-count"), composerMode: $("composer-mode"),
    glossaryDialog: $("glossary-dialog"), glossaryClose: $("glossary-close"),
    glossaryMeta: $("glossary-meta"), glossaryView: $("glossary-view"), glossaryEditor: $("glossary-editor"),
    glossaryError: $("glossary-error"), glossaryEdit: $("glossary-edit"), glossaryCancel: $("glossary-cancel"),
    glossarySave: $("glossary-save"), glossaryAi: $("glossary-ai"), glossaryNote: $("glossary-note"),
    glossaryDraft: $("glossary-draft"), glossaryAiStatus: $("glossary-ai-status"), glossaryDraftNote: $("glossary-draft-note"),
  };

  const storage = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); return true; } catch { return false; } },
    remove(k) { try { localStorage.removeItem(k); } catch { /* private mode */ } },
  };

  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };

  const reducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const scrollTo = (node, block = "start") =>
    node.scrollIntoView({ behavior: reducedMotion() ? "auto" : "smooth", block });

  // ---- App state ------------------------------------------------------------------

  const state = {
    reportInfo: null,      // summary from /api/report
    scope: { kind: "auto" }, // "auto" | "all" | { kind: "pull", id }
    showAllPulls: false,
    thread: null,          // the conversation on screen
    busy: false,
  };

  // ---- Auth -----------------------------------------------------------------------

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
    return fetch(API + path, {
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
    });
  }

  function showLogin(error) {
    els.loginPanel.hidden = false;
    els.sidebar.hidden = true;
    els.workspace.hidden = true;
    els.glossaryBtn.hidden = true;
    els.userArea.replaceChildren();
    els.authError.hidden = !error;
    els.authError.textContent = error || "";
  }

  function showApp(user) {
    els.loginPanel.hidden = true;
    els.sidebar.hidden = false;
    els.workspace.hidden = false;
    els.glossaryBtn.hidden = false;
    const logout = el("button", "btn link", "Log out");
    logout.onclick = () => { storage.remove(TOKEN_KEY); showLogin(); };
    els.userArea.replaceChildren(el("span", "user-name", user.name), logout);

    els.report.value = storage.get(REPORT_KEY) || "";
    renderRecent();
    if (els.report.value) loadReport();
    els.question.focus();
  }

  async function init() {
    const authError = consumeAuthFragment();
    loadFilters();
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

  // ---- Report & scope ------------------------------------------------------------

  let reportTimer = null;
  let reportRequest = 0;

  function reportCode(value) {
    return (value || "").match(/reports\/(?:a:)?([A-Za-z0-9]+)/)?.[1] || (value || "").trim();
  }

  async function loadReport() {
    const value = els.report.value.trim();
    const requestId = ++reportRequest;
    state.reportInfo = null;
    els.reportSummary.hidden = true;
    els.scopeBlock.hidden = true;
    if (!value) { els.reportStatus.hidden = true; return; }

    els.reportStatus.hidden = false;
    els.reportStatus.textContent = "Loading report…";
    try {
      const res = await api(`/api/report?code=${encodeURIComponent(value)}`);
      if (requestId !== reportRequest) return; // a newer report was typed meanwhile
      if (res.status === 401) { storage.remove(TOKEN_KEY); showLogin("Your login expired. Please log in again."); return; }
      const data = await res.json().catch(() => ({}));
      if (!res.ok) { els.reportStatus.textContent = data.error || "Couldn't load that report."; return; }

      storage.set(REPORT_KEY, value);
      state.reportInfo = data;
      els.reportStatus.hidden = true;
      els.reportTitle.textContent = data.title || data.code;
      const pulls = data.pulls || [];
      const kills = pulls.filter((p) => p.kill).length;
      const best = pulls.filter((p) => !p.kill && p.bossPercentage != null)
        .reduce((min, p) => Math.min(min, Number(p.bossPercentage.toFixed(1))), Infinity);
      const bossText = data.bosses?.length === 1 ? data.bosses[0] : `${data.bosses?.length || 0} bosses`;
      const progress = kills > 0 ? `${kills} kill${kills === 1 ? "" : "s"}` : best < Infinity ? `best ${best}%` : "";
      els.reportSub.textContent = [bossText, `${pulls.length} pull${pulls.length === 1 ? "" : "s"}`, progress].filter(Boolean).join(" · ");
      els.reportSummary.hidden = false;
      renderScope();
    } catch {
      if (requestId === reportRequest) els.reportStatus.textContent = "Couldn't reach the server.";
    }
  }

  function scopeKey(scope) {
    return scope.kind === "pull" ? `pull:${scope.id}` : scope.kind;
  }

  function setScope(scope) {
    if (scopeKey(scope) === scopeKey(state.scope)) return;
    state.scope = scope;
    renderScope();
    startFresh(); // a different pinned scope starts a new conversation
  }

  function renderScope() {
    const info = state.reportInfo;
    if (!info) return;
    const pulls = [...(info.pulls || [])].reverse(); // newest first
    const multipleBosses = (info.bosses?.length || 0) > 1;
    const shown = state.showAllPulls ? pulls : pulls.slice(0, PULLS_SHOWN);

    const option = (scope, content, title) => {
      const selected = scopeKey(scope) === scopeKey(state.scope);
      const b = el("button", `scope-option${selected ? " selected" : ""}`);
      b.type = "button";
      b.setAttribute("role", "radio");
      b.setAttribute("aria-checked", String(selected));
      if (title) b.title = title;
      b.append(...content);
      b.onclick = () => setScope(scope);
      return b;
    };

    const items = [
      option({ kind: "auto" }, [el("span", "scope-name", "Let the AI decide"), el("span", "scope-hint", "last pull, or all for \"tonight\"")]),
      option({ kind: "all" }, [el("span", "scope-name", `All ${pulls.length} pulls`)]),
    ];
    for (const p of shown) {
      const progress = p.kill ? 100 : Math.max(0, 100 - (p.bossPercentage ?? 100));
      const bar = el("span", "pull-bar");
      const fill = el("span", `pull-fill${p.kill ? " kill" : ""}`);
      fill.style.width = `${progress}%`;
      bar.append(fill);
      const pct = p.bossPercentage == null ? "?" : Number(p.bossPercentage).toFixed(1);
      const label = el("span", "pull-result", p.kill ? "Kill" : `${pct}%`);
      const name = el("span", "pull-id", `#${p.id}`);
      const content = multipleBosses ? [name, el("span", "pull-boss", p.name), label] : [name, bar, label];
      items.push(option({ kind: "pull", id: p.id }, content, `${p.name}, pull ${p.id}: ${p.kill ? "kill" : `wipe at ${pct}%`}`));
    }
    els.scopeList.replaceChildren(...items);
    els.pullsMore.hidden = pulls.length <= PULLS_SHOWN;
    els.pullsMore.textContent = state.showAllPulls ? "Show fewer pulls" : `Show all ${pulls.length} pulls`;
    els.scopeBlock.hidden = false;
  }

  // ---- Filters ----------------------------------------------------------------------

  // Sent with every message: the pinned scope and filters. The server applies filters to every lookup.
  function currentSettings() {
    const n = Math.round(Number(els.deathsCount.value));
    return {
      deathCutoff: els.deathsOn.checked && n >= 1 && n <= 30 ? n : null,
      pull: state.scope.kind === "pull" ? state.scope.id : null,
      allPulls: state.scope.kind === "all",
    };
  }

  function describeFilters(settings) {
    return settings.deathCutoff ? `ignoring events after ${settings.deathCutoff} deaths` : null;
  }

  function loadFilters() {
    try {
      const saved = JSON.parse(storage.get(FILTERS_KEY) || "{}");
      els.deathsOn.checked = !!saved.deathsOn;
      if (saved.deathsCount) els.deathsCount.value = saved.deathsCount;
    } catch { /* ignore bad saved data */ }
    els.deathsCount.disabled = !els.deathsOn.checked;
  }

  function saveFilters() {
    storage.set(FILTERS_KEY, JSON.stringify({ deathsOn: els.deathsOn.checked, deathsCount: els.deathsCount.value }));
    els.deathsCount.disabled = !els.deathsOn.checked;
  }

  // ---- Recent questions (kept in this browser) -------------------------------------

  function loadRecent() {
    try { return JSON.parse(storage.get(RECENT_KEY) || "[]"); } catch { return []; }
  }

  function saveRecent(list) {
    // Charts make entries large; drop the oldest until it fits the browser's storage.
    let trimmed = list.slice(0, MAX_RECENT);
    while (trimmed.length && !storage.set(RECENT_KEY, JSON.stringify(trimmed))) trimmed = trimmed.slice(0, -1);
  }

  function rememberThread(thread) {
    const list = loadRecent().filter((r) => r.key !== thread.key);
    list.unshift({
      key: thread.key,
      conversationId: thread.conversationId,
      report: thread.report,
      title: thread.entries[0]?.question || "Question",
      at: Date.now(),
      entries: thread.entries,
    });
    saveRecent(list);
    renderRecent();
  }

  function renderRecent() {
    const list = loadRecent();
    els.clearRecent.hidden = list.length === 0;
    if (!list.length) {
      els.recentList.replaceChildren(el("p", "muted small", "Your questions appear here."));
      return;
    }
    els.recentList.replaceChildren(...list.map((r) => {
      const b = el("button", `recent-item${state.thread?.key === r.key ? " current" : ""}`);
      b.type = "button";
      b.append(el("span", "recent-title", r.title), el("span", "recent-meta", `${reportCode(r.report)} · ${timeAgo(r.at)}`));
      b.onclick = () => openRecent(r);
      return b;
    }));
  }

  function timeAgo(t) {
    const mins = Math.round((Date.now() - t) / 60000);
    if (mins < 1) return "just now";
    if (mins < 60) return `${mins}m ago`;
    const hours = Math.round(mins / 60);
    return hours < 24 ? `${hours}h ago` : `${Math.round(hours / 24)}d ago`;
  }

  function openRecent(r) {
    if (state.busy) return;
    if (reportCode(r.report) !== reportCode(els.report.value)) {
      els.report.value = r.report;
      loadReport();
    }
    state.thread = new Thread(r.report, r);
    updateComposer();
    renderRecent();
    scrollTo(els.thread);
  }

  // ---- Conversation --------------------------------------------------------------

  // One conversation on screen at a time. Typing continues it; "New question", a different report
  // or a different pinned pull starts a new one (the old one stays in Recent).
  class Thread {
    constructor(report, saved) {
      this.report = report;
      this.key = saved?.key || `t${Date.now()}`;
      this.conversationId = saved?.conversationId || null;
      this.entries = [];
      els.thread.replaceChildren();
      for (const entry of saved?.entries || []) this.renderEntry(entry);
      this.entries = saved?.entries ? [...saved.entries] : [];
      updateComposer();
    }

    async send(text, { asNew = false } = {}) {
      if (state.busy) return;
      state.busy = true;
      updateComposer();

      const settings = currentSettings();
      const entry = { question: text, filters: describeFilters(settings) };
      const { block, scopeLine } = this.renderQuestion(entry);
      const pending = el("p", "thinking");
      const status = el("span", "", "Reading the logs…");
      pending.append(el("span", "spinner"), status);
      els.thread.append(pending);
      requestAnimationFrame(() => scrollTo(block));
      const started = Date.now();
      const timer = setInterval(() => {
        const s = Math.round((Date.now() - started) / 1000);
        status.textContent = s < 25 ? `Reading the logs… ${s}s` : `Still digging (big questions take a while)… ${s}s`;
      }, 1000);

      try {
        let res;
        try {
          res = this.conversationId && !asNew
            ? await api(`/api/ask/${encodeURIComponent(this.conversationId)}/reply`, {
                method: "POST", body: JSON.stringify({ message: text, settings }),
              })
            : await api("/api/ask", {
                method: "POST", body: JSON.stringify({ report: this.report, question: text, settings }),
              });
        } catch {
          this.error("Couldn't reach the server.");
          return;
        } finally {
          clearInterval(timer);
          pending.remove();
        }

        if (res.status === 401) { storage.remove(TOKEN_KEY); showLogin("Your login expired. Please log in again."); return; }
        if (res.status === 429) { this.error("You've hit the hourly question limit. Try again later."); return; }
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
          // The server forgets conversations after 30 idle minutes: carry on as a fresh question.
          if (this.conversationId && !asNew && /expired/i.test(data.error || "")) {
            block.remove();
            this.notice("That conversation expired, so this was asked as a new question.");
            state.busy = false;
            this.conversationId = null;
            return await this.send(text, { asNew: true }); // await, so our finally runs after the retry
          }
          this.error(data.error || "Something went wrong answering that.");
          return;
        }

        this.conversationId = data.conversationId;
        entry.data = data;
        this.entries.push(entry);
        try {
          scopeLine.textContent = scopeText(data, entry);
          const answer = this.renderAnswer(data);
          requestAnimationFrame(() => scrollTo(answer));
        } catch (err) {
          console.error("Failed to render answer", err, data);
          this.error("Got an answer but couldn't display it.");
        }
        rememberThread(this);
      } finally {
        state.busy = false;
        updateComposer();
      }
    }

    renderEntry(entry) {
      const { scopeLine } = this.renderQuestion(entry);
      if (entry.data) scopeLine.textContent = scopeText(entry.data, entry);
      if (entry.data) this.renderAnswer(entry.data, { replay: true });
    }

    renderQuestion(entry) {
      els.emptyState.hidden = true;
      const block = el("div", "msg-user");
      block.append(el("p", "msg-user-text", entry.question));
      const scopeLine = el("p", "msg-scope", entry.filters ? entry.filters : "");
      block.append(scopeLine);
      els.thread.append(block);
      return { block, scopeLine };
    }

    renderAnswer(data, { replay = false } = {}) {
      const card = el("article", "answer");
      if (data.answer) {
        const body = el("div", "markdown");
        body.innerHTML = DOMPurify.sanitize(marked.parse(data.answer));
        decorateMarkdown(body, data.players);
        card.append(body);
      }

      for (const chart of data.charts || []) card.append(renderChart(chart));

      if (data.status === "question") card.append(this.renderClarify(data, replay));

      const footer = el("div", "answer-footer");
      if (data.usage) {
        const u = data.usage;
        const tokens = u.input + u.cacheRead + u.cacheWrite + u.output;
        let usage = `${data.toolCalls?.length || 0} lookup${data.toolCalls?.length === 1 ? "" : "s"} · ${(tokens / 1000).toFixed(1)}k tokens`;
        if (u.estimatedCostUsd != null) usage += ` · ≈$${u.estimatedCostUsd.toFixed(3)}`;
        footer.append(el("span", "", usage));
      }
      for (const link of data.links || []) {
        const a = el("a", "wcl-link");
        a.append(el("span", "wcl-badge", "WCL"), el("span", "", link.label));
        a.href = link.url;
        a.target = "_blank";
        a.rel = "noopener noreferrer";
        footer.append(a);
      }
      if (data.toolCalls?.length) footer.append(renderTrace(data.toolCalls));
      card.append(footer);

      els.thread.append(card);
      refreshTooltips();

      if (!replay) {
        if (data.status === "question") {
          els.question.placeholder = data.options?.length ? "…or type your own answer" : "Type your answer";
          els.question.focus({ preventScroll: true });
        } else {
          els.question.placeholder = "Ask a follow-up, or a new question about this report…";
        }
      }
      return card;
    }

    renderClarify(data, replay) {
      const q = el("div", "clarify");
      q.append(el("p", "clarify-label", "Quick question"));
      const qText = el("div", "clarify-question markdown");
      qText.innerHTML = DOMPurify.sanitize(marked.parseInline(data.question || ""));
      decorateMarkdown(qText, data.players);
      q.append(qText);
      if (data.options?.length) {
        const opts = el("div", "chips");
        for (const option of data.options) {
          const label = typeof option === "string" ? option : option.label;
          let b;
          if (option.spellId) {
            // Ability options are Wowhead links styled as buttons: Wowhead's script only adds its icon and
            // hover tooltip to links. Clicking still answers instead of opening Wowhead.
            b = el("a", "chip option", label);
            b.href = `https://www.wowhead.com/spell=${option.spellId}`;
            b.setAttribute("role", "button");
          } else {
            b = el("button", "chip option", label);
            b.type = "button";
          }
          const letMeType = /something else|\bother\b|type it|i'll type|let me type|own answer/i.test(label);
          b.onclick = (e) => {
            e.preventDefault();
            if (b.classList.contains("disabled")) return;
            if (letMeType) { els.question.focus(); return; }
            disableOptions(q);
            this.send(label);
          };
          opts.append(b);
        }
        if (replay) disableOptions(opts);
        q.append(opts);
      }
      return q;
    }

    notice(message) {
      els.thread.append(el("p", "notice", message));
    }

    error(message) {
      els.thread.append(el("p", "error", message));
    }
  }

  // The line under a question: what the answer covered ("All 29 pulls · Phase 1") plus any filters.
  function scopeText(data, entry) {
    const scope = data.scope || (data.status === "answer" && !data.toolCalls?.length ? "From the previous answer" : null);
    return [scope, entry.filters].filter(Boolean).join(" · ");
  }

  function disableOptions(root) {
    for (const b of root.querySelectorAll(".option")) {
      b.disabled = true;
      b.classList.add("disabled");
      b.setAttribute("aria-disabled", "true");
    }
  }

  function startFresh() {
    state.thread = null;
    els.thread.replaceChildren();
    els.emptyState.hidden = false;
    els.question.placeholder = "Ask a question about this report…";
    renderRecent();
    updateComposer();
  }

  function ask() {
    const text = els.question.value.trim();
    const report = els.report.value.trim();
    if (!report) { els.report.focus(); return; }
    if (!text || state.busy) { els.question.focus(); return; }
    storage.set(REPORT_KEY, report);
    if (!state.thread || reportCode(state.thread.report) !== reportCode(report)) state.thread = new Thread(report);
    els.question.value = "";
    autoGrow();
    updateCount();
    state.thread.send(text);
  }

  function updateComposer() {
    els.askBtn.disabled = state.busy;
    els.newBtn.hidden = !state.thread;
    els.composerMode.textContent = state.thread
      ? "Continues this conversation · New question to start over"
      : "Enter to send · Shift+Enter for a new line";
  }

  function updateCount() {
    els.charCount.textContent = `${els.question.value.length} / 500`;
  }

  function autoGrow() {
    els.question.style.height = "auto";
    els.question.style.height = `${Math.min(els.question.scrollHeight, 160)}px`;
  }

  // ---- Answer decoration -----------------------------------------------------------

  // Wrap player names in the answer with their class colour, like Warcraft Logs does.
  function colourPlayers(root, players) {
    if (!players?.length) return;
    const classOf = new Map(players.map((p) => [p.name.toLocaleLowerCase(), p.class]));
    const names = [...new Set(players.map((p) => p.name))].sort((a, b) => b.length - a.length);
    const escaped = names.map((n) => n.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
    const pattern = new RegExp(`(?<![\\p{L}\\p{N}])(${escaped.join("|")})(?![\\p{L}\\p{N}])`, "giu");

    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: (node) => node.parentElement.closest("a, code, pre, .player")
        ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
    });
    const textNodes = [];
    while (walker.nextNode()) textNodes.push(walker.currentNode);

    for (const node of textNodes) {
      const text = node.nodeValue;
      pattern.lastIndex = 0;
      if (!pattern.test(text)) continue;
      pattern.lastIndex = 0;
      const frag = document.createDocumentFragment();
      let last = 0;
      for (const m of text.matchAll(pattern)) {
        frag.append(text.slice(last, m.index));
        const span = el("span", "player", m[0]);
        const cls = classOf.get(m[0].toLocaleLowerCase());
        span.style.color = CLASS_COLORS[cls] || "";
        frag.append(span);
        last = m.index + m[0].length;
      }
      frag.append(text.slice(last));
      node.replaceWith(frag);
    }
  }

  function decorateMarkdown(root, players) {
    for (const a of root.querySelectorAll("a[href]")) {
      a.target = "_blank";
      a.rel = "noopener noreferrer";
    }
    colourPlayers(root, players);
  }

  // Wowhead's script turns spell links into hover tooltips; tell it about newly added links.
  function refreshTooltips() {
    try { window.$WowheadPower?.refreshLinks?.(); } catch { /* script not loaded yet */ }
    try { window.WH?.Tooltips?.refreshLinks?.(); } catch { /* older/newer script versions */ }
  }

  function renderTrace(calls) {
    const details = el("details", "trace");
    details.append(el("summary", "", "How I found this"));
    const list = el("ol");
    for (const c of calls) {
      const li = el("li");
      li.append(el("code", "", `${c.name} ${c.input}`));
      if (c.error) li.append(el("span", "error", ` — ${c.error}`));
      list.append(li);
    }
    details.append(list);
    return details;
  }

  // ---- Charts ----------------------------------------------------------------------

  const formatAmount = (n) =>
    n >= 1e9 ? `${(n / 1e9).toFixed(2)}b` : n >= 1e6 ? `${(n / 1e6).toFixed(2)}m` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}k` : `${Math.round(n)}`;
  const formatClock = (sec) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, "0")}`;
  const colorFor = (series, i) => CLASS_COLORS[series.class] || FALLBACK_COLORS[i % FALLBACK_COLORS.length];

  // A Warcraft Logs-style panel: per-player lines over time (or per pull) beside a ranked table.
  function renderChart(chart) {
    const panel = el("section", "chart-panel");
    panel.append(el("h3", "chart-title", chart.title));

    const canvasWrap = el("div", "chart-canvas");
    const canvas = el("canvas");
    canvasWrap.append(canvas);

    // "timeline": x is seconds into the fight. "pulls": x is one category per pull.
    const perPull = chart.kind === "pulls";
    const pointCount = Math.max(...chart.series.map((s) => s.points.length));
    const xs = perPull
      ? chart.xLabels
      : Array.from({ length: pointCount }, (_, i) => chart.startSec + i * chart.intervalSec);
    const toPoints = (values) => perPull ? values : values.map((y, i) => ({ x: xs[i], y }));
    const top = chart.series.slice(0, CHART_LINES);
    const total = Array.from({ length: pointCount }, (_, i) => chart.series.reduce((sum, s) => sum + (s.points[i] || 0), 0));

    const lineStyle = perPull
      ? { pointRadius: 2.5, pointHoverRadius: 4, tension: 0.15 }
      : { pointRadius: 0, tension: 0.3 };
    const datasets = [
      { label: "Total", data: toPoints(total), borderColor: "#7fd3ff", borderWidth: 2, hidden: true, ...lineStyle },
      ...top.map((s, i) => ({
        label: s.name, data: toPoints(s.points),
        borderColor: colorFor(s, i), backgroundColor: colorFor(s, i), borderWidth: 1.5, ...lineStyle,
      })),
    ];

    const xScale = perPull
      ? {
          type: "category",
          ticks: { color: "#9aa3b2", autoSkip: true, maxRotation: 0, callback: (v, i) => xs[i].replace("Pull ", "") },
          grid: { color: "rgba(127,211,255,0.08)" },
          title: { display: true, text: "Pull", color: "#9aa3b2" },
        }
      : {
          type: "linear", min: xs[0], max: xs[xs.length - 1],
          ticks: { color: "#9aa3b2", callback: (v) => formatClock(v), maxTicksLimit: 10 },
          grid: { color: "rgba(127,211,255,0.08)" },
        };

    // Chart.js needs the canvas in the document to size itself, so build after this tick.
    requestAnimationFrame(() => new Chart(canvas, {
      type: "line",
      data: perPull ? { labels: xs, datasets } : { datasets },
      options: {
        responsive: true, maintainAspectRatio: false, animation: false, parsing: perPull, normalized: true,
        interaction: { mode: "index", intersect: false },
        scales: {
          x: xScale,
          y: {
            beginAtZero: true,
            ticks: { color: "#9aa3b2", callback: (v) => formatAmount(v) },
            grid: { color: "rgba(255,255,255,0.06)" },
            title: { display: true, text: chart.metric, color: "#9aa3b2" },
          },
        },
        plugins: {
          legend: { labels: { color: "#c9d1dc", boxWidth: 12, boxHeight: 2, font: { size: 11 } } },
          tooltip: {
            itemSort: (a, b) => b.parsed.y - a.parsed.y,
            filter: (item) => item.parsed.y > 0,
            callbacks: {
              title: (items) => perPull ? xs[items[0].dataIndex] : formatClock(items[0].parsed.x),
              label: (item) => ` ${item.dataset.label}: ${formatAmount(item.parsed.y)} ${chart.metric}`,
            },
          },
        },
      },
    }));

    const tableWrap = el("div", "rank-wrap");
    tableWrap.append(renderRankTable(chart));
    panel.append(canvasWrap, tableWrap);
    if (chart.series.length > CHART_LINES)
      panel.append(el("p", "chart-note", `Graph shows the top ${CHART_LINES}; the table lists everyone. Click names in the legend to hide/show lines.`));
    return panel;
  }

  function renderRankTable(chart) {
    const grand = chart.series.reduce((sum, s) => sum + s.total, 0) || 1;
    const max = chart.series[0]?.total || 1;
    const table = el("table", "rank-table");
    const head = el("tr");
    for (const h of ["Name", "", chart.totalLabel, chart.secondaryLabel]) head.append(el("th", "", h));
    const thead = el("thead");
    thead.append(head);
    table.append(thead);
    const body = el("tbody");
    chart.series.forEach((s, i) => {
      const row = el("tr");
      const name = el("td", "rank-name", s.name);
      name.style.color = colorFor(s, i);

      const pct = el("td", "rank-pct", `${((s.total / grand) * 100).toFixed(1)}%`);
      const barCell = el("td", "rank-bar");
      const bar = el("span");
      bar.style.width = `${(s.total / max) * 70}%`; // leave room for the amount beside the longest bar
      bar.style.background = colorFor(s, i);
      const amount = el("span", "rank-amount", formatAmount(s.total));
      barCell.append(bar, amount);

      const rate = el("td", "rank-rate", s.secondary.toLocaleString(undefined, { maximumFractionDigits: 1 }));
      row.append(name, pct, barCell, rate);
      body.append(row);
    });
    table.append(body);
    return table;
  }

  // ---- Glossary --------------------------------------------------------------------

  let glossaryText = "";
  let canEditGlossary = false;

  async function openGlossary() {
    canEditGlossary = false;
    setGlossaryEditing(false);
    els.glossaryError.hidden = true;
    els.glossaryMeta.textContent = "Loading…";
    els.glossaryView.replaceChildren();
    els.glossaryDialog.showModal();
    try {
      const res = await api("/api/glossary");
      if (res.status === 401) { els.glossaryDialog.close(); showLogin("Your login expired. Please log in again."); return; }
      showGlossary(await res.json());
    } catch {
      els.glossaryMeta.textContent = "";
      showGlossaryError("Couldn't load the glossary.");
    }
  }

  function showGlossary(g) {
    glossaryText = g.text || "";
    els.glossaryView.innerHTML = DOMPurify.sanitize(marked.parse(glossaryText || "_The glossary is empty._"));
    decorateMarkdown(els.glossaryView, []);
    refreshTooltips();
    els.glossaryMeta.textContent = g.updatedBy
      ? `Last edited by ${g.updatedBy} on ${new Date(g.updatedAt).toLocaleString()}`
      : "Not edited yet (this is the built-in starting version).";
    canEditGlossary = !!g.canEdit;
    els.glossaryEdit.hidden = !g.canEdit;
    els.glossaryAi.hidden = !g.canEdit;
    if (!g.canEdit) els.glossaryMeta.textContent += " · Only officers and listed editors can edit it.";
  }

  // draft: text to put in the editor instead of the saved glossary (an AI draft to review).
  function setGlossaryEditing(editing, draft) {
    els.glossaryView.hidden = editing;
    els.glossaryEditor.hidden = !editing;
    els.glossaryEdit.hidden = editing || !canEditGlossary;
    els.glossaryAi.hidden = editing || !canEditGlossary;
    els.glossaryCancel.hidden = !editing;
    els.glossarySave.hidden = !editing;
    els.glossaryDraftNote.hidden = !(editing && draft);
    if (editing) {
      els.glossaryEditor.value = draft ?? glossaryText;
      els.glossaryEditor.focus();
    }
  }

  // Ask the AI to fold a plain-English note into the glossary; the result opens in the editor to review.
  async function draftGlossary() {
    const note = els.glossaryNote.value.trim();
    if (!note) { els.glossaryNote.focus(); return; }
    els.glossaryDraft.disabled = true;
    els.glossaryError.hidden = true;
    els.glossaryAiStatus.textContent = "Drafting… (looking up spell ids in your current report)";
    try {
      const report = els.report.value.trim() || storage.get(REPORT_KEY) || "";
      const res = await api("/api/glossary/draft", { method: "POST", body: JSON.stringify({ note, report }) });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) { showGlossaryError(data.error || "Couldn't draft that change."); return; }
      els.glossaryNote.value = "";
      setGlossaryEditing(true, data.text);
    } catch {
      showGlossaryError("Couldn't reach the server.");
    } finally {
      els.glossaryDraft.disabled = false;
      els.glossaryAiStatus.textContent = "The AI rewrites the glossary for you to check before saving.";
    }
  }

  function showGlossaryError(message) {
    els.glossaryError.textContent = message;
    els.glossaryError.hidden = false;
  }

  async function saveGlossary() {
    els.glossarySave.disabled = true;
    els.glossaryError.hidden = true;
    try {
      const res = await api("/api/glossary", { method: "PUT", body: JSON.stringify({ text: els.glossaryEditor.value }) });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) { showGlossaryError(data.error || "Couldn't save the glossary."); return; }
      showGlossary(data);
      setGlossaryEditing(false);
    } catch {
      showGlossaryError("Couldn't reach the server.");
    } finally {
      els.glossarySave.disabled = false;
    }
  }

  // ---- Wire up ---------------------------------------------------------------------

  els.loginBtn.onclick = () => {
    location.href = `${API}/auth/login?returnUrl=${encodeURIComponent(location.href.split("#")[0])}`;
  };

  els.composer.onsubmit = (e) => { e.preventDefault(); ask(); };
  els.question.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) { e.preventDefault(); ask(); }
  });
  els.question.addEventListener("input", () => { updateCount(); autoGrow(); });
  els.newBtn.onclick = () => { startFresh(); els.question.focus(); };

  els.report.addEventListener("input", () => {
    clearTimeout(reportTimer);
    reportTimer = setTimeout(() => {
      state.scope = { kind: "auto" };
      state.showAllPulls = false;
      if (state.thread && reportCode(state.thread.report) !== reportCode(els.report.value)) startFresh();
      loadReport();
    }, 500);
  });
  els.pullsMore.onclick = () => { state.showAllPulls = !state.showAllPulls; renderScope(); };

  els.deathsOn.addEventListener("change", saveFilters);
  els.deathsCount.addEventListener("change", saveFilters);
  els.clearRecent.onclick = () => { storage.remove(RECENT_KEY); renderRecent(); };

  els.glossaryBtn.onclick = openGlossary;
  els.glossaryClose.onclick = () => els.glossaryDialog.close();
  els.glossaryEdit.onclick = () => setGlossaryEditing(true);
  els.glossaryCancel.onclick = () => setGlossaryEditing(false);
  els.glossarySave.onclick = saveGlossary;
  els.glossaryDraft.onclick = draftGlossary;

  init();
})();
