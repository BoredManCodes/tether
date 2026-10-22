import Fastify from 'fastify';
import multipart from '@fastify/multipart';
import rateLimit from '@fastify/rate-limit';
import fastifyStatic from '@fastify/static';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { openDb } from './db.js';
import {
  registerAuth, seedAdmin, hashPassword, verifyPassword, createSession, newToken, sha256,
  createLoginOtp, verifyLoginOtp,
} from './auth.js';
import { sendEmail } from './email.js';
import {
  raiseAlert, checkKeywords, checkGeofences, checkTamper, offlineWatchdog, clearOfflineAlert,
} from './alerts.js';
import {
  startBlocklist, deviceBlockState, deviceBlocklist, makeClassifier, normalizeDomain, parseCats, CATEGORIES,
} from './blocklist.js';

const DATA_DIR = process.env.TETHER_DATA || path.resolve('data');
const MEDIA_DIR = path.join(DATA_DIR, 'media');
const PORT = Number(process.env.PORT || 3150);
const HOST = process.env.HOST || '127.0.0.1';
const LONG_POLL_MS = 25000;
const COMMAND_TYPES = new Set([
  'LOCATE', 'LOCK', 'UNLOCK', 'RING', 'SCREEN_START', 'SCREEN_STOP', 'SCAN_MEDIA', 'SCREENSHOT',
]);

fs.mkdirSync(MEDIA_DIR, { recursive: true });
const db = openDb(DATA_DIR);
startBlocklist(db);
if (seedAdmin(db, process.env.ADMIN_EMAIL, process.env.ADMIN_PASSWORD)) {
  console.log(`seeded admin ${process.env.ADMIN_EMAIL}; password change required at first login`);
}

const app = Fastify({ logger: true, bodyLimit: 5 * 1024 * 1024, trustProxy: true });
await app.register(rateLimit, { global: false });
await app.register(multipart, { limits: { fileSize: 50 * 1024 * 1024, files: 1 } });
registerAuth(app, db);

const here = path.dirname(fileURLToPath(import.meta.url));
await app.register(fastifyStatic, { root: path.join(here, '..', 'public') });
app.addContentTypeParser('image/jpeg', { parseAs: 'buffer', bodyLimit: 4 * 1024 * 1024 }, (_r, b, d) => d(null, b));

const CSP = [
  "default-src 'self'",
  "script-src 'self' https://cdnjs.cloudflare.com",
  "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com",
  "img-src 'self' data: blob: https://server.arcgisonline.com https://cdnjs.cloudflare.com",
  "connect-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'",
].join('; ');
app.addHook('onSend', async (_req, reply) => {
  reply.header('Content-Security-Policy', CSP);
  reply.header('X-Content-Type-Options', 'nosniff');
  reply.header('Referrer-Policy', 'no-referrer');
});

const waiters = new Map(); // deviceId -> Set of wake functions
const latestFrame = new Map(); // deviceId -> { ts, buf }

function wake(deviceId) {
  for (const fn of waiters.get(deviceId) || []) fn();
}

// The phone grabs a screenshot the moment a keyword shows up (see KeywordWatch.kt)
// and uploads it as media kind=screenshot stamped with the message time. The
// message itself syncs later, so match the nearest screenshot within 30s. Retries
// briefly in case the upload is still in flight. Resolves null if there is none.
const SHOT_MATCH_MS = 30 * 1000;

async function shotNear(deviceId, ts) {
  const q = db.prepare(
    `SELECT file FROM media WHERE device_id = ? AND kind = 'screenshot' AND ts BETWEEN ? AND ?
     ORDER BY abs(ts - ?) LIMIT 1`);
  for (let i = 0; i < 3; i++) {
    const row = q.get(deviceId, ts - SHOT_MATCH_MS, ts + SHOT_MATCH_MS, ts);
    if (row) {
      try { return fs.readFileSync(path.join(MEDIA_DIR, row.file)); } catch { return null; }
    }
    await new Promise((r) => setTimeout(r, 3000));
  }
  return null;
}

const APP_NAME_SQL ='coalesce(n.app_name, ia.app_name, n.package)';

const own = (req, deviceId) =>
  db.prepare('SELECT * FROM devices WHERE id = ? AND user_id = ?').get(deviceId, req.user.id);

// ---------- parent auth ----------
app.post('/api/auth/login', { config: { rateLimit: { max: 5, timeWindow: '1 minute' } } }, async (req, reply) => {
  const { email, password } = req.body || {};
  const u = typeof email === 'string' && db.prepare('SELECT * FROM users WHERE email = ?').get(email.toLowerCase());
  if (!u || typeof password !== 'string' || !verifyPassword(password, u.pw_hash)) {
    return reply.code(401).send({ error: 'invalid_credentials' });
  }
  if (u.mfa_enabled && !u.must_change) {
    const { pendingToken, code } = createLoginOtp(db, u.id);
    await sendEmail(app, {
      to: u.alert_email || u.email,
      subject: 'Tether sign-in code',
      text: `Your sign-in code is ${code}. It expires in 10 minutes.`,
    });
    return { need_code: true, pending_token: pendingToken };
  }
  return { token: createSession(db, u.id), must_change: !!u.must_change };
});

app.post('/api/auth/2fa/verify', { config: { rateLimit: { max: 10, timeWindow: '1 minute' } } }, async (req, reply) => {
  const { pending_token, code } = req.body || {};
  if (typeof pending_token !== 'string') return reply.code(400).send({ error: 'bad_request' });
  const result = verifyLoginOtp(db, pending_token, code);
  if (typeof result === 'string') return reply.code(401).send({ error: result });
  const u = db.prepare('SELECT must_change FROM users WHERE id = ?').get(result);
  return { token: createSession(db, result), must_change: !!u?.must_change };
});

app.post('/api/auth/change-password', { preHandler: app.requireUser }, async (req, reply) => {
  const { current, next } = req.body || {};
  const u = db.prepare('SELECT * FROM users WHERE id = ?').get(req.user.id);
  if (!verifyPassword(String(current || ''), u.pw_hash)) return reply.code(401).send({ error: 'invalid_credentials' });
  if (typeof next !== 'string' || next.length < 12) return reply.code(400).send({ error: 'password_min_12_chars' });
  db.prepare('UPDATE users SET pw_hash = ?, must_change = 0 WHERE id = ?').run(hashPassword(next), u.id);
  db.prepare('DELETE FROM sessions WHERE user_id = ?').run(u.id);
  return { token: createSession(db, u.id) };
});

app.get('/api/auth/me', { preHandler: app.requireUser }, async (req) => {
  const u = db.prepare('SELECT mfa_enabled, alert_email FROM users WHERE id = ?').get(req.user.id);
  return { ...req.user, mfa_enabled: !!u.mfa_enabled, alert_email: u.alert_email };
});

// ---------- parent API ----------
const auth = { preHandler: app.requireUser };

app.post('/api/auth/2fa/enable', auth, async (req) => {
  db.prepare('UPDATE users SET mfa_enabled = 1 WHERE id = ?').run(req.user.id);
  return { ok: true };
});

app.post('/api/auth/2fa/disable', auth, async (req, reply) => {
  const u = db.prepare('SELECT pw_hash FROM users WHERE id = ?').get(req.user.id);
  if (!verifyPassword(String(req.body?.password || ''), u.pw_hash)) return reply.code(401).send({ error: 'invalid_credentials' });
  db.prepare('UPDATE users SET mfa_enabled = 0 WHERE id = ?').run(req.user.id);
  return { ok: true };
});

app.post('/api/auth/alert-email', auth, async (req, reply) => {
  const email = req.body?.email;
  if (email != null && (typeof email !== 'string' || !/^\S+@\S+\.\S+$/.test(email))) {
    return reply.code(400).send({ error: 'bad_email' });
  }
  db.prepare('UPDATE users SET alert_email = ? WHERE id = ?').run(email || null, req.user.id);
  return { ok: true };
});

// ---------- alerts ----------
app.get('/api/devices/:id/alerts', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  const limit = Math.min(Number(req.query.limit) || 100, 500);
  return db.prepare('SELECT id, ts, type, message, read FROM alerts WHERE device_id = ? ORDER BY ts DESC LIMIT ?')
    .all(req.params.id, limit);
});

app.post('/api/devices/:id/alerts/:aid/read', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  db.prepare('UPDATE alerts SET read = 1 WHERE id = ? AND device_id = ?').run(req.params.aid, req.params.id);
  return { ok: true };
});

// ---------- keywords (per account, applies to every device) ----------
app.get('/api/keywords', auth, async (req) =>
  db.prepare('SELECT id, word, is_regex FROM keywords WHERE user_id = ? ORDER BY is_regex DESC, word').all(req.user.id));

app.post('/api/keywords', auth, async (req, reply) => {
  const isRegex = req.body?.regex ? 1 : 0;
  const word = String(req.body?.word || '').trim().slice(0, isRegex ? 200 : 100);
  if (!word) return reply.code(400).send({ error: 'word_required' });
  if (isRegex) {
    // Phone matches with java.util.regex, server with JS; flags mirror each other (case-insensitive, unicode).
    try { if (new RegExp(word, 'iu').test('')) return reply.code(400).send({ error: 'regex_matches_empty' }); }
    catch { return reply.code(400).send({ error: 'invalid_regex' }); }
  }
  try {
    const r = db.prepare('INSERT INTO keywords (user_id, word, is_regex) VALUES (?, ?, ?)').run(req.user.id, word, isRegex);
    return { id: r.lastInsertRowid, word, is_regex: isRegex };
  } catch {
    return reply.code(409).send({ error: 'already_exists' });
  }
});

app.delete('/api/keywords/:kid', auth, async (req) => {
  db.prepare('DELETE FROM keywords WHERE id = ? AND user_id = ?').run(req.params.kid, req.user.id);
  return { ok: true };
});

// ---------- geofences ----------
app.get('/api/devices/:id/geofences', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  return db.prepare('SELECT id, name, lat, lon, radius_m FROM geofences WHERE device_id = ?').all(req.params.id);
});

app.post('/api/devices/:id/geofences', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  const { name, lat, lon, radius_m } = req.body || {};
  if (!name || typeof lat !== 'number' || typeof lon !== 'number' || !(radius_m > 0)) {
    return reply.code(400).send({ error: 'bad_geofence' });
  }
  const r = db.prepare('INSERT INTO geofences (device_id, name, lat, lon, radius_m) VALUES (?, ?, ?, ?, ?)')
    .run(d.id, String(name).slice(0, 100), lat, lon, radius_m);
  return { id: r.lastInsertRowid };
});

app.delete('/api/devices/:id/geofences/:gid', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  db.prepare('DELETE FROM geofences WHERE id = ? AND device_id = ?').run(req.params.gid, req.params.id);
  return { ok: true };
});

// ---------- app rules (block list / daily limits) ----------
app.get('/api/devices/:id/apprules', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  return db.prepare('SELECT id, package, app_name, blocked, uninstall, daily_limit_min FROM app_rules WHERE device_id = ?').all(req.params.id);
});

app.get('/api/devices/:id/apps', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  return db.prepare('SELECT package, app_name, system FROM installed_apps WHERE device_id = ? ORDER BY lower(coalesce(app_name, package))')
    .all(req.params.id);
});

app.get('/api/devices/:id/sites', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  const limit = Math.min(Number(req.query.limit) || 300, 1000);
  const since = Number(req.query.since) || Date.now() - 24 * 60 * 60 * 1000;
  const rows = db.prepare(
    `SELECT domain, count(*) AS hits, max(ts) AS last_ts, max(app_name) AS app_name, max(package) AS package
     FROM dns_log WHERE device_id = ? AND ts >= ? GROUP BY domain ORDER BY last_ts DESC LIMIT ?`
  ).all(req.params.id, since, limit);
  const classify = makeClassifier(db, deviceBlockState(db, req.params.id));
  return rows.map((r) => ({ ...r, ...classify(r.domain) }));
});

// ---------- DNS blocking: category switches and custom domains (see AdBlock.kt) ----------
const blockSettings = (id) => {
  const s = deviceBlockState(db, id);
  const counts = new Map(db.prepare('SELECT cat, count FROM block_meta').all().map((m) => [m.cat, m.count]));
  return {
    enabled: s.enabled,
    cats: s.cats,
    custom: s.custom,
    categories: Object.entries(CATEGORIES).map(([key, c]) => ({ key, label: c.label, count: counts.get(key) ?? null })),
  };
};

app.get('/api/devices/:id/adblock', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  return blockSettings(d.id);
});

app.put('/api/devices/:id/adblock', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  if (req.body?.enabled !== undefined) db.prepare('UPDATE devices SET adblock = ? WHERE id = ?').run(req.body.enabled ? 1 : 0, d.id);
  if (Array.isArray(req.body?.cats)) db.prepare('UPDATE devices SET block_cats = ? WHERE id = ?').run(parseCats(req.body.cats.join(',')).join(','), d.id);
  return blockSettings(d.id);
});

app.post('/api/devices/:id/blocked-domains', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  const domain = normalizeDomain(req.body?.domain);
  if (!domain) return reply.code(400).send({ error: 'invalid_domain' });
  db.prepare('INSERT OR IGNORE INTO custom_blocks (device_id, domain) VALUES (?, ?)').run(d.id, domain);
  return { domain };
});

app.delete('/api/devices/:id/blocked-domains/:domain', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  db.prepare('DELETE FROM custom_blocks WHERE device_id = ? AND domain = ?').run(d.id, req.params.domain.toLowerCase());
  return { ok: true };
});

app.put('/api/devices/:id/apprules', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  const list = Array.isArray(req.body) ? req.body : [];
  for (const r of list) {
    if (!r.package || typeof r.package !== 'string') return reply.code(400).send({ error: 'bad_rule' });
  }
  db.transaction(() => {
    db.prepare('DELETE FROM app_rules WHERE device_id = ?').run(d.id);
    const ins = db.prepare('INSERT OR REPLACE INTO app_rules (device_id, package, app_name, blocked, uninstall, daily_limit_min) VALUES (?, ?, ?, ?, ?, ?)');
    for (const r of list) {
      // Uninstall implies block, so the app stays unusable if the uninstall fails (system app, offline phone).
      const uninstall = r.uninstall ? 1 : 0;
      ins.run(d.id, r.package.slice(0, 200), r.app_name ? String(r.app_name).slice(0, 200) : null,
        r.blocked || uninstall ? 1 : 0, uninstall, r.daily_limit_min > 0 ? Math.round(r.daily_limit_min) : null);
    }
  })();
  wake(d.id);
  return { ok: true };
});

// ---------- message search (SMS + notifications) ----------
app.get('/api/devices/:id/search', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  const q = String(req.query.q || '').trim();
  if (q.length < 2) return reply.code(400).send({ error: 'query_too_short' });
  const like = `%${q}%`;
  const limit = Math.min(Number(req.query.limit) || 100, 300);
  const smsRows = db.prepare(
    `SELECT 'sms' AS kind, ts, contact_name, address AS who, body AS text FROM sms
     WHERE device_id = ? AND (body LIKE ? OR contact_name LIKE ? OR address LIKE ?) ORDER BY ts DESC LIMIT ?`
  ).all(req.params.id, like, like, like, limit);
  const notifRows = db.prepare(
    `SELECT 'notification' AS kind, n.ts, ${APP_NAME_SQL} AS contact_name, n.package AS who, (n.title || ': ' || n.text) AS text
     FROM notifications n LEFT JOIN installed_apps ia ON ia.device_id = n.device_id AND ia.package = n.package
     WHERE n.device_id = ? AND (n.title LIKE ? OR n.text LIKE ? OR n.package LIKE ? OR n.app_name LIKE ? OR ia.app_name LIKE ?)
     ORDER BY n.ts DESC LIMIT ?`
  ).all(req.params.id, like, like, like, like, like, limit);
  return [...smsRows, ...notifRows].sort((a, b) => b.ts - a.ts).slice(0, limit);
});

app.get('/api/devices', auth, async (req) =>
  db.prepare(`SELECT id, name, battery_pct, charging, locked, last_seen,
    cell_type, cell_level, cell_dbm, wifi_connected, wifi_ssid, wifi_level, wifi_rssi
    FROM devices WHERE user_id = ?`).all(req.user.id));

function listRoute(table, cols, orderCol = 'ts') {
  app.get(`/api/devices/:id/${table}`, auth, async (req, reply) => {
    if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
    const limit = Math.min(Number(req.query.limit) || 100, 500);
    const before = Number(req.query.before) || Number.MAX_SAFE_INTEGER;
    return db.prepare(`SELECT ${cols} FROM ${table} WHERE device_id = ? AND ${orderCol} < ? ORDER BY ${orderCol} DESC LIMIT ?`)
      .all(req.params.id, before, limit);
  });
}
listRoute('locations', 'ts, lat, lon, accuracy');
listRoute('sms', 'ts, direction, address, body, contact_name');
listRoute('calls', 'ts, direction, number, duration_s, contact_name');
listRoute('media', 'id, kind, ts, orig_name');

// Notifications carry the phone's app label when it sent one; rows from before
// that fall back to the installed-apps list, then to the raw package name.
app.get('/api/devices/:id/notifications', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  const limit = Math.min(Number(req.query.limit) || 100, 500);
  const before = Number(req.query.before) || Number.MAX_SAFE_INTEGER;
  return db.prepare(
    `SELECT n.ts, n.package, ${APP_NAME_SQL} AS app_name, n.title, n.text
     FROM notifications n LEFT JOIN installed_apps ia ON ia.device_id = n.device_id AND ia.package = n.package
     WHERE n.device_id = ? AND n.ts < ? ORDER BY n.ts DESC LIMIT ?`
  ).all(req.params.id, before, limit);
});

app.get('/api/devices/:id/usage', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  const day = String(req.query.day || new Date().toISOString().slice(0, 10));
  return db.prepare('SELECT package, app_name, foreground_ms FROM usage WHERE device_id = ? AND day = ? ORDER BY foreground_ms DESC')
    .all(req.params.id, day);
});

app.get('/api/media/:mid', auth, async (req, reply) => {
  const m = db.prepare(`SELECT m.* FROM media m JOIN devices d ON d.id = m.device_id WHERE m.id = ? AND d.user_id = ?`)
    .get(req.params.mid, req.user.id);
  if (!m) return reply.code(404).send({ error: 'not_found' });
  return reply.type('image/jpeg').send(fs.createReadStream(path.join(MEDIA_DIR, m.file)));
});

app.get('/api/devices/:id/screen', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  const f = latestFrame.get(Number(req.params.id));
  if (!f) return reply.code(204).send();
  return reply.type('image/jpeg').header('X-Frame-Ts', f.ts).header('Cache-Control', 'no-store').send(f.buf);
});

app.post('/api/devices/:id/commands', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  const { type, args } = req.body || {};
  if (!COMMAND_TYPES.has(type)) return reply.code(400).send({ error: 'unknown_command' });
  const r = db.prepare('INSERT INTO commands (device_id, type, args, created_at) VALUES (?, ?, ?, ?)')
    .run(d.id, type, args ? JSON.stringify(args) : null, Date.now());
  wake(d.id);
  return { id: r.lastInsertRowid };
});

app.get('/api/devices/:id/commands', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  return db.prepare('SELECT id, type, args, status, created_at, done_at FROM commands WHERE device_id = ? ORDER BY id DESC LIMIT 50')
    .all(req.params.id);
});

app.get('/api/devices/:id/schedules', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  return db.prepare('SELECT id, days, start_min, end_min FROM schedules WHERE device_id = ?').all(req.params.id);
});

app.put('/api/devices/:id/schedules', auth, async (req, reply) => {
  const d = own(req, req.params.id);
  if (!d) return reply.code(404).send({ error: 'not_found' });
  const list = Array.isArray(req.body) ? req.body : [];
  for (const s of list) {
    if (!/^[0-6]{1,7}$/.test(String(s.days)) || !(s.start_min >= 0 && s.start_min < 1440) || !(s.end_min >= 0 && s.end_min <= 1440)) {
      return reply.code(400).send({ error: 'bad_schedule' });
    }
  }
  db.transaction(() => {
    db.prepare('DELETE FROM schedules WHERE device_id = ?').run(d.id);
    const ins = db.prepare('INSERT INTO schedules (device_id, days, start_min, end_min) VALUES (?, ?, ?, ?)');
    for (const s of list) ins.run(d.id, String(s.days), s.start_min, s.end_min);
  })();
  wake(d.id);
  return { ok: true };
});

app.delete('/api/devices/:id', auth, async (req, reply) => {
  if (!own(req, req.params.id)) return reply.code(404).send({ error: 'not_found' });
  db.prepare('DELETE FROM devices WHERE id = ?').run(req.params.id);
  return { ok: true };
});

// ---------- device API ----------
app.post('/api/device/enroll', { config: { rateLimit: { max: 5, timeWindow: '1 minute' } } }, async (req, reply) => {
  const { email, password, name } = req.body || {};
  const u = typeof email === 'string' && db.prepare('SELECT * FROM users WHERE email = ?').get(email.toLowerCase());
  if (!u || typeof password !== 'string' || !verifyPassword(password, u.pw_hash)) {
    return reply.code(401).send({ error: 'invalid_credentials' });
  }
  if (u.must_change) return reply.code(403).send({ error: 'password_change_required' });
  const token = newToken();
  const r = db.prepare('INSERT INTO devices (user_id, name, token_hash, created_at) VALUES (?, ?, ?, ?)')
    .run(u.id, String(name || 'Phone').slice(0, 60), sha256(token), Date.now());
  return { device_id: r.lastInsertRowid, token };
});

const dev = { preHandler: app.requireDevice };

app.post('/api/device/sync', dev, async (req) => {
  const b = req.body || {};
  const id = req.device.id;
  const prevFlags = req.device; // row as it was before this request's last_seen update, so tamper checks see the pre-sync state
  const str = (v, n = 4000) => (v == null ? null : String(v).slice(0, n));
  const boolOrNull = (v) => (v == null ? null : (v ? 1 : 0));
  db.transaction(() => {
    if (b.status) {
      db.prepare(`UPDATE devices SET battery_pct = ?, charging = ?, locked = ?,
        admin_active = coalesce(?, admin_active), accessibility_active = coalesce(?, accessibility_active),
        notif_listener_active = coalesce(?, notif_listener_active),
        cell_type = ?, cell_level = ?, cell_dbm = ?,
        wifi_connected = ?, wifi_ssid = ?, wifi_level = ?, wifi_rssi = ? WHERE id = ?`)
        .run(b.status.battery_pct ?? null, b.status.charging ? 1 : 0, b.status.locked ? 1 : 0,
          boolOrNull(b.status.admin_active), boolOrNull(b.status.accessibility_active), boolOrNull(b.status.notif_listener_active),
          str(b.status.cell_type, 8), b.status.cell_level ?? null, b.status.cell_dbm ?? null,
          boolOrNull(b.status.wifi_connected), str(b.status.wifi_ssid, 64), b.status.wifi_level ?? null, b.status.wifi_rssi ?? null, id);
    }
    const insLoc = db.prepare('INSERT INTO locations (device_id, ts, lat, lon, accuracy) VALUES (?, ?, ?, ?, ?)');
    for (const l of b.locations || []) insLoc.run(id, l.ts, l.lat, l.lon, l.accuracy ?? null);
    const insSms = db.prepare('INSERT OR IGNORE INTO sms (device_id, ts, direction, address, body, contact_name) VALUES (?, ?, ?, ?, ?, ?)');
    for (const s of b.sms || []) insSms.run(id, s.ts, str(s.direction, 8), str(s.address, 64), str(s.body), str(s.contact, 200));
    const insCall = db.prepare('INSERT OR IGNORE INTO calls (device_id, ts, direction, number, duration_s, contact_name) VALUES (?, ?, ?, ?, ?, ?)');
    for (const c of b.calls || []) insCall.run(id, c.ts, str(c.direction, 12), str(c.number, 64), c.duration_s ?? 0, str(c.contact, 200));
    const insN = db.prepare('INSERT INTO notifications (device_id, ts, package, app_name, title, text) VALUES (?, ?, ?, ?, ?, ?)');
    for (const n of b.notifications || []) insN.run(id, n.ts, str(n.package, 200), str(n.app_name, 200), str(n.title, 500), str(n.text));
    if (Array.isArray(b.apps)) {
      // The phone sends its full launchable-app list; replace ours so uninstalled apps drop off.
      db.prepare('DELETE FROM installed_apps WHERE device_id = ?').run(id);
      const insA = db.prepare('INSERT OR REPLACE INTO installed_apps (device_id, package, app_name, system) VALUES (?, ?, ?, ?)');
      for (const a of b.apps) if (a.package) insA.run(id, str(a.package, 200), str(a.app_name, 200), a.system ? 1 : 0);
    }
    const insD = db.prepare('INSERT INTO dns_log (device_id, ts, domain, package, app_name) VALUES (?, ?, ?, ?, ?)');
    for (const q of b.dns || []) if (q.domain) insD.run(id, q.ts, str(q.domain, 255), str(q.package, 200), str(q.app_name, 200));
    const insU = db.prepare(`INSERT INTO usage (device_id, day, package, app_name, foreground_ms) VALUES (?, ?, ?, ?, ?)
      ON CONFLICT (device_id, day, package) DO UPDATE SET app_name = excluded.app_name, foreground_ms = excluded.foreground_ms`);
    for (const u of b.usage || []) insU.run(id, str(u.day, 10), str(u.package, 200), str(u.app_name, 200), u.foreground_ms);
  })();

  // Alerting runs after the transaction commits: offline/tamper/geofence/keyword
  // checks each do their own small write (alerts table) plus a fire-and-forget email.
  clearOfflineAlert(db, id);
  if (b.status) {
    checkTamper(app, db, prevFlags, {
      admin_active: boolOrNull(b.status.admin_active),
      accessibility_active: boolOrNull(b.status.accessibility_active),
      notif_listener_active: boolOrNull(b.status.notif_listener_active),
    });
  }
  for (const l of b.locations || []) checkGeofences(app, db, id, l.lat, l.lon);
  for (const s of b.sms || []) checkKeywords(app, db, id, req.device.user_id, `SMS from ${s.contact || s.address || 'unknown'}`, s.body, () => shotNear(id, s.ts));
  for (const n of b.notifications || []) checkKeywords(app, db, id, req.device.user_id, `notification (${n.app_name || n.package || 'app'})`, `${n.title || ''} ${n.text || ''}`.trim(), () => shotNear(id, n.ts));

  return { ok: true };
});

app.get('/api/device/schedule', dev, async (req) =>
  db.prepare('SELECT days, start_min, end_min FROM schedules WHERE device_id = ?').all(req.device.id));

app.get('/api/device/keywords', dev, async (req) =>
  db.prepare('SELECT word, is_regex AS regex FROM keywords WHERE user_id = ?').all(req.device.user_id)
    .map((k) => ({ word: k.word, regex: !!k.regex })));

// DNS ad block: per-device switch, plus the shared domain list (see AdBlock.kt).
app.get('/api/device/adblock', dev, async (req) =>
  ({ version: deviceBlockState(db, req.device.id).version }));

app.get('/api/device/blocklist', dev, async (req, reply) => {
  const state = deviceBlockState(db, req.device.id);
  return reply.header('X-Blocklist-Version', state.version).type('text/plain').send(deviceBlocklist(db, state));
});

// A keyword the phone saw on screen or typed (see KeywordWatch.checkScreen).
app.post('/api/device/screen-hit', dev, async (req) => {
  const b = req.body || {};
  const cut = (v, n) => String(v ?? '').slice(0, n);
  const ts = Number(b.ts) || Date.now();
  raiseAlert(app, db, req.device.id, 'keyword',
    `Keyword "${cut(b.words, 300)}" on screen in ${cut(b.app_name || b.package, 100)}: ${cut(b.snippet, 400)}`,
    () => shotNear(req.device.id, ts));
  return { ok: true };
});

app.get('/api/device/apprules', dev, async (req) =>
  db.prepare('SELECT package, blocked, uninstall, daily_limit_min FROM app_rules WHERE device_id = ?').all(req.device.id));

app.get('/api/device/commands', dev, async (req, reply) => {
  const take = () => db.prepare(`SELECT id, type, args FROM commands WHERE device_id = ? AND status = 'pending' ORDER BY id`).all(req.device.id);
  let rows = take();
  if (!rows.length) {
    await new Promise((resolve) => {
      const set = waiters.get(req.device.id) || new Set();
      waiters.set(req.device.id, set);
      const done = () => { clearTimeout(t); set.delete(done); resolve(); };
      const t = setTimeout(done, LONG_POLL_MS);
      set.add(done);
      req.raw.on('close', done);
    });
    rows = take();
  }
  const mark = db.prepare(`UPDATE commands SET status = 'sent' WHERE id = ?`);
  for (const r of rows) mark.run(r.id);
  return rows.map((r) => ({ id: r.id, type: r.type, args: r.args ? JSON.parse(r.args) : null }));
});

app.post('/api/device/commands/:cid/done', dev, async (req) => {
  db.prepare(`UPDATE commands SET status = ?, done_at = ? WHERE id = ? AND device_id = ?`)
    .run(req.body?.ok === false ? 'failed' : 'done', Date.now(), req.params.cid, req.device.id);
  return { ok: true };
});

app.post('/api/device/screen', dev, async (req, reply) => {
  if (!Buffer.isBuffer(req.body)) return reply.code(415).send({ error: 'jpeg_required' });
  latestFrame.set(req.device.id, { ts: Date.now(), buf: req.body });
  return { ok: true };
});

app.post('/api/device/media', dev, async (req, reply) => {
  const part = await req.file();
  if (!part) return reply.code(400).send({ error: 'file_required' });
  const kind = ['photo', 'screenshot'].includes(part.fields.kind?.value) ? part.fields.kind.value : 'photo';
  const ts = Number(part.fields.ts?.value) || Date.now();
  const file = `${req.device.id}-${crypto.randomUUID()}.jpg`;
  const out = fs.createWriteStream(path.join(MEDIA_DIR, file));
  await new Promise((res, rej) => { part.file.pipe(out).on('finish', res).on('error', rej); });
  if (part.file.truncated) { fs.rmSync(path.join(MEDIA_DIR, file), { force: true }); return reply.code(413).send({ error: 'too_large' }); }
  const r = db.prepare('INSERT INTO media (device_id, kind, ts, file, orig_name) VALUES (?, ?, ?, ?, ?)')
    .run(req.device.id, kind, ts, file, String(part.filename || '').slice(0, 200));
  return { id: r.lastInsertRowid };
});

setInterval(() => offlineWatchdog(app, db), 5 * 60 * 1000);
setInterval(() => db.prepare('DELETE FROM dns_log WHERE ts < ?').run(Date.now() - 30 * 24 * 60 * 60 * 1000), 6 * 60 * 60 * 1000);

await app.listen({ port: PORT, host: HOST });
