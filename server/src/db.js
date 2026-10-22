import Database from 'better-sqlite3';
import fs from 'node:fs';
import path from 'node:path';

export function openDb(dataDir) {
  fs.mkdirSync(dataDir, { recursive: true });
  const db = new Database(path.join(dataDir, 'tether.db'));
  db.pragma('journal_mode = WAL');
  db.pragma('foreign_keys = ON');
  db.exec(`
    CREATE TABLE IF NOT EXISTS users (
      id INTEGER PRIMARY KEY,
      email TEXT NOT NULL UNIQUE,
      pw_hash TEXT NOT NULL,
      must_change INTEGER NOT NULL DEFAULT 0,
      mfa_enabled INTEGER NOT NULL DEFAULT 0,
      alert_email TEXT,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS sessions (
      token_hash TEXT PRIMARY KEY,
      user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      expires_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS login_otps (
      token_hash TEXT PRIMARY KEY,
      user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      code_hash TEXT NOT NULL,
      attempts INTEGER NOT NULL DEFAULT 0,
      expires_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS devices (
      id INTEGER PRIMARY KEY,
      user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      name TEXT NOT NULL,
      token_hash TEXT NOT NULL UNIQUE,
      battery_pct INTEGER,
      charging INTEGER,
      locked INTEGER NOT NULL DEFAULT 0,
      last_seen INTEGER,
      admin_active INTEGER,
      accessibility_active INTEGER,
      notif_listener_active INTEGER,
      offline_alerted_at INTEGER,
      cell_type TEXT,
      cell_level INTEGER,
      cell_dbm INTEGER,
      wifi_connected INTEGER,
      wifi_ssid TEXT,
      wifi_level INTEGER,
      wifi_rssi INTEGER,
      created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS locations (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      ts INTEGER NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL, accuracy REAL
    );
    CREATE TABLE IF NOT EXISTS sms (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      ts INTEGER NOT NULL, direction TEXT NOT NULL, address TEXT, body TEXT, contact_name TEXT,
      UNIQUE (device_id, ts, direction, address)
    );
    CREATE TABLE IF NOT EXISTS calls (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      ts INTEGER NOT NULL, direction TEXT NOT NULL, number TEXT, duration_s INTEGER, contact_name TEXT,
      UNIQUE (device_id, ts, number)
    );
    CREATE TABLE IF NOT EXISTS notifications (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      ts INTEGER NOT NULL, package TEXT, title TEXT, text TEXT
    );
    CREATE TABLE IF NOT EXISTS usage (
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      day TEXT NOT NULL, package TEXT NOT NULL, app_name TEXT, foreground_ms INTEGER NOT NULL,
      PRIMARY KEY (device_id, day, package)
    );
    CREATE TABLE IF NOT EXISTS media (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      kind TEXT NOT NULL, ts INTEGER NOT NULL, file TEXT NOT NULL, orig_name TEXT
    );
    CREATE TABLE IF NOT EXISTS commands (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      type TEXT NOT NULL, args TEXT, status TEXT NOT NULL DEFAULT 'pending',
      created_at INTEGER NOT NULL, done_at INTEGER
    );
    CREATE TABLE IF NOT EXISTS schedules (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      days TEXT NOT NULL, start_min INTEGER NOT NULL, end_min INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS keywords (
      id INTEGER PRIMARY KEY,
      user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      word TEXT NOT NULL,
      UNIQUE (user_id, word)
    );
    CREATE TABLE IF NOT EXISTS alerts (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      ts INTEGER NOT NULL,
      type TEXT NOT NULL,
      message TEXT NOT NULL,
      read INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS geofences (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      name TEXT NOT NULL,
      lat REAL NOT NULL, lon REAL NOT NULL, radius_m REAL NOT NULL
    );
    CREATE TABLE IF NOT EXISTS geofence_state (
      geofence_id INTEGER PRIMARY KEY REFERENCES geofences(id) ON DELETE CASCADE,
      inside INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS app_rules (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      package TEXT NOT NULL,
      app_name TEXT,
      blocked INTEGER NOT NULL DEFAULT 0,
      daily_limit_min INTEGER,
      UNIQUE (device_id, package)
    );
    CREATE TABLE IF NOT EXISTS installed_apps (
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      package TEXT NOT NULL,
      app_name TEXT,
      system INTEGER NOT NULL DEFAULT 0,
      PRIMARY KEY (device_id, package)
    );
    CREATE TABLE IF NOT EXISTS dns_log (
      id INTEGER PRIMARY KEY,
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      ts INTEGER NOT NULL,
      domain TEXT NOT NULL,
      package TEXT,
      app_name TEXT
    );
    CREATE INDEX IF NOT EXISTS idx_dns ON dns_log (device_id, ts);
    CREATE INDEX IF NOT EXISTS idx_loc ON locations (device_id, ts);
    CREATE INDEX IF NOT EXISTS idx_sms ON sms (device_id, ts);
    CREATE INDEX IF NOT EXISTS idx_calls ON calls (device_id, ts);
    CREATE INDEX IF NOT EXISTS idx_notif ON notifications (device_id, ts);
    CREATE INDEX IF NOT EXISTS idx_cmd ON commands (device_id, status);
    CREATE INDEX IF NOT EXISTS idx_alerts ON alerts (device_id, ts);
  `);

  const columns = (table) => db.prepare(`PRAGMA table_info(${table})`).all().map((c) => c.name);
  if (!columns('sms').includes('contact_name')) db.exec('ALTER TABLE sms ADD COLUMN contact_name TEXT');
  if (!columns('calls').includes('contact_name')) db.exec('ALTER TABLE calls ADD COLUMN contact_name TEXT');
  if (!columns('usage').includes('app_name')) db.exec('ALTER TABLE usage ADD COLUMN app_name TEXT');
  if (!columns('notifications').includes('app_name')) db.exec('ALTER TABLE notifications ADD COLUMN app_name TEXT');
  if (!columns('app_rules').includes('uninstall')) db.exec('ALTER TABLE app_rules ADD COLUMN uninstall INTEGER NOT NULL DEFAULT 0');
  if (!columns('users').includes('mfa_enabled')) db.exec('ALTER TABLE users ADD COLUMN mfa_enabled INTEGER NOT NULL DEFAULT 0');
  if (!columns('users').includes('alert_email')) db.exec('ALTER TABLE users ADD COLUMN alert_email TEXT');
  if (!columns('keywords').includes('is_regex')) db.exec('ALTER TABLE keywords ADD COLUMN is_regex INTEGER NOT NULL DEFAULT 0');
  if (!columns('devices').includes('admin_active')) db.exec('ALTER TABLE devices ADD COLUMN admin_active INTEGER');
  if (!columns('devices').includes('accessibility_active')) db.exec('ALTER TABLE devices ADD COLUMN accessibility_active INTEGER');
  if (!columns('devices').includes('notif_listener_active')) db.exec('ALTER TABLE devices ADD COLUMN notif_listener_active INTEGER');
  if (!columns('devices').includes('offline_alerted_at')) db.exec('ALTER TABLE devices ADD COLUMN offline_alerted_at INTEGER');
  if (!columns('devices').includes('adblock')) db.exec('ALTER TABLE devices ADD COLUMN adblock INTEGER NOT NULL DEFAULT 1');
  if (!columns('devices').includes('block_cats')) db.exec("ALTER TABLE devices ADD COLUMN block_cats TEXT NOT NULL DEFAULT 'ads,tracking,scams'");
  db.exec(`
    CREATE TABLE IF NOT EXISTS block_domains (
      domain TEXT NOT NULL,
      cat TEXT NOT NULL,
      PRIMARY KEY (domain, cat)
    ) WITHOUT ROWID;
    CREATE TABLE IF NOT EXISTS block_meta (
      cat TEXT PRIMARY KEY,
      updated INTEGER NOT NULL,
      count INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS custom_blocks (
      device_id INTEGER NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
      domain TEXT NOT NULL,
      PRIMARY KEY (device_id, domain)
    ) WITHOUT ROWID;
  `);
  for (const [col, type] of [['cell_type', 'TEXT'], ['cell_level', 'INTEGER'], ['cell_dbm', 'INTEGER'],
    ['wifi_connected', 'INTEGER'], ['wifi_ssid', 'TEXT'], ['wifi_level', 'INTEGER'], ['wifi_rssi', 'INTEGER']]) {
    if (!columns('devices').includes(col)) db.exec(`ALTER TABLE devices ADD COLUMN ${col} ${type}`);
  }

  return db;
}
