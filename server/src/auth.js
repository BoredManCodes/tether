import crypto from 'node:crypto';

const SESSION_TTL_MS = 7 * 24 * 3600 * 1000;
const OTP_TTL_MS = 10 * 60 * 1000;
const OTP_MAX_ATTEMPTS = 5;

export const sha256 = (s) => crypto.createHash('sha256').update(s).digest('hex');
export const newToken = () => crypto.randomBytes(32).toString('base64url');

export function hashPassword(pw) {
  const salt = crypto.randomBytes(16);
  const key = crypto.scryptSync(pw, salt, 64);
  return `scrypt$${salt.toString('base64')}$${key.toString('base64')}`;
}

export function verifyPassword(pw, stored) {
  const [alg, saltB64, keyB64] = stored.split('$');
  if (alg !== 'scrypt') return false;
  const expected = Buffer.from(keyB64, 'base64');
  const actual = crypto.scryptSync(pw, Buffer.from(saltB64, 'base64'), expected.length);
  return crypto.timingSafeEqual(expected, actual);
}

export function seedAdmin(db, email, password) {
  if (!email || !password) return false;
  if (db.prepare('SELECT COUNT(*) AS n FROM users').get().n > 0) return false;
  db.prepare('INSERT INTO users (email, pw_hash, must_change, created_at) VALUES (?, ?, 1, ?)')
    .run(email.toLowerCase(), hashPassword(password), Date.now());
  return true;
}

export function createSession(db, userId) {
  const token = newToken();
  db.prepare('INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (?, ?, ?)')
    .run(sha256(token), userId, Date.now() + SESSION_TTL_MS);
  return token;
}

// ---------- email-code 2FA ----------
// A password check that passes for an MFA-enabled user doesn't get a
// session yet: it gets a pending token tied to a freshly mailed 6-digit
// code. /api/auth/2fa/verify trades that pair for a real session.

const genCode = () => String(crypto.randomInt(0, 1_000_000)).padStart(6, '0');

export function createLoginOtp(db, userId) {
  const token = newToken();
  const code = genCode();
  db.prepare('INSERT INTO login_otps (token_hash, user_id, code_hash, expires_at) VALUES (?, ?, ?, ?)')
    .run(sha256(token), userId, sha256(code), Date.now() + OTP_TTL_MS);
  return { pendingToken: token, code };
}

// Returns the user id on success, or a string error code on failure.
export function verifyLoginOtp(db, pendingToken, code) {
  const row = db.prepare('SELECT * FROM login_otps WHERE token_hash = ?').get(sha256(pendingToken));
  if (!row || row.expires_at < Date.now()) return 'expired';
  if (row.attempts >= OTP_MAX_ATTEMPTS) return 'too_many_attempts';
  if (typeof code !== 'string' || sha256(code) !== row.code_hash) {
    db.prepare('UPDATE login_otps SET attempts = attempts + 1 WHERE token_hash = ?').run(sha256(pendingToken));
    return 'invalid_code';
  }
  db.prepare('DELETE FROM login_otps WHERE token_hash = ?').run(sha256(pendingToken));
  return row.user_id;
}

const bearer = (req) => (req.headers.authorization || '').replace(/^Bearer /, '');

export function registerAuth(app, db) {
  app.decorateRequest('user', null);
  app.decorateRequest('device', null);

  app.decorate('requireUser', async (req, reply) => {
    const t = bearer(req);
    const row = t && db.prepare(
      `SELECT u.id, u.email, u.must_change FROM sessions s JOIN users u ON u.id = s.user_id
       WHERE s.token_hash = ? AND s.expires_at > ?`).get(sha256(t), Date.now());
    if (!row) return reply.code(401).send({ error: 'unauthorized' });
    req.user = row;
    if (row.must_change && !req.url.startsWith('/api/auth/')) {
      return reply.code(403).send({ error: 'password_change_required' });
    }
  });

  app.decorate('requireDevice', async (req, reply) => {
    const t = bearer(req);
    const row = t && db.prepare('SELECT * FROM devices WHERE token_hash = ?').get(sha256(t));
    if (!row) return reply.code(401).send({ error: 'unauthorized' });
    req.device = row;
    db.prepare('UPDATE devices SET last_seen = ? WHERE id = ?').run(Date.now(), row.id);
  });
}
