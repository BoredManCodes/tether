// Thin wrapper around SMTP2GO's HTTP API. No SMTP, no extra deps: one
// fetch call per email. Silently no-ops (and logs) if the API key isn't
// configured, so a missing env var doesn't crash sync/alert code paths.

const API_URL = 'https://api.smtp2go.com/v3/email/send';

export async function sendEmail(app, { to, subject, text }) {
  const apiKey = process.env.SMTP2GO_API_KEY;
  const from = process.env.ALERT_EMAIL_FROM;
  if (!apiKey || !from || !to) {
    app.log.warn({ to, subject }, 'email not sent: SMTP2GO_API_KEY/ALERT_EMAIL_FROM/recipient missing');
    return false;
  }
  try {
    const res = await fetch(API_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Smtp2go-Api-Key': apiKey },
      body: JSON.stringify({ api_key: apiKey, to: [to], sender: from, subject, text_body: text }),
    });
    if (!res.ok) {
      app.log.error({ status: res.status, body: await res.text().catch(() => '') }, 'smtp2go send failed');
      return false;
    }
    return true;
  } catch (e) {
    app.log.error({ err: e.message }, 'smtp2go send threw');
    return false;
  }
}
