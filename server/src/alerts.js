import { sendEmail } from './email.js';

// All the "something the parent should know about right now" logic lives
// here: keyword hits in SMS/notifications, geofence enter/exit, device
// tamper flags flipping off, and the offline watchdog. Everything funnels
// through raiseAlert, which both logs a row the dashboard can show and
// emails the parent via SMTP2GO.

const OFFLINE_THRESHOLD_MS = 30 * 60 * 1000; // no check-in for 30 min
const EARTH_R_M = 6371000;

function haversineM(lat1, lon1, lat2, lon2) {
  const toRad = (d) => (d * Math.PI) / 180;
  const dLat = toRad(lat2 - lat1);
  const dLon = toRad(lon2 - lon1);
  const a = Math.sin(dLat / 2) ** 2 + Math.cos(toRad(lat1)) * Math.cos(toRad(lat2)) * Math.sin(dLon / 2) ** 2;
  return 2 * EARTH_R_M * Math.asin(Math.sqrt(a));
}

export function raiseAlert(app, db, deviceId, type, message) {
  db.prepare('INSERT INTO alerts (device_id, ts, type, message) VALUES (?, ?, ?, ?)')
    .run(deviceId, Date.now(), type, message);
  const row = db.prepare(
    `SELECT u.email, u.alert_email, d.name FROM devices d JOIN users u ON u.id = d.user_id WHERE d.id = ?`
  ).get(deviceId);
  if (!row) return;
  const to = row.alert_email || row.email;
  sendEmail(app, { to, subject: `Tether alert: ${row.name}`, text: message }).catch(() => {});
}

export function checkKeywords(app, db, deviceId, userId, source, text) {
  if (!text) return;
  const words = db.prepare('SELECT word FROM keywords WHERE user_id = ?').all(userId);
  const lower = text.toLowerCase();
  for (const { word } of words) {
    if (word && lower.includes(word.toLowerCase())) {
      const snippet = text.length > 160 ? `${text.slice(0, 160)}…` : text;
      raiseAlert(app, db, deviceId, 'keyword',
        `Keyword "${word}" matched in ${source}: ${snippet}`);
      break; // one alert per message is enough, even if several words match
    }
  }
}

export function checkGeofences(app, db, deviceId, lat, lon) {
  const fences = db.prepare('SELECT * FROM geofences WHERE device_id = ?').all(deviceId);
  for (const f of fences) {
    const inside = haversineM(lat, lon, f.lat, f.lon) <= f.radius_m;
    const state = db.prepare('SELECT inside FROM geofence_state WHERE geofence_id = ?').get(f.id);
    const wasInside = state ? !!state.inside : null; // null = no prior reading, don't fire on first fix
    if (state) {
      db.prepare('UPDATE geofence_state SET inside = ? WHERE geofence_id = ?').run(inside ? 1 : 0, f.id);
    } else {
      db.prepare('INSERT INTO geofence_state (geofence_id, inside) VALUES (?, ?)').run(f.id, inside ? 1 : 0);
    }
    if (wasInside === null) continue;
    if (inside && !wasInside) raiseAlert(app, db, deviceId, 'geofence', `Entered "${f.name}"`);
    else if (!inside && wasInside) raiseAlert(app, db, deviceId, 'geofence', `Left "${f.name}"`);
  }
}

// Called once per sync with the flags the device reported this tick.
// Fires only on a 1 -> 0 transition for each flag, not on every sync
// where it happens to already be off (that would spam every 5 minutes).
export function checkTamper(app, db, device, flags) {
  const checks = [
    ['admin_active', 'Device Owner / admin was turned off'],
    ['accessibility_active', 'Accessibility service was turned off'],
    ['notif_listener_active', 'Notification access was turned off'],
  ];
  for (const [key, msg] of checks) {
    if (flags[key] == null) continue;
    const prev = device[key];
    if (prev === 1 && flags[key] === 0) {
      raiseAlert(app, db, device.id, 'tamper', msg);
    }
  }
}

export function offlineWatchdog(app, db) {
  const now = Date.now();
  const stale = db.prepare(
    `SELECT id, name FROM devices WHERE last_seen IS NOT NULL AND last_seen < ? AND offline_alerted_at IS NULL`
  ).all(now - OFFLINE_THRESHOLD_MS);
  for (const d of stale) {
    db.prepare('UPDATE devices SET offline_alerted_at = ? WHERE id = ?').run(now, d.id);
    raiseAlert(app, db, d.id, 'offline', `No check-in for over ${OFFLINE_THRESHOLD_MS / 60000} minutes.`);
  }
}

// Called from the sync handler so a device coming back online clears its
// own offline flag (and, if one had been raised, logs that it's back).
export function clearOfflineAlert(db, deviceId) {
  const d = db.prepare('SELECT offline_alerted_at FROM devices WHERE id = ?').get(deviceId);
  if (d?.offline_alerted_at) {
    db.prepare('UPDATE devices SET offline_alerted_at = NULL WHERE id = ?').run(deviceId);
  }
}
