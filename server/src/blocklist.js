import crypto from 'node:crypto';

// Domain lists for the phone's DNS filter, grouped by category. Fetched and
// stored here so the child's phone never talks to the list host; each phone
// pulls one list built from the categories enabled for it plus its own
// custom blocks. Lists are hosts files from the Block List Project.
const BASE = process.env.BLOCKLIST_BASE || 'https://blocklistproject.github.io/Lists';
const REFRESH_MS = 24 * 60 * 60 * 1000;

export const CATEGORIES = {
  ads: { label: 'Adverts', files: ['ads'] },
  tracking: { label: 'Analytics and trackers', files: ['tracking'] },
  scams: { label: 'Scams and phishing', files: ['scam', 'phishing', 'ransomware'] },
  adult: { label: 'Adult', files: ['porn'] },
  gambling: { label: 'Gambling', files: ['gambling'] },
  drugs: { label: 'Drugs', files: ['drugs'] },
  piracy: { label: 'Piracy', files: ['piracy'] },
};
export const DEFAULT_CATS = ['ads', 'tracking', 'scams'];

const DOMAIN_RE = /^(?=.{3,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)+[a-z][a-z0-9-]{0,62}$/;

/** Hosts-file text to a list of lowercase domains. */
export function parseHosts(text) {
  const out = new Set();
  for (const line of text.split('\n')) {
    const m = /^(?:0\.0\.0\.0|127\.0\.0\.1)\s+([a-z0-9._-]+)/i.exec(line.trim());
    if (!m) continue;
    const d = m[1].toLowerCase();
    if (d.includes('.') && !/^\d+(\.\d+){3}$/.test(d)) out.add(d);
  }
  return [...out];
}

/** What a person typed (URL, host, *.host) to a bare lowercase domain, or null. */
export function normalizeDomain(raw) {
  let d = String(raw || '').trim().toLowerCase();
  d = d.replace(/^[a-z][a-z0-9+.-]*:\/\//, '').replace(/^\*\./, '').split(/[/?#]/)[0].replace(/:\d+$/, '').replace(/\.$/, '');
  return DOMAIN_RE.test(d) ? d : null;
}

async function fetchList(name) {
  const res = await fetch(`${BASE}/${name}.txt`, { signal: AbortSignal.timeout(120_000) });
  if (!res.ok) throw new Error(`${name}: HTTP ${res.status}`);
  return parseHosts(await res.text());
}

async function refreshCategory(db, cat) {
  const domains = new Set();
  for (const f of CATEGORIES[cat].files) for (const d of await fetchList(f)) domains.add(d);
  if (domains.size < 100) throw new Error(`${cat}: list too small, ignoring`);
  const ins = db.prepare('INSERT OR IGNORE INTO block_domains (domain, cat) VALUES (?, ?)');
  db.transaction(() => {
    db.prepare('DELETE FROM block_domains WHERE cat = ?').run(cat);
    for (const d of domains) ins.run(d, cat);
    db.prepare('INSERT OR REPLACE INTO block_meta (cat, updated, count) VALUES (?, ?, ?)').run(cat, Date.now(), domains.size);
  })();
}

async function refreshAll(db) {
  const meta = new Map(db.prepare('SELECT cat, updated FROM block_meta').all().map((r) => [r.cat, r.updated]));
  for (const cat of Object.keys(CATEGORIES)) {
    if (Date.now() - (meta.get(cat) || 0) < REFRESH_MS - 60_000) continue;
    try { await refreshCategory(db, cat); }
    catch (e) { console.error('blocklist refresh failed:', e.message); }
    await new Promise((r) => setImmediate(r)); // let requests through between big inserts
  }
}

export function startBlocklist(db) {
  refreshAll(db);
  setInterval(() => refreshAll(db), 60 * 60 * 1000).unref();
}

export const parseCats = (s) => String(s || '').split(',').filter((c) => CATEGORIES[c]);

/** Everything the phone needs to decide: switch, categories, own blocks, and a version that changes when any of it does. */
export function deviceBlockState(db, deviceId) {
  const d = db.prepare('SELECT adblock, block_cats FROM devices WHERE id = ?').get(deviceId);
  const enabled = !!d?.adblock;
  const cats = parseCats(d?.block_cats);
  const custom = db.prepare('SELECT domain FROM custom_blocks WHERE device_id = ? ORDER BY domain').all(deviceId).map((r) => r.domain);
  const active = enabled ? cats : [];
  const stamps = db.prepare('SELECT cat, updated FROM block_meta ORDER BY cat').all()
    .filter((m) => active.includes(m.cat)).map((m) => `${m.cat}:${m.updated}`);
  const version = crypto.createHash('sha1').update(JSON.stringify([stamps, custom])).digest('hex').slice(0, 16);
  return { enabled, cats, custom, active, version };
}

/** The list a phone downloads: its custom blocks plus every domain in its active categories. */
export function deviceBlocklist(db, state) {
  const out = new Set(state.custom);
  if (state.active.length) {
    const q = db.prepare(`SELECT DISTINCT domain FROM block_domains WHERE cat IN (${state.active.map(() => '?').join(',')})`);
    for (const r of q.iterate(...state.active)) out.add(r.domain);
  }
  return [...out].join('\n');
}

/** Category of a domain (or its closest listed parent), and whether it is on the custom list. */
export function makeClassifier(db, state) {
  const q = db.prepare('SELECT cat FROM block_domains WHERE domain = ?');
  const custom = new Set(state.custom);
  const order = Object.keys(CATEGORIES);
  return (domain) => {
    let d = domain;
    const cats = new Set();
    let customHit = false;
    while (true) {
      if (custom.has(d)) customHit = true;
      for (const r of q.all(d)) cats.add(r.cat);
      const dot = d.indexOf('.');
      if (dot < 0 || dot === d.length - 1) break;
      d = d.slice(dot + 1);
    }
    const found = [...cats].sort((a, b) => order.indexOf(a) - order.indexOf(b));
    return { category: found[0] || null, blocked: customHit || found.some((c) => state.active.includes(c)) };
  };
}
