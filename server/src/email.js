// Thin wrapper around SMTP2GO's HTTP API. No SMTP, no extra deps: one
// fetch call per email. Silently no-ops (and logs) if the API key isn't
// configured, so a missing env var doesn't crash sync/alert code paths.

const API_URL = 'https://api.smtp2go.com/v3/email/send';

const esc = (s) => String(s)
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

// Message text can contain SMS/notification content from the child's phone,
// so it is always escaped before going into the HTML body.
function renderHtml(subject, text) {
  const body = esc(text).replace(/\r?\n/g, '<br>');
  return `<!doctype html>
<html><body style="margin:0;padding:24px;background:#f4f5f7;font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#1f2933">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center">
<table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px;background:#ffffff;border-radius:8px;border:1px solid #e1e4e8">
<tr><td style="padding:16px 24px;border-bottom:1px solid #e1e4e8;font-size:13px;font-weight:600;letter-spacing:.05em;text-transform:uppercase;color:#52606d">Tether</td></tr>
<tr><td style="padding:24px">
<h1 style="margin:0 0 12px;font-size:18px;line-height:1.3">${esc(subject)}</h1>
<p style="margin:0;font-size:15px;line-height:1.5;word-break:break-word">${body}</p>
</td></tr>
</table>
</td></tr></table>
</body></html>`;
}

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
      body: JSON.stringify({ api_key: apiKey, to: [to], sender: from, subject, text_body: text, html_body: renderHtml(subject, text) }),
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
