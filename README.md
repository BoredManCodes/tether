# Tether

Self-hosted parental control for an Android phone and a Windows laptop you own and manage for your child, plus a small web dashboard.

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
- `windows/`: .NET 8 service, tray helper and installer for a Windows laptop
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

## Windows

The laptop needs two accounts: an administrator account you keep, and a standard (non-admin) account for your child. Tether cannot hold back an administrator, and the installer refuses a child account that is one.

What it does on the child account: matches on-screen and typed text against the blocked-phrase list (screenshot and email alert on a hit), live screen view, domain blocking and a DNS log through a local DNS proxy, app list, per-app daily limits and blocks, bedtime lock, and tamper and offline alerts. It does not classify images.

1. On a machine with the .NET 8 SDK, run `powershell -ExecutionPolicy Bypass -File windows/build.ps1`. It writes `windows/dist/tether-setup.exe` and `tether-signing.cer`.
2. Copy `tether-setup.exe` to `server/data/` to serve it at `/download/tether-setup.exe`, or carry it over on a USB stick.
3. Optional: on the laptop, import `tether-signing.cer` as an administrator so the signature is trusted:
   `Import-Certificate -FilePath tether-signing.cer -CertStoreLocation Cert:LocalMachineTrustedPublisher` (and again into `Cert:LocalMachineRoot`).
4. In an administrator terminal on the laptop: `tether-setup.exe install --server https://your.server --email you@example.com --child <child username>`. It prompts for your dashboard password; the password is never accepted on the command line.
5. Sign in as the child once. A tray icon appears with the tooltip "Tether: this laptop is monitored by your parent". Tell your child the laptop is monitored.
6. `C:Program FilesTetherTether.Setup.exe uninstall` removes everything and puts the adapters back on DHCP DNS. Delete the device in the dashboard afterwards, or the offline alert keeps firing.

The service holds the server token in `%ProgramData%Tetherconfig.json`, readable only by SYSTEM and Administrators. The helper runs only for the configured child account and exits for anyone else.

### Windows limits

- Lock screen, UAC prompts and other secure-desktop windows are not captured, and DRM-protected video shows black.
- Domain blocking covers the system resolver. Chrome, Edge and Firefox get DNS over HTTPS switched off by policy, and outbound DNS over TLS (port 853) is blocked. An app with its own hard-coded DoH server bypasses the block.
- Hits and screenshots are sent once; if the server is unreachable at that moment they are lost.
- A local administrator can stop or remove Tether. A standard user who ends the helper in Task Manager triggers a tamper alert, but the helper is not restarted until the next sign-in.
- Windows Defender or another antivirus may flag a keyboard hook and screen capture program. The installer adds a Defender exclusion for its folder.
- The installer has been checked by unit tests and a build, not by a full install on a live machine. Try it on a spare account first.

## Limits

- The VPN sees domains only. Apps with their own or encrypted DNS are not visible.
- Android allows one VPN at a time; Tether's replaces any other VPN app.
- Anyone with the parent PIN or physical access to a non-Device-Owner phone can remove the app.

## Licence

MIT. Third-party assets in `server/public/vendor` keep their own licences.
