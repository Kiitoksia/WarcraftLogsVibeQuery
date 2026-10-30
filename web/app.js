(() => {
  const API = window.WCLQ_CONFIG.apiBase.replace(/\/$/, "");
  const TOKEN_KEY = "wclq_token";
  const REPORT_KEY = "wclq_report";
  const FILTERS_KEY = "wclq_filters";

  const EXAMPLES = [
    "Who pressed Bloodlust on the last pull, and when?",
    "Who ninja pulled the boss on the last pull?",
    "Who did the most damage to the adds in phase 1 of the last pull?",
    "Who died first most often tonight, and to what?",
    "Who interrupted the most on the last pull?",
    "Who took the most avoidable damage on the last pull?",
  ];

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
    userArea: $("user-area"), loginPanel: $("login-panel"), loginBtn: $("login-btn"),
    authError: $("auth-error"), askPanel: $("ask-panel"), report: $("report"),
    question: $("question"), examples: $("examples"), askBtn: $("ask-btn"),
    charCount: $("char-count"), results: $("results"),
    deathsOn: $("filter-deaths"), deathsCount: $("filter-deaths-count"),
    askSummary: $("ask-summary"), askSummaryReport: $("ask-summary-report"), newQuestionBtn: $("new-question-btn"),
    glossaryBtn: $("glossary-btn"), glossaryDialog: $("glossary-dialog"), glossaryClose: $("glossary-close"),
    glossaryMeta: $("glossary-meta"), glossaryView: $("glossary-view"), glossaryEditor: $("glossary-editor"),
    glossaryError: $("glossary-error"), glossaryEdit: $("glossary-edit"), glossaryCancel: $("glossary-cancel"),
    glossarySave: $("glossary-save"),
  };

  const storage = {
    get(k) { try { return localStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
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

  // ---- Auth -------------------------------------------------------------------

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
    els.askPanel.hidden = true;
    els.askSummary.hidden = true;
    els.glossaryBtn.hidden = true;
    els.userArea.replaceChildren();
    els.authError.hidden = !error;
    els.authError.textContent = error || "";
  }

  function showApp(user) {
    els.loginPanel.hidden = true;
    els.askPanel.hidden = false;
    els.glossaryBtn.hidden = false;
    const logout = el("button", "btn link", "Log out");
    logout.onclick = () => { storage.remove(TOKEN_KEY); showLogin(); };
    els.userArea.replaceChildren(el("span", "", user.name), logout);
    els.report.value = storage.get(REPORT_KEY) || "";
    els.question.focus();
  }

  async function init() {
    const authError = consumeAuthFragment();
    renderExamples();
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

  // ---- Filters ------------------------------------------------------------------

  // Filters are sent with every message; the server applies them to all of the AI's lookups.
  function currentSettings() {
    const n = Math.round(Number(els.deathsCount.value));
    return { deathCutoff: els.deathsOn.checked && n >= 1 && n <= 30 ? n : null };
  }

  function describeSettings(settings) {
    return settings.deathCutoff ? `Ignoring events after ${settings.deathCutoff} player deaths` : null;
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

  // ---- Ask form -----------------------------------------------------------------

  function renderExamples() {
    for (const text of EXAMPLES) {
      const chip = el("button", "chip", text);
      chip.type = "button";
      chip.onclick = () => { els.question.value = text; updateCount(); els.question.focus(); };
      els.examples.append(chip);
    }
  }

  function updateCount() {
    els.charCount.textContent = `${els.question.value.length} / 500 · Ctrl+Enter to ask`;
  }

  // While a question is answered, fold the form into a one-line bar so the answer gets the screen.
  function collapseForm(report) {
    const code = report.match(/reports\/(?:a:)?([A-Za-z0-9]+)/)?.[1] || report;
    els.askSummaryReport.textContent = code;
    els.askPanel.hidden = true;
    els.askSummary.hidden = false;
  }

  function expandForm() {
    els.askSummary.hidden = true;
    els.askPanel.hidden = false;
    els.question.value = "";
    updateCount();
    scrollTo(els.askPanel);
    els.question.focus({ preventScroll: true });
  }

  function ask() {
    const question = els.question.value.trim();
    const report = els.report.value.trim();
    if (!report) { els.report.focus(); return; }
    if (!question) { els.question.focus(); return; }
    storage.set(REPORT_KEY, report);
    collapseForm(report);
    new Thread(report).send(question);
  }

  // ---- Answer decoration --------------------------------------------------------

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
        span.dataset.class = cls || "";
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

  // ---- Conversation thread ------------------------------------------------------

  // One card per conversation: the question, the AI's answers/questions, and a reply box.
  class Thread {
    constructor(report) {
      this.report = report;
      this.conversationId = null;
      this.busy = false;

      this.card = el("article", "panel thread");
      this.log = el("div", "thread-log");
      this.replyForm = el("form", "reply");
      this.replyInput = el("input");
      this.replyInput.maxLength = 500;
      this.replyInput.placeholder = "Ask a follow-up…";
      const send = el("button", "btn small", "Send");
      this.replyForm.append(this.replyInput, send);
      this.replyForm.hidden = true;
      this.replyForm.onsubmit = (e) => {
        e.preventDefault();
        const text = this.replyInput.value.trim();
        if (text) this.send(text);
      };

      this.card.append(this.log, this.replyForm);
      els.results.prepend(this.card);
      requestAnimationFrame(() => scrollTo(this.card));
    }

    async send(text) {
      if (this.busy) return;
      this.busy = true;
      els.askBtn.disabled = true;
      this.replyForm.hidden = true;
      this.replyInput.value = "";
      this.disableOptions();

      const settings = currentSettings();
      this.log.append(el("p", "msg-user", text));
      const filterNote = describeSettings(settings);
      if (filterNote) this.log.append(el("p", "msg-filters", `Filter: ${filterNote}`));

      const pending = el("p", "thinking");
      const status = el("span", "", "Reading the logs…");
      pending.append(el("span", "spinner"), status);
      this.log.append(pending);
      if (this.conversationId) scrollTo(pending, "nearest");
      const started = Date.now();
      const timer = setInterval(() => {
        const s = Math.round((Date.now() - started) / 1000);
        status.textContent = s < 25 ? `Reading the logs… ${s}s` : `Still digging (big questions take a while)… ${s}s`;
      }, 1000);

      try {
        let res;
        try {
          res = this.conversationId
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
        if (!res.ok) { this.error(data.error || "Something went wrong answering that."); return; }

        this.conversationId = data.conversationId;
        try {
          const block = this.render(data);
          requestAnimationFrame(() => scrollTo(block));
        } catch (err) {
          console.error("Failed to render answer", err, data);
          this.error("Got an answer but couldn't display it.");
        }
      } finally {
        this.busy = false;
        els.askBtn.disabled = false;
        this.replyForm.hidden = false;
      }
    }

    render(data) {
      const block = el("div", "msg-assistant");
      if (data.answer) {
        const body = el("div", "markdown");
        body.innerHTML = DOMPurify.sanitize(marked.parse(data.answer));
        decorateMarkdown(body, data.players);
        block.append(body);
      }

      for (const chart of data.charts || []) block.append(renderChart(chart));

      if (data.status === "question") {
        const q = el("div", "clarify");
        q.append(el("p", "clarify-label", "Quick question"));
        const qText = el("div", "clarify-question markdown");
        qText.innerHTML = DOMPurify.sanitize(marked.parseInline(data.question || ""));
        decorateMarkdown(qText, data.players);
        q.append(qText);
        if (data.options?.length) {
          const opts = el("div", "chips");
          for (const option of data.options) {
            const b = el("button", "chip option", option);
            b.type = "button";
            // "Something else / other / I'll type it" means "let me type", so focus the box instead of sending.
            b.onclick = /something else|\bother\b|type it|i'll type|let me type|own answer/i.test(option)
              ? () => { this.replyInput.placeholder = "Type your answer"; this.replyInput.focus(); }
              : () => this.send(option);
            opts.append(b);
          }
          q.append(opts);
        }
        block.append(q);
        this.replyInput.placeholder = data.options?.length ? "…or type your own answer" : "Type your answer";
      } else {
        this.replyInput.placeholder = "Ask a follow-up…";
      }

      if (data.links?.length) {
        const links = el("div", "wcl-links");
        for (const link of data.links) {
          const a = el("a", "wcl-link");
          a.append(el("span", "wcl-badge", "WCL"), el("span", "", link.label));
          a.href = link.url;
          a.target = "_blank";
          a.rel = "noopener noreferrer";
          links.append(a);
        }
        block.append(links);
      }

      if (data.toolCalls?.length) block.append(renderTrace(data.toolCalls, data.usage));
      this.log.append(block);
      refreshTooltips();
      if (data.status === "question") this.replyInput.focus({ preventScroll: true });
      return block;
    }

    disableOptions() {
      for (const b of this.log.querySelectorAll(".option")) b.disabled = true;
    }

    error(message) {
      this.log.append(el("p", "error", message));
    }
  }

  function renderTrace(calls, usage) {
    const details = el("details", "trace");
    let summary = `How I found this (${calls.length} lookup${calls.length === 1 ? "" : "s"})`;
    if (usage) {
      const tokens = usage.input + usage.cacheRead + usage.cacheWrite + usage.output;
      summary += ` · ${(tokens / 1000).toFixed(1)}k tokens`;
      if (usage.estimatedCostUsd != null) summary += ` · ≈$${usage.estimatedCostUsd.toFixed(3)}`;
    }
    details.append(el("summary", "", summary));
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

  // ---- Charts -------------------------------------------------------------------

  const formatAmount = (n) =>
    n >= 1e9 ? `${(n / 1e9).toFixed(2)}b` : n >= 1e6 ? `${(n / 1e6).toFixed(2)}m` : n >= 1e3 ? `${(n / 1e3).toFixed(1)}k` : `${Math.round(n)}`;
  const formatClock = (sec) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, "0")}`;
  const colorFor = (series, i) => CLASS_COLORS[series.class] || FALLBACK_COLORS[i % FALLBACK_COLORS.length];

  // A Warcraft Logs-style panel: per-player lines over time, then a ranked table.
  function renderChart(chart) {
    const panel = el("section", "chart-panel");
    panel.append(el("h3", "chart-title", chart.title));

    const canvasWrap = el("div", "chart-canvas");
    const canvas = el("canvas");
    canvasWrap.append(canvas);
    panel.append(canvasWrap);

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
          ticks: { color: "#9aa3b2", callback: (v) => formatClock(v), maxTicksLimit: 12 },
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

    panel.append(renderRankTable(chart));
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

      const pct = el("td", "rank-pct", `${((s.total / grand) * 100).toFixed(2)}%`);
      const barCell = el("td", "rank-bar");
      const bar = el("span");
      bar.style.width = `${(s.total / max) * 78}%`; // leave room for the amount beside the longest bar
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

  // ---- Glossary -----------------------------------------------------------------

  let glossaryText = "";

  async function openGlossary() {
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
    els.glossaryEdit.hidden = !g.canEdit;
    if (!g.canEdit) els.glossaryMeta.textContent += " · Only officers and listed editors can edit it.";
  }

  function setGlossaryEditing(editing) {
    els.glossaryView.hidden = editing;
    els.glossaryEditor.hidden = !editing;
    els.glossaryEdit.hidden = editing;
    els.glossaryCancel.hidden = !editing;
    els.glossarySave.hidden = !editing;
    if (editing) {
      els.glossaryEditor.value = glossaryText;
      els.glossaryEditor.focus();
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

  // ---- Wire up ------------------------------------------------------------------

  els.loginBtn.onclick = () => {
    location.href = `${API}/auth/login?returnUrl=${encodeURIComponent(location.href.split("#")[0])}`;
  };
  els.askBtn.onclick = ask;
  els.newQuestionBtn.onclick = expandForm;
  els.question.addEventListener("input", updateCount);
  els.question.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) ask();
  });
  els.deathsOn.addEventListener("change", saveFilters);
  els.deathsCount.addEventListener("change", saveFilters);

  els.glossaryBtn.onclick = openGlossary;
  els.glossaryClose.onclick = () => els.glossaryDialog.close();
  els.glossaryEdit.onclick = () => setGlossaryEditing(true);
  els.glossaryCancel.onclick = () => setGlossaryEditing(false);
  els.glossarySave.onclick = saveGlossary;

  init();
})();
