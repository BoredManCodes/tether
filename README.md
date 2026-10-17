# Tether

Self-hosted parental control for an Android phone you own and manage for your child, plus a small web dashboard.

Use it only on devices you are legally responsible for, and tell the person using the phone that it is monitored.

## What it does

- Location, battery, SMS, call log, notifications and per-app screen time, synced to your own server
- Dashboard: messages, calls, notifications (with app names), alerts, photos, live screen, schedule, places
- App rules: daily limits, block, or block + uninstall (the rule survives a reinstall)
- Bedtime schedule and remote lock to Phone and SMS only
- Keyword, geofence, offline and tamper alerts by email
- DNS-only local VPN that records which domains the phone looks up (domain and app, never page content)
- Incognito disabled in Chrome and Edge

Device Owner is required for suspend, uninstall, the browser policies and the always-on VPN. Without it the app falls back to data collection only.

## Layout

- `server/`: Node 20+ Fastify and SQLite backend with a static dashboard in `server/public/`
- `android/`: Kotlin app, no UI beyond setup and PIN screens
- `brand/`: logo concepts

## Server

```
cd server
cp .env.example .env     # set ADMIN_EMAIL, ADMIN_PASSWORD, SMTP2GO_API_KEY, ALERT_EMAIL_FROM
npm install
npm start                # listens on 127.0.0.1:3150
```

Put it behind a TLS reverse proxy. The seeded admin must change the password at first login.

## Android

1. Add to `android/local.properties`: `tether.secretCode=<digits>` (dial `*#*#<digits>#*#*` to open the app) and your `sdk.dir`.
2. `cd android && ./gradlew assembleDebug`
3. Install the APK, dial the code, and enter your server URL and parent login.
4. For Device Owner, provision on a freshly reset phone:
   `adb shell dpm set-device-owner com.trentbuckley.tether/.admin.TetherDeviceAdminReceiver`
   The notification listener and accessibility service are enabled once over adb.

## Limits

- The VPN sees domains only. Apps with their own or encrypted DNS are not visible.
- Android allows one VPN at a time; Tether's replaces any other VPN app.
- Anyone with the parent PIN or physical access to a non-Device-Owner phone can remove the app.
