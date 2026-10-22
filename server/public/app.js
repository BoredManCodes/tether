'use strict';

const root = document.getElementById('app');
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { v == null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { /* storage blocked */ } },
};
let token = store.get('tether.token');
let devices = [];
let deviceId = Number(store.get('tether.device')) || null;
let tab = 'overview';
let timers = [];

// Tabler themes through data-bs-theme; follow the OS setting.
const darkMq = matchMedia('(prefers-color-scheme: dark)');
const applyTheme = () => document.documentElement.setAttribute('data-bs-theme', darkMq.matches ? 'dark' : 'light');
applyTheme();
darkMq.addEventListener('change', applyTheme);

// All dynamic text goes through textContent. Phone data (SMS, notifications) is untrusted.
function h(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (k === 'class') e.className = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (v === true) e.setAttribute(k, '');
    else if (v !== false && v != null) e.setAttribute(k, v);
  }
  for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
  return e;
}

// Groups SMS/call rows by resolved contact name (falls back to the raw
// number when the phone had no matching contact). Rows arrive newest-first
// from the server, so the first row landing in each bucket is already that
// contact's most recent activity; sorting buckets on that keeps the whole
// list newest-contact-first without re-sorting anything.
function groupByContact(rows, idKey) {
  const map = new Map();
  for (const r of rows) {
    const label = r.contact_name || r[idKey] || 'Unknown';
    if (!map.has(label)) map.set(label, []);
    map.get(label).push(r);
  }
  return [...map.entries()].sort((a, b) => b[1][0].ts - a[1][0].ts);
}

function groupedList(rows, idKey, renderItem, emptyText) {
  if (!rows.length) return h('div', { class: 'card card-body text-secondary' }, emptyText);
  const groups = groupByContact(rows, idKey);
  return h('div', { class: 'd-flex flex-column gap-3' }, groups.map(([label, items]) =>
    h('details', { class: 'card card-body', open: groups.length === 1 },
      h('summary', {}, h('b', {}, label), h('span', { class: 'text-secondary' }, ` (${items.length})`)),
      items.map(renderItem))));
}

async function api(path, opts = {}) {
  const res = await fetch(path, {
    method: opts.method || 'GET',
    headers: { ...(token ? { authorization: `Bearer ${token}` } : {}), ...(opts.body ? { 'content-type': 'application/json' } : {}) },
    body: opts.body ? JSON.stringify(opts.body) : undefined,
  });
  if (res.status === 401 && token) { signOut(); throw new Error('signed out'); }
  if (res.status === 403) {
    const j = await res.clone().json().catch(() => ({}));
    if (j.error === 'password_change_required') { showChangePassword(); throw new Error('password change required'); }
  }
  if (opts.blob) return res.ok && res.status !== 204 ? res.blob() : null;
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
  return data;
}

function clearTimers() { timers.forEach(clearInterval); timers = []; }
function signOut() { token = null; store.set('tether.token', null); clearTimers(); showLogin(); }

const fmtTime = (ts) => (ts ? new Date(ts).toLocaleString() : 'never');
const ago = (ts) => {
  if (!ts) return 'never';
  const s = Math.round((Date.now() - ts) / 1000);
  if (s < 90) return `${s}s ago`;
  if (s < 5400) return `${Math.round(s / 60)} min ago`;
  if (s < 129600) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
};
const fmtDur = (ms) => { const m = Math.round(ms / 60000); return m >= 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m} min`; };

// ---------- Tabler building blocks ----------
const rowFlex = 'd-flex flex-wrap align-items-center gap-2';
const input = (attrs) => h('input', { ...attrs, class: `form-control ${attrs.class || ''}`.trim() });
const bars = (level) => `${level}/4`;
const stat = (value, label, cls = '') => h('div', { class: 'col-sm-6 col-lg-4' },
  h('div', { class: 'card card-body' }, h('div', { class: 'subheader' }, label), h('div', { class: `h1 mb-0 ${cls}`.trim() }, value)));
const formCard = (...kids) => h('div', { class: 'card mb-3' }, h('div', { class: 'card-body' }, ...kids));
const listItem = (cls, ...kids) => h('div', { class: `list-group-item ${cls}`.trim() }, ...kids);
const listCard = (items, empty) => h('div', { class: 'card' },
  items.length ? h('div', { class: 'list-group list-group-flush' }, items) : h('div', { class: 'card-body text-secondary' }, empty));
const badge = (text, cls = 'bg-secondary-lt') => h('span', { class: `badge ${cls}` }, text);

// ---------- auth screens ----------
function authCard(onsubmit, ...kids) {
  root.className = 'page page-center';
  root.replaceChildren(h('div', { class: 'container container-tight py-4' },
    h('form', { class: 'card card-md', onsubmit }, h('div', { class: 'card-body' }, ...kids))));
}
const field = (attrs) => h('div', { class: 'mb-3' }, input(attrs));
const errBox = (msg) => h('div', { class: 'text-danger mt-3', id: 'err' }, msg || '');
const authTitle = (text) => h('h2', { class: 'card-title text-center mb-4' }, text);

function showLogin(msg) {
  authCard(onLogin,
    authTitle('Tether'),
    field({ name: 'email', type: 'email', placeholder: 'Email', autocomplete: 'username', required: true }),
    field({ name: 'password', type: 'password', placeholder: 'Password', autocomplete: 'current-password', required: true }),
    h('button', { class: 'btn btn-primary w-100' }, 'Sign in'),
    errBox(msg));
}

async function onLogin(ev) {
  ev.preventDefault();
  const f = new FormData(ev.target);
  try {
    const r = await api('/api/auth/login', { method: 'POST', body: { email: f.get('email'), password: f.get('password') } });
    if (r.need_code) { showTwoFactor(r.pending_token); return; }
    token = r.token; store.set('tether.token', token);
    window.__pw = f.get('password');
    r.must_change ? showChangePassword() : start();
  } catch (e) { document.getElementById('err').textContent = e.message === 'invalid_credentials' ? 'Wrong email or password.' : e.message; }
}

function showTwoFactor(pendingToken) {
  authCard((ev) => onVerifyCode(ev, pendingToken),
    authTitle('Enter your code'),
    h('p', { class: 'text-secondary' }, 'A 6-digit code was emailed to you. It expires in 10 minutes.'),
    field({ name: 'code', inputmode: 'numeric', pattern: '[0-9]{6}', maxlength: 6, placeholder: '000000', autocomplete: 'one-time-code', required: true }),
    h('button', { class: 'btn btn-primary w-100' }, 'Verify'),
    errBox());
}

async function onVerifyCode(ev, pendingToken) {
  ev.preventDefault();
  const f = new FormData(ev.target);
  try {
    const r = await api('/api/auth/2fa/verify', { method: 'POST', body: { pending_token: pendingToken, code: f.get('code') } });
    token = r.token; store.set('tether.token', token);
    r.must_change ? showChangePassword() : start();
  } catch (e) {
    document.getElementById('err').textContent = { invalid_code: 'Wrong code.', expired: 'Code expired, sign in again.', too_many_attempts: 'Too many attempts, sign in again.' }[e.message] || e.message;
  }
}

function showChangePassword() {
  authCard(onChange,
    authTitle('Set a new password'),
    h('p', { class: 'text-secondary' }, 'The starter password has to be replaced before anything else works. Minimum 12 characters.'),
    field({ name: 'current', type: 'password', placeholder: 'Current password', autocomplete: 'current-password', value: window.__pw || '', required: true }),
    field({ name: 'next', type: 'password', placeholder: 'New password', autocomplete: 'new-password', minlength: 12, required: true }),
    h('button', { class: 'btn btn-primary w-100' }, 'Change password'),
    errBox());
}

async function onChange(ev) {
  ev.preventDefault();
  const f = new FormData(ev.target);
  try {
    const r = await api('/api/auth/change-password', { method: 'POST', body: { current: f.get('current'), next: f.get('next') } });
    token = r.token; store.set('tether.token', token); window.__pw = null;
    start();
  } catch (e) { document.getElementById('err').textContent = e.message === 'password_min_12_chars' ? 'Use at least 12 characters.' : e.message; }
}

// ---------- shell ----------
const TABS = [['overview', 'Overview'], ['alerts', 'Alerts'], ['messages', 'Messages'], ['calls', 'Calls'], ['notifications', 'Notifications'],
  ['search', 'Search'], ['usage', 'Screen time'], ['photos', 'Photos'], ['schedule', 'Schedule'],
  ['apprules', 'Apps'], ['sites', 'Sites'], ['geofences', 'Places'], ['settings', 'Settings']];

async function start() {
  try { devices = await api('/api/devices'); } catch { return; }
  if (!devices.find((d) => d.id === deviceId)) deviceId = devices[0]?.id ?? null;
  renderShell();
}

function renderShell() {
  clearTimers();
  root.className = 'page';
  const sel = h('select', { class: 'form-select w-auto', onchange: (e) => { deviceId = Number(e.target.value); store.set('tether.device', deviceId); renderShell(); } },
    devices.map((d) => h('option', { value: d.id, selected: d.id === deviceId }, d.name)));
  const body = h('div', { id: 'body' });
  root.replaceChildren(
    h('header', { class: 'navbar d-print-none' }, h('div', { class: 'container-xl' },
      h('h1', { class: 'navbar-brand m-0' }, 'Tether'),
      h('div', { class: 'ms-auto d-flex gap-2' }, devices.length ? sel : null, h('button', { class: 'btn', onclick: signOut }, 'Sign out')))),
    h('div', { class: 'page-wrapper' }, h('div', { class: 'container-xl pb-5' },
      h('ul', { class: 'nav nav-pills tabs-scroll mb-3' }, TABS.map(([id, label]) =>
        h('li', { class: 'nav-item' }, h('a', {
          href: '#', class: `nav-link${id === tab ? ' active' : ''}`, 'aria-current': id === tab ? 'page' : null,
          onclick: (e) => { e.preventDefault(); tab = id; renderShell(); },
        }, label)))),
      body)));
  if (!deviceId) {
    body.append(h('div', { class: 'card card-body' }, 'No phone enrolled yet. Sign in to the Tether app on the phone with this account.'));
    return;
  }
  const device = devices.find((d) => d.id === deviceId);
  VIEWS[tab](body, device).catch((e) => body.append(h('div', { class: 'alert alert-danger' }, e.message)));
}

// ---------- views ----------
async function sendCommand(type, args) {
  await api(`/api/devices/${deviceId}/commands`, { method: 'POST', body: { type, args } });
}

const VIEWS = {
  async overview(body, d) {
    const [locs] = await Promise.all([api(`/api/devices/${deviceId}/locations?limit=50`)]);
    const stale = !d.last_seen || Date.now() - d.last_seen > 15 * 60 * 1000;
    const actionBtn = (label, type, cls = '') => h('button', { class: `btn ${cls}`.trim(), onclick: async (e) => {
      e.target.disabled = true; try { await sendCommand(type); e.target.textContent = `${label} sent`; } catch (x) { e.target.textContent = x.message; }
      setTimeout(() => { e.target.textContent = label; e.target.disabled = false; }, 2500);
    } }, label);
    const frame = h('img', { id: 'screen', alt: 'Live screen', hidden: true });
    let live = false;
    const liveBtn = h('button', { class: 'btn', onclick: async () => {
      live = !live;
      liveBtn.textContent = live ? 'Stop live screen' : 'Live screen';
      await sendCommand(live ? 'SCREEN_START' : 'SCREEN_STOP');
      frame.hidden = !live;
      saveBtn.hidden = !live;
    } }, 'Live screen');
    const saveBtn = h('button', { class: 'btn', hidden: true, onclick: () => {
      if (!frame.src) return;
      const a = h('a', { href: frame.src, download: `screen-${Date.now()}.jpg` });
      a.click();
    } }, 'Save image');
    body.append(
      h('div', { class: 'row row-cards mb-3' },
        stat(d.battery_pct == null ? '-' : `${d.battery_pct}%`, d.charging ? 'Battery, charging' : 'Battery'),
        stat(ago(d.last_seen), 'Last check-in', stale ? 'text-warning' : ''),
        stat(d.locked ? 'Locked' : 'Open', d.locked ? 'Only phone and SMS' : 'All apps available'),
        stat(d.cell_level == null ? (d.cell_type || '-') : `${d.cell_type ? d.cell_type + ' ' : ''}${bars(d.cell_level)}`,
          d.cell_dbm == null ? 'Mobile signal' : `Mobile signal, ${d.cell_dbm} dBm`),
        stat(d.wifi_connected ? (d.wifi_ssid || 'Connected') : (d.wifi_connected == null ? '-' : 'Not on Wi-Fi'),
          d.wifi_connected && d.wifi_level != null ? `Wi-Fi ${bars(d.wifi_level)}${d.wifi_rssi == null ? '' : `, ${d.wifi_rssi} dBm`}` : 'Wi-Fi')),
      h('div', { class: `card card-body ${rowFlex} flex-row mb-3` }, actionBtn('Locate now', 'LOCATE'), actionBtn('Lock to phone and SMS', 'LOCK', 'btn-outline-danger'),
        actionBtn('Unlock', 'UNLOCK'), actionBtn('Ring', 'RING'), liveBtn, saveBtn),
      h('div', { class: 'mb-3' }, frame));
    const mapDiv = h('div', { id: 'map', class: 'card' });
    body.append(mapDiv);
    if (typeof L !== 'undefined' && locs.length) {
      const map = L.map(mapDiv).setView([locs[0].lat, locs[0].lon], 15);
      L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}', { maxZoom: 19, attribution: '&copy; Esri' }).addTo(map);
      L.circle([locs[0].lat, locs[0].lon], { radius: locs[0].accuracy || 30 }).addTo(map);
      L.marker([locs[0].lat, locs[0].lon]).addTo(map).bindPopup(fmtTime(locs[0].ts));
      L.polyline(locs.map((l) => [l.lat, l.lon]), { weight: 3 }).addTo(map);
    } else mapDiv.append(h('p', { class: 'text-secondary m-3' }, 'No locations received yet.'));
    timers.push(setInterval(async () => {
      if (!live) return;
      const blob = await api(`/api/devices/${deviceId}/screen`, { blob: true }).catch(() => null);
      if (blob) { const old = frame.src; frame.src = URL.createObjectURL(blob); if (old.startsWith('blob:')) URL.revokeObjectURL(old); }
    }, 1000));
  },

  async messages(body) {
    const rows = await api(`/api/devices/${deviceId}/sms?limit=200`);
    body.append(groupedList(rows, 'address', (m) =>
      h('div', { class: `chat-row ${m.direction === 'out' ? 'out' : 'in'}` },
        h('div', { class: `bubble ${m.direction === 'out' ? 'out' : 'in'}` },
          h('div', { class: 'msg' }, m.body),
          h('span', { class: 'time' }, fmtTime(m.ts)))), 'No messages yet.'));
  },

  async calls(body) {
    const rows = await api(`/api/devices/${deviceId}/calls?limit=200`);
    const sorted = [...rows].sort((a, b) => b.ts - a.ts);
    body.append(listCard(sorted.map((c) =>
      listItem('', h('div', { class: rowFlex },
        h('b', {}, c.contact_name || c.number || 'Unknown'),
        badge(c.direction, c.direction === 'out' ? 'bg-green-lt' : 'bg-secondary-lt')),
        h('div', { class: 'text-secondary' }, `${fmtTime(c.ts)} · ${fmtDur(c.duration_s * 1000)}`))), 'No calls yet.'));
  },

  async notifications(body) {
    const rows = await api(`/api/devices/${deviceId}/notifications?limit=200`);
    body.append(listCard(rows.map((n) =>
      listItem('', h('div', { class: rowFlex }, h('b', {}, n.title || '(no title)'), h('span', { class: 'text-secondary' }, `${n.app_name || n.package}, ${fmtTime(n.ts)}`)),
        h('div', { class: 'msg' }, n.text))), 'No notifications yet.'));
  },

  async usage(body) {
    const day = new Date().toISOString().slice(0, 10);
    const dayInput = input({ type: 'date', value: day, class: 'w-auto' });
    const out = h('div', { class: 'card card-body d-flex flex-column gap-3' });
    const load = async () => {
      const rows = await api(`/api/devices/${deviceId}/usage?day=${dayInput.value}`);
      const max = Math.max(1, ...rows.map((r) => r.foreground_ms));
      const total = rows.reduce((a, r) => a + r.foreground_ms, 0);
      out.replaceChildren(h('div', {}, h('b', {}, `Total ${fmtDur(total)}`)), ...rows.map((r) =>
        h('div', {}, h('div', { class: 'd-flex justify-content-between' }, h('span', {}, r.app_name || r.package), h('span', { class: 'text-secondary' }, fmtDur(r.foreground_ms))),
          h('div', { class: 'progress progress-sm mt-1' }, h('div', { class: 'progress-bar', style: `width:${Math.round((r.foreground_ms / max) * 100)}%` })))));
      if (!rows.length) out.append('No usage recorded for this day.');
    };
    dayInput.addEventListener('change', load);
    body.append(h('div', { class: 'card card-body mb-3' }, h('div', { class: rowFlex }, 'Day', dayInput,
      h('span', { class: 'text-secondary' }, 'Dates are UTC.'))), out);
    await load();
  },

  async photos(body) {
    const rows = await api(`/api/devices/${deviceId}/media?limit=120`);
    const grid = h('div', { class: 'photo-grid' });
    body.append(h('div', { class: 'card card-body d-flex flex-row gap-2 mb-3' },
      h('button', { class: 'btn', onclick: async (e) => { await sendCommand('SCAN_MEDIA'); e.target.textContent = 'Requested'; } }, 'Fetch new photos'),
      h('button', { class: 'btn', onclick: async (e) => { await sendCommand('SCREENSHOT'); e.target.textContent = 'Requested'; } }, 'Take screenshot')),
    h('div', { class: 'card card-body' }, grid));
    if (!rows.length) grid.replaceWith('No photos uploaded yet.');
    for (const m of rows) {
      const img = h('img', { alt: `${m.kind} ${fmtTime(m.ts)}`, title: `${m.kind}, ${fmtTime(m.ts)}`, loading: 'lazy' });
      img.addEventListener('click', () => img.src && window.open(img.src, '_blank', 'noopener'));
      grid.append(img);
      api(`/api/media/${m.id}`, { blob: true }).then((b) => { if (b) img.src = URL.createObjectURL(b); });
    }
  },

  async schedule(body) {
    const list = await api(`/api/devices/${deviceId}/schedules`);
    const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    const toHM = (m) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
    const fromHM = (s) => { const [a, b] = s.split(':').map(Number); return a * 60 + b; };
    const rows = h('div', { class: 'd-flex flex-column gap-3' });
    const addRow = (s = { days: '12345', start_min: 21 * 60, end_min: 7 * 60 }) => {
      const checks = DAYS.map((n, i) => h('label', { class: 'form-check form-check-inline m-0' },
        h('input', { class: 'form-check-input', type: 'checkbox', checked: s.days.includes(String(i)), 'data-d': i }), h('span', { class: 'form-check-label' }, n)));
      const start = input({ type: 'time', value: toHM(s.start_min), class: 'w-auto' });
      const end = input({ type: 'time', value: toHM(s.end_min), class: 'w-auto' });
      const row = h('div', { class: rowFlex }, checks, 'Locked from', start, 'to', end, h('button', { class: 'btn btn-sm', onclick: () => row.remove() }, 'Remove'));
      row.read = () => ({ days: checks.map((c) => c.firstChild).filter((c) => c.checked).map((c) => c.dataset.d).join(''), start_min: fromHM(start.value), end_min: fromHM(end.value) });
      rows.append(row);
    };
    list.forEach(addRow);
    const status = h('span', { class: 'text-secondary' });
    body.append(formCard(h('p', { class: 'text-secondary' }, 'During these windows the phone allows only Phone and SMS. A window that ends earlier than it starts runs overnight into the next day.'),
      rows, h('div', { class: `${rowFlex} mt-3` }, h('button', { class: 'btn', onclick: () => addRow() }, 'Add window'),
        h('button', { class: 'btn btn-primary', onclick: async () => {
          const payload = [...rows.children].map((r) => r.read()).filter((s) => s.days);
          try { await api(`/api/devices/${deviceId}/schedules`, { method: 'PUT', body: payload }); status.textContent = 'Saved.'; } catch (e) { status.textContent = e.message; }
        } }, 'Save'), status)));
  },

  async alerts(body) {
    const rows = await api(`/api/devices/${deviceId}/alerts?limit=200`);
    const ICON = { keyword: 'Keyword', geofence: 'Location', tamper: 'Tamper', offline: 'Offline' };
    body.append(listCard(rows.map((a) =>
      listItem(a.read ? '' : 'alert-unread', h('div', { class: rowFlex },
        badge(ICON[a.type] || a.type), h('span', { class: 'text-secondary' }, fmtTime(a.ts)),
        !a.read ? h('button', { class: 'btn btn-sm ms-auto', onclick: async (e) => { await api(`/api/devices/${deviceId}/alerts/${a.id}/read`, { method: 'POST' }); e.target.closest('.list-group-item').classList.remove('alert-unread'); e.target.remove(); } }, 'Dismiss') : null),
        h('div', { class: 'msg' }, a.message))), 'No alerts yet.'));
  },

  async search(body) {
    const out = h('div', { class: 'card card-body d-flex flex-column gap-3' });
    const q = input({ type: 'search', placeholder: 'Search messages and notifications…' });
    const run = async () => {
      if (q.value.trim().length < 2) { out.replaceChildren('Type at least 2 characters.'); return; }
      const rows = await api(`/api/devices/${deviceId}/search?q=${encodeURIComponent(q.value.trim())}`).catch((e) => { out.replaceChildren(e.message); return []; });
      out.replaceChildren(...(rows.length ? rows.map((r) =>
        h('div', {}, h('div', { class: rowFlex }, badge(r.kind), h('b', {}, r.contact_name || r.who || 'Unknown'), h('span', { class: 'text-secondary' }, fmtTime(r.ts))),
          h('div', { class: 'msg' }, r.text))) : ['No matches.']));
    };
    let debounce;
    q.addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(run, 300); });
    body.append(h('div', { class: 'card card-body mb-3' }, q), out);
  },

  async apprules(body) {
    const [rules, apps, usage] = await Promise.all([
      api(`/api/devices/${deviceId}/apprules`),
      api(`/api/devices/${deviceId}/apps`).catch(() => []),
      api(`/api/devices/${deviceId}/usage`).catch(() => []),
    ]);
    const byPkg = new Map();
    for (const a of apps) byPkg.set(a.package, { package: a.package, name: a.app_name || a.package, system: !!a.system, installed: true });
    for (const u of usage) if (!byPkg.has(u.package)) byPkg.set(u.package, { package: u.package, name: u.app_name || u.package, system: false, installed: false });
    const ruleOf = new Map(rules.map((r) => [r.package, r]));
    // A rule can outlive its app (that is how block + uninstall catches a reinstall), so keep those rows visible.
    for (const r of rules) if (!byPkg.has(r.package)) byPkg.set(r.package, { package: r.package, name: r.app_name || r.package, system: false, installed: false });
    const modeOf = (r) => (!r ? 'allow' : r.uninstall ? 'uninstall' : r.blocked ? 'block' : r.daily_limit_min ? 'limit' : 'allow');

    const list = [...byPkg.values()].map((a) => ({ ...a, rule: ruleOf.get(a.package) }));
    list.sort((x, y) => (modeOf(y.rule) !== 'allow') - (modeOf(x.rule) !== 'allow') || x.name.toLowerCase().localeCompare(y.name.toLowerCase()));

    const rows = h('div', { class: 'list-group list-group-flush' });
    const entries = list.map((a) => {
      const mode = h('select', { class: 'form-select w-auto' },
        h('option', { value: 'allow' }, 'Allowed'),
        h('option', { value: 'limit' }, 'Daily limit'),
        h('option', { value: 'block' }, 'Block'),
        h('option', { value: 'uninstall', disabled: a.system }, 'Block + uninstall'));
      mode.value = modeOf(a.rule);
      const limit = input({ type: 'number', min: 1, placeholder: 'min/day', value: a.rule?.daily_limit_min ?? '', class: 'w-auto', style: 'width:110px' });
      const sync = () => { limit.hidden = mode.value !== 'limit'; };
      mode.addEventListener('change', sync); sync();
      const tag = a.system ? badge('system') : (!a.installed ? badge('not installed', 'bg-yellow-lt') : null);
      const row = listItem('', h('div', { class: rowFlex },
        h('div', { class: 'me-auto' }, h('b', {}, a.name), tag ? [' ', tag] : null, h('div', { class: 'text-secondary small' }, a.package)),
        limit, mode));
      row.dataset.search = `${a.name} ${a.package}`.toLowerCase();
      row.read = () => {
        const m = mode.value;
        if (m === 'allow') return null;
        return { package: a.package, app_name: a.name, blocked: m === 'block' || m === 'uninstall', uninstall: m === 'uninstall',
          daily_limit_min: m === 'limit' ? Number(limit.value) || null : null };
      };
      rows.append(row);
      return row;
    });
    const search = input({ type: 'search', placeholder: 'Filter apps…', class: 'w-auto' });
    search.addEventListener('input', () => { const q = search.value.trim().toLowerCase(); for (const r of entries) r.hidden = !!q && !r.dataset.search.includes(q); });
    const status = h('span', { class: 'text-secondary' });
    body.append(formCard(
      h('p', { class: 'text-secondary' }, 'Block suspends the app. Block + uninstall also removes it, and removes it again if it is ever reinstalled. The rule stays until you set the app back to Allowed. System apps can be blocked but not uninstalled.'),
      !apps.length ? h('p', { class: 'text-warning' }, 'The phone has not sent its app list yet. It arrives with the next check-in after the updated Tether app is installed.') : null,
      h('div', { class: `${rowFlex} mb-3` }, search, h('button', { class: 'btn btn-primary', onclick: async () => {
        const payload = entries.map((r) => r.read()).filter(Boolean);
        try { await api(`/api/devices/${deviceId}/apprules`, { method: 'PUT', body: payload }); status.textContent = `Saved ${payload.length} rule${payload.length === 1 ? '' : 's'}.`; } catch (e) { status.textContent = e.message; }
      } }, 'Save'), status)),
      h('div', { class: 'card' }, rows));
  },

  async sites(body) {
    const rows = await api(`/api/devices/${deviceId}/sites`);
    const ab = await api(`/api/devices/${deviceId}/adblock`);
    const labels = Object.fromEntries(ab.categories.map((c) => [c.key, c.label]));
    const abStatus = h('span', { class: 'text-secondary' });
    const save = async () => {
      try { await api(`/api/devices/${deviceId}/adblock`, { method: 'PUT', body: { enabled: ab.enabled, cats: ab.cats } }); abStatus.textContent = 'Saved. The phone picks it up at its next sync.'; }
      catch (e) { abStatus.textContent = e.message; }
    };
    const master = h('input', { type: 'checkbox', class: 'form-check-input', checked: ab.enabled });
    master.addEventListener('change', () => { ab.enabled = master.checked; save(); });
    const catBoxes = ab.categories.map((c) => {
      const box = h('input', { type: 'checkbox', class: 'form-check-input', checked: ab.cats.includes(c.key) });
      box.addEventListener('change', () => { ab.cats = box.checked ? [...ab.cats, c.key] : ab.cats.filter((k) => k !== c.key); save(); });
      return h('label', { class: 'd-flex align-items-center gap-2' }, box, c.label,
        c.count != null ? h('span', { class: 'text-secondary' }, `${c.count.toLocaleString()} domains`) : h('span', { class: 'text-secondary' }, 'downloading'));
    });

    const customList = h('div', { class: 'd-flex flex-column gap-2 mt-3' });
    const siteList = h('div');
    const blockedNow = (r) => r.blocked || ab.custom.some((d) => r.domain === d || r.domain.endsWith(`.${d}`));
    const renderCustom = () => {
      customList.replaceChildren(...ab.custom.map((d) => h('div', { class: rowFlex }, h('b', {}, d), badge('subdomains too', 'bg-secondary-lt'),
        h('button', { class: 'btn btn-sm ms-auto', onclick: async () => {
          await api(`/api/devices/${deviceId}/blocked-domains/${encodeURIComponent(d)}`, { method: 'DELETE' });
          ab.custom = ab.custom.filter((x) => x !== d); renderCustom(); renderSites();
        } }, 'Unblock'))));
    };
    const addCustom = async (raw) => {
      const r = await api(`/api/devices/${deviceId}/blocked-domains`, { method: 'POST', body: { domain: raw } });
      if (!ab.custom.includes(r.domain)) ab.custom = [...ab.custom, r.domain].sort();
      renderCustom(); renderSites();
      return r.domain;
    };
    const renderSites = () => siteList.replaceChildren(listCard(rows.map((r) => listItem('', h('div', { class: rowFlex },
      h('b', {}, r.domain), badge(`${r.hits}×`),
      r.category ? badge(labels[r.category] || r.category, 'bg-purple-lt') : null,
      r.app_name ? badge(r.app_name, 'bg-blue-lt') : null,
      blockedNow(r) ? badge('Blocked', 'bg-red-lt') : h('button', { class: 'btn btn-sm', onclick: async (e) => {
        try { await addCustom(r.domain); } catch (err) { e.target.textContent = err.message; }
      } }, 'Block'),
      h('span', { class: 'text-secondary ms-auto' }, fmtTime(r.last_ts))))), 'No lookups recorded yet.'));

    const domainInput = input({ placeholder: 'Domain to block, e.g. example.com', class: 'w-auto', style: 'min-width:260px' });
    const addStatus = h('span', { class: 'text-secondary' });
    const doAdd = async () => {
      try { const d = await addCustom(domainInput.value); domainInput.value = ''; addStatus.textContent = `Blocked ${d} and its subdomains.`; }
      catch (e) { addStatus.textContent = e.message === 'invalid_domain' ? 'Not a valid domain.' : e.message; }
    };
    domainInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') doAdd(); });

    body.append(formCard(
      h('label', { class: rowFlex }, master, h('b', {}, 'Block by category'), abStatus),
      h('p', { class: 'text-secondary mt-2' }, 'DNS filter: lookups for listed domains get no answer. It cannot remove ads served from the same domain as the content, such as YouTube video ads. Lists refresh daily.'),
      h('div', { class: 'd-flex flex-column gap-2' }, catBoxes),
      h('hr'),
      h('b', {}, 'Block a domain'),
      h('div', { class: `${rowFlex} mt-2` }, domainInput, h('button', { class: 'btn btn-primary', onclick: doAdd }, 'Block'), addStatus),
      customList));
    body.append(h('p', { class: 'text-secondary' }, 'Domains the phone looked up in the last 24 hours, from the Tether VPN. Shows the site, not the page. Lookups from apps with their own DNS or encrypted DNS do not appear.'), siteList);
    renderCustom();
    renderSites();
  },

  async geofences(body) {
    const list = await api(`/api/devices/${deviceId}/geofences`);
    const locs = await api(`/api/devices/${deviceId}/locations?limit=1`).catch(() => []);
    const rows = h('div', { class: 'd-flex flex-column gap-2' });
    const render = (items) => {
      rows.replaceChildren(...items.map((g) => h('div', { class: rowFlex },
        h('b', {}, g.name), h('span', { class: 'text-secondary' }, `${g.lat.toFixed(5)}, ${g.lon.toFixed(5)} · ${g.radius_m} m`),
        h('button', { class: 'btn btn-sm', onclick: async () => { await api(`/api/devices/${deviceId}/geofences/${g.id}`, { method: 'DELETE' }); render(items.filter((x) => x.id !== g.id)); } }, 'Remove'))));
      if (!items.length) rows.append('No places set up yet.');
    };
    render(list);
    const name = input({ placeholder: 'Name, e.g. Home', class: 'w-auto' });
    const lat = input({ type: 'number', step: 'any', placeholder: 'Latitude', value: locs[0]?.lat ?? '', class: 'w-auto' });
    const lon = input({ type: 'number', step: 'any', placeholder: 'Longitude', value: locs[0]?.lon ?? '', class: 'w-auto' });
    const radius = input({ type: 'number', value: 150, class: 'w-auto', style: 'width:100px' });
    const status = h('span', { class: 'text-secondary' });
    body.append(formCard(h('p', { class: 'text-secondary' }, 'Get an alert whenever the phone enters or leaves a place. Latitude/longitude default to the last known location.'),
      rows, h('div', { class: `${rowFlex} mt-3` }, name, lat, lon, 'radius (m)', radius,
        h('button', { class: 'btn btn-primary', onclick: async () => {
          try {
            const g = await api(`/api/devices/${deviceId}/geofences`, { method: 'POST', body: { name: name.value, lat: Number(lat.value), lon: Number(lon.value), radius_m: Number(radius.value) } });
            render([...list, { id: g.id, name: name.value, lat: Number(lat.value), lon: Number(lon.value), radius_m: Number(radius.value) }]);
            name.value = '';
          } catch (e) { status.textContent = e.message; }
        } }, 'Add place'), status)));
  },

  async settings(body) {
    const me = await api('/api/auth/me');
    const keywords = await api('/api/keywords');
    const emailInput = input({ type: 'email', placeholder: 'Leave blank to use your login email', value: me.alert_email || '', class: 'w-auto' });
    const emailStatus = h('span', { class: 'text-secondary' });
    const mfaBtn = h('button', { class: 'btn', onclick: async () => {
      if (me.mfa_enabled) {
        const password = prompt('Enter your password to turn off 2FA:');
        if (!password) return;
        try { await api('/api/auth/2fa/disable', { method: 'POST', body: { password } }); mfaBtn.textContent = 'Turn on email codes at sign-in'; me.mfa_enabled = false; }
        catch (e) { alert(e.message); }
      } else {
        await api('/api/auth/2fa/enable', { method: 'POST' });
        mfaBtn.textContent = 'Turn off email codes at sign-in'; me.mfa_enabled = true;
      }
    } }, me.mfa_enabled ? 'Turn off email codes at sign-in' : 'Turn on email codes at sign-in');
    const kwList = h('div', { class: 'd-flex flex-column gap-2' });
    const renderKw = (items) => {
      kwList.replaceChildren(...items.map((k) => h('div', { class: rowFlex }, k.is_regex ? badge('Regex') : '', k.word,
        h('button', { class: 'btn btn-sm', onclick: async () => { await api(`/api/keywords/${k.id}`, { method: 'DELETE' }); renderKw(items.filter((x) => x.id !== k.id)); } }, 'Remove'))));
      if (!items.length) kwList.append('No keywords set. Add words or regex patterns below to get an alert whenever they show up in a message, notification or on screen.');
    };
    renderKw(keywords);
    const kwInput = input({ placeholder: 'Word, phrase or regex', class: 'w-auto' });
    const kwRegex = h('input', { type: 'checkbox', class: 'form-check-input' });
    body.append(
      formCard(h('b', {}, 'Alert email'), h('p', { class: 'text-secondary' }, 'Where alerts and sign-in codes are sent.'),
        h('div', { class: rowFlex }, emailInput, h('button', { class: 'btn btn-primary', onclick: async () => {
          try { await api('/api/auth/alert-email', { method: 'POST', body: { email: emailInput.value || null } }); emailStatus.textContent = 'Saved.'; } catch (e) { emailStatus.textContent = e.message; }
        } }, 'Save'), emailStatus)),
      formCard(h('b', {}, 'Two-factor sign-in'), h('p', { class: 'text-secondary' }, 'Email a 6-digit code every time someone signs in.'), mfaBtn),
      formCard(h('b', {}, 'Keyword alerts'), h('p', { class: 'text-secondary' }, 'Applies to every phone on this account: SMS, app notifications and on-screen text. Regex is case-insensitive and must match what Java and JavaScript both accept.'),
        kwList, h('div', { class: `${rowFlex} mt-3` }, kwInput, h('label', { class: 'form-check m-0' }, kwRegex, ' Regex'), h('button', { class: 'btn btn-primary', onclick: async () => {
          const word = kwInput.value.trim();
          if (!word) return;
          try { const k = await api('/api/keywords', { method: 'POST', body: { word, regex: kwRegex.checked } }); renderKw([...keywords, k]); keywords.push(k); kwInput.value = ''; } catch (e) { alert(e.message); }
        } }, 'Add'))));
  },
};

token ? start() : showLogin();
