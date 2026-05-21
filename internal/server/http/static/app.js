// JMW Agent UI - vanilla JS

// CSRF helper: read token from cookie and attach to fetch().
function getCookie(name) {
  const m = document.cookie.match('(^|;) ?' + name + '=([^;]*)(;|$)');
  return m ? decodeURIComponent(m[2]) : '';
}

async function apiFetch(url, opts = {}) {
  opts.headers = opts.headers || {};
  opts.headers['X-CSRF-Token'] = getCookie('jmw_csrf');
  opts.credentials = 'same-origin';
  return fetch(url, opts);
}

// Theme toggle (persisted)
function applyTheme(t) {
  document.documentElement.setAttribute('data-theme', t);
  try { localStorage.setItem('jmw_theme', t); } catch (e) {}
}
(function initTheme() {
  let t = 'dark';
  try { t = localStorage.getItem('jmw_theme') || 'dark'; } catch (e) {}
  applyTheme(t);
})();

// CPU chart on agent_detail.html
async function renderCpuChart() {
  const c = document.getElementById('cpu-chart');
  if (!c) return;
  const id = c.getAttribute('data-agent');
  const res = await apiFetch('/api/v1/ui/agents/' + encodeURIComponent(id) + '/metrics?since=1h');
  if (!res.ok) return;
  const data = await res.json();
  drawLineChart(c, (data.snapshots || []).map(s => ({ x: new Date(s.ts), y: s.cpu_pct || 0 })), { yMax: 100, label: 'CPU %' });
}

function drawLineChart(canvas, points, opts) {
  const ctx = canvas.getContext('2d');
  const dpr = window.devicePixelRatio || 1;
  const w = canvas.width = canvas.clientWidth * dpr;
  const h = canvas.height = canvas.clientHeight * dpr;
  ctx.clearRect(0, 0, w, h);

  const css = getComputedStyle(document.documentElement);
  const fg = css.getPropertyValue('--fg').trim() || '#e6edf3';
  const muted = css.getPropertyValue('--fg-muted').trim() || '#8b949e';
  const accent = css.getPropertyValue('--accent').trim() || '#2f81f7';

  const padL = 40 * dpr, padR = 16 * dpr, padT = 16 * dpr, padB = 28 * dpr;
  const pw = w - padL - padR;
  const ph = h - padT - padB;

  ctx.strokeStyle = muted;
  ctx.fillStyle = muted;
  ctx.font = (12 * dpr) + 'px system-ui, sans-serif';
  ctx.lineWidth = dpr;
  ctx.beginPath();
  ctx.moveTo(padL, padT); ctx.lineTo(padL, padT + ph); ctx.lineTo(padL + pw, padT + ph);
  ctx.stroke();

  if (!points.length) {
    ctx.fillStyle = muted;
    ctx.fillText('No data', padL + 10, padT + ph / 2);
    return;
  }

  const xs = points.map(p => +p.x);
  const xMin = Math.min(...xs), xMax = Math.max(...xs) || (xMin + 1);
  const yMax = opts.yMax || Math.max(...points.map(p => p.y), 1);

  // y-axis labels (0, 25, 50, 75, 100)
  for (let i = 0; i <= 4; i++) {
    const v = (yMax / 4) * i;
    const y = padT + ph - (v / yMax) * ph;
    ctx.fillText(v.toFixed(0), 4 * dpr, y + 4 * dpr);
    ctx.strokeStyle = '#30363d';
    ctx.beginPath(); ctx.moveTo(padL, y); ctx.lineTo(padL + pw, y); ctx.stroke();
  }

  ctx.strokeStyle = accent;
  ctx.lineWidth = 2 * dpr;
  ctx.beginPath();
  points.forEach((p, i) => {
    const x = padL + ((+p.x - xMin) / (xMax - xMin || 1)) * pw;
    const y = padT + ph - (p.y / yMax) * ph;
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  });
  ctx.stroke();

  ctx.fillStyle = fg;
  ctx.fillText(opts.label || '', padL, 12 * dpr);
}

// Tabs (deep-linkable via URL hash)
function initTabs() {
  document.querySelectorAll('[data-tabs]').forEach((root) => {
    const tabs = root.querySelectorAll('.tab');
    const panels = root.querySelectorAll('.tabpanel');
    function activate(name, push) {
      let found = false;
      tabs.forEach((t) => {
        const match = t.dataset.tab === name;
        t.setAttribute('aria-selected', match ? 'true' : 'false');
        if (match) found = true;
      });
      panels.forEach((p) => { p.hidden = p.dataset.panel !== name; });
      if (found && push) {
        history.replaceState(null, '', '#' + name);
      }
      return found;
    }
    tabs.forEach((t) => {
      t.addEventListener('click', () => activate(t.dataset.tab, true));
    });
    const initial = (location.hash || '').replace(/^#/, '');
    if (!initial || !activate(initial, false)) {
      const first = tabs[0];
      if (first) activate(first.dataset.tab, false);
    }
  });
}

// Sortable tables: click any th in a table's thead to sort by that column.
// Applies to all tables with a thead + tbody. Skip .kv tables (row-label
// definition lists, no thead) and tables explicitly opted out with .no-sort.
function initSortableTables() {
  document.querySelectorAll('table').forEach((table) => {
    if (table.classList.contains('kv') || table.classList.contains('no-sort')) return;
    const thead = table.querySelector('thead');
    const tbody = table.querySelector('tbody');
    if (!thead || !tbody) return;
    const ths = thead.querySelectorAll('th');
    if (!ths.length) return;
    ths.forEach((th, colIdx) => {
      if (th.classList.contains('no-sort')) return;
      th.classList.add('sortable');
      th.setAttribute('data-sort', 'none');
      th.addEventListener('click', () => sortByCol(table, th, colIdx));
    });
  });
}

function sortByCol(table, th, colIdx) {
  const thead = th.closest('thead');
  const ths = thead.querySelectorAll('th');
  const wasDir = th.getAttribute('data-sort');
  const dir = wasDir === 'asc' ? 'desc' : 'asc';

  ths.forEach((h) => { h.setAttribute('data-sort', 'none'); });
  th.setAttribute('data-sort', dir);

  const tbody = table.querySelector('tbody');
  const rows = Array.from(tbody.querySelectorAll(':scope > tr'));

  rows.sort((a, b) => {
    const aText = cellText(a, colIdx);
    const bText = cellText(b, colIdx);
    const aVal = parseVal(aText);
    const bVal = parseVal(bText);
    let cmp;
    if (typeof aVal === 'number' && typeof bVal === 'number') {
      cmp = aVal - bVal;
    } else {
      cmp = String(aVal).localeCompare(String(bVal), undefined, { numeric: true, sensitivity: 'base' });
    }
    return dir === 'asc' ? cmp : -cmp;
  });

  rows.forEach((r) => tbody.appendChild(r));
}

function cellText(row, colIdx) {
  const cell = row.cells[colIdx];
  return cell ? (cell.textContent || '').trim() : '';
}

// Try to parse dates and numbers so they sort correctly.
function parseVal(text) {
  if (text === '' || text === '—') return '';
  // ISO-ish date: "Jan 2, 2006 15:04" or similar — if it parses, use ms.
  const d = Date.parse(text);
  if (!isNaN(d)) return d;
  const n = Number(text.replace(/[,$%]/g, ''));
  if (!isNaN(n) && text !== '') return n;
  return text.toLowerCase();
}

document.addEventListener('DOMContentLoaded', () => {
  autoWrapTables();
  renderCpuChart();
  initTabs();
  initRoleTabs();
  initSortableTables();
  initThemeToggle();
  initNavToggle();
  initNavGroups();
  initAlertCharts();
  initAlertChannelKindToggle();
});

// Every <table class="data"> gets wrapped in a horizontally-scrollable
// container so wide tables never break the page layout. Idempotent: if
// a template already wrapped its table in .table-wrap we skip it.
function autoWrapTables() {
  document.querySelectorAll('table.data').forEach((t) => {
    const parent = t.parentElement;
    if (!parent || parent.classList.contains('table-wrap')) return;
    const wrap = document.createElement('div');
    wrap.className = 'table-wrap';
    parent.insertBefore(wrap, t);
    wrap.appendChild(t);
  });
}

// Alerts page: when the user picks a channel kind from the form,
// reveal only the matching <fieldset data-kind="…">.
function initAlertChannelKindToggle() {
  const sel = document.getElementById('ch-kind');
  if (!sel) return;
  const fields = document.querySelectorAll('fieldset[data-kind]');
  function apply() {
    const k = sel.value;
    fields.forEach((f) => { f.hidden = f.dataset.kind !== k; });
  }
  sel.addEventListener('change', apply);
  apply();
}

// Theme toggle button in the navbar.
function initThemeToggle() {
  const btn = document.getElementById('theme-toggle');
  if (!btn) return;
  btn.addEventListener('click', () => {
    const cur = document.documentElement.getAttribute('data-theme') || 'dark';
    applyTheme(cur === 'dark' ? 'light' : 'dark');
  });
}

// Mobile nav hamburger.
function initNavToggle() {
  const btn = document.querySelector('.navtoggle');
  const list = document.getElementById('primary-nav');
  if (!btn || !list) return;
  btn.addEventListener('click', () => {
    const open = list.classList.toggle('open');
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  });
}

// Grouped nav dropdowns — click-to-toggle as a keyboard/touch fallback
// when hover-to-open isn't available. Closes when clicking outside.
function initNavGroups() {
  const groups = document.querySelectorAll('.navgroup');
  if (!groups.length) return;
  groups.forEach((g) => {
    const btn = g.querySelector('.navgroup-label');
    if (!btn) return;
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const wasOpen = g.classList.contains('open');
      groups.forEach((other) => {
        other.classList.remove('open');
        const ob = other.querySelector('.navgroup-label');
        if (ob) ob.setAttribute('aria-expanded', 'false');
      });
      if (!wasOpen) {
        g.classList.add('open');
        btn.setAttribute('aria-expanded', 'true');
      }
    });
  });
  document.addEventListener('click', () => {
    groups.forEach((g) => {
      g.classList.remove('open');
      const b = g.querySelector('.navgroup-label');
      if (b) b.setAttribute('aria-expanded', 'false');
    });
  });
}

// Role-based tabs (button[role=tab] + section[role=tabpanel]), used on the
// agents page. Each tablist controls the panels at sibling level. Supports
// click and Left/Right/Home/End keyboard nav per WAI-ARIA APG.
function initRoleTabs() {
  document.querySelectorAll('[role="tablist"]').forEach((list) => {
    // Skip if inside a [data-tabs] container (handled by initTabs).
    if (list.closest('[data-tabs]')) return;
    const tabs = Array.from(list.querySelectorAll('[role="tab"]'));
    if (!tabs.length) return;
    const container = list.parentElement || document;
    const panels = Array.from(container.querySelectorAll('[role="tabpanel"]'));
    function activate(name) {
      tabs.forEach((t) => {
        const match = t.dataset.tab === name;
        t.classList.toggle('active', match);
        t.setAttribute('aria-selected', match ? 'true' : 'false');
        t.tabIndex = match ? 0 : -1;
      });
      panels.forEach((p) => {
        const match = p.id === 'panel-' + name;
        p.classList.toggle('active', match);
        p.hidden = !match;
      });
    }
    tabs.forEach((t, i) => {
      t.tabIndex = i === 0 ? 0 : -1;
      t.addEventListener('click', () => { activate(t.dataset.tab); t.focus(); });
      t.addEventListener('keydown', (e) => {
        let next = -1;
        switch (e.key) {
          case 'ArrowLeft':  next = (i - 1 + tabs.length) % tabs.length; break;
          case 'ArrowRight': next = (i + 1) % tabs.length; break;
          case 'Home':       next = 0; break;
          case 'End':        next = tabs.length - 1; break;
        }
        if (next >= 0) {
          e.preventDefault();
          const target = tabs[next];
          activate(target.dataset.tab);
          target.focus();
        }
      });
    });
  });
}

// Placeholder hook for charts moved off the alerts page inline script.
// Real implementation reads canvases populated by the alerts template.
function initAlertCharts() {
  document.querySelectorAll('canvas[data-alert-chart]').forEach((c) => {
    // No-op until/unless alerts.html opts into client-rendered charts.
    void c;
  });
}
